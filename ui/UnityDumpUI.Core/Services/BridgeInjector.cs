// BridgeInjector.cs — Phase 22d orchestrator for the
// "Inject Discrete Bridge into CE Table" Tools-menu action.
//
// Wraps the sequential AOBMaker pipe calls the bridge install consists
// of: 1 CheckAvailability + N InjectTableFile (N = the picked file
// subset) + 1 CreateAAScript (the "Load Discrete Bridge" bootstrap; its
// [DISABLE] does the dormant teardown). Lives in Core so the test
// rig can drive it with a mock IAobMakerBridge — the
// MainWindowViewModel is too entangled with PipeClient + FileLogger
// to test through.
//
// Failure semantics: stop on the first error. The user sees a status
// line pointing at the exact step that failed + the AOBMaker plugin's
// error message. Partial progress IS NOT rolled back — re-running the
// command after fixing the cause re-injects everything (InjectTableFile
// is idempotent on the plugin side: existing same-name files are
// deleted before recreation; CreateAAScript appends a new record).
//
// Why no rollback: the AOBMaker protocol has no "delete table file"
// or "delete memory record" commands. The user can manually clean up
// in CE if they want a pristine state. In practice the most common
// failure (CE pipe busy, plugin not loaded) leaves zero side effects
// because the very first call fails.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services.Export;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// One step in the bridge install. <see cref="Description"/> is a
/// human-readable label (e.g. "Injecting unity_invoke.lua",
/// "Creating Load Discrete Bridge AA entry"). <see cref="Index"/> is
/// 1-based, <see cref="Total"/> is the step count for the whole run.
/// </summary>
public sealed record BridgeInjectProgress(string Description, int Index, int Total);

/// <summary>
/// Outcome of a <see cref="BridgeInjector.InjectAsync"/> run.
/// </summary>
/// <param name="Ok">True iff every step succeeded.</param>
/// <param name="FilesInjected">How many of the N bridge files actually
/// landed in the CT before any failure (or all of them on success).</param>
/// <param name="AaScriptCreated">True iff the bootstrap AA-script entry
/// was created. Implies <see cref="FilesInjected"/> == file count.</param>
/// <param name="FailedStepDescription">When <c>!Ok</c>, the
/// <see cref="BridgeInjectProgress.Description"/> of the step that
/// failed (so callers can build "Step 3/8 'Injecting unity_dump.lua' failed: …" without re-deriving labels).</param>
/// <param name="ErrorMessage">When <c>!Ok</c>, the underlying reason —
/// the AOBMaker plugin's <c>message</c> field on a plugin-side failure,
/// or a UI-side description (e.g. "AOBMaker pipe unavailable") on a
/// connect-class failure.</param>
public sealed record BridgeInjectResult(
    bool    Ok,
    int     FilesInjected,
    bool    AaScriptCreated,
    string? FailedStepDescription = null,
    string? ErrorMessage          = null);

public static class BridgeInjector
{
    /// <summary>
    /// Run the full 8-step bridge install: 1× CheckAvailability +
    /// N× InjectTableFile (one per <see cref="BridgeLuaResources.LoadOrder"/>
    /// entry) + 1× CreateAAScript for the bootstrap entry. Stops on
    /// the first failure.
    /// </summary>
    /// <param name="bridge">The AOBMaker bridge. Must not be null.</param>
    /// <param name="progress">Optional sink for per-step status. Each
    /// step fires once before the underlying pipe call begins. Use it
    /// to update a UI status text. Steps are reported with 1-based index;
    /// the availability check is step 1, file inject N is step (N+1),
    /// the bootstrap AA is the last step.</param>
    /// <param name="ct">Cancellation. Cancelling between steps is
    /// honoured immediately; cancelling mid-pipe-call propagates as
    /// the underlying bridge method's normal cancellation behaviour.</param>
    public static async Task<BridgeInjectResult> InjectAsync(
        IAobMakerBridge                 bridge,
        IProgress<BridgeInjectProgress>? progress = null,
        CancellationToken               ct        = default)
        => await InjectAsync(bridge, fileNames: null, progress, ct).ConfigureAwait(false);

