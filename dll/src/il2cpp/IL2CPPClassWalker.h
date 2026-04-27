// IL2CPPClassWalker.h — class / field / method enumeration on top of
// IL2CPPResolver. API mirrors mono/MonoClassWalker.h but the underlying calls
// hit GameAssembly.dll directly (no metadata-table walk required: IL2CPP gives
// us il2cpp_image_get_class_count + il2cpp_image_get_class).
#pragma once

#include "IL2CPPTypes.h"

#include <cstdint>
#include <string>
#include <vector>

namespace UnityCEDumper::IL2CPP {

struct ClassEntry {
    std::string  ns;
    std::string  name;
    Il2CppClass* klass = nullptr;
};

struct FieldEntry {
    std::string   name;
    std::string   typeName;
    std::size_t   offset = 0;
    std::uint32_t flags  = 0;
};

struct MethodEntry {
    std::string   name;
    std::uint32_t token   = 0;
    std::uint32_t flags   = 0;
    void*         pointer = nullptr;   // AOT native code address
};

struct ClassInfo {
    ClassEntry               self;
    ClassEntry               parent;
    std::vector<FieldEntry>  fields;
    std::vector<MethodEntry> methods;
};

struct ClassInfoResult {
    bool      ok = false;
    ClassInfo info;
};

std::vector<ClassEntry> EnumerateClasses(const Il2CppImage* image);
Il2CppClass*            FindClass(const Il2CppImage* image,
                                  const std::string& ns, const std::string& name);
ClassInfoResult         ReadClassInfo(Il2CppClass* klass);

struct StaticFieldAddress {
    bool          ok      = false;
    void*         address = nullptr;
    std::size_t   offset  = 0;
    std::string   typeName;
    std::uint32_t flags   = 0;
};
StaticFieldAddress ResolveStaticFieldAddress(Il2CppClass* klass, const std::string& fieldName);

struct MethodPointer {
    bool          ok      = false;
    void*         address = nullptr;
    std::string   name;
    std::uint32_t token   = 0;
};
MethodPointer GetMethodPointerByName(Il2CppClass* klass, const std::string& methodName);

}  // namespace UnityCEDumper::IL2CPP
