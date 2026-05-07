// MonoMethodInvoker.cpp — Phase 19b callMethod backend (Mono).
//
// Mirror of IL2CPPMethodInvoker.cpp. The shape (single-pass overload
// resolver, 8-byte-slot marshaling, SEH-wrapped runtime_invoke, exception
// type+message decode at MonoException + 0x18) is identical — only the
// underlying runtime calls differ. Mono is JIT, so the address field of
// the response is populated via `mono_compile_method` after resolve.

#include "MonoMethodInvoker.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/Memory.h"
#include "../shared/MethodSignatureFormat.h"
#include "MonoDomain.h"
#include "MonoResolver.h"
#include "MonoThreadAttach.h"
#include "MonoTypeParser.h"

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstring>

namespace UnityCEDumper::Mono {

namespace {

const char* SafeStr(const char* s) { return s ? s : ""; }

// SEH leaves — same convention as MonoMethodFinder.cpp. One set per .cpp.
#pragma warning(push)
#pragma warning(disable: 4509)
MonoMethod* SehNextMethod(fn_class_get_methods fn, MonoClass* k, void** iter) {
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehMethodName(fn_method_get_name fn, MonoMethod* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
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
void* SehCompile(fn_compile_method fn, MonoMethod* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehClassName(fn_class_get_name fn, MonoClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehClassNs(fn_class_get_namespace fn, MonoClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
MonoClass* SehObjectGetClass(fn_object_get_class fn, MonoObject* o) {
    __try { return fn(o); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* SehObjectUnbox(fn_object_unbox fn, MonoObject* o) {
    __try { return fn(o); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
// runtime_invoke is the throwing call — wrap the whole call in SEH so a
// native crash inside the JIT trampoline becomes "exception captured"
// rather than tearing down the host process.
bool SehRuntimeInvoke(fn_runtime_invoke fn,
                      MonoMethod* m,
                      void* obj,
                      void** params,
                      MonoObject** outExc,
                      MonoObject** outRet) {
    __try {
        MonoObject* exc = nullptr;
        MonoObject* ret = fn(m, obj, params, &exc);
        if (outExc) *outExc = exc;
        if (outRet) *outRet = ret;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        if (outExc) *outExc = nullptr;
        if (outRet) *outRet = nullptr;
        return false;
    }
}
#pragma warning(pop)

// ECMA-335 §I.10.5.4 — relevant MethodAttributes bits.
constexpr std::uint32_t kMethodStatic   = 0x0010u;
constexpr std::uint32_t kMethodAbstract = 0x0400u;

struct ResolvedMethod {
    MonoMethod*              method        = nullptr;
    bool                     isStatic      = false;
    bool                     isAbstract    = false;
    std::vector<std::string> paramTypes;
    std::string              returnType;
    std::string              signatureString;
    std::string              humanSuffix;
    void*                    address       = nullptr;
};

// Method resolver — single pass, returns the unique MonoMethod* for
// (klass, methodName) optionally narrowed by signature substring. Caller
// must already hold a ThreadAttachGuard.
//
// Result codes via outErr:
//   ""                    → ok, exactly one match
//   "method_not_found"    → no method with that name + signature filter
//   "ambiguous_method"    → multiple matches; tighten signatureMatch
bool ResolveOverload(MonoClass* klass,
                     const std::string& methodName,
                     const std::string& signatureMatch,
                     ResolvedMethod& out,
                     std::string& outErr) {
    const auto& r = GetResolver();
    if (r.class_get_methods == nullptr || r.method_get_name == nullptr) {
        outErr = "mono class/method exports unbound";
        return false;
    }

    int matchCount = 0;
    ResolvedMethod first;

    void* iter = nullptr;
    for (;;) {
        MonoMethod* m = SehNextMethod(r.class_get_methods, klass, &iter);
        if (m == nullptr) break;

        const char* nameC = SehMethodName(r.method_get_name, m);
        if (nameC == nullptr || methodName != nameC) continue;

        ResolvedMethod cand;
        cand.method = m;

        if (r.method_get_flags != nullptr) {
            std::uint32_t iflags = 0;
            std::uint32_t flags  = SehMethodFlags(r.method_get_flags, m, &iflags);
            cand.isStatic   = (flags & kMethodStatic)   != 0u;
            cand.isAbstract = (flags & kMethodAbstract) != 0u;
        }

        // Decode signature when the optional Phase 18 exports are bound.
        if (r.method_signature != nullptr) {
            MonoMethodSignature* sig = SehMethodSig(r.method_signature, m);
            if (sig != nullptr) {
                std::uint32_t paramCount = 0;
                if (r.signature_get_param_count != nullptr) {
                    paramCount = SehSigParamCount(r.signature_get_param_count, sig);
                }
                cand.paramTypes.reserve(paramCount);
                if (r.signature_get_params != nullptr) {
                    void* sigIter = nullptr;
                    for (;;) {
                        MonoType* pt = SehSigNextParam(r.signature_get_params, sig, &sigIter);
                        if (pt == nullptr) break;
                        cand.paramTypes.push_back(TypeName(pt));
                    }
                }
                if (r.signature_get_return_type != nullptr) {
                    if (MonoType* rt = SehSigReturnType(r.signature_get_return_type, sig)) {
                        cand.returnType = TypeName(rt);
                    }
                }
            }
        }
        cand.signatureString = Shared::MakeSignatureString(cand.paramTypes);
        cand.humanSuffix     = Shared::MakeHumanSuffix(cand.paramTypes);

        if (!signatureMatch.empty() &&
            cand.signatureString.find(signatureMatch) == std::string::npos) {
            continue;
        }

        // JIT-compile so the wire response carries the same `address` field
        // as IL2CPP. A failed compile (abstract / generic-without-args)
        // leaves address=nullptr; the call still proceeds because
        // mono_runtime_invoke handles JIT internally.
        if (r.compile_method != nullptr && !cand.isAbstract) {
            cand.address = SehCompile(r.compile_method, m);
        }

        if (matchCount == 0) first = std::move(cand);
        ++matchCount;
        if (matchCount >= 2) break;  // ambiguity bail-out
    }

    if (matchCount == 0) { outErr = "method_not_found"; return false; }
    if (matchCount  > 1) { outErr = "ambiguous_method";  return false; }

    out = std::move(first);
    return true;
}

// Decode the System.String at `strPtr` (a managed MonoString*) into UTF-8.
// Layout is shared between Mono bdwgc and IL2CPP at +0x10 length, +0x14 chars.
// Returns "" for null pointers and best-effort on partial reads.
std::string DecodeManagedString(std::uintptr_t strPtr) {
    if (strPtr == 0) return {};
    auto rd = [](std::uintptr_t a, void* dst, std::size_t n) {
        return Memory::SafeReadBytes(reinterpret_cast<void*>(a), dst, n);
    };
    std::int32_t len = 0;
    if (!rd(strPtr + 0x10, &len, 4)) return {};
    if (len <= 0 || len > 65536) return {};

    std::vector<std::uint16_t> chars(static_cast<std::size_t>(len));
    if (!rd(strPtr + 0x14, chars.data(),
            static_cast<std::size_t>(len) * sizeof(std::uint16_t))) {
        return {};
    }

    std::string utf8;
    utf8.reserve(static_cast<std::size_t>(len));
    auto pushCp = [&utf8](std::uint32_t cp) {
        if (cp < 0x80u) utf8.push_back(static_cast<char>(cp));
        else if (cp < 0x800u) {
            utf8.push_back(static_cast<char>(0xC0u | (cp >> 6)));
            utf8.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
        } else if (cp < 0x10000u) {
            utf8.push_back(static_cast<char>(0xE0u | (cp >> 12)));
            utf8.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
            utf8.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
        } else {
            utf8.push_back(static_cast<char>(0xF0u | (cp >> 18)));
            utf8.push_back(static_cast<char>(0x80u | ((cp >> 12) & 0x3Fu)));
            utf8.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
            utf8.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
        }
    };
    for (std::size_t i = 0; i < chars.size(); ++i) {
        std::uint32_t cp = chars[i];
        if (cp >= 0xD800u && cp <= 0xDBFFu && i + 1 < chars.size()) {
            std::uint32_t lo = chars[i + 1];
            if (lo >= 0xDC00u && lo <= 0xDFFFu) {
                cp = 0x10000u + ((cp - 0xD800u) << 10) + (lo - 0xDC00u);
                ++i;
            } else {
                cp = 0xFFFDu;
            }
        } else if (cp >= 0xDC00u && cp <= 0xDFFFu) {
            cp = 0xFFFDu;
        }
        pushCp(cp);
    }
    return utf8;
}

// Format `Type: message` for a captured managed exception.
//
// MonoException mirrors managed System.Exception. On x64 the relevant
// offsets are identical to IL2CPP (Mono and IL2CPP share the corlib
// layout):
//   +0x00  MonoObject (vtable*, sync)              16 bytes
//   +0x10  _className   (MonoString*)              legacy, often null
//   +0x18  _message     (MonoString*)              ← the message
//   +0x20  _data        (IDictionary*)
//   +0x28  _innerException
//   ...
// If the message decode fails we still emit the exception type alone
// (decoded via mono_object_get_class + mono_class_get_name) so the caller
// knows the call faulted.
std::string FormatException(MonoObject* exc) {
    if (exc == nullptr) return {};

    const auto& r = GetResolver();
    std::string typeName = "System.Exception";
    if (r.object_get_class != nullptr) {
        MonoClass* k = SehObjectGetClass(r.object_get_class, exc);
        if (k != nullptr) {
            std::string ns;
            std::string nm;
            if (r.class_get_namespace != nullptr) ns = SafeStr(SehClassNs(r.class_get_namespace, k));
            if (r.class_get_name      != nullptr) nm = SafeStr(SehClassName(r.class_get_name, k));
            if (!nm.empty()) typeName = ns.empty() ? nm : (ns + "." + nm);
        }
    }

    std::uintptr_t msgPtr = 0;
    auto rd = [](std::uintptr_t a, void* dst, std::size_t n) {
        return Memory::SafeReadBytes(reinterpret_cast<void*>(a), dst, n);
    };
    if (rd(reinterpret_cast<std::uintptr_t>(exc) + 0x18, &msgPtr, sizeof(msgPtr)) &&
        msgPtr != 0) {
        std::string msg = DecodeManagedString(msgPtr);
        if (!msg.empty()) return typeName + ": " + msg;
    }
    return typeName;
}

// Map a formal parameter type name to a CallReturn-decoder for unboxing.
void DecodeReturnValue(const std::string& returnType,
                       MonoObject* boxed,
                       CallReturn& out) {
    out.typeName = returnType;

    if (returnType.empty() || returnType == "System.Void") {
        out.kind = CallReturn::Kind::Void;
        return;
    }

    if (boxed == nullptr) {
        if (returnType == "System.String") {
            out.kind = CallReturn::Kind::Address;
            out.u64  = 0;
            return;
        }
        out.kind = CallReturn::Kind::Address;
        out.u64  = 0;
        return;
    }

    const auto& r = GetResolver();
    auto unbox = [&](std::size_t n, void* dst) -> bool {
        if (r.object_unbox == nullptr) return false;
        void* p = SehObjectUnbox(r.object_unbox, boxed);
        if (p == nullptr) return false;
        return Memory::SafeReadBytes(p, dst, n);
    };

    if (returnType == "System.Boolean") {
        std::uint8_t v = 0;
        if (unbox(1, &v)) { out.kind = CallReturn::Kind::SignedInt; out.i64 = (v != 0) ? 1 : 0; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (returnType == "System.Byte") {
        std::uint8_t v = 0;
        if (unbox(1, &v)) { out.kind = CallReturn::Kind::UnsignedInt; out.u64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (returnType == "System.SByte") {
        std::int8_t v = 0;
        if (unbox(1, &v)) { out.kind = CallReturn::Kind::SignedInt; out.i64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (returnType == "System.Int16") {
        std::int16_t v = 0;
        if (unbox(2, &v)) { out.kind = CallReturn::Kind::SignedInt; out.i64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (returnType == "System.UInt16" || returnType == "System.Char") {
        std::uint16_t v = 0;
        if (unbox(2, &v)) { out.kind = CallReturn::Kind::UnsignedInt; out.u64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (returnType == "System.Int32") {
        std::int32_t v = 0;
        if (unbox(4, &v)) { out.kind = CallReturn::Kind::SignedInt; out.i64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (returnType == "System.UInt32") {
        std::uint32_t v = 0;
        if (unbox(4, &v)) { out.kind = CallReturn::Kind::UnsignedInt; out.u64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (returnType == "System.Int64") {
        std::int64_t v = 0;
        if (unbox(8, &v)) { out.kind = CallReturn::Kind::SignedInt; out.i64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (returnType == "System.UInt64") {
        std::uint64_t v = 0;
        if (unbox(8, &v)) { out.kind = CallReturn::Kind::UnsignedInt; out.u64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (returnType == "System.Single") {
        float v = 0.f;
        if (unbox(4, &v)) { out.kind = CallReturn::Kind::Float; out.f = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (returnType == "System.Double") {
        double v = 0.0;
        if (unbox(8, &v)) { out.kind = CallReturn::Kind::Float; out.f = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (returnType == "System.IntPtr" || returnType == "System.UIntPtr") {
        std::uintptr_t v = 0;
        if (unbox(sizeof(v), &v)) { out.kind = CallReturn::Kind::Address; out.u64 = v; }
        else                      { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }

    // System.String surfaces as Address in 19b (raw MonoString*); 19c will
    // decode it into UTF-8 here.
    if (returnType == "System.String") {
        out.kind = CallReturn::Kind::Address;
        out.u64  = reinterpret_cast<std::uintptr_t>(boxed);
        return;
    }

    // Reference type — `boxed` is the object pointer itself, no unbox.
    out.kind = CallReturn::Kind::Address;
    out.u64  = reinterpret_cast<std::uintptr_t>(boxed);
}

// Marshal one CallArg into its 8-byte slot. Returns false when the wire
// `type` tag conflicts with the formal parameter type or carries a value
// 19b refuses to forward (e.g. string args).
bool MarshalArg(const CallArg& arg,
                const std::string& formalType,
                std::uint64_t* slot,
                std::string& outErr) {
    auto write = [slot](const void* src, std::size_t n) {
        std::memcpy(slot, src, n);
    };

    switch (arg.kind) {
    case CallArgKind::Null:
        *slot = 0;
        return true;
    case CallArgKind::Bool: {
        std::uint8_t v = (arg.i64 != 0 || arg.u64 != 0) ? 1u : 0u;
        write(&v, 1);
        return true;
    }
    case CallArgKind::Byte: {
        std::uint8_t v = static_cast<std::uint8_t>(arg.u64);
        write(&v, 1);
        return true;
    }
    case CallArgKind::SByte: {
        std::int8_t v = static_cast<std::int8_t>(arg.i64);
        write(&v, 1);
        return true;
    }
    case CallArgKind::Int16: {
        std::int16_t v = static_cast<std::int16_t>(arg.i64);
        write(&v, 2);
        return true;
    }
    case CallArgKind::UInt16: {
        std::uint16_t v = static_cast<std::uint16_t>(arg.u64);
        write(&v, 2);
        return true;
    }
    case CallArgKind::Int32: {
        std::int32_t v = static_cast<std::int32_t>(arg.i64);
        write(&v, 4);
        return true;
    }
    case CallArgKind::UInt32: {
        std::uint32_t v = static_cast<std::uint32_t>(arg.u64);
        write(&v, 4);
        return true;
    }
    case CallArgKind::Int64: {
        std::int64_t v = arg.i64;
        write(&v, 8);
        return true;
    }
    case CallArgKind::UInt64: {
        std::uint64_t v = arg.u64;
        write(&v, 8);
        return true;
    }
    case CallArgKind::Float: {
        float v = static_cast<float>(arg.f);
        write(&v, 4);
        return true;
    }
    case CallArgKind::Double: {
        double v = arg.f;
        write(&v, 8);
        return true;
    }
    case CallArgKind::Char: {
        std::uint16_t v = static_cast<std::uint16_t>(arg.u64);
        write(&v, 2);
        return true;
    }
    case CallArgKind::Address:
        *slot = arg.u64;
        return true;
    case CallArgKind::String:
        outErr = "string_args_not_supported_in_19b";
        (void)formalType;
        return false;
    }
    outErr = "unknown_arg_kind";
    return false;
}

}  // namespace

CallMethodResult CallMethod(MonoClass* klass,
                            const std::string& methodName,
                            const std::string& signatureMatch,
                            std::uintptr_t instance,
                            const std::vector<CallArg>& args) {
    CallMethodResult result;
    auto t0 = std::chrono::steady_clock::now();

    if (klass == nullptr)        { result.error = "klass_is_null"; return result; }
    if (methodName.empty())      { result.error = "method_name_empty"; return result; }

    const auto& r = GetResolver();
    if (!r.resolved) { result.error = "mono runtime not resolved"; return result; }
    if (r.runtime_invoke == nullptr) {
        result.error = "mono_runtime_invoke unbound";
        return result;
    }

    // 19b constraint — instance dispatch lifts in 19c.
    if (instance != 0) {
        result.error = "instance_methods_not_supported_in_19b";
        return result;
    }

    MonoDomain* domain = GetRootDomain();
    if (domain == nullptr) { result.error = "no mono root domain"; return result; }

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) { result.error = "mono_thread_attach failed"; return result; }

    ResolvedMethod m;
    std::string resolveErr;
    if (!ResolveOverload(klass, methodName, signatureMatch, m, resolveErr)) {
        result.error = resolveErr;
        return result;
    }
    result.addressCalled  = m.address;
    result.methodSignature = m.signatureString;
    result.humanSuffix     = m.humanSuffix;
    result.ret.typeName    = m.returnType;

    if (m.isAbstract) {
        result.error = "method_is_abstract";
        return result;
    }
    if (!m.isStatic) {
        // 19c lifts this; in 19b reject early with a distinct error code.
        result.error = "instance_required_for_non_static";
        return result;
    }

    if (args.size() != m.paramTypes.size()) {
        result.error = "arg_count_mismatch";
        return result;
    }

    // 19b explicitly refuses string args.
    for (std::size_t i = 0; i < args.size(); ++i) {
        if (args[i].kind == CallArgKind::String) {
            result.error = "string_args_not_supported_in_19b";
            return result;
        }
    }

    // Build the params array. Same convention as IL2CPP: one 8-byte slot
    // per arg, primitives written at the low end. Mono's runtime_invoke
    // marshaling matches this on x64 (see mono/metadata/object.c).
    std::vector<std::uint64_t> slots(args.size(), 0u);
    std::vector<void*>         paramPtrs(args.size(), nullptr);
    for (std::size_t i = 0; i < args.size(); ++i) {
        std::string err;
        if (!MarshalArg(args[i], m.paramTypes[i], &slots[i], err)) {
            result.error = err.empty() ? "arg_marshal_failed" : err;
            return result;
        }
        paramPtrs[i] = &slots[i];
    }

    MonoObject* exc = nullptr;
    MonoObject* ret = nullptr;
    if (!SehRuntimeInvoke(r.runtime_invoke,
                          m.method,
                          /*obj=*/nullptr,                     // 19b: static only
                          paramPtrs.empty() ? nullptr : paramPtrs.data(),
                          &exc,
                          &ret)) {
        result.error = "native_crash_in_runtime_invoke";
        return result;
    }

    if (exc != nullptr) {
        result.exception = FormatException(exc);
        if (result.exception.empty()) result.exception = "managed exception (no message)";
        result.ret.kind  = CallReturn::Kind::Void;
        result.ret.typeName = m.returnType;
    } else {
        DecodeReturnValue(m.returnType, ret, result.ret);
        result.ok = true;
    }

    auto t1 = std::chrono::steady_clock::now();
    result.elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(t1 - t0).count();

    Log::Write(MagicStrings::LogCategoryWalk,
               std::string{"callMethod: "} + methodName + m.signatureString +
               (result.ok ? " ok" :
                (result.exception.empty() ? " err=" + result.error
                                          : " exc=" + result.exception)));
    return result;
}

}  // namespace UnityCEDumper::Mono
