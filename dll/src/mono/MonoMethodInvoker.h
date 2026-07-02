// MonoMethodInvoker.h — Phase 19b/c callMethod backend (Mono).
//
// Mirror of IL2CPPMethodInvoker. Resolves a method by class + name (+ optional
// signatureMatch substring), marshals typed CallArgs into Mono's `void**`
// params array, invokes `mono_runtime_invoke`, captures managed exceptions,
// and unboxes the return value into a typed CallReturn. 19b delivered
// static-only; 19c added instance dispatch + string in/out marshaling.
//
// CallArg / CallReturn / CallMethodResult are intentionally namespace-local
// (Mono::) duplicates of the IL2CPP::* types — both backends compile into
// separate DLLs (Discrete-Mono.dll vs Discrete-IL2CPP.dll), so sharing
// a single namespace would force one to depend on the other. The wire
// shapes ExportAPI.cpp emits on each side are byte-identical.
#pragma once

#include "MonoTypes.h"

#include <cstdint>
#include <string>
#include <vector>

namespace Discrete::Mono {

enum class CallArgKind {
    Null,
    Bool,
    Byte,
    SByte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    Double,
    Char,
    String,    // 19c — alloc MonoString* via mono_string_new(domain, utf8).
    Address,
    ValueStruct,  // value-type-by-value arg (Vector3/Quaternion/…) — raw
                  // little-endian struct bytes carried in `bytes`. params[i]
                  // points at a padded backing buffer (value-type convention).
};

struct CallArg {
    CallArgKind   kind = CallArgKind::Null;
    std::int64_t  i64  = 0;
    std::uint64_t u64  = 0;
    double        f    = 0.0;
    std::string   s;
    std::vector<std::uint8_t> bytes;  // ValueStruct — raw struct value bytes
};

struct CallReturn {
    enum class Kind {
        Void,
        Bool,
        SignedInt,
        UnsignedInt,
        Float,
        Address,
        String,
        ValueStruct,  // value-type-by-value return — raw unboxed bytes in `bytes`
        Unsupported,
    };
    Kind          kind = Kind::Void;
    std::string   typeName;
    std::int64_t  i64  = 0;
    std::uint64_t u64  = 0;
    double        f    = 0.0;
    std::string   s;
    std::vector<std::uint8_t> bytes;  // for Kind::ValueStruct — raw struct bytes
};

struct CallMethodResult {
    bool          ok = false;
    std::string   error;
    std::string   exception;
    CallReturn    ret;
    void*         addressCalled   = nullptr;
    std::string   methodSignature;
    std::string   humanSuffix;
    std::int64_t  elapsedMs       = 0;
};

// 19c: pass `instance == 0` for static methods (silently ignored if the
// resolved method is non-static); pass the managed object pointer
// (typically from findInstances / whatIsAt) for instance dispatch. Method
// resolution validates and routes accordingly.
// `wantStructReturn` opts into decoding a value-type-by-value return
// (Vector3/Quaternion/…) into CallReturn::bytes: the boxed return is unboxed
// and `class_value_size` (or `structReturnSizeHint` when the export is
// unbound / returns garbage) bytes are copied out. Off by default so the
// legacy shape (Unsupported + note) is unchanged for callers that don't ask.
CallMethodResult CallMethod(MonoClass* klass,
                            const std::string& methodName,
                            const std::string& signatureMatch,
                            std::uintptr_t instance,
                            const std::vector<CallArg>& args,
                            bool wantStructReturn = false,
                            std::uint32_t structReturnSizeHint = 0);

}  // namespace Discrete::Mono
