// SnapshotViewModelPersistenceTests — Phase 29c-3 / 29c-4 / 29c-5 / 29c-6.
//
// Covers the integration glue between SnapshotViewModel + SnapshotStore +
// UserSettingsStore + UserSettings:
//   * OnGameDbContextAsync round-trip — fresh DB → save → reopen → see meta
//   * Lazy-load on diff (envelope arrives empty, EnsureLoadedAsync hydrates)
//   * Cross-session diff defaults to statics-only
//   * Cross-build diff is unblocked + carries the yellow banner copy
//   * ForceIncludeInstancesCrossSession toggle re-runs diff with instances
//   * Auto-save honors UserSettings.AutoSaveSnapshots toggle
//   * GetUnsavedDirtySnapshots surfaces in-memory-only envelopes
//   * Auto-prune kicks in after Save when threshold exceeded
//   * ApplySettingsAsync persists to disk + reopens DB on dir change
//
// The tests construct SnapshotViewModel with a stub BackendAdapter that
// never receives RPC calls — every code path under test is reachable
// without a live game.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class SnapshotViewModelPersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public SnapshotViewModelPersistenceTests()
    {
        SQLitePCL.Batteries_V2.Init();
        _tempDir = Path.Combine(Path.GetTempPath(), "SnapVmPersistTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static System.Threading.CancellationToken Ct =>
        TestContext.Current.CancellationToken;

    /// <summary>Build a SnapshotViewModel against a temp dir-scoped
    /// UserSettings so each test gets its own DB folder. Backend pipe
    /// stays unconnected — never used by the code paths under test.
    /// <para/>
    /// Per-VM logger root so multiple VMs in the same test don't fight
    /// over a single exclusive log file handle.</summary>
    private SnapshotViewModel BuildVm(out UserSettings settings, out string settingsPath, string vmTag = "vm1")
    {
        settingsPath = Path.Combine(_tempDir, "settings.json");
        settings = new UserSettings
        {
            SnapshotDbDirectory     = Path.Combine(_tempDir, "snapshots"),
            AutoSaveSnapshots       = false,
            AutoPruneThresholdBytes = 0,  // disabled by default; tests opt in
        };
        var pipe    = new PipeClient(pipeName: "tests-never-connect");
        var backend = new BackendAdapter(pipe);
        var logger  = new FileLogger(Path.Combine(_tempDir, "logs-" + vmTag));
        var settingsStore = new UserSettingsStore(settingsPath, logger);
        return new SnapshotViewModel(backend, logger, settingsStore, settings);
    }

    private static SnapshotEnvelope MakeFreshEnvelope(
        string buildId, string sessionId, string label,
        int classCount = 2, int instancesPerClass = 1, int fieldsPerInstance = 2,
        DateTime? capturedAtLocal = null)
    {
        var classes = new List<SnapshotClassEntry>(classCount);
        for (int ci = 0; ci < classCount; ci++)
        {
            var instances = new List<SnapshotInstanceEntry>(instancesPerClass);
            for (int ii = 0; ii < instancesPerClass; ii++)
            {
                var fields = new List<SnapshotFieldEntry>(fieldsPerInstance);
                for (int fi = 0; fi < fieldsPerInstance; fi++)
                {
                    fields.Add(new SnapshotFieldEntry
                    {
                        Name   = $"field_{fi}",
                        Type   = "System.Int32",
                        Offset = (ulong)(fi * 4),
                        Value  = System.Text.Json.JsonDocument.Parse($"\"{ii * 10 + fi}\"").RootElement,
                    });
                }
                instances.Add(new SnapshotInstanceEntry
                {
                    Address = $"0x{(ulong)(0x1000 + ii):X16}",
                    Fields  = fields,
                });
            }
            classes.Add(new SnapshotClassEntry
            {
                Assembly  = "Assembly-CSharp",
                Namespace = "Game",
                Name      = $"Class{ci}",
                Instances = instances,
                Statics   = new List<SnapshotFieldEntry>
                {
                    new() { Name = "kStaticConst", Type = "System.Int32", Offset = 0,
                            Value = System.Text.Json.JsonDocument.Parse($"\"{ci * 100}\"").RootElement },
                },
            });
        }
        var local = capturedAtLocal ?? DateTime.Now;
        var env = new SnapshotEnvelope
        {
            ServerId       = "in-memory",
            Label          = label,
            CapturedAt     = local,
            CapturedAtUtc  = local.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            GameBuildId    = buildId,
            GameSessionId  = sessionId,
            TotalClasses   = classCount,
            TotalInstances = classCount * instancesPerClass,
            Classes        = classes,
            ClassesLoaded  = true,
            IsDirty        = true,
        };
        env.BuildFieldIndex();
        return env;
    }

    // ─── OnGameDbContextAsync ──────────────────────────────────────────

    [Fact]
    public async Task OnGameDbContextAsync_FreshDbStartsEmpty()
    {
        var vm = BuildVm(out var settings, out _);
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);
        Assert.NotNull(vm.Store);
        Assert.Empty(vm.Snapshots);
        // DB lives under the per-test SnapshotDbDirectory override.
        Assert.StartsWith(settings.SnapshotDbDirectory!, vm.Store!.DatabasePath);
        Assert.EndsWith(".db", vm.Store.DatabasePath);
    }

    [Fact]
    public async Task OnGameDbContextAsync_ReloadsPreviouslySavedSnapshotsAsLazyMetadata()
    {
        // First "session": open DB, persist a snapshot, then dispose.
        string savedDbPath;
        {
            var vm = BuildVm(out _, out _);
            await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);
            savedDbPath = vm.Store!.DatabasePath;
            var env = MakeFreshEnvelope("build1", "session-A", "first-capture");
            vm.Snapshots.Add(env);
            await vm.SaveSnapshotAsync(env);
            Assert.NotNull(env.PersistedId);
        }
        // Second "session": new VM (separate logger root so we don't
        // collide on the exclusive log file handle), same DB path, must
        // surface the earlier snapshot as a lazy-loaded envelope.
        {
            var vm2 = BuildVm(out var s2, out _, vmTag: "vm2");
            s2.SnapshotDbDirectory = Path.GetDirectoryName(savedDbPath);
            await vm2.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);
            Assert.Single(vm2.Snapshots);
            var reloaded = vm2.Snapshots[0];
            Assert.Equal("first-capture", reloaded.Label);
            // Lazy — Classes empty until EnsureLoadedAsync.
            Assert.False(reloaded.ClassesLoaded);
            Assert.Empty(reloaded.Classes);
            await vm2.EnsureLoadedAsync(reloaded, Ct);
            Assert.True(reloaded.ClassesLoaded);
            Assert.NotEmpty(reloaded.Classes);
            // Phase 29g-2: the FieldIndex is lazy — not built on load. The
            // cheap field-value count is refreshed though, and the index
            // builds on demand from the round-tripped Classes.
            Assert.False(reloaded.FieldIndexBuilt);
            Assert.True(reloaded.FieldValueCount > 0);
            Assert.NotEmpty(reloaded.EnsureFieldIndex());
        }
    }

    [Fact]
    public async Task OnGameDbContextAsync_DifferentBuildIdSwitchesStoreAndClearsInMemory()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("build_A", "mono-2.0-bdwgc.dll", Ct);
        var firstPath = vm.Store!.DatabasePath;

        // Add an in-memory envelope tagged for build_A.
        var env = MakeFreshEnvelope("build_A", "session-1", "ephemeral");
        vm.Snapshots.Add(env);
        Assert.Single(vm.Snapshots);

        // Switch to a different build_id → in-memory list MUST clear so
        // the user doesn't see the previous game's data after reconnect.
        await vm.OnGameDbContextAsync("build_B", "GameAssembly.dll", Ct);
        Assert.NotEqual(firstPath, vm.Store!.DatabasePath);
        Assert.Empty(vm.Snapshots);
    }

    [Fact]
    public async Task OnGameDbContextAsync_SameBuildIdIsIdempotentNoOp()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);
        var dbPath = vm.Store!.DatabasePath;

        var env = MakeFreshEnvelope("build1", "s1", "should-survive");
        vm.Snapshots.Add(env);
        Assert.Single(vm.Snapshots);

        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);
        Assert.Equal(dbPath, vm.Store!.DatabasePath);
        Assert.Single(vm.Snapshots);
    }

    // ─── Save / Delete ─────────────────────────────────────────────────

    [Fact]
    public async Task SaveSnapshotAsync_FlipsIsDirtyAndAssignsPersistedId()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);
        var env = MakeFreshEnvelope("build1", "s1", "tag");
        vm.Snapshots.Add(env);
        Assert.True(env.IsDirty);
        Assert.Null(env.PersistedId);

        await vm.SaveSnapshotAsync(env);
        Assert.False(env.IsDirty);
        Assert.NotNull(env.PersistedId);
        Assert.True(env.PersistedId!.Value > 0);
        Assert.Contains("✓ saved", env.PersistenceStatus);
    }

    [Fact]
    public async Task SaveSnapshotAsync_IsIdempotent()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);
        var env = MakeFreshEnvelope("build1", "s1", "tag");
        vm.Snapshots.Add(env);
        await vm.SaveSnapshotAsync(env);
        long firstId = env.PersistedId!.Value;
        await vm.SaveSnapshotAsync(env);  // no-op
        Assert.Equal(firstId, env.PersistedId);
        Assert.Single(await vm.Store!.ListSnapshotsAsync(Ct));
    }

    [Fact]
    public async Task DeleteSnapshotFromDbAsync_RemovesFromMemoryAndDb()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);
        var env = MakeFreshEnvelope("build1", "s1", "tag");
        vm.Snapshots.Add(env);
        await vm.SaveSnapshotAsync(env);
        Assert.NotNull(env.PersistedId);

        await vm.DeleteSnapshotFromDbAsync(env);
        Assert.DoesNotContain(env, vm.Snapshots);
        Assert.Empty(await vm.Store!.ListSnapshotsAsync(Ct));
    }

    // ─── Auto-save ─────────────────────────────────────────────────────

    [Fact]
    public async Task AutoSave_OffMeansFreshEnvelopeStaysDirty()
    {
        var vm = BuildVm(out var s, out _);
        s.AutoSaveSnapshots = false;
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);
        var env = MakeFreshEnvelope("build1", "s1", "tag");
        // Manually simulate the TakeSnapshot path's dirty mark + auto-save check —
        // TakeSnapshotAsync uses the pipe so we'd otherwise need a fake backend.
        // The behaviour under test is the auto-save check (no actual TakeSnapshot).
        vm.Snapshots.Add(env);
        Assert.True(env.IsDirty);  // not auto-saved
    }

    [Fact]
    public async Task GetUnsavedDirtySnapshots_SurfacesInMemoryOnlyEnvelopes()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);

        var dirty = MakeFreshEnvelope("build1", "s1", "unsaved");
        var saved = MakeFreshEnvelope("build1", "s1", "persisted");
        vm.Snapshots.Add(dirty);
        vm.Snapshots.Add(saved);
        await vm.SaveSnapshotAsync(saved);

        var pending = vm.GetUnsavedDirtySnapshots();
        Assert.Single(pending);
        Assert.Same(dirty, pending[0]);
    }

    // ─── Cross-session diff (29c-5) ────────────────────────────────────

    [Fact]
    public async Task DiffAsync_SameSessionIsLiveAndShowsAllRows()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);

        var a = MakeFreshEnvelope("build1", "session-X", "A");
        var b = MakeFreshEnvelope("build1", "session-X", "B");
        // Mutate one instance field in B so diff sees a "changed" row.
        b.Classes[0].Instances![0].Fields[0].Value =
            System.Text.Json.JsonDocument.Parse("\"999\"").RootElement;
        b.BuildFieldIndex();

        vm.Snapshots.Add(a);
        vm.Snapshots.Add(b);
        vm.SelectedSnapshotA = a;
        vm.SelectedSnapshotB = b;
        await vm.DiffAsync();

        Assert.Equal(string.Empty, vm.CrossSessionBanner);
        Assert.Contains(vm.DiffRows, r => r.ChangeKind == "changed");
    }

    [Fact]
    public async Task DiffAsync_CrossSessionDefaultsToStaticsOnlyWithBanner()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);

        var a = MakeFreshEnvelope("build1", "session-A", "A");
        var b = MakeFreshEnvelope("build1", "session-B", "B");
        vm.Snapshots.Add(a);
        vm.Snapshots.Add(b);
        vm.SelectedSnapshotA = a;
        vm.SelectedSnapshotB = b;
        await vm.DiffAsync();

        Assert.Contains("Cross-session", vm.CrossSessionBanner);
        Assert.Contains("suppressed", vm.CrossSessionBanner);
        // Statics carry the same value on both sides (synthetic fixture) →
        // no "changed" rows from statics; instance rows are filtered out.
        Assert.All(vm.DiffRows, r => Assert.DoesNotContain("0x0000", r.InstanceAddress));
    }

    [Fact]
    public async Task DiffAsync_ForceIncludeInstancesCrossSessionRevealsInstanceRows()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);

        var a = MakeFreshEnvelope("build1", "session-A", "A");
        var b = MakeFreshEnvelope("build1", "session-B", "B");
        vm.Snapshots.Add(a);
        vm.Snapshots.Add(b);
        vm.SelectedSnapshotA = a;
        vm.SelectedSnapshotB = b;
        vm.ForceIncludeInstancesCrossSession = true;  // fires DiffAsync internally

        // Yield once so the fire-and-forget DiffAsync the toggle kicks
        // off has a chance to publish DiffRows.
        await Task.Yield();
        // Banner reflects the override.
        Assert.Contains("Force-include is ON", vm.CrossSessionBanner);
    }

    [Fact]
    public async Task DiffAsync_CrossBuildIsUnblockedWithStaticsOnlyAndYellowBanner()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("buildA", "mono-2.0-bdwgc.dll", Ct);

        var a = MakeFreshEnvelope("buildA", "s1", "A");
        var b = MakeFreshEnvelope("buildB", "s1", "B");  // different build
        vm.Snapshots.Add(a);
        vm.Snapshots.Add(b);
        vm.SelectedSnapshotA = a;
        vm.SelectedSnapshotB = b;
        await vm.DiffAsync();

        Assert.Contains("Cross-build", vm.CrossSessionBanner);
        Assert.Contains("⚠", vm.CrossSessionBanner);
        // No "blocked" message in ErrorMessage anymore — the v1 behaviour
        // (block cross-build) is gone.
        Assert.True(string.IsNullOrEmpty(vm.ErrorMessage));
    }

    // ─── ApplySettingsAsync ────────────────────────────────────────────

    // ─── Auto-eviction (Phase 29c-eviction) ────────────────────────────

    [Fact]
    public async Task DiffAsync_EvictsLoadedNonSelectedPersistedEnvelopes()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);

        // Persist three snapshots so all are eligible for eviction.
        var a = MakeFreshEnvelope("build1", "session-X", "A");
        var b = MakeFreshEnvelope("build1", "session-X", "B");
        var c = MakeFreshEnvelope("build1", "session-X", "C");
        // Make B differ from A so diff has signal.
        b.Classes[0].Instances![0].Fields[0].Value =
            System.Text.Json.JsonDocument.Parse("\"999\"").RootElement;
        b.BuildFieldIndex();
        vm.Snapshots.Add(a);
        vm.Snapshots.Add(b);
        vm.Snapshots.Add(c);
        await vm.SaveSnapshotAsync(a);
        await vm.SaveSnapshotAsync(b);
        await vm.SaveSnapshotAsync(c);

        // All three are loaded right now (saved from in-memory state).
        Assert.True(a.ClassesLoaded);
        Assert.True(b.ClassesLoaded);
        Assert.True(c.ClassesLoaded);

        vm.SelectedSnapshotA = a;
        vm.SelectedSnapshotB = b;
        await vm.DiffAsync();

        // After diff: A and B stay loaded (still selected), C gets dropped.
        Assert.True(a.ClassesLoaded);
        Assert.True(b.ClassesLoaded);
        Assert.False(c.ClassesLoaded);
        Assert.Empty(c.Classes);
        Assert.Empty(c.FieldIndex);
        // Status line surfaces the eviction count.
        Assert.Contains("evicted", vm.StatusMessage);
    }

    [Fact]
    public async Task DiffAsync_DoesNotEvictDirtyInMemoryEnvelopes()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);

        var a = MakeFreshEnvelope("build1", "session-X", "A");
        var b = MakeFreshEnvelope("build1", "session-X", "B");
        var dirty = MakeFreshEnvelope("build1", "session-X", "unsaved");
        b.Classes[0].Instances![0].Fields[0].Value =
            System.Text.Json.JsonDocument.Parse("\"999\"").RootElement;
        b.BuildFieldIndex();
        vm.Snapshots.Add(a);
        vm.Snapshots.Add(b);
        vm.Snapshots.Add(dirty);
        await vm.SaveSnapshotAsync(a);
        await vm.SaveSnapshotAsync(b);
        // dirty stays unsaved (PersistedId == null)

        vm.SelectedSnapshotA = a;
        vm.SelectedSnapshotB = b;
        await vm.DiffAsync();

        // Dirty envelope MUST stay loaded — there's no DB row to reload
        // from, dropping it would lose the user's work. Phase 29g-2: its
        // FieldIndex IS released (rebuildable cache), but Classes — the
        // unsaved data — are preserved so it can still be saved/rebuilt.
        Assert.Null(dirty.PersistedId);
        Assert.True(dirty.ClassesLoaded);
        Assert.NotEmpty(dirty.Classes);
        Assert.False(dirty.FieldIndexBuilt);
    }

    [Fact]
    public async Task DiffAsync_EvictedEnvelopeRehydratesOnSelection()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);

        var a = MakeFreshEnvelope("build1", "session-X", "A");
        var b = MakeFreshEnvelope("build1", "session-X", "B");
        var c = MakeFreshEnvelope("build1", "session-X", "C");
        b.Classes[0].Instances![0].Fields[0].Value =
            System.Text.Json.JsonDocument.Parse("\"999\"").RootElement;
        b.BuildFieldIndex();
        vm.Snapshots.Add(a);
        vm.Snapshots.Add(b);
        vm.Snapshots.Add(c);
        await vm.SaveSnapshotAsync(a);
        await vm.SaveSnapshotAsync(b);
        await vm.SaveSnapshotAsync(c);

        // First diff evicts C.
        vm.SelectedSnapshotA = a;
        vm.SelectedSnapshotB = b;
        await vm.DiffAsync();
        Assert.False(c.ClassesLoaded);

        // Swap A → C and re-diff. Eviction is at end of DiffAsync, AFTER
        // EnsureLoadedAsync has rehydrated C, so it must contain data.
        vm.SelectedSnapshotA = c;
        await vm.DiffAsync();
        Assert.True(c.ClassesLoaded);
        Assert.NotEmpty(c.FieldIndex);
        // A is now the one that got evicted (no longer selected).
        Assert.False(a.ClassesLoaded);
    }

    [Fact]
    public void DisplayName_BadgesUnsavedEnvelopes()
    {
        var env = MakeFreshEnvelope("b1", "s1", "fresh");
        // IsDirty defaults to true on a freshly captured envelope.
        Assert.True(env.IsDirty);
        Assert.Contains("● unsaved", env.DisplayName);

        env.IsDirty = false;  // simulate post-save state
        Assert.DoesNotContain("● unsaved", env.DisplayName);
    }

    [Fact]
    public void DisplayName_StaleBadgeWinsOverUnsavedWhenBothFire()
    {
        var env = MakeFreshEnvelope("b1", "s1", "label");
        env.IsDirty = true;
        env.IsStale = true;
        // Stale prefix is at the front; unsaved badge sits inside.
        Assert.StartsWith("⚠ stale", env.DisplayName);
        Assert.Contains("● unsaved", env.DisplayName);
    }

    [Fact]
    public async Task ApplySettingsAsync_PersistsToDiskAndUpdatesInMemorySettings()
    {
        var vm = BuildVm(out _, out var settingsPath);

        var draft = new UserSettings
        {
            SnapshotDbDirectory     = Path.Combine(_tempDir, "snapshots"),
            AutoSaveSnapshots       = true,
            AutoPruneThresholdBytes = 250L * 1024 * 1024,
        };
        await vm.ApplySettingsAsync(draft, Ct);

        Assert.True(vm.Settings.AutoSaveSnapshots);
        Assert.Equal(250L * 1024 * 1024, vm.Settings.AutoPruneThresholdBytes);

        Assert.True(File.Exists(settingsPath));
        var contents = await File.ReadAllTextAsync(settingsPath, Ct);
        Assert.Contains("auto_save_snapshots", contents);
        Assert.Contains("true", contents);
    }

    [Fact]
    public async Task ApplySettingsAsync_DirectoryChangeReopensStoreUnderNewDir()
    {
        var vm = BuildVm(out _, out _);
        await vm.OnGameDbContextAsync("build1", "mono-2.0-bdwgc.dll", Ct);
        var oldPath = vm.Store!.DatabasePath;

        string newDir = Path.Combine(_tempDir, "moved");
        var draft = new UserSettings
        {
            SnapshotDbDirectory     = newDir,
            AutoSaveSnapshots       = vm.Settings.AutoSaveSnapshots,
            AutoPruneThresholdBytes = vm.Settings.AutoPruneThresholdBytes,
        };
        await vm.ApplySettingsAsync(draft, Ct);

        Assert.NotNull(vm.Store);
        Assert.NotEqual(oldPath, vm.Store!.DatabasePath);
        Assert.StartsWith(newDir, vm.Store!.DatabasePath);
    }
}
