// SymbolPipeline.h — Phase D-5.2 + D-5.3. Closes Phase D.
//
// Pulls together every prior D-step into one offline pipeline:
//
//   GameAssembly.dll → PEParser (D-1)
//                    → CodeGenModulesFinder (D-4) — per-image method ptrs
//
//   global-metadata.dat → MetadataParser (Phase B + D-5.1)
//                       → Image table + Method table + Type table
//
//   For each Image matched to a CodeGenModule (Image.name == module.name):
//     1. Walk each TypeDefinition in [image.typeStart, image.typeStart + image.typeCount)
//     2. Walk each MethodDefinition of the type
//     3. localIdx = (method.token & 0x00FFFFFF) - 1  (ECMA-335 MethodDef RID - 1,
//        matching IL2CPP runtime GetTokenRowId(token) - 1; D-M6)
//     4. addressVa = module.methodPointers[localIdx] (read from file bytes)
//     5. addressRva = addressVa - imageBase (skip if == 0 i.e. abstract /
//        generic-only / inlined method)
//
//   Resolved rows → 3 emit formats (port of ui/UnityDumpUI.Core/Services/
//   Export/SymbolExporter.cs):
//     * .dd64 (x64dbg) — JSON with labels[] + comments[]
//     * Ghidra symbols.txt — "LABEL ADDR TYPE" lines
//     * IDA .idc — MakeName + MakeComm script
//
// Limitations matching the upstream D-1..D-4 surface:
//   * x64 only.
//   * v24.2+ only (pre-v24.2 IL2CPP has no codeGenModules — deferred).
//   * Token-based overload disambiguation in labels (the C# live-path
//     uses a parameter-type-derived humanSuffix; that would need
//     Il2CppType tag resolution offline, deferred). The token suffix
//     guarantees uniqueness across overloads; just less human-readable.

#pragma once

#include "../../dll/src/il2cpp/MetadataParser.h"
#include "../../dll/src/shared/MethodTokenIndex.h"
#include "CodeGenModulesFinder.h"
#include "PEParser.h"

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace Discrete::Phase_D::Symbols {

enum class Format { Dd64, Ghidra, Ida };

struct ResolvedClass {
    std::string   assembly;       // "Assembly-CSharp" (no ".dll")
    std::string   ns;
    std::string   name;
    std::uint64_t classRva = 0;   // RVA of the Il2CppClass* pointer slot (best-effort)
};

struct ResolvedMethod {
    std::string   assembly;       // "Assembly-CSharp"
    std::string   ns;
    std::string   className;
    std::string   methodName;
    std::uint32_t token   = 0;
    bool          isStatic = false;
    std::uint64_t addressRva = 0;  // RVA of the AOT entry in GameAssembly.dll
};

struct PipelineResult {
    // Stats — for the CLI to surface to the user.
    std::size_t imagesTotal          = 0;
    std::size_t imagesMatched        = 0;
    std::size_t methodsTotal         = 0;
    std::size_t methodsResolved      = 0;   // had a non-null methodPointers[] entry
    std::size_t methodsNullPtr       = 0;   // null entry (abstract / generic / inlined)
    std::size_t methodsIndexMismatch = 0;   // D-M6: token RID - 1 != (m.index - image.methodStart)
    std::size_t classesTotal         = 0;

    std::vector<ResolvedClass>  classes;
    std::vector<ResolvedMethod> methods;
};

// D-M6: Re-exported from dll/src/shared/MethodTokenIndex.h for callers in
// Discrete::Phase_D::Symbols (and shared with discrete_dll_test).
using Discrete::Shared::ResolveMethodPointerIndex;

// End-to-end resolve. `metadataPath` and `gaPath` are wide because
// the CLI gets them as utf-8 argv → wide for CreateFileW. `errOut`
// (optional) is populated on any preflight failure (file missing,
// PE parse, metadata parse, registration scan, codeGenModules scan).
std::optional<PipelineResult> ResolveSymbols(const std::wstring& metadataPath,
                                              const std::wstring& gaPath,
                                              std::string*        errOut = nullptr);

// Renderers — pure functions over a PipelineResult. Same label naming
// convention as ui/UnityDumpUI.Core/Services/Export/SymbolExporter.cs:
//   class  rows → "Class_<NS>_<Class>"
//   method rows → "<NS>_<Class>__<Method>_t<token>"
// All non-alnum/underscore chars collapse to '_'. moduleName for .dd64
// is "GameAssembly.dll".
std::string RenderDd64    (const PipelineResult& r, const std::string& moduleName,
                            std::uint64_t moduleBase);
std::string RenderGhidra  (const PipelineResult& r, std::uint64_t moduleBase);
std::string RenderIda     (const PipelineResult& r, std::uint64_t moduleBase);

}  // namespace Discrete::Phase_D::Symbols
