// ProtocolModels.cs — typed DTOs for the pipe protocol.
//
// Mirror of `docs/pipe-protocol.md`. Every `result` payload the DLL emits
// for a command we call gets a class here. JSON property names are the
// wire field names exactly (snake_case-to-camelCase mismatches caused the
// first-pass serializer to silently drop fields).

using System.Collections.Generic;
using System.Text.Json;
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

// ---------------------------------------------------------------------------
// Phase 9 — live inspection (instance discovery, instance walking,
// static field data, raw memory read).
// ---------------------------------------------------------------------------

/// <summary>
/// `findInstances` result. IL2CPP fills <see cref="ClassPtr"/>; Mono fills
/// <see cref="VTable"/>. Both backends share <see cref="Matches"/>.
/// </summary>
public sealed class FindInstancesResult
{
    [JsonPropertyName("class_ptr")] public string?       ClassPtr { get; set; }
    [JsonPropertyName("vtable")]    public string?       VTable   { get; set; }
    [JsonPropertyName("count")]     public int           Count    { get; set; }
    [JsonPropertyName("limit")]     public int           Limit    { get; set; }
    [JsonPropertyName("matches")]   public List<string>  Matches  { get; set; } = new();
    [JsonPropertyName("error")]     public string?       Error    { get; set; }
}

/// <summary>
/// `walkInstance` result. Decodes managed instance fields up the inheritance
/// chain (depth capped at 16, count capped at 4096 by the DLL).
/// </summary>
public sealed class WalkInstanceResult
{
    [JsonPropertyName("address")]   public string?               Address   { get; set; }
    [JsonPropertyName("namespace")] public string?               Namespace { get; set; }
    [JsonPropertyName("name")]      public string?               Name      { get; set; }
    [JsonPropertyName("depth")]     public int                   Depth     { get; set; }
    [JsonPropertyName("count")]     public int                   Count     { get; set; }
    [JsonPropertyName("fields")]    public List<WalkedField>?    Fields    { get; set; }
    [JsonPropertyName("error")]     public string?               Error     { get; set; }
}

/// <summary>
/// One decoded field from <c>walkInstance</c>. Per pipe-protocol.md the per-type
/// payload is heterogeneous (bool / int / number / hex string / decoded UTF-16
/// string / unreadable marker), so <see cref="Value"/> is a raw
/// <see cref="JsonElement"/> the UI inspects at render time.
/// </summary>
public sealed class WalkedField
{
    [JsonPropertyName("name")]          public string       Name         { get; set; } = string.Empty;
    [JsonPropertyName("type")]          public string?      Type         { get; set; }
    [JsonPropertyName("offset")]        public int          Offset       { get; set; }
    [JsonPropertyName("from")]          public string?      From         { get; set; }
    [JsonPropertyName("address")]       public string?      Address      { get; set; }

    // Decoded payload — exactly one of these is meaningful per row.
    [JsonPropertyName("value")]         public JsonElement? Value        { get; set; }
    [JsonPropertyName("ptr")]           public string?      Ptr          { get; set; }
    [JsonPropertyName("string")]        public string?      String       { get; set; }
    [JsonPropertyName("string_length")] public int?         StringLength { get; set; }
    [JsonPropertyName("string_error")]  public string?      StringError  { get; set; }
    [JsonPropertyName("error")]         public string?      Error        { get; set; }
}

/// <summary>
/// `getStaticFieldData` result — runtime address of the static field's storage
/// so callers can <c>readMemory</c> it directly.
/// </summary>
public sealed class GetStaticFieldDataResult
{
    [JsonPropertyName("field")]   public string? Field   { get; set; }
    [JsonPropertyName("type")]    public string? Type    { get; set; }
    [JsonPropertyName("offset")]  public int     Offset  { get; set; }
    [JsonPropertyName("flags")]   public uint    Flags   { get; set; }
    [JsonPropertyName("address")] public string? Address { get; set; }
    [JsonPropertyName("error")]   public string? Error   { get; set; }
}

/// <summary>
/// `readMemory` result. Soft errors land in <see cref="Error"/>
/// (e.g. "memory not readable").
/// </summary>
public sealed class ReadMemoryResult
{
    [JsonPropertyName("address")] public string? Address { get; set; }
    [JsonPropertyName("size")]    public int     Size    { get; set; }
    [JsonPropertyName("hex")]     public string? Hex     { get; set; }
    [JsonPropertyName("error")]   public string? Error   { get; set; }
}

/// <summary>
/// `walkArray` result — one entry per slot in a managed <c>T[]</c>, capped
/// by the request limit. Pointer-typed arrays come back with <c>ptr</c>
/// set per slot (the dereferenced object pointer); primitive arrays come
/// back with the same per-type fields the field decoder uses
/// (<c>value</c> / <c>string</c> / etc.).
/// </summary>
public sealed class WalkArrayResult
{
    [JsonPropertyName("address")]       public string?               Address      { get; set; }
    [JsonPropertyName("data_address")]  public string?               DataAddress  { get; set; }
    /// <summary>Array length read from the header — may exceed <see cref="Count"/> when capped.</summary>
    [JsonPropertyName("length")]        public ulong                 Length       { get; set; }
    [JsonPropertyName("limit")]         public int                   Limit        { get; set; }
    [JsonPropertyName("element_type")]  public string?               ElementType  { get; set; }
    [JsonPropertyName("element_size")]  public int                   ElementSize  { get; set; }
    [JsonPropertyName("is_pointer")]    public bool                  IsPointer    { get; set; }
    [JsonPropertyName("count")]         public int                   Count        { get; set; }
    [JsonPropertyName("elements")]      public List<WalkedArrayElement>? Elements { get; set; }
    [JsonPropertyName("error")]         public string?               Error        { get; set; }
}

/// <summary>
/// One slot in a <see cref="WalkArrayResult"/>. Same per-type payload model
/// as <see cref="WalkedField"/> — exactly one of <c>value</c> / <c>ptr</c> /
/// <c>string</c> / <c>error</c> is meaningful per row.
/// </summary>
public sealed class WalkedArrayElement
{
    [JsonPropertyName("index")]         public int          Index        { get; set; }
    [JsonPropertyName("address")]       public string?      Address      { get; set; }
    [JsonPropertyName("value")]         public JsonElement? Value        { get; set; }
    [JsonPropertyName("ptr")]           public string?      Ptr          { get; set; }
    [JsonPropertyName("string")]        public string?      String       { get; set; }
    [JsonPropertyName("string_length")] public int?         StringLength { get; set; }
    [JsonPropertyName("string_error")]  public string?      StringError  { get; set; }
    [JsonPropertyName("hex")]           public string?      Hex          { get; set; }
    [JsonPropertyName("error")]         public string?      Error        { get; set; }
}
