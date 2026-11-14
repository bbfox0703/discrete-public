//! Group-scan SDR matcher (Phase R2) — behaviourally identical port of the
//! legacy (non-`DISCRETE_RUST_R2`) half of `dll/src/shared/GroupMatch.h`.
//!
//! Given, per candidate object, each slot's list of matching leaf indices,
//! decide whether every slot can be given a DISTINCT leaf (a System of
//! Distinct Representatives), and recover which leaf each slot gets. The
//! algorithm is the legacy one, step for step: Kuhn's augmenting path, slots
//! processed in index order, each slot's leaf list walked in list order, a
//! fresh `seen` set per top-level slot. `resolve_assignment` therefore returns
//! exactly the leaf the legacy `ResolveAssignment` would return for each slot
//! — not merely *an* SDR.
//!
//! Differences from the C++ that do not change any result:
//!
//! * The legacy `TryAssign_` recursion is replaced by an explicit stack
//!   (`try_assign`), visiting the same frames in the same order. Legacy
//!   recursion depth is bounded by the slot count (see `try_assign`), so a
//!   huge slot list could overflow the game thread's stack in C++; here it
//!   cannot.
//! * Allocation is fallible. Legacy allocates `nLeaves` ints (plus `nLeaves`
//!   bytes per slot) with `std::vector`, which throws on failure; the Rust
//!   side never aborts the host on OOM and instead reports "no assignment"
//!   (and logs it). This is the one observable deviation, and only in a case
//!   where the legacy code would have thrown `std::bad_alloc` /
//!   `std::length_error` out of a function its callers treat as `bool`.
//! * `seen` is allocated once and reset through a "touched" list instead of
//!   being re-allocated zeroed for every slot (same contents, less work).
//!
//! The C ABI (`dc_gm_*`) is the "Phase R2: GroupMatch SDR" section of
//! `include/discrete_core.h`: slots arrive in CSR form and the C++ wrapper
//! `dll/src/shared/GroupMatchRs.cpp` flattens `std::vector<SlotMatches>`.

use crate::ffi::{LogCategory, guard, log};

/// "Leaf not assigned to any slot" — the legacy `-1` in `leafToSlot`. Slot
/// ids are stored as `u32` (half the memory of `usize` on x64; legacy uses
/// `int`). Every slot id fits: `match_slots` rejects `n_slots > n_leaves`
/// up front and `n_leaves <= i32::MAX`.
const FREE: u32 = u32::MAX;

/// Why no assignment was produced.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SdrError {
    /// No complete SDR exists (or the input is degenerate: no slots, negative
    /// `n_leaves`, an empty slot) — legacy `return false`.
    NoAssignment,
    /// A working buffer could not be allocated (legacy would have thrown).
    AllocFailed,
}

/// Read access to per-slot leaf-index lists. Implemented for slices of
/// anything that derefs to `[i32]` (tests, pure callers) and for the CSR view
/// the C ABI receives.
pub trait Slots {
    fn slot_count(&self) -> usize;
    fn slot(&self, s: usize) -> &[i32];
}

impl<T: AsRef<[i32]>> Slots for [T] {
    fn slot_count(&self) -> usize {
        self.len()
    }
    fn slot(&self, s: usize) -> &[i32] {
        self[s].as_ref()
    }
}

/// CSR view: slot `s` is `leaves[offsets[s] - base .. offsets[s + 1] - base]`
/// where `base = offsets[0]` (the C++ wrapper always passes `offsets[0] == 0`,
/// but the contract does not require it, so the view starts at `offsets[0]`
/// and never claims memory before it).
#[derive(Clone, Copy, Debug)]
pub struct Csr<'a> {
    offsets: &'a [usize],
    base: usize,
    leaves: &'a [i32],
}

