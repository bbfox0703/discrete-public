// MethodInvokeValidation.h — shared B-M1 / B-M2 / B-M3 helpers for Mono &
// IL2CPP MethodInvoker backends.
//
// Header-only and runtime-independent so both Discrete-Mono.dll and
// Discrete-IL2CPP.dll enforce identical argument validation, GC-root pinning,
// and return-value classification rules — and so dll_core_test can exercise
// every rule directly on x64 and x86 without a live Unity runtime.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace Discrete::Shared {

// Backend-agnostic mirror of Mono::CallArgKind / IL2CPP::CallArgKind.
enum class InvokeArgKind {
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
    String,
    Address,
    ValueStruct,
};

// Runtime-resolved metadata for one formal method parameter.
struct FormalParamDescriptor {
    std::string   typeName;          // e.g. "System.Int32", "UnityEngine.Vector3", "MyNs.MyEnum"
    bool          isValueType = false;  // true for primitives, enums, and structs
    bool          isEnum      = false;  // true for CLR enum types
    std::string   enumBaseType;      // e.g. "System.Int32", "System.Byte" (empty if unknown)
    std::uint32_t valueSize   = 0;   // unboxed byte size from class_value_size (0 if unknown)
    bool          metaKnown   = false;  // true when runtime resolved class metadata
};

// How the invoker must pass a validated argument into runtime_invoke's
// `void** params` array.
enum class MarshalRoute {
    Reject,
    ValueSlot,       // 8-byte slot holds value bytes; params[i] = &slot
    ValueStructBuf,  // padded byte buffer holds struct image; params[i] = buf.data()
    DirectPointer,   // reference type (Null / Address); params[i] = reinterpret_cast<void*>(u64)
    ManagedString,   // allocate managed string + pin GC root; params[i] = strPtr
};

struct ArgValidationResult {
    bool         ok    = false;
    MarshalRoute route = MarshalRoute::Reject;
    std::string  error;  // empty on ok; "arg_type_mismatch" or "value_struct_arg_bad_size"
};

// Exact byte width of known CLR primitive types (0 for non-primitives).
inline std::uint32_t PrimitiveWidthBytes(const std::string& typeName) {
    if (typeName == "System.Boolean" || typeName == "System.Byte" || typeName == "System.SByte") {
        return 1u;
    }
    if (typeName == "System.Int16" || typeName == "System.UInt16" || typeName == "System.Char") {
        return 2u;
    }
    if (typeName == "System.Int32" || typeName == "System.UInt32" || typeName == "System.Single") {
        return 4u;
    }
    if (typeName == "System.Int64" || typeName == "System.UInt64" || typeName == "System.Double") {
        return 8u;
    }
    if (typeName == "System.IntPtr" || typeName == "System.UIntPtr") {
        return static_cast<std::uint32_t>(sizeof(void*));
    }
    return 0u;
}

// Map a CLR primitive type name (excluding IntPtr/UIntPtr, which accept
// Address/Null) to its exact required InvokeArgKind. Returns false if
// `typeName` is not a scalar CLR primitive.
inline bool TryGetScalarPrimitiveArgKind(const std::string& typeName, InvokeArgKind& outKind) {
    if (typeName == "System.Boolean") { outKind = InvokeArgKind::Bool;   return true; }
    if (typeName == "System.Byte")    { outKind = InvokeArgKind::Byte;   return true; }
    if (typeName == "System.SByte")   { outKind = InvokeArgKind::SByte;  return true; }
    if (typeName == "System.Int16")   { outKind = InvokeArgKind::Int16;  return true; }
    if (typeName == "System.UInt16")  { outKind = InvokeArgKind::UInt16; return true; }
    if (typeName == "System.Char")    { outKind = InvokeArgKind::Char;   return true; }
    if (typeName == "System.Int32")   { outKind = InvokeArgKind::Int32;  return true; }
    if (typeName == "System.UInt32")  { outKind = InvokeArgKind::UInt32; return true; }
    if (typeName == "System.Int64")   { outKind = InvokeArgKind::Int64;  return true; }
    if (typeName == "System.UInt64")  { outKind = InvokeArgKind::UInt64; return true; }
    if (typeName == "System.Single")  { outKind = InvokeArgKind::Float;  return true; }
    if (typeName == "System.Double")  { outKind = InvokeArgKind::Double; return true; }
    return false;
}

