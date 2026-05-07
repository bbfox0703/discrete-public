// IL2CPPMethodFinder.h — Phase 18 findMethodInstances backend (IL2CPP).
// Mirror of mono/MonoMethodFinder.h, but the underlying calls hit
// GameAssembly.dll directly. AOT means there's no JIT step — the native
// address comes from MethodInfo->methodPointer (or il2cpp_method_get_pointer
// where the export is bound).
#pragma once

#include "IL2CPPTypes.h"

#include <cstdint>
#include <string>
#include <vector>

namespace UnityCEDumper::IL2CPP {

struct MethodOverload {
    std::string   name;
    std::uint32_t token = 0;
    std::uint32_t flags = 0;            // MethodAttributes
    std::uint32_t iflags = 0;           // MethodImplAttributes
    bool          isStatic   = false;
    bool          isVirtual  = false;
    bool          isAbstract = false;
    bool          compiled   = false;   // false when ReadMethodPointer returned null
    void*         address = nullptr;    // AOT native code pointer
    std::vector<std::string> paramTypes;
    std::vector<std::string> paramNames; // empty when method_get_param_name unbound
    std::string   returnType;
    std::string   declaringClass;
    std::string   declaringNamespace;
    std::string   signatureString;
    std::string   humanSuffix;
};

struct FindMethodResult {
    bool ok = false;
    std::string error;
    std::vector<MethodOverload> matches;
};

FindMethodResult FindMethodInstances(Il2CppClass* klass,
                                     const std::string& methodName,
                                     const std::string& signatureMatch);

}  // namespace UnityCEDumper::IL2CPP
