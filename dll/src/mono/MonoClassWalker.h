// MonoClassWalker.h — class / field / method enumeration on top of MonoResolver.
#pragma once

#include "MonoTypes.h"

#include <cstdint>
#include <string>
#include <vector>

namespace Discrete::Mono {

struct ClassEntry {
    std::string ns;     // namespace ("" for global)
    std::string name;
    std::uint32_t token = 0;
    MonoClass*  klass = nullptr;
};

struct FieldEntry {
    std::string   name;
    std::string   typeName;
    std::int32_t  offset = 0;          // normalized offset (-1 sentinel for thread-static/invalid)
    std::uint32_t flags  = 0;          // FieldAttributes (Static = 0x10, Literal = 0x40, etc.)
    // Phase 11.5 follow-up: enum / value-type detection so the inspector can
    // classify a field as Leaf (enum/primitive) vs ValueStruct (Vector3 etc.)
    // vs PointerToClass without re-parsing the runtime metadata. Both default
    // to false; populated when the resolver has the right exports bound.
    bool          isEnum             = false;
    bool          isValueType        = false;
    // For enum fields: the underlying integer type name (e.g.
    // "System.Int32"). Empty when isEnum=false or detection failed.
    std::string   underlyingTypeName;
    // Phase 31-2 (Phase B) — for an inline value-type STRUCT field (isValueType
    // && !isEnum, not a primitive), the field's runtime class, captured here
    // during ReadClassInfo so the snapshot / walkInstance decode can recurse
    // into its members WITHOUT re-resolving the type by name (which is
    // ambiguous for nested types). nullptr otherwise. Raw pointer to an
    // incomplete type — never owned, valid for the session.
    MonoClass*    valueTypeClass     = nullptr;
};

struct MethodEntry {
    std::string   name;
    std::uint32_t token = 0;
    std::uint32_t flags = 0;           // MethodAttributes
};

struct ClassInfo {
    ClassEntry               self;
    ClassEntry               parent;       // empty if no parent
    std::vector<FieldEntry>  fields;
    std::vector<MethodEntry> methods;
};

// Enumerates every type definition in `image`. The image must come from one of
// the AssemblyEntry results returned by EnumerateAssemblies().
std::vector<ClassEntry> EnumerateClasses(MonoImage* image);

// Looks up a class by namespace + name (use "" namespace for top-level types).
// Returns nullptr if not found.
MonoClass* FindClass(MonoImage* image, const std::string& ns, const std::string& name);

// Reads parent + fields + methods of a class. Returns std::nullopt when the
// class is null or runtime is unavailable (callers should map to pipe error).
struct ClassInfoResult {
    bool      ok = false;
    ClassInfo info;
};
// forceSetup (default true): when a class reports NO own fields (metadata not
// set up — common for never-instantiated types), force a one-shot
// mono_class_vtable() to populate the field table so the retry sees real
// fields. REQUIRED for the Inspector single-class drilldown.
//
// FREEZE WARNING: mono_class_vtable() from the pipe worker thread races the
// main-thread BDWGC GC and deadlocks the game. One call (single-class
// drilldown) is safe; calling it across thousands of never-set-up classes is
// NOT. BULK metadata scanners (searchSymbols / dumpClassInfo over every class
// in an assembly) MUST pass forceSetup=false — they accept that the rare
// not-set-up class shows no fields, in exchange for never touching the vtable.
// (Core Keeper Mono x64 froze on "Search all user assemblies" until this gate
// was added, 2026-06-06.)
ClassInfoResult ReadClassInfo(MonoClass* klass, bool forceSetup = true);

// Phase 29f (Layer B) — SEH-guarded mono_object_get_class on a raw managed
// object pointer. Returns nullptr if the resolver lacks the export, the
// pointer is null, or the call faults (a false-positive / garbage array
// slot). The snapshot reference-array inner-field capture uses this to
// resolve each element object's runtime class before walking its fields.
MonoClass* ClassOfObject(std::uintptr_t objPtr);

// Phase 29f (Layer B) — SEH-guarded sanity probe for a MonoClass* that came
// from an unverified source (e.g. object_get_class on a false-positive array
// slot). Returns true only when mono_class_get_name yields a non-empty name
// without faulting. Mirrors IL2CPP::ProbeKlassValid; callers gate the
// element-field walk on this so a garbage class can't reach the (unguarded)
// class_get_parent / field-walk path and crash the game.
bool ProbeKlassValid(MonoClass* klass);

// --- runtime ops (Phase 3 segment 3) ---------------------------------------

struct StaticFieldAddress {
    bool          ok      = false;
    void*         address = nullptr;   // base + field offset, ready to dereference
    std::int32_t  offset  = -1;        // normalized offset (-1 for thread-static / unresolved)
    std::string   typeName;
    std::uint32_t flags   = 0;
    std::string   reason;              // populated when !ok ("not_static" / "literal" / "thread_static" / ...)
};

// Resolves the in-process address of a static field. Returns ok=false if the
// field is not declared on `klass` or it is not actually static.
StaticFieldAddress ResolveStaticFieldAddress(MonoClass* klass, const std::string& fieldName);

// Phase 29a-v3 — batch helper for snapshot decode. Calls
// `mono_class_vtable(domain, klass)` + `mono_vtable_get_static_data(vt)`
// exactly ONCE per class and returns the static-fields base pointer. The
// caller then computes per-static-field addresses as `base + f.offset`
// using offsets already in hand from `ReadClassInfo`. Replaces the
// `ResolveStaticFieldAddress(k, name)` call-per-static-field pattern
// that was triggering tens of thousands of mono_class_vtable calls on
// large Mono targets (15K-class 活俠傳 snapshot → ~50K vtable calls →
// BDWGC pressure → game freeze). Returns nullptr when the class has no
// static storage or vtable resolution fails.
void* ResolveStaticBaseAddress(MonoClass* klass);

struct CompiledMethodAddress {
    bool          ok      = false;
    void*         address = nullptr;   // native code pointer after JIT
    std::string   name;
    std::uint32_t token   = 0;
};

// JIT-compiles the named method (or first overload) and returns the native
// entry-point. Wrapped in SEH; failure logs and returns ok=false.
CompiledMethodAddress CompileMethodByName(MonoClass* klass, const std::string& methodName);

// --- Phase 5 follow-up: enum decode -----------------------------------------

struct LiteralFieldEntry {
    std::string name;
    std::string typeName;        // e.g. "System.Int32"
    const void* data = nullptr;  // mono_field_get_data result; nullptr on failure
};

// Iterate every LITERAL (`const`) field on `klass`, returning each one's
// name, declared type, and the raw constant blob pointer (per ECMA-335:
// raw little-endian bytes of the appropriate width — no type prefix, no
// length prefix for non-string defaults). Empty vector if the runtime is
// unavailable, the class is null, or `mono_field_get_data` is unbound.
std::vector<LiteralFieldEntry> EnumerateLiteralFields(MonoClass* klass);

}  // namespace Discrete::Mono
