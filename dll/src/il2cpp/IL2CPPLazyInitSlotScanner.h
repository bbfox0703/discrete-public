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

#include "IL2CPPTypes.h"  // forward decl of Il2CppClass / Il2CppType

#include <cstddef>
#include <cstdint>
#include <vector>

namespace Discrete::IL2CPP {

struct LazyInitSlot {
    /// Absolute VA of the `.data` / `.bss` slot the lea instruction pointed to.
    /// After `il2cpp_codegen_initialize_runtime_metadata` runs, this slot
    /// holds either an `Il2CppClass*` (kind=1), `Il2CppType*` (kind=2),
    /// `MethodInfo*` (kind=3/6), `FieldInfo*` (kind=4), or `Il2CppString*`
    /// (kind=5). Before init, the slot holds the encoded token (see
    /// `tokenAtScan` below).
    std::uintptr_t slotAddress = 0;

    /// IP of the matched `lea` instruction. Useful for cross-reference
    /// debugging.
    std::uintptr_t leaIP       = 0;

    /// IP of the matched `call` instruction (always 5 bytes after the
    /// lea's start: 7-byte lea + 0 padding).
    std::uintptr_t callIP      = 0;

    /// Resolved absolute target of the call. The IL2CPP runtime init
    /// function (typically `il2cpp_codegen_initialize_runtime_metadata`).
    /// All slots in a given session share the same callTarget; the
    /// scanner doesn't filter by it (the signature is sufficient).
    std::uintptr_t callTarget  = 0;

    /// Phase 28b-v3.1 — slot's value as observed at scan time.
    /// When the high 32 bits are zero AND the low 32 bits look like a
    /// metadata-usage token (high 3 bits = kind in 1..6), the slot was
    /// uninitialised at scan time and we captured the original token.
    /// Otherwise tokenAtScan == 0 and the slot was either already
    /// initialised (holds a heap pointer) or held a non-token value.
    ///
    /// Token encoding (standard IL2CPP `Il2CppMetadataUsage`):
    ///   bits 29-31: kind   (1=TypeInfo / 2=Il2CppType / 3=MethodDef /
    ///                       4=FieldInfo / 5=StringLiteral / 6=MethodRef)
    ///   bits 0-28:  index  (into the corresponding metadata table)
    ///
    /// For v3.1's class-pointer xref purpose, only kind=1 (TypeInfo)
    /// slots matter — those resolve to an `Il2CppClass*` after init.
    /// Other kinds are kept in the list for diagnostic visibility but
    /// the `FieldXref` consumer ignores them.
    std::uint32_t tokenAtScan = 0;
    /// Decoded kind from `tokenAtScan` (0 if no token was captured).
    std::uint8_t  tokenKind   = 0;
    /// Decoded index from `tokenAtScan` (0 if no token was captured).
    /// Note: encoding may be 1-based or 0-based depending on IL2CPP
    /// version — `ResolveTypeInfoSlotClass` tries both forms.
    std::uint32_t tokenIndex  = 0;
};

/// Kind enum mirroring IL2CPP's `Il2CppMetadataUsage`. Kept here as
/// constants since `IL2CPPTypes.h` is intentionally minimal.
enum class MetadataUsageKind : std::uint8_t {
    Invalid       = 0,
    TypeInfo      = 1,  // -> Il2CppClass*
    Il2CppType    = 2,  // -> Il2CppType*
    MethodDef     = 3,  // -> MethodInfo*
    FieldInfo     = 4,  // -> FieldInfo*
    StringLiteral = 5,  // -> Il2CppString*
    MethodRef     = 6,  // -> MethodInfo*
};

/// Walks GameAssembly.dll's `il2cpp` exec section for the canonical
/// IL2CPP class-init signature (`lea reg,[rip+SLOT]; call rel32;
/// lock or [rsp],0; mov byte [rip+FLAG],1`). For each match, captures
/// the slot address + 4 IPs + the slot's current value at scan time
/// (decoded into a `tokenAtScan` if it's a metadata-usage token, so
/// Phase 28b-v3.1 can statically map slot → class even when the
/// slot hasn't been runtime-populated yet).
///
/// Returns a deduplicated list of LazyInitSlots sorted by slotAddress.
///
/// Internally cached on first successful scan (B-L1: throttled retry on
/// early failure) — subsequent calls are a lock-free `const&` return.
const std::vector<LazyInitSlot>& EnumerateLazyInitSlots();

}  // namespace Discrete::IL2CPP
