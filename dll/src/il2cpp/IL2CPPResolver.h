// IL2CPPResolver.h — resolves IL2CPP runtime exports from GameAssembly.dll.
// Lazy bind: missing exports are logged but do not crash.
#pragma once

#include "IL2CPPTypes.h"

#include <cstddef>
#include <cstdint>
#include <string>

namespace UnityCEDumper::IL2CPP {

using fn_domain_get             = Il2CppDomain*   (*)();
using fn_thread_attach          = Il2CppThread*   (*)(Il2CppDomain*);
using fn_thread_detach          = void            (*)(Il2CppThread*);
using fn_domain_get_assemblies  = const Il2CppAssembly** (*)(Il2CppDomain*, std::size_t* /*size*/);
using fn_assembly_get_image     = const Il2CppImage* (*)(const Il2CppAssembly*);
using fn_image_get_name         = const char*     (*)(const Il2CppImage*);
using fn_image_get_filename     = const char*     (*)(const Il2CppImage*);
using fn_image_get_class_count  = std::size_t     (*)(const Il2CppImage*);
using fn_image_get_class        = Il2CppClass*    (*)(const Il2CppImage*, std::size_t /*index*/);
using fn_class_from_name        = Il2CppClass*    (*)(const Il2CppImage*, const char* /*ns*/, const char* /*name*/);
using fn_class_get_name         = const char*     (*)(Il2CppClass*);
using fn_class_get_namespace    = const char*     (*)(Il2CppClass*);
using fn_class_get_parent       = Il2CppClass*    (*)(Il2CppClass*);
using fn_class_get_image        = const Il2CppImage* (*)(Il2CppClass*);
using fn_class_get_declaring    = Il2CppClass*    (*)(Il2CppClass*);
using fn_class_get_nested_types = Il2CppClass*    (*)(Il2CppClass*, void** /*iter*/);
using fn_class_get_fields       = FieldInfo*      (*)(Il2CppClass*, void** /*iter*/);
using fn_class_get_methods      = const MethodInfo* (*)(Il2CppClass*, void** /*iter*/);
using fn_class_get_static_data  = void*           (*)(Il2CppClass*);
using fn_class_get_element_class = Il2CppClass*  (*)(Il2CppClass*);
using fn_class_value_size       = int             (*)(Il2CppClass*, std::uint32_t* /*align*/);
using fn_class_is_valuetype     = int             (*)(Il2CppClass*);
using fn_field_get_name         = const char*     (*)(FieldInfo*);
using fn_field_get_type         = const Il2CppType* (*)(FieldInfo*);
using fn_field_get_offset       = std::size_t     (*)(FieldInfo*);
using fn_field_get_flags        = int             (*)(FieldInfo*);
using fn_method_get_name        = const char*     (*)(const MethodInfo*);
using fn_method_get_token       = std::uint32_t   (*)(const MethodInfo*);
using fn_method_get_flags       = std::uint32_t   (*)(const MethodInfo*, std::uint32_t* /*iflags*/);
using fn_method_get_pointer     = void*           (*)(const MethodInfo*);
using fn_type_get_name          = char*           (*)(const Il2CppType*);
using fn_free                   = void            (*)(void*);

struct Resolver {
    bool         resolved = false;
    std::wstring moduleName;        // L"GameAssembly.dll"

    fn_domain_get             domain_get             = nullptr;
    fn_thread_attach          thread_attach          = nullptr;
    fn_thread_detach          thread_detach          = nullptr;
    fn_domain_get_assemblies  domain_get_assemblies  = nullptr;
    fn_assembly_get_image     assembly_get_image     = nullptr;
    fn_image_get_name         image_get_name         = nullptr;
    fn_image_get_filename     image_get_filename     = nullptr;
    fn_image_get_class_count  image_get_class_count  = nullptr;
    fn_image_get_class        image_get_class        = nullptr;
    fn_class_from_name        class_from_name        = nullptr;
    fn_class_get_name         class_get_name         = nullptr;
    fn_class_get_namespace    class_get_namespace    = nullptr;
    fn_class_get_parent       class_get_parent       = nullptr;
    fn_class_get_image        class_get_image        = nullptr;
    fn_class_get_declaring    class_get_declaring    = nullptr;
    fn_class_get_nested_types class_get_nested_types = nullptr;
    fn_class_get_fields       class_get_fields       = nullptr;
    fn_class_get_methods      class_get_methods      = nullptr;
    fn_class_get_static_data  class_get_static_data  = nullptr;
    fn_class_get_element_class class_get_element_class = nullptr;
    fn_class_value_size       class_value_size       = nullptr;
    fn_class_is_valuetype     class_is_valuetype     = nullptr;
    fn_field_get_name         field_get_name         = nullptr;
    fn_field_get_type         field_get_type         = nullptr;
    fn_field_get_offset       field_get_offset       = nullptr;
    fn_field_get_flags        field_get_flags        = nullptr;
    fn_method_get_name        method_get_name        = nullptr;
    fn_method_get_token       method_get_token       = nullptr;
    fn_method_get_flags       method_get_flags       = nullptr;
    fn_method_get_pointer     method_get_pointer     = nullptr;
    fn_type_get_name          type_get_name          = nullptr;
    fn_free                   free                   = nullptr;
};

const Resolver& GetResolver();
bool IsResolved();

}  // namespace UnityCEDumper::IL2CPP
