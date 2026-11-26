// IL2CPPClassReferenceDiagnostic.h — Phase 28b-v3 investigation helper.
//
// Why this exists
// ---------------
// Phase 28b-v2's chain detector handles user-defined statics via the
// "class pointer in .data + 2-hop chain" pattern. It returns 0 matches
// for compiler-generated `<>c` lambda-cache classes on SaGa F2R, which
// strongly suggests these are routed through a different indirection.
//
// The original plan-doc hypothesis (`g_codegenModule->types[index]`)
// doesn't match the real `Il2CppCodeGenModule` layout — that struct has
// no `types[]` field (vendor/Il2CppDumper/Il2CppDumper/Il2Cpp/Il2CppClass.cs).
// Before redesigning the chain detector we need to know empirically
// where these class pointers actually live.
//
// What this helper does
// ---------------------
// Given an `Il2CppClass*`, the diagnostic reports:
//   * The class pointer value + the static-fields pointer (if any)
//   * Every storage slot in GameAssembly.dll's non-executable sections
//     that holds either pointer
//   * Every storage slot in committed RW heap regions that holds either
//     pointer (capped per-region to bound scan cost)
//   * 128 bytes of surrounding context for each slot — enough to see
//     whether the slot is standalone or part of a pointer table
//
// This is a diagnostic-only RPC — no production code paths consume it.
// Once the v3 design is confirmed, the diagnostic stays around as a
// debugging aid for future codegen-pattern investigations.
#pragma once

#include "IL2CPPTypes.h"

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace Discrete::IL2CPP {

struct DiagnosticSection {
    std::string    name;
    std::uintptr_t va        = 0;
    std::size_t    size      = 0;
    bool           isExecutable = false;
    bool           isWritable   = false;
};

struct DiagnosticSlot {
    /// Absolute VA of the matched 8-byte slot.
    std::uintptr_t address       = 0;
    /// For module slots: section name. Empty for heap slots.
    std::string    sectionName;
    /// For heap slots: enclosing region info.
    std::uintptr_t regionBase    = 0;
    std::size_t    regionSize    = 0;
    /// Win32 PAGE_* protection mask. Filled for heap slots.
    unsigned long  regionProtect = 0;
    /// Win32 MEM_IMAGE / MEM_MAPPED / MEM_PRIVATE.
    unsigned long  regionType    = 0;
    /// 128 bytes of context: 64 before the slot + 64 starting at the slot.
    /// Captured as raw bytes for hex formatting on the wire side.
    std::vector<std::uint8_t> context;
    /// Index of the matched qword within `context` (always 64 in v1; kept
    /// as a field so future variations can report off-center matches).
    std::size_t    contextSlotOffset = 64;
};

/// One candidate AOT method pointer extracted from a heap slot's
/// surrounding qwords. Surfaces when a heap slot looks like an
/// IL2CPP `MethodInfo` (declaringType==target class, methodPointer
/// lives in the IL2CPP executable section). The user can navigate to
/// `methodPointer` in CE Memory View to capture the AOT disassembly
/// that references the target class — that's the disasm Phase 28b-v3
/// design needs.
struct NavigationCandidate {
    std::uintptr_t methodPointer = 0;  // VA inside GameAssembly.dll's exec section
    std::uintptr_t slotAddress   = 0;  // the heap slot that surfaced this candidate
};

struct DiagnosticResult {
    bool        ok = false;
    bool        cancelled = false;
    std::string error;

    /// Resolved class pointer + static-fields pointer.
    std::uintptr_t classPtr        = 0;
    std::uintptr_t staticFieldsPtr = 0;
    std::size_t    staticFieldsOffsetInClass = 0;  // for hop-2 reference

    /// When the caller supplied a `fieldName` arg, we resolve its
    /// `FieldInfo*` via iteration + name match. Even when the class
    /// hasn't been runtime-initialised (`staticFieldsPtr == 0`), the
    /// FieldInfo* IS always allocated at class-load time — so this
    /// gives v3 something to inverse-scan for in the AOT code even
    /// when the static-fields block doesn't exist yet.
    std::string    targetFieldName;        // echo of input arg
    std::uintptr_t targetFieldInfoPtr = 0;  // 0 = not requested OR not found
    std::size_t    targetFieldOffset  = 0;  // FieldInfo.offset

    /// GameAssembly.dll module bounds + section table.
    std::uintptr_t            moduleBase = 0;
    std::size_t               moduleSize = 0;
    std::vector<DiagnosticSection> sections;

    /// 256-byte dump of the class struct itself (helps see neighboring
    /// fields without a separate readMemory round-trip).
    std::vector<std::uint8_t> classStructDump;

    /// Slots in GameAssembly.dll non-executable sections holding the class ptr.
    std::vector<DiagnosticSlot> moduleClassPtrSlots;
    /// Same for static-fields ptr.
    std::vector<DiagnosticSlot> moduleStaticFieldsSlots;

    /// Slots in committed RW heap regions (MEM_PRIVATE / MEM_MAPPED, NOT
    /// MEM_IMAGE for GameAssembly.dll — that's reported above). Useful
    /// for finding runtime metadata-usage tables that hold the pointer
    /// after class init.
    std::vector<DiagnosticSlot> heapClassPtrSlots;
    std::vector<DiagnosticSlot> heapStaticFieldsSlots;

    /// Slots holding the resolved `FieldInfo*` — both in module sections
    /// and on the heap. Only populated when caller passed `fieldName`
    /// AND the field resolved. This is the v3 critical diagnostic:
    /// presence of `.data` slots here would confirm AOT code uses
    /// `FieldInfo*` indirection (and v3 should scan for those slots,
    /// not for `Il2CppClass*`).
    std::vector<DiagnosticSlot> moduleFieldInfoSlots;
    std::vector<DiagnosticSlot> heapFieldInfoSlots;

    /// AOT method pointers extracted from heap class-ptr slots that look
    /// like `MethodInfo` entries. These are the addresses to navigate to
    /// in CE Memory View to capture the disasm that references the target
    /// class. Deduped + sorted.
    std::vector<NavigationCandidate> navigationCandidates;

    /// Scan telemetry.
    std::size_t moduleBytesScanned = 0;
    std::size_t heapRegionsScanned = 0;
    std::size_t heapBytesScanned   = 0;
    std::size_t heapRegionsSkipped = 0;  // skipped due to per-region size cap or MEM_IMAGE filter
    /// Set true if either heap-scan list hit its per-pointer cap.
    bool        heapCapHit         = false;
};

/// Runs the diagnostic against the given class.
///
/// `heapScan` toggles the runtime-heap pass — when false only the module
/// sections are scanned (cheap, deterministic). True is the default for
/// the investigation; it adds maybe 0.5-2 s of scan time on a big process.
///
/// `heapSlotLimitPerPointer` caps how many heap slots are reported per
/// pointer (default 64). Stops the diagnostic from running away on a
/// process with the pointer stored in thousands of GC roots.
///
/// `heapMaxRegionSize` skips heap regions larger than N bytes (default
/// 256 MB). Defends against the giant 4 GB+ reserved-but-mostly-unused
/// virtual ranges Unity allocates for jit/native heaps.
DiagnosticResult DiagnoseClassReferences(Il2CppClass*       klass,
                                          const std::string& fieldName /* may be empty */,
                                          bool               heapScan                = true,
                                          std::size_t        heapSlotLimitPerPointer = 64,
                                          std::size_t        heapMaxRegionSize       = 256u * 1024u * 1024u);

}  // namespace Discrete::IL2CPP
