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

    // Phase 11.5 follow-up: server-side type classification so the inspector
    // can route enums to a Leaf row (showing the int) and reserve ValueStruct
    // expansion for actual value-type structs (Vector3, custom structs, …).
    // Both default to false; populated only when the runtime resolver has
    // the right exports bound (see MonoResolver / IL2CPPResolver).
    [JsonPropertyName("is_enum")]         public bool    IsEnum         { get; set; }
    [JsonPropertyName("is_value_type")]   public bool    IsValueType    { get; set; }
    [JsonPropertyName("underlying_type")] public string? UnderlyingType { get; set; }
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
    // ↓ populated only when the caller supplied an elementAssembly/Namespace/Name
    //   triple AND the runtime resolver had class_value_size / class_is_valuetype
    //   bound (Unity 2021.3+ exports both for IL2CPP; Mono bdwgc usually too).
    [JsonPropertyName("element_class_namespace")] public string? ElementClassNamespace { get; set; }
    [JsonPropertyName("element_class_name")]      public string? ElementClassName      { get; set; }
    [JsonPropertyName("element_is_value_type")]   public bool?   ElementIsValueType    { get; set; }
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

// ---------------------------------------------------------------------------
// Phase 17a + 17b — address finder (reverse lookup).
//
// `whatIsAt` is the inverse of `findInstances`: given any process address,
// walk backwards looking for an object header whose first qword is a valid
// runtime class pointer (IL2CPP) or vtable pointer (Mono); then resolve the
// queried byte offset back to the declared field whose [offset, offset+size)
// range contains it (or the closest one below).
// ---------------------------------------------------------------------------

/// <summary>
/// `whatIsAt` result. <see cref="Found"/> = true means the backward scan
/// landed inside a managed instance; <see cref="Field"/> /
/// <see cref="NearestField"/> carry the Phase 17b field-offset resolution.
/// </summary>
public sealed class WhatIsAtResult
{
    [JsonPropertyName("found")]              public bool                  Found             { get; set; }
    [JsonPropertyName("reason")]             public string?               Reason            { get; set; }
    [JsonPropertyName("address")]            public string?               Address           { get; set; }
    [JsonPropertyName("instance")]           public string?               Instance          { get; set; }
    [JsonPropertyName("class_ptr")]          public string?               ClassPtr          { get; set; }
    [JsonPropertyName("vtable_ptr")]         public string?               VTablePtr         { get; set; }
    [JsonPropertyName("assembly")]           public string?               Assembly          { get; set; }
    [JsonPropertyName("namespace")]          public string?               Namespace         { get; set; }
    [JsonPropertyName("class")]              public string?               Class             { get; set; }
    [JsonPropertyName("fieldOffset")]        public long                  FieldOffset       { get; set; }
    [JsonPropertyName("field")]              public WhatIsAtField?        Field             { get; set; }
    [JsonPropertyName("nearestField")]       public WhatIsAtField?        NearestField      { get; set; }
    [JsonPropertyName("nearestFieldDelta")]  public long?                 NearestFieldDelta { get; set; }
    [JsonPropertyName("scannedRange")]       public WhatIsAtScanRange?    ScannedRange      { get; set; }
    [JsonPropertyName("scannedSteps")]       public int                   ScannedSteps      { get; set; }
    [JsonPropertyName("error")]              public string?               Error             { get; set; }
}

public sealed class WhatIsAtField
{
    [JsonPropertyName("name")]            public string  Name             { get; set; } = string.Empty;
    [JsonPropertyName("type")]            public string? Type             { get; set; }
    [JsonPropertyName("offset")]          public int     Offset           { get; set; }
    [JsonPropertyName("size")]            public int     Size             { get; set; }
    [JsonPropertyName("isStatic")]        public bool    IsStatic         { get; set; }
    [JsonPropertyName("isValueType")]     public bool    IsValueType      { get; set; }
    [JsonPropertyName("isEnum")]          public bool    IsEnum           { get; set; }
    [JsonPropertyName("declaredIn")]      public string? DeclaredIn       { get; set; }
    /// <summary>"leaf" | "value-struct" | "pointer" | "interior".</summary>
    [JsonPropertyName("kind")]            public string? Kind             { get; set; }
    [JsonPropertyName("interiorOffset")]  public int?    InteriorOffset   { get; set; }
    [JsonPropertyName("underlying_type")] public string? UnderlyingType   { get; set; }
}

