// SceneExplorerViewModel.cs — Phase 20b Scene Explorer tab.
//
// Filter-driven Scene tree. The user types a name pattern, clicks Search;
// the VM calls SceneEnumerator to fetch GameObject addresses, batches name
// decodes, filters by substring, populates top-level tree nodes. Each
// node lazy-expands on chevron click via getChildren.
//
// Surface (Scene tab):
//   * Pattern TextBox — substring filter; empty = "first N alphabetically"
//   * Limit NumericUpDown — caps the initial heap-fetch (1..500, default 200)
//   * Search command — fires the enumeration + filter pass
//   * TreeView — Roots collection; each row has a lazy Children list
//   * Right-click → Inspect (cross-tab handoff) / Copy address
//
// Cross-tab handoff: `NavigateToInspectorRequested(string address)` event
// is raised by the code-behind context menu. MainWindowViewModel wires
// that to `Inspector.FocusOnInstance(addr)` + `SelectedDetailTabIndex
// = TabIndexInspector` — same pattern as the Phase 19d-2 Methods → Inspector
// hop.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class SceneExplorerViewModel : ObservableObject
{
    private readonly SceneEnumerator _enumerator;
    private readonly FileLogger      _logger;
    private CancellationTokenSource? _searchCts;

    public SceneExplorerViewModel(SceneEnumerator enumerator, FileLogger logger)
    {
        _enumerator = enumerator;
        _logger     = logger;
    }

    /// <summary>The underlying enumerator. Exposed so
    /// <see cref="MainWindowViewModel"/> can reuse it for cross-tab affordances
    /// like the connection-bar "Enable runInBackground" banner (Phase 20-MT v2)
    /// without spinning up a second SceneEnumerator that would maintain its
    /// own (out-of-sync) <c>_resolvedUnityEngineAssembly</c> cache.</summary>
    public SceneEnumerator Enumerator => _enumerator;

    public ObservableCollection<SceneNode> Roots { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _pattern = string.Empty;

    [ObservableProperty]
    private int _limit = 200;

    /// <summary>
    /// Phase 20-MT workaround — seconds to wait before issuing the
    /// first Unity icall when the user clicks Search. Lets the user
    /// alt-tab back to the game window so it regains focus and
    /// `Update()` resumes ticking before the
    /// `Type.GetType` / `Object.FindObjectsByType` calls fire through
    /// the main-thread dispatcher.
    ///
    /// Without this, on Unity targets where
    /// <c>Application.runInBackground = false</c> (the engine default)
    /// the dispatcher hook never gets a chance to drain the queue
    /// because the engine pauses Update() the moment DiscreteUI takes
    /// focus → every <c>mainThread:true</c> RPC times out.
    ///
    /// 0 = no delay (default — fine when the game is already
    /// configured with <c>runInBackground=true</c> or when the user
    /// can keep the game window focused). Capped at 10 seconds in the
    /// XAML NumericUpDown.
    /// </summary>
    [ObservableProperty]
    private int _focusDelaySeconds;

    /// <summary>
    /// True once <c>Application.runInBackground = true</c> has been
    /// successfully applied via <see cref="EnableRunInBackgroundCommand"/>.
    /// Drives the button's IsEnabled in XAML so the user can't double-
    /// click it. The flag is per-session (resets when the user reconnects
    /// — the underlying Unity state also resets when the game restarts).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnableRunInBackgroundCommand))]
    private bool _runInBackgroundEnabled;

    /// <summary>
    /// Set once <see cref="EnableRunInBackgroundAsync"/> comes back
    /// <c>method_not_found</c> — the target stripped
    /// <c>Application.set_runInBackground</c> (IL2CPP code stripping), so the
    /// toggle can never succeed on this game. Greys out the Enable button; the
    /// status/error explains the focus workaround. Reset by
    /// <see cref="MainWindowViewModel"/> on disconnect.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnableRunInBackgroundCommand))]
    private bool _runInBackgroundUnavailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnableRunInBackgroundCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Type a name pattern (or leave blank for first N) and press Search.";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private SceneNode? _selectedNode;

    /// <summary>
    /// Raised when the user activates a node (right-click → Inspect or
    /// double-click). <see cref="MainWindowViewModel"/> consumes this
    /// to flip the right-pane tab and populate the Inspector's address.
    /// </summary>
    public event Action<string>? NavigateToInspectorRequested;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    public async Task SearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsBusy        = true;
        ErrorMessage  = null;
        Roots.Clear();

        var sw = Stopwatch.StartNew();
        try
        {
            int limit = Math.Clamp(Limit, 1, 500);
            string pattern = (Pattern ?? string.Empty).Trim();

            // Phase 20-MT workaround: countdown before issuing icalls so
            // the user can alt-tab back to the game window. See
            // FocusDelaySeconds docs for why.
            int delay = Math.Clamp(FocusDelaySeconds, 0, 10);

            // Phase 20-MT v2: pre-flight dispatcher check. Skipped when the
            // user has already set up a workaround — Focus delay > 0 means
            // they're going to alt-tab during the countdown; the per-session
            // RunInBackgroundEnabled flag means we already toggled the
            // engine flag. Both are explicit "I know what I'm doing"
            // signals. Otherwise, eager-fail with a clear message saves
            // the user a 5-second timeout wait on the first icall.
            if (delay == 0 && !RunInBackgroundEnabled)
            {
                string? preflightErr =
                    await _enumerator.PreflightDispatcherCheckAsync(ct).ConfigureAwait(true);
                if (preflightErr is not null)
                {
                    ErrorMessage  = preflightErr;
                    StatusMessage = "Search blocked — dispatcher pre-flight failed.";
                    return;
                }
            }

            for (int s = delay; s > 0; --s)
            {
                ct.ThrowIfCancellationRequested();
                StatusMessage = $"Focus the game window — resuming in {s}s…";
                await Task.Delay(1000, ct).ConfigureAwait(true);
            }

            StatusMessage = string.IsNullOrEmpty(pattern)
                ? $"Enumerating up to {limit} GameObjects…"
                : $"Enumerating up to {limit} GameObjects and filtering by \"{pattern}\"…";

            EnumerateResult enumResult =
                await _enumerator.EnumerateGameObjectAddressesAsync(limit, ct).ConfigureAwait(true);
            if (!enumResult.Ok)
            {
                ErrorMessage  = enumResult.Error;
                StatusMessage = "Enumeration failed — see error above.";
                return;
            }

            // Decode names for the addresses we just got. Cap at the
            // user's Limit (already enforced by EnumerateAsync), then
            // filter to substring matches before adding to the tree.
            // Names that fail to decode (destroyed wrappers / dead
            // pointers) get an empty string and are filtered out unless
            // the user's pattern is empty.
            int filtered = 0;
            int scanned  = 0;
            foreach (string addr in enumResult.Addresses)
            {
                ct.ThrowIfCancellationRequested();
                ++scanned;
                string? name = await _enumerator.GetNameAsync(addr, ct).ConfigureAwait(true);
                string nameOrPlaceholder = name ?? string.Empty;

                if (!string.IsNullOrEmpty(pattern)
                    && nameOrPlaceholder.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                if (string.IsNullOrEmpty(nameOrPlaceholder) && !string.IsNullOrEmpty(pattern))
                {
                    // Pattern requested but the GameObject's wrapper is dead — skip.
                    continue;
                }

                Roots.Add(new SceneNode(_enumerator, _logger, addr,
                                       string.IsNullOrEmpty(nameOrPlaceholder) ? "<no name>" : nameOrPlaceholder));
                ++filtered;
            }

            sw.Stop();
            StatusMessage = string.IsNullOrEmpty(pattern)
                ? $"{filtered} GameObject(s) shown of {enumResult.TotalInScene} in scene ({sw.ElapsedMilliseconds} ms; via {enumResult.UsedApiNote})."
                : $"{filtered} match(es) for \"{pattern}\" of {scanned} scanned ({enumResult.TotalInScene} in scene, {sw.ElapsedMilliseconds} ms; via {enumResult.UsedApiNote}).";
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage  = $"{ex.GetType().Name}: {ex.Message}";
            StatusMessage = "Search failed.";
            _logger.Write(Constants.LogCategoryView,
                $"SceneExplorer: search '{Pattern}' failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// One-shot button: call <c>Application.set_runInBackground(true)</c>
    /// on the target via the Phase 20-MT main-thread dispatcher. Removes
    /// the engine-pause-on-focus-loss behaviour for the rest of the
    /// session so subsequent mainThread RPCs (Scene tab, MethodCallWindow,
    /// FieldWriteWindow, etc.) work without needing the game focused.
    ///
    /// Reuses the same Focus delay countdown as Search so the first call
    /// itself doesn't time out — without the delay the user would be
    /// stuck in a chicken-and-egg loop where they can't enable
    /// runInBackground because runInBackground is off.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEnableRunInBackground))]
    public async Task EnableRunInBackgroundAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsBusy       = true;
        ErrorMessage = null;
        try
        {
            int delay = Math.Clamp(FocusDelaySeconds, 0, 10);
            for (int s = delay; s > 0; --s)
            {
                ct.ThrowIfCancellationRequested();
                StatusMessage = $"Focus the game window — enabling runInBackground in {s}s…";
                await Task.Delay(1000, ct).ConfigureAwait(true);
            }
            StatusMessage = "Calling Application.set_runInBackground(true)…";

            SetRunInBackgroundResult r =
                await _enumerator.SetApplicationRunInBackgroundAsync(true, ct).ConfigureAwait(true);
            if (r.Ok)
            {
                RunInBackgroundEnabled = true;
                StatusMessage =
                    $"Application.runInBackground = true (via {r.ResolvedAssembly}). "
                    + "Subsequent main-thread RPCs no longer need the game window focused.";
                _logger.Write(Constants.LogCategoryView,
                    $"SceneExplorer: runInBackground enabled via {r.ResolvedAssembly}");
            }
            else if (r.SetterUnavailable)
            {
                // Stripped setter — doomed on this build. Grey out the button
                // and point the user at the focus workaround instead.
                RunInBackgroundUnavailable = true;
                ErrorMessage  = SetRunInBackgroundResult.SetterStrippedHint;
                StatusMessage = "runInBackground not available on this build.";
                _logger.Write(Constants.LogCategoryView,
                    "SceneExplorer: runInBackground unavailable — Application.set_runInBackground "
                    + "stripped from the build (method_not_found)");
            }
            else
            {
                ErrorMessage = $"Failed to enable runInBackground: {r.Error}";
                StatusMessage = "runInBackground toggle failed.";
            }
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage  = $"{ex.GetType().Name}: {ex.Message}";
            StatusMessage = "runInBackground toggle failed.";
            _logger.Write(Constants.LogCategoryView,
                $"SceneExplorer: runInBackground failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanEnableRunInBackground() =>
        !IsBusy && !RunInBackgroundEnabled && !RunInBackgroundUnavailable;

    private bool CanSearch() => !IsBusy;

    /// <summary>Wired from the view — Inspect context menu / double-tap.</summary>
    public void NavigateToInspector(SceneNode node)
    {
        if (string.IsNullOrEmpty(node.Address)) return;
        NavigateToInspectorRequested?.Invoke(node.Address);
    }
}

/// <summary>
/// One node in the Scene Explorer tree.
///
/// Top-level nodes come from the filter-driven enumeration. Children
/// are lazy-loaded on first expand (the TreeView's chevron is wired to
/// IsExpanded; the partial OnIsExpandedChanged hook kicks off the async
/// load on the first true transition).
///
/// To make the chevron actually appear before we know whether the node
/// has children, every "loadable" node seeds a single dummy child at
/// construction. Expanding triggers a load that replaces the dummy with
/// the real children — or clears it back to empty if there are none.
/// Dummies themselves use the explicit "leaf" constructor so they don't
/// recursively spawn more dummies.
/// </summary>
public sealed partial class SceneNode : ObservableObject
{
    private readonly SceneEnumerator? _enumerator;
    private readonly FileLogger?      _logger;
    private bool                      _childrenLoaded;
    private readonly bool             _isLeaf;

    /// <summary>Loadable node — seeds a dummy child so the chevron shows.</summary>
    public SceneNode(SceneEnumerator enumerator, FileLogger logger, string address, string name)
    {
        _enumerator = enumerator;
        _logger     = logger;
        _isLeaf     = false;
        Address     = address;
        Name        = name;
        Children    = new ObservableCollection<SceneNode> { CreateDummy("…") };
    }

    /// <summary>Leaf / message node — no async load, no chevron once expanded.</summary>
    private SceneNode(string address, string name)
    {
        _enumerator = null;
        _logger     = null;
        _isLeaf     = true;
        Address     = address;
        Name        = name;
        Children    = new ObservableCollection<SceneNode>();
    }

    private static SceneNode CreateDummy(string label) => new(string.Empty, label);

    public string Address { get; }
    public string Name    { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<SceneNode> Children { get; }

    /// <summary>Row label shown in the TreeView.</summary>
    public string DisplayLabel =>
        string.IsNullOrEmpty(Address) ? Name : $"{Name}  ({Address})";

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _childrenLoaded || _isLeaf) return;
        // Fire-and-forget — UI binding can't await. Errors surface as
        // a synthesised "<load failed…>" leaf inside Children.
        _ = LoadChildrenAsync();
    }

    private async Task LoadChildrenAsync()
    {
        if (_childrenLoaded || _enumerator is null) return;
        _childrenLoaded = true;

        // Swap the "…" dummy for a "Loading…" placeholder while the
        // async getChildren is in flight.
        Children.Clear();
        Children.Add(CreateDummy("Loading…"));

        try
        {
            IReadOnlyList<GameObjectRef> kids =
                await _enumerator.GetChildrenAsync(Address).ConfigureAwait(true);

            Children.Clear();
            foreach (var k in kids)
            {
                Children.Add(new SceneNode(_enumerator, _logger!, k.Address,
                                           string.IsNullOrEmpty(k.Name) ? "<no name>" : k.Name));
            }
            // Empty `Children` ⇒ no chevron next time. Avalonia TreeView
            // auto-hides the expander when ItemsSource is empty.
        }
        catch (Exception ex)
        {
            _logger?.Write(Constants.LogCategoryView,
                $"SceneNode.LoadChildrenAsync({Address}) failed: {ex.GetType().Name}: {ex.Message}");
            Children.Clear();
            Children.Add(CreateDummy($"<load failed: {ex.Message}>"));
        }
    }
}
