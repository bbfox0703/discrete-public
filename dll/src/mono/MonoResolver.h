// MonoResolver.h — resolves Mono runtime exports from mono-2.0-bdwgc.dll
// (or legacy mono.dll). Lazy bind: missing exports are logged but do not crash.
#pragma once

#include "MonoTypes.h"

#include <string>

namespace Discrete::Mono {

// Standard glib-style iterator callback used by Mono's *_foreach exports.
using GFunc = void (*)(void* item, void* user_data);

// Function pointer typedefs for every export we need.
// Add new ones at the bottom and bump RequiredCount() / Resolve() accordingly.
using fn_get_root_domain        = MonoDomain*    (*)();
using fn_domain_get             = MonoDomain*    (*)();
using fn_thread_attach          = MonoThread*    (*)(MonoDomain*);
using fn_thread_detach          = void           (*)(MonoThread*);
using fn_assembly_foreach       = void           (*)(GFunc, void*);
using fn_assembly_get_image     = MonoImage*     (*)(MonoAssembly*);
using fn_assembly_get_name      = MonoAssemblyName* (*)(MonoAssembly*);
using fn_assembly_name_get_name = const char*    (*)(MonoAssemblyName*);
using fn_image_get_name         = const char*    (*)(MonoImage*);
using fn_image_get_filename     = const char*    (*)(MonoImage*);

// --- class / field / method walking (Phase 3 segment 2) ---------------------
struct MonoTableInfo;
using fn_image_get_table_info   = const MonoTableInfo* (*)(MonoImage*, int);
using fn_table_info_get_rows    = int            (*)(const MonoTableInfo*);
using fn_class_get              = MonoClass*     (*)(MonoImage*, std::uint32_t /*token*/);
using fn_class_from_name        = MonoClass*     (*)(MonoImage*, const char* /*ns*/, const char* /*name*/);
using fn_class_get_name         = const char*    (*)(MonoClass*);
using fn_class_get_namespace    = const char*    (*)(MonoClass*);
using fn_class_get_parent       = MonoClass*     (*)(MonoClass*);
using fn_class_get_image        = MonoImage*     (*)(MonoClass*);
using fn_class_get_nesting_type = MonoClass*     (*)(MonoClass*);
using fn_class_get_nested_types = MonoClass*     (*)(MonoClass*, void** /*iter*/);
using fn_class_get_fields       = MonoClassField*(*)(MonoClass*, void** /*iter*/);
using fn_class_get_methods      = MonoMethod*    (*)(MonoClass*, void** /*iter*/);
using fn_field_get_name         = const char*    (*)(MonoClassField*);
using fn_field_get_type         = MonoType*      (*)(MonoClassField*);
using fn_field_get_offset       = int            (*)(MonoClassField*);
using fn_field_get_flags        = std::uint32_t  (*)(MonoClassField*);
using fn_method_get_name        = const char*    (*)(MonoMethod*);
using fn_method_get_token       = std::uint32_t  (*)(MonoMethod*);
using fn_method_get_flags       = std::uint32_t  (*)(MonoMethod*, std::uint32_t* /*iflags*/);
// Phase 18 — method signature decoding. mono_method_signature returns the
// per-method signature handle; the *_signature_get_* family iterates params
// and returns the return type. mono_method_get_param_names is optional and
// fills a caller-supplied array of `const char*` (one slot per declared
// parameter) — older Mono builds may not export it.
using fn_method_signature       = MonoMethodSignature* (*)(MonoMethod*);
using fn_signature_get_param_count = std::uint32_t (*)(MonoMethodSignature*);
using fn_signature_get_params   = MonoType*      (*)(MonoMethodSignature*, void** /*iter*/);
using fn_signature_get_return_type = MonoType*   (*)(MonoMethodSignature*);
using fn_method_get_param_names = void           (*)(MonoMethod*, const char** /*names*/);
using fn_type_get_name          = char*          (*)(MonoType*);
using fn_free                   = void           (*)(void*);

// --- runtime ops (Phase 3 segment 3) ---------------------------------------
using fn_class_vtable           = MonoVTable*    (*)(MonoDomain*, MonoClass*);
using fn_vtable_get_static_data = void*          (*)(MonoVTable*);
using fn_compile_method         = void*          (*)(MonoMethod*);

// --- Mono enum decode (Phase 5 follow-up) -----------------------------------
using fn_field_get_data         = const char*    (*)(MonoClassField*);
using fn_class_is_enum          = int            (*)(MonoClass*);
using fn_class_enum_basetype    = MonoType*      (*)(MonoClass*);
// MonoType -> MonoClass bridge. Lets the field walker detect enum / value-type
// on a field's declared type without re-parsing the type name. Optional: when
// unbound, walkInstance falls back to its existing catch-all decode.
using fn_class_from_mono_type   = MonoClass*     (*)(MonoType*);

// --- Mono array element metadata (Phase 10 follow-up) ----------------------
using fn_class_get_element_class = MonoClass*    (*)(MonoClass*);
using fn_class_get_rank         = int            (*)(MonoClass*);
using fn_class_value_size       = int            (*)(MonoClass*, std::uint32_t* /*align*/);
using fn_class_instance_size    = std::int32_t   (*)(MonoClass*);
using fn_class_is_valuetype     = int            (*)(MonoClass*);

// --- Phase 19 managed-method invocation -------------------------------------
// `mono_runtime_invoke` boxes the return value (or returns NULL for void) and
// captures any managed exception in `*exc`. `mono_object_unbox` returns a
// pointer to the unboxed bytes for value-type returns. `mono_object_get_class`
// is the bridge from a boxed object to its class — used to resolve the
// exception's type name. String-related entries are bound now even though
// 19b doesn't use them (19c needs them for string args / string return
// UTF-16 → UTF-8 decode).
using fn_runtime_invoke         = MonoObject*    (*)(MonoMethod*, void* /*obj*/, void** /*params*/, MonoObject** /*exc*/);
using fn_object_unbox           = void*          (*)(MonoObject*);
using fn_object_get_class       = MonoClass*     (*)(MonoObject*);
using fn_string_new             = MonoString*    (*)(MonoDomain*, const char* /*utf8*/);
using fn_string_chars           = std::uint16_t* (*)(MonoString*);
using fn_string_length          = std::int32_t   (*)(MonoString*);
// B-M2 — GC handle pinning for managed objects allocated during callMethod
// argument marshaling (e.g. mono_string_new). Optional: falls back to stack
// rooting when unbound.
using fn_gchandle_new           = std::uint32_t  (*)(MonoObject*, int /*pinned*/);
using fn_gchandle_free          = void           (*)(std::uint32_t /*gchandle*/);

// --- Phase 28 — IL body access for findFieldReferences --------------------
// `mono_method_get_header` returns the per-method IL header (callable on
// methods with a managed body — abstract / pinvoke / icall methods return
// null). `mono_method_header_get_code` extracts the IL byte pointer plus
// the bytecode length via the out-param. `mono_metadata_free_mh` releases
// headers created from inflated/instantiated methods — optional, the
// per-method leak is small. `mono_class_get_field_token` returns the
// metadata token (0x04xxxxxx) of a MonoClassField — used to identify the
// target field when scanning IL operand immediates.
using fn_method_get_header      = MonoMethodHeader* (*)(MonoMethod*);
using fn_method_header_get_code = const unsigned char* (*)(MonoMethodHeader*, std::uint32_t* /*code_size*/, std::uint32_t* /*max_stack*/);
using fn_metadata_free_mh       = void           (*)(MonoMethodHeader*);
using fn_class_get_field_token  = std::uint32_t  (*)(MonoClassField*);
// B-M8 — MemberRef (0x0Axxxxxx) resolution for generic / cross-assembly field
// references in Mono IL bodies. Optional: missing on minimal builds just falls
// back to FieldDef-only matching with an advisory note on generic classes.
using fn_field_from_token       = MonoClassField* (*)(MonoImage*, std::uint32_t /*token*/, MonoClass** /*retklass*/, void* /*generic_context*/);
using fn_field_get_parent       = MonoClass*     (*)(MonoClassField*);

// --- Disconnect-time GC drain (build #547 follow-up) ----------------------
// `mono_gc_collect(int generation)` triggers a Mono / BDWGC collection
// cycle. Generation -1 means "full collection (max generation)". We call
// this from the PipeServer disconnect hook so finalizers queued during a
// long-running batch RPC (Phase 28 IL walker over 2000+ methods is the
// canonical trigger) drain incrementally while DiscreteUI / the bridge is
// still attached, instead of all-at-once at game shutdown (observed
// symptom: BongoCat shutdown 10-30s post-findFieldReferences).
// Optional binding — older Mono builds may not export it; the disconnect
// hook silently no-ops when unbound.
using fn_gc_collect             = void           (*)(int /*generation, -1 = full*/);

// --- Phase 32 — live-object enumeration (Unity Mono / bdwgc) ----------------
// `mono_gc_walk_heap(flags, callback, data)` is a public MONO_API profiler
// entry: it stops the world, walks every LIVE managed object, and invokes the
// callback once per object with the object pointer + its MonoClass*. We use it
// for findInstances' opt-in `liveOnly` mode — it returns ONLY live objects, so
// it skips the dead/uncollected shells and runtime-bookkeeping false positives
// that the raw pointer scan picks up. Optional binding: non-Unity / older Mono
// builds may not export it, in which case liveOnly silently falls back to the
// raw scan. `mono_object_get_size` returns an object's allocated size (bound
// for future object-extent validation; not required by liveOnly).
using fn_gc_references_cb       = int            (*)(MonoObject* /*obj*/, MonoClass* /*klass*/,
                                                     std::uintptr_t /*size*/, std::uintptr_t /*num*/,
                                                     MonoObject** /*refs*/, std::uintptr_t* /*offsets*/,
                                                     void* /*data*/);
using fn_gc_walk_heap           = void           (*)(int /*flags*/, fn_gc_references_cb /*cb*/, void* /*data*/);
using fn_object_get_size        = std::uint32_t  (*)(MonoObject*);

// MONO_TABLE_TYPEDEF row index in a MonoImage; pass to image_get_table_info.
inline constexpr int          MonoTableTypedef = 2;
// CLI metadata token base for TypeDef rows.
inline constexpr std::uint32_t MonoTokenTypeDef = 0x02000000;

struct Resolver {
    bool         resolved = false;
    std::wstring moduleName;        // L"mono-2.0-bdwgc.dll" or L"mono.dll"

