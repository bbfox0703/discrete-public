// IL2CPPMethodInvoker.cpp — Phase 19a callMethod backend (IL2CPP).

#include "IL2CPPMethodInvoker.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/Memory.h"
#include "../shared/MethodSignatureFormat.h"
#include "IL2CPPDomain.h"
#include "IL2CPPResolver.h"
#include "IL2CPPThreadAttach.h"
#include "IL2CPPTypeParser.h"

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <cstring>

namespace Discrete::IL2CPP {

namespace {

const char* SafeStr(const char* s) { return s ? s : ""; }

// SEH leaves — same convention as IL2CPPMethodFinder.cpp. Each .cpp owns its
// copies because SEH cannot share a frame with destructors and the
// codebase's house style is one set per file.
#pragma warning(push)
#pragma warning(disable: 4509)
const MethodInfo* SehNextMethod(fn_class_get_methods fn, Il2CppClass* k, void** iter) {
    __try { return fn(k, iter); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehMethodName(fn_method_get_name fn, const MethodInfo* m) {
    __try { return fn(m); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
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
Il2CppClass* SehObjectGetClass(fn_object_get_class fn, Il2CppObject* o) {
    __try { return fn(o); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
void* SehObjectUnbox(fn_object_unbox fn, Il2CppObject* o) {
    __try { return fn(o); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
Il2CppString* SehStringNew(fn_string_new fn, const char* utf8) {
    __try { return fn(utf8); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
// runtime_invoke is the throwing call — we wrap the whole call in SEH so a
// native crash inside the JIT/AOT entry point becomes "exception captured"
// rather than tearing down the host process.
bool SehRuntimeInvoke(fn_runtime_invoke fn,
                      const MethodInfo* m,
                      void* obj,
                      void** params,
                      Il2CppException** outExc,
                      Il2CppObject** outRet) {
    __try {
        Il2CppException* exc = nullptr;
        Il2CppObject* ret = fn(m, obj, params, &exc);
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

constexpr std::uint32_t kMethodStatic   = 0x0010u;
constexpr std::uint32_t kMethodAbstract = 0x0400u;

// MethodInfo's first qword (`methodPointer`) is the AOT entry point — same
// path as MethodFinder uses. il2cpp_method_get_pointer is not exported in
// Unity 2021.3+, so the struct read is the load-bearing path.
void* ReadMethodPointer(const MethodInfo* m) {
    if (m == nullptr) return nullptr;
    const auto& r = GetResolver();
    if (r.method_get_pointer != nullptr) {
        if (void* p = SehMethodPtr(r.method_get_pointer, m)) return p;
    }
    return SehMethodPtrFromStruct(m);
}

struct ResolvedMethod {
    const MethodInfo*        method        = nullptr;
    bool                     isStatic      = false;
    bool                     isAbstract    = false;
    std::vector<std::string> paramTypes;
    std::string              returnType;
    std::string              signatureString;
    std::string              humanSuffix;
    void*                    address       = nullptr;
};

// Method resolver — single pass, returns the unique MethodInfo* for
// (klass, methodName) optionally narrowed by signature substring. Caller
// must already hold a ThreadAttachGuard (we don't take one here so the
// invoker can keep the same scope across resolve + invoke).
//
// Result codes via outErr:
//   ""                    → ok, exactly one match
//   "method_not_found"    → no method with that name + signature filter
//   "ambiguous_method"    → multiple matches; caller should pass tighter signatureMatch
bool ResolveOverload(Il2CppClass* klass,
                     const std::string& methodName,
                     const std::string& signatureMatch,
                     ResolvedMethod& out,
                     std::string& outErr) {
    const auto& r = GetResolver();
    if (r.class_get_methods == nullptr || r.method_get_name == nullptr) {
        outErr = "il2cpp class/method exports unbound";
        return false;
    }

    int matchCount = 0;
    ResolvedMethod first;

    void* iter = nullptr;
    for (;;) {
        const MethodInfo* m = SehNextMethod(r.class_get_methods, klass, &iter);
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

        std::uint32_t paramCount = 0;
        if (r.method_get_param_count != nullptr) {
            paramCount = SehParamCount(r.method_get_param_count, m);
        }
        if (paramCount > 0 && r.method_get_param != nullptr) {
            cand.paramTypes.reserve(paramCount);
            for (std::uint32_t i = 0; i < paramCount; ++i) {
                const Il2CppType* pt = SehParamType(r.method_get_param, m, i);
                cand.paramTypes.push_back(pt ? TypeName(pt) : std::string{});
            }
        }
        if (r.method_get_return_type != nullptr) {
            if (const Il2CppType* rt = SehReturnType(r.method_get_return_type, m)) {
                cand.returnType = TypeName(rt);
            }
        }
        cand.signatureString = Shared::MakeSignatureString(cand.paramTypes);
        cand.humanSuffix     = Shared::MakeHumanSuffix(cand.paramTypes);

        if (!signatureMatch.empty() &&
            cand.signatureString.find(signatureMatch) == std::string::npos) {
            continue;
        }

        cand.address = cand.isAbstract ? nullptr : ReadMethodPointer(m);

        if (matchCount == 0) first = std::move(cand);
        ++matchCount;
        if (matchCount >= 2) break;  // ambiguity bail-out
    }

    if (matchCount == 0) { outErr = "method_not_found"; return false; }
    if (matchCount  > 1) { outErr = "ambiguous_method";  return false; }

    out = std::move(first);
    return true;
}

// Decode the System.String at `strPtr` (a managed Il2CppString*) into UTF-8.
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

// Format `Type: message` for a captured Il2CppException.
//
// IL2CPP's Il2CppException mirrors System.Exception's managed layout. On
// x64 the relevant offsets are:
//   +0x00  Il2CppObject (klass*, monitor)         16 bytes
//   +0x10  _className   (Il2CppString*)           legacy, often null
//   +0x18  _message     (Il2CppString*)           ← the message we want
//   +0x20  _data        (IDictionary*)
//   +0x28  _innerException
//   ...
// The 0x18 message offset has been stable across Mono bdwgc + IL2CPP for
// the entire Unity 2018+ lifecycle. If the decode fails we still emit the
// exception type so the caller knows the call faulted.
std::string FormatException(Il2CppException* exc) {
    if (exc == nullptr) return {};

    const auto& r = GetResolver();
    std::string typeName = "System.Exception";
    if (r.object_get_class != nullptr) {
        Il2CppClass* k = SehObjectGetClass(r.object_get_class,
                                            reinterpret_cast<Il2CppObject*>(exc));
        if (k != nullptr) {
            std::string ns;
            std::string nm;
            if (r.class_get_namespace != nullptr) ns = SafeStr(SehClassNs(r.class_get_namespace, k));
            if (r.class_get_name      != nullptr) nm = SafeStr(SehClassName(r.class_get_name, k));
            if (!nm.empty()) typeName = ns.empty() ? nm : (ns + "." + nm);
        }
    }

    // Read message string pointer at exc + 0x18.
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
// Returns false when the formal type is something 19a doesn't decode into a
// JSON-friendly scalar (multi-field value type, etc.) — caller should
// surface kind=Unsupported instead.
void DecodeReturnValue(const std::string& returnType,
                       Il2CppObject* boxed,
                       CallReturn& out) {
    out.typeName = returnType;

    if (returnType.empty() || returnType == "System.Void") {
        out.kind = CallReturn::Kind::Void;
        return;
    }

    if (boxed == nullptr) {
        // Reference-type return that came back null.
        if (returnType == "System.String") {
            out.kind = CallReturn::Kind::Address;
            out.u64  = 0;
            return;
        }
        // Treat any non-void null return as Address(0) — caller can tell from
        // typeName that this is a reference type.
        out.kind = CallReturn::Kind::Address;
        out.u64  = 0;
        return;
    }

    // Primitive returns are boxed; unbox to access bytes.
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

    // 19c — decode managed string return into UTF-8. Boxed is the raw
    // Il2CppString* itself (strings are reference types — no unbox). Null
    // managed string returns surface as the empty string with the address
    // stashed in u64=0 so callers can distinguish via a separate field if
    // they care; the wire shape just renders returnValue:"".
    if (returnType == "System.String") {
        out.kind = CallReturn::Kind::String;
        out.u64  = reinterpret_cast<std::uintptr_t>(boxed);
        out.s    = DecodeManagedString(reinterpret_cast<std::uintptr_t>(boxed));
        return;
    }

    // Reference type — `boxed` is the object pointer itself, no unbox.
    out.kind = CallReturn::Kind::Address;
    out.u64  = reinterpret_cast<std::uintptr_t>(boxed);
}

// Marshal one CallArg into its 8-byte slot AND set the params-array entry
// the runtime sees. IL2CPP's runtime_invoke uses a non-uniform convention:
//   * value types  → params[i] = &slot, slot holds the value bytes
//   * reference types (Null / String / Address) → params[i] = the object
//     pointer DIRECTLY (one less level of indirection than value types)
//
// This split is what the popular IL2CPP mod loaders (BepInEx,
// MelonLoader, Il2CppInterop) use. The "uniform" convention from
// upstream Mono docs (params[i] always = &slot, even for refs) is a
// historical mismatch — the trampoline IL2CPP generates dereferences
// once for value types and zero times for reference types. Passing
// &(MonoString*) where a MonoString* is expected was reading the address
// of our slot as if it were a managed string, hence Parse("42") seeing
// garbage bytes and throwing FormatException.
bool MarshalArg(const CallArg& arg,
                const std::string& formalType,
                std::uint64_t* slot,
                void*& paramPtr,
                std::string& outErr) {
    auto write = [slot](const void* src, std::size_t n) {
        std::memcpy(slot, src, n);
    };

    switch (arg.kind) {
    case CallArgKind::Null:
        // Reference-type null — pass NULL directly as the param pointer.
        *slot = 0;
        paramPtr = nullptr;
        return true;
    case CallArgKind::Bool: {
        std::uint8_t v = (arg.i64 != 0 || arg.u64 != 0) ? 1u : 0u;
        write(&v, 1);
        paramPtr = slot;
        return true;
    }
    case CallArgKind::Byte: {
        std::uint8_t v = static_cast<std::uint8_t>(arg.u64);
        write(&v, 1);
        paramPtr = slot;
        return true;
    }
    case CallArgKind::SByte: {
        std::int8_t v = static_cast<std::int8_t>(arg.i64);
        write(&v, 1);
        paramPtr = slot;
        return true;
    }
    case CallArgKind::Int16: {
        std::int16_t v = static_cast<std::int16_t>(arg.i64);
        write(&v, 2);
        paramPtr = slot;
        return true;
    }
    case CallArgKind::UInt16: {
        std::uint16_t v = static_cast<std::uint16_t>(arg.u64);
        write(&v, 2);
        paramPtr = slot;
        return true;
    }
    case CallArgKind::Int32: {
        std::int32_t v = static_cast<std::int32_t>(arg.i64);
        write(&v, 4);
        paramPtr = slot;
        return true;
    }
    case CallArgKind::UInt32: {
        std::uint32_t v = static_cast<std::uint32_t>(arg.u64);
        write(&v, 4);
        paramPtr = slot;
        return true;
    }
    case CallArgKind::Int64: {
        std::int64_t v = arg.i64;
        write(&v, 8);
        paramPtr = slot;
        return true;
    }
    case CallArgKind::UInt64: {
        std::uint64_t v = arg.u64;
        write(&v, 8);
        paramPtr = slot;
        return true;
    }
    case CallArgKind::Float: {
        float v = static_cast<float>(arg.f);
        write(&v, 4);
        paramPtr = slot;
        return true;
    }
    case CallArgKind::Double: {
        double v = arg.f;
        write(&v, 8);
        paramPtr = slot;
        return true;
    }
    case CallArgKind::Char: {
        std::uint16_t v = static_cast<std::uint16_t>(arg.u64);
        write(&v, 2);
        paramPtr = slot;
        return true;
    }
    case CallArgKind::Address:
        // Reference-shaped — pass the pre-resolved managed pointer / IntPtr
        // directly as the param. Slot is unused for routing but kept set so
        // the address shows up in any debug log of slots[].
        *slot = arg.u64;
        paramPtr = reinterpret_cast<void*>(arg.u64);
        return true;
    case CallArgKind::String: {
        // 19c — alloc Il2CppString* via il2cpp_string_new and pass the
        // managed pointer DIRECTLY in params[i] (non-uniform convention —
        // see big comment on MarshalArg). Slot stores the pointer too for
        // diagnostic visibility.
        const auto& r = GetResolver();
        if (r.string_new == nullptr) {
            outErr = "il2cpp_string_new unbound";
            (void)formalType;
            return false;
        }
        // Empty string is valid — il2cpp_string_new("") returns a non-null
        // empty MonoString. Null wire value would arrive as Kind::Null, not
        // here.
        Il2CppString* s = SehStringNew(r.string_new, arg.s.c_str());
        if (s == nullptr) {
            outErr = "il2cpp_string_new returned null";
            (void)formalType;
            return false;
        }
        *slot   = reinterpret_cast<std::uintptr_t>(s);
        paramPtr = s;
        return true;
    }
    }
    outErr = "unknown_arg_kind";
    return false;
}

}  // namespace

CallMethodResult CallMethod(Il2CppClass* klass,
                            const std::string& methodName,
                            const std::string& signatureMatch,
                            std::uintptr_t instance,
                            const std::vector<CallArg>& args) {
    CallMethodResult result;
    auto t0 = std::chrono::steady_clock::now();

    if (klass == nullptr)        { result.error = "klass_is_null"; return result; }
    if (methodName.empty())      { result.error = "method_name_empty"; return result; }

    const auto& r = GetResolver();
    if (!r.resolved) { result.error = "il2cpp runtime not resolved"; return result; }
    if (r.runtime_invoke == nullptr) {
        result.error = "il2cpp_runtime_invoke unbound";
        return result;
    }

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) { result.error = "no il2cpp domain"; return result; }

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) { result.error = "il2cpp_thread_attach failed"; return result; }

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

    // 19c — instance dispatch supported. Validate the instance arg vs the
    // resolved method's static-ness:
    //  * static  + instance != 0 → ignore instance (callers may always send
    //    one for safety; we don't reject so the same Lua snippet works for
    //    both static and instance overloads of a given method).
    //  * !static + instance == 0 → reject (needs a `this` pointer).
    //  * !static + instance != 0 → forward as obj to runtime_invoke.
    void* invokeObj = nullptr;
    if (m.isStatic) {
        // instance silently ignored on static methods.
    } else {
        if (instance == 0) {
            result.error = "instance_required_for_non_static";
            return result;
        }
        invokeObj = reinterpret_cast<void*>(instance);
    }

    if (args.size() != m.paramTypes.size()) {
        result.error = "arg_count_mismatch";
        return result;
    }

    // Build the params array. Each arg gets one 8-byte backing slot; the
    // slot holds the value bytes for value types and stores the object
    // pointer (for diagnostic visibility) for ref types. paramPtrs[i] is
    // set per-arg by MarshalArg per the non-uniform convention:
    //   * value types → paramPtrs[i] = &slot
    //   * ref types   → paramPtrs[i] = the object pointer directly
    std::vector<std::uint64_t> slots(args.size(), 0u);
    std::vector<void*>         paramPtrs(args.size(), nullptr);
    for (std::size_t i = 0; i < args.size(); ++i) {
        std::string err;
        if (!MarshalArg(args[i], m.paramTypes[i], &slots[i], paramPtrs[i], err)) {
            result.error = err.empty() ? "arg_marshal_failed" : err;
            return result;
        }
    }

    Il2CppException* exc = nullptr;
    Il2CppObject*    ret = nullptr;
    if (!SehRuntimeInvoke(r.runtime_invoke,
                          m.method,
                          invokeObj,                           // 19c: instance dispatch
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

}  // namespace Discrete::IL2CPP