impl<'a> Csr<'a> {
    /// Validates and wraps borrowed CSR arrays. `offsets` must be empty (no
    /// slots) or non-decreasing with `leaves.len() == offsets[last] -
    /// offsets[0]`. Returns `None` for anything else.
    pub fn new(offsets: &'a [usize], leaves: &'a [i32]) -> Option<Self> {
        if offsets.is_empty() {
            return Some(Self { offsets, base: 0, leaves: &[] });
        }
        if offsets.windows(2).any(|w| w[0] > w[1]) {
            return None;
        }
        let base = offsets[0];
        let len = offsets[offsets.len() - 1] - base; // cannot underflow: monotone
        if leaves.len() != len {
            return None;
        }
        Some(Self { offsets, base, leaves })
    }

    /// Builds the view from the raw C ABI arguments.
    ///
    /// Judgement call — malformed input (NULL `offsets` with slots, offsets
    /// that decrease, sizes no allocation could have, NULL `leaves` while a
    /// slot has entries) returns `None`, which the entry points turn into the
    /// failure value 0. The C++ wrapper never produces such input; the legacy
    /// code had no equivalent (it received a well-formed `std::vector`).
    ///
    /// # Safety
    /// When `n_slots > 0`, `offsets` must point to `n_slots + 1` readable
    /// `usize`s, and when `offsets` is non-decreasing, `leaves` must point so
    /// that `leaves[offsets[0] .. offsets[n_slots])` is readable. Both must
    /// stay valid and unmodified for `'a`.
    pub unsafe fn from_raw(offsets: *const usize, leaves: *const i32, n_slots: usize) -> Option<Self> {
        if n_slots == 0 {
            // Contract: offsets may be NULL only when n_slots == 0. Nothing to read.
            return Some(Self { offsets: &[], base: 0, leaves: &[] });
        }
        if offsets.is_null() {
            return None;
        }
        let n_off = n_slots.checked_add(1)?;
        if n_off > isize::MAX as usize / size_of::<usize>() {
            return None; // no real array can be this long; from_raw_parts would be UB
        }
        // SAFETY: non-null, n_off elements readable per the caller contract,
        // and n_off * size_of::<usize>() <= isize::MAX was checked above.
        let offsets: &'a [usize] = unsafe { std::slice::from_raw_parts(offsets, n_off) };
        if offsets.windows(2).any(|w| w[0] > w[1]) {
            return None;
        }
        let base = offsets[0];
        let len = offsets[n_slots] - base;
        let leaves: &'a [i32] = if len == 0 {
            // Every slot is empty; `leaves` may legitimately be NULL.
            &[]
        } else {
            if leaves.is_null()
                || base > isize::MAX as usize / size_of::<i32>()
                || len > isize::MAX as usize / size_of::<i32>()
            {
                return None;
            }
            // SAFETY: the caller guarantees leaves[base .. base + len) is
            // readable; sizes were bounded above.
            unsafe { std::slice::from_raw_parts(leaves.add(base), len) }
        };
        Some(Self { offsets, base, leaves })
    }
}

impl Slots for Csr<'_> {
    fn slot_count(&self) -> usize {
        self.offsets.len().saturating_sub(1)
    }
    fn slot(&self, s: usize) -> &[i32] {
        &self.leaves[self.offsets[s] - self.base..self.offsets[s + 1] - self.base]
    }
}

/// Legacy `HasDistinctAssignment`: true when every slot can be assigned a
/// DISTINCT leaf in `[0, n_leaves)`. False for no slots, negative
/// `n_leaves`, any empty slot, no SDR — and (deviation, see module docs) when
/// a working buffer cannot be allocated.
pub fn has_distinct_assignment<S: Slots + ?Sized>(m: &S, n_leaves: i32) -> bool {
    match_slots(m, n_leaves).is_ok()
}

/// Legacy `ResolveAssignment`: on success `out[s]` is the distinct leaf the
/// legacy Kuhn order assigns to slot `s` (every slot gets one, so no entry is
/// -1). On failure the legacy function leaves `out` all -1; callers of this
/// pure version get the error instead.
pub fn resolve_assignment<S: Slots + ?Sized>(m: &S, n_leaves: i32) -> Result<Vec<i32>, SdrError> {
    let leaf_to_slot = match_slots(m, n_leaves)?;
    let mut out = try_filled(m.slot_count(), -1i32)?;
    // Legacy: `for (leaf = 0; leaf < nLeaves; ++leaf) if (s >= 0) out[s] = leaf;`
    // Each slot owns exactly one leaf after a successful match (see
    // `try_assign`), so the scan order cannot change the result; it is kept
    // anyway.
    for (leaf, &s) in leaf_to_slot.iter().enumerate() {
        if s != FREE {
            // leaf < n_leaves <= i32::MAX, so the cast is lossless.
            out[s as usize] = leaf as i32;
        }
    }
    Ok(out)
}

