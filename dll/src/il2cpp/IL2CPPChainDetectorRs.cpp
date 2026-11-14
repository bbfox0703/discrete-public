// IL2CPPChainDetectorRs.cpp — Phase R2. Implements the IL2CPPChainDetector.h
// API over the Rust port (dll/rust/src/chain, C ABI dc_chain_* in
// dll/rust/include/discrete_core.h). Compiled instead of
// IL2CPPChainDetector.cpp when DISCRETE_RUST_R2 is ON (root CMakeLists.txt).
//
// This file holds no detection logic. It keeps the legacy function's early
// returns and module lookup (so the C++ side decides *whether* to scan, with
// the same side effects), hands the module base plus the SEH read shim to
// Rust, and copies the result back into the C++ types.
//
// Rust never dereferences game memory: every byte of GameAssembly.dll it
// looks at comes through RustCore::SafeReadShim (Memory::SafeReadBytes under
// SEH), at exactly the addresses and lengths the legacy code passed to
// Memory::SafeRead / SafeReadBytes. The Rust side catches its own panics
// (dc_chain_detect returns NULL), so nothing unwinds across this boundary;
// the only C++ exceptions possible here are std::bad_alloc from the vectors
// below, exactly as in the legacy implementation, and the guard frees the
// Rust handle on that path too.

#include "IL2CPPChainDetector.h"

#include "../shared/MagicStrings.h"
#include "../shared/ModuleHelper.h"
#include "../shared/RustCore.h"

#include <discrete_core.h>

#include <cstddef>
#include <cstdint>
#include <string>
#include <utility>
#include <vector>

namespace Discrete::IL2CPP {

namespace {

// Frees the Rust result handle on every exit path (NULL is accepted).
class ChainResultHandle {
public:
    explicit ChainResultHandle(DcChainResult* r) noexcept : r_(r) {}
    ~ChainResultHandle() { dc_chain_result_free(r_); }
    ChainResultHandle(const ChainResultHandle&)            = delete;
    ChainResultHandle& operator=(const ChainResultHandle&) = delete;
    const DcChainResult* get() const noexcept { return r_; }

private:
    DcChainResult* r_;
};

}  // namespace

std::vector<ChainMatch> DetectFieldAccessChains(
    const std::vector<ClassPointerSlot>& classPointerSlots,
    std::size_t                          staticFieldsOffset,
    std::size_t                          targetFieldOffset,
    std::size_t                          matchLimit,
    ChainScanStats*                      stats) {

    std::vector<ChainMatch> result;
    // Same early returns as the legacy implementation, before the module
    // lookup; *stats is left untouched on these paths.
    if (classPointerSlots.empty() || staticFieldsOffset == 0) {
        return result;
    }

    void* gaBase = ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP);
    if (gaBase == nullptr) return result;

    // Only the slot addresses cross the boundary (sectionVa is debug-only and
    // the legacy scan never read it).
    std::vector<std::uintptr_t> slots;
    slots.reserve(classPointerSlots.size());
    for (const auto& s : classPointerSlots) slots.push_back(s.address);

    const ChainResultHandle r(dc_chain_detect(reinterpret_cast<std::uintptr_t>(gaBase),
                                              &Discrete::RustCore::SafeReadShim, nullptr,
                                              slots.data(), slots.size(),
                                              staticFieldsOffset, targetFieldOffset, matchLimit));
    // NULL = internal panic or the 1 MiB chunk buffer could not be allocated
    // (where the legacy std::vector would have thrown): no matches, stats
    // untouched.
    if (r.get() == nullptr) return result;

    const std::size_t n = dc_chain_result_len(r.get());
    result.reserve(n);
    for (std::size_t i = 0; i < n; ++i) {
        DcChainMatch m{};
        if (dc_chain_result_get(r.get(), i, &m) != 1) break;
        ChainMatch cm;
        cm.hitIP    = m.hit_ip;
        cm.hopOneIP = m.hop_one_ip;
        cm.refKind  = m.is_write != 0 ? "write" : "read";
        result.push_back(std::move(cm));
    }

    // The legacy function writes *stats only when the scan ran to completion
    // (it returns early, stats untouched, when the PE headers or executable
    // sections cannot be read); `valid` carries exactly that.
    if (stats != nullptr) {
        DcChainStats st{};
        dc_chain_result_stats(r.get(), &st);
        if (st.valid != 0) {
            stats->hopOneCandidates = st.hop_one_candidates;
            stats->chainBroken      = st.chain_broken;
            stats->chainsEmitted    = st.chains_emitted;
            stats->bytesScanned     = st.bytes_scanned;
            stats->failedReads      = st.failed_reads;
        }
    }
    return result;
}

}  // namespace Discrete::IL2CPP