    fn_get_root_domain        get_root_domain        = nullptr;
    fn_domain_get             domain_get             = nullptr;
    fn_thread_attach          thread_attach          = nullptr;
    fn_thread_detach          thread_detach          = nullptr;
    fn_assembly_foreach       assembly_foreach       = nullptr;
    fn_assembly_get_image     assembly_get_image     = nullptr;
    fn_assembly_get_name      assembly_get_name      = nullptr;
    fn_assembly_name_get_name assembly_name_get_name = nullptr;
    fn_image_get_name         image_get_name         = nullptr;
    fn_image_get_filename     image_get_filename     = nullptr;

    // Class / field / method walking — segment 2.
    fn_image_get_table_info   image_get_table_info   = nullptr;
    fn_table_info_get_rows    table_info_get_rows    = nullptr;
    fn_class_get              class_get              = nullptr;
    fn_class_from_name        class_from_name        = nullptr;
    fn_class_get_name         class_get_name         = nullptr;
    fn_class_get_namespace    class_get_namespace    = nullptr;
    fn_class_get_parent       class_get_parent       = nullptr;
    fn_class_get_image        class_get_image        = nullptr;
    fn_class_get_nesting_type class_get_nesting_type = nullptr;
    fn_class_get_nested_types class_get_nested_types = nullptr;
    fn_class_get_fields       class_get_fields       = nullptr;
    fn_class_get_methods      class_get_methods      = nullptr;
    fn_field_get_name         field_get_name         = nullptr;
    fn_field_get_type         field_get_type         = nullptr;
    fn_field_get_offset       field_get_offset       = nullptr;
    fn_field_get_flags        field_get_flags        = nullptr;
    fn_method_get_name        method_get_name        = nullptr;
    fn_method_get_token       method_get_token       = nullptr;
    fn_method_get_flags       method_get_flags       = nullptr;
    // Phase 18 — method signature decoding (param types + return type).
    fn_method_signature           method_signature           = nullptr;
    fn_signature_get_param_count  signature_get_param_count  = nullptr;
    fn_signature_get_params       signature_get_params       = nullptr;
    fn_signature_get_return_type  signature_get_return_type  = nullptr;
    fn_method_get_param_names     method_get_param_names     = nullptr;
    fn_type_get_name          type_get_name          = nullptr;
    fn_free                   free                   = nullptr;