/// `Vec` of `n` copies of `v`, or `AllocFailed` instead of aborting.
fn try_filled<T: Clone>(n: usize, v: T) -> Result<Vec<T>, SdrError> {
    let mut vec = Vec::new();
    vec.try_reserve_exact(n).map_err(|_| SdrError::AllocFailed)?;
    vec.resize(n, v);
    Ok(vec)
}

fn try_with_capacity<T>(n: usize) -> Result<Vec<T>, SdrError> {
    let mut vec = Vec::new();
    vec.try_reserve_exact(n).map_err(|_| SdrError::AllocFailed)?;
    Ok(vec)
}

/// The legacy driver loop shared by `HasDistinctAssignment` and
/// `ResolveAssignment`. Returns the final `leafToSlot` table.
fn match_slots<S: Slots + ?Sized>(m: &S, n_leaves: i32) -> Result<Vec<u32>, SdrError> {
    let n_slots = m.slot_count();
    // Legacy: `if (m.empty() || nLeaves < 0) return false;`
    if n_slots == 0 || n_leaves < 0 {
        return Err(SdrError::NoAssignment);
    }
    // Judgement call: legacy tests `m[s].leafIdx.empty()` only when its loop
    // reaches slot s, after slots 0..s were assigned. Any empty slot makes the
    // answer false either way (and a failed ResolveAssignment leaves out all
    // -1 regardless of where it failed), so testing them all first gives the
    // same result and skips the allocations for a doomed call.
    if (0..n_slots).any(|s| m.slot(s).is_empty()) {
        return Err(SdrError::NoAssignment);
    }
    let n = n_leaves as usize; // n_leaves >= 0 checked above
    // Judgement call: with more slots than leaves no SDR can exist (pigeonhole
    // — the assigned leaves must be distinct members of [0, n_leaves)), and
    // Kuhn's algorithm is exact, so legacy returns false here too. Deciding it
    // up front also bounds n_slots <= i32::MAX, so slot ids fit in u32 (where
    // legacy's `static_cast<int>(s)` would otherwise wrap).
    if n_slots > n {
        return Err(SdrError::NoAssignment);
    }

    // Legacy: `std::vector<int> leafToSlot(nLeaves, -1);`
    let mut leaf_to_slot: Vec<u32> = try_filled(n, FREE)?;
    // Legacy re-creates `std::vector<char> seen(nLeaves, 0)` per slot; one
    // buffer reset via `touched` holds identical contents at every step.
    let mut seen: Vec<bool> = try_filled(n, false)?;
    // A top-level call marks each in-range leaf at most once, so it touches at
    // most min(n_leaves, total list entries) leaves. Reserving that bound means
    // `touched.push` never reallocates (an infallible reallocation could abort).
    let total_entries: usize = (0..n_slots).map(|s| m.slot(s).len()).fold(0usize, usize::saturating_add);
    let mut touched: Vec<usize> = try_with_capacity(n.min(total_entries))?;
    // Stack depth is at most n_slots frames (see `try_assign`).
    let mut stack: Vec<Frame> = try_with_capacity(n_slots)?;

    for s in 0..n_slots {
        for &lf in &touched {
            seen[lf] = false;
        }
        touched.clear();
        // s < n_slots <= n_leaves <= i32::MAX: fits in u32 and never equals FREE.
        if !try_assign(m, s as u32, &mut leaf_to_slot, &mut seen, &mut touched, &mut stack) {
            return Err(SdrError::NoAssignment);
        }
    }
    Ok(leaf_to_slot)
}

