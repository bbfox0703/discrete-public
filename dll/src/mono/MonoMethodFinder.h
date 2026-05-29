// MonoMethodFinder.h — Phase 18 findMethodInstances backend (Mono).
// Enumerates every overload of a named method on a class, decodes each
// signature (param types, return type) via the mono_signature_get_* exports
// added in 18a-1, and JIT-compiles each match to expose its native code
// address for downstream registerSymbol / AOB scan use.
#pragma once

#include "MonoTypes.h"

#include <cstdint>
#include <string>
#include <vector>

namespace Discrete::Mono {

struct MethodOverload {
    std::string   name;
    std::uint32_t token = 0;
    std::uint32_t flags = 0;            // MethodAttributes
    std::uint32_t iflags = 0;           // MethodImplAttributes
    bool          isStatic   = false;
    bool          isVirtual  = false;
    bool          isAbstract = false;
    bool          compiled   = false;   // false when compile_method failed
    void*         address = nullptr;    // JIT native code pointer
    std::vector<std::string> paramTypes;
    std::vector<std::string> paramNames; // empty when method_get_param_names unbound
    std::string   returnType;
    std::string   declaringClass;       // "Outer/Inner" form for nested types
    std::string   declaringNamespace;
    std::string   signatureString;      // "(System.Single, System.Single, System.Single)"
    std::string   humanSuffix;          // symbol-safe, e.g. "3float"
};

struct FindMethodResult {
    bool ok = false;
    std::string error;
    std::vector<MethodOverload> matches;
};

// Enumerate every overload of `methodName` declared on `klass`. Optional
// `signatureMatch` is a case-sensitive substring filter applied to the
// rendered signatureString — handy for narrowing by parameter type when a
// caller already knows part of the signature (e.g. "Vector3" to pick the
// Vector3-arg Set over the 3-float Set).
//
// Each match is JIT-compiled (via mono_compile_method) so callers get a real
// native address back. JIT failures don't abort the whole result — the
// individual MethodOverload just carries `compiled=false, address=nullptr`.
FindMethodResult FindMethodInstances(MonoClass* klass,
                                     const std::string& methodName,
                                     const std::string& signatureMatch);

}  // namespace Discrete::Mono
