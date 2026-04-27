// MonoClassWalker.h — class / field / method enumeration on top of MonoResolver.
#pragma once

#include "MonoTypes.h"

#include <cstdint>
#include <string>
#include <vector>

namespace UnityCEDumper::Mono {

struct ClassEntry {
    std::string ns;     // namespace ("" for global)
    std::string name;
    std::uint32_t token = 0;
    MonoClass*  klass = nullptr;
};

struct FieldEntry {
    std::string   name;
    std::string   typeName;
    int           offset = 0;          // offset from MonoObject start (raw)
    std::uint32_t flags  = 0;          // FieldAttributes (Static = 0x10, etc.)
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
ClassInfoResult ReadClassInfo(MonoClass* klass);

// --- runtime ops (Phase 3 segment 3) ---------------------------------------

struct StaticFieldAddress {
    bool          ok      = false;
    void*         address = nullptr;   // base + field offset, ready to dereference
    int           offset  = 0;         // raw offset reported by mono_field_get_offset
    std::string   typeName;
    std::uint32_t flags   = 0;
};

// Resolves the in-process address of a static field. Returns ok=false if the
// field is not declared on `klass` or it is not actually static.
StaticFieldAddress ResolveStaticFieldAddress(MonoClass* klass, const std::string& fieldName);

struct CompiledMethodAddress {
    bool          ok      = false;
    void*         address = nullptr;   // native code pointer after JIT
    std::string   name;
    std::uint32_t token   = 0;
};

// JIT-compiles the named method (or first overload) and returns the native
// entry-point. Wrapped in SEH; failure logs and returns ok=false.
CompiledMethodAddress CompileMethodByName(MonoClass* klass, const std::string& methodName);

}  // namespace UnityCEDumper::Mono
