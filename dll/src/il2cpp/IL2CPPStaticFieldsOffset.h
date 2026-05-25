// IL2CPPStaticFieldsOffset.h — Phase 28b-v2 helper.
//
// Runtime-version-independent discovery of `offsetof(Il2CppClass,
// static_fields)`. IL2CPP stores per-class static storage as a `void*`
// member somewhere in the `Il2CppClass` struct, but the offset of that
// member differs across IL2CPP versions (v24 / v27 / v29 / v31 /
// v35-v39 each shift it around as fields are added / reordered).
//
// Rather than hardcode the offset per version (which would need
// recompiles whenever Unity ships a new IL2CPP layout), this helper
// auto-discovers it at runtime:
//
//   1. Walk every assembly's class list
//   2. Find the first class where `il2cpp_class_get_static_data()`
//      returns a non-null pointer (= class has at least one static
//      field AND is already runtime-initialised)
//   3. Linear-scan the first 1024 bytes of that class's struct,
//      8-byte aligned, looking for the returned pointer value as a
//      stored qword
//   4. The byte offset of the match IS `offsetof(static_fields)`
//
// The chain-detection in Phase 28b-v2 needs this offset to recognise
// the second hop of the IL2CPP static-field access pattern:
//
//     mov rax, [rip + CLASS_PTR_DISP32]            ; hop 1: load Il2CppClass*
//     mov rax, [rax + STATIC_FIELDS_OFFSET]        ; hop 2: deref to static block
//     mov reg, [rax + FIELD_OFFSET]                ; hop 3: read field
//
// without the offset constant, hop 2 cannot be distinguished from any
// other class-pointer dereference (e.g. parent-class access, vtable
// indirection, interface dispatch).
//
// Cached via `std::call_once` — first caller pays the discovery cost
// (a few ms on a typical game), every subsequent call is a memory
// load. Thread-safe.
#pragma once

#include <cstddef>

namespace UnityCEDumper::IL2CPP {

/// Returns the byte offset of `static_fields` within `Il2CppClass`.
/// Returns 0 when discovery fails (resolver unbound, no class with
/// non-null static storage in the first 1024 probed classes, or
/// pointer not found in scan window). Caller should treat 0 as
/// "Phase 28b-v2 chain detection unavailable on this target" and
/// fall back to the v1 advisory banner.
std::size_t GetStaticFieldsOffsetInClass();

}  // namespace UnityCEDumper::IL2CPP