inline bool IsArrayTypeName(const std::string& typeName) {
    return typeName.size() >= 2 && typeName.back() == ']';
}

// B-M1 — strict allowlist matching between a caller-supplied argument kind
// (and optional struct byte length) and the resolved formal parameter type.
// Prevents passing a native stack/heap slot pointer where a managed reference
// is expected, passing a raw pointer where a value-type struct is expected,
// or OOB-reading past an 8-byte slot for multi-field structs.
inline ArgValidationResult ValidateCallArg(InvokeArgKind kind,
                                           std::size_t structBytesSize,
                                           std::uint32_t maxStructBytes,
                                           const FormalParamDescriptor& param) {
    auto reject = [](const char* err) {
        return ArgValidationResult{false, MarshalRoute::Reject, err};
    };
    auto accept = [](MarshalRoute route) {
        return ArgValidationResult{true, route, {}};
    };

    const std::string& ft = param.typeName;
    if (ft.empty()) {
        return reject("arg_type_mismatch");
    }

    // By-ref parameters (ref / out / in) are out of scope for callMethod.
    if (ft.back() == '&') {
        return reject("arg_type_mismatch");
    }

    // Unsafe unmanaged pointer (e.g. System.Byte*, System.Void*): value-slot
    // pointer marshaled via &slot.
    if (ft.back() == '*') {
        if (kind == InvokeArgKind::Address || kind == InvokeArgKind::Null) {
            return accept(MarshalRoute::ValueSlot);
        }
        return reject("arg_type_mismatch");
    }

    // System.IntPtr / System.UIntPtr are CLR value types — runtime_invoke
    // dereferences params[i] once to read the pointer-sized value.
    if (ft == "System.IntPtr" || ft == "System.UIntPtr") {
        if (kind == InvokeArgKind::Address || kind == InvokeArgKind::Null) {
            return accept(MarshalRoute::ValueSlot);
        }
        return reject("arg_type_mismatch");
    }

    // Scalar CLR primitives — exact kind match required.
    InvokeArgKind expectedPrim = InvokeArgKind::Null;
    if (TryGetScalarPrimitiveArgKind(ft, expectedPrim)) {
        return (kind == expectedPrim) ? accept(MarshalRoute::ValueSlot)
                                      : reject("arg_type_mismatch");
    }

    // System.String — managed string allocation or null reference only.
    if (ft == "System.String") {
        if (kind == InvokeArgKind::String) return accept(MarshalRoute::ManagedString);
        if (kind == InvokeArgKind::Null)   return accept(MarshalRoute::DirectPointer);
        return reject("arg_type_mismatch");
    }

    // System.Object — any reference (Address / Null) or a freshly allocated
    // managed string (System.String derives from System.Object, e.g.
    // UnityEngine.Debug.Log(object)).
    if (ft == "System.Object") {
        if (kind == InvokeArgKind::Address || kind == InvokeArgKind::Null) {
            return accept(MarshalRoute::DirectPointer);
        }
        if (kind == InvokeArgKind::String) {
            return accept(MarshalRoute::ManagedString);
        }
        return reject("arg_type_mismatch");
    }

    // Enum formal parameter — accept the matching underlying integer kind (or
    // a ValueStruct with the exact underlying byte width).
    if (param.isEnum) {
        if (!param.enumBaseType.empty()) {
            InvokeArgKind enumKind = InvokeArgKind::Null;
            if (TryGetScalarPrimitiveArgKind(param.enumBaseType, enumKind) && kind == enumKind) {
                return accept(MarshalRoute::ValueSlot);
            }
            std::uint32_t expectedWidth = PrimitiveWidthBytes(param.enumBaseType);
            if (kind == InvokeArgKind::ValueStruct) {
                if (structBytesSize == 0 || structBytesSize > maxStructBytes) {
                    return reject("value_struct_arg_bad_size");
                }
                if (expectedWidth > 0 && structBytesSize == expectedWidth) {
                    return accept(MarshalRoute::ValueStructBuf);
                }
            }
            return reject("arg_type_mismatch");
        }
        // Fallback when enum basetype export was unavailable: match by
        // valueSize (defaulting to 4-byte Int32/UInt32 per ECMA-335).
        const std::uint32_t width = (param.valueSize > 0) ? param.valueSize : 4u;
        if (width == 1u && (kind == InvokeArgKind::Byte || kind == InvokeArgKind::SByte)) {
            return accept(MarshalRoute::ValueSlot);
        }
        if (width == 2u && (kind == InvokeArgKind::Int16 || kind == InvokeArgKind::UInt16)) {
            return accept(MarshalRoute::ValueSlot);
        }
        if (width == 4u && (kind == InvokeArgKind::Int32 || kind == InvokeArgKind::UInt32)) {
            return accept(MarshalRoute::ValueSlot);
        }
        if (width == 8u && (kind == InvokeArgKind::Int64 || kind == InvokeArgKind::UInt64)) {
            return accept(MarshalRoute::ValueSlot);
        }
        if (kind == InvokeArgKind::ValueStruct) {
            if (structBytesSize == 0 || structBytesSize > maxStructBytes) {
                return reject("value_struct_arg_bad_size");
            }
            if (structBytesSize == width) {
                return accept(MarshalRoute::ValueStructBuf);
            }
        }
        return reject("arg_type_mismatch");
    }

    // Non-enum, non-primitive value type (Vector3, Quaternion, Color,
    // DateTime, Nullable<T>, user struct): accepts ValueStruct ONLY, and the
    // byte count must match class_value_size when known.
    if (param.isValueType) {
        if (kind != InvokeArgKind::ValueStruct) {
            return reject("arg_type_mismatch");
        }
        if (structBytesSize == 0 || structBytesSize > maxStructBytes) {
            return reject("value_struct_arg_bad_size");
        }
        if (param.valueSize > 0 && structBytesSize != param.valueSize) {
            return reject("arg_type_mismatch");
        }
        return accept(MarshalRoute::ValueStructBuf);
    }

    // Reference types (arrays, classes, interfaces, delegates). When
    // `metaKnown` is false (runtime lacks class_from_type / is_valuetype),
    // fail closed against scalars/strings/structs and accept Address / Null
    // only so we never feed a native slot pointer to a reference-type param.
    if (IsArrayTypeName(ft) || !param.isValueType) {
        if (kind == InvokeArgKind::Address || kind == InvokeArgKind::Null) {
            return accept(MarshalRoute::DirectPointer);
        }
        return reject("arg_type_mismatch");
    }

    return reject("arg_type_mismatch");
}

