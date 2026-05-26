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
    // Phase 11.5 follow-up: enum / value-type detection — see Mono walker
    // for the full rationale. Defaults to false; populated only when the
    // type → class bridge (il2cpp_class_from_il2cpp_type) is available.
    bool          isEnum             = false;
    bool          isValueType        = false;
    std::string   underlyingTypeName;
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

/// SEH-guarded sanity probe used by callers that resolve an
/// <c>Il2CppClass*</c> from an unverified source — e.g. the first qword of
/// an object header (klass-from-header expand path). Returns true when
/// <c>il2cpp_class_get_name(klass)</c> yields a non-empty C string without
/// access-violating. Falls back to false on any SEH or null-export.
bool ProbeKlassValid(Il2CppClass* klass);

struct StaticFieldAddress {
    bool          ok      = false;
    void*         address = nullptr;
    std::size_t   offset  = 0;
    std::string   typeName;
    std::uint32_t flags   = 0;
};
StaticFieldAddress ResolveStaticFieldAddress(Il2CppClass* klass, const std::string& fieldName);

/// Phase 29a-v3 — batch static-base resolver. Mirror of
/// `Mono::ResolveStaticBaseAddress`. Returns nullptr when the class has
/// no static storage or the resolver isn't bound. Caller computes
/// per-field addresses as `base + f.offset` using offsets already in
/// hand from <see cref="ReadClassInfo"/>.
void* ResolveStaticBaseAddress(Il2CppClass* klass);

struct MethodPointer {
    bool          ok      = false;
    void*         address = nullptr;
    std::string   name;
    std::uint32_t token   = 0;
};
MethodPointer GetMethodPointerByName(Il2CppClass* klass, const std::string& methodName);

// Bulk enumeration used by dumpIl2cppSymbols. Like ReadClassInfo's method
// pass but additionally reads each method's parameter types so the caller
// can compute a symbol-safe overload-disambiguating suffix via
// shared/MethodSignatureFormat.h::MakeHumanSuffix. Heavier than
// ReadClassInfo (one runtime param-type call per parameter) so we only
// pay it in bulk-dump paths the user explicitly invokes.
struct MethodSignatureEntry {
    std::string              name;
    std::uint32_t            token       = 0;
    std::uint32_t            flags       = 0;
    void*                    pointer     = nullptr;  // AOT native code (nullptr for abstract / uncompiled generics)
    std::vector<std::string> paramTypes;
    std::string              humanSuffix;
};
std::vector<MethodSignatureEntry> EnumerateMethodSignatures(Il2CppClass* klass);

}  // namespace UnityCEDumper::IL2CPP
