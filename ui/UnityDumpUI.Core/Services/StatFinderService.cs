// StatFinderService.cs — the "find my HP / Gold" flow (UE5 DetectStats port).
//
// #5 ScoreAsync: one bulk `dumpClassInfo` over the user assemblies -> keyword-
// score every field name (InterestingFieldScorer) -> ranked list.
// #6 DetectAsync: score, then LIVE-CONFIRM the top numeric candidates — resolve
// one live instance per class (findInstances aliveOnly), walk it, and check the
// field currently holds a plausible numeric value. Confidence combines the
// keyword score + a live-instance bonus + a value-plausible bonus; a finding is
// "confirmed" only when a live instance exists AND its value is plausible AND
// the total clears the confirm threshold.
//
// Low-accuracy shortlist by design (name heuristics + a single-instance probe),
// single-player oriented — it narrows thousands of fields to a handful to try in
// CE. The UE-only signals (GAS attributes, the snapshot-decreased boost, the CDO
// "Default__" filter) are dropped; Unity has no CDO and any alive instance is real.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services;

/// <summary>A scored field plus its live-confirmation verdict.</summary>
public sealed class StatFinding
{
    public required InterestingField Field { get; init; }
    /// <summary>A live (alive) instance of the owning class was found.</summary>
    public bool    LiveInstanceExists { get; set; }
    /// <summary>The field currently holds a finite, sane-magnitude number.</summary>
    public bool    ValuePlausible     { get; set; }
    /// <summary>Display of the current live value (null if not read).</summary>
    public string? LiveValue          { get; set; }
    /// <summary>Keyword score + live/plausible bonuses.</summary>
    public int     Confidence         { get; set; }
    /// <summary>Live instance + plausible value + confidence over threshold.</summary>
    public bool    Confirmed          { get; set; }
}

/// <summary>
/// Orchestrates the Interesting-Fields scorer + a live-confirm probe over the
/// backend. Constructed with a <see cref="BackendAdapter"/>; no UI dependency.
/// </summary>
public sealed class StatFinderService
{
    /// <summary>Numeric candidates kept for the live-confirm probe.</summary>
    public const int MaxCandidates = 80;
    /// <summary>Distinct classes resolved + walked during confirm.</summary>
    public const int MaxClassesProbed = 30;

    private const int LiveInstanceBonus = 2;
    private const int ValuePlausibleBonus = 3;
    private const int ConfirmThreshold = 9;
    private const double PlausibleMagnitudeCap = 1e9;

    private readonly BackendAdapter _adapter;
    private readonly FileLogger? _logger;

    public StatFinderService(BackendAdapter adapter, FileLogger? logger = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _logger = logger;
    }

    private void Log(string message) => _logger?.Write("StatFinder", message);

    /// <summary>#5 — keyword-score every field in the user assemblies (one bulk
    /// dumpClassInfo). Returns the ranked list (already above threshold).</summary>
    public async Task<IReadOnlyList<InterestingField>> ScoreAsync(
        IReadOnlyList<string> userAssemblies, int? limit = null, CancellationToken ct = default)
    {
        if (userAssemblies is null || userAssemblies.Count == 0)
            return Array.Empty<InterestingField>();
        DumpClassInfoResult dump = await _adapter
            .DumpClassInfoAsync(userAssemblies, limit, ct).ConfigureAwait(false);
        IReadOnlyList<InterestingField> scored = InterestingFieldScorer.Score(dump);
        Log($"ScoreAsync: {dump.Rows.Count} classes -> {scored.Count} interesting fields");
        return scored;
    }

