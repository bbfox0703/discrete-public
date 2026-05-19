// FieldWriteViewModel.cs — Phase 21b.
//
// Drives the "Write value…" modal that lets the user edit a primitive
// Inspector field's storage in the host process. One row per call (single
// field, single value), one editor flavor (NumericUpDown / Bool /
// Float-textbox / Address / Char) picked via InspectorWriteTypeMap based on
// the CLR type the inspector reported.
//
// Wire path: BackendAdapter.SetInstanceFieldAsync — see
// `docs/pipe-protocol.md` §"setInstanceField". This VM owns the editor
// state, the WriteCommand, the status text, and the OnWritten callback
// the parent inspector hooks to refresh the row after a successful write.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityDumpUI.Core;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.ViewModels;

public sealed partial class FieldWriteViewModel : ObservableObject
{
    private readonly BackendAdapter _backend;
    private readonly FileLogger     _logger;
    private CancellationTokenSource? _cts;

    public FieldWriteViewModel(
        BackendAdapter backend, FileLogger logger,
        string fieldName, string clrTypeName, string declaredIn,
        string fieldAddress, string currentDisplay)
    {
        _backend       = backend;
        _logger        = logger;
        FieldName      = fieldName;
        ClrType        = clrTypeName;
        DeclaredIn     = declaredIn;
        FieldAddress   = fieldAddress;
        CurrentDisplay = currentDisplay;

        TypeInfo = InspectorWriteTypeMap.Resolve(clrTypeName);

        // Seed each editor's value from the current display when we can
        // parse it; otherwise leave the editor's default in place. We
        // tolerate a wide range of stringy inputs (e.g. "999", "True",
        // "0x7FF600000010") because the inspector's display formatter is
        // best-effort and we want the user to land in a sensible state.
        SeedEditor(currentDisplay);

        EditorLabel = TypeInfo.Supported
            ? $"{ClrType} ({TypeInfo.ByteSize} byte{(TypeInfo.ByteSize == 1 ? "" : "s")})"
            : $"{ClrType} — write not supported in v1";
    }

    // ---------- Field metadata (read-only labels) -----------------------

    public string FieldName      { get; }
    public string ClrType        { get; }
    public string DeclaredIn     { get; }
    public string FieldAddress   { get; }
    public string CurrentDisplay { get; }
    public string EditorLabel    { get; }

    public FieldWriteTypeInfo TypeInfo { get; }

    // ---------- Editor visibility (drives one editor at a time) ---------

    public bool IsEditorInteger => TypeInfo.Supported && TypeInfo.EditorKind == FieldWriteEditorKind.Integer;
    public bool IsEditorFloat   => TypeInfo.Supported && TypeInfo.EditorKind == FieldWriteEditorKind.Float;
    public bool IsEditorBool    => TypeInfo.Supported && TypeInfo.EditorKind == FieldWriteEditorKind.Bool;
    public bool IsEditorAddress => TypeInfo.Supported && TypeInfo.EditorKind == FieldWriteEditorKind.Address;
    public bool IsEditorChar    => TypeInfo.Supported && TypeInfo.EditorKind == FieldWriteEditorKind.Char;
    public bool IsUnsupported   => !TypeInfo.Supported;

    /// <summary>
    /// AutoCompleteBox needs ItemsSource on Avalonia 12 (the inline
    /// <c>AutoCompleteBox.Items</c> shape doesn't compile under AOT
    /// XAML). Mirrors Phase 19d-3's MethodCallParamRow.BoolSuggestions.
    /// </summary>
    public string[] BoolSuggestions { get; } = new[] { "true", "false" };

    // NumericUpDown ranges — Mirror Phase 19d-3's MethodCallParamRow.
    public decimal NumericMin => TypeInfo.MinValue;
    public decimal NumericMax => TypeInfo.MaxValue;

    // ---------- Bound editor state --------------------------------------

    [ObservableProperty] private decimal _numericValue;
    [ObservableProperty] private string  _floatText = string.Empty;
    [ObservableProperty] private string  _boolText = "false";
    [ObservableProperty] private string  _addressText = "0x0000000000000000";
    [ObservableProperty] private string  _charText = string.Empty;

    // ---------- Status / outcome ----------------------------------------

    [ObservableProperty] private bool    _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>
    /// Fired after a successful write. The parent inspector hooks this to
    /// trigger a walkInstance refresh so the displayed value updates.
    /// </summary>
    public event EventHandler? Written;

