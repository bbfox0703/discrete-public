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
// therefore pure combinatorics over ints: no memory reads, no Mono/IL2CPP API. (The richer per-width predicate evaluation
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

// Implemented in Rust (dll/rust/src/group_match.rs) behind GroupMatchRs.cpp.
// The original header-only C++ (Kuhn's augmenting path, recursive) was
// removed when Phase R2 closed; it is in git history (last in aae48d3).

// True when every slot can be assigned a DISTINCT leaf (a System of Distinct
// Representatives exists). `nLeaves` bounds the leaf-index space; leaf
// indices outside [0, nLeaves) are ignored. Handles the duplicate-value case
// ("24 AND 24" needs two different fields both holding 24) naturally via the
// matching. Returns false for an empty slot set, a negative nLeaves, or any
// slot with an empty list.
bool HasDistinctAssignment(const std::vector<SlotMatches>& m, int nLeaves);

// Recover the actual SDR after a true return: out[s] = the DISTINCT leaf
// index assigned to slot s (or -1 if none). Use this for each slot's
// representative leaf instead of m[s].leafIdx[0] -- the first matching leaf
// can be shared by two slots, which the SDR resolves to distinct
// representatives. Returns false (and fills out with -1) when no complete
// assignment exists.
bool ResolveAssignment(const std::vector<SlotMatches>& m, int nLeaves,
                       std::vector<int>& out);

}  // namespace Discrete::GroupMatch