    /// <summary>#6 — score, then live-confirm the top numeric candidates. Returns
    /// findings ranked confirmed-first, highest-confidence-first.</summary>
    public async Task<IReadOnlyList<StatFinding>> DetectAsync(
        IReadOnlyList<string> userAssemblies, int? limit = null, CancellationToken ct = default)
    {
        IReadOnlyList<InterestingField> scored = await ScoreAsync(userAssemblies, limit, ct).ConfigureAwait(false);
        var findings = scored.Select(f => new StatFinding { Field = f, Confidence = f.Score }).ToList();

        // Live-confirm the top numeric candidates, grouped by owning class so each
        // class is resolved + walked exactly once.
        List<StatFinding> numeric = findings.Where(x => x.Field.IsNumeric).Take(MaxCandidates).ToList();
        var byClass = numeric
            .GroupBy(x => (x.Field.Assembly, x.Field.Namespace, x.Field.Class))
            .OrderByDescending(g => g.Max(x => x.Field.Score))
            .Take(MaxClassesProbed);

        foreach (var group in byClass)
        {
            ct.ThrowIfCancellationRequested();
            (string asm, string ns, string cls) = group.Key;
            (bool liveExists, Dictionary<string, WalkedField>? walked) =
                await ProbeClassAsync(asm, ns, cls, ct).ConfigureAwait(false);
            if (!liveExists) continue;
            foreach (StatFinding finding in group)
            {
                finding.LiveInstanceExists = true;
                if (walked is not null && TryFindWalked(walked, finding.Field, out WalkedField wf))
                {
                    (bool plausible, string? display) = PlausibleNumeric(wf);
                    finding.ValuePlausible = plausible;
                    finding.LiveValue = display;
                }
            }
        }

        foreach (StatFinding x in findings)
        {
            x.Confidence = x.Field.Score
                + (x.LiveInstanceExists ? LiveInstanceBonus : 0)
                + (x.ValuePlausible ? ValuePlausibleBonus : 0);
            x.Confirmed = x.LiveInstanceExists && x.ValuePlausible && x.Confidence >= ConfirmThreshold;
        }

        return findings
            .OrderByDescending(x => x.Confirmed)
            .ThenByDescending(x => x.Confidence)
            .ThenBy(x => x.Field.ClassDisplay, StringComparer.Ordinal)
            .ThenBy(x => x.Field.Name, StringComparer.Ordinal)
            .ToList();
    }

    // Resolve one live instance of the class + walk it. Returns (liveExists, fields
    // keyed by name). liveExists=true with a null/empty map means an instance was
    // found but the walk yielded nothing (still counts as "live").
    private async Task<(bool Live, Dictionary<string, WalkedField>? Fields)> ProbeClassAsync(
        string assembly, string @namespace, string className, CancellationToken ct)
    {
        try
        {
            FindInstancesResult fi = await _adapter.FindInstancesAsync(
                assembly, @namespace, className, limit: 3, aliveOnly: true, sane: true, ct: ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(fi.Error) || fi.Matches.Count == 0)
                return (false, null);

            string addr = fi.Matches[0];
            WalkInstanceResult w = await _adapter
                .WalkInstanceAsync(addr, assembly, @namespace, className, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(w.Error) || w.Fields is null)
                return (true, null);   // instance exists, value unreadable

            var map = new Dictionary<string, WalkedField>(StringComparer.Ordinal);
            foreach (WalkedField f in w.Fields)
                map[f.Name] = f;       // last-wins; name is the confirm key
            return (true, map);
        }
        catch (Exception ex)
        {
            Log($"ProbeClass {className}: {ex.Message}");
            return (false, null);
        }
    }

    // Match a scored field to a walked field. Prefer exact (name, offset); fall
    // back to name-only (offsets can differ across the header vs inline decode).
    private static bool TryFindWalked(
        Dictionary<string, WalkedField> walked, InterestingField field, out WalkedField result)
    {
        if (walked.TryGetValue(field.Name, out WalkedField? wf) && wf is not null)
        {
            result = wf;
            return true;
        }
        result = default!;
        return false;
    }

    private static (bool Plausible, string? Display) PlausibleNumeric(WalkedField wf)
    {
        if (wf.Value is not JsonElement v || v.ValueKind != JsonValueKind.Number)
            return (false, wf.Value?.ToString());
        if (v.TryGetDouble(out double d) && double.IsFinite(d) && Math.Abs(d) < PlausibleMagnitudeCap)
            return (true, v.ToString());   // zero is a valid stat (0 HP)
        return (false, v.ToString());
    }
}
