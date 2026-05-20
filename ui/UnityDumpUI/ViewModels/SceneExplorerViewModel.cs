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

    public ObservableCollection<SceneNode> Roots { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _pattern = string.Empty;

    [ObservableProperty]
    private int _limit = 200;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
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
