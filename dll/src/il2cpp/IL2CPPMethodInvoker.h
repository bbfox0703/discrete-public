// IL2CPPMethodInvoker.h — Phase 19a callMethod backend (IL2CPP).
//
// Resolves a method by class + name (+ optional signatureMatch substring),
// marshals typed CallArgs into the IL2CPP `void**` params array, invokes
// `il2cpp_runtime_invoke`, captures managed exceptions, and unboxes the
// return value into a typed CallReturn. 19a is static-only on IL2CPP;
// instance dispatch + string marshaling land in 19c.
#pragma once

#include "IL2CPPTypes.h"

#include <cstdint>
#include <string>
#include <vector>

namespace UnityCEDumper::IL2CPP {

// Wire `type` tag for an argument or return value. Mirrors the JSON arg
// schema in docs/lua-invoke.md exactly. ExportAPI.cpp parses the JSON and
// produces a CallArg per element of the request `args` array.
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
    String,    // Reserved for 19c — rejected by 19a invoker.
    Address,
};

struct CallArg {
    CallArgKind   kind = CallArgKind::Null;
    std::int64_t  i64  = 0;        // signed integer carrier
    std::uint64_t u64  = 0;        // unsigned integer / address / char carrier
    double        f    = 0.0;      // float / double carrier (separate so cast-loss is visible)
    std::string   s;               // utf-8 (string args — deferred to 19c)
};

// Discriminated return shape. The invoker fills `kind` per the resolved
// method's formal returnType; ExportAPI.cpp picks the right JSON key
// (returnValue: number / null / hex string).
struct CallReturn {
    enum class Kind {
        Void,
        Bool,
        SignedInt,    // any signed integer width — value lives in i64
        UnsignedInt,  // any unsigned integer width — value in u64
        Float,        // single or double — value in f
        Address,      // reference-type return, raw managed pointer in u64
        String,       // 19c — managed string decoded to UTF-8 in s
        Unsupported,  // value-type-by-value return we can't decode in 19a
    };
    Kind          kind = Kind::Void;
    std::string   typeName;        // e.g. "System.Int32", "System.Void"
    std::int64_t  i64  = 0;
    std::uint64_t u64  = 0;
    double        f    = 0.0;
    std::string   s;               // for Kind::String
};

struct CallMethodResult {
    bool          ok = false;             // true iff call ran AND no managed exception
    std::string   error;                  // protocol-level (resolution / arg / config). Empty if call ran.
    std::string   exception;              // managed exception "Type: message". Empty if no exception.
    CallReturn    ret;
    void*         addressCalled   = nullptr;
    std::string   methodSignature;        // rendered "(System.Int32, System.Single)"
    std::string   humanSuffix;
    std::int64_t  elapsedMs       = 0;
};

// 19a: pass `instance == 0` for static. Non-zero instance triggers
// `instance_methods_not_supported_in_19a` (lifts in 19c).
CallMethodResult CallMethod(Il2CppClass* klass,
                            const std::string& methodName,
                            const std::string& signatureMatch,
                            std::uintptr_t instance,
                            const std::vector<CallArg>& args);

}  // namespace UnityCEDumper::IL2CPP
