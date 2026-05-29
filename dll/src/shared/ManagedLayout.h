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

}  // namespace Discrete::ManagedLayout
