// FieldOffsetJsonConverter.cs — AOT-safe JSON converters for managed field
// offsets returned by the DLL (C-H1 defense in depth).
//
// IL2CPP encodes [ThreadStatic] fields with THREAD_STATIC_FIELD_OFFSET = -1
// inside an unsigned size_t (18446744073709551615 on x64, 4294967295 on x86).
// Newer DLL builds normalize these sentinels to -1 on the wire, and these
// converters normalize any raw unsigned sentinel (> int.MaxValue, including
// uint.MaxValue and ulong.MaxValue) to -1 on the UI side as well so older DLL
// builds or corrupt field entries never throw JsonException during
// getClassInfo / dumpClassInfo / searchSymbols / whatIsAt deserialization.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityDumpUI.Core.Services;

public sealed class FieldOffsetJsonConverter : JsonConverter<int>
{
    internal static int ReadNormalizedInt32(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException($"Expected numeric field offset, got {reader.TokenType}.");

        if (reader.TryGetInt64(out long signed))
        {
            if (signed > int.MaxValue || signed < int.MinValue)
                return -1;
            return (int)signed;
        }

        if (reader.TryGetUInt64(out ulong unsigned))
            return unsigned > int.MaxValue ? -1 : (int)unsigned;

        throw new JsonException("Field offset JSON number is not a valid integer.");
    }

    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadNormalizedInt32(ref reader);

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

public sealed class NullableFieldOffsetJsonConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        return FieldOffsetJsonConverter.ReadNormalizedInt32(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else               writer.WriteNumberValue(value.Value);
    }
}

public sealed class NullableLongFieldOffsetJsonConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException($"Expected numeric field offset, got {reader.TokenType}.");

        if (reader.TryGetInt64(out long signed))
            return signed > int.MaxValue ? -1L : signed;

        if (reader.TryGetUInt64(out ulong unsigned))
            return unsigned > int.MaxValue ? -1L : (long)unsigned;

        throw new JsonException("Field offset JSON number is not a valid integer.");
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else               writer.WriteNumberValue(value.Value);
    }
}
