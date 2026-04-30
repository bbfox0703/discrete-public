// ProtocolModels.cs — typed DTOs for the pipe protocol.
//
// Mirror of `docs/pipe-protocol.md`. Every `result` payload the DLL emits
// for a command we call gets a class here. JSON property names are the
// wire field names exactly (snake_case-to-camelCase mismatches caused the
// first-pass serializer to silently drop fields).

using System.Text.Json.Serialization;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Top-level pipe envelope. Exactly one of <see cref="Result"/> or
/// <see cref="Error"/> is present on the wire; both are nullable so the
/// source-gen serializer can reuse the same shape for both.
/// </summary>
public sealed class RpcEnvelope<TResult> where TResult : class
{
    [JsonPropertyName("id")]      public int       Id     { get; set; }
    [JsonPropertyName("result")]  public TResult?  Result { get; set; }
    [JsonPropertyName("error")]   public RpcError? Error  { get; set; }
}

public sealed class RpcError
{
    [JsonPropertyName("code")]    public int    Code    { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

/// <summary>
/// `handshake` / `scan` result. See pipe-protocol.md §Connection commands.
/// </summary>
public sealed class HandshakeResult
{
    [JsonPropertyName("engine")]            public string? Engine          { get; set; }
    [JsonPropertyName("backend")]           public string? Backend         { get; set; }
    [JsonPropertyName("dll_variant")]       public string? DllVariant      { get; set; }
    [JsonPropertyName("game_module")]       public string? GameModule      { get; set; }
    [JsonPropertyName("unity_version")]     public string? UnityVersion    { get; set; }
    [JsonPropertyName("metadata_version")]  public int?    MetadataVersion { get; set; }
    [JsonPropertyName("assemblies_count")]  public int     AssembliesCount { get; set; }
    [JsonPropertyName("build")]             public uint    Build           { get; set; }
    [JsonPropertyName("protocol")]          public int     Protocol        { get; set; }
}

/// <summary>
/// `ping` result.
/// </summary>
public sealed class PingResult
{
    [JsonPropertyName("pong")] public bool Pong { get; set; }
}