    /// <summary>
    /// Fired when the user clicks Write OR Cancel after a successful write.
    /// The view subscribes to close the modal. Failed writes leave the
    /// modal open so the user can correct the value.
    /// </summary>
    public event EventHandler? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        IsBusy = true;
        StatusMessage = null;
        ErrorMessage = null;
        try
        {
            // Build the wire value via SetFieldValueFactory.
            System.Text.Json.JsonElement value = BuildWireValue(out string? buildError);
            if (buildError is not null)
            {
                ErrorMessage = buildError;
                return;
            }

            SetInstanceFieldResult r = await _backend
                .SetInstanceFieldAsync(FieldAddress, fieldOffset: 0,
                    type: TypeInfo.WireType, value: value, ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            if (!r.Ok || !string.IsNullOrEmpty(r.Error))
            {
                ErrorMessage = string.IsNullOrEmpty(r.Error)
                    ? "setInstanceField returned ok=false"
                    : r.Error;
                _logger.Write(Constants.LogCategoryView,
                    $"setInstanceField {FieldName}@{FieldAddress} {TypeInfo.WireType}: {ErrorMessage}");
                return;
            }
            StatusMessage = string.Create(CultureInfo.InvariantCulture,
                $"Wrote {r.Written} byte{(r.Written == 1 ? "" : "s")} at {r.FieldAddress}.");
            _logger.Write(Constants.LogCategoryView,
                $"setInstanceField {FieldName}@{FieldAddress} {TypeInfo.WireType} = OK");
            Written?.Invoke(this, EventArgs.Empty);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.Write(Constants.LogCategoryView,
                $"setInstanceField {FieldName}@{FieldAddress} failed: {ErrorMessage}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanWrite() => !IsBusy && TypeInfo.Supported;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ---------- Internal helpers ----------------------------------------

    private System.Text.Json.JsonElement BuildWireValue(out string? error)
    {
        error = null;
        switch (TypeInfo.EditorKind)
        {
            case FieldWriteEditorKind.Bool:
            {
                string s = (BoolText ?? string.Empty).Trim();
                if (bool.TryParse(s, out bool b)) return SetFieldValueFactory.Bool(b);
                if (s == "1") return SetFieldValueFactory.Bool(true);
                if (s == "0") return SetFieldValueFactory.Bool(false);
                error = $"'{s}' is not a bool";
                return default;
            }
            case FieldWriteEditorKind.Integer:
            {
                // NumericUpDown gives us a decimal already clamped to [Min, Max].
                // For unsigned we route through ulong; for signed through long.
                if (TypeInfo.IsUnsigned)
                {
                    if (NumericValue < 0m)
                    {
                        error = "value cannot be negative for unsigned type";
                        return default;
                    }
                    ulong u = (ulong)NumericValue;
                    return SetFieldValueFactory.UInt(u);
                }
                long v = (long)NumericValue;
                return SetFieldValueFactory.Int(v);
            }
            case FieldWriteEditorKind.Float:
            {
                if (!double.TryParse(FloatText, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    error = $"'{FloatText}' is not a valid number";
                    return default;
                }
                return SetFieldValueFactory.Float(d);
            }
            case FieldWriteEditorKind.Address:
            {
                string s = (AddressText ?? string.Empty).Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
                if (!ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong u))
                {
                    error = $"'{AddressText}' is not a valid hex address";
                    return default;
                }
                return SetFieldValueFactory.Address(u);
            }
            case FieldWriteEditorKind.Char:
            {
                string s = CharText ?? string.Empty;
                if (s.Length == 0) { error = "char cannot be empty"; return default; }
                return SetFieldValueFactory.Char(s[0]);
            }
            default:
                error = $"unsupported editor kind {TypeInfo.EditorKind}";
                return default;
        }
    }

    private void SeedEditor(string? current)
    {
        string s = (current ?? string.Empty).Trim();
        switch (TypeInfo.EditorKind)
        {
            case FieldWriteEditorKind.Bool:
                BoolText = s.Length > 0 ? s : "false";
                break;
            case FieldWriteEditorKind.Integer:
                if (decimal.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out decimal d))
                {
                    if (d < TypeInfo.MinValue) d = TypeInfo.MinValue;
                    if (d > TypeInfo.MaxValue) d = TypeInfo.MaxValue;
                    NumericValue = d;
                }
                break;
            case FieldWriteEditorKind.Float:
                FloatText = s.Length > 0 ? s : "0";
                break;
            case FieldWriteEditorKind.Address:
                AddressText = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? s : "0x" + s;
                break;
            case FieldWriteEditorKind.Char:
                CharText = s.Length > 0 ? s.Substring(0, 1) : string.Empty;
                break;
        }
    }
}