// B-M2 — RAII GC-root pin list for managed objects allocated during argument
// marshaling (e.g. mono_string_new / il2cpp_string_new).
//
// Keeps BOTH:
//   1. A pinned GCHandle via runtime gchandle_new / gchandle_free when bound
//      (protects across moving/compact GC and across CRT-heap containers), and
//   2. A volatile stack-resident root table (up to kMaxStackRoots entries)
//      kept live until ReleaseAll(), so BDWGC conservative stack scanning also
//      roots the objects even on legacy builds lacking gchandle exports.
template <typename HandleT, typename PinFn, typename FreeFn>
class ScopedGcPinList {
public:
    static constexpr std::size_t kMaxStackRoots = 32u;

    ScopedGcPinList(PinFn pinFn, FreeFn freeFn)
        : pinFn_(pinFn), freeFn_(freeFn) {}

    ~ScopedGcPinList() { ReleaseAll(); }

    ScopedGcPinList(const ScopedGcPinList&) = delete;
    ScopedGcPinList& operator=(const ScopedGcPinList&) = delete;

    bool Pin(void* obj) {
        if (obj == nullptr) return true;
        bool storedOnStack = false;
        if (stackCount_ < kMaxStackRoots) {
            stackRoots_[stackCount_++] = obj;
            storedOnStack = true;
        }
        if (pinFn_ != nullptr && freeFn_ != nullptr) {
            HandleT h = pinFn_(obj);
            if (h != HandleT{}) {
                handles_.push_back(h);
                return true;
            }
            return storedOnStack;
        }
        return storedOnStack;
    }

