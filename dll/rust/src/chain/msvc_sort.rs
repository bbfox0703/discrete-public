//! A transcription of MSVC STL's `std::sort` (introsort) for the final
//! "sort by hitIP" step of the chain detector.
//!
//! Why not `slice::sort_by_key`: the legacy code sorts with `std::sort`, which
//! is NOT stable, and then keeps the first element of every run of equal hit
//! IPs (`std::unique`). When two matches share a hit IP but differ in
//! `hop_one_ip`, *which* one survives depends on the exact element moves of
//! MSVC's introsort. Ranges of at most 32 elements are insertion-sorted (and
//! so behave stably), larger ones are partitioned around a median-of-three /
//! Tukey ninther pivot, which reorders equal elements. Reproducing the
//! algorithm move for move is the only way to keep the survivor identical.
//!
//! Source: `<algorithm>` / `<__msvc_heap_algorithms.hpp>` of MSVC 14.52
//! (`_Sort_unchecked`, `_Insertion_sort_unchecked`, `_Partition_by_median_
//! guess_unchecked`, `_Partition_by_pivot_unchecked`, `_Guess_median_
//! unchecked`, `_Med3_unchecked`, `_Make_heap_unchecked`, `_Sort_heap_
//! unchecked`, `_Pop_heap_hole_by_index`, `_Push_heap_by_index`). The
//! algorithm has been unchanged across the MSVC releases this project
//! builds with. Iterators are translated to `isize` indices; the C++
//! `_Iter_diff_t` arithmetic is signed, so the same type is used here.
//! `_DEBUG_LT_PRED` (a debug-only strict-weak-order check) only re-evaluates
//! the predicate and never changes the result, so it is a plain `lt` here.

/// Sorts `v` exactly like MSVC's `std::sort(v.begin(), v.end(), lt)`.
pub(crate) fn sort_by<T: Copy>(v: &mut [T], lt: impl Fn(&T, &T) -> bool) {
    let n = v.len() as isize;
    sort_unchecked(v, 0, n, n, &lt);
}

const ISORT_MAX: isize = 32;

fn at<T: Copy>(v: &[T], i: isize) -> T {
    v[i as usize]
}

fn put<T: Copy>(v: &mut [T], i: isize, x: T) {
    v[i as usize] = x;
}

fn swap<T: Copy>(v: &mut [T], a: isize, b: isize) {
    v.swap(a as usize, b as usize);
}

fn sort_unchecked<T: Copy>(v: &mut [T], mut first: isize, mut last: isize, mut ideal: isize, lt: &impl Fn(&T, &T) -> bool) {
    loop {
        if last - first <= ISORT_MAX {
            insertion_sort(v, first, last, lt);
            return;
        }
        if ideal <= 0 {
            make_heap(v, first, last, lt);
            sort_heap(v, first, last, lt);
            return;
        }
        let (mid_first, mid_second) = partition_by_median_guess(v, first, last, lt);
        ideal = (ideal >> 1) + (ideal >> 2);
        if mid_first - first < last - mid_second {
            sort_unchecked(v, first, mid_first, ideal, lt);
            first = mid_second;
        } else {
            sort_unchecked(v, mid_second, last, ideal, lt);
            last = mid_first;
        }
    }
}

fn insertion_sort<T: Copy>(v: &mut [T], first: isize, last: isize, lt: &impl Fn(&T, &T) -> bool) {
    if first == last {
        return;
    }
    let mut mid = first;
    loop {
        mid += 1;
        if mid == last {
            break;
        }
        let mut hole = mid;
        let val = at(v, mid);
        if lt(&val, &at(v, first)) {
            // _Move_backward_unchecked(first, mid, mid + 1)
            v.copy_within(first as usize..mid as usize, first as usize + 1);
            put(v, first, val);
        } else {
            let mut prev = hole;
            loop {
                prev -= 1;
                if !lt(&val, &at(v, prev)) {
                    break;
                }
                let x = at(v, prev);
                put(v, hole, x);
                hole = prev;
            }
            put(v, hole, val);
        }
    }
}

fn med3<T: Copy>(v: &mut [T], first: isize, mid: isize, last: isize, lt: &impl Fn(&T, &T) -> bool) {
    if lt(&at(v, mid), &at(v, first)) {
        swap(v, mid, first);
    }
    if lt(&at(v, last), &at(v, mid)) {
        swap(v, last, mid);
        if lt(&at(v, mid), &at(v, first)) {
            swap(v, mid, first);
        }
    }
}

fn guess_median<T: Copy>(v: &mut [T], first: isize, mid: isize, last: isize, lt: &impl Fn(&T, &T) -> bool) {
    let count = last - first;
    if 40 < count {
        let step = (count + 1) >> 3;
        let two_step = step << 1;
        med3(v, first, first + step, first + two_step, lt);
        med3(v, mid - step, mid, mid + step, lt);
        med3(v, last - two_step, last - step, last, lt);
        med3(v, first + step, mid, last - step, lt);
    } else {
        med3(v, first, mid, last, lt);
    }
}

