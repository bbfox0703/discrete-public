// IL2CPPFieldXrefScanRs.cpp — implements the IL2CPPFieldXrefScan.h kernels
// over the Rust port (dll/rust/src/xref, C ABI dc_xref_* in
// dll/rust/include/discrete_core.h). The legacy Zydis implementation was
// removed once proven byte-identical; it is in git history as
// IL2CPPFieldXrefScan.cpp (last in 987b20d).
//
// This file holds no scan logic: it copies the caller's ranges into the C ABI
// structs, hands them plus the SEH read shim to Rust, and copies the
// candidates back in order. Rust never dereferences game memory — every byte
// of GameAssembly.dll it looks at comes through RustCore::SafeReadShim
// (Memory::SafeReadBytes under SEH), at exactly the addresses and lengths the
// legacy loops passed to Memory::SafeReadBytes. Rust catches its own panics
// (the scan returns NULL), so nothing unwinds across this boundary; the only
// C++ exceptions possible here are std::bad_alloc from the vectors below, as
// in the legacy implementation, and the RAII handles free the Rust result on
// that path too.

#include "IL2CPPFieldXrefScan.h"

#include "../shared/RustCore.h"

#include <discrete_core.h>

#include <cstddef>
#include <cstdint>
#include <vector>

namespace Discrete::IL2CPP::XrefScan {

namespace {

// Frees a Rust result handle on every exit path (NULL is accepted by the
// free functions).
template <typename H, void (*Free)(H*)>
class ResultHandle {
public:
    explicit ResultHandle(H* h) noexcept : h_(h) {}
    ~ResultHandle() { Free(h_); }
    ResultHandle(const ResultHandle&)            = delete;
    ResultHandle& operator=(const ResultHandle&) = delete;
    const H* get() const noexcept { return h_; }

private:
    H* h_;
};

using RipHandle  = ResultHandle<DcXrefRipResult, &dc_xref_rip_result_free>;
using InstHandle = ResultHandle<DcXrefInstResult, &dc_xref_inst_result_free>;

}  // namespace

std::vector<RipCandidate> ScanRipRelative(const std::vector<ExecSection>& sections,
                                          std::uintptr_t target) {
    std::vector<RipCandidate> out;

    std::vector<DcXrefSection> secs;
    secs.reserve(sections.size());
    for (const auto& s : sections) secs.push_back(DcXrefSection{s.va, s.size});

    const RipHandle r(dc_xref_rip_scan(secs.data(), secs.size(), target,
                                       &Discrete::RustCore::SafeReadShim, nullptr));
    // NULL = internal panic or the 1 MiB chunk buffer could not be allocated
    // (where the legacy std::vector would have thrown): no candidates.
    if (r.get() == nullptr) return out;

    const std::size_t n = dc_xref_rip_result_len(r.get());
    out.reserve(n);
    for (std::size_t i = 0; i < n; ++i) {
        DcXrefRipHit h{};
        if (dc_xref_rip_result_get(r.get(), i, &h) != 1) break;
        out.push_back(RipCandidate{h.ip, h.opcode});
    }
    return out;
}

std::vector<InstanceHit> ScanInstanceAccesses(const std::vector<MethodRange>& methods,
                                              std::size_t fieldOffset) {
    std::vector<InstanceHit> out;

    std::vector<DcXrefRange> ranges;
    ranges.reserve(methods.size());
    for (const auto& m : methods) ranges.push_back(DcXrefRange{m.start, m.len});

    const InstHandle r(dc_xref_inst_scan(ranges.data(), ranges.size(), fieldOffset,
                                         &Discrete::RustCore::SafeReadShim, nullptr));
    // NULL = internal panic or the read buffer could not be allocated: no hits.
    if (r.get() == nullptr) return out;

    const std::size_t n = dc_xref_inst_result_len(r.get());
    out.reserve(n);
    for (std::size_t i = 0; i < n; ++i) {
        DcXrefInstHit h{};
        if (dc_xref_inst_result_get(r.get(), i, &h) != 1) break;
        out.push_back(InstanceHit{h.method, h.ip, h.write != 0});
    }
    return out;
}

}  // namespace Discrete::IL2CPP::XrefScan
