// IL2CPPMethodFinder.cpp — Phase 18 findMethodInstances (IL2CPP).

#include "IL2CPPMethodFinder.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/MethodSignatureFormat.h"
#include "IL2CPPClassWalker.h"
#include "IL2CPPDomain.h"
#include "IL2CPPResolver.h"
#include "IL2CPPThreadAttach.h"
#include "IL2CPPTypeParser.h"

#include <windows.h>

#include <cstring>

namespace Discrete::IL2CPP {

namespace {

const char* SafeStr(const char* s) { return s ? s : ""; }

#pragma warning(push)
#pragma warning(disable: 4509)
const MethodInfo* SehNextMethod(fn_class_get_methods fn, Il2CppClass* k, void** iter) {
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehMethodName(fn_method_get_name fn, const MethodInfo* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
std::uint32_t SehMethodToken(fn_method_get_token fn, const MethodInfo* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0u; }
}
std::uint32_t SehMethodFlags(fn_method_get_flags fn, const MethodInfo* m, std::uint32_t* iflags) {
    __try { return fn(m, iflags); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0u; }
}
std::uint32_t SehParamCount(fn_method_get_param_count fn, const MethodInfo* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0u; }
}
const Il2CppType* SehParamType(fn_method_get_param fn, const MethodInfo* m, std::uint32_t i) {
    __try { return fn(m, i); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehParamName(fn_method_get_param_name fn, const MethodInfo* m, std::uint32_t i) {
    __try { return fn(m, i); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const Il2CppType* SehReturnType(fn_method_get_return_type fn, const MethodInfo* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* SehMethodPtr(fn_method_get_pointer fn, const MethodInfo* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* SehMethodPtrFromStruct(const MethodInfo* m) {
    __try { return *reinterpret_cast<void* const*>(m); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehClassName(fn_class_get_name fn, Il2CppClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehClassNs(fn_class_get_namespace fn, Il2CppClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
#pragma warning(pop)

// Mirror of IL2CPPClassWalker.cpp::ReadMethodPointer. Duplicated because
// that copy lives in an anonymous namespace and the convention across this
// codebase is one SEH-leaf set per .cpp.
void* ReadMethodPointer(const MethodInfo* m) {
    if (m == nullptr) return nullptr;
    const auto& r = GetResolver();
    if (r.method_get_pointer != nullptr) {
        if (void* p = SehMethodPtr(r.method_get_pointer, m)) return p;
    }
    return SehMethodPtrFromStruct(m);
}

constexpr std::uint32_t kMethodStatic   = 0x0010u;
constexpr std::uint32_t kMethodVirtual  = 0x0040u;
constexpr std::uint32_t kMethodAbstract = 0x0400u;

}  // namespace

FindMethodResult FindMethodInstances(Il2CppClass* klass,
                                     const std::string& methodName,
                                     const std::string& signatureMatch) {
    FindMethodResult result;
    if (klass == nullptr) {
        result.error = "klass is null";
        return result;
    }
    if (methodName.empty()) {
        result.error = "method name is empty";
        return result;
    }

    const auto& r = GetResolver();
    if (!r.resolved || r.class_get_methods == nullptr || r.method_get_name == nullptr) {
        result.error = "il2cpp runtime not resolved";
        return result;
    }

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) {
        result.error = "no il2cpp domain";
        return result;
    }
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) {
        result.error = "il2cpp_thread_attach failed";
        return result;
    }

    std::string classDisplay;
    std::string classNs;
    if (r.class_get_name != nullptr) {
        if (const char* nm = SehClassName(r.class_get_name, klass)) classDisplay = nm;
    }
    if (r.class_get_namespace != nullptr) {
        if (const char* ns = SehClassNs(r.class_get_namespace, klass)) classNs = ns;
    }

    void* iter = nullptr;
    for (;;) {
        const MethodInfo* m = SehNextMethod(r.class_get_methods, klass, &iter);
        if (m == nullptr) break;

        const char* nameC = SehMethodName(r.method_get_name, m);
        if (nameC == nullptr || methodName != nameC) continue;

        MethodOverload ov;
        ov.name = nameC;
        ov.declaringClass     = classDisplay;
        ov.declaringNamespace = classNs;

        if (r.method_get_token != nullptr) {
            ov.token = SehMethodToken(r.method_get_token, m);
        }
        if (r.method_get_flags != nullptr) {
            std::uint32_t iflags = 0;
            ov.flags  = SehMethodFlags(r.method_get_flags, m, &iflags);
            ov.iflags = iflags;
            ov.isStatic   = (ov.flags & kMethodStatic)   != 0u;
            ov.isVirtual  = (ov.flags & kMethodVirtual)  != 0u;
            ov.isAbstract = (ov.flags & kMethodAbstract) != 0u;
        }

        // Decode signature when the optional exports are bound.
        std::uint32_t paramCount = 0;
        if (r.method_get_param_count != nullptr) {
            paramCount = SehParamCount(r.method_get_param_count, m);
        }
        if (paramCount > 0 && r.method_get_param != nullptr) {
            ov.paramTypes.reserve(paramCount);
            for (std::uint32_t i = 0; i < paramCount; ++i) {
                const Il2CppType* pt = SehParamType(r.method_get_param, m, i);
                ov.paramTypes.push_back(pt ? TypeName(pt) : std::string{});
            }
        }
        if (paramCount > 0 && r.method_get_param_name != nullptr) {
            ov.paramNames.reserve(paramCount);
            for (std::uint32_t i = 0; i < paramCount; ++i) {
                const char* pn = SehParamName(r.method_get_param_name, m, i);
                ov.paramNames.emplace_back(SafeStr(pn));
            }
        }
        if (r.method_get_return_type != nullptr) {
            if (const Il2CppType* rt = SehReturnType(r.method_get_return_type, m)) {
                ov.returnType = TypeName(rt);
            }
        }

        ov.signatureString = Shared::MakeSignatureString(ov.paramTypes);
        ov.humanSuffix     = Shared::MakeHumanSuffix(ov.paramTypes);

        if (!signatureMatch.empty() &&
            ov.signatureString.find(signatureMatch) == std::string::npos) {
            continue;
        }

        if (!ov.isAbstract) {
            void* p = ReadMethodPointer(m);
            if (p != nullptr) {
                ov.address  = p;
                ov.compiled = true;
            } else {
                Log::Write(MagicStrings::LogCategoryWalk,
                           "FindMethodInstances: ReadMethodPointer null for " +
                           classDisplay + "." + ov.name + ov.signatureString);
            }
        }

        result.matches.push_back(std::move(ov));
    }

    result.ok = true;
    return result;
}

}  // namespace Discrete::IL2CPP
