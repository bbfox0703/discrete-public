// SpcQueryViewModelTests — Phase 29d-6.
//
// Exercises the SpcQueryViewModel above SpcQueryEngine — focuses on:
//   * Selection-row sync with the shared snapshot pool
//   * Preflight (< 2 snaps / relative-at-index-0 / etc.)
//   * Cross-session auto-mode (StaticsOnly forced when session_ids differ)
//   * Cross-build banner copy
//   * Post-run eviction (non-additive RAM contract)
//   * Clipboard hooks fire the registered delegate
//   * Sort order matches captured_at_utc ascending

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class SpcQueryViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public SpcQueryViewModelTests()
    {
        SQLitePCL.Batteries_V2.Init();
        _tempDir = Path.Combine(Path.GetTempPath(), "SpcVmTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static System.Threading.CancellationToken Ct =>
        TestContext.Current.CancellationToken;

    /// <summary>Build a SnapshotViewModel (the SPC VM depends on it)
    /// against a temp dir-scoped UserSettings. Same shape as the
    /// Phase 29c persistence tests.</summary>
    private (SnapshotViewModel snapVm, SpcQueryViewModel spc) BuildVms()
    {
        var settings = new UserSettings
        {
            SnapshotDbDirectory     = Path.Combine(_tempDir, "snapshots"),
            AutoSaveSnapshots       = false,
            AutoPruneThresholdBytes = 0,
        };
        var pipe    = new PipeClient(pipeName: "tests-never-connect");
        var backend = new BackendAdapter(pipe);
        var logger  = new FileLogger(Path.Combine(_tempDir, "logs-" + Guid.NewGuid().ToString("N")));
        var settingsStore = new UserSettingsStore(Path.Combine(_tempDir, "settings.json"), logger);
        var snap = new SnapshotViewModel(backend, logger, settingsStore, settings);
        var spc  = new SpcQueryViewModel(snap, logger);
        return (snap, spc);
    }

    private static SnapshotEnvelope MakeEnv(
        string buildId, string sessionId, string label, string capturedAtUtc,
        params (string fqn, string fieldName, string value, bool isStatic)[] rows)
    {
        var env = new SnapshotEnvelope
        {
            ServerId       = label,
            Label          = label,
            CapturedAt     = System.DateTime.UtcNow,
            CapturedAtUtc  = capturedAtUtc,
            GameBuildId    = buildId,
            GameSessionId  = sessionId,
            TotalClasses   = rows.Length,
            TotalInstances = rows.Length,
            Classes        = new List<SnapshotClassEntry>(),
            ClassesLoaded  = true,
        };
        var idx = new Dictionary<string, SnapshotFieldValue>();
        foreach (var (fqn, fieldName, value, isStatic) in rows)
        {
            string instance = isStatic ? string.Empty : $"0xCAFE{fqn.GetHashCode():X4}";
            string key = $"asm|{fqn}|{(isStatic ? "static" : instance)}|0|{fieldName}";
            idx[key] = new SnapshotFieldValue
            {
                Assembly        = "asm",
                ClassFqn        = fqn,
                ClassName       = fqn,
                Namespace       = string.Empty,
                InstanceAddress = instance,
                IsStatic        = isStatic,
                FieldName       = fieldName,
                FieldType       = "System.Int32",
                FieldOffset     = 0,
                ValueDisplay    = value,
            };
        }
        // Phase 29g-2: FieldIndex is lazy (no public setter) — inject via the
        // test seam so the engine sees the controlled fixture dict.
        env.SetFieldIndexForTest(idx);
        return env;
    }

    // ─── Sync + ordering ───────────────────────────────────────────────

    [Fact]
    public void SelectionRows_MirrorUnderlyingPoolInCaptureOrder()
    {
        var (snap, spc) = BuildVms();
        // Add out-of-order; SPC should sort ascending by CapturedAtUtc.
        snap.Snapshots.Add(MakeEnv("b1", "s1", "C", "2026-05-29T12:00:00Z"));
        snap.Snapshots.Add(MakeEnv("b1", "s1", "A", "2026-05-29T10:00:00Z"));
        snap.Snapshots.Add(MakeEnv("b1", "s1", "B", "2026-05-29T11:00:00Z"));

        Assert.Equal(3, spc.SelectionRows.Count);
        Assert.Equal(new[] { "A", "B", "C" },
            spc.SelectionRows.Select(r => r.Snapshot.Label));
        // Index advertised to the user matches the sort order.
        Assert.Equal(new[] { 0, 1, 2 }, spc.SelectionRows.Select(r => r.Index));
        // Default Include is ON for every row so a fresh user can press
        // Run immediately.
        Assert.All(spc.SelectionRows, r => Assert.True(r.Include));
    }

    [Fact]
    public void SelectionRows_PreserveUserStateOnPoolChange()
    {
        var (snap, spc) = BuildVms();
        var a = MakeEnv("b1", "s1", "A", "2026-05-29T10:00:00Z");
        snap.Snapshots.Add(a);
        // User flips include off + changes predicate.
        spc.SelectionRows[0].Include       = false;
        spc.SelectionRows[0].PredicateKind = SpcPredicateKind.Exact;
        spc.SelectionRows[0].OperandText   = "42";

        // Pool changes — another snap appears.
        snap.Snapshots.Add(MakeEnv("b1", "s1", "B", "2026-05-29T11:00:00Z"));

        // A's per-row state survived the sync.
        var aRow = spc.SelectionRows.Single(r => ReferenceEquals(r.Snapshot, a));
        Assert.False(aRow.Include);
        Assert.Equal(SpcPredicateKind.Exact, aRow.PredicateKind);
        Assert.Equal("42", aRow.OperandText);
    }

    // ─── Preflight ─────────────────────────────────────────────────────

    [Fact]
    public async Task Run_FewerThanTwoSelectedReportsError()
    {
        var (snap, spc) = BuildVms();
        snap.Snapshots.Add(MakeEnv("b1", "s1", "A", "2026-05-29T10:00:00Z"));
        // Only one snap → can't run.
        await spc.RunAsync();
        Assert.Contains("at least 2", spc.ErrorMessage);
    }

    [Fact]
    public async Task Run_ExactWithEmptyOperandReportsErrorInsteadOfZeroMatches()
    {
        // Regression: user picked Exact but left the Operand cell empty
        // → engine silently returned 0 matches, indistinguishable from
        // "no field met the criteria". Preflight now catches the missing
        // operand and surfaces a row-specific error.
        var (snap, spc) = BuildVms();
        snap.Snapshots.Add(MakeEnv("b1", "s1", "A", "2026-05-29T10:00:00Z",
            ("Foo", "x", "10160", true)));
        snap.Snapshots.Add(MakeEnv("b1", "s1", "B", "2026-05-29T11:00:00Z",
            ("Foo", "x", "9910", true)));
        spc.SelectionRows[0].PredicateKind = SpcPredicateKind.Any;
        spc.SelectionRows[1].PredicateKind = SpcPredicateKind.Exact;
        // OperandText intentionally left blank.

        await spc.RunAsync();
        Assert.Contains("needs a numeric Operand", spc.ErrorMessage);
        Assert.Empty(spc.Results);
    }

    [Fact]
    public async Task Run_RangeWithBothBoundsEmptyReportsError()
    {
        var (snap, spc) = BuildVms();
        snap.Snapshots.Add(MakeEnv("b1", "s1", "A", "2026-05-29T10:00:00Z",
            ("Foo", "x", "10", true)));
        snap.Snapshots.Add(MakeEnv("b1", "s1", "B", "2026-05-29T11:00:00Z",
            ("Foo", "x", "20", true)));
        spc.SelectionRows[0].PredicateKind = SpcPredicateKind.Range;
        spc.SelectionRows[1].PredicateKind = SpcPredicateKind.Any;
        // Both operand fields empty for a Range predicate.

        await spc.RunAsync();
        Assert.Contains("needs at least one of Operand or Operand 2", spc.ErrorMessage);
    }

    [Fact]
    public async Task Run_RangeWithOnlyMinSetSucceeds()
    {
        var (snap, spc) = BuildVms();
        snap.Snapshots.Add(MakeEnv("b1", "s1", "A", "2026-05-29T10:00:00Z",
            ("Foo", "above", "50", true),
            ("Foo", "below", "5",  true)));
        snap.Snapshots.Add(MakeEnv("b1", "s1", "B", "2026-05-29T11:00:00Z",
            ("Foo", "above", "60", true),
            ("Foo", "below", "6",  true)));
        spc.SelectionRows[0].PredicateKind = SpcPredicateKind.Range;
        spc.SelectionRows[0].OperandText   = "10";  // min only — open upper bound
        spc.SelectionRows[1].PredicateKind = SpcPredicateKind.Any;

        await spc.RunAsync();
        Assert.Empty(spc.ErrorMessage);
        // Only "above" passes Range(min=10, max=null) in snap A.
        Assert.Single(spc.Results);
        Assert.Equal("above", spc.Results[0].FieldName);
    }

    [Fact]
    public async Task Run_FirstPredicateRelativeBlocksWithExplanation()
    {
        var (snap, spc) = BuildVms();
        snap.Snapshots.Add(MakeEnv("b1", "s1", "A", "2026-05-29T10:00:00Z",
            ("Foo", "x", "1", true)));
        snap.Snapshots.Add(MakeEnv("b1", "s1", "B", "2026-05-29T11:00:00Z",
            ("Foo", "x", "2", true)));
        spc.SelectionRows[0].PredicateKind = SpcPredicateKind.Increased;
        await spc.RunAsync();
        Assert.Contains("cannot be a relative kind", spc.ErrorMessage);
    }

    // ─── Cross-session / cross-build auto-mode ─────────────────────────

    [Fact]
    public async Task Run_CrossSessionAutoEnablesStaticsOnly()
    {
        var (snap, spc) = BuildVms();
        snap.Snapshots.Add(MakeEnv("b1", "sX", "A", "2026-05-29T10:00:00Z",
            ("Foo", "kStatic", "10", true)));
        snap.Snapshots.Add(MakeEnv("b1", "sY", "B", "2026-05-29T11:00:00Z",  // different session
            ("Foo", "kStatic", "20", true)));

        Assert.False(spc.StaticsOnly);  // default off
        await spc.RunAsync();
        Assert.True(spc.StaticsOnly);   // auto-enabled
        Assert.Contains("Cross-session", spc.ModeBanner);
    }

    [Fact]
    public async Task Run_CrossBuildShowsYellowWarningBanner()
    {
        var (snap, spc) = BuildVms();
        snap.Snapshots.Add(MakeEnv("bA", "s1", "A", "2026-05-29T10:00:00Z",
            ("Foo", "kStatic", "1", true)));
        snap.Snapshots.Add(MakeEnv("bB", "s1", "B", "2026-05-29T11:00:00Z",
            ("Foo", "kStatic", "1", true)));

        await spc.RunAsync();
        Assert.Contains("Cross-build", spc.ModeBanner);
        Assert.Contains("⚠", spc.ModeBanner);
    }

    // ─── End-to-end killer use case ────────────────────────────────────

    [Fact]
    public async Task Run_KillerUseCase_LocatesGameStatByValueSignature()
    {
        var (snap, spc) = BuildVms();
        snap.Snapshots.Add(MakeEnv("b1", "s1", "A", "2026-05-29T10:00:00Z",
            ("Mortal.Core.GameStat", "_value", "10160", true),
            ("Noise", "x", "999", true)));
        snap.Snapshots.Add(MakeEnv("b1", "s1", "B", "2026-05-29T11:00:00Z",
            ("Mortal.Core.GameStat", "_value", "9910", true),
            ("Noise", "x", "1234", true)));
        snap.Snapshots.Add(MakeEnv("b1", "s1", "C", "2026-05-29T12:00:00Z",
            ("Mortal.Core.GameStat", "_value", "9410", true),
            ("Noise", "x", "5555", true)));
        snap.Snapshots.Add(MakeEnv("b1", "s1", "D", "2026-05-29T13:00:00Z",
            ("Mortal.Core.GameStat", "_value", "9410", true),
            ("Noise", "x", "9999", true)));

        spc.SelectionRows[0].PredicateKind = SpcPredicateKind.Exact;
        spc.SelectionRows[0].OperandText   = "10160";
        spc.SelectionRows[1].PredicateKind = SpcPredicateKind.DeltaExact;
        spc.SelectionRows[1].OperandText   = "-250";
        spc.SelectionRows[2].PredicateKind = SpcPredicateKind.DeltaExact;
        spc.SelectionRows[2].OperandText   = "-500";
        spc.SelectionRows[3].PredicateKind = SpcPredicateKind.Unchanged;

        await spc.RunAsync();

        Assert.Empty(spc.ErrorMessage);
        Assert.Single(spc.Results);
        Assert.Equal("Mortal.Core.GameStat", spc.Results[0].ClassFqn);
        Assert.Equal("_value",               spc.Results[0].FieldName);
        Assert.Contains("1 match", spc.StatusMessage);
    }

    // ─── Eviction contract (29d-4) ─────────────────────────────────────

    [Fact]
    public async Task Run_EvictsOnlyEnvelopesItHydratedItself()
    {
        var (snap, spc) = BuildVms();
        // Simulate the realistic flow: open per-game DB so we can
        // exercise the Persisted / Unload path properly.
        await snap.OnGameDbContextAsync("b1", "mono.dll", Ct);
        // Add 3 dirty envelopes + save them so they're persistable.
        var a = MakeEnv("b1", "s1", "A", "2026-05-29T10:00:00Z",
            ("Foo", "x", "1", true));
        var b = MakeEnv("b1", "s1", "B", "2026-05-29T11:00:00Z",
            ("Foo", "x", "2", true));
        var c = MakeEnv("b1", "s1", "C", "2026-05-29T12:00:00Z",
            ("Foo", "x", "3", true));
        // BuildFieldIndex was bypassed in our fixture so the persisted
        // payload would be empty. Use the regular flow: produce one
        // class entry per row so Save round-trips real bytes.
        AttachOneClassEntry(a, "Foo", "x", "1", true);
        AttachOneClassEntry(b, "Foo", "x", "2", true);
        AttachOneClassEntry(c, "Foo", "x", "3", true);
        snap.Snapshots.Add(a);
        snap.Snapshots.Add(b);
        snap.Snapshots.Add(c);
        await snap.SaveSnapshotAsync(a);
        await snap.SaveSnapshotAsync(b);
        await snap.SaveSnapshotAsync(c);

        // Pretend only A was "already open" before SPC fires — unload
        // B and C so SPC's hydrate is non-trivial.
        b.Unload();
        c.Unload();
        Assert.True(a.ClassesLoaded);
        Assert.False(b.ClassesLoaded);
        Assert.False(c.ClassesLoaded);

        // Build a 3-snap query.
        await spc.RunAsync();

        // Contract: A (already loaded before Run) stays loaded; B and C
        // (hydrated for the query) get unloaded again on completion.
        Assert.True(a.ClassesLoaded);
        Assert.False(b.ClassesLoaded);
        Assert.False(c.ClassesLoaded);
    }

    /// <summary>Hand-build a single SnapshotClassEntry on an envelope
    /// so SaveSnapshotAsync persists meaningful bytes (the test
    /// fixture's FieldIndex is hand-crafted and bypasses Classes).</summary>
    private static void AttachOneClassEntry(SnapshotEnvelope env, string fqn, string fieldName, string value, bool isStatic)
    {
        var field = new SnapshotFieldEntry
        {
            Name   = fieldName,
            Type   = "System.Int32",
            Offset = 0,
            Value  = System.Text.Json.JsonDocument.Parse($"\"{value}\"").RootElement,
        };
        var cls = new SnapshotClassEntry
        {
            Assembly  = "asm",
            Namespace = string.Empty,
            Name      = fqn,
            Statics   = isStatic ? new List<SnapshotFieldEntry> { field } : null,
            Instances = isStatic ? null : new List<SnapshotInstanceEntry>
            {
                new() { Address = "0x1000", Fields = new() { field } },
            },
        };
        env.Classes.Add(cls);
    }

    // ─── Clipboard hooks (29d-5) ───────────────────────────────────────

    [Fact]
    public async Task CopyClassFieldAsync_FiresRegisteredClipboardDelegate()
    {
        var (_, spc) = BuildVms();
        string? captured = null;
        spc.CopyToClipboardAsync = text => { captured = text; return Task.CompletedTask; };

        var match = new SpcMatch
        {
            Key             = "k",
            Assembly        = "asm",
            ClassFqn        = "Mortal.Core.GameStat",
            FieldName       = "_value",
            FieldType       = "System.Int32",
            InstanceAddress = "0x1000",
            IsStatic        = false,
            FieldOffset     = 0x10,
            ValueDisplays   = new[] { "10160", "9910" },
            NumericValues   = new double?[] { 10160, 9910 },
        };
        await spc.CopyClassFieldAsync(match);
        Assert.Equal("Mortal.Core.GameStat._value", captured);
        Assert.Contains("Copied:", spc.StatusMessage);
    }

    [Fact]
    public async Task CopyValueSequenceAsync_FormatsArrowJoinedString()
    {
        var (_, spc) = BuildVms();
        string? captured = null;
        spc.CopyToClipboardAsync = text => { captured = text; return Task.CompletedTask; };

        var match = new SpcMatch
        {
            Key             = "k",
            Assembly        = "asm",
            ClassFqn        = "Foo",
            FieldName       = "x",
            FieldType       = "System.Int32",
            InstanceAddress = string.Empty,
            IsStatic        = true,
            FieldOffset     = 0,
            ValueDisplays   = new[] { "100", "200", "300" },
            NumericValues   = new double?[] { 100, 200, 300 },
        };
        await spc.CopyValueSequenceAsync(match);
        Assert.Equal("100 → 200 → 300", captured);
    }

    [Fact]
    public async Task CopyInstanceAddressAsync_NoOpForStaticRows()
    {
        var (_, spc) = BuildVms();
        bool fired = false;
        spc.CopyToClipboardAsync = _ => { fired = true; return Task.CompletedTask; };

        var match = new SpcMatch
        {
            Key             = "k",
            Assembly        = "asm",
            ClassFqn        = "Foo",
            FieldName       = "x",
            FieldType       = "System.Int32",
            InstanceAddress = string.Empty,  // static
            IsStatic        = true,
            FieldOffset     = 0,
            ValueDisplays   = new[] { "1" },
            NumericValues   = new double?[] { 1.0 },
        };
        await spc.CopyInstanceAddressAsync(match);
        Assert.False(fired);
    }
}