public sealed class WhatIsAtScanRange
{
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("to")]   public string? To   { get; set; }
}

/// <summary>
/// `getClassRangeStats` result — diagnostic for the address-finder cache.
/// Mono populates the vtable counters; IL2CPP leaves them null.
/// </summary>
public sealed class ClassRangeStatsResult
{
    [JsonPropertyName("backend")]      public string? Backend     { get; set; }
    [JsonPropertyName("built")]        public bool    Built       { get; set; }
    [JsonPropertyName("class_count")]  public int     ClassCount  { get; set; }
    [JsonPropertyName("vtable_count")] public int?    VtableCount { get; set; }
    [JsonPropertyName("min_klass")]    public string? MinKlass    { get; set; }
    [JsonPropertyName("max_klass")]    public string? MaxKlass    { get; set; }
    [JsonPropertyName("min_vtable")]   public string? MinVtable   { get; set; }
    [JsonPropertyName("max_vtable")]   public string? MaxVtable   { get; set; }
}

/// <summary>
/// `getMetadataStats` result — name-shape stats over all type definitions
/// the backend can see. Drives the Phase 14b stripped-metadata banner;
/// <see cref="Verdict"/> is one of "clean" / "partial_strip" /
/// "heavy_strip" (or null when the metadata isn't loaded).
/// </summary>
public sealed class MetadataStatsResult
{
    [JsonPropertyName("scope")]              public string?  Scope             { get; set; }
    [JsonPropertyName("total_classes")]      public int      TotalClasses      { get; set; }
    [JsonPropertyName("with_namespace")]     public int      WithNamespace     { get; set; }
    [JsonPropertyName("single_char_names")]  public int      SingleCharNames   { get; set; }
    [JsonPropertyName("short_names_2")]      public int      ShortNames2       { get; set; }
    [JsonPropertyName("compiler_generated")] public int      CompilerGenerated { get; set; }
    [JsonPropertyName("numeric_lead")]       public int      NumericLead       { get; set; }
    [JsonPropertyName("avg_name_length")]    public double?  AvgNameLength     { get; set; }
    [JsonPropertyName("min_name_length")]    public int?     MinNameLength     { get; set; }
    [JsonPropertyName("max_name_length")]    public int      MaxNameLength     { get; set; }
    [JsonPropertyName("strip_ratio")]        public double?  StripRatio        { get; set; }
    [JsonPropertyName("verdict")]            public string?  Verdict           { get; set; }
    [JsonPropertyName("error")]              public string?  Error             { get; set; }
}

// ---------------------------------------------------------------------------
// Phase 18 — find method instances + AOB pattern generator.
// findMethodInstances enumerates every overload of a named method on a class
// and returns each one's native address (Mono: post-JIT; IL2CPP: AOT) +
// decoded signature + a symbol-safe humanSuffix used for AA-script naming.
// getMethodAobPattern reads N bytes at any address, walks them with a
// minimal x64 length disassembler, returns a CE-style pattern with
// rel8/rel32 immediates and RIP-relative ModRM disp32 wildcarded.
// ---------------------------------------------------------------------------

public sealed class FindMethodInstancesResult
{
    [JsonPropertyName("method")]  public string                 Method  { get; set; } = string.Empty;
    [JsonPropertyName("count")]   public int                    Count   { get; set; }
    [JsonPropertyName("matches")] public List<MethodOverloadEntry> Matches { get; set; } = new();
    [JsonPropertyName("error")]   public string?                Error   { get; set; }
}