fn partition_by_median_guess<T: Copy>(v: &mut [T], first: isize, last: isize, lt: &impl Fn(&T, &T) -> bool) -> (isize, isize) {
    let mid = first + ((last - first) >> 1);
    guess_median(v, first, mid, last - 1, lt);
    partition_by_pivot(v, first, mid, last, lt)
}

fn partition_by_pivot<T: Copy>(
    v: &mut [T],
    first: isize,
    mut pfirst: isize,
    last: isize,
    lt: &impl Fn(&T, &T) -> bool,
) -> (isize, isize) {
    let mut plast = pfirst + 1;
    while first < pfirst && !lt(&at(v, pfirst - 1), &at(v, pfirst)) && !lt(&at(v, pfirst), &at(v, pfirst - 1)) {
        pfirst -= 1;
    }
    while plast < last && !lt(&at(v, plast), &at(v, pfirst)) && !lt(&at(v, pfirst), &at(v, plast)) {
        plast += 1;
    }
    let mut gfirst = plast;
    let mut glast = pfirst;
    loop {
        while gfirst < last {
            if lt(&at(v, pfirst), &at(v, gfirst)) {
                // continue
            } else if lt(&at(v, gfirst), &at(v, pfirst)) {
                break;
            } else if plast != gfirst {
                swap(v, plast, gfirst);
                plast += 1;
            } else {
                plast += 1;
            }
            gfirst += 1;
        }
        while first < glast {
            let glast_prev = glast - 1;
            if lt(&at(v, glast_prev), &at(v, pfirst)) {
                // continue
            } else if lt(&at(v, pfirst), &at(v, glast_prev)) {
                break;
            } else {
                pfirst -= 1;
                if pfirst != glast_prev {
                    swap(v, pfirst, glast_prev);
                }
            }
            glast -= 1;
        }
        if glast == first && gfirst == last {
            return (pfirst, plast);
        }
        if glast == first {
            if plast != gfirst {
                swap(v, pfirst, plast);
            }
            plast += 1;
            swap(v, pfirst, gfirst);
            pfirst += 1;
            gfirst += 1;
        } else if gfirst == last {
            glast -= 1;
            pfirst -= 1;
            if glast != pfirst {
                swap(v, glast, pfirst);
            }
            plast -= 1;
            swap(v, pfirst, plast);
        } else {
            glast -= 1;
            swap(v, gfirst, glast);
            gfirst += 1;
        }
    }
}

fn push_heap_by_index<T: Copy>(v: &mut [T], first: isize, mut hole: isize, top: isize, val: T, lt: &impl Fn(&T, &T) -> bool) {
    let mut idx = (hole - 1) >> 1;
    while top < hole && lt(&at(v, first + idx), &val) {
        let x = at(v, first + idx);
        put(v, first + hole, x);
        hole = idx;
        idx = (hole - 1) >> 1;
    }
    put(v, first + hole, val);
}

fn pop_heap_hole_by_index<T: Copy>(v: &mut [T], first: isize, mut hole: isize, bottom: isize, val: T, lt: &impl Fn(&T, &T) -> bool) {
    let top = hole;
    let mut idx = hole;
    let max_sequence_non_leaf = (bottom - 1) >> 1;
    while idx < max_sequence_non_leaf {
        idx = 2 * idx + 2;
        if lt(&at(v, first + idx), &at(v, first + (idx - 1))) {
            idx -= 1;
        }
        let x = at(v, first + idx);
        put(v, first + hole, x);
        hole = idx;
    }
    if idx == max_sequence_non_leaf && bottom % 2 == 0 {
        let x = at(v, first + (bottom - 1));
        put(v, first + hole, x);
        hole = bottom - 1;
    }
    push_heap_by_index(v, first, hole, top, val, lt);
}

fn make_heap<T: Copy>(v: &mut [T], first: isize, last: isize, lt: &impl Fn(&T, &T) -> bool) {
    let bottom = last - first;
    let mut hole = bottom >> 1;
    while hole > 0 {
        hole -= 1;
        let val = at(v, first + hole);
        pop_heap_hole_by_index(v, first, hole, bottom, val, lt);
    }
}

fn sort_heap<T: Copy>(v: &mut [T], first: isize, mut last: isize, lt: &impl Fn(&T, &T) -> bool) {
    while last - first >= 2 {
        // _Pop_heap_unchecked(first, last)
        let new_last = last - 1;
        let val = at(v, new_last);
        let x = at(v, first);
        put(v, new_last, x);
        pop_heap_hole_by_index(v, first, 0, new_last - first, val, lt);
        last -= 1;
    }
}
