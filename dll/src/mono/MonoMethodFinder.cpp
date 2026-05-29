// MonoMethodFinder.cpp — Phase 18 findMethodInstances (Mono).

#include "MonoMethodFinder.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/MethodSignatureFormat.h"
#include "MonoClassWalker.h"
#include "MonoDomain.h"
#include "MonoResolver.h"
#include "MonoThreadAttach.h"
#include "MonoTypeParser.h"

#include <windows.h>

#include <cstring>

namespace Discrete::Mono {

namespace {

const char* SafeStr(const char* s) { return s ? s : ""; }

// SEH-leaf wrappers — every Mono runtime call from a non-Mono thread can fault
// when we hand it ill-formed input (degenerate method, stripped runtime). Same
// pattern as MonoAddressFinder / MonoClassWalker.
#pragma warning(push)
#pragma warning(disable: 4509)
MonoMethod* SehNextMethod(fn_class_get_methods fn, MonoClass* k, void** iter) {
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehMethodName(fn_method_get_name fn, MonoMethod* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
std::uint32_t SehMethodToken(fn_method_get_token fn, MonoMethod* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0u; }
}
std::uint32_t SehMethodFlags(fn_method_get_flags fn, MonoMethod* m, std::uint32_t* iflags) {
    __try { return fn(m, iflags); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0u; }
}
MonoMethodSignature* SehMethodSig(fn_method_signature fn, MonoMethod* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
std::uint32_t SehSigParamCount(fn_signature_get_param_count fn, MonoMethodSignature* s) {
    __try { return fn(s); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0u; }
}
MonoType* SehSigNextParam(fn_signature_get_params fn, MonoMethodSignature* s, void** iter) {
    __try { return fn(s, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoType* SehSigReturnType(fn_signature_get_return_type fn, MonoMethodSignature* s) {
    __try { return fn(s); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void SehMethodParamNames(fn_method_get_param_names fn, MonoMethod* m, const char** out) {
    __try { fn(m, out); } __except (EXCEPTION_EXECUTE_HANDLER) { /* leave out[] zero */ }
}
void* SehCompile(fn_compile_method fn, MonoMethod* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehClassName(fn_class_get_name fn, MonoClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehClassNs(fn_class_get_namespace fn, MonoClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
#pragma warning(pop)

// ECMA-335 §I.10.5.4 — relevant MethodAttributes bits.
constexpr std::uint32_t kMethodStatic   = 0x0010u;
constexpr std::uint32_t kMethodVirtual  = 0x0040u;
constexpr std::uint32_t kMethodAbstract = 0x0400u;

}  // namespace

FindMethodResult FindMethodInstances(MonoClass* klass,
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
        result.error = "mono runtime not resolved";
        return result;
    }

    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) {
        result.error = "no mono root domain";
        return result;
    }
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) {
        result.error = "mono_thread_attach failed";
        return result;
    }

    // Capture the class's display name + namespace once. Used for every match
    // so the caller doesn't need to look it up separately.
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
        MonoMethod* m = SehNextMethod(r.class_get_methods, klass, &iter);
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

        // Decode the signature when the resolver bound the optional exports.
        // Older Mono builds may genuinely lack one or more — degrade gracefully
        // (empty paramTypes / returnType) instead of dropping the overload.
        if (r.method_signature != nullptr) {
            MonoMethodSignature* sig = SehMethodSig(r.method_signature, m);
            if (sig != nullptr) {
                std::uint32_t paramCount = 0;
                if (r.signature_get_param_count != nullptr) {
                    paramCount = SehSigParamCount(r.signature_get_param_count, sig);
                }
                ov.paramTypes.reserve(paramCount);
                if (r.signature_get_params != nullptr) {
                    void* sigIter = nullptr;
                    for (;;) {
                        MonoType* pt = SehSigNextParam(r.signature_get_params, sig, &sigIter);
                        if (pt == nullptr) break;
                        ov.paramTypes.push_back(TypeName(pt));
                    }
                }
                if (r.signature_get_return_type != nullptr) {
                    if (MonoType* rt = SehSigReturnType(r.signature_get_return_type, sig)) {
                        ov.returnType = TypeName(rt);
                    }
                }

                // Param names are optional; the export fills a caller-allocated
                // array with one C-string per declared param. Some Mono builds
                // lack this entirely.
                if (r.method_get_param_names != nullptr && paramCount > 0) {
                    std::vector<const char*> namesBuf(paramCount, nullptr);
                    SehMethodParamNames(r.method_get_param_names, m, namesBuf.data());
                    ov.paramNames.reserve(paramCount);
                    for (auto p : namesBuf) ov.paramNames.emplace_back(SafeStr(p));
                }
            }
        }

        ov.signatureString = Shared::MakeSignatureString(ov.paramTypes);
        ov.humanSuffix     = Shared::MakeHumanSuffix(ov.paramTypes);

        // Optional substring filter: if the caller asked for "Vector3" they
        // only want overloads whose signature mentions Vector3.
        if (!signatureMatch.empty() &&
            ov.signatureString.find(signatureMatch) == std::string::npos) {
            continue;
        }

        // JIT-compile so callers can register a CE symbol or AOB-scan the
        // resulting bytes. A failed compile (abstract / generic-without-args)
        // leaves address=nullptr; we still ship the overload so the UI can
        // show the user what's there.
        if (r.compile_method != nullptr && !ov.isAbstract) {
            void* p = SehCompile(r.compile_method, m);
            if (p != nullptr) {
                ov.address  = p;
                ov.compiled = true;
            } else {
                Log::Write(MagicStrings::LogCategoryWalk,
                           "FindMethodInstances: compile failed for " +
                           classDisplay + "." + ov.name + ov.signatureString);
            }
        }

        result.matches.push_back(std::move(ov));
    }

    result.ok = true;
    return result;
}

}  // namespace Discrete::Mono