public sealed class MethodOverloadEntry
{
    [JsonPropertyName("name")]               public string       Name              { get; set; } = string.Empty;
    [JsonPropertyName("token")]              public uint         Token             { get; set; }
    [JsonPropertyName("flags")]              public uint         Flags             { get; set; }
    [JsonPropertyName("iflags")]             public uint         IFlags            { get; set; }
    [JsonPropertyName("isStatic")]           public bool         IsStatic          { get; set; }
    [JsonPropertyName("isVirtual")]          public bool         IsVirtual         { get; set; }
    [JsonPropertyName("isAbstract")]         public bool         IsAbstract        { get; set; }
    [JsonPropertyName("compiled")]           public bool         Compiled          { get; set; }
    [JsonPropertyName("address")]            public string?      Address           { get; set; }
    [JsonPropertyName("paramTypes")]         public List<string> ParamTypes        { get; set; } = new();
    [JsonPropertyName("paramNames")]         public List<string> ParamNames        { get; set; } = new();
    [JsonPropertyName("returnType")]         public string?      ReturnType        { get; set; }
    [JsonPropertyName("declaringClass")]     public string?      DeclaringClass    { get; set; }
    [JsonPropertyName("declaringNamespace")] public string?      DeclaringNamespace { get; set; }
    [JsonPropertyName("signature")]          public string?      Signature         { get; set; }
    [JsonPropertyName("humanSuffix")]        public string?      HumanSuffix       { get; set; }
}

public sealed class GetMethodAobPatternResult
{
    [JsonPropertyName("address")]       public string?               Address       { get; set; }
    [JsonPropertyName("length")]        public int                   Length        { get; set; }
    [JsonPropertyName("pattern")]       public string?               Pattern       { get; set; }
    [JsonPropertyName("bytes")]         public string?               Bytes         { get; set; }
    [JsonPropertyName("moduleName")]    public string?               ModuleName    { get; set; }
    [JsonPropertyName("moduleBase")]    public string?               ModuleBase    { get; set; }
    [JsonPropertyName("moduleOffset")]  public string?               ModuleOffset  { get; set; }
    [JsonPropertyName("moduleSize")]    public long                  ModuleSize    { get; set; }
    [JsonPropertyName("stats")]         public AobPatternStats?      Stats         { get; set; }
    [JsonPropertyName("decoderBailed")] public bool                  DecoderBailed { get; set; }
    [JsonPropertyName("bailedAt")]      public int?                  BailedAt      { get; set; }
    [JsonPropertyName("error")]         public string?               Error         { get; set; }
}

public sealed class AobPatternStats
{
    [JsonPropertyName("instructions")] public int Instructions { get; set; }
    [JsonPropertyName("wildcards")]    public int Wildcards    { get; set; }
    [JsonPropertyName("relBranches")]  public int RelBranches  { get; set; }
    [JsonPropertyName("ripRel")]       public int RipRel       { get; set; }
}

// ---------------------------------------------------------------------------
// Phase 19 — callMethod (Lua-callable Unity methods).
//
// Wire shape mirrors docs/lua-invoke.md. Request carries:
//   * class triple (assembly + namespace + name) — same as getClassInfo
//   * method name + optional signatureMatch substring
//   * optional instance hex address (null/omit = static)
//   * args array of {type, value} pairs
// Response is the typed return value (decoded per the resolved method's
// formal returnType — see lua-invoke.md "returnValue encoding by
// returnType" table) plus any captured managed exception.
// ---------------------------------------------------------------------------

/// <summary>
/// One {type, value} pair in the wire `args` array. `Value` is polymorphic
/// (string for "string", number for primitives, hex string for "address",
/// JsonValueKind.Null for "null") — kept as <see cref="JsonElement"/> so
/// the source-gen serializer doesn't need a polymorphic shape.
/// </summary>
public sealed class CallArgRequest
{
    [JsonPropertyName("type")]  public string      Type  { get; set; } = string.Empty;
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
}