    // Runtime ops — segment 3.
    fn_class_vtable           class_vtable           = nullptr;
    fn_vtable_get_static_data vtable_get_static_data = nullptr;
    fn_compile_method         compile_method         = nullptr;

    // Enum decode — Phase 5 follow-up.
    fn_field_get_data         field_get_data         = nullptr;
    fn_class_is_enum          class_is_enum          = nullptr;
    fn_class_enum_basetype    class_enum_basetype    = nullptr;
    fn_class_from_mono_type   class_from_mono_type   = nullptr;
    fn_class_get_element_class class_get_element_class = nullptr;
    fn_class_get_rank         class_get_rank         = nullptr;
    fn_class_value_size       class_value_size       = nullptr;
    fn_class_instance_size    class_instance_size    = nullptr;
    fn_class_is_valuetype     class_is_valuetype     = nullptr;

    // Phase 19 — managed-method invocation.
    fn_runtime_invoke         runtime_invoke         = nullptr;
    fn_object_unbox           object_unbox           = nullptr;
    fn_object_get_class       object_get_class       = nullptr;
    fn_string_new             string_new             = nullptr;
    fn_string_chars           string_chars           = nullptr;
    fn_string_length          string_length          = nullptr;
    fn_gchandle_new           gchandle_new           = nullptr;
    fn_gchandle_free          gchandle_free          = nullptr;

    // Phase 28 — IL body access for findFieldReferences.
    fn_method_get_header      method_get_header      = nullptr;
    fn_method_header_get_code method_header_get_code = nullptr;
    fn_metadata_free_mh       metadata_free_mh       = nullptr;
    fn_class_get_field_token  class_get_field_token  = nullptr;
    fn_field_from_token       field_from_token       = nullptr;
    fn_field_get_parent       field_get_parent       = nullptr;

    // Disconnect-time GC drain — see header comment on fn_gc_collect.
    fn_gc_collect             gc_collect             = nullptr;

    // Phase 32 — live-object enumeration (findInstances liveOnly mode).
    fn_gc_walk_heap           gc_walk_heap           = nullptr;
    fn_object_get_size        object_get_size        = nullptr;
};

// Singleton accessor. First call probes the loaded mono module and binds every
// export. Subsequent calls return the cached resolver. Always non-null.
const Resolver& GetResolver();

// Returns true if every "must-have" export was bound. Currently all exports
// listed above are mandatory for assembly enumeration.
bool IsResolved();

}  // namespace Discrete::Mono
