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
    /// <summary>Phase 29b — fingerprint hash of the active game module
    /// (FNV-1a over file path + size + last-write-time). Same value
    /// <see cref="SnapshotBeginResult.GameBuildId"/> carries; the UI
    /// uses it to detect "reconnected to a different game build"
    /// and mark old snapshots stale.</summary>
    [JsonPropertyName("game_build_id")]     public string? GameBuildId     { get; set; }

    /// <summary>Phase 29-polish (2026-05-27) — sizeof(void*) inside the
    /// game process. 4 on WOW64 / x86 targets, 8 on x64. UI uses this
    /// to auto-clamp snapshot caps on 32-bit games where the DLL-side
    /// JSON cache can push past the 4 GB user-space ceiling (build #646
    /// 활俠傳 CTD).</summary>
    [JsonPropertyName("game_pointer_size")]  public int?  GamePointerSize  { get; set; }

    /// <summary>Current game-process WorkingSet in bytes (physical
    /// memory mapping). Sampled fresh per handshake call. UI displays
    /// alongside its own WorkingSet so the user sees real memory
    /// pressure on the target.</summary>
    [JsonPropertyName("game_working_set")]   public ulong? GameWorkingSet  { get; set; }

    /// <summary>Current game-process PrivateUsage in bytes — total
    /// committed virtual memory. Closer-to-real metric for the 32-bit
    /// address-space pressure that triggers OOM. Used by the DLL's
    /// snapshotBegin pre-check (>3 GB on x86 refuses the snapshot).</summary>
    [JsonPropertyName("game_private_usage")] public ulong? GamePrivateUsage { get; set; }
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
/// `setInstanceField` result — Phase 21a Inspector field write-back.
/// <see cref="Ok"/> is true on a successful write of <see cref="Written"/>
/// bytes at <see cref="FieldAddress"/>. Soft errors (memory not writable,
/// value out of range, unsupported type) land in <see cref="Error"/>; the
/// returned object still carries <see cref="FieldAddress"/> / <see cref="Type"/>
/// so the UI can show the rejected address verbatim.
/// </summary>
public sealed class SetInstanceFieldResult
{
    [JsonPropertyName("ok")]           public bool    Ok           { get; set; }
    [JsonPropertyName("address")]      public string? Address      { get; set; }
    [JsonPropertyName("fieldOffset")]  public long    FieldOffset  { get; set; }
    [JsonPropertyName("fieldAddress")] public string? FieldAddress { get; set; }
    [JsonPropertyName("type")]         public string? Type         { get; set; }
    [JsonPropertyName("written")]      public int     Written      { get; set; }
    [JsonPropertyName("size")]         public int     Size         { get; set; }
    [JsonPropertyName("error")]        public string? Error        { get; set; }
    [JsonPropertyName("hint")]         public string? Hint         { get; set; }
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
// Phase 21a — reverse-property lookup.
//
// `listFieldValuesAcrossInstances` is the row-wise inverse of `walkInstance`:
// it takes one class + one field offset + the field's managed type, finds
// every live instance via the same heap scan as `findInstances`, then decodes
// the field at instance + offset for each. The UI uses this to answer "what
// value does field X have across every live instance of class Y right now?"
// — useful when chasing per-entity state (player chrNo, item slot id, …).
//
// Scope is value-typed fields whose offset is statically fixed from the
// instance base. Pointer-traversal fields and array-element fields are
// rejected at the UI layer (they'd need per-row deref / per-row index
// resolution, neither of which fits the bulk scan model). See
// `docs/pipe-protocol.md` §"listFieldValuesAcrossInstances".
// ---------------------------------------------------------------------------

/// <summary>
/// `listFieldValuesAcrossInstances` result. <see cref="ClassPtr"/> and
/// (Mono-only) <see cref="VTable"/> echo the values <c>findInstances</c>
/// returns so cross-backend consumers can use one canonical class identity
/// field. <see cref="Rows"/> is empty (count = 0) when no live instances of
/// the class were located; a non-empty rows list with every row carrying
/// <c>error</c> is the "instances found but offset unreadable" case.
/// </summary>
public sealed class ListFieldValuesResult
{
    [JsonPropertyName("class_ptr")]   public string?                    ClassPtr    { get; set; }
    [JsonPropertyName("vtable")]      public string?                    VTable      { get; set; }
    [JsonPropertyName("count")]       public int                        Count       { get; set; }
    [JsonPropertyName("limit")]       public int                        Limit       { get; set; }
    [JsonPropertyName("fieldOffset")] public long                       FieldOffset { get; set; }
    [JsonPropertyName("fieldType")]   public string?                    FieldType   { get; set; }
    [JsonPropertyName("rows")]        public List<ListFieldValueRow>?   Rows        { get; set; }
    [JsonPropertyName("error")]       public string?                    Error       { get; set; }
}

/// <summary>
/// One decoded row from <see cref="ListFieldValuesResult.Rows"/>. Per-type
/// payload mirrors <see cref="WalkedField"/> exactly — exactly one of
/// <see cref="Value"/> / <see cref="Ptr"/> / <see cref="String"/> /
/// <see cref="Error"/> is meaningful per row.
/// </summary>
public sealed class ListFieldValueRow
{
    [JsonPropertyName("instance")]      public string?      Instance     { get; set; }
    [JsonPropertyName("fieldAddress")]  public string?      FieldAddress { get; set; }
    [JsonPropertyName("value")]         public JsonElement? Value        { get; set; }
    [JsonPropertyName("ptr")]           public string?      Ptr          { get; set; }
    [JsonPropertyName("string")]        public string?      String       { get; set; }
    [JsonPropertyName("string_length")] public int?         StringLength { get; set; }
    [JsonPropertyName("string_error")]  public string?      StringError  { get; set; }
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

// ---------------------------------------------------------------------------
// Phase 25 — cross-class symbol finder.
//
// Searches every class in a single assembly for fields + methods whose name
// contains the (case-insensitive ASCII) substring `pattern`. One RPC call
// replaces the historical "click each class in turn" loop.
//
// Wire shape is unified across backends: Mono and IL2CPP emit the same
// `SearchSymbolMatch` records, with `Address` populated only on IL2CPP
// (Mono needs mono_compile_method to JIT a method, which is too expensive
// for bulk scanning — JIT is deferred to the user's follow-up "Call method"
// click).
// ---------------------------------------------------------------------------

public sealed class SearchSymbolsResult
{
    /// <summary>Matching rows, capped at the requested `limit` (default 200).</summary>
    [JsonPropertyName("matches")]         public List<SearchSymbolMatch> Matches        { get; set; } = new();
    /// <summary>Classes the backend walked. May exceed Matches.Count if Hits filtered out.</summary>
    [JsonPropertyName("scanned_classes")] public int                     ScannedClasses { get; set; }
    /// <summary>True when the result was cut short by `limit` — the user
    /// should narrow the pattern or raise the cap.</summary>
    [JsonPropertyName("truncated")]       public bool                    Truncated      { get; set; }
    /// <summary>Assemblies the caller asked for that the backend couldn't
    /// resolve (multi-assembly mode only). Empty / omitted on the wire
    /// in single-assembly mode where a missing assembly is a hard error.</summary>
    [JsonPropertyName("missing_assemblies")] public List<string>?        MissingAssemblies { get; set; }
    /// <summary>Per-assembly truncation (build #547 follow-up). In
    /// multi-assembly mode each assembly gets its own <c>limit</c> budget
    /// so a single big assembly (e.g. <c>Ak.Wwise.Api.WAAPI</c>) can't
    /// starve the rest. Any assembly that filled its per-asm budget is
    /// reported here so the UI can surface a "raise the limit to see more
    /// from X, Y, Z" hint. Empty / omitted in single-assembly mode (use
    /// <see cref="Truncated"/> there).</summary>
    [JsonPropertyName("truncated_assemblies")] public List<string>?      TruncatedAssemblies { get; set; }
    [JsonPropertyName("error")]           public string?                 Error          { get; set; }
}

public sealed class SearchSymbolMatch
{
    [JsonPropertyName("namespace")] public string  Namespace { get; set; } = string.Empty;
    [JsonPropertyName("class")]     public string  Class     { get; set; } = string.Empty;
    /// <summary>Assembly the row was found in. Multi-assembly scans need this
    /// to navigate back to the right Browser tab; single-assembly scans
    /// repeat the caller's input here for shape consistency.</summary>
    [JsonPropertyName("assembly")]  public string? Assembly  { get; set; }
    [JsonPropertyName("name")]      public string  Name      { get; set; } = string.Empty;
    /// <summary>"field" or "method".</summary>
    [JsonPropertyName("kind")]      public string  Kind      { get; set; } = string.Empty;
    /// <summary>Field type for "field" rows. Empty for "method" (return type
    /// isn't carried by the lightweight scan — use findMethodInstances for
    /// full method signatures).</summary>
    [JsonPropertyName("type")]      public string? Type      { get; set; }
    /// <summary>Field offset in bytes from instance base (for "field" rows).</summary>
    [JsonPropertyName("offset")]    public int?    Offset    { get; set; }
    /// <summary>AOT method address (IL2CPP "method" rows only). Mono methods
    /// land here as null because Mono JIT is deferred to the user-driven
    /// findMethodInstances / Call method flow.</summary>
    [JsonPropertyName("address")]   public string? Address   { get; set; }
    [JsonPropertyName("token")]     public uint?   Token     { get; set; }
    [JsonPropertyName("static")]    public bool    IsStatic  { get; set; }
}

/// <summary>
/// `dumpIl2cppSymbols` result — IL2CPP-only bulk dump of class metadata
/// pointers (`Il2CppClass*` in GameAssembly.dll's .data) + AOT method
/// entry points (in .text). Drives the debugger-symbol exporters (x64dbg
/// .dd64 / Ghidra labels / IDA .idc).
/// </summary>
public sealed class DumpIl2cppSymbolsResult
{
    /// <summary>The module that owns every address in <see cref="Rows"/>.
    /// Always "GameAssembly.dll" for IL2CPP, or empty when the module
    /// can't be located (the backend then leaves <see cref="ModuleBase"/>
    /// at 0 too).</summary>
    [JsonPropertyName("moduleName")]      public string  ModuleName      { get; set; } = string.Empty;
    /// <summary>Module image base, formatted as uppercase "0x..." hex.
    /// "0x0000000000000000" when the module isn't located.</summary>
    [JsonPropertyName("moduleBase")]      public string  ModuleBase      { get; set; } = string.Empty;
    [JsonPropertyName("moduleSize")]      public ulong   ModuleSize      { get; set; }
    [JsonPropertyName("rows")]            public List<SymbolDumpRow> Rows { get; set; } = new();
    [JsonPropertyName("count")]           public int     Count           { get; set; }
    [JsonPropertyName("scanned_classes")] public int     ScannedClasses  { get; set; }
    [JsonPropertyName("truncated")]       public bool    Truncated       { get; set; }
    [JsonPropertyName("missing_assemblies")] public List<string>? MissingAssemblies { get; set; }
    [JsonPropertyName("error")]           public string? Error           { get; set; }
}

/// <summary>
/// `dumpClassInfo` result — bulk equivalent of `getClassInfo`. One row
/// per class across every requested assembly, in ONE pipe round-trip.
/// Replaces the N×getClassInfo client-side loop that
/// <see cref="Export.Il2cppHeaderBatchExporter"/> originally used (and
/// that caused the SaGa F2R >60s slow-quit documented in
/// lessons-learned.md "Phase C follow-up — IL2CPP equivalent"). Same
/// wire shape on both backends; Mono method rows carry null `Address`.
/// </summary>
public sealed class DumpClassInfoResult
{
    [JsonPropertyName("rows")]               public List<DumpedClassRow>  Rows               { get; set; } = new();
    [JsonPropertyName("count")]              public int                   Count              { get; set; }
    [JsonPropertyName("scanned")]            public int                   Scanned            { get; set; }
    [JsonPropertyName("truncated")]          public bool                  Truncated          { get; set; }
    [JsonPropertyName("missing_assemblies")] public List<string>?         MissingAssemblies  { get; set; }
    [JsonPropertyName("error")]              public string?               Error              { get; set; }
}

public sealed class DumpedClassRow
{
    [JsonPropertyName("assembly")]  public string                  Assembly  { get; set; } = string.Empty;
    [JsonPropertyName("namespace")] public string?                 Namespace { get; set; }
    [JsonPropertyName("name")]      public string                  Name      { get; set; } = string.Empty;
    [JsonPropertyName("parent")]    public ClassRef?               Parent    { get; set; }
    [JsonPropertyName("fields")]    public List<FieldEntry>?       Fields    { get; set; }
    [JsonPropertyName("methods")]   public List<MethodEntry>?      Methods   { get; set; }
    /// <summary>When set, this row failed server-side (e.g. ReadClassInfo
    /// returned !ok). Fields / Methods are then empty. UI consumers treat
    /// this the same as a null getClassInfo result — emit a `// SKIPPED`
    /// marker in the il2cpp.h instead of a struct body.</summary>
    [JsonPropertyName("error")]     public string?                 Error     { get; set; }
}

public sealed class SymbolDumpRow
{
    /// <summary>"class" or "method".</summary>
    [JsonPropertyName("kind")]        public string  Kind        { get; set; } = string.Empty;
    [JsonPropertyName("assembly")]    public string  Assembly    { get; set; } = string.Empty;
    [JsonPropertyName("namespace")]   public string  Namespace   { get; set; } = string.Empty;
    [JsonPropertyName("class")]       public string  Class       { get; set; } = string.Empty;
    /// <summary>Method simple name (method rows only).</summary>
    [JsonPropertyName("name")]        public string? Name        { get; set; }
    /// <summary>Method metadata token (method rows only).</summary>
    [JsonPropertyName("token")]       public uint?   Token       { get; set; }
    /// <summary>Method static flag (method rows only).</summary>
    [JsonPropertyName("static")]      public bool?   IsStatic    { get; set; }
    /// <summary>Absolute address, uppercase "0x..." hex. For class rows it's
    /// the `Il2CppClass*`; for method rows it's the AOT methodPointer.</summary>
    [JsonPropertyName("address")]     public string  Address     { get; set; } = string.Empty;
    /// <summary>Overload-disambiguating suffix (method rows only) — same
    /// shape as <c>findMethodInstances</c>: "noargs" / "int" / "3float" /
    /// "string_Card_Point". Used to keep label collisions out of the
    /// generated debugger symbols.</summary>
    [JsonPropertyName("humanSuffix")] public string? HumanSuffix { get; set; }
}

// ---------------------------------------------------------------------------
// Phase 28 — Field → Methods cross-reference (`findFieldReferences`).
//
// Inverse of <see cref="SearchSymbolsResult"/>: given a Unity field, list
// every method that touches it. Same wire shape across backends; the
// behavioural differences are captured in the response fields:
//   - Mono: <see cref="FieldReferenceRow.Address"/> null (JIT addresses
//     don't survive across runs; user-driven `findMethodInstances` /
//     `callMethod` flows are the way to get a real entry pointer).
//     <see cref="FieldReferenceRow.IlOffset"/> populated.
//   - IL2CPP: <see cref="FieldReferenceRow.Address"/> populated with
//     the AOT entry of the enclosing method; IlOffset null.
//   - IL2CPP also surfaces <see cref="FieldReferenceFieldInfo.StaticAddress"/>
//     so the UI can show the resolved static-storage address that drove
//     the RIP scan.
// ---------------------------------------------------------------------------

public sealed class FindFieldReferencesResult
{
    [JsonPropertyName("ok")]              public bool                       Ok             { get; set; }
    [JsonPropertyName("field")]           public FieldReferenceFieldInfo?   Field          { get; set; }
    [JsonPropertyName("references")]      public List<FieldReferenceRow>    References     { get; set; } = new();
    [JsonPropertyName("scanned_methods")] public int                        ScannedMethods { get; set; }
    [JsonPropertyName("truncated")]       public bool                       Truncated      { get; set; }
    [JsonPropertyName("error")]           public string?                    Error          { get; set; }
    /// <summary>Build #555 — IL2CPP advisory. Set when the backend scan ran
    /// successfully but returned 0 matches AND the static-storage address
    /// sits outside the GameAssembly.dll module (= heap-allocated by IL2CPP
    /// runtime at class-init time, no compiled instruction can RIP-address
    /// it). Tells the user the 0-match result reflects a structural
    /// limitation of v1's IL2CPP scanner, not "field is unused". Surfaced
    /// as a yellow advisory banner in <c>FieldReferencesWindow</c>. Null on
    /// Mono and on IL2CPP scans whose static lived in the module (rare).</summary>
    [JsonPropertyName("note")]            public string?                    Note           { get; set; }
}

public sealed class FieldReferenceFieldInfo
{
    [JsonPropertyName("name")]          public string  Name          { get; set; } = string.Empty;
    [JsonPropertyName("type")]          public string? Type          { get; set; }
    [JsonPropertyName("offset")]        public long?   Offset        { get; set; }
    [JsonPropertyName("isStatic")]      public bool    IsStatic      { get; set; }
    /// <summary>Mono only — the field's FieldDef metadata token (0x04xxxxxx)
    /// used to identify the field while walking IL operand immediates.</summary>
    [JsonPropertyName("token")]         public uint?   Token         { get; set; }
    /// <summary>IL2CPP only — resolved static storage absolute address that
    /// drove the RIP scan. Surface this in the UI so the user can confirm
    /// the scan targeted the right location.</summary>
    [JsonPropertyName("staticAddress")] public string? StaticAddress { get; set; }
}

public sealed class FieldReferenceRow
{
    [JsonPropertyName("assembly")]       public string  Assembly       { get; set; } = string.Empty;
    [JsonPropertyName("namespace")]      public string  Namespace      { get; set; } = string.Empty;
    [JsonPropertyName("class")]          public string  Class          { get; set; } = string.Empty;
    [JsonPropertyName("method")]         public string  Method         { get; set; } = string.Empty;
    [JsonPropertyName("methodToken")]    public uint    MethodToken    { get; set; }
    [JsonPropertyName("methodFlags")]    public uint    MethodFlags    { get; set; }
    [JsonPropertyName("isMethodStatic")] public bool    IsMethodStatic { get; set; }
    /// <summary>True iff the *target* field is static. Carried per-row so
    /// the UI doesn't have to glance back at <see cref="FindFieldReferencesResult.Field"/>.</summary>
    [JsonPropertyName("isStatic")]       public bool    IsStatic       { get; set; }
    /// <summary>"read" or "write".</summary>
    [JsonPropertyName("kind")]           public string  Kind           { get; set; } = string.Empty;
    /// <summary>"exact" for both Mono (IL token match) and IL2CPP
    /// (RIP-relative decode). Reserved for a future "heuristic" badge if
    /// Phase 28c instance-field AOB scanning ships.</summary>
    [JsonPropertyName("confidence")]     public string  Confidence     { get; set; } = string.Empty;
    /// <summary>Enclosing method's AOT entry address as uppercase "0x..."
    /// hex (IL2CPP). Null on Mono — JIT addresses don't survive restarts
    /// and bulk JIT is too expensive for this scan.</summary>
    [JsonPropertyName("address")]        public string? Address        { get; set; }
    /// <summary>Exact instruction pointer of the matching insn — IL2CPP
    /// only. Useful for "Navigate to disassembly" handoff.</summary>
    [JsonPropertyName("hitAddress")]     public string? HitAddress     { get; set; }
    /// <summary>Byte offset of the matching IL op within the method body
    /// — Mono only.</summary>
    [JsonPropertyName("ilOffset")]       public uint?   IlOffset       { get; set; }
}

// ---------------------------------------------------------------------------
// Phase 20-MT — main-thread dispatcher diagnostics.
//
// Read-only snapshot of the DLL's main-thread queue counters. Drives the
// connection-bar "Enable runInBackground" banner (Phase 20-MT v2): if
// <see cref="DispatcherStatsResult.IsHealthy"/> is false but
// <see cref="DispatcherStatsResult.HookInstalled"/> is true, the engine
// is alive but stopped ticking — almost always means
// `Application.runInBackground = false` and the game lost focus. The
// banner surfaces a one-click fix instead of letting the user discover
// it via a 5-second RPC timeout.
// ---------------------------------------------------------------------------

public sealed class DispatcherStatsResult
{
    [JsonPropertyName("initialized")]      public bool   Initialized     { get; set; }
    [JsonPropertyName("hook_installed")]   public bool   HookInstalled   { get; set; }
    [JsonPropertyName("main_thread_id")]   public uint   MainThreadId    { get; set; }
    [JsonPropertyName("tick_count")]       public ulong  TickCount       { get; set; }
    [JsonPropertyName("submit_count")]     public ulong  SubmitCount     { get; set; }
    [JsonPropertyName("drain_count")]      public ulong  DrainCount      { get; set; }
    [JsonPropertyName("timeout_count")]    public ulong  TimeoutCount    { get; set; }
    [JsonPropertyName("reject_count")]     public ulong  RejectCount     { get; set; }
    /// <summary>Milliseconds since the dispatcher's drain hook last fired.
    /// Big values (&gt;1000ms) on a connected target almost always mean
    /// the engine has paused — typically focus-loss with
    /// <c>runInBackground=false</c>.</summary>
    [JsonPropertyName("last_tick_age_ms")] public long   LastTickAgeMs   { get; set; }
    [JsonPropertyName("queue_depth")]      public uint   QueueDepth      { get; set; }
    /// <summary>True when the hook is installed AND the drain has fired
    /// recently. False = either no hook (engine doesn't have one of the
    /// expected entry points) OR the engine is paused.</summary>
    [JsonPropertyName("is_healthy")]       public bool   IsHealthy       { get; set; }
    [JsonPropertyName("error")]            public string? Error          { get; set; }
}

// ---------------------------------------------------------------------------
// Phase 27 — Unity-aware value search (scanFieldValue + Begin/Refine/End).
// ---------------------------------------------------------------------------

public sealed class ValueScanCandidate
{
    [JsonPropertyName("addr")]         public string  Address      { get; set; } = string.Empty;
    [JsonPropertyName("instAddr")]     public string? InstanceAddr { get; set; }
    [JsonPropertyName("asm")]          public string? Assembly     { get; set; }
    [JsonPropertyName("ns")]           public string? Namespace    { get; set; }
    [JsonPropertyName("class")]        public string? Class        { get; set; }
    [JsonPropertyName("field")]        public string? Field        { get; set; }
    [JsonPropertyName("fieldType")]    public string? FieldType    { get; set; }
    [JsonPropertyName("fieldKind")]    public string? FieldKind    { get; set; }
    [JsonPropertyName("fieldOffset")]  public long?   FieldOffset  { get; set; }
    /// <summary>Stringified current value — number for numeric scans, string
    /// for string scans. Wire shape is JSON value; we accept it as string
    /// after the round-trip to keep AOT serializers happy.</summary>
    [JsonPropertyName("currentValue")] public JsonElement? CurrentValue { get; set; }
    [JsonPropertyName("matchedLength")] public int?   MatchedLength { get; set; }
}

public sealed class BeginValueScanResult
{
    [JsonPropertyName("ok")]                public bool   Ok                { get; set; }
    [JsonPropertyName("sessionId")]         public string SessionId         { get; set; } = string.Empty;
    [JsonPropertyName("mode")]              public string Mode              { get; set; } = string.Empty;
    [JsonPropertyName("dataType")]          public string DataType          { get; set; } = string.Empty;
    [JsonPropertyName("scanType")]          public string ScanType          { get; set; } = string.Empty;
    [JsonPropertyName("count")]             public int    Count             { get; set; }
    [JsonPropertyName("totalCandidates")]   public int    TotalCandidates   { get; set; }
    [JsonPropertyName("droppedNonManaged")] public int    DroppedNonManaged { get; set; }
    [JsonPropertyName("truncated")]         public bool   Truncated         { get; set; }
    [JsonPropertyName("scannedBytes")]      public long   ScannedBytes      { get; set; }
    [JsonPropertyName("candidates")]        public List<ValueScanCandidate> Candidates { get; set; } = new();
    [JsonPropertyName("error")]             public string? Error            { get; set; }
}

public sealed class RefineValueScanResult
{
    [JsonPropertyName("ok")]              public bool   Ok              { get; set; }
    [JsonPropertyName("sessionId")]       public string SessionId       { get; set; } = string.Empty;
    [JsonPropertyName("scanType")]        public string ScanType        { get; set; } = string.Empty;
    [JsonPropertyName("dataType")]        public string DataType        { get; set; } = string.Empty;
    [JsonPropertyName("count")]           public int    Count           { get; set; }
    [JsonPropertyName("totalCandidates")] public int    TotalCandidates { get; set; }
    [JsonPropertyName("removed")]         public int    Removed         { get; set; }
    [JsonPropertyName("truncated")]       public bool   Truncated       { get; set; }
    [JsonPropertyName("candidates")]      public List<ValueScanCandidate> Candidates { get; set; } = new();
    [JsonPropertyName("error")]           public string? Error          { get; set; }
}

public sealed class EndValueScanResult
{
    [JsonPropertyName("ok")]        public bool   Ok        { get; set; }
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("dropped")]   public bool   Dropped   { get; set; }
    [JsonPropertyName("error")]     public string? Error    { get; set; }
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

// ---------------------------------------------------------------------------
// Phase 29 — Snapshot & Diff (snapshotBegin / snapshotChunk / snapshotEnd).
// ---------------------------------------------------------------------------

/// <summary>Per-assembly summary returned in the Begin response's `plan`
/// array. UI uses this for the "X classes / Y instances per assembly"
/// progress feedback.</summary>
public sealed class SnapshotPlanEntry
{
    [JsonPropertyName("assembly")]      public string Assembly      { get; set; } = string.Empty;
    [JsonPropertyName("classCount")]    public int    ClassCount    { get; set; }
    [JsonPropertyName("instanceCount")] public int    InstanceCount { get; set; }
}

public sealed class SnapshotScanStats
{
    [JsonPropertyName("regionsTotal")]     public int  RegionsTotal     { get; set; }
    [JsonPropertyName("regionsScanned")]   public int  RegionsScanned   { get; set; }
    [JsonPropertyName("regionsSkipImage")] public int  RegionsSkipImage { get; set; }
    [JsonPropertyName("regionsSkipStack")] public int  RegionsSkipStack { get; set; }
    [JsonPropertyName("bytesScanned")]     public long BytesScanned     { get; set; }
    /// <summary>Mono-only stat — number of vtable→klass derefs the wide scan
    /// resolved on its first pass. IL2CPP returns null.</summary>
    [JsonPropertyName("vtablesResolved")]  public int? VtablesResolved  { get; set; }
}

public sealed class SnapshotDecodeStats
{
    [JsonPropertyName("decodedClasses")]           public int DecodedClasses           { get; set; }
    [JsonPropertyName("decodedInstances")]         public int DecodedInstances         { get; set; }
    [JsonPropertyName("decodedArrays")]            public int DecodedArrays            { get; set; }
    [JsonPropertyName("skippedOpenGenericArrays")] public int SkippedOpenGenericArrays { get; set; }
    [JsonPropertyName("classInfoCacheHits")]       public int ClassInfoCacheHits       { get; set; }
    [JsonPropertyName("classInfoCacheMisses")]     public int ClassInfoCacheMisses     { get; set; }
}

public sealed class SnapshotBeginResult
{
    [JsonPropertyName("ok")]              public bool   Ok              { get; set; }
    [JsonPropertyName("snapshotId")]      public string SnapshotId      { get; set; } = string.Empty;
    [JsonPropertyName("capturedAt")]      public string CapturedAt      { get; set; } = string.Empty;
    [JsonPropertyName("gameBuildId")]     public string GameBuildId     { get; set; } = string.Empty;
    [JsonPropertyName("gameSessionId")]   public string GameSessionId   { get; set; } = string.Empty;
    [JsonPropertyName("instanceCap")]     public int    InstanceCap     { get; set; }
    [JsonPropertyName("arrayCap")]        public int    ArrayCap        { get; set; }
    [JsonPropertyName("totalClasses")]    public int    TotalClasses    { get; set; }
    [JsonPropertyName("totalInstances")]  public int    TotalInstances  { get; set; }
    [JsonPropertyName("plan")]            public List<SnapshotPlanEntry> Plan       { get; set; } = new();
    [JsonPropertyName("scanStats")]       public SnapshotScanStats?      ScanStats   { get; set; }
    [JsonPropertyName("decodeStats")]     public SnapshotDecodeStats?    DecodeStats { get; set; }
    [JsonPropertyName("excludedSystemAssemblies")] public int            ExcludedSystemAssemblies { get; set; }
    [JsonPropertyName("includeSystemAssemblies")]  public bool           IncludeSystemAssemblies  { get; set; }
    [JsonPropertyName("error")]           public string?                 Error       { get; set; }