    /// <summary>
    /// Selective overload: inject only <paramref name="fileNames"/> (e.g.
    /// the output of <see cref="BridgeLuaResources.ResolveFiles"/>) instead
    /// of the full <see cref="BridgeLuaResources.LoadOrder"/>. The same
    /// subset drives both the InjectTableFile loop AND the bootstrap AA
    /// script's load list, so the CT only carries — and the AA only loads —
    /// the lua the user actually picked. Pass <c>null</c> for the full set.
    /// </summary>
    public static async Task<BridgeInjectResult> InjectAsync(
        IAobMakerBridge                 bridge,
        IReadOnlyList<string>?          fileNames,
        IProgress<BridgeInjectProgress>? progress = null,
        CancellationToken               ct        = default)
    {
        if (bridge is null) throw new ArgumentNullException(nameof(bridge));

        IReadOnlyList<string> selected = fileNames ?? BridgeLuaResources.LoadOrder;
        IReadOnlyList<(string FileName, string Content)> files = BridgeLuaResources.ReadAll(selected);
        int totalSteps = 1 /* check */ + files.Count + 1 /* bootstrap AA */;

        // Step 1: availability probe. CheckAvailabilityAsync flips
        // IsAvailable + fires AvailabilityChanged, so the menu's
        // IsEnabled refresh on the VM side picks up "actually
        // unavailable" before we even start the inject loop.
        progress?.Report(new BridgeInjectProgress(
            "Checking AOBMaker plugin availability", 1, totalSteps));
        ct.ThrowIfCancellationRequested();
        if (!await bridge.CheckAvailabilityAsync(ct).ConfigureAwait(false))
        {
            return new BridgeInjectResult(
                Ok: false, FilesInjected: 0, AaScriptCreated: false,
                FailedStepDescription: "Checking AOBMaker plugin availability",
                ErrorMessage: "AOBMaker plugin pipe unreachable. Open Cheat Engine with the AOBMaker plugin loaded and try again.");
        }

        // Steps 2…N+1: inject each bridge file. Using AOBMaker's
        // InjectTableFile (raw UTF-8 content; plugin internally
        // findTableFile-deletes any existing same-name entry).
        int injected = 0;
        for (int i = 0; i < files.Count; ++i)
        {
            string fileName = files[i].FileName;
            string content  = files[i].Content;
            string desc     = $"Injecting {fileName}";

            progress?.Report(new BridgeInjectProgress(desc, 2 + i, totalSteps));
            ct.ThrowIfCancellationRequested();

            (bool ok, string? errorMessage) = await bridge
                .InjectTableFileAsync(fileName, content, ct).ConfigureAwait(false);
            if (!ok)
            {
                return new BridgeInjectResult(
                    Ok: false, FilesInjected: injected, AaScriptCreated: false,
                    FailedStepDescription: desc,
                    ErrorMessage: errorMessage ?? "AOBMaker pipe disconnected mid-inject");
            }
            ++injected;
        }

        // Final step: bootstrap AA script. autoActivate=false so the
        // user sees the new entry in CE's address list and can read
        // its description before flipping the checkbox — they
        // probably want to verify the game is attached + DiscreteUI's
        // DLL is injected before letting the bootstrap run.
        const string finalDesc = "Creating " + BootstrapAaScriptGenerator.EntryDescription + " AA entry";
        progress?.Report(new BridgeInjectProgress(finalDesc, totalSteps, totalSteps));
        ct.ThrowIfCancellationRequested();

        bool aaOk = await bridge.CreateAAScriptAsync(
            description: BootstrapAaScriptGenerator.EntryDescription,
            // Build the bootstrap over the SAME subset we just injected, so
            // its findTableFile() load list never references a file the CT
            // doesn't carry.
            script:      BootstrapAaScriptGenerator.Build(selected),
            autoActivate: false,
            ct:          ct).ConfigureAwait(false);
        if (!aaOk)
        {
            return new BridgeInjectResult(
                Ok: false, FilesInjected: injected, AaScriptCreated: false,
                FailedStepDescription: finalDesc,
                ErrorMessage: "AOBMaker CreateAAScript failed (see %LOCALAPPDATA%\\Discrete_Dumper\\Logs\\ui\\current.log for plugin-side detail)");
        }

        return new BridgeInjectResult(
            Ok: true, FilesInjected: injected, AaScriptCreated: true);
    }
}
