// GroupMatchRs.cpp — Phase R2. Implements the Discrete::GroupMatch API declared
// in the DISCRETE_RUST_R2 half of GroupMatch.h over the Rust port
// (dll/rust/src/group_match.rs, C ABI "Phase R2: GroupMatch SDR" in
// dll/rust/include/discrete_core.h).
//
// Compiled instead of the legacy header-only implementation when
// DISCRETE_RUST_R2 is defined. No matching logic lives here: each function
// flattens std::vector<SlotMatches> into the CSR arrays of the contract
// (slot s = leaves[offsets[s] .. offsets[s+1])) and makes one dc_gm_* call.
//
// Exceptions: the only throwing operations are this file's own std::vector
// allocations (std::bad_alloc), exactly as the legacy inline code could throw
// from its own vectors. They happen before / after the Rust call, never
// across it — the dc_gm_* functions are extern "C", never throw, and catch
// their own panics.
//
// The one behavioural deviation from the legacy code is inside Rust: when the
// Rust side cannot allocate its nLeaves-sized working buffers it returns "no
// assignment" (0) where the legacy std::vector would have thrown. See the
// module docs in group_match.rs.

#include "GroupMatch.h"

#include <discrete_core.h>

#include <cstddef>
#include <cstdint>
#include <type_traits>
#include <vector>

namespace Discrete::GroupMatch {

namespace {

// SlotMatches::leafIdx and the ResolveAssignment output are std::vector<int>;
// the C ABI speaks int32_t. Same type on every MSVC target (x86 and x64), so
// out.data() can be handed to Rust directly.
static_assert(std::is_same_v<int, std::int32_t>, "GroupMatch C ABI assumes int == int32_t");

struct FlatSlots {
    std::vector<std::size_t>  offsets;  // m.size() + 1 entries, offsets[0] == 0
    std::vector<std::int32_t> leaves;   // every slot's list, concatenated in slot order
};

FlatSlots Flatten(const std::vector<SlotMatches>& m) {
    FlatSlots f;
    std::size_t total = 0;
    for (const SlotMatches& s : m) total += s.leafIdx.size();
    f.offsets.reserve(m.size() + 1);
    f.leaves.reserve(total);
    f.offsets.push_back(0);
    for (const SlotMatches& s : m) {
        // Copied verbatim: duplicates, negative and out-of-range indices are
        // preserved so the Rust side sees exactly what the legacy code saw.
        f.leaves.insert(f.leaves.end(), s.leafIdx.begin(), s.leafIdx.end());
        f.offsets.push_back(f.leaves.size());
    }
    return f;
}

// The contract allows leaves == NULL when no slot has entries; vector::data()
// of an empty vector may be NULL or not, so normalise it.
const std::int32_t* LeavesPtr(const FlatSlots& f) {
    return f.leaves.empty() ? nullptr : f.leaves.data();
}

}  // namespace

bool HasDistinctAssignment(const std::vector<SlotMatches>& m, int nLeaves) {
    // Same early-out as the legacy code (and the Rust side); skips flattening.
    if (m.empty() || nLeaves < 0) return false;
    const FlatSlots f = Flatten(m);
    return dc_gm_has_distinct_assignment(f.offsets.data(), LeavesPtr(f),
                                         m.size(), nLeaves) == 1;
}

bool ResolveAssignment(const std::vector<SlotMatches>& m, int nLeaves,
                       std::vector<int>& out) {
    // Legacy: out.assign(m.size(), -1) happens before any early return.
    out.assign(m.size(), -1);
    if (m.empty() || nLeaves < 0) return false;
    const FlatSlots f = Flatten(m);
    if (dc_gm_resolve_assignment(f.offsets.data(), LeavesPtr(f), m.size(), nLeaves,
                                 out.data()) == 1) {
        return true;
    }
    // The Rust side already leaves out all -1 on failure (including after a
    // caught panic); re-assert it here so the legacy postcondition never
    // depends on that.
    out.assign(m.size(), -1);
    return false;
}

}  // namespace Discrete::GroupMatch
