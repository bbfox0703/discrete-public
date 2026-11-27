// IL2CPPFieldXref.h — Phase 28b findFieldReferences backend (IL2CPP).
//
// v1: STATIC FIELDS ONLY. Resolves the field's static-storage address via
// `il2cpp_class_get_static_data(klass) + field.offset`, then scans the
// executable sections of `GameAssembly.dll` for `mov reg, [rip+disp32]`
// and `lea reg, [rip+disp32]` instructions whose absolute target equals
// that storage address. Each hit is attributed to its enclosing method
// via binary search against a sorted method-pointer index built from
// every class in the searched assemblies' image.
//
// Instance fields use a separate scanner — FindInstanceFieldReferences —
// which decodes the DECLARING CLASS's own methods looking for
// `[gpReg + fieldOffset]` accesses (base != rip/rsp/rbp). These are emitted
// with confidence="heuristic" because the displacement alone (no taint of
// `this`) can collide with unrelated objects touched in the same method.
//
// Confidence: static path = "exact" (RIP-relative encoding is unambiguous,
// disp32 + IP arithmetic recovers a single absolute target); instance path =
// "heuristic" (displacement match within declaring-class scope, no taint).
#pragma once

#include "IL2CPPTypes.h"

#include <cstdint>
#include <string>
#include <vector>

namespace Discrete::IL2CPP {

enum class FieldRefKind : int {
    Any   = 0,   // read or write
    Read  = 1,   // mov reg, [rip+disp32] / lea reg, [rip+disp32]
    Write = 2,   // mov [rip+disp32], reg (0x89) / chain & instance write ops
};

struct FieldRefMatch {
    std::string   ns;
    std::string   className;
    std::string   methodName;
    std::uint32_t methodToken = 0;
    std::uint32_t methodFlags = 0;
    bool          isStatic    = false;
    bool          isMethodStatic = false;
    void*         methodAddress = nullptr;  // AOT entry of enclosing method
    void*         hitAddress    = nullptr;  // exact IP of the matching insn
    std::string   refKind;      // "read" | "write"
    std::string   confidence;   // "exact"
};

struct FindFieldRefsResult {
    bool          ok = false;
    bool          cancelled = false;
    std::string   error;
    void*         staticAddress = nullptr;  // resolved target absolute address
    std::vector<FieldRefMatch> matches;
    std::size_t   scannedMethods = 0;
    bool          truncated = false;

    // Build #555 follow-up. A user-facing advisory string set when the scan
    // completed cleanly but returned 0 matches AND the static-storage
    // address sits OUTSIDE the GameAssembly.dll module (heap-allocated by
    // IL2CPP runtime at class-init time, which covers ~every IL2CPP static
    // in practice). In that case there is NO RIP-relative immediate in the
    // entire .text section that can possibly reference the storage, so
    // returning "0 matches" is technically correct but misleading — the
    // user infers "field has no readers/writers" when really "this scanner
    // can't see them". The note documents the structural limitation and
    // points at the Mono backend as the working alternative. Empty
    // otherwise (any error path / nonzero matches / RIP-addressable
    // storage). See lessons-learned.md.
    std::string   note;
};

// Resolves `(image, ns, className, fieldName)`, walks the .text section of
// GameAssembly.dll for RIP-relative references to the field's static
// storage, and attributes each hit to the enclosing method via the
// method-pointer index built from the image's classes.
//
// Returns ok=false + an `error` message when:
//   - field not found / not static  (instance-field v2)
//   - GameAssembly.dll not loaded
//   - il2cpp_class_get_static_data unbound
FindFieldRefsResult FindStaticFieldReferences(const Il2CppImage* image,
                                              const std::string& ns,
                                              const std::string& className,
                                              const std::string& fieldName,
                                              FieldRefKind       kindFilter,
                                              std::size_t        limit);

// Phase 28b instance path — scans the declaring class's own AOT methods for
// `[gpReg + fieldOffset]` accesses (base != rip/rsp/rbp, no index), emitting
// each as a confidence="heuristic" match classified read/write by the memory
// operand's action. See the .cpp header comment for the v1 recall gaps
// (external-class access / cross-method inlining missed; rbp-spilled `this`
// still matched by displacement). Returns ok=false + `error` when the field
// isn't found, is static (route to FindStaticFieldReferences), or the runtime
// isn't resolved.
FindFieldRefsResult FindInstanceFieldReferences(const Il2CppImage* image,
                                                const std::string& ns,
                                                const std::string& className,
                                                const std::string& fieldName,
                                                FieldRefKind       kindFilter,
                                                std::size_t        limit);

}  // namespace Discrete::IL2CPP