/// One activation of the legacy recursive `TryAssign_(slot, ...)`.
struct Frame {
    /// The `slot` argument.
    slot: u32,
    /// Index of the next entry of `m[slot]` the range-for will visit.
    pos: usize,
    /// The leaf whose current owner the pending child call is re-seating
    /// (meaningful only while a child frame sits above this one).
    leaf: usize,
}

/// Iterative form of the legacy
///
/// ```c++
/// bool TryAssign_(int slot, ...) {
///     for (int leaf : m[slot].leafIdx) {
///         size_t lf = (size_t)leaf;
///         if (lf >= seen.size()) continue;   // out of range (negative wraps)
///         if (seen[lf]) continue;
///         seen[lf] = 1;
///         if (leafToSlot[lf] == -1 || TryAssign_(leafToSlot[lf], ...)) {
///             leafToSlot[lf] = slot;
///             return true;
///         }
///     }
///     return false;
/// }
/// ```
///
/// Each stack frame is one activation; `child` carries a returning call's
/// result to its parent, which then either commits `leafToSlot[lf] = slot`
/// and returns true, or resumes its loop at the next leaf — the same order of
/// reads and writes as the recursion.
///
/// Depth bound: after every successful top-level call each slot owns at most
/// one leaf (a re-seated slot's old leaf is immediately overwritten by its
/// new owner). A child call is only made into the owner of a leaf just marked
/// `seen`, so a slot can be entered only through its single owned leaf, once
/// per top-level call; the root slot owns no leaf yet. Hence at most
/// `n_slots` frames — exactly the legacy recursion depth.
fn try_assign<S: Slots + ?Sized>(
    m: &S,
    root: u32,
    leaf_to_slot: &mut [u32],
    seen: &mut [bool],
    touched: &mut Vec<usize>,
    stack: &mut Vec<Frame>,
) -> bool {
    let n = seen.len();
    stack.clear();
    stack.push(Frame { slot: root, pos: 0, leaf: usize::MAX });
    let mut child: Option<bool> = None;
    loop {
        let top = stack.len() - 1;
        let slot = stack[top].slot;
        // `take()` consumes a pending child result either way; Some(false)
        // (the child failed) falls through so the legacy loop continues with
        // the next leaf, None means this frame was just entered.
        if child.take() == Some(true) {
            // `leafToSlot[lf] = slot; return true;`
            leaf_to_slot[stack[top].leaf] = slot;
            stack.pop();
            if stack.is_empty() {
                return true;
            }
            child = Some(true);
            continue;
        }

        let list = m.slot(slot as usize);
        let frame = &mut stack[top];
        let mut descend_into: Option<u32> = None;
        let mut assigned = false;
        while frame.pos < list.len() {
            let leaf = list[frame.pos];
            frame.pos += 1;
            // `(size_t)leaf >= seen.size()`: a negative int converts to a value
            // >= 2^31 on both x86 and x64, which always exceeds nLeaves <=
            // INT_MAX — i.e. negative and too-large indices are both skipped.
            let Ok(lf) = usize::try_from(leaf) else { continue };
            if lf >= n || seen[lf] {
                continue;
            }
            seen[lf] = true;
            touched.push(lf);
            let owner = leaf_to_slot[lf];
            if owner == FREE {
                leaf_to_slot[lf] = slot;
                assigned = true;
            } else {
                frame.leaf = lf;
                descend_into = Some(owner);
            }
            break;
        }

        if let Some(owner) = descend_into {
            // `TryAssign_(leafToSlot[lf], ...)`
            stack.push(Frame { slot: owner, pos: 0, leaf: usize::MAX });
            continue;
        }
        // Either assigned a free leaf (return true) or the loop ran out
        // (return false).
        stack.pop();
        if stack.is_empty() {
            return assigned;
        }
        child = Some(assigned);
    }
}

fn log_alloc_failure(entry: &str, n_slots: usize, n_leaves: i32) {
    log(
        LogCategory::Scan,
        &format!(
            "discrete_core: {entry}: could not allocate working buffers \
             (n_slots={n_slots}, n_leaves={n_leaves}); reporting no assignment"
        ),
    );
}