public sealed class CallMethodResult
{
    /// <summary>True iff the call ran AND no managed exception was raised.</summary>
    [JsonPropertyName("ok")]          public bool                  Ok          { get; set; }
    /// <summary>Protocol-level error (resolution / arg / config). Empty if call ran.</summary>
    [JsonPropertyName("error")]       public string?               Error       { get; set; }
    /// <summary>The resolved method's formal return type (e.g. "System.Int32").</summary>
    [JsonPropertyName("returnType")]  public string?               ReturnType  { get; set; }
    /// <summary>
    /// Polymorphic — number for primitives, string for System.String (decoded
    /// UTF-8) and address-encoded reference types ("0x..."), null for void
    /// or value-type-by-value returns. Caller should switch on
    /// <see cref="ReturnType"/>, not on the JSON kind.
    /// </summary>
    [JsonPropertyName("returnValue")] public JsonElement           ReturnValue { get; set; }
    /// <summary>"Type: message" if a managed exception was caught; null otherwise.</summary>
    [JsonPropertyName("exception")]   public string?               Exception   { get; set; }
    [JsonPropertyName("elapsedMs")]   public long                  ElapsedMs   { get; set; }
    /// <summary>
    /// Diagnostic note (e.g. "value-type-by-value returns are not decoded
    /// in 19a/19b/19c; pass an out-buffer in v2 or read the address").
    /// </summary>
    [JsonPropertyName("note")]        public string?               Note        { get; set; }
    [JsonPropertyName("method")]      public CallMethodMethodInfo? Method      { get; set; }
}

public sealed class CallMethodMethodInfo
{
    [JsonPropertyName("address")]     public string? Address     { get; set; }
    [JsonPropertyName("signature")]   public string? Signature   { get; set; }
    [JsonPropertyName("humanSuffix")] public string? HumanSuffix { get; set; }
}

// ---------------------------------------------------------------------------
// Phase 6 segment D — getEnumValues. Used by Phase 19d-3 smart-typed editors
// so the MethodCallWindow can render a ComboBox of named enum members instead
// of a raw integer TextBox. IL2CPP combines runtime + metadata; Mono needs
// mono_field_get_data + mono_class_enum_basetype optional exports (returns
// `error` if missing — caller treats that as soft-fail and reverts to text).
// ---------------------------------------------------------------------------

public sealed class GetEnumValuesResult
{
    [JsonPropertyName("namespace")]       public string?              Namespace      { get; set; }
    [JsonPropertyName("name")]            public string?              Name           { get; set; }
    [JsonPropertyName("underlying_type")] public string?              UnderlyingType { get; set; }
    [JsonPropertyName("count")]           public int                  Count          { get; set; }
    [JsonPropertyName("values")]          public List<EnumValueEntry> Values         { get; set; } = new();
    [JsonPropertyName("error")]           public string?              Error          { get; set; }
    [JsonPropertyName("hint")]            public string?              Hint           { get; set; }
    /// <summary>When the class isn't an enum the DLL returns the parent class name (e.g. "Object").</summary>
    [JsonPropertyName("parent")]          public string?              Parent         { get; set; }
}

public sealed class EnumValueEntry
{
    [JsonPropertyName("name")]         public string       Name        { get; set; } = string.Empty;
    [JsonPropertyName("field_index")]  public int?         FieldIndex  { get; set; }
    /// <summary>
    /// Decoded numeric value (number for integer / float / Boolean enums, raw
    /// numeric for Char). Null when the DLL couldn't decode the blob and set
    /// <see cref="DecodeError"/> instead.
    /// </summary>
    [JsonPropertyName("value")]        public JsonElement? Value       { get; set; }
    [JsonPropertyName("enc_bytes")]    public int?         EncBytes    { get; set; }
    [JsonPropertyName("raw_hex")]      public string?      RawHex      { get; set; }
    [JsonPropertyName("decode_error")] public string?      DecodeError { get; set; }
}