    /// <summary>Phase 29-polish — game-process pointer size echoed back
    /// from the DLL on every snapshotBegin. 4 = x86 (32-bit OOM risk),
    /// 8 = x64. Populated even when Error is set (the 32-bit OOM
    /// refusal includes it so the UI can show the right banner).</summary>
    [JsonPropertyName("gamePointerSize")]    public int?   GamePointerSize  { get; set; }

    /// <summary>Post-decode game-process WorkingSet in bytes. UI surfaces
    /// this in the snapshot status line alongside its own WorkingSet.</summary>
    [JsonPropertyName("gameWorkingSet")]     public ulong? GameWorkingSet   { get; set; }

    /// <summary>Post-decode game-process PrivateUsage in bytes — used
    /// by the UI to warn before snap #2 if we're approaching the
    /// 32-bit OOM threshold.</summary>
    [JsonPropertyName("gamePrivateUsage")]   public ulong? GamePrivateUsage { get; set; }
}

/// <summary>One field inside an instance (or one static field of a class).
/// The shape mirrors the existing <see cref="WalkedField"/> with the
/// addition of <see cref="ArrayElements"/> (inline T[] capture from the
/// snapshot walker) and <see cref="ArrayError"/> for arrays whose stored
/// pointer didn't point at a valid managed array header (false-positive
/// instances).</summary>
public sealed class SnapshotFieldEntry
{
    [JsonPropertyName("name")]              public string       Name             { get; set; } = string.Empty;
    [JsonPropertyName("type")]              public string       Type             { get; set; } = string.Empty;
    // ulong: C++ writes IL2CPP::FieldEntry::offset which is std::size_t
    // (uint64 on x64). Most fields have small offsets, but garbage values
    // from false-positive class entries (live-smoke #629 SaGa F2R:
    // classes[8].statics[3].offset exceeded Int64.MaxValue) trip
    // Int64 deserialization. Match C++ type.
    [JsonPropertyName("offset")]            public ulong        Offset           { get; set; }
    [JsonPropertyName("from")]              public string?      From             { get; set; }
    [JsonPropertyName("is_enum")]           public bool?        IsEnum           { get; set; }
    [JsonPropertyName("is_value_type")]     public bool?        IsValueType      { get; set; }
    [JsonPropertyName("address")]           public string?      Address          { get; set; }
    [JsonPropertyName("value")]             public JsonElement? Value            { get; set; }
    [JsonPropertyName("ptr")]               public string?      Ptr              { get; set; }
    [JsonPropertyName("string")]            public string?      StringValue      { get; set; }
    [JsonPropertyName("string_length")]     public int?         StringLength     { get; set; }
    [JsonPropertyName("string_error")]      public string?      StringError      { get; set; }
    [JsonPropertyName("error")]             public string?      Error            { get; set; }
    [JsonPropertyName("arrayElements")]     public SnapshotArrayElements? ArrayElements    { get; set; }
    [JsonPropertyName("arrayError")]        public string?      ArrayError       { get; set; }
    // ulong? not long?: C++ writes std::uintptr_t which on x64 is uint64.
    // False-positive instances can have garbage values > Int64.MaxValue
    // (build #627 SaGa F2R / BongoCat smoke: JsonException at this field
    // when value didn't fit signed long). ulong covers the full range.
    [JsonPropertyName("arrayErrorLength")]  public ulong?       ArrayErrorLength { get; set; }
}

public sealed class SnapshotArrayElement
{
    [JsonPropertyName("index")]   public int          Index   { get; set; }
    [JsonPropertyName("value")]   public JsonElement? Value   { get; set; }
    [JsonPropertyName("ptr")]     public string?      Ptr     { get; set; }
    [JsonPropertyName("string")]  public string?      String  { get; set; }
    [JsonPropertyName("error")]   public string?      Error   { get; set; }
    [JsonPropertyName("address")] public string?      Address { get; set; }
}

public sealed class SnapshotArrayElements
{
    // ulong: C++ writes std::uintptr_t. Legitimate arrays are capped
    // at 16M by the DLL implausible-length check before reaching here,
    // so values comfortably fit in any integer. ulong (not long) is
    // defensive against future garbage paths and matches the C++ type.
    [JsonPropertyName("length")]       public ulong                         Length       { get; set; }
    [JsonPropertyName("count")]        public int                           Count        { get; set; }
    [JsonPropertyName("element_type")] public string?                       ElementType  { get; set; }
    [JsonPropertyName("element_size")] public ulong                         ElementSize  { get; set; }
    [JsonPropertyName("is_pointer")]   public bool                          IsPointer    { get; set; }
    [JsonPropertyName("elements")]     public List<SnapshotArrayElement>    Elements     { get; set; } = new();
}

public sealed class SnapshotInstanceEntry
{
    [JsonPropertyName("address")] public string                     Address { get; set; } = string.Empty;
    [JsonPropertyName("fields")]  public List<SnapshotFieldEntry>   Fields  { get; set; } = new();
}

public sealed class SnapshotClassParent
{
    [JsonPropertyName("namespace")] public string Namespace { get; set; } = string.Empty;
    [JsonPropertyName("name")]      public string Name      { get; set; } = string.Empty;
}

public sealed class SnapshotClassEntry
{
    [JsonPropertyName("assembly")]  public string                       Assembly  { get; set; } = string.Empty;
    [JsonPropertyName("namespace")] public string                       Namespace { get; set; } = string.Empty;
    [JsonPropertyName("name")]      public string                       Name      { get; set; } = string.Empty;
    [JsonPropertyName("parent")]    public SnapshotClassParent?         Parent    { get; set; }
    [JsonPropertyName("statics")]   public List<SnapshotFieldEntry>?    Statics   { get; set; }
    [JsonPropertyName("instances")] public List<SnapshotInstanceEntry>? Instances { get; set; }
    [JsonPropertyName("error")]     public string?                      Error     { get; set; }
}

public sealed class SnapshotChunkResult
{
    [JsonPropertyName("ok")]         public bool                       Ok         { get; set; }
    [JsonPropertyName("snapshotId")] public string                     SnapshotId { get; set; } = string.Empty;
    [JsonPropertyName("cursor")]     public int                        Cursor     { get; set; }
    [JsonPropertyName("nextCursor")] public int                        NextCursor { get; set; }
    [JsonPropertyName("done")]       public bool                       Done       { get; set; }
    [JsonPropertyName("classes")]    public List<SnapshotClassEntry>   Classes    { get; set; } = new();
    [JsonPropertyName("error")]      public string?                    Error      { get; set; }
}

public sealed class SnapshotEndResult
{
    [JsonPropertyName("ok")]         public bool   Ok         { get; set; }
    [JsonPropertyName("snapshotId")] public string SnapshotId { get; set; } = string.Empty;
    [JsonPropertyName("dropped")]    public bool   Dropped    { get; set; }
    [JsonPropertyName("error")]      public string? Error     { get; set; }
}
