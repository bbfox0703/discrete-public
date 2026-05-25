// IL2CPPChainDetector.h — Phase 28b-v2 chain detector.
//
// Recognises the 2-hop class-pointer indirection IL2CPP AOT code uses
// when reading or writing a static field:
//
//     mov R1, [rip + CLASS_PTR_DISP32]      ; hop 1: load Il2CppClass*
//     mov R2, [R1 + STATIC_FIELDS_OFFSET]   ; hop 2: deref to static_fields
//     mov R3, [R2 + FIELD_OFFSET]           ; hop 3 READ
//   OR
//     mov [R2 + FIELD_OFFSET], R3           ; hop 3 WRITE
//   OR
//     lea R3, [R2 + FIELD_OFFSET]           ; hop 3 — take field address
//                                           ; (treated as READ; common
//                                           ; pattern for value-type
//                                           ; field access)
//
// Why this exists
// ---------------
// v1's RIP scanner returned 0 matches for ~every IL2CPP static field
// because `static_fields` is heap-allocated by the runtime (outside
// GameAssembly.dll) — no machine instruction can RIP-relative-address
// a runtime address. v2 walks the 2-hop chain the compiler actually
// emits, leveraging the fact that the `Il2CppClass*` IS in `.data` /
// `.rdata` and IS RIP-addressable.
//
// Pattern reference
// -----------------
// AOBMaker's `RipReferenceScanner` (single-hop) provides the
// displacement-first candidate-scan pattern and chunk-overlap rules;
// the 2-hop chain logic here is greenfield. See
// `docs/lessons-learned.md` §"Phase 28b IL2CPP — `static_fields` is
// runtime-heap-allocated" for the root-cause that motivated this
// redesign.
//
// Register tracking caveats
// -------------------------
// * Tracks ONE live class-pointer register and ONE live static-fields
//   register at any time. Pathological codegen that copies the class
//   pointer to two registers and dereferences both paths in parallel
//   will only match one path (the one we tracked). Unusual in practice
//   — IL2CPP's translated C++ keeps the chain tight.
// * Sub-register aliasing (rax / eax / ax / al all share storage) is
//   handled via `ZydisRegisterGetLargestEnclosing` — we compare
//   registers in their 64-bit canonical form.
// * The forward trace caps at 8 instructions per candidate. Compilers
//   sometimes insert a null-check (`test rax, rax` / `jz null_path`)
//   between hops — 8 is enough slack for that without admitting false
//   chains across function calls (a CALL clobbers volatile registers
//   and breaks the chain).
#pragma once

#include "IL2CPPClassPointerScanner.h"

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace UnityCEDumper::IL2CPP {

struct ChainMatch {
    /// IP of the chain's hop-3 instruction (the actual field read or write).
    std::uintptr_t hitIP = 0;
    /// IP of the chain's hop-1 instruction (the initial RIP-relative
    /// class-pointer load). Useful for cross-referencing class-init
    /// sequences.
    std::uintptr_t hopOneIP = 0;
    /// "read" or "write".
    std::string    refKind;
};

struct ChainScanStats {
    /// Total candidate hits — instructions matching the hop-1 pattern
    /// `mov reg, [rip+disp]` whose `disp` resolved into one of the
    /// supplied class-pointer slots.
    std::size_t hopOneCandidates = 0;
    /// Candidates that DID NOT find a hop-2 deref within the forward
    /// trace window. Means the class pointer was loaded for some
    /// reason other than static-field access (type comparison, vtable
    /// lookup, etc.).
    std::size_t chainBroken = 0;
    /// Successful chain matches (= `result.size()`).
    std::size_t chainsEmitted = 0;
    /// Bytes scanned across all executable sections.
    std::size_t bytesScanned = 0;
    /// SafeReadBytes failures (AC-stripped pages).
    std::size_t failedReads = 0;
};

/// Scan GameAssembly.dll's executable sections for IL2CPP static-field
/// access chains targeting any of `classPointerSlots` followed by the
/// 2-hop deref through `staticFieldsOffset` + `targetFieldOffset`.
///
/// Returns 0-N matches. Caps internally at `matchLimit`. The `stats`
/// out-param (optional) reports diagnostic counters for the log line.
std::vector<ChainMatch> DetectFieldAccessChains(
    const std::vector<ClassPointerSlot>& classPointerSlots,
    std::size_t                          staticFieldsOffset,
    std::size_t                          targetFieldOffset,
    std::size_t                          matchLimit,
    ChainScanStats*                      stats);

}  // namespace UnityCEDumper::IL2CPP