/// C ABI: legacy `HasDistinctAssignment` over CSR slots. 1 = an SDR exists,
/// 0 = it does not (or degenerate / malformed input, allocation failure,
/// internal panic).
///
/// # Safety
/// See `discrete_core.h`: `offsets` has `n_slots + 1` entries (NULL allowed
/// only when `n_slots == 0`) and `leaves[offsets[0] .. offsets[n_slots])` is
/// readable (`leaves` may be NULL when no slot has entries).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_gm_has_distinct_assignment(
    offsets: *const usize,
    leaves: *const i32,
    n_slots: usize,
    n_leaves: i32,
) -> i32 {
    guard("dc_gm_has_distinct_assignment", 0, || {
        // SAFETY: forwarded caller contract (see above).
        let Some(csr) = (unsafe { Csr::from_raw(offsets, leaves, n_slots) }) else {
            return 0;
        };
        match match_slots(&csr, n_leaves) {
            Ok(_) => 1,
            Err(SdrError::NoAssignment) => 0,
            Err(SdrError::AllocFailed) => {
                log_alloc_failure("dc_gm_has_distinct_assignment", n_slots, n_leaves);
                0
            }
        }
    })
}

/// C ABI: legacy `ResolveAssignment` over CSR slots. Returns 1 and writes
/// slot s's distinct leaf to `out[s]`, or returns 0 with every `out[s] = -1`.
///
/// `out` must hold `n_slots` entries (NULL allowed when `n_slots == 0`). It
/// is filled with -1 before anything else and only overwritten with the
/// result once the result is complete, so a panic mid-call (fallback 0) also
/// leaves it all -1. Judgement call: a NULL `out` with `n_slots > 0` cannot
/// be honoured and returns 0 without writing.
///
/// # Safety
/// As for [`dc_gm_has_distinct_assignment`], plus `out` writable for
/// `n_slots` `int32_t`s.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_gm_resolve_assignment(
    offsets: *const usize,
    leaves: *const i32,
    n_slots: usize,
    n_leaves: i32,
    out: *mut i32,
) -> i32 {
    guard("dc_gm_resolve_assignment", 0, || {
        let out: &mut [i32] = if n_slots == 0 {
            &mut []
        } else if out.is_null() || n_slots > isize::MAX as usize / size_of::<i32>() {
            return 0;
        } else {
            // SAFETY: non-null, n_slots writable elements per the contract,
            // size bounded above.
            unsafe { std::slice::from_raw_parts_mut(out, n_slots) }
        };
        // Legacy: `out.assign(m.size(), -1);` happens before any early return.
        out.fill(-1);
        // SAFETY: forwarded caller contract.
        let Some(csr) = (unsafe { Csr::from_raw(offsets, leaves, n_slots) }) else {
            return 0;
        };
        match resolve_assignment(&csr, n_leaves) {
            Ok(v) => {
                out.copy_from_slice(&v);
                1
            }
            Err(SdrError::NoAssignment) => 0,
            Err(SdrError::AllocFailed) => {
                log_alloc_failure("dc_gm_resolve_assignment", n_slots, n_leaves);
                0
            }
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Literal transcription of the legacy C++ (recursive TryAssign_, fresh
    /// `seen` per slot, `-1` sentinel, final leaf scan) — the oracle the
    /// iterative port is compared against.
    mod legacy {
        fn try_assign(slot: usize, m: &[Vec<i32>], leaf_to_slot: &mut [i32], seen: &mut [u8]) -> bool {
            for &leaf in &m[slot] {
                // size_t conversion: negative -> huge.
                let lf = leaf as isize as usize;
                if lf >= seen.len() {
                    continue;
                }
                if seen[lf] != 0 {
                    continue;
                }
                seen[lf] = 1;
                if leaf_to_slot[lf] == -1 || try_assign(leaf_to_slot[lf] as usize, m, leaf_to_slot, seen) {
                    leaf_to_slot[lf] = slot as i32;
                    return true;
                }
            }
            false
        }

        pub fn has(m: &[Vec<i32>], n_leaves: i32) -> bool {
            if m.is_empty() || n_leaves < 0 {
                return false;
            }
            let mut lts = vec![-1i32; n_leaves as usize];
            for s in 0..m.len() {
                if m[s].is_empty() {
                    return false;
                }
                let mut seen = vec![0u8; n_leaves as usize];
                if !try_assign(s, m, &mut lts, &mut seen) {
                    return false;
                }
            }
            true
        }

        pub fn resolve(m: &[Vec<i32>], n_leaves: i32) -> (bool, Vec<i32>) {
            let mut out = vec![-1i32; m.len()];
            if m.is_empty() || n_leaves < 0 {
                return (false, out);
            }
            let mut lts = vec![-1i32; n_leaves as usize];
            for s in 0..m.len() {
                if m[s].is_empty() {
                    return (false, out);
                }
                let mut seen = vec![0u8; n_leaves as usize];
                if !try_assign(s, m, &mut lts, &mut seen) {
                    return (false, out);
                }
            }
            for leaf in 0..n_leaves {
                let s = lts[leaf as usize];
                if s >= 0 {
                    out[s as usize] = leaf;
                }
            }
            (true, out)
        }
    }

    /// Flattens to CSR and runs both C entry points.
    fn via_ffi(m: &[Vec<i32>], n_leaves: i32) -> (i32, i32, Vec<i32>) {
        let mut offsets = vec![0usize];
        let mut leaves = Vec::new();
        for s in m {
            leaves.extend_from_slice(s);
            offsets.push(leaves.len());
        }
        let lp = if leaves.is_empty() { std::ptr::null() } else { leaves.as_ptr() };
        let op = if m.is_empty() { std::ptr::null() } else { offsets.as_ptr() };
        let mut out = vec![0x5A5A_5A5Ai32; m.len()];
        let outp = if m.is_empty() { std::ptr::null_mut() } else { out.as_mut_ptr() };
        // SAFETY: well-formed CSR arrays and an out buffer of m.len() entries.
        let has = unsafe { dc_gm_has_distinct_assignment(op, lp, m.len(), n_leaves) };
        // SAFETY: as above.
        let res = unsafe { dc_gm_resolve_assignment(op, lp, m.len(), n_leaves, outp) };
        (has, res, out)
    }

    fn check(m: &[Vec<i32>], n_leaves: i32) {
        let want_has = legacy::has(m, n_leaves);
        let (want_res, want_out) = legacy::resolve(m, n_leaves);
        assert_eq!(want_has, want_res, "oracle self-consistency for {m:?} / {n_leaves}");
        assert_eq!(has_distinct_assignment(m, n_leaves), want_has, "has {m:?} / {n_leaves}");
        match resolve_assignment(m, n_leaves) {
            Ok(v) => {
                assert!(want_res, "resolve Ok but legacy false: {m:?} / {n_leaves}");
                assert_eq!(v, want_out, "resolve {m:?} / {n_leaves}");
            }
            Err(e) => {
                assert_eq!(e, SdrError::NoAssignment);
                assert!(!want_res, "resolve Err but legacy true: {m:?} / {n_leaves}");
            }
        }
        let (has, res, out) = via_ffi(m, n_leaves);
        assert_eq!(has, want_has as i32, "ffi has {m:?} / {n_leaves}");
        assert_eq!(res, want_res as i32, "ffi resolve {m:?} / {n_leaves}");
        assert_eq!(out, want_out, "ffi out {m:?} / {n_leaves}");
    }

    #[test]
    fn degenerate_inputs() {
        check(&[], 4);
        check(&[], -1);
        check(&[vec![0]], -1);
        check(&[vec![0], vec![]], 4);
        check(&[vec![], vec![0]], 4);
        check(&[vec![0]], 0);
        check(&[vec![0, 1]], i32::MIN);
    }

    #[test]
    fn kuhn_reseats_in_legacy_order() {
        // Slot 0 takes leaf 0; slot 1 wants 0 too, so slot 0 is bumped to 1.
        assert_eq!(resolve_assignment(&[vec![0, 1], vec![0, 1]][..], 2), Ok(vec![1, 0]));
        // Duplicate value case ("24 AND 24"): both slots match leaves 3 and 5.
        assert_eq!(resolve_assignment(&[vec![3, 5], vec![3, 5]][..], 8), Ok(vec![5, 3]));
        // Free leaf found directly: no re-seat.
        assert_eq!(resolve_assignment(&[vec![0, 1], vec![1, 0]][..], 2), Ok(vec![0, 1]));
        // Three-level chain.
        assert_eq!(
            resolve_assignment(&[vec![0, 1], vec![1, 2], vec![0]][..], 3),
            Ok(vec![1, 2, 0])
        );
        check(&[vec![0, 1], vec![0, 1]], 2);
        check(&[vec![0], vec![0]], 5);
    }

    #[test]
    fn ignores_out_of_range_and_duplicates() {
        check(&[vec![-1, 7, 0], vec![0, 0, 9, 1]], 2);
        check(&[vec![i32::MIN, i32::MAX, 1], vec![1, -5, 1]], 3);
        check(&[vec![2, 2, 2], vec![2]], 3);
        check(&[vec![-1], vec![0]], 1);
        assert_eq!(resolve_assignment(&[vec![-1, 5, 1], vec![1, 0]][..], 2), Ok(vec![1, 0]));
    }

    #[test]
    fn more_slots_than_leaves_is_false() {
        check(&[vec![0], vec![1], vec![0, 1]], 2);
        check(&[vec![0], vec![0]], 1);
    }

    /// Small deterministic PRNG (no dev-dependencies allowed).
    struct Lcg(u64);
    impl Lcg {
        fn next(&mut self) -> u32 {
            self.0 = self.0.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            (self.0 >> 33) as u32
        }
        fn below(&mut self, n: u32) -> u32 {
            self.next() % n
        }
    }

    #[test]
    fn matches_legacy_on_random_cases() {
        let mut rng = Lcg(0x00C0_FFEE);
        for _ in 0..200_000 {
            let n_slots = rng.below(7) as usize;
            let n_leaves = rng.below(11) as i32 - 2; // -2 ..= 8
            let mut m = Vec::with_capacity(n_slots);
            for _ in 0..n_slots {
                // Lengths 0..=6 (an empty slot about one time in seven).
                let len = rng.below(7) as usize;
                let mut v = Vec::with_capacity(len);
                for _ in 0..len {
                    // Mostly in range, sometimes negative / past the end.
                    let leaf = match rng.below(10) {
                        0 => -(rng.below(4) as i32) - 1,
                        1 => 8 + rng.below(4) as i32,
                        _ => rng.below(9) as i32,
                    };
                    v.push(leaf);
                }
                m.push(v);
            }
            check(&m, n_leaves);
        }
    }

    #[test]
    fn matches_legacy_on_dense_random_cases() {
        // More slots and leaves, so long augmenting paths occur.
        let mut rng = Lcg(42);
        for _ in 0..20_000 {
            let n_leaves = rng.below(24) as i32;
            let n_slots = 1 + rng.below(n_leaves.max(1) as u32 + 2) as usize;
            let m: Vec<Vec<i32>> = (0..n_slots)
                .map(|_| (0..1 + rng.below(4)).map(|_| rng.below(n_leaves.max(1) as u32 + 1) as i32).collect())
                .collect();
            check(&m, n_leaves);
        }
    }

    #[test]
    fn deep_augmenting_path_does_not_overflow_the_stack() {
        // Slots 0..N-2 hold [i, i+1] and take leaf i; the last slot only
        // matches leaf 0, which re-seats every earlier slot in one augmenting
        // path N-1 frames deep — far beyond what the legacy recursion (or the
        // oracle above) survives on a 1 MiB thread stack.
        const N: usize = 1_000_000;
        let mut m: Vec<Vec<i32>> = (0..N - 1).map(|i| vec![i as i32, i as i32 + 1]).collect();
        m.push(vec![0]);
        let out = resolve_assignment(&m[..], N as i32).unwrap();
        for (i, &leaf) in out.iter().enumerate().take(N - 1) {
            assert_eq!(leaf, i as i32 + 1);
        }
        assert_eq!(out[N - 1], 0);
    }

    #[test]
    fn ffi_rejects_malformed_csr() {
        let leaves = [0i32, 1];
        let mut out = [7i32; 2];
        // Decreasing offsets.
        let bad = [0usize, 2, 1];
        // SAFETY: valid buffers; the content is what is malformed.
        unsafe {
            assert_eq!(dc_gm_has_distinct_assignment(bad.as_ptr(), leaves.as_ptr(), 2, 2), 0);
            assert_eq!(dc_gm_resolve_assignment(bad.as_ptr(), leaves.as_ptr(), 2, 2, out.as_mut_ptr()), 0);
        }
        assert_eq!(out, [-1, -1]);
        // NULL offsets with slots / NULL leaves with entries / NULL out.
        let good = [0usize, 1, 2];
        // SAFETY: NULLs are rejected before any read.
        unsafe {
            assert_eq!(dc_gm_has_distinct_assignment(std::ptr::null(), leaves.as_ptr(), 2, 2), 0);
            assert_eq!(dc_gm_has_distinct_assignment(good.as_ptr(), std::ptr::null(), 2, 2), 0);
            assert_eq!(dc_gm_resolve_assignment(good.as_ptr(), leaves.as_ptr(), 2, 2, std::ptr::null_mut()), 0);
            assert_eq!(dc_gm_has_distinct_assignment(good.as_ptr(), leaves.as_ptr(), 2, 2), 1);
        }
        // NULL leaves is fine when every slot is empty (answer: false).
        let empty = [3usize, 3, 3];
        out = [7, 7];
        // SAFETY: no leaf is read.
        unsafe {
            assert_eq!(dc_gm_has_distinct_assignment(empty.as_ptr(), std::ptr::null(), 2, 2), 0);
            assert_eq!(dc_gm_resolve_assignment(empty.as_ptr(), std::ptr::null(), 2, 2, out.as_mut_ptr()), 0);
        }
        assert_eq!(out, [-1, -1]);
        // n_slots == 0: offsets and out may be NULL.
        // SAFETY: nothing is read or written.
        unsafe {
            assert_eq!(dc_gm_has_distinct_assignment(std::ptr::null(), std::ptr::null(), 0, 3), 0);
            assert_eq!(dc_gm_resolve_assignment(std::ptr::null(), std::ptr::null(), 0, 3, std::ptr::null_mut()), 0);
        }
    }

    #[test]
    fn csr_with_nonzero_base() {
        // offsets[0] != 0: the view starts at leaves[offsets[0]].
        let leaves = [99i32, 99, 0, 1, 0, 1];
        let offsets = [2usize, 4, 6];
        let mut out = [0i32; 2];
        // SAFETY: leaves[2..6] readable, out has 2 entries.
        let r = unsafe { dc_gm_resolve_assignment(offsets.as_ptr(), leaves.as_ptr(), 2, 2, out.as_mut_ptr()) };
        assert_eq!((r, out), (1, [1, 0]));
        let csr = Csr::new(&offsets, &leaves[2..]).unwrap();
        assert_eq!(csr.slot_count(), 2);
        assert_eq!(csr.slot(1), &[0, 1]);
        assert!(Csr::new(&offsets, &leaves).is_none());
    }

    #[cfg(target_pointer_width = "32")]
    #[test]
    fn huge_n_leaves_allocation_failure_is_false_not_abort() {
        // i32::MAX u32 entries exceed isize::MAX bytes on 32-bit, so the
        // fallible reservation fails instead of aborting the host.
        let m = [vec![0], vec![1]];
        assert_eq!(resolve_assignment(&m[..], i32::MAX), Err(SdrError::AllocFailed));
        let (has, res, out) = via_ffi(&m, i32::MAX);
        assert_eq!((has, res, out), (0, 0, vec![-1, -1]));
    }
}
