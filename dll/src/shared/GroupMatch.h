// ============================================================
// GroupMatch — source-agnostic SDR / assignment core for "Multiple values
// group scan" (the discrete analogue of UE5CEDumper's Orden SDR matcher).
//
// Given, per candidate object, each slot's list of DISTINCT matching leaves
// (a leaf = one numeric field of the object, identified by its field offset),
// decide whether ALL N slots can be satisfied SIMULTANEOUSLY by DISTINCT leaves
// — a System of Distinct Representatives (SDR), solved with Kuhn's augmenting
// path. This is the object-aware analogue of Cheat Engine's "Group Scan".
//
// Design (per the user's steer): the LIVE value scan does NOT collect every
// numeric field of every object and re-implement the per-width compare here.
// Instead it REUSES the existing single-value pipeline — one shared
// ReverseLookupIndex, then ScanForValue per slot-value — so slot MEMBERSHIP
// (which field matched which value) is already known from the scanner, and the
// caller hands this matcher only the per-slot leaf-index lists. The matcher is
// therefore pure combinatorics over ints: no memory reads, no Mono/IL2CPP API,
// no dependency beyond <vector>. (The richer per-width predicate evaluation
// lives where the data lives: ScanForValue + CompareCandidateForRefine on the
// live side; the C# twin ui/.../GroupMatch.cs on the captured-snapshot side.)
//
// N is small (2..4 in the UI), so the SDR is O(slots * edges) — trivial.
// ============================================================

#pragma once

#include <cstddef>
#include <vector>

namespace Discrete::GroupMatch {

// Per-slot list of leaf indices (into the caller's per-object leaf vector) that
// currently satisfy the slot. This IS the progressive-convergence list the
// caller persists per candidate: refine re-reads each leaf and shrinks the
// list; a singleton means the field is "locked".
struct SlotMatches {
    std::vector<int> leafIdx;
};

// Internal: Kuhn's augmenting path — try to assign `slot` a distinct leaf,
// bumping already-assigned slots along an alternating path if needed.
inline bool TryAssign_(int slot, const std::vector<SlotMatches>& m,
                       std::vector<int>& leafToSlot, std::vector<char>& seen) {
    for (int leaf : m[static_cast<std::size_t>(slot)].leafIdx) {
        const std::size_t lf = static_cast<std::size_t>(leaf);
        if (lf >= seen.size()) continue;          // defensive: out-of-range leaf index
        if (seen[lf]) continue;
        seen[lf] = 1;
        if (leafToSlot[lf] == -1
            || TryAssign_(leafToSlot[lf], m, leafToSlot, seen)) {
            leafToSlot[lf] = slot;
            return true;
        }
    }
    return false;
}

// True when every slot can be assigned a DISTINCT leaf (a System of Distinct
// Representatives exists). `nLeaves` bounds the leaf-index space. An empty slot
// list means no SDR. Handles the duplicate-value case ("24 AND 24" needs two
// different fields both holding 24) naturally via the matching. Returns false
// for an empty slot set (a group scan needs >= 2 slots; the caller enforces the
// 2..4 bound).
inline bool HasDistinctAssignment(const std::vector<SlotMatches>& m, int nLeaves) {
    if (m.empty() || nLeaves < 0) return false;
    std::vector<int> leafToSlot(static_cast<std::size_t>(nLeaves), -1);
    for (std::size_t s = 0; s < m.size(); ++s) {
        if (m[s].leafIdx.empty()) return false;
        std::vector<char> seen(static_cast<std::size_t>(nLeaves), 0);
        if (!TryAssign_(static_cast<int>(s), m, leafToSlot, seen)) return false;
    }
    return true;
}

// Recover the actual SDR after a true return: out[s] = the DISTINCT leaf index
// assigned to slot s (or -1 if none). Use this for each slot's representative
// leaf instead of m[s].leafIdx[0] — the first matching leaf can be shared by
// two slots, which the SDR resolves to distinct representatives. Returns false
// (and fills out with -1) when no complete assignment exists.
inline bool ResolveAssignment(const std::vector<SlotMatches>& m, int nLeaves,
                              std::vector<int>& out) {
    out.assign(m.size(), -1);
    if (m.empty() || nLeaves < 0) return false;
    std::vector<int> leafToSlot(static_cast<std::size_t>(nLeaves), -1);
    for (std::size_t s = 0; s < m.size(); ++s) {
        if (m[s].leafIdx.empty()) return false;
        std::vector<char> seen(static_cast<std::size_t>(nLeaves), 0);
        if (!TryAssign_(static_cast<int>(s), m, leafToSlot, seen)) return false;
    }
    for (int leaf = 0; leaf < nLeaves; ++leaf) {
        const int s = leafToSlot[static_cast<std::size_t>(leaf)];
        if (s >= 0) out[static_cast<std::size_t>(s)] = leaf;
    }
    return true;
}

}  // namespace Discrete::GroupMatch
