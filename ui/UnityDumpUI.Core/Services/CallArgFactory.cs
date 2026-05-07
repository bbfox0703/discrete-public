// CallArgFactory.cs — Phase 19d helper for building CallArgRequest wire
// payloads from typed C# values.
//
// ProtocolJsonContext is internal (the source-gen registrations stay private
// to Core). ViewModels in the outer UnityDumpUI project need a public seam
// to build callMethod request args without leaking the JsonContext type.
// This factory wraps the AOT-safe JsonSerializer.SerializeToElement calls
// against the right per-primitive context entry, one method per wire `type`
// tag from docs/lua-invoke.md.

using System.Text.Json;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Static helpers that produce typed <see cref="CallArgRequest"/> instances
/// for the `callMethod` pipe RPC. All methods are AOT-safe: each call goes
/// through the source-gen <see cref="ProtocolJsonContext"/> entries, never
/// the reflection-based <c>JsonSerializer.SerializeToElement&lt;T&gt;</c>
/// overload.
/// </summary>
public static class CallArgFactory
{
    /// <summary>Reference-type null. Wire `type: "null"`.</summary>
    public static CallArgRequest Null() => new()
    {
        Type  = "null",
        Value = JsonSerializer.SerializeToElement<string?>(null,
            ProtocolJsonContext.Default.String),
    };

    /// <summary>Wire `type: "bool"`. Serialized as JSON true/false.</summary>
    public static CallArgRequest Bool(bool value) => new()
    {
        Type  = "bool",
        Value = JsonSerializer.SerializeToElement(value,
            ProtocolJsonContext.Default.Boolean),
    };

    /// <summary>
    /// Signed integer arg. Caller picks the wire type tag (`int8` /
    /// `int16` / `int32` / `int64`) — the DLL truncates to the formal
    /// parameter's actual width.
    /// </summary>
    public static CallArgRequest Int(string typeTag, long value) => new()
    {
        Type  = typeTag,
        Value = JsonSerializer.SerializeToElement(value,
            ProtocolJsonContext.Default.Int64),
    };

    /// <summary>
    /// Unsigned integer arg up to <see cref="long.MaxValue"/>. Caller picks
    /// the wire type tag (`byte` / `uint16` / `uint32` / `uint64`).
    /// Values above <see cref="long.MaxValue"/> would need a UInt64 context
    /// entry — no Unity API in the wild takes one as a useful argument.
    /// </summary>
    public static CallArgRequest UInt(string typeTag, ulong value) => new()
    {
        Type  = typeTag,
        Value = JsonSerializer.SerializeToElement((long)value,
            ProtocolJsonContext.Default.Int64),
    };

    /// <summary>Wire `type: "float"` or `"double"`. Serialized as JSON number.</summary>
    public static CallArgRequest Float(string typeTag, double value) => new()
    {
        Type  = typeTag,
        Value = JsonSerializer.SerializeToElement(value,
            ProtocolJsonContext.Default.Double),
    };

    /// <summary>UTF-8 string arg. Wire `type: "string"`.</summary>
    public static CallArgRequest String(string value) => new()
    {
        Type  = "string",
        Value = JsonSerializer.SerializeToElement(value,
            ProtocolJsonContext.Default.String),
    };

    /// <summary>
    /// Pre-resolved managed pointer / IntPtr. Wire `type: "address"`. Value
    /// is sent as a hex string (`"0x..."`) — the DLL's <c>parseAddr</c>
    /// also accepts bare-hex.
    /// </summary>
    public static CallArgRequest Address(string hex) => new()
    {
        Type  = "address",
        Value = JsonSerializer.SerializeToElement(hex,
            ProtocolJsonContext.Default.String),
    };
}
