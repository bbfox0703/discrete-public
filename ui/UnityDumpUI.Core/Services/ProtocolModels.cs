// ProtocolModels.cs — typed DTOs for the pipe protocol.
//
// Mirror of `docs/pipe-protocol.md`. Every `result` payload the DLL emits
// for a command we call gets a class here. JSON property names are the
// wire field names exactly (snake_case-to-camelCase mismatches caused the
// first-pass serializer to silently drop fields).

using System.Collections.Generic;
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

// ---------------------------------------------------------------------------
// Phase 8 — assembly / class introspection.
//
// These mirror docs/pipe-protocol.md §"Assembly / class introspection". The
// DLL response shape is identical between Mono and IL2CPP except where noted
// (Mono adds `token` to class entries; IL2CPP fills `address` on methods).
// ---------------------------------------------------------------------------

/// <summary>
/// `getAssemblies` result.
/// </summary>
public sealed class GetAssembliesResult
{
    [JsonPropertyName("count")]      public int                       Count      { get; set; }
    [JsonPropertyName("assemblies")] public List<AssemblyEntry>       Assemblies { get; set; } = new();
}

public sealed class AssemblyEntry
{
    [JsonPropertyName("name")] public string  Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string? Path { get; set; }
}

/// <summary>
/// `getClasses` result.
/// </summary>
public sealed class GetClassesResult
{
    [JsonPropertyName("assembly")] public string?           Assembly { get; set; }
    [JsonPropertyName("count")]    public int               Count    { get; set; }
    [JsonPropertyName("classes")]  public List<ClassEntry>  Classes  { get; set; } = new();
    [JsonPropertyName("error")]    public string?           Error    { get; set; }
}

public sealed class ClassEntry
{
    [JsonPropertyName("namespace")] public string? Namespace { get; set; }
    [JsonPropertyName("name")]      public string  Name      { get; set; } = string.Empty;
    // Mono only.
    [JsonPropertyName("token")]     public uint?   Token     { get; set; }
}

/// <summary>
/// `getClassInfo` result.
/// </summary>
public sealed class GetClassInfoResult
{
    [JsonPropertyName("namespace")] public string?                 Namespace { get; set; }
    [JsonPropertyName("name")]      public string?                 Name      { get; set; }
    [JsonPropertyName("parent")]    public ClassRef?               Parent    { get; set; }
    [JsonPropertyName("fields")]    public List<FieldEntry>?       Fields    { get; set; }
    [JsonPropertyName("methods")]   public List<MethodEntry>?      Methods   { get; set; }
    [JsonPropertyName("error")]     public string?                 Error     { get; set; }
}

public sealed class ClassRef
{
    [JsonPropertyName("namespace")] public string? Namespace { get; set; }
    [JsonPropertyName("name")]      public string? Name      { get; set; }
}

public sealed class FieldEntry
{
    [JsonPropertyName("name")]   public string  Name   { get; set; } = string.Empty;
    [JsonPropertyName("type")]   public string? Type   { get; set; }
    [JsonPropertyName("offset")] public int     Offset { get; set; }
    [JsonPropertyName("flags")]  public uint    Flags  { get; set; }
    [JsonPropertyName("static")] public bool    Static { get; set; }
}

public sealed class MethodEntry
{
    [JsonPropertyName("name")]    public string  Name    { get; set; } = string.Empty;
    [JsonPropertyName("token")]   public uint    Token   { get; set; }
    [JsonPropertyName("flags")]   public uint    Flags   { get; set; }
    [JsonPropertyName("static")]  public bool    Static  { get; set; }
    // IL2CPP only — Mono path returns the JIT entry from monoCompileMethod on demand.
    [JsonPropertyName("address")] public string? Address { get; set; }
}
