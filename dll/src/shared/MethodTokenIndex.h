// MethodTokenIndex.h — shared pure helper for IL2CPP CodeGenModule methodPointers[]
// indexing (D-M6). Kept header-only in dll/src/shared/ so both tools/metadata-cli
// (SymbolPipeline) and dll/tests/dll_core_test can test the exact same implementation
// on both x64 and x86 without pulling metadata-cli headers into the DLL test harness.

#pragma once

#include <cstdint>
#include <optional>

namespace Discrete::Shared {

// Resolve the 0-based index into Il2CppCodeGenModule::methodPointers[] using
// the ECMA-335 MethodDef token RID (`(token & 0x00FFFFFF) - 1`), matching the
// IL2CPP runtime's `GetTokenRowId(method.token) - 1`.
//
// Also compares against the legacy `methodGlobalIndex - imageMethodStart`
// formula and sets `outIndexMismatch = true` when they diverge so callers can
// count and report non-contiguous or reordered metadata method tables.
//
// Returns std::nullopt when the token RID is 0 or >= moduleMethodCount.
inline std::optional<std::uint32_t> ResolveMethodPointerIndex(
    std::uint32_t token,
    std::int32_t  methodGlobalIndex,
    std::int32_t  imageMethodStart,
    std::uint64_t moduleMethodCount,
    bool&         outIndexMismatch) noexcept {
    outIndexMismatch = false;
    const std::uint32_t rid = token & 0x00FFFFFFu;
    if (rid == 0u) return std::nullopt;
    const std::uint32_t localIdx = rid - 1u;
    const std::int64_t  legacyIdx =
        static_cast<std::int64_t>(methodGlobalIndex) - static_cast<std::int64_t>(imageMethodStart);
    if (static_cast<std::int64_t>(localIdx) != legacyIdx) {
        outIndexMismatch = true;
    }
    if (static_cast<std::uint64_t>(localIdx) >= moduleMethodCount) {
        return std::nullopt;
    }
    return localIdx;
}

}  // namespace Discrete::Shared