    // Prevent the optimizer from shortening the lifetime of stackRoots_
    // before runtime_invoke completes.
    void KeepAlive() const noexcept {
        volatile std::size_t n = stackCount_;
        if (n > 0) {
            void* volatile p = stackRoots_[n - 1];
            (void)p;
        }
    }

    void ReleaseAll() noexcept {
        KeepAlive();
        if (freeFn_ != nullptr) {
            while (!handles_.empty()) {
                HandleT h = handles_.back();
                handles_.pop_back();
                if (h != HandleT{}) {
                    freeFn_(h);
                }
            }
        } else {
            handles_.clear();
        }
        for (std::size_t i = 0; i < stackCount_; ++i) {
            stackRoots_[i] = nullptr;
        }
        stackCount_ = 0;
    }

    std::size_t HandleCount()    const noexcept { return handles_.size(); }
    std::size_t StackRootCount() const noexcept { return stackCount_; }

private:
    PinFn                 pinFn_;
    FreeFn                freeFn_;
    void* volatile        stackRoots_[kMaxStackRoots] = {};
    std::size_t           stackCount_ = 0;
    std::vector<HandleT>  handles_;
};

// B-M3 — classification of a runtime_invoke return object so both backends
// handle Void, primitives (including Bool), String, Enum, opted-in
// ValueStruct, non-opted-in ValueType (-> Unsupported, never ephemeral boxed
// Address), and genuine ReferenceType identically.
enum class ReturnDecodeAction {
    Void,
    NullReference,
    Primitive,      // unbox `primitiveType` (formal primitive or enum base type)
    String,         // decode managed string at boxed ptr
    ValueStruct,    // unbox struct bytes (caller opted in via wantStructReturn)
    Unsupported,    // value type without opt-in (do NOT expose boxed address)
    ReferenceAddr,  // genuine reference-type object pointer
};

struct ReturnDecodePlan {
    ReturnDecodeAction action = ReturnDecodeAction::Void;
    std::string        primitiveType;  // populated when action == Primitive
};

inline bool IsKnownPrimitiveReturnType(const std::string& typeName) {
    return PrimitiveWidthBytes(typeName) != 0u;
}

inline ReturnDecodePlan ClassifyReturnObject(const std::string& returnType,
                                             bool boxedIsNull,
                                             bool objectIsEnum,
                                             const std::string& enumBaseType,
                                             bool objectIsValueType,
                                             bool wantStructReturn) {
    if (returnType.empty() || returnType == "System.Void") {
        return {ReturnDecodeAction::Void, {}};
    }
    if (boxedIsNull) {
        return {ReturnDecodeAction::NullReference, {}};
    }
    if (IsKnownPrimitiveReturnType(returnType)) {
        return {ReturnDecodeAction::Primitive, returnType};
    }
    if (returnType == "System.String") {
        return {ReturnDecodeAction::String, {}};
    }
    if (objectIsEnum) {
        const std::string base = IsKnownPrimitiveReturnType(enumBaseType)
            ? enumBaseType
            : std::string{"System.Int32"};
        return {ReturnDecodeAction::Primitive, base};
    }
    if (objectIsValueType) {
        return wantStructReturn
            ? ReturnDecodePlan{ReturnDecodeAction::ValueStruct, {}}
            : ReturnDecodePlan{ReturnDecodeAction::Unsupported, {}};
    }
    return {ReturnDecodeAction::ReferenceAddr, {}};
}

}  // namespace Discrete::Shared
