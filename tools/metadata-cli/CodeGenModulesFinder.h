// CodeGenModulesFinder.h — Phase D step 4. Offline counterpart to the
// runtime IL2CPP method-pointer lookup path (`Il2CppCodeRegistration::
// codeGenModules`).
//
// Why we scan for codeGenModules directly (instead of locating
// Il2CppCodeRegistration first): the CodeRegistration struct has
// significant per-version layout drift (different fields appearing /
// disappearing across v22 / v24.2 / v24.5 / v27.1 / v29.1 — see
// vendor/Il2CppDumper/Il2Cpp/Il2CppClass.cs:5-66 for the [Version]
// attributes). Trying to validate that whole shape requires a
// per-version validator.
//
// `codeGenModules`, by contrast, is a uniform structure across every
// version that has it (Min=24.2 onwards):
//
//   codeGenModules: ptr → array of `Il2CppCodeGenModule*`
//     each `Il2CppCodeGenModule`:
//       0x00  ptr   moduleName              (always — first field)
//       0x08  i64   methodPointerCount
//       0x10  ptr   methodPointers          (array of native func ptrs)
//       0x18+ ... version-variable tail (adjustorThunks etc.)
//
// The `moduleName` is a string ending in ".dll" — strong signal for
// validation. We scan the data sections for arrays of 8-byte aligned
// pointers where every entry dereferences to a struct whose first
// field points to a ".dll" string. Once located, we expose each
// module's name + methodPointers RVA + count.
//
// Limitations (this v0.1):
//   * Pre-v24.2 (R-Type Dimensions! v24.0) has NO codeGenModules —
//     it uses the flat `methodPointers` on CodeRegistration directly.
//     That code path is TBD; the finder returns std::nullopt for
//     v24.0 / v24.1 targets.
//   * x64 only (8-byte pointers). x86 IL2CPP support TBD.

#pragma once

#include "PEParser.h"

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace Discrete::Phase_D::CodeGenModules {

struct Module {
    std::string   name;                    // "Assembly-CSharp.dll" etc.
    std::uint64_t modulePtrVa     = 0;     // VA of the Il2CppCodeGenModule struct
    std::uint64_t modulePtrRva    = 0;     // RVA of the same
    std::uint64_t methodPointersVa  = 0;   // VA of methodPointers[]
    std::uint64_t methodPointersRva = 0;
    std::uint64_t methodCount       = 0;
};

struct Found {
    std::uint64_t        arrayRva      = 0;    // RVA of the codeGenModules pointer-array itself
    std::uint64_t        arrayVa       = 0;
    std::size_t          moduleCount   = 0;
    std::vector<Module>  modules;
    std::string          sectionName;          // section the array lived in
};

// Scan the PE for the `codeGenModules` array. Returns the modules
// list (each with its native methodPointers RVA and count). `errOut`
// (optional) receives a one-line note on failure.
//
// `minModules` / `maxModules` (default 2 / 1024) bound the contiguous-
// pointer run search — real games have at least mscorlib + Assembly-
// CSharp (≥2 modules), and even Songs of Silence's 250-assembly game
// stays well under 1024.
std::optional<Found> FindOffline(const std::uint8_t* bytes,
                                  std::size_t          size,
                                  const PE::Image&     img,
                                  std::size_t          minModules = 2,
                                  std::size_t          maxModules = 1024,
                                  std::string*         errOut    = nullptr);

}  // namespace Discrete::Phase_D::CodeGenModules
