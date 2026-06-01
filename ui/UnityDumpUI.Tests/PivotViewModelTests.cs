// PivotViewModelTests — Phase 29e-2.
//
// Covers the ViewModel layer above PivotEngine:
//   * SelectionRows mirror the shared snapshot pool in capture order
//   * Include-state survives pool resync
//   * Preflight rejects empty class FQN / key fields / value fields
//   * Class FQN not in any snap → friendly error, not exception
//   * Cross-session banner reads as "feature" (pivot is built for it),
//     cross-build banner stays a warning
//   * End-to-end killer use case — money tracking across drifting
//     instance addresses across multiple sessions
//   * Compound key disambiguates collisions; HasCollision row marker
//   * Clipboard hooks fire the registered delegate
//   * KnownClasses + KnownFieldNames refresh after Run from hydrated
//     snapshots
//   * Multi-value-field trajectory display renders one line per field

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class PivotViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public PivotViewModelTests()
    {
        SQLitePCL.Batteries_V2.Init();
        _tempDir = Path.Combine(Path.GetTempPath(), "PivotVmTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ─── Fixture helpers ───────────────────────────────────────────────

    private (SnapshotViewModel snap, PivotViewModel pivot) BuildVms()
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
        var snap  = new SnapshotViewModel(backend, logger, settingsStore, settings);
        var pivot = new PivotViewModel(snap, logger);
        return (snap, pivot);
    }

    private static SnapshotEnvelope MakeEnv(
        string label, string buildId, string sessionId, string capturedAtUtc,
        params SnapshotClassEntry[] classes)
    {
        return new SnapshotEnvelope
        {
            ServerId       = label,
            Label          = label,
            CapturedAt     = DateTime.UtcNow,
            CapturedAtUtc  = capturedAtUtc,
            GameBuildId    = buildId,
            GameSessionId  = sessionId,
            TotalClasses   = classes.Length,
            TotalInstances = classes.Sum(c => c.Instances?.Count ?? 0),
            Classes        = classes.ToList(),
            ClassesLoaded  = true,
        };
    }

    private static SnapshotClassEntry MakeClass(
        string fqn, string assembly,
        params SnapshotInstanceEntry[] instances)
    {
        int dot = fqn.LastIndexOf('.');
        string ns   = dot >= 0 ? fqn.Substring(0, dot) : string.Empty;
        string name = dot >= 0 ? fqn.Substring(dot + 1) : fqn;
        return new SnapshotClassEntry
        {
            Assembly  = assembly,
            Namespace = ns,
            Name      = name,
            Instances = instances.ToList(),
        };
    }

    private static SnapshotInstanceEntry MakeInstance(
        string address, params (string name, string type, object? value)[] fields)
    {
        var inst = new SnapshotInstanceEntry { Address = address };
        foreach (var (name, type, value) in fields)
        {
            var fe = new SnapshotFieldEntry { Name = name, Type = type };
            switch (value)
            {
                case int i:
                    fe.Value = JsonDocument.Parse(i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                          .RootElement.Clone();
                    break;
                case string s:
                    fe.StringValue = s;
                    break;
                case null:
                    break;
                default:
                    throw new ArgumentException($"Unhandled value type {value.GetType()}");
            }
            inst.Fields.Add(fe);
        }
        return inst;
    }

    // ─── Layer B (29f-4) array-element fixtures ────────────────────────

    private static SnapshotFieldEntry MakeFieldEntry(string name, string type, object? value)
    {
        var fe = new SnapshotFieldEntry { Name = name, Type = type };
        switch (value)
        {
            case int i:
                fe.Value = JsonDocument.Parse(i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                      .RootElement.Clone();
                break;
            case string s: fe.StringValue = s; break;
            case null: break;
            default: throw new ArgumentException($"Unhandled value type {value.GetType()}");
        }
        return fe;
    }

    private static SnapshotArrayElement Elem(string addr,
        params (string name, string type, object? value)[] fields)
        => new()
        {
            Address = addr, Ptr = addr,
            Fields = fields.Select(f => MakeFieldEntry(f.name, f.type, f.value)).ToList(),
        };

    private static SnapshotArrayElement ElemNoFields(string addr)
        => new() { Address = addr, Ptr = addr };   // pre-Layer-B / primitive slot

    /// <summary>Instance with optional owner fields + a reference-array
    /// field whose elements carry inner Fields (Layer B).</summary>
    private static SnapshotInstanceEntry MakeInstanceWithArray(
        string address, string arrayFieldName, string elementType,
        SnapshotArrayElement[] elements,
        params (string name, string type, object? value)[] ownerFields)
    {
        var inst = MakeInstance(address, ownerFields);
        inst.Fields.Add(new SnapshotFieldEntry
        {
            Name = arrayFieldName, Type = elementType + "[]",
            ArrayElements = new SnapshotArrayElements
            {
                Length = (ulong)elements.Length, Count = elements.Length,
                ElementType = elementType, ElementSize = 8, IsPointer = true,
                Elements = elements.ToList(),
            },
        });
        return inst;
    }

    // ─── Selection sync + ordering ─────────────────────────────────────

    [Fact]
    public void SelectionRows_MirrorPoolInCaptureOrder()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("C", "b1", "s1", "2026-05-29T12:00:00Z"));
        snap.Snapshots.Add(MakeEnv("A", "b1", "s1", "2026-05-29T10:00:00Z"));
        snap.Snapshots.Add(MakeEnv("B", "b1", "s1", "2026-05-29T11:00:00Z"));

        Assert.Equal(new[] { "A", "B", "C" },
            pivot.SelectionRows.Select(r => r.Snapshot.Label));
        Assert.All(pivot.SelectionRows, r => Assert.True(r.Include));
    }

    [Fact]
    public void SelectionRows_PreserveIncludeFlagOnPoolChange()
    {
        var (snap, pivot) = BuildVms();
        var a = MakeEnv("A", "b1", "s1", "2026-05-29T10:00:00Z");
        snap.Snapshots.Add(a);
        pivot.SelectionRows[0].Include = false;

        snap.Snapshots.Add(MakeEnv("B", "b1", "s1", "2026-05-29T11:00:00Z"));

        var aRow = pivot.SelectionRows.Single(r => ReferenceEquals(r.Snapshot, a));
        Assert.False(aRow.Include);
    }

    // ─── Preflight ─────────────────────────────────────────────────────

    [Fact]
    public async Task Run_NoSelectedSnapshots_ReportsError()
    {
        var (_, pivot) = BuildVms();
        pivot.ClassFqn        = "Foo";
        pivot.KeyFieldsText   = "id";
        pivot.ValueFieldsText = "v";
        await pivot.RunAsync();
        Assert.Contains("at least 1 snapshot", pivot.ErrorMessage);
    }

    [Fact]
    public async Task Run_MissingClassFqn_ReportsError()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("A", "b1", "s1", "2026-05-29T10:00:00Z"));
        pivot.KeyFieldsText   = "id";
        pivot.ValueFieldsText = "v";
        await pivot.RunAsync();
        Assert.Contains("Class FQN is required", pivot.ErrorMessage);
    }

    [Fact]
    public async Task Run_EmptyKeyOrValueFields_ReportsError()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("A", "b1", "s1", "2026-05-29T10:00:00Z"));
        pivot.ClassFqn        = "Foo";
        pivot.ValueFieldsText = "v";   // key empty
        await pivot.RunAsync();
        Assert.Contains("key field", pivot.ErrorMessage);

        pivot.ErrorMessage     = string.Empty;
        pivot.KeyFieldsText    = "id";
        pivot.ValueFieldsText  = string.Empty;
        await pivot.RunAsync();
        Assert.Contains("value field", pivot.ErrorMessage);
    }

    [Fact]
    public async Task Run_ClassNotInAnySnapshot_FriendlyError()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("A", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.RealClass", "asm",
                MakeInstance("0x1", ("id", "System.Int32", 1)))));
        pivot.ClassFqn        = "Game.MissingClass";
        pivot.KeyFieldsText   = "id";
        pivot.ValueFieldsText = "id";

        await pivot.RunAsync();
        Assert.Contains("not found in any selected snapshot", pivot.ErrorMessage);
        Assert.Empty(pivot.Results);
    }

    // ─── Banner copy ───────────────────────────────────────────────────

    [Fact]
    public async Task Run_CrossSession_BannerReadsAsFeature()
    {
        var (snap, pivot) = BuildVms();
        var c1 = MakeClass("Game.Stat", "asm",
            MakeInstance("0x1", ("id", "System.Int32", 1), ("v", "System.Int32", 10)));
        var c2 = MakeClass("Game.Stat", "asm",
            MakeInstance("0xff", ("id", "System.Int32", 1), ("v", "System.Int32", 20)));
        snap.Snapshots.Add(MakeEnv("A", "b1", "sX", "2026-05-29T10:00:00Z", c1));
        snap.Snapshots.Add(MakeEnv("B", "b1", "sY", "2026-05-29T11:00:00Z", c2));
        pivot.ClassFqn        = "Game.Stat";
        pivot.KeyFieldsText   = "id";
        pivot.ValueFieldsText = "v";

        await pivot.RunAsync();
        Assert.Empty(pivot.ErrorMessage);
        Assert.Contains("Cross-session pivot", pivot.ModeBanner);
        Assert.Contains("sidesteps address drift", pivot.ModeBanner);
        Assert.Single(pivot.Results);
    }

    [Fact]
    public async Task Run_CrossBuild_BannerStaysAWarning()
    {
        var (snap, pivot) = BuildVms();
        var c1 = MakeClass("Game.Stat", "asm",
            MakeInstance("0x1", ("id", "System.Int32", 1), ("v", "System.Int32", 10)));
        var c2 = MakeClass("Game.Stat", "asm",
            MakeInstance("0xff", ("id", "System.Int32", 1), ("v", "System.Int32", 20)));
        snap.Snapshots.Add(MakeEnv("A", "bA", "s1", "2026-05-29T10:00:00Z", c1));
        snap.Snapshots.Add(MakeEnv("B", "bB", "s1", "2026-05-29T11:00:00Z", c2));
        pivot.ClassFqn        = "Game.Stat";
        pivot.KeyFieldsText   = "id";
        pivot.ValueFieldsText = "v";

        await pivot.RunAsync();
        Assert.Contains("Cross-build pivot", pivot.ModeBanner);
        Assert.Contains("⚠", pivot.ModeBanner);
    }

    // ─── End-to-end killer use case ────────────────────────────────────

    [Fact]
    public async Task Run_KillerUseCase_TracksMoneyAcrossDriftingAddresses()
    {
        // Mortal.Core.GameStat shows up at different addresses across
        // sessions but always carries _propertyID == 101 for Money.
        // Pivot keyed by _propertyID must produce one row with the
        // full _value trajectory.
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "S1", "2026-05-29T10:00:00Z",
            MakeClass("Mortal.Core.GameStat", "Assembly-CSharp",
                MakeInstance("0x1f3a000", ("_propertyID", "System.Int32", 101), ("_value", "System.Int32", 10160)),
                MakeInstance("0x1f3a020", ("_propertyID", "System.Int32", 201), ("_value", "System.Int32", 1200)))));
        snap.Snapshots.Add(MakeEnv("snap2", "b1", "S2", "2026-05-29T11:00:00Z",
            MakeClass("Mortal.Core.GameStat", "Assembly-CSharp",
                MakeInstance("0x1f60a00", ("_propertyID", "System.Int32", 201), ("_value", "System.Int32", 1180)),
                MakeInstance("0x1f60a20", ("_propertyID", "System.Int32", 101), ("_value", "System.Int32",  9910)))));
        snap.Snapshots.Add(MakeEnv("snap3", "b1", "S3", "2026-05-29T12:00:00Z",
            MakeClass("Mortal.Core.GameStat", "Assembly-CSharp",
                MakeInstance("0x1f80a40", ("_propertyID", "System.Int32", 101), ("_value", "System.Int32",  9410)))));

        pivot.ClassFqn        = "Mortal.Core.GameStat";
        pivot.KeyFieldsText   = "_propertyID";
        pivot.ValueFieldsText = "_value";
        pivot.KeyFilter       = "101";   // narrow to the money row

        await pivot.RunAsync();

        Assert.Empty(pivot.ErrorMessage);
        var money = Assert.Single(pivot.Results);
        Assert.Equal("101", money.KeyTupleDisplay);
        Assert.False(money.HasCollision);
        Assert.Contains("10160", money.TrajectoryDisplay);
        Assert.Contains("9910",  money.TrajectoryDisplay);
        Assert.Contains("9410",  money.TrajectoryDisplay);
        Assert.Contains("→",     money.TrajectoryDisplay);
        // AddressPerSnap traces the drifting addresses across sessions.
        Assert.Equal("0x1f3a000", money.AddressPerSnap[0]);
        Assert.Equal("0x1f60a20", money.AddressPerSnap[1]);
        Assert.Equal("0x1f80a40", money.AddressPerSnap[2]);
        Assert.Contains("Cross-session pivot", pivot.ModeBanner);
        Assert.Contains("1 row", pivot.StatusMessage);
    }

    // ─── Compound key + collision ──────────────────────────────────────

    [Fact]
    public async Task Run_CompoundKey_DisambiguatesCollisions()
    {
        // Single _propertyID=101 would collide between Currency and
        // Weapon groups. Compound (_itemGroup, _propertyID) splits
        // them into 2 distinct rows.
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.Item", "asm",
                MakeInstance("0xa", ("_itemGroup", "string", "Currency"), ("_propertyID", "System.Int32", 101), ("_count", "System.Int32", 100)),
                MakeInstance("0xb", ("_itemGroup", "string", "Weapon"),   ("_propertyID", "System.Int32", 101), ("_count", "System.Int32",   3)))));

        pivot.ClassFqn        = "Game.Item";
        pivot.KeyFieldsText   = "_itemGroup, _propertyID";
        pivot.ValueFieldsText = "_count";

        await pivot.RunAsync();
        Assert.Equal(2, pivot.Results.Count);
        Assert.Contains(pivot.Results, r => r.KeyTupleDisplay.Contains("Currency") && r.TrajectoryDisplay.Contains("100"));
        Assert.Contains(pivot.Results, r => r.KeyTupleDisplay.Contains("Weapon")   && r.TrajectoryDisplay.Contains("3"));
    }

    [Fact]
    public async Task Run_SingleKeyWithCollision_FlagsRow()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.Item", "asm",
                MakeInstance("0x1", ("_propertyID", "System.Int32", 42), ("_count", "System.Int32", 5)),
                MakeInstance("0x2", ("_propertyID", "System.Int32", 42), ("_count", "System.Int32", 7)))));

        pivot.ClassFqn        = "Game.Item";
        pivot.KeyFieldsText   = "_propertyID";
        pivot.ValueFieldsText = "_count";

        await pivot.RunAsync();
        var row = Assert.Single(pivot.Results);
        Assert.True(row.HasCollision);
        Assert.Equal("⚠", row.CollisionMarker);
        Assert.Contains("collision", pivot.StatusMessage);
    }

    // ─── Multi-value-field trajectory ──────────────────────────────────

    [Fact]
    public async Task Run_MultipleValueFields_TrajectoryHasOneLinePerField()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.Player", "asm",
                MakeInstance("0x1", ("id", "System.Int32", 1), ("hp", "System.Int32", 100), ("mp", "System.Int32", 30)))));
        snap.Snapshots.Add(MakeEnv("snap2", "b1", "s1", "2026-05-29T11:00:00Z",
            MakeClass("Game.Player", "asm",
                MakeInstance("0x1", ("id", "System.Int32", 1), ("hp", "System.Int32",  80), ("mp", "System.Int32", 50)))));

        pivot.ClassFqn        = "Game.Player";
        pivot.KeyFieldsText   = "id";
        pivot.ValueFieldsText = "hp, mp";

        await pivot.RunAsync();
        var row = Assert.Single(pivot.Results);
        Assert.Contains("hp:", row.TrajectoryDisplay);
        Assert.Contains("mp:", row.TrajectoryDisplay);
        Assert.Contains("100", row.TrajectoryDisplay);
        Assert.Contains("80",  row.TrajectoryDisplay);
        Assert.Contains("30",  row.TrajectoryDisplay);
        Assert.Contains("50",  row.TrajectoryDisplay);
    }

    // ─── Autocomplete refresh ──────────────────────────────────────────

    [Fact]
    public async Task Run_RefreshesKnownClassesAndFieldNames()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.Player", "asm",
                MakeInstance("0x1", ("id", "System.Int32", 1), ("hp", "System.Int32", 100)))));
        pivot.ClassFqn        = "Game.Player";
        pivot.KeyFieldsText   = "id";
        pivot.ValueFieldsText = "hp";

        Assert.Empty(pivot.KnownClasses);   // pre-run

        await pivot.RunAsync();
        Assert.Contains("Game.Player", pivot.KnownClasses);
        Assert.Contains("id", pivot.KnownFieldNames);
        Assert.Contains("hp", pivot.KnownFieldNames);
    }

    // ─── Clipboard hooks ───────────────────────────────────────────────

    [Fact]
    public async Task CopyKeyTuple_FiresClipboardDelegate()
    {
        var (_, pivot) = BuildVms();
        string captured = string.Empty;
        pivot.CopyToClipboardAsync = s => { captured = s; return Task.CompletedTask; };

        var row = new PivotResultRow
        {
            KeyTupleDisplay        = "Currency, 101",
            KeyTuple               = new[] { "Currency", "101" },
            TrajectoryDisplay      = "10160 → 9910",
            AddressPerSnap         = new string?[] { "0x1", "0xff" },
            HasCollision           = false,
            PerValueFieldSequences = new List<PivotValueFieldSequence>
            {
                new() { FieldName = "_value", Values = new string?[] { "10160", "9910" } },
            },
        };

        await pivot.CopyKeyTupleAsync(row);
        Assert.Equal("Currency, 101", captured);
    }

    [Fact]
    public async Task CopyValueSequence_FormatsPerFieldLine()
    {
        var (_, pivot) = BuildVms();
        string captured = string.Empty;
        pivot.CopyToClipboardAsync = s => { captured = s; return Task.CompletedTask; };

        var row = new PivotResultRow
        {
            KeyTupleDisplay        = "1",
            KeyTuple               = new[] { "1" },
            TrajectoryDisplay      = "hp: 100 → 80\nmp: 30 → 50",
            AddressPerSnap         = new string?[] { "0x1", "0x1" },
            HasCollision           = false,
            PerValueFieldSequences = new List<PivotValueFieldSequence>
            {
                new() { FieldName = "hp", Values = new string?[] { "100", "80" } },
                new() { FieldName = "mp", Values = new string?[] {  "30", "50" } },
            },
        };

        await pivot.CopyValueSequenceAsync(row);
        Assert.Contains("hp:", captured);
        Assert.Contains("mp:", captured);
        Assert.Contains("100 → 80", captured);
        Assert.Contains("30 → 50",  captured);
    }

    [Fact]
    public async Task CopyAddressLatest_PicksLastNonNullAddress()
    {
        var (_, pivot) = BuildVms();
        string captured = string.Empty;
        pivot.CopyToClipboardAsync = s => { captured = s; return Task.CompletedTask; };

        var row = new PivotResultRow
        {
            KeyTupleDisplay        = "1",
            KeyTuple               = new[] { "1" },
            TrajectoryDisplay      = "x",
            AddressPerSnap         = new string?[] { "0xAAA", null, "0xCCC", null },
            HasCollision           = false,
            PerValueFieldSequences = new List<PivotValueFieldSequence>(),
        };

        await pivot.CopyAddressLatestAsync(row);
        Assert.Equal("0xCCC", captured);
    }

    // ─── NavigateToInspector handoff ───────────────────────────────────

    [Fact]
    public void RequestInspectInstance_RaisesEventWithLatestAddress()
    {
        var (_, pivot) = BuildVms();
        string? raised = null;
        pivot.NavigateToInspectorRequested += (_, addr) => raised = addr;

        var row = new PivotResultRow
        {
            KeyTupleDisplay        = "1",
            KeyTuple               = new[] { "1" },
            TrajectoryDisplay      = "x",
            AddressPerSnap         = new string?[] { "0x1", null, "0x3" },
            HasCollision           = false,
            PerValueFieldSequences = new List<PivotValueFieldSequence>(),
        };

        pivot.RequestInspectInstance(row);
        Assert.Equal("0x3", raised);
    }

    [Fact]
    public void RequestInspectInstance_NoAddressAvailable_NoOp()
    {
        var (_, pivot) = BuildVms();
        bool raised = false;
        pivot.NavigateToInspectorRequested += (_, _) => raised = true;

        var row = new PivotResultRow
        {
            KeyTupleDisplay        = "1",
            KeyTuple               = new[] { "1" },
            TrajectoryDisplay      = "x",
            AddressPerSnap         = new string?[] { null, null },
            HasCollision           = false,
            PerValueFieldSequences = new List<PivotValueFieldSequence>(),
        };

        pivot.RequestInspectInstance(row);
        Assert.False(raised);
    }

    // ─── Stage 0: class shortlist (29e-3) ──────────────────────────────

    [Fact]
    public async Task AnalyzeClasses_PopulatesShortlistAndAssemblies()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.Stat", "Assembly-CSharp",
                MakeInstance("0x1", ("id", "System.Int32", 1), ("v", "System.Int32", 10)),
                MakeInstance("0x2", ("id", "System.Int32", 2), ("v", "System.Int32", 20)))));
        snap.Snapshots.Add(MakeEnv("snap2", "b1", "s1", "2026-05-29T11:00:00Z",
            MakeClass("Game.Stat", "Assembly-CSharp",
                MakeInstance("0x3", ("id", "System.Int32", 1), ("v", "System.Int32", 11)),
                MakeInstance("0x4", ("id", "System.Int32", 2), ("v", "System.Int32", 21)))));

        await pivot.AnalyzeClassesAsync();

        var stat = Assert.Single(pivot.ClassShortlist);
        Assert.Equal("Game.Stat", stat.ClassFqn);
        Assert.Equal(2, stat.Presence);
        Assert.Equal(0.0, stat.Cv, 5);   // 2 instances both snaps → stable
        Assert.Contains("Assembly-CSharp", pivot.KnownAssemblies);
        Assert.Contains("Analyzed", pivot.ShortlistStatus);
    }

    [Fact]
    public async Task AnalyzeClasses_DefaultFilterHidesSyntheticAndPartialAndTiny()
    {
        var (snap, pivot) = BuildVms();
        // Real full-presence class with 2 fields → shown.
        // Synthetic class → hidden by default.
        // Tiny (1 field) class → hidden (nothing to key+value on).
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.Good", "asm",
                MakeInstance("0x1", ("id", "System.Int32", 1), ("v", "System.Int32", 10))),
            MakeClass("<>c__Synthetic", "asm",
                MakeInstance("0x2", ("id", "System.Int32", 1), ("v", "System.Int32", 2))),
            MakeClass("Game.Tiny", "asm",
                MakeInstance("0x3", ("only", "System.Int32", 1)))));

        await pivot.AnalyzeClassesAsync();
        Assert.Single(pivot.ClassShortlist);
        Assert.Equal("Game.Good", pivot.ClassShortlist[0].ClassFqn);

        // Toggling synthetic on surfaces the synthetic class too.
        pivot.ShortlistShowSynthetic = true;
        Assert.Contains(pivot.ClassShortlist, r => r.ClassFqn == "<>c__Synthetic");
    }

    [Fact]
    public async Task AnalyzeClasses_AssemblyFilterScopesResults()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.A", "Assembly-CSharp",
                MakeInstance("0x1", ("id", "System.Int32", 1), ("v", "System.Int32", 10))),
            MakeClass("Plugin.B", "PluginAsm",
                MakeInstance("0x2", ("id", "System.Int32", 1), ("v", "System.Int32", 20)))));
        pivot.AssemblyFilter = "PluginAsm";

        await pivot.AnalyzeClassesAsync();
        Assert.Single(pivot.ClassShortlist);
        Assert.Equal("Plugin.B", pivot.ClassShortlist[0].ClassFqn);
    }

    [Fact]
    public void UseShortlistRow_SetsClassFqnAndAssembly()
    {
        var (_, pivot) = BuildVms();
        var row = new ClassShortlistRow
        {
            Assembly = "Assembly-CSharp", ClassFqn = "Mortal.Core.GameStat",
            Presence = 4, SnapCount = 4, InstancesPerSnap = new[] { 100, 101, 100, 102 },
            AvgInstances = 100.75, StdDevInstances = 0.8, Cv = 0.008,
            FieldCount = 13, PrimitiveFieldRatio = 0.8, StringFieldRatio = 0.1,
            ArrayFieldRatio = 0.05, NullFieldRatio = 0.02, IsSynthetic = false,
            Fields = System.Array.Empty<ClassFieldInfo>(),
        };

        pivot.UseShortlistRow(row);
        Assert.Equal("Mortal.Core.GameStat", pivot.ClassFqn);
        Assert.Equal("Assembly-CSharp", pivot.AssemblyFilter);
        Assert.Contains("Class set to", pivot.StatusMessage);
    }

    // ─── Stage 1: field key auto-discovery (29e-4) ─────────────────────

    [Fact]
    public async Task ScoreFields_PopulatesScoresAndSuggestsKey()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.Stat", "asm",
                MakeInstance("0x1", ("_propertyID", "System.Int32", 101), ("_value", "System.Int32", 10)),
                MakeInstance("0x2", ("_propertyID", "System.Int32", 201), ("_value", "System.Int32", 20)))));
        snap.Snapshots.Add(MakeEnv("snap2", "b1", "s1", "2026-05-29T11:00:00Z",
            MakeClass("Game.Stat", "asm",
                MakeInstance("0x3", ("_propertyID", "System.Int32", 101), ("_value", "System.Int32", 11)),
                MakeInstance("0x4", ("_propertyID", "System.Int32", 201), ("_value", "System.Int32", 21)))));
        pivot.ClassFqn = "Game.Stat";

        await pivot.ScoreFieldsAsync();

        Assert.Empty(pivot.ErrorMessage);
        Assert.NotEmpty(pivot.FieldScores);
        // _propertyID is stable + unique → should be the suggested key.
        Assert.Contains("_propertyID", pivot.SuggestedKeyDisplay);
        Assert.Contains("unique", pivot.SuggestedKeyReason);
    }

    [Fact]
    public async Task ScoreFields_MissingClass_ReportsError()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.Real", "asm", MakeInstance("0x1", ("id", "System.Int32", 1)))));
        pivot.ClassFqn = "Game.Missing";

        await pivot.ScoreFieldsAsync();
        Assert.Contains("not found", pivot.ErrorMessage);
    }

    [Fact]
    public async Task ApplySuggestedKey_FillsKeyFieldsText()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.Stat", "asm",
                MakeInstance("0x1", ("_id", "System.Int32", 1), ("v", "System.Int32", 10)),
                MakeInstance("0x2", ("_id", "System.Int32", 2), ("v", "System.Int32", 20)))));
        pivot.ClassFqn = "Game.Stat";
        await pivot.ScoreFieldsAsync();

        pivot.ApplySuggestedKey();
        Assert.Equal("_id", pivot.KeyFieldsText);
    }

    [Fact]
    public async Task AddFieldToKeysAndValues_AppendDedup()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.Stat", "asm",
                MakeInstance("0x1", ("_itemGroup", "System.Int32", 1), ("_property", "System.Int32", 101), ("_value", "System.Int32", 10)))));
        pivot.ClassFqn = "Game.Stat";
        await pivot.ScoreFieldsAsync();

        var group    = pivot.FieldScores.Single(f => f.FieldName == "_itemGroup");
        var property = pivot.FieldScores.Single(f => f.FieldName == "_property");
        var value    = pivot.FieldScores.Single(f => f.FieldName == "_value");

        pivot.AddFieldToKeys(group);
        pivot.AddFieldToKeys(property);
        pivot.AddFieldToKeys(group);   // dedup — no double-add
        pivot.AddFieldToValues(value);

        Assert.Equal("_itemGroup, _property", pivot.KeyFieldsText);
        Assert.Equal("_value", pivot.ValueFieldsText);
    }

    [Fact]
    public async Task ScoreFields_FilterNarrowsTheList()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.Stat", "asm",
                MakeInstance("0x1", ("_hp", "System.Int32", 1), ("_mp", "System.Int32", 2), ("_name", "string", "Bob")))));
        pivot.ClassFqn = "Game.Stat";
        await pivot.ScoreFieldsAsync();
        int total = pivot.FieldScores.Count;
        Assert.True(total >= 3);

        pivot.FieldScoreFilter = "_m";   // matches _mp only
        Assert.Single(pivot.FieldScores);
        Assert.Equal("_mp", pivot.FieldScores[0].FieldName);
    }

    // ─── Stage 0 class-name filter + sort (29e-5 polish) ───────────────

    [Fact]
    public async Task ClassNameFilter_NarrowsShortlist()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.GameStat", "asm",
                MakeInstance("0x1", ("id", "System.Int32", 1), ("v", "System.Int32", 10))),
            MakeClass("Game.PlayerData", "asm",
                MakeInstance("0x2", ("id", "System.Int32", 1), ("v", "System.Int32", 20)))));
        await pivot.AnalyzeClassesAsync();
        Assert.Equal(2, pivot.ClassShortlist.Count);

        pivot.ClassNameFilter = "stat";   // case-insensitive
        Assert.Single(pivot.ClassShortlist);
        Assert.Equal("Game.GameStat", pivot.ClassShortlist[0].ClassFqn);
    }

    // (Column sort for the shortlist / field-score / results grids now
    // goes through the shared CustomSortComparer mechanism wired in
    // MainWindow.axaml.cs — a view-layer concern, not unit-tested here.)

    // ─── Stage 0 DB-seeded autocomplete (29e-7) ───────────────────────

    [Fact]
    public async Task SeedKnownClassesFromDb_PopulatesClassesAndAssembliesPreHydrate()
    {
        // VM #1 saves a snapshot to the per-game DB.
        var (snap1, _) = BuildVms();
        await snap1.OnGameDbContextAsync("b1", "mono.dll", TestContext.Current.CancellationToken);
        var env = MakeEnv("snap1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.Money", "Assembly-CSharp",
                MakeInstance("0x1", ("id", "System.Int32", 1), ("v", "System.Int32", 10))));
        env.IsDirty = true;
        env.BuildFieldIndex();
        snap1.Snapshots.Add(env);
        await snap1.SaveSnapshotAsync(env);

        // VM #2 reopens the same DB → envelopes are metadata-only (not
        // hydrated). Seeding must still fill the autocomplete from the
        // DB metadata columns, with no blob load.
        var (snap2, pivot2) = BuildVms();
        await snap2.OnGameDbContextAsync("b1", "mono.dll", TestContext.Current.CancellationToken);
        Assert.Empty(pivot2.KnownClasses);   // nothing analysed yet

        await pivot2.SeedKnownClassesFromDbAsync();
        Assert.Contains("Game.Money", pivot2.KnownClasses);
        Assert.Contains("Assembly-CSharp", pivot2.KnownAssemblies);
    }

    [Fact]
    public async Task SeedKnownClassesFromDb_NoStore_IsNoOp()
    {
        var (_, pivot) = BuildVms();   // no OnGameDbContextAsync → no store
        await pivot.SeedKnownClassesFromDbAsync();
        Assert.Empty(pivot.KnownClasses);   // soft no-op, no throw
    }

    // ─── Layer B array inner-pivot (29f-4) ─────────────────────────────

    [Fact]
    public async Task ArrayPivot_TracksElementInnerValue_AcrossSlotShuffle()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("s1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.GameStat", "asm",
                MakeInstanceWithArray("0xI1", "_statAdditionData", "Game.Add", new[]
                {
                    Elem("0xA", ("_propertyID", "System.Int32", 101), ("_amount", "System.Int32", 10)),
                    Elem("0xB", ("_propertyID", "System.Int32", 202), ("_amount", "System.Int32", 20)),
                }))));
        snap.Snapshots.Add(MakeEnv("s2", "b1", "s1", "2026-05-29T11:00:00Z",
            MakeClass("Game.GameStat", "asm",
                MakeInstanceWithArray("0xI1", "_statAdditionData", "Game.Add", new[]
                {   // reordered slots — index keying would report a false change
                    Elem("0xC", ("_propertyID", "System.Int32", 202), ("_amount", "System.Int32", 25)),
                    Elem("0xD", ("_propertyID", "System.Int32", 101), ("_amount", "System.Int32", 15)),
                }))));

        pivot.ClassFqn             = "Game.GameStat";
        pivot.ArrayPivotMode       = true;
        pivot.ArrayFieldName       = "_statAdditionData";
        pivot.InnerKeyFieldsText   = "_propertyID";
        pivot.InnerValueFieldsText = "_amount";

        await pivot.RunAsync();

        Assert.Empty(pivot.ErrorMessage);
        Assert.Equal(2, pivot.Results.Count);
        Assert.Equal("10 → 15", pivot.Results.Single(r => r.KeyTupleDisplay == "101").TrajectoryDisplay);
        Assert.Equal("20 → 25", pivot.Results.Single(r => r.KeyTupleDisplay == "202").TrajectoryDisplay);
    }

    [Fact]
    public async Task ArrayPivot_CompoundOuterKey_SeparatesSameInnerKey()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("s1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.GameStat", "asm",
                MakeInstanceWithArray("0xI1", "_data", "Game.Add",
                    new[] { Elem("0xA", ("_pid", "System.Int32", 101), ("_amt", "System.Int32", 10)) },
                    ("_statId", "System.Int32", 1)),
                MakeInstanceWithArray("0xI2", "_data", "Game.Add",
                    new[] { Elem("0xB", ("_pid", "System.Int32", 101), ("_amt", "System.Int32", 99)) },
                    ("_statId", "System.Int32", 2)))));

        pivot.ClassFqn             = "Game.GameStat";
        pivot.ArrayPivotMode       = true;
        pivot.ArrayFieldName       = "_data";
        pivot.KeyFieldsText        = "_statId";   // outer key
        pivot.InnerKeyFieldsText   = "_pid";
        pivot.InnerValueFieldsText = "_amt";

        await pivot.RunAsync();

        Assert.Equal(2, pivot.Results.Count);
        Assert.Equal("10", pivot.Results.Single(r => r.KeyTupleDisplay == "1, 101").TrajectoryDisplay);
        Assert.Equal("99", pivot.Results.Single(r => r.KeyTupleDisplay == "2, 101").TrajectoryDisplay);
    }

    [Fact]
    public async Task ArrayPivot_MissingArrayField_ReportsError()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("s1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.GameStat", "asm", MakeInstance("0x1", ("id", "System.Int32", 1)))));
        pivot.ClassFqn             = "Game.GameStat";
        pivot.ArrayPivotMode       = true;
        pivot.InnerKeyFieldsText   = "_pid";
        pivot.InnerValueFieldsText = "_amt";
        // ArrayFieldName left blank
        await pivot.RunAsync();
        Assert.Contains("Array field is required", pivot.ErrorMessage);
    }

    [Fact]
    public async Task ArrayPivot_MissingInnerKeyOrValue_ReportsError()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("s1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.GameStat", "asm", MakeInstance("0x1", ("id", "System.Int32", 1)))));
        pivot.ClassFqn       = "Game.GameStat";
        pivot.ArrayPivotMode = true;
        pivot.ArrayFieldName = "_data";
        pivot.InnerValueFieldsText = "_amt";   // inner key blank
        await pivot.RunAsync();
        Assert.Contains("inner key field", pivot.ErrorMessage);

        pivot.ErrorMessage         = string.Empty;
        pivot.InnerKeyFieldsText   = "_pid";
        pivot.InnerValueFieldsText = string.Empty;   // inner value blank
        await pivot.RunAsync();
        Assert.Contains("inner value field", pivot.ErrorMessage);
    }

    [Fact]
    public async Task ArrayPivot_NoCapturedElements_ZeroRowsWithLayerBHint()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("s1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.GameStat", "asm",
                MakeInstanceWithArray("0xI1", "_data", "Game.Add", new[]
                {   // pre-Layer-B elements: no inner fields captured
                    ElemNoFields("0xA"), ElemNoFields("0xB"),
                }))));
        pivot.ClassFqn             = "Game.GameStat";
        pivot.ArrayPivotMode       = true;
        pivot.ArrayFieldName       = "_data";
        pivot.InnerKeyFieldsText   = "_pid";
        pivot.InnerValueFieldsText = "_amt";

        await pivot.RunAsync();

        Assert.Empty(pivot.ErrorMessage);
        Assert.Empty(pivot.Results);
        Assert.Contains("Capture array element fields", pivot.StatusMessage);
    }

    [Fact]
    public void ArrayPivot_AutocompletePopulatesFromHydratedElements()
    {
        var (snap, pivot) = BuildVms();
        snap.Snapshots.Add(MakeEnv("s1", "b1", "s1", "2026-05-29T10:00:00Z",
            MakeClass("Game.GameStat", "asm",
                MakeInstanceWithArray("0xI1", "_statAdditionData", "Game.Add", new[]
                {
                    Elem("0xA", ("_propertyID", "System.Int32", 101), ("_amount", "System.Int32", 10)),
                }))));

        pivot.ClassFqn = "Game.GameStat";
        Assert.Contains("_statAdditionData", pivot.KnownArrayFields);

        pivot.ArrayFieldName = "_statAdditionData";
        Assert.Contains("_propertyID", pivot.KnownElementFieldNames);
        Assert.Contains("_amount", pivot.KnownElementFieldNames);
    }

}
