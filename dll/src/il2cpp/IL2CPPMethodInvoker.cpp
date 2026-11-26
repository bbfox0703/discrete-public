// IL2CPPMethodInvoker.cpp — Phase 19a callMethod backend (IL2CPP).

#include "IL2CPPMethodInvoker.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/ManagedLayout.h"
#include "../shared/Memory.h"
#include "../shared/MethodInvokeValidation.h"
#include "../shared/MethodSignatureFormat.h"
#include "../shared/RuntimeHealth.h"
#include "../shared/SehFilter.h"
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
    __try { return fn(k, iter); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const char* SehMethodName(fn_method_get_name fn, const MethodInfo* m) {
    __try { return fn(m); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
std::uint32_t SehMethodFlags(fn_method_get_flags fn, const MethodInfo* m, std::uint32_t* iflags) {
    __try { return fn(m, iflags); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0u; }
}
std::uint32_t SehParamCount(fn_method_get_param_count fn, const MethodInfo* m) {
    __try { return fn(m); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0u; }
}
const Il2CppType* SehParamType(fn_method_get_param fn, const MethodInfo* m, std::uint32_t i) {
    __try { return fn(m, i); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const Il2CppType* SehReturnType(fn_method_get_return_type fn, const MethodInfo* m) {
    __try { return fn(m); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
void* SehMethodPtr(fn_method_get_pointer fn, const MethodInfo* m) {
    __try { return fn(m); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
void* SehMethodPtrFromStruct(const MethodInfo* m) {
    __try { return *reinterpret_cast<void* const*>(m); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const char* SehClassName(fn_class_get_name fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const char* SehClassNs(fn_class_get_namespace fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
Il2CppClass* SehClassGetParent(fn_class_get_parent fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
Il2CppClass* SehClassFromType(fn_class_from_il2cpp_type fn, const Il2CppType* t) {
    __try { return fn(t); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
bool SehClassIsValueType(fn_class_is_valuetype fn, Il2CppClass* k) {
    // il2cpp_class_is_valuetype returns C++ bool (low byte in AL); mask to
    // 0xFF so upper garbage bytes in EAX cannot produce a false positive.
    __try { return (fn(k) & 0xFF) != 0; }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return false; }
}
bool SehClassIsEnum(fn_class_is_enum fn, const Il2CppClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return false; }
}
const Il2CppType* SehClassEnumBasetype(fn_class_enum_basetype fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
FieldInfo* SehNextField(fn_class_get_fields fn, Il2CppClass* k, void** iter) {
    __try { return fn(k, iter); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const char* SehFieldName(fn_field_get_name fn, FieldInfo* f) {
    __try { return fn(f); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
const Il2CppType* SehFieldType(fn_field_get_type fn, FieldInfo* f) {
    __try { return fn(f); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
int SehFieldFlags(fn_field_get_flags fn, FieldInfo* f) {
    __try { return fn(f); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0; }
}
Il2CppClass* SehObjectGetClass(fn_object_get_class fn, Il2CppObject* o) {
    __try { return fn(o); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
void* SehObjectUnbox(fn_object_unbox fn, Il2CppObject* o) {
    __try { return fn(o); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
int SehClassValueSize(fn_class_value_size fn, Il2CppClass* k, std::uint32_t* align) {
    __try { return fn(k, align); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0; }
}
Il2CppString* SehStringNew(fn_string_new fn, const char* utf8) {
    __try { return fn(utf8); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return nullptr; }
}
std::uintptr_t SehGcHandleNew(fn_gchandle_new fn, Il2CppObject* obj, bool pinned) {
    __try { return fn(obj, pinned); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) { return 0u; }
}
void SehGcHandleFree(fn_gchandle_free fn, std::uintptr_t handle) {
    __try { fn(handle); }
    __except (Seh::MemoryAccessExceptionFilter(GetExceptionCode(), GetExceptionInformation())) {}
}
// runtime_invoke is the throwing call — we wrap the whole call in SEH so a
// native crash inside the JIT/AOT entry point becomes "exception captured"
// rather than tearing down the host process.
bool SehRuntimeInvoke(fn_runtime_invoke fn,
                      const MethodInfo* m,
                      void* obj,
                      void** params,
                      Il2CppException** outExc,
                      Il2CppObject** outRet,
                      unsigned long* outFaultCode) {
    unsigned long code = 0;
    __try {
        Il2CppException* exc = nullptr;
        Il2CppObject* ret = fn(m, obj, params, &exc);
        if (outExc) *outExc = exc;
        if (outRet) *outRet = ret;
        return true;
    } __except ((code = GetExceptionCode()),
                Seh::MemoryAccessExceptionFilter(code, GetExceptionInformation())) {
        if (outExc) *outExc = nullptr;
        if (outRet) *outRet = nullptr;
        if (outFaultCode) *outFaultCode = code;
        return false;
    }
}
#pragma warning(pop)

constexpr std::uint32_t kMethodStatic   = 0x0010u;
constexpr std::uint32_t kMethodAbstract = 0x0400u;

// Value-struct arg/return byte cap. Covers Vector2(8)/Vector3(12)/
// Quaternion(16)/Color(16)/Matrix4x4(64). Value-struct arg backing buffers
// are always padded to this size so a size-mismatched struct can never
// OOB-read inside the JIT/AOT trampoline; struct returns bigger than this
// surface as Unsupported rather than copying an unbounded blob.
constexpr std::uint32_t kValueStructMaxBytes = 128u;

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

bool IsEnumClass(Il2CppClass* k) {
    if (k == nullptr) return false;
    const auto& r = GetResolver();
    if (r.class_is_enum != nullptr) {
        return SehClassIsEnum(r.class_is_enum, k);
    }
    if (r.class_get_parent != nullptr && r.class_get_name != nullptr) {
        Il2CppClass* parent = SehClassGetParent(r.class_get_parent, k);
        if (parent != nullptr) {
            const char* pName = SehClassName(r.class_get_name, parent);
            const char* pNs   = r.class_get_namespace ? SehClassNs(r.class_get_namespace, parent) : nullptr;
            if (pName != nullptr && std::strcmp(pName, "Enum") == 0 &&
                pNs != nullptr && std::strcmp(pNs, "System") == 0) {
                return true;
            }
        }
    }
    return false;
}

bool IsValueTypeClass(Il2CppClass* k) {
    if (k == nullptr) return false;
    if (IsEnumClass(k)) return true;
    const auto& r = GetResolver();
    if (r.class_is_valuetype != nullptr) {
        return SehClassIsValueType(r.class_is_valuetype, k);
    }
    if (r.class_get_parent != nullptr && r.class_get_name != nullptr) {
        Il2CppClass* parent = SehClassGetParent(r.class_get_parent, k);
        if (parent != nullptr) {
            const char* pName = SehClassName(r.class_get_name, parent);
            const char* pNs   = r.class_get_namespace ? SehClassNs(r.class_get_namespace, parent) : nullptr;
            if (pName != nullptr && pNs != nullptr && std::strcmp(pNs, "System") == 0 &&
                (std::strcmp(pName, "ValueType") == 0 || std::strcmp(pName, "Enum") == 0)) {
                return true;
            }
        }
    }
    return false;
}

std::string ResolveEnumBaseType(Il2CppClass* k) {
    if (k == nullptr) return {};
    const auto& r = GetResolver();
    if (r.class_enum_basetype != nullptr) {
        if (const Il2CppType* bt = SehClassEnumBasetype(r.class_enum_basetype, k)) {
            std::string name = TypeName(bt);
            if (!name.empty()) return name;
        }
    }
    if (r.class_get_fields != nullptr && r.field_get_type != nullptr) {
        void* iter = nullptr;
        for (int guard = 0; guard < 256; ++guard) {
            FieldInfo* f = SehNextField(r.class_get_fields, k, &iter);
            if (f == nullptr) break;
            if (r.field_get_flags != nullptr) {
                const std::uint32_t flags = static_cast<std::uint32_t>(SehFieldFlags(r.field_get_flags, f));
                if ((flags & ManagedLayout::kFieldAttrStatic) != 0u) continue;
            }
            if (r.field_get_name != nullptr) {
                const char* fn = SehFieldName(r.field_get_name, f);
                if (fn != nullptr && std::strcmp(fn, "value__") != 0) continue;
            }
            if (const Il2CppType* ft = SehFieldType(r.field_get_type, f)) {
                std::string name = TypeName(ft);
                if (!name.empty()) return name;
            }
            break;
        }
    }
    return {};
}

Shared::FormalParamDescriptor BuildParamDescriptor(const Il2CppType* pt) {
    Shared::FormalParamDescriptor desc;
    if (pt == nullptr) return desc;
    desc.typeName = TypeName(pt);
    if (desc.typeName.empty() || desc.typeName.back() == '&' || desc.typeName.back() == '*') {
        return desc;
    }
    const auto& r = GetResolver();
    if (r.class_from_il2cpp_type != nullptr) {
        if (Il2CppClass* pk = SehClassFromType(r.class_from_il2cpp_type, pt)) {
            desc.metaKnown   = true;
            desc.isEnum      = IsEnumClass(pk);
            desc.isValueType = desc.isEnum || IsValueTypeClass(pk);
            if (desc.isEnum) {
                desc.enumBaseType = ResolveEnumBaseType(pk);
            }
            if (desc.isValueType && r.class_value_size != nullptr) {
                std::uint32_t align = 0;
                int vs = SehClassValueSize(r.class_value_size, pk, &align);
                if (vs > 0) desc.valueSize = static_cast<std::uint32_t>(vs);
            }
        }
    }
    return desc;
}

Shared::InvokeArgKind ToSharedArgKind(CallArgKind k) {
    switch (k) {
    case CallArgKind::Null:        return Shared::InvokeArgKind::Null;
    case CallArgKind::Bool:        return Shared::InvokeArgKind::Bool;
    case CallArgKind::Byte:        return Shared::InvokeArgKind::Byte;
    case CallArgKind::SByte:       return Shared::InvokeArgKind::SByte;
    case CallArgKind::Int16:       return Shared::InvokeArgKind::Int16;
    case CallArgKind::UInt16:      return Shared::InvokeArgKind::UInt16;
    case CallArgKind::Int32:       return Shared::InvokeArgKind::Int32;
    case CallArgKind::UInt32:      return Shared::InvokeArgKind::UInt32;
    case CallArgKind::Int64:       return Shared::InvokeArgKind::Int64;
    case CallArgKind::UInt64:      return Shared::InvokeArgKind::UInt64;
    case CallArgKind::Float:       return Shared::InvokeArgKind::Float;
    case CallArgKind::Double:      return Shared::InvokeArgKind::Double;
    case CallArgKind::Char:        return Shared::InvokeArgKind::Char;
    case CallArgKind::String:      return Shared::InvokeArgKind::String;
    case CallArgKind::Address:     return Shared::InvokeArgKind::Address;
    case CallArgKind::ValueStruct: return Shared::InvokeArgKind::ValueStruct;
    }
    return Shared::InvokeArgKind::Null;
}

struct ResolvedMethod {
    const MethodInfo*                           method        = nullptr;
    bool                                        isStatic      = false;
    bool                                        isAbstract    = false;
    std::vector<std::string>                    paramTypes;
    std::vector<Shared::FormalParamDescriptor>  params;
    std::string                                 returnType;
    bool                                        returnIsValueType = false;
    bool                                        returnIsEnum      = false;
    std::string                                 returnEnumBaseType;
    std::string                                 signatureString;
    std::string                                 humanSuffix;
    void*                                       address       = nullptr;
};

// Method resolver — single pass, returns the unique MethodInfo* for
// (klass, methodName) optionally narrowed by signature substring. Caller
// must already hold a ThreadAttachGuard.
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
            cand.params.reserve(paramCount);
            for (std::uint32_t i = 0; i < paramCount; ++i) {
                const Il2CppType* pt = SehParamType(r.method_get_param, m, i);
                Shared::FormalParamDescriptor pd = BuildParamDescriptor(pt);
                cand.paramTypes.push_back(pd.typeName);
                cand.params.push_back(std::move(pd));
            }
        }
        if (r.method_get_return_type != nullptr) {
            if (const Il2CppType* rt = SehReturnType(r.method_get_return_type, m)) {
                Shared::FormalParamDescriptor rd = BuildParamDescriptor(rt);
                cand.returnType         = std::move(rd.typeName);
                cand.returnIsValueType  = rd.isValueType;
                cand.returnIsEnum       = rd.isEnum;
                cand.returnEnumBaseType = std::move(rd.enumBaseType);
            }
        }
        cand.signatureString = Shared::MakeSignatureString(cand.paramTypes);
        cand.humanSuffix     = Shared::MakeHumanSuffix(cand.paramTypes);

        if (!signatureMatch.empty() &&
            cand.signatureString.find(signatureMatch) == std::string::npos) {
            continue;
        }

        if (matchCount == 0) first = std::move(cand);
        ++matchCount;
        if (matchCount >= 2) break;  // ambiguity bail-out
    }

    if (matchCount == 0) { outErr = "method_not_found"; return false; }
    if (matchCount  > 1) { outErr = "ambiguous_method";  return false; }

    first.address = first.isAbstract ? nullptr : ReadMethodPointer(first.method);

    out = std::move(first);
    return true;
}

// Decode the System.String at `strPtr` (a managed Il2CppString*) into UTF-8.
// Uses ManagedLayout pointer-scaled offsets (B-L8 symmetry with Mono).
std::string DecodeManagedString(std::uintptr_t strPtr) {
    if (strPtr == 0) return {};
    auto rd = [](std::uintptr_t a, void* dst, std::size_t n) {
        return Memory::SafeReadBytes(reinterpret_cast<void*>(a), dst, n);
    };
    std::int32_t len = 0;
    if (!rd(strPtr + ManagedLayout::kStringLengthOffset, &len, 4)) return {};
    if (len <= 0 || len > 65536) return {};

    std::vector<std::uint16_t> chars(static_cast<std::size_t>(len));
    if (!rd(strPtr + ManagedLayout::kStringCharsOffset, chars.data(),
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

    std::uintptr_t msgPtr = 0;
    auto rd = [](std::uintptr_t a, void* dst, std::size_t n) {
        return Memory::SafeReadBytes(reinterpret_cast<void*>(a), dst, n);
    };
    if (rd(reinterpret_cast<std::uintptr_t>(exc) + ManagedLayout::kExceptionMessageOffset,
           &msgPtr, sizeof(msgPtr)) &&
        msgPtr != 0) {
        std::string msg = DecodeManagedString(msgPtr);
        if (!msg.empty()) return typeName + ": " + msg;
    }
    return typeName;
}

// Decode a boxed primitive (or boxed enum via its underlying integer type).
template <typename UnboxFn>
void DecodePrimitiveUnbox(const std::string& primType, UnboxFn&& unbox, CallReturn& out) {
    if (primType == "System.Boolean") {
        std::uint8_t v = 0;
        if (unbox(1, &v)) { out.kind = CallReturn::Kind::Bool; out.i64 = (v != 0) ? 1 : 0; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (primType == "System.Byte") {
        std::uint8_t v = 0;
        if (unbox(1, &v)) { out.kind = CallReturn::Kind::UnsignedInt; out.u64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (primType == "System.SByte") {
        std::int8_t v = 0;
        if (unbox(1, &v)) { out.kind = CallReturn::Kind::SignedInt; out.i64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (primType == "System.Int16") {
        std::int16_t v = 0;
        if (unbox(2, &v)) { out.kind = CallReturn::Kind::SignedInt; out.i64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (primType == "System.UInt16" || primType == "System.Char") {
        std::uint16_t v = 0;
        if (unbox(2, &v)) { out.kind = CallReturn::Kind::UnsignedInt; out.u64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (primType == "System.Int32") {
        std::int32_t v = 0;
        if (unbox(4, &v)) { out.kind = CallReturn::Kind::SignedInt; out.i64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (primType == "System.UInt32") {
        std::uint32_t v = 0;
        if (unbox(4, &v)) { out.kind = CallReturn::Kind::UnsignedInt; out.u64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (primType == "System.Int64") {
        std::int64_t v = 0;
        if (unbox(8, &v)) { out.kind = CallReturn::Kind::SignedInt; out.i64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (primType == "System.UInt64") {
        std::uint64_t v = 0;
        if (unbox(8, &v)) { out.kind = CallReturn::Kind::UnsignedInt; out.u64 = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (primType == "System.Single") {
        float v = 0.f;
        if (unbox(4, &v)) { out.kind = CallReturn::Kind::Float; out.f = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (primType == "System.Double") {
        double v = 0.0;
        if (unbox(8, &v)) { out.kind = CallReturn::Kind::Float; out.f = v; }
        else              { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    if (primType == "System.IntPtr" || primType == "System.UIntPtr") {
        std::uintptr_t v = 0;
        if (unbox(sizeof(v), &v)) { out.kind = CallReturn::Kind::Address; out.u64 = v; }
        else                      { out.kind = CallReturn::Kind::Unsupported; }
        return;
    }
    out.kind = CallReturn::Kind::Unsupported;
}

// B-M3 — classify and decode the return value from il2cpp_runtime_invoke:
//   * System.Boolean → CallReturn::Kind::Bool
//   * Primitives → unboxed scalar
//   * Enums → unboxed underlying integer (never a transient boxed address)
//   * Non-enum value types → ValueStruct when opted in (wantStructReturn),
//     otherwise Unsupported (never a transient boxed address)
//   * Reference types → CallReturn::Kind::Address
void DecodeReturnValue(const ResolvedMethod& m,
                       Il2CppObject* boxed,
                       bool wantStructReturn,
                       std::uint32_t sizeHint,
                       CallReturn& out) {
    out.typeName = m.returnType;

    const auto& r = GetResolver();
    Il2CppClass* vk = (boxed != nullptr && r.object_get_class != nullptr)
        ? SehObjectGetClass(r.object_get_class, boxed)
        : nullptr;

    const bool objIsEnum = (vk != nullptr) ? IsEnumClass(vk) : m.returnIsEnum;
    const std::string enumBase = objIsEnum
        ? ((vk != nullptr) ? ResolveEnumBaseType(vk) : m.returnEnumBaseType)
        : std::string{};
    const bool objIsValueType = objIsEnum || ((vk != nullptr) ? IsValueTypeClass(vk) : m.returnIsValueType);

    const Shared::ReturnDecodePlan plan = Shared::ClassifyReturnObject(
        m.returnType, boxed == nullptr, objIsEnum, enumBase, objIsValueType, wantStructReturn);

    auto unbox = [&](std::size_t n, void* dst) -> bool {
        if (r.object_unbox == nullptr) return false;
        void* p = SehObjectUnbox(r.object_unbox, boxed);
        if (p == nullptr) return false;
        return Memory::SafeReadBytes(p, dst, n);
    };

    switch (plan.action) {
    case Shared::ReturnDecodeAction::Void:
        out.kind = CallReturn::Kind::Void;
        return;
    case Shared::ReturnDecodeAction::NullReference:
        out.kind = CallReturn::Kind::Address;
        out.u64  = 0;
        return;
    case Shared::ReturnDecodeAction::Primitive:
        DecodePrimitiveUnbox(plan.primitiveType, unbox, out);
        return;
    case Shared::ReturnDecodeAction::String:
        out.kind = CallReturn::Kind::String;
        out.u64  = reinterpret_cast<std::uintptr_t>(boxed);
        out.s    = DecodeManagedString(reinterpret_cast<std::uintptr_t>(boxed));
        return;
    case Shared::ReturnDecodeAction::ValueStruct: {
        std::uint32_t size = sizeHint;
        if (vk != nullptr && r.class_value_size != nullptr) {
            std::uint32_t align = 0;
            int vs = SehClassValueSize(r.class_value_size, vk, &align);
            if (vs > 0) size = static_cast<std::uint32_t>(vs);
        }
        if (size > 0 && size <= kValueStructMaxBytes && r.object_unbox != nullptr) {
            void* p = SehObjectUnbox(r.object_unbox, boxed);
            out.bytes.assign(size, 0);
            if (p != nullptr && Memory::SafeReadBytes(p, out.bytes.data(), size)) {
                out.kind = CallReturn::Kind::ValueStruct;
                return;
            }
            out.bytes.clear();
        }
        out.kind = CallReturn::Kind::Unsupported;
        return;
    }
    case Shared::ReturnDecodeAction::Unsupported:
        out.kind = CallReturn::Kind::Unsupported;
        return;
    case Shared::ReturnDecodeAction::ReferenceAddr:
        out.kind = CallReturn::Kind::Address;
        out.u64  = reinterpret_cast<std::uintptr_t>(boxed);
        return;
    }
}

// Write one already-validated value-slot arg into its 8-byte backing slot
// and point paramPtr at `&slot`.
bool WriteValueSlotArg(const CallArg& arg,
                       std::uint64_t* slot,
                       void*& paramPtr,
                       std::string& outErr) {
    auto write = [slot](const void* src, std::size_t n) {
        std::memcpy(slot, src, n);
    };

    switch (arg.kind) {
    case CallArgKind::Null:
        *slot = 0u;
        paramPtr = slot;
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
    case CallArgKind::Address: {
        // System.IntPtr / System.UIntPtr / unmanaged pointer — value type, so
        // store pointer-sized value in slot and pass &slot.
        std::uintptr_t v = static_cast<std::uintptr_t>(arg.u64);
        write(&v, sizeof(v));
        paramPtr = slot;
        return true;
    }
    case CallArgKind::String:
    case CallArgKind::ValueStruct:
        outErr = "arg_type_mismatch";
        return false;
    }
    outErr = "unknown_arg_kind";
    return false;
}

}  // namespace

CallMethodResult CallMethod(Il2CppClass* klass,
                            const std::string& methodName,
                            const std::string& signatureMatch,
                            std::uintptr_t instance,
                            const std::vector<CallArg>& args,
                            bool wantStructReturn,
                            std::uint32_t structReturnSizeHint) {
    CallMethodResult result;
    auto t0 = std::chrono::steady_clock::now();
    auto finish = [&]() -> CallMethodResult {
        auto t1 = std::chrono::steady_clock::now();
        result.elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(t1 - t0).count();
        Log::Write(MagicStrings::LogCategoryWalk,
                   std::string{"callMethod: "} + methodName + result.methodSignature +
                   (result.ok ? " ok" :
                    (result.exception.empty() ? " err=" + result.error
                                              : " exc=" + result.exception)));
        return result;
    };

    if (klass == nullptr)   { result.error = "klass_is_null";     return finish(); }
    if (methodName.empty()) { result.error = "method_name_empty"; return finish(); }

    const auto& r = GetResolver();
    if (!r.resolved)                 { result.error = "il2cpp runtime not resolved";   return finish(); }
    if (r.runtime_invoke == nullptr) { result.error = "il2cpp_runtime_invoke unbound"; return finish(); }

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) { result.error = "no il2cpp domain"; return finish(); }

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) { result.error = "il2cpp_thread_attach failed"; return finish(); }

    ResolvedMethod m;
    std::string resolveErr;
    if (!ResolveOverload(klass, methodName, signatureMatch, m, resolveErr)) {
        result.error = resolveErr;
        return finish();
    }
    result.addressCalled   = m.address;
    result.methodSignature = m.signatureString;
    result.humanSuffix     = m.humanSuffix;
    result.ret.typeName    = m.returnType;

    if (m.isAbstract) {
        result.error = "method_is_abstract";
        return finish();
    }

    // 19c — instance dispatch supported.
    void* invokeObj = nullptr;
    if (!m.isStatic) {
        if (instance == 0) {
            result.error = "instance_required_for_non_static";
            return finish();
        }
        invokeObj = reinterpret_cast<void*>(instance);
    }

    if (args.size() != m.params.size()) {
        result.error = "arg_count_mismatch";
        return finish();
    }

    // B-M2 — RAII GC-root pin list keeps every allocated Il2CppString* alive
    // across subsequent allocations and through il2cpp_runtime_invoke.
    using PinFn  = std::uintptr_t (*)(void*);
    using FreeFn = void (*)(std::uintptr_t);
    PinFn pinFn = (r.gchandle_new != nullptr && r.gchandle_free != nullptr)
        ? +[](void* obj) -> std::uintptr_t {
            const auto& rr = GetResolver();
            return SehGcHandleNew(rr.gchandle_new, reinterpret_cast<Il2CppObject*>(obj), true);
          }
        : nullptr;
    FreeFn freeFn = (r.gchandle_new != nullptr && r.gchandle_free != nullptr)
        ? +[](std::uintptr_t h) {
            const auto& rr = GetResolver();
            SehGcHandleFree(rr.gchandle_free, h);
          }
        : nullptr;
    Shared::ScopedGcPinList<std::uintptr_t, PinFn, FreeFn> gcPins(pinFn, freeFn);

    // Build the params array with B-M1 formal-type validation.
    std::vector<std::uint64_t> slots(args.size(), 0u);
    std::vector<void*>         paramPtrs(args.size(), nullptr);
    std::vector<std::vector<std::uint8_t>> structBackings;
    structBackings.reserve(args.size());
    for (std::size_t i = 0; i < args.size(); ++i) {
        const Shared::ArgValidationResult val = Shared::ValidateCallArg(
            ToSharedArgKind(args[i].kind),
            args[i].bytes.size(),
            kValueStructMaxBytes,
            m.params[i]);
        if (!val.ok) {
            result.error = val.error.empty() ? "arg_type_mismatch" : val.error;
            return finish();
        }

        switch (val.route) {
        case Shared::MarshalRoute::ValueStructBuf: {
            const std::size_t n = args[i].bytes.size();
            structBackings.emplace_back(kValueStructMaxBytes, std::uint8_t{0});
            std::memcpy(structBackings.back().data(), args[i].bytes.data(), n);
            paramPtrs[i] = structBackings.back().data();
            break;
        }
        case Shared::MarshalRoute::ValueSlot: {
            std::string err;
            if (!WriteValueSlotArg(args[i], &slots[i], paramPtrs[i], err)) {
                result.error = err.empty() ? "arg_marshal_failed" : err;
                return finish();
            }
            break;
        }
        case Shared::MarshalRoute::DirectPointer:
            slots[i]     = (args[i].kind == CallArgKind::Null) ? 0u : args[i].u64;
            paramPtrs[i] = reinterpret_cast<void*>(static_cast<std::uintptr_t>(slots[i]));
            break;
        case Shared::MarshalRoute::ManagedString: {
            if (r.string_new == nullptr) {
                result.error = "il2cpp_string_new unbound";
                return finish();
            }
            Il2CppString* s = SehStringNew(r.string_new, args[i].s.c_str());
            if (s == nullptr) {
                result.error = "il2cpp_string_new returned null";
                return finish();
            }
            if (!gcPins.Pin(s)) {
                result.error = "string_arg_gc_pin_failed";
                return finish();
            }
            slots[i]     = reinterpret_cast<std::uintptr_t>(s);
            paramPtrs[i] = s;
            break;
        }
        case Shared::MarshalRoute::Reject:
            result.error = "arg_type_mismatch";
            return finish();
        }
    }

    Il2CppException* exc = nullptr;
    Il2CppObject*    ret = nullptr;
    unsigned long faultCode = 0;
    const bool invoked = SehRuntimeInvoke(r.runtime_invoke,
                                          m.method,
                                          invokeObj,
                                          paramPtrs.empty() ? nullptr : paramPtrs.data(),
                                          &exc,
                                          &ret,
                                          &faultCode);
    gcPins.KeepAlive();
    if (!invoked) {
        RuntimeHealth::MarkRuntimeFault("il2cpp_runtime_invoke", faultCode);
        Log::Write(MagicStrings::LogCategoryWalk,
                   "WARN callMethod: " + RuntimeHealth::FormatReason());
        result.error = "native_crash_in_runtime_invoke";
        return finish();
    }

    if (exc != nullptr) {
        result.exception    = FormatException(exc);
        if (result.exception.empty()) result.exception = "managed exception (no message)";
        result.ret.kind     = CallReturn::Kind::Void;
        result.ret.typeName = m.returnType;
    } else {
        DecodeReturnValue(m, ret, wantStructReturn, structReturnSizeHint, result.ret);
        result.ok = true;
    }

    return finish();
}

}  // namespace Discrete::IL2CPP
