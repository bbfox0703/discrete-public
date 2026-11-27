// ValueScanWireEncoder.cs — C-M8 lossless numeric encoding for value / group scans.
//
// Previously the UI parsed every scan input as `double`, silently losing low
// bits on Int64 / UInt64 values > 2^53 (e.g. DateTime.Ticks, Steam IDs) and
// silently truncating fractional numbers (e.g. "3.7") on integer scans.
//
// This helper parses integer widths directly as `long` / `ulong` (with exact
// bit-width range checks and optional `0x` hex prefix) and serializes them via
// ProtocolJsonContext.Default.Int64 / UInt64 so the full 64-bit value crosses
// the pipe losslessly as a JSON integer. Floating-point widths serialize as
// JSON double (rejecting NaN / Infinity). All calls are Native AOT safe.

using System;
using System.Globalization;
using System.Text.Json;

namespace UnityDumpUI.Core.Services;

public static class ValueScanWireEncoder
{
    public static bool IsIntegerDataType(string? dataType) =>
        Normalize(dataType) is "int8" or "uint8" or "int16" or "uint16"
                            or "int32" or "uint32" or "int64" or "uint64";

    public static bool IsFloatDataType(string? dataType) =>
        Normalize(dataType) is "float" or "double";

    /// <summary>
    /// Parse <paramref name="rawText"/> according to <paramref name="dataType"/>
    /// and encode it into a Native-AOT-safe <see cref="JsonElement"/> without
    /// passing 64-bit integers through <see cref="double"/>.
    /// </summary>
    public static bool TryEncodeNumericArg(
        string dataType, string? rawText,
        out JsonElement element, out string error)
    {
        element = default;
        error = string.Empty;

        string s = (rawText ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            error = "Value cannot be empty.";
            return false;
        }

        string norm = Normalize(dataType);
        switch (norm)
        {
            case "int8":
                return TryEncodeSigned(s, sbyte.MinValue, sbyte.MaxValue, norm, out element, out error);
            case "int16":
                return TryEncodeSigned(s, short.MinValue, short.MaxValue, norm, out element, out error);
            case "int32":
                return TryEncodeSigned(s, int.MinValue, int.MaxValue, norm, out element, out error);
            case "int64":
                return TryEncodeSigned(s, long.MinValue, long.MaxValue, norm, out element, out error);

            case "uint8":
                return TryEncodeUnsigned(s, byte.MaxValue, norm, out element, out error);
            case "uint16":
                return TryEncodeUnsigned(s, ushort.MaxValue, norm, out element, out error);
            case "uint32":
                return TryEncodeUnsigned(s, uint.MaxValue, norm, out element, out error);
            case "uint64":
                return TryEncodeUnsigned(s, ulong.MaxValue, norm, out element, out error);

            case "float":
            case "double":
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv)
                    || !double.IsFinite(dv))
                {
                    error = $"Value '{s}' is not a valid finite {norm} number.";
                    return false;
                }
                if (norm == "float" && !float.IsFinite((float)dv))
                {
                    error = $"Value '{s}' overflows single-precision float.";
                    return false;
                }
                element = JsonSerializer.SerializeToElement(dv, ProtocolJsonContext.Default.Double);
                return true;

            default:
                error = $"Unsupported numeric dataType '{dataType}'.";
                return false;
        }
    }

    private static bool TryEncodeSigned(
        string s, long min, long max, string label,
        out JsonElement element, out string error)
    {
        element = default;
        error = string.Empty;
        if (!TryParseInt64(s, out long v))
        {
            error = $"Value '{s}' is not a valid {label} integer.";
            return false;
        }
        if (v < min || v > max)
        {
            error = $"Value '{s}' is out of range for {label} [{min}, {max}].";
            return false;
        }
        element = JsonSerializer.SerializeToElement(v, ProtocolJsonContext.Default.Int64);
        return true;
    }

    private static bool TryEncodeUnsigned(
        string s, ulong max, string label,
        out JsonElement element, out string error)
    {
        element = default;
        error = string.Empty;
        if (!TryParseUInt64(s, out ulong v))
        {
            error = $"Value '{s}' is not a valid unsigned {label} integer.";
            return false;
        }
        if (v > max)
        {
            error = $"Value '{s}' is out of range for {label} [0, {max}].";
            return false;
        }
        element = JsonSerializer.SerializeToElement(v, ProtocolJsonContext.Default.UInt64);
        return true;
    }

    private static bool TryParseInt64(string s, out long value)
    {
        value = 0;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            string hex = s.Substring(2);
            if (hex.Length == 0 || !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong uv))
                return false;
            if (uv > (ulong)long.MaxValue) return false;
            value = (long)uv;
            return true;
        }
        if (s.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
        {
            string hex = s.Substring(3);
            if (hex.Length == 0 || !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong mag))
                return false;
            const ulong absMin = (ulong)long.MaxValue + 1UL;
            if (mag > absMin) return false;
            value = mag == absMin ? long.MinValue : -(long)mag;
            return true;
        }
        return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseUInt64(string s, out ulong value)
    {
        value = 0;
        if (s.StartsWith("-", StringComparison.Ordinal)) return false;
        if (s.StartsWith("+", StringComparison.Ordinal)) s = s.Substring(1);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            string hex = s.Substring(2);
            return hex.Length > 0 && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
        return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static string Normalize(string? dt) => (dt ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "int8" or "sbyte" or "system.sbyte"   => "int8",
        "uint8" or "byte" or "system.byte"    => "uint8",
        "int16" or "short" or "system.int16"  => "int16",
        "uint16" or "ushort" or "system.uint16" => "uint16",
        "int32" or "int" or "system.int32"    => "int32",
        "uint32" or "uint" or "system.uint32" => "uint32",
        "int64" or "long" or "system.int64"   => "int64",
        "uint64" or "ulong" or "system.uint64" => "uint64",
        "float" or "single" or "system.single" => "float",
        "double" or "system.double"           => "double",
        _                                     => string.Empty,
    };
}
