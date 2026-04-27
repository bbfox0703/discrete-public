// MonoResolver.h — resolves Mono runtime exports from mono-2.0-bdwgc.dll
// (or legacy mono.dll). Lazy bind: missing exports are logged but do not crash.
#pragma once

#include "MonoTypes.h"

#include <string>

namespace UnityCEDumper::Mono {

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
using fn_class_get_fields       = MonoClassField*(*)(MonoClass*, void** /*iter*/);
using fn_class_get_methods      = MonoMethod*    (*)(MonoClass*, void** /*iter*/);
using fn_field_get_name         = const char*    (*)(MonoClassField*);
using fn_field_get_type         = MonoType*      (*)(MonoClassField*);
using fn_field_get_offset       = int            (*)(MonoClassField*);
using fn_field_get_flags        = std::uint32_t  (*)(MonoClassField*);
using fn_method_get_name        = const char*    (*)(MonoMethod*);
using fn_method_get_token       = std::uint32_t  (*)(MonoMethod*);
using fn_method_get_flags       = std::uint32_t  (*)(MonoMethod*, std::uint32_t* /*iflags*/);
using fn_type_get_name          = char*          (*)(MonoType*);
using fn_free                   = void           (*)(void*);

// --- runtime ops (Phase 3 segment 3) ---------------------------------------
using fn_class_vtable           = MonoVTable*    (*)(MonoDomain*, MonoClass*);
using fn_vtable_get_static_data = void*          (*)(MonoVTable*);
using fn_compile_method         = void*          (*)(MonoMethod*);

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
    fn_class_get_fields       class_get_fields       = nullptr;
    fn_class_get_methods      class_get_methods      = nullptr;
    fn_field_get_name         field_get_name         = nullptr;
    fn_field_get_type         field_get_type         = nullptr;
    fn_field_get_offset       field_get_offset       = nullptr;
    fn_field_get_flags        field_get_flags        = nullptr;
    fn_method_get_name        method_get_name        = nullptr;
    fn_method_get_token       method_get_token       = nullptr;
    fn_method_get_flags       method_get_flags       = nullptr;
    fn_type_get_name          type_get_name          = nullptr;
    fn_free                   free                   = nullptr;

    // Runtime ops — segment 3.
    fn_class_vtable           class_vtable           = nullptr;
    fn_vtable_get_static_data vtable_get_static_data = nullptr;
    fn_compile_method         compile_method         = nullptr;
};

// Singleton accessor. First call probes the loaded mono module and binds every
// export. Subsequent calls return the cached resolver. Always non-null.
const Resolver& GetResolver();

// Returns true if every "must-have" export was bound. Currently all exports
// listed above are mandatory for assembly enumeration.
bool IsResolved();

}  // namespace UnityCEDumper::Mono
