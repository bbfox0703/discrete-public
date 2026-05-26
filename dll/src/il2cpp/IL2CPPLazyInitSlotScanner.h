// IL2CPPLazyInitSlotScanner.h — Phase 28b-v3 lazy-init slot discovery.
//
// Why this exists
// ---------------
// Phase 28b-v2's `ClassPointerScanner` walks `.data`/`.rdata` for slots
// that currently hold the target `Il2CppClass*`. That works fine for
// statics whose declaring class has been initialised — singleton-style
// classes like `SteamManager` have their per-method cache slots filled
// in at game startup, so the scanner finds them.
//
// It fails for compiler-generated `<>c` lambda-cache classes when the
// lambda body has never executed in the current session: IL2CPP emits a
// **per-method lazy-init cache slot** for each class the method needs,
// and those slots stay NULL until the first time the method runs and
// `il2cpp_codegen_initialize_method` populates them.
//
// Live-decoded codegen pattern (SaGa F2R `<>c.<>9__0_X` accessor):
//
//     lea  rcx, [rip + SLOT_ADDR]               ; pass &SLOT to init
//     call il2cpp_codegen_initialize_method     ; fills SLOT with class ptr
//     ...                                       ; lock or [rsp], 0   (mem barrier)
//     mov  byte [rip + INIT_FLAG], 1            ; mark init done
//     mov  rcx, [rip + SLOT_ADDR]               ; read slot (now Il2CppClass*)
//     mov  rdx, [rcx + 0xB8]                    ; static_fields (Phase 28b-v2 hop 2)
//     mov  [rdx + FIELD_OFFSET], rax            ; field access (Phase 28b-v2 hop 3)
//
// What this scanner does
// ----------------------
// One-time `.text` walk that collects every `lea reg, [rip+disp32]`
// instruction immediately followed by a `call rel32` whose target lies
// inside GameAssembly.dll's executable region. Each match's `lea`
// target is recorded as a candidate lazy-init slot — the AOT code
// expects that slot to hold an `Il2CppClass*` after the init call
// returns.
//
// Caller (Phase 28b-v3 in `IL2CPPFieldXref.cpp`) iterates the slot list,
// reads each slot's current value with `SafeRead`, and if any slot holds
// the target class pointer, adds it to the v2 chain detector's
// `ClassPointerSlot` input. That extends v2's coverage to lazy-init
// slots that HAVE been populated, even if no `.data` direct cache slot
// holds the same value.
//
// v3.0 limitation: slots whose values are still NULL (lambda has never
// run in the current session) can't be class-attributed via runtime
// value comparison. v3.1 plans to parse the `MetadataInitializerEntry`
// struct the `lea` target points to and resolve the class via type
// index — that closes the never-executed-lambda case.
#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

namespace UnityCEDumper::IL2CPP {

struct LazyInitSlot {
    /// Absolute VA of the `.data` / `.bss` slot the lea instruction pointed to.
    /// After `il2cpp_codegen_initialize_method` runs, this slot holds an
    /// `Il2CppClass*` (well — typically; v3.1 will refine for non-class
    /// metadata-initialiser slots).
    std::uintptr_t slotAddress = 0;

    /// IP of the matched `lea` instruction. Useful for cross-reference
    /// debugging.
    std::uintptr_t leaIP       = 0;

    /// IP of the matched `call` instruction (always 5 bytes after the
    /// lea's start: 7-byte lea + 0 padding).
    std::uintptr_t callIP      = 0;

    /// Resolved absolute target of the call. Typically one of a small
    /// set of IL2CPP runtime init functions (e.g.
    /// `il2cpp_codegen_initialize_method`, `il2cpp_codegen_initialize_runtime_metadata`).
    /// We don't filter by which function — the lea + immediate-call
    /// sequence with the right addressing modes is signature enough.
    std::uintptr_t callTarget  = 0;
};

/// Walks GameAssembly.dll's executable sections, recognises every
/// `lea reg, [rip+disp32]; call rel32` 2-instruction sequence where:
///   * the lea target falls inside a `.data` / `.bss`-class section of
///     GameAssembly.dll, and
///   * the call target falls inside an executable section of the same
///     module.
/// Returns a deduplicated list of LazyInitSlots sorted by slotAddress.
///
/// Internally cached via `std::call_once` — first call pays the
/// ~50-200 ms scan cost on a typical IL2CPP module, every subsequent
/// call is a `const&` return.
const std::vector<LazyInitSlot>& EnumerateLazyInitSlots();

}  // namespace UnityCEDumper::IL2CPP
