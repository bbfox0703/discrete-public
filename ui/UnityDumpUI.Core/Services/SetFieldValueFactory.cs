// SetFieldValueFactory.cs — Phase 21a helper for building setInstanceField
// `value` payloads from typed C# values.
//
// Same pattern as CallArgFactory: each method returns a JsonElement
// serialized through the source-gen ProtocolJsonContext (AOT-safe — no
// reflection). ViewModels in the outer UnityDumpUI project need a public
// seam to build setInstanceField request values without leaking the
// internal JsonContext type.

using System.Globalization;
using System.Text.Json;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Static helpers that produce typed JSON values for the
/// <see cref="BackendAdapter.SetInstanceFieldAsync"/> pipe RPC. All methods
/// are AOT-safe: each call goes through the source-gen
/// <see cref="ProtocolJsonContext"/> entries, never the reflection-based
/// <c>JsonSerializer.SerializeToElement&lt;T&gt;</c> overload.
/// </summary>
public static class SetFieldValueFactory
{
    /// <summary>Wire value for `bool`. Serialized as JSON true/false.</summary>
    public static JsonElement Bool(bool value)
        => JsonSerializer.SerializeToElement(value, ProtocolJsonContext.Default.Boolean);

    /// <summary>
    /// Wire value for any signed integer width. Caller passes the desired
    /// width as <paramref name="value"/>; the DLL clamps to the formal
    /// type's range and rejects out-of-range with a soft error.
    /// </summary>
    public static JsonElement Int(long value)
        => JsonSerializer.SerializeToElement(value, ProtocolJsonContext.Default.Int64);

    /// <summary>
    /// Wire value for any unsigned integer width up to
    /// <see cref="long.MaxValue"/>. Values above are not representable on
    /// the wire today (no UInt64 context entry); use
    /// <see cref="Address(string)"/> for raw 64-bit bit patterns.
    /// </summary>
    public static JsonElement UInt(ulong value)
        => JsonSerializer.SerializeToElement((long)value, ProtocolJsonContext.Default.Int64);

    /// <summary>Wire value for `float` or `double`. Serialized as JSON number.</summary>
    public static JsonElement Float(double value)
        => JsonSerializer.SerializeToElement(value, ProtocolJsonContext.Default.Double);

    /// <summary>
    /// Wire value for `address` / `IntPtr` / `UIntPtr`. The DLL's
    /// <c>parseAddr</c> accepts both `"0xDEAD"` and bare-hex `"DEAD"`; we
    /// emit a leading `0x` for human readability in the request line.
    /// </summary>
    public static JsonElement Address(string hex)
    {
        string s = (hex ?? string.Empty).Trim();
        if (!s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("0X", StringComparison.Ordinal))
        {
            s = "0x" + s;
        }
        return JsonSerializer.SerializeToElement(s, ProtocolJsonContext.Default.String);
    }

    /// <summary>
    /// Wire value for <c>address</c> from a raw <see cref="ulong"/> bit
    /// pattern. Formatted as <c>0x...</c> uppercase hex (matches the
    /// <c>formatHex</c> convention used elsewhere on the wire).
    /// </summary>
    public static JsonElement Address(ulong value)
        => Address("0x" + value.ToString("X", CultureInfo.InvariantCulture));

    /// <summary>
    /// Wire value for `char`. The DLL accepts either a single-char string
    /// (uses its first code unit) or a numeric code point — we send a
    /// number here so non-BMP / control characters round-trip cleanly.
    /// </summary>
    public static JsonElement Char(char value)
        => JsonSerializer.SerializeToElement((long)value, ProtocolJsonContext.Default.Int64);
}
