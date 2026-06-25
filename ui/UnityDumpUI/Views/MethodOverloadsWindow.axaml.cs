// MethodOverloadsWindow.axaml.cs — Phase 18c modal dialog code-behind.
//
// Wires the VM's clipboard delegate to Avalonia's top-level Clipboard, kicks
// off the initial findMethodInstances pass when the window opens, and (Phase
// 19d) opens the MethodCallWindow when the user picks "Call method…" from
// the per-row context menu.

using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UnityDumpUI.Core.Services;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class MethodOverloadsWindow : Window
{
    private BackendAdapter? _backend;
    private FileLogger?     _logger;

    public MethodOverloadsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DialogWindowGeometry.Attach(this);
    }

    public MethodOverloadsWindow(MethodOverloadsViewModel vm,
                                 BackendAdapter backend,
                                 FileLogger logger) : this()
    {
        _backend = backend;
        _logger  = logger;

        DataContext = vm;
        vm.CopyToClipboardAsync       = CopyToClipboardAsync;
        vm.OpenCallMethodDialogAsync  = OpenCallMethodDialogAsync;
        // Kick off the search as soon as the window is shown so the grid
        // populates without an extra click. Errors land in vm.ErrorMessage.
        Opened += async (_, _) =>
        {
            // AOT-safe DataGrid sort: install per-column comparers once the
            // grid is realised (see DataGridSortComparers three-layer trap).
            this.FindControl<DataGrid>("OverloadsGrid")?.WireSortComparers(OverloadsSortComparers);
            try { await vm.RefreshAsync(); }
            catch { /* surfaced via VM */ }
        };
    }

    /// <summary>Per-column sort comparers for the overloads grid. Address sorts
    /// numerically (hex); the rest are short ordinal-string labels.</summary>
    private static readonly IReadOnlyDictionary<string, IComparer> OverloadsSortComparers =
        new Dictionary<string, IComparer>
        {
            ["SignatureLabel"] = DataGridSortComparers.Ordinal<MethodOverloadRow>(r => r.SignatureLabel),
            ["ReturnType"]     = DataGridSortComparers.Ordinal<MethodOverloadRow>(r => r.ReturnType ?? string.Empty),
            ["Address"]        = DataGridSortComparers.Hex<MethodOverloadRow>(r => r.Address ?? string.Empty),
            ["HumanSuffix"]    = DataGridSortComparers.Ordinal<MethodOverloadRow>(r => r.HumanSuffix ?? string.Empty),
            ["ModifiersLabel"] = DataGridSortComparers.Ordinal<MethodOverloadRow>(r => r.ModifiersLabel),
        };

    /// <summary>
    /// Phase 19d — open the MethodCallWindow seeded from the right-clicked
    /// row. Constructs a MethodCallViewModel from the row's signature info
    /// (paramTypes / paramNames / returnType / isStatic) and shows it as a
    /// modal owned by this window.
    /// </summary>
    private async Task OpenCallMethodDialogAsync(MethodOverloadRow row)
    {
        if (DataContext is not MethodOverloadsViewModel parentVm) return;
        if (_backend is null || _logger is null) return;

        // Pin the signature to the picked overload's exact signature string,
        // so the DLL resolves the same overload the user just clicked even
        // when other overloads share the method name. Empty signature ()
        // matches every nullary overload — defaulting to "()" is fine here
        // because the DLL would then reject as ambiguous if more than one
        // exists, prompting the user to refine.
        string sigMatch = string.IsNullOrEmpty(row.Signature) ? "()" : row.Signature;

        // Phase 19d-2: pre-fill the Instance address textbox from
        // DefaultInstanceAddress (typically the Inspector's currently-
        // selected instance, threaded in by MainWindow.axaml.cs when the
        // browser's selected class matches the inspector's target). Static
        // methods ignore the field at the DLL layer, so harmlessly seeding
        // it for static overloads is fine — the textbox is also dimmed
        // unused via the existing IsStatic styling.
        var callVm = new MethodCallViewModel(
            _backend, _logger,
            parentVm.Assembly, parentVm.Namespace, parentVm.ClassName, parentVm.MethodName,
            signatureMatch:   sigMatch,
            signatureDisplay: row.SignatureLabel,
            paramTypes:       row.ParamTypes is System.Collections.Generic.List<string> lt
                                ? lt
                                : new System.Collections.Generic.List<string>(row.ParamTypes),
            paramNames:       row.ParamNames is System.Collections.Generic.List<string> ln
                                ? ln
                                : new System.Collections.Generic.List<string>(row.ParamNames),
            returnType:       row.ReturnType ?? "System.Void",
            isStatic:         row.IsStatic,
            defaultInstanceAddress: parentVm.DefaultInstanceAddress);
        // Phase 22e: thread the same AOBMaker bridge through so the
        // MethodCallWindow's "Send to CE Table" button reaches the
        // session-wide instance. Same-pipe-client lifetime as the
        // overloads window's own Create-AA command.
        callVm.AobMaker = parentVm.AobMaker;

        var win = new MethodCallWindow(callVm);
        await win.ShowDialog(this);
    }

    private async Task CopyToClipboardAsync(string text)
    {
        // Avalonia 12: same clipboard pattern MainWindow uses — see the
        // detailed comment there. IClipboard.SetDataAsync(IAsyncDataTransfer).
        var clipboard = Clipboard;
        if (clipboard is null) return;
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, text);
        var transfer = new DataTransfer();
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
