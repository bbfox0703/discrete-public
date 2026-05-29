// RawJsonStringConverter.cs — Phase 30a-2 (value compaction).
//
// Reads any JSON value token into its raw JSON TEXT (a small string) and writes
// that text back verbatim. Used on the snapshot value properties so the
// in-memory model retains a `string` per value instead of a `JsonElement`.
//
// Why this matters: System.Text.Json deserializes every `JsonElement` property
// into its OWN standalone `JsonDocument`. At snapshot scale (millions of
// instance × field values) that is millions of tiny JsonDocuments — the
// dominant UI retained cost (29g-0b heap analysis + Phase 30 design). Storing
// the raw text string instead drops them; callers that still want a JsonElement
// get one via a computed property that parses on demand (transient, GC'd).
//
// AOT-safe (no reflection); usable from the source-gen JsonSerializerContext via
// a property-level [JsonConverter]. The owning context's WhenWritingNull policy
// omits a null backing string, so a value-less field serializes exactly as
// before (no "value" key).

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityDumpUI.Core.Services;

public sealed class RawJsonStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // STJ handles a bare JSON null itself (HandleNull defaults to false), so
        // this is reached only for present, non-null value tokens. ParseValue
        // consumes exactly the current value (scalar / object / array); the
        // owning JsonDocument is disposed here, leaving only the raw text.
        if (reader.TokenType == JsonTokenType.Null) return null;
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else                writer.WriteRawValue(value);
    }
}
