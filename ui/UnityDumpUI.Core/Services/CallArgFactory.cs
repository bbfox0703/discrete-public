// CallArgFactory.cs — Phase 19d helper for building CallArgRequest wire
// payloads from typed C# values.
//
// ProtocolJsonContext is internal (the source-gen registrations stay private
// to Core). ViewModels in the outer UnityDumpUI project need a public seam
// to build callMethod request args without leaking the JsonContext type.
// This factory wraps the AOT-safe JsonSerializer.SerializeToElement calls
// against the right per-primitive context entry, one method per wire `type`
// tag from docs/lua-invoke.md.

using System;
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

    /// <summary>
    /// Value-type-by-value arg (Vector3 / Quaternion / user struct). Wire
    /// `type: "value-struct"`, <c>bytes</c> = the struct's raw little-endian
    /// memory image as a hex string; the DLL points <c>params[i]</c> at these
    /// bytes (value-type marshaling convention). The caller must pack the
    /// exact managed layout — see <see cref="Vector3"/> / <see cref="Quaternion"/>.
    /// </summary>
    public static CallArgRequest ValueStruct(ReadOnlySpan<byte> bytes) => new()
    {
        Type = "value-struct",
        // The DLL ignores `value` for value-struct args, but a default
        // (Undefined) JsonElement can't be serialized — send JSON null.
        Value = JsonSerializer.SerializeToElement<string?>(null,
            ProtocolJsonContext.Default.String),
        Bytes = Convert.ToHexString(bytes).ToLowerInvariant(),
    };

    /// <summary>UnityEngine.Vector3 arg — 3 contiguous little-endian floats (12 bytes).</summary>
    public static CallArgRequest Vector3(float x, float y, float z)
    {
        Span<byte> b = stackalloc byte[12];
        BitConverter.TryWriteBytes(b.Slice(0, 4), x);
        BitConverter.TryWriteBytes(b.Slice(4, 4), y);
        BitConverter.TryWriteBytes(b.Slice(8, 4), z);
        return ValueStruct(b);
    }

    /// <summary>UnityEngine.Quaternion arg — 4 contiguous little-endian floats
    /// (16 bytes, x/y/z/w order).</summary>
    public static CallArgRequest Quaternion(float x, float y, float z, float w)
    {
        Span<byte> b = stackalloc byte[16];
        BitConverter.TryWriteBytes(b.Slice(0, 4),  x);
        BitConverter.TryWriteBytes(b.Slice(4, 4),  y);
        BitConverter.TryWriteBytes(b.Slice(8, 4),  z);
        BitConverter.TryWriteBytes(b.Slice(12, 4), w);
        return ValueStruct(b);
    }

    /// <summary>
    /// Decode the little-endian float components of a value-struct return
    /// (<see cref="CallMethodResult.ReturnStructBytes"/>). Returns exactly
    /// <paramref name="count"/> floats; missing bytes default to 0, extra
    /// bytes are ignored. Bad / empty hex yields all-zero (never throws).
    /// </summary>
    public static float[] DecodeStructFloats(string? hex, int count)
    {
        var result = new float[count];
        if (string.IsNullOrEmpty(hex)) return result;
        byte[] bytes;
        try { bytes = Convert.FromHexString(hex); }
        catch (FormatException) { return result; }
        for (int i = 0; i < count && (i * 4) + 4 <= bytes.Length; i++)
        {
            result[i] = BitConverter.ToSingle(bytes, i * 4);
        }
        return result;
    }
}
