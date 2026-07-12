// SetFieldValueParser.cs — parse a single user string into the typed wire value
// for setInstanceField, driven by an InspectorWriteTypeMap FieldWriteTypeInfo.
//
// The Inspector's FieldWriteViewModel uses per-editor input controls (a
// NumericUpDown, a bool box, …); the class-wide Freeze feature has a single
// "freeze at" textbox, so it needs a one-string parser. Same SetFieldValueFactory
// output as the Inspector write path, so a frozen write is byte-identical to a
// one-shot write of the same value.

using System;
using System.Globalization;
using System.Text.Json;

namespace UnityDumpUI.Core.Services;

/// <summary>Parses a single string into a typed setInstanceField wire value.</summary>
public static class SetFieldValueParser
{
    /// <summary>
    /// Parse <paramref name="input"/> per <paramref name="info"/>'s editor kind
    /// into a <see cref="JsonElement"/> for <c>setInstanceField</c>. Returns false
    /// with <paramref name="error"/> set on an unparseable / unsupported value.
    /// </summary>
    public static bool TryParse(FieldWriteTypeInfo info, string? input,
                                out JsonElement value, out string? error)
    {
        value = default;
        error = null;
        if (!info.Supported)
        {
            error = "field type is not writable";
            return false;
        }

        string s = (input ?? string.Empty).Trim();
        switch (info.EditorKind)
        {
            case FieldWriteEditorKind.Bool:
                if (bool.TryParse(s, out bool b)) { value = SetFieldValueFactory.Bool(b); return true; }
                if (s == "1") { value = SetFieldValueFactory.Bool(true);  return true; }
                if (s == "0") { value = SetFieldValueFactory.Bool(false); return true; }
                error = $"'{s}' is not a bool";
                return false;

            case FieldWriteEditorKind.Integer:
                if (info.IsUnsigned)
                {
                    if (!ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong u))
                    {
                        error = $"'{s}' is not an unsigned integer";
                        return false;
                    }
                    value = SetFieldValueFactory.UInt(u);
                    return true;
                }
                if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                {
                    error = $"'{s}' is not an integer";
                    return false;
                }
                value = SetFieldValueFactory.Int(v);
                return true;

            case FieldWriteEditorKind.Float:
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    error = $"'{s}' is not a number";
                    return false;
                }
                value = SetFieldValueFactory.Float(d);
                return true;

            case FieldWriteEditorKind.Address:
            {
                string h = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
                if (!ulong.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong addr))
                {
                    error = $"'{s}' is not a hex address";
                    return false;
                }
                value = SetFieldValueFactory.Address(addr);
                return true;
            }

            case FieldWriteEditorKind.Char:
                if (s.Length == 0) { error = "char cannot be empty"; return false; }
                value = SetFieldValueFactory.Char(s[0]);
                return true;

            default:
                error = $"unsupported editor kind {info.EditorKind}";
                return false;
        }
    }
}
