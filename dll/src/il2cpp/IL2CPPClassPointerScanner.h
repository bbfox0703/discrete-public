// IL2CPPClassPointerScanner.h — Phase 28b-v2 helper.
//
// Inverse pointer scan: given an `Il2CppClass*` target, walk every data
// section of `GameAssembly.dll` looking for 8-byte slots whose stored
// value equals that pointer. Each hit is a "class pointer slot" — a
// `.data` / `.rdata` location holding a cached reference to the class.
//
// Why this exists
// ---------------
// Phase 28b v1's RIP scanner couldn't find IL2CPP static-field references
// because static_fields is heap-allocated by the runtime, outside the
// `GameAssembly.dll` module image — no machine instruction can RIP-
// relative-address a runtime address. v2 fixes this by chasing the
// 2-hop indirection IL2CPP actually emits:
//
//     mov rax, [rip + CLASS_PTR_DISP32]      ; hop 1: load Il2CppClass* (from .data)
//     mov rax, [rax + STATIC_FIELDS_OFFSET]  ; hop 2: deref to static_fields
//     mov reg, [rax + FIELD_OFFSET]          ; hop 3: read field
//
// The `Il2CppClass*` IS RIP-addressable because it lives in `.data` /
// `.rdata`. This scanner finds every `.data` slot holding the target
// pointer; the next phase (Zydis forward-trace) scans `.text` for
// RIP-relative loads of those slots and verifies the trailing
// dereference chain.
//
// Pattern reference
// -----------------
// Modelled on AOBMaker's `Il2CppPeReader.FindPointerReferences`
// (D:\Github\AOBMaker\src\AOBMaker.Core\Services\Il2CppPeReader.cs:328-341).
// Their version operates offline on PE-on-disk; ours runs against the
// live mapped module so we pick up runtime-patched cells too.
//
// A class pointer can sit in MULTIPLE `.data` slots — IL2CPP emits a
// per-translation-unit class cache, so a class touched from N
// compilation units lands in N storage slots. The chain detector
// scans for refs to ALL of them.
#pragma once

#include "IL2CPPTypes.h"

#include <cstdint>
#include <vector>

namespace UnityCEDumper::IL2CPP {

struct ClassPointerSlot {
    /// Absolute VA of the 8-byte slot holding the class pointer.
    std::uintptr_t address;
    /// Absolute VA of the containing section's start (for debug).
    std::uintptr_t sectionVa;
};

/// Walks GameAssembly.dll's non-executable data sections looking for
/// every 8-byte aligned qword whose value equals `(uintptr_t)targetClass`.
/// Returns 0-N slots — empty result usually means GameAssembly.dll isn't
/// loaded or the class isn't referenced from anywhere in compiled code
/// (rare; even pure-data classes are referenced by metadata tables).
std::vector<ClassPointerSlot> EnumerateClassPointerSlots(
    Il2CppClass* targetClass);

}  // namespace UnityCEDumper::IL2CPP
