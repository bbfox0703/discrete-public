// RegistrationFinder.h — Phase D step 3. Offline counterpart to the
// runtime `IL2CPPMetadataRegistration::Find()` scanner.
//
// Operates on PE file bytes + the parsed Image (from PEParser.h) —
// NO process injection, NO LoadLibraryEx. The runtime scanner reads
// in-process memory (everything mapped + relocated by the OS loader);
// this offline variant reads file bytes and follows pointer fields
// via VA → RVA → file-offset translation.
//
// What it finds: the `Il2CppMetadataRegistration` struct's location
// (as an RVA) inside `GameAssembly.dll`, plus the `types[]` array
// RVA and count. Together these unlock the Phase D-4 method-pointer
// resolution (cross-link metadata MethodDefinitions to native
// addresses) without launching the game.
//
// Limitations vs the runtime scanner (deliberate, for now):
//   * Validates the candidate's structural shape + types[] sampling;
//     doesn't deref every type pointer (the runtime needs that for
//     Il2CppType tag decode, which we can do later).
//   * Defaults assume x64 (the only IL2CPP Windows shape in our
//     corpus); x86 IL2CPP would need a parallel struct with 4-byte
//     pointers — TBD when we hit a 32-bit IL2CPP target.
//
// What actually works (verified build #500): v24.0 / v29 / v31 / v39
// all parse cleanly with one shared 0x80-byte struct shape. The
// metadata.dat format version (v24 / v29 / v31 / v38 section-headers
// / v39 variable-width indices) changes the on-disk metadata layout
// but NOT the runtime registration struct — the runtime side stayed
// stable across the whole span, so this offline scanner works
// version-agnostically (same finding as Phase 5 segment 2d for the
// in-process scanner: structural shape is version-portable).

#pragma once

#include "PEParser.h"

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>

namespace Discrete::Phase_D::Registration {

struct Found {
    std::uint64_t   regRva           = 0;   // RVA of Il2CppMetadataRegistration in the PE
    std::uint64_t   typesArrayRva    = 0;   // RVA of the types[] array (regRva field at +0x38)
    std::uint32_t   typesCount       = 0;   // length of types[] (regRva field at +0x30)
    std::uint64_t   typesArrayVa     = 0;   // absolute VA = imageBase + typesArrayRva
    int             nearMissCount    = 0;   // candidates that passed the cheap rejector but failed full validation

    // Field-offset plumbing (struct slots +0x50 / +0x58). `fieldOffsets` is
    // `const int32_t**` — one entry per TypeDefinition, each pointing at that
    // type's offsets. Read verbatim; 0 when the slot looks unusable. See
    // FieldOffsets.h for how the two storage modes are told apart.
    std::uint32_t   fieldOffsetsCount = 0;
    std::uint64_t   fieldOffsetsVa    = 0;

    // Il2CppTypeDefinitionSizes** (slots +0x60 / +0x68) — instance_size per
    // TypeDefinition. Doubles as a validation oracle: an instance field's
    // offset must land inside its type's instance size.
    std::uint32_t   typeDefSizesCount = 0;
    std::uint64_t   typeDefSizesVa    = 0;

    // For diagnostics — the section that contained the hit.
    std::string     sectionName;
};

// Scan the PE for the Il2CppMetadataRegistration struct.
// `bytes` / `size` are the full PE file contents; `img` is the
// parsed Image (must match `bytes`).
//
// Returns std::nullopt on miss. `errOut` (optional) receives a
// one-line note about why the scan failed (e.g. "no init-data
// sections", "no candidate matched").
std::optional<Found> FindOffline(const std::uint8_t* bytes,
                                  std::size_t          size,
                                  const PE::Image&     img,
                                  std::string*         errOut = nullptr);

}  // namespace Discrete::Phase_D::Registration
