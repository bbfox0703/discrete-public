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
// separate DLLs (UnityDumper-Mono.dll vs UnityDumper-IL2CPP.dll), so sharing
// a single namespace would force one to depend on the other. The wire
// shapes ExportAPI.cpp emits on each side are byte-identical.
#pragma once

#include "MonoTypes.h"

#include <cstdint>
#include <string>
#include <vector>

namespace UnityCEDumper::Mono {

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
};

struct CallArg {
    CallArgKind   kind = CallArgKind::Null;
    std::int64_t  i64  = 0;
    std::uint64_t u64  = 0;
    double        f    = 0.0;
    std::string   s;
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
        Unsupported,
    };
    Kind          kind = Kind::Void;
    std::string   typeName;
    std::int64_t  i64  = 0;
    std::uint64_t u64  = 0;
    double        f    = 0.0;
    std::string   s;
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
CallMethodResult CallMethod(MonoClass* klass,
                            const std::string& methodName,
                            const std::string& signatureMatch,
                            std::uintptr_t instance,
                            const std::vector<CallArg>& args);

}  // namespace UnityCEDumper::Mono
