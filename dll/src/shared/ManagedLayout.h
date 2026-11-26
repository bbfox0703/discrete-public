// ManagedLayout.h — managed-object byte offsets that scale with pointer width.
//
// Mono and IL2CPP share the same managed-object layout convention:
//
//   MonoObject / Il2CppObject prefix:
//     +0          vtable*         (sizeof(void*))
//     +sizeof(void*)  sync block  (sizeof(void*))
//   = header is 2 * sizeof(void*) bytes (16 on x64, 8 on x86).
//
//   MonoString / Il2CppString (reference type — header + fixed body):
//     +header          length          (int32, always 4 bytes)
//     +header + 4      chars[0]        (uint16 UTF-16 units)
//
//   MonoArray / Il2CppArray (SZARRAY — single-dimension):
//     +header                  bounds*    (sizeof(void*) — NULL for SZARRAY)
//     +header + sizeof(void*)  max_length (uintptr_t — total length)
//     +header + 2*sizeof(void*) vector[0] (first element)
//
//   System.Exception (corlib layout, Mono and IL2CPP both use this):
//     +header          _className   (MonoString* / Il2CppString*)   legacy, often null
//     +header + ptr    _message     (MonoString* / Il2CppString*)   ← we read this
//     +header + 2*ptr  _data        (IDictionary*)
//     +header + 3*ptr  _innerException
//
// These offsets were hard-coded as x64 constants (0x10 / 0x14 / 0x18 / 0x20)
// across ExportAPI.cpp + MonoMethodInvoker.cpp before Phase 12d. Switching
// to constexpr expressions in sizeof(void*) lets the same source compile
// correctly for both the x64 and x86 Mono backends — no per-arch macros.
// IL2CPP is x64-only (no shipping 32-bit targets), but the IL2CPP code
// paths that share these offsets pick up the right values automatically.
#pragma once

#include <cstddef>
#include <cstdint>
#include <type_traits>

namespace Discrete::ManagedLayout {

inline constexpr std::size_t kPointerSize = sizeof(void*);

// MonoObject / Il2CppObject header (vtable + sync). 16 on x64, 8 on x86.
inline constexpr std::size_t kObjectHeader = 2 * kPointerSize;

// MonoString — int32 length at start of body, UTF-16 chars right after.
inline constexpr std::size_t kStringLengthOffset = kObjectHeader;
inline constexpr std::size_t kStringCharsOffset  = kObjectHeader + sizeof(std::int32_t);

// MonoArray (SZARRAY) — bounds*, max_length (uintptr_t), vector[].
inline constexpr std::size_t kArrayBoundsOffset    = kObjectHeader;
inline constexpr std::size_t kArrayMaxLengthOffset = kObjectHeader + kPointerSize;
inline constexpr std::size_t kArrayDataOffset      = kObjectHeader + 2 * kPointerSize;

// System.Exception._message (the field we decode for managed-exception
// reporting in Phase 19c's invoker). Skips _className.
inline constexpr std::size_t kExceptionMessageOffset = kObjectHeader + kPointerSize;

// ECMA-335 FieldAttributes flags used across both Mono and IL2CPP walkers.
inline constexpr std::uint32_t kFieldAttrStatic  = 0x10u;
inline constexpr std::uint32_t kFieldAttrLiteral = 0x40u;

// Normalize any runtime field offset (int on Mono, size_t on IL2CPP where
// THREAD_STATIC_FIELD_OFFSET is (size_t)-1) into a unified int32_t with -1 as
// the sentinel for thread-static / invalid offsets (B-M4).
template <typename T>
inline constexpr std::int32_t NormalizeFieldOffset32(T raw) noexcept {
    if constexpr (std::is_signed_v<T>) {
        if (raw < 0) return -1;
        if constexpr (sizeof(T) > 4) {
            if (static_cast<std::int64_t>(raw) > 0x7FFFFFFFLL) return -1;
        }
        return static_cast<std::int32_t>(raw);
    } else {
        if (raw > static_cast<T>(0x7FFFFFFFu)) return -1;
        return static_cast<std::int32_t>(raw);
    }
}

enum class StaticFieldStorageKind : int {
    Regular = 0,
    NotStatic,
    Literal,
    ThreadStatic,
};

// Classify whether a field has addressable per-domain/per-class static storage
// (B-M4). Rejects non-static fields, ECMA-335 Literal (const) fields which have
// no static storage (offset 0 or -1 would alias slot 0 or underflow base-1),
// and [ThreadStatic] fields whose offset sentinel is -1 / (size_t)-1.
inline constexpr StaticFieldStorageKind ClassifyStaticFieldStorage(
    std::uint32_t flags, std::int32_t normalizedOffset) noexcept {
    if ((flags & kFieldAttrStatic) == 0u) return StaticFieldStorageKind::NotStatic;
    if ((flags & kFieldAttrLiteral) != 0u) return StaticFieldStorageKind::Literal;
    if (normalizedOffset < 0) return StaticFieldStorageKind::ThreadStatic;
    return StaticFieldStorageKind::Regular;
}

inline constexpr const char* StaticFieldStorageReason(StaticFieldStorageKind kind) noexcept {
    switch (kind) {
        case StaticFieldStorageKind::Regular:      return "";
        case StaticFieldStorageKind::NotStatic:    return "not_static";
        case StaticFieldStorageKind::Literal:      return "literal";
        case StaticFieldStorageKind::ThreadStatic: return "thread_static";
    }
    return "unknown";
}

}  // namespace Discrete::ManagedLayout
