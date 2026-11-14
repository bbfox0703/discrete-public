//! Value-scan refine comparator (Phase R2) — behaviourally identical port of
//! `dll/src/shared/ValueRefine.cpp` (`Discrete::ValueRefine::
//! CompareCandidateForRefine`), the "Next Scan" filter shared by both
//! backends' `scanFieldValue` and group-scan handlers.
//!
//! The C ABI is `dc_value_refine_compare` (the "Phase R2: value-scan refine
//! comparator" section of `include/discrete_core.h`); the C++ wrapper is
//! `dll/src/shared/ValueRefineRs.cpp`.
//!
//! Semantics mirrored from the legacy code, case by case:
//!
//! * `dt` is the numeric `Discrete::Memory::ValueDataType` — NOTE the enum
//!   order is Int8, Int16, Int32, Int64, UInt8, UInt16, UInt32, UInt64, Float,
//!   Double (0..=9), signed types first. Any other value falls out of the
//!   legacy `switch` and drops without touching `prev`.
//! * `scan_type` is matched byte-for-byte (`std::string_view == "exact"`), so
//!   it is case-sensitive and an embedded / trailing NUL makes it unknown.
//!   `exact` and `newValue` are the same comparison. Unknown scan types drop
//!   without touching `prev`.
//! * Integers compare in their own type (signedness matters: 0xFF is -1 as
//!   Int8 but 255 as UInt8).
//! * Float / Double: a non-finite `cur` always drops. `exact` / `newValue`
//!   keep when `fabs(double(cur) - double(arg)) <= tolerance` (computed in
//!   f64 even for Float; a NaN or negative tolerance therefore never keeps,
//!   an infinite tolerance keeps every finite pair, an infinite `arg` only
//!   matches an infinite tolerance). `range` / `changed` / `unchanged` /
//!   `increased` / `decreased` compare in the value's own type with IEEE
//!   rules (any NaN operand makes `==`, `<`, `<=`, `>`, `>=` false and `!=`
//!   true; -0.0 == +0.0).
//! * On keep, the first `ValueDataTypeSize(dt)` bytes of `prev` are
//!   overwritten with `cur`; the remaining bytes are left alone.

use std::ptr;

use crate::ffi::guard;

/// Numeric values of `Discrete::Memory::ValueDataType` (Memory.h).
pub mod dt {
    pub const INT8: i32 = 0;
    pub const INT16: i32 = 1;
    pub const INT32: i32 = 2;
    pub const INT64: i32 = 3;
    pub const UINT8: i32 = 4;
    pub const UINT16: i32 = 5;
    pub const UINT32: i32 = 6;
    pub const UINT64: i32 = 7;
    pub const FLOAT: i32 = 8;
    pub const DOUBLE: i32 = 9;
}

/// Port of `Memory::ValueDataTypeSize`: byte width of `dt`, 0 for an unknown
/// type.
pub fn value_data_type_size(data_type: i32) -> usize {
    match data_type {
        dt::INT8 | dt::UINT8 => 1,
        dt::INT16 | dt::UINT16 => 2,
        dt::INT32 | dt::UINT32 | dt::FLOAT => 4,
        dt::INT64 | dt::UINT64 | dt::DOUBLE => 8,
        _ => 0,
    }
}

/// The refine modes the legacy comparator understands. `newValue` folds
/// into `Exact` because the legacy code tests `exact || newValue` together.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ScanType {
    Exact,
    Range,
    Changed,
    Unchanged,
    Increased,
    Decreased,
}

impl ScanType {
    /// Byte-exact match, like the legacy `std::string_view` comparisons.
    pub fn parse(s: &[u8]) -> Option<Self> {
        match s {
            b"exact" | b"newValue" => Some(Self::Exact),
            b"range" => Some(Self::Range),
            b"changed" => Some(Self::Changed),
            b"unchanged" => Some(Self::Unchanged),
            b"increased" => Some(Self::Increased),
            b"decreased" => Some(Self::Decreased),
            _ => None,
        }
    }

    /// Whether the legacy code reads `argLo` for this mode.
    fn reads_arg_lo(self) -> bool {
        matches!(self, Self::Exact | Self::Range)
    }

    /// Whether the legacy code reads `argHi` for this mode.
    fn reads_arg_hi(self) -> bool {
        matches!(self, Self::Range)
    }
}

/// One scalar lane type: decoded from the low `size_of::<Self>()` bytes of an
/// 8-byte payload in native byte order — exactly what the legacy
/// `std::memcpy(&name, src, sizeof(T))` does.
trait Lane: Copy + PartialOrd {
    fn load(b: &[u8; 8]) -> Self;
}

macro_rules! impl_lane {
    ($($t:ty),*) => {$(
        impl Lane for $t {
            fn load(b: &[u8; 8]) -> Self {
                let mut raw = [0u8; std::mem::size_of::<$t>()];
                raw.copy_from_slice(&b[..std::mem::size_of::<$t>()]);
                <$t>::from_ne_bytes(raw)
            }
        }
    )*};
}
impl_lane!(i8, u8, i16, u16, i32, u32, i64, u64, f32, f64);

/// Float lanes additionally widen to f64 (exact for f32 -> f64, like the
/// legacy `static_cast<double>`).
trait FloatLane: Lane {
    fn widen(self) -> f64;
}
impl FloatLane for f32 {
    fn widen(self) -> f64 {
        f64::from(self)
    }
}
impl FloatLane for f64 {
    fn widen(self) -> f64 {
        self
    }
}

/// The comparisons both `BODY_INT` and `BODY_FLT` share (`range` and the
/// prev-relative modes). Rust's `PartialOrd` on floats is IEEE-754, the same
/// as C++'s built-in operators, and is the ordinary total order on integers.
///
/// `arg_lo` / `arg_hi` are `None` only when the C caller passed NULL for an
/// argument this mode reads; the legacy code would dereference NULL (UB), so
/// any answer matches it for every defined input — we drop.
fn shared_compare<T: Lane>(
    kind: ScanType,
    c: T,
    p: T,
    arg_lo: Option<&[u8; 8]>,
    arg_hi: Option<&[u8; 8]>,
) -> bool {
    match kind {
        // `Exact` is handled by the callers (it differs between int / float).
        ScanType::Exact => unreachable!("exact handled by the typed body"),
        ScanType::Range => match (arg_lo, arg_hi) {
            (Some(lo), Some(hi)) => {
                let (lo, hi) = (T::load(lo), T::load(hi));
                // Same operator order as legacy `vCur >= vLo && vCur <= vHi`.
                c >= lo && c <= hi
            }
            _ => false,
        },
        ScanType::Changed => c != p,
        ScanType::Unchanged => c == p,
        ScanType::Increased => c > p,
        ScanType::Decreased => c < p,
    }
}

/// `BODY_INT(T)`.
fn body_int<T: Lane>(
    kind: ScanType,
    cur: &[u8; 8],
    prev: &[u8; 8],
    arg_lo: Option<&[u8; 8]>,
    arg_hi: Option<&[u8; 8]>,
) -> bool {
    let c = T::load(cur);
    let p = T::load(prev);
    match kind {
        ScanType::Exact => arg_lo.is_some_and(|a| c == T::load(a)),
        _ => shared_compare(kind, c, p, arg_lo, arg_hi),
    }
}

/// `BODY_FLT(T)`.
fn body_flt<T: FloatLane>(
    kind: ScanType,
    cur: &[u8; 8],
    prev: &[u8; 8],
    arg_lo: Option<&[u8; 8]>,
    arg_hi: Option<&[u8; 8]>,
    tolerance: f64,
) -> bool {
    let c = T::load(cur);
    let p = T::load(prev);
    // Legacy: `if (!std::isfinite(static_cast<double>(vCur))) return false;`
    // Widening preserves finiteness, so this equals is_finite on T.
    if !c.widen().is_finite() {
        return false;
    }
    match kind {
        ScanType::Exact => arg_lo.is_some_and(|a| {
            // fabs(double(cur) - double(arg)) <= tolerance, evaluated in f64
            // (MSVC /fp:precise with SSE2 on both x64 and x86 does the same:
            // no extended precision). f64::abs == fabs (clears the sign bit;
            // NaN stays NaN and then compares false).
            (c.widen() - T::load(a).widen()).abs() <= tolerance
        }),
        _ => shared_compare(kind, c, p, arg_lo, arg_hi),
    }
}

/// Typed comparator: `kind` already parsed, payloads already copied into
/// local 8-byte buffers. Returns keep/drop and, on keep, copies the first
/// `value_data_type_size(data_type)` bytes of `cur` into `prev`.
pub fn compare_parsed(
    data_type: i32,
    kind: ScanType,
    cur: &[u8; 8],
    prev: &mut [u8; 8],
    arg_lo: Option<&[u8; 8]>,
    arg_hi: Option<&[u8; 8]>,
    tolerance: f64,
) -> bool {
    let keep = match data_type {
        dt::INT8 => body_int::<i8>(kind, cur, prev, arg_lo, arg_hi),
        dt::UINT8 => body_int::<u8>(kind, cur, prev, arg_lo, arg_hi),
        dt::INT16 => body_int::<i16>(kind, cur, prev, arg_lo, arg_hi),
        dt::UINT16 => body_int::<u16>(kind, cur, prev, arg_lo, arg_hi),
        dt::INT32 => body_int::<i32>(kind, cur, prev, arg_lo, arg_hi),
        dt::UINT32 => body_int::<u32>(kind, cur, prev, arg_lo, arg_hi),
        dt::INT64 => body_int::<i64>(kind, cur, prev, arg_lo, arg_hi),
        dt::UINT64 => body_int::<u64>(kind, cur, prev, arg_lo, arg_hi),
        dt::FLOAT => body_flt::<f32>(kind, cur, prev, arg_lo, arg_hi, tolerance),
        dt::DOUBLE => body_flt::<f64>(kind, cur, prev, arg_lo, arg_hi, tolerance),
        // Legacy: falls out of the switch, `return false`, prev untouched.
        _ => return false,
    };
    if keep {
        let n = value_data_type_size(data_type);
        prev[..n].copy_from_slice(&cur[..n]);
    }
    keep
}

/// Safe, pure equivalent of `CompareCandidateForRefine` over 8-byte payloads.
/// An unknown `scan_type` drops without touching `prev` (for floats the
/// legacy code checks finiteness first, but both paths return false without
/// a prev update, so the order is unobservable).
pub fn compare_candidate_for_refine(
    data_type: i32,
    scan_type: &[u8],
    cur: &[u8; 8],
    prev: &mut [u8; 8],
    arg_lo: &[u8; 8],
    arg_hi: &[u8; 8],
    tolerance: f64,
) -> bool {
    match ScanType::parse(scan_type) {
        Some(kind) => compare_parsed(data_type, kind, cur, prev, Some(arg_lo), Some(arg_hi), tolerance),
        None => false,
    }
}

/// C ABI — see `dc_value_refine_compare` in `include/discrete_core.h`.
///
/// Memory access: the pointers are the caller's own small buffers (C++ stack
/// arrays / candidate records), never game memory, so they are copied
/// directly. Judgement calls, all of which agree with the legacy code for
/// every input on which the legacy code is defined:
///
/// * Only the first `ValueDataTypeSize(dt)` bytes of each buffer are read,
///   and `arg_lo` / `arg_hi` only for the modes that use them (exact /
///   newValue / range, resp. range) — the same bytes the legacy `memcpy`s
///   touch, so a caller passing a narrower buffer or a dangling unused
///   argument pointer is as safe as before.
/// * Payloads are copied into locals before comparing and `prev` is written
///   back (with an overlap-tolerant copy) only after the decision, so
///   `cur == prev` aliasing is harmless and a panic leaves `prev` untouched.
/// * NULL `cur` / `prev`, or a NULL argument the mode needs, drops (the
///   legacy code would dereference NULL). NULL `scan_type` with length 0 is
///   the empty string (a default `std::string_view`), which is unknown.
///
/// # Safety
/// Non-NULL pointers must be readable (`prev`: also writable) for
/// `ValueDataTypeSize(dt)` bytes; `scan_type` must be readable for
/// `scan_type_len` bytes when non-NULL.
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dc_value_refine_compare(
    data_type: i32,
    scan_type: *const u8,
    scan_type_len: usize,
    cur: *const u8,
    prev: *mut u8,
    arg_lo: *const u8,
    arg_hi: *const u8,
    tolerance: f64,
) -> i32 {
    guard("dc_value_refine_compare", 0, || {
        let n = value_data_type_size(data_type);
        if n == 0 {
            return 0;
        }
        let st: &[u8] = if scan_type.is_null() || scan_type_len == 0 {
            &[]
        } else {
            // SAFETY: caller contract — readable for scan_type_len bytes.
            unsafe { std::slice::from_raw_parts(scan_type, scan_type_len) }
        };
        let Some(kind) = ScanType::parse(st) else {
            return 0;
        };
        if cur.is_null() || prev.is_null() {
            return 0;
        }
        let load = |p: *const u8| -> [u8; 8] {
            let mut a = [0u8; 8];
            // SAFETY: p is non-NULL and readable for n (<= 8) bytes; `a` is a
            // distinct local, so the ranges cannot overlap.
            unsafe { ptr::copy_nonoverlapping(p, a.as_mut_ptr(), n) };
            a
        };
        let opt_load = |p: *const u8, needed: bool| -> Option<[u8; 8]> {
            (needed && !p.is_null()).then(|| load(p))
        };
        let c = load(cur);
        let mut p = load(prev.cast_const());
        let lo = opt_load(arg_lo, kind.reads_arg_lo());
        let hi = opt_load(arg_hi, kind.reads_arg_hi());
        let keep = compare_parsed(data_type, kind, &c, &mut p, lo.as_ref(), hi.as_ref(), tolerance);
        if keep {
            // SAFETY: prev is non-NULL and writable for n bytes; `p` is a local.
            unsafe { ptr::copy(p.as_ptr(), prev, n) };
        }
        i32::from(keep)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const ALL_DT: [i32; 10] = [
        dt::INT8,
        dt::INT16,
        dt::INT32,
        dt::INT64,
        dt::UINT8,
        dt::UINT16,
        dt::UINT32,
        dt::UINT64,
        dt::FLOAT,
        dt::DOUBLE,
    ];
    const SCANS: [&str; 7] = ["exact", "newValue", "range", "changed", "unchanged", "increased", "decreased"];

    fn pay(bytes: &[u8]) -> [u8; 8] {
        let mut a = [0u8; 8];
        a[..bytes.len()].copy_from_slice(bytes);
        a
    }
    fn f32p(v: f32) -> [u8; 8] {
        pay(&v.to_ne_bytes())
    }
    fn f64p(v: f64) -> [u8; 8] {
        v.to_ne_bytes()
    }
    fn i64p(v: i64) -> [u8; 8] {
        v.to_ne_bytes()
    }

    /// Runs the pure comparator; returns (keep, prev after).
    fn run(d: i32, st: &str, cur: [u8; 8], prev: [u8; 8], lo: [u8; 8], hi: [u8; 8], tol: f64) -> (bool, [u8; 8]) {
        let mut p = prev;
        let k = compare_candidate_for_refine(d, st.as_bytes(), &cur, &mut p, &lo, &hi, tol);
        (k, p)
    }

    /// Runs through the C ABI; must agree with `run`.
    fn run_ffi(d: i32, st: &[u8], cur: [u8; 8], prev: [u8; 8], lo: [u8; 8], hi: [u8; 8], tol: f64) -> (bool, [u8; 8]) {
        let mut p = prev;
        let r = unsafe {
            dc_value_refine_compare(d, st.as_ptr(), st.len(), cur.as_ptr(), p.as_mut_ptr(), lo.as_ptr(), hi.as_ptr(), tol)
        };
        assert!(r == 0 || r == 1);
        (r == 1, p)
    }

    #[test]
    fn sizes_match_memory_h() {
        let want = [1, 2, 4, 8, 1, 2, 4, 8, 4, 8];
        for (d, w) in ALL_DT.iter().zip(want) {
            assert_eq!(value_data_type_size(*d), w);
        }
        assert_eq!(value_data_type_size(-1), 0);
        assert_eq!(value_data_type_size(10), 0);
        assert_eq!(value_data_type_size(i32::MIN), 0);
    }

    #[test]
    fn unknown_dt_drops_and_leaves_prev() {
        for d in [-1, 10, 11, 255, i32::MAX, i32::MIN] {
            for st in SCANS {
                let prev = pay(&[9; 8]);
                assert_eq!(run(d, st, pay(&[1]), prev, pay(&[1]), pay(&[1]), 1.0), (false, prev));
                assert_eq!(run_ffi(d, st.as_bytes(), pay(&[1]), prev, pay(&[1]), pay(&[1]), 1.0), (false, prev));
            }
        }
    }

    #[test]
    fn unknown_scan_type_drops_and_leaves_prev() {
        for d in ALL_DT {
            for st in [&b""[..], b"Exact", b"EXACT", b"exact\0", b" exact", b"newvalue", b"between", b"rang"] {
                let prev = pay(&[7; 8]);
                assert_eq!(run_ffi(d, st, pay(&[0]), prev, pay(&[0]), pay(&[0; 8]), 1.0), (false, prev));
            }
        }
        // NULL scan_type with len 0 behaves like "".
        let mut p = [7u8; 8];
        let c = [0u8; 8];
        let r = unsafe { dc_value_refine_compare(dt::INT32, ptr::null(), 0, c.as_ptr(), p.as_mut_ptr(), c.as_ptr(), c.as_ptr(), 0.0) };
        assert_eq!((r, p), (0, [7u8; 8]));
    }

    #[test]
    fn int_exact_newvalue_range() {
        // Int32 = 5
        let cur = pay(&5i32.to_ne_bytes());
        for st in ["exact", "newValue"] {
            assert!(run(dt::INT32, st, cur, [0; 8], pay(&5i32.to_ne_bytes()), [0; 8], 0.0).0);
            assert!(!run(dt::INT32, st, cur, [0; 8], pay(&6i32.to_ne_bytes()), [0; 8], 0.0).0);
        }
        let r = |lo: i32, hi: i32| run(dt::INT32, "range", cur, [0; 8], pay(&lo.to_ne_bytes()), pay(&hi.to_ne_bytes()), 0.0).0;
        assert!(r(5, 5));
        assert!(r(-3, 5));
        assert!(r(5, 9));
        assert!(!r(6, 9));
        assert!(!r(9, 1)); // inverted range never matches
        assert!(!r(-9, 4));
    }

    #[test]
    fn signedness_matters() {
        // 0xFF: Int8 -1, UInt8 255. prev = 0.
        let cur = pay(&[0xFF]);
        assert!(run(dt::INT8, "decreased", cur, [0; 8], [0; 8], [0; 8], 0.0).0);
        assert!(!run(dt::INT8, "increased", cur, [0; 8], [0; 8], [0; 8], 0.0).0);
        assert!(run(dt::UINT8, "increased", cur, [0; 8], [0; 8], [0; 8], 0.0).0);
        assert!(!run(dt::UINT8, "decreased", cur, [0; 8], [0; 8], [0; 8], 0.0).0);
        // Extremes of every width.
        assert!(run(dt::INT64, "decreased", i64p(i64::MIN), i64p(i64::MAX), [0; 8], [0; 8], 0.0).0);
        assert!(run(dt::UINT64, "increased", i64p(i64::MIN), i64p(i64::MAX), [0; 8], [0; 8], 0.0).0);
        assert!(run(dt::INT16, "decreased", pay(&[0x00, 0x80]), pay(&[0xFF, 0x7F]), [0; 8], [0; 8], 0.0).0);
        assert!(run(dt::UINT16, "increased", pay(&[0x00, 0x80]), pay(&[0xFF, 0x7F]), [0; 8], [0; 8], 0.0).0);
        assert!(run(dt::INT32, "range", pay(&i32::MIN.to_ne_bytes()), [0; 8], pay(&i32::MIN.to_ne_bytes()), pay(&i32::MAX.to_ne_bytes()), 0.0).0);
    }

    #[test]
    fn high_bytes_ignored_for_narrow_types() {
        // Garbage above the type width must not affect the comparison.
        let cur = [5, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11];
        let prev = [5, 1, 2, 3, 4, 5, 6, 7];
        assert!(run(dt::UINT8, "unchanged", cur, prev, [0; 8], [0; 8], 0.0).0);
        assert!(run(dt::UINT8, "exact", cur, prev, [5, 9, 9, 9, 9, 9, 9, 9], [0; 8], 0.0).0);
        assert!(!run(dt::UINT16, "unchanged", cur, prev, [0; 8], [0; 8], 0.0).0);
    }

    #[test]
    fn prev_update_width_and_only_on_keep() {
        let cur = [1, 2, 3, 4, 5, 6, 7, 8];
        let prev = [0xF0; 8];
        for d in ALL_DT {
            let n = value_data_type_size(d);
            // changed: keeps (cur differs from prev in every width; for floats
            // the cur bit patterns are finite).
            let (k, p) = run(d, "changed", cur, prev, [0; 8], [0; 8], 0.0);
            assert!(k, "dt {d}");
            let mut want = prev;
            want[..n].copy_from_slice(&cur[..n]);
            assert_eq!(p, want, "dt {d}");
            let (k2, p2) = run_ffi(d, b"changed", cur, prev, [0; 8], [0; 8], 0.0);
            assert_eq!((k2, p2), (k, p));
            // unchanged: drops, prev untouched.
            assert_eq!(run(d, "unchanged", cur, prev, [0; 8], [0; 8], 0.0), (false, prev));
            assert_eq!(run_ffi(d, b"unchanged", cur, prev, [0; 8], [0; 8], 0.0), (false, prev));
        }
    }

    #[test]
    fn float_nonfinite_cur_drops_everywhere() {
        for st in SCANS {
            for v in [f32::NAN, f32::INFINITY, f32::NEG_INFINITY, f32::from_bits(0xFFC0_0001)] {
                let prev = f32p(1.0);
                assert_eq!(run(dt::FLOAT, st, f32p(v), prev, f32p(v), f32p(v), f64::INFINITY), (false, prev));
            }
            for v in [f64::NAN, f64::INFINITY, f64::NEG_INFINITY] {
                let prev = f64p(1.0);
                assert_eq!(run(dt::DOUBLE, st, f64p(v), prev, f64p(v), f64p(v), f64::INFINITY), (false, prev));
            }
        }
    }

    #[test]
    fn float_exact_tolerance_ieee() {
        let e = |c: f32, a: f32, t: f64| run(dt::FLOAT, "exact", f32p(c), [0; 8], f32p(a), [0; 8], t).0;
        assert!(e(1.0, 1.0, 0.0));
        assert!(e(0.0, -0.0, 0.0)); // -0.0 == +0.0
        assert!(!e(1.0, 1.0, -0.0001)); // negative tolerance never keeps
        assert!(!e(1.0, 1.0, f64::NAN)); // NaN tolerance never keeps
        assert!(!e(1.0, f32::NAN, f64::INFINITY)); // NaN arg -> NaN diff
        assert!(!e(1.0, f32::INFINITY, 1e300)); // inf diff
        assert!(e(1.0, f32::INFINITY, f64::INFINITY)); // inf <= inf
        assert!(e(f32::MAX, -f32::MAX, f64::INFINITY));
        // Difference computed in f64 after widening: 1.0f vs next float up is
        // exactly 2^-23 in double.
        let next = f32::from_bits(1.0f32.to_bits() + 1);
        let ulp = 2f64.powi(-23);
        assert!(e(1.0, next, ulp));
        assert!(!e(1.0, next, ulp * 0.999_999));
        // Identical non-dyadic values match at zero tolerance.
        assert!(e(0.1, 0.1, 0.0));
        // Subnormals compare exactly.
        let sub = f32::from_bits(1);
        assert!(e(sub, 0.0, f64::from(sub)));
        assert!(!e(sub, 0.0, f64::from(sub) / 2.0));

        let d = |c: f64, a: f64, t: f64| run(dt::DOUBLE, "newValue", f64p(c), [0; 8], f64p(a), [0; 8], t).0;
        assert!(d(1.0, 1.0 + 1e-5, 1e-4));
        assert!(!d(1.0, 1.0 + 1e-3, 1e-4));
        assert!(!d(f64::MAX, -f64::MAX, 1e308)); // diff overflows to inf
        assert!(d(f64::MAX, -f64::MAX, f64::INFINITY));
        assert!(!d(0.0, f64::NAN, f64::INFINITY));
    }

    #[test]
    fn float_relational_ieee() {
        let f = |st: &str, c: f32, p: f32| run(dt::FLOAT, st, f32p(c), f32p(p), [0; 8], [0; 8], 0.0).0;
        // NaN prev: changed true, everything else false.
        assert!(f("changed", 1.0, f32::NAN));
        assert!(!f("unchanged", 1.0, f32::NAN));
        assert!(!f("increased", 1.0, f32::NAN));
        assert!(!f("decreased", 1.0, f32::NAN));
        // -0.0 vs +0.0 are equal.
        assert!(f("unchanged", -0.0, 0.0));
        assert!(!f("changed", -0.0, 0.0));
        assert!(!f("increased", 0.0, -0.0));
        // Infinite prev with finite cur.
        assert!(f("increased", 1.0, f32::NEG_INFINITY));
        assert!(f("decreased", 1.0, f32::INFINITY));

        let r = |c: f64, lo: f64, hi: f64| run(dt::DOUBLE, "range", f64p(c), [0; 8], f64p(lo), f64p(hi), 0.0).0;
        assert!(r(1.0, 1.0, 1.0));
        assert!(r(0.0, -0.0, -0.0));
        assert!(!r(1.0, f64::NAN, 2.0));
        assert!(!r(1.0, 0.0, f64::NAN));
        assert!(r(1.0, f64::NEG_INFINITY, f64::INFINITY));
        // Float range compares in f32, not f64: lo given as f32 bits.
        let rf = |c: f32, lo: f32, hi: f32| run(dt::FLOAT, "range", f32p(c), [0; 8], f32p(lo), f32p(hi), 0.0).0;
        assert!(rf(0.1, 0.1, 0.1));
        assert!(!rf(0.1, f32::NAN, 1.0));
    }

    #[test]
    fn ffi_null_handling() {
        let c = pay(&5i32.to_ne_bytes());
        let mut p = [9u8; 8];
        // NULL cur / prev drop.
        let r = unsafe { dc_value_refine_compare(dt::INT32, b"changed".as_ptr(), 7, ptr::null(), p.as_mut_ptr(), ptr::null(), ptr::null(), 0.0) };
        assert_eq!((r, p), (0, [9u8; 8]));
        let r = unsafe { dc_value_refine_compare(dt::INT32, b"changed".as_ptr(), 7, c.as_ptr(), ptr::null_mut(), ptr::null(), ptr::null(), 0.0) };
        assert_eq!(r, 0);
        // Args are not read by prev-relative modes: NULL args are fine.
        let r = unsafe { dc_value_refine_compare(dt::INT32, b"changed".as_ptr(), 7, c.as_ptr(), p.as_mut_ptr(), ptr::null(), ptr::null(), 0.0) };
        assert_eq!(r, 1);
        assert_eq!(p, [5, 0, 0, 0, 9, 9, 9, 9]);
        // exact with NULL arg_lo drops; exact never reads arg_hi.
        let mut p = [0u8; 8];
        let r = unsafe { dc_value_refine_compare(dt::INT32, b"exact".as_ptr(), 5, c.as_ptr(), p.as_mut_ptr(), ptr::null(), ptr::null(), 0.0) };
        assert_eq!(r, 0);
        let r = unsafe { dc_value_refine_compare(dt::INT32, b"exact".as_ptr(), 5, c.as_ptr(), p.as_mut_ptr(), c.as_ptr(), ptr::null(), 0.0) };
        assert_eq!(r, 1);
        // range with NULL arg_hi drops.
        let mut p = [0u8; 8];
        let r = unsafe { dc_value_refine_compare(dt::INT32, b"range".as_ptr(), 5, c.as_ptr(), p.as_mut_ptr(), c.as_ptr(), ptr::null(), 0.0) };
        assert_eq!((r, p), (0, [0u8; 8]));
    }

    #[test]
    fn ffi_reads_only_type_width() {
        // A 1-byte buffer for UInt8 must be enough (Miri-style guard: the
        // slice is exactly 1 byte long).
        let cur = [3u8];
        let mut prev = [2u8];
        let lo = [1u8];
        let hi = [4u8];
        let r = unsafe { dc_value_refine_compare(dt::UINT8, b"range".as_ptr(), 5, cur.as_ptr(), prev.as_mut_ptr(), lo.as_ptr(), hi.as_ptr(), 0.0) };
        assert_eq!((r, prev), (1, [3u8]));
    }

    #[test]
    fn ffi_aliasing_cur_prev() {
        // cur and prev pointing at the same buffer (legacy memcpy onto
        // itself) — unchanged keeps, value stays.
        let mut b = pay(&42i32.to_ne_bytes());
        let r = unsafe { dc_value_refine_compare(dt::INT32, b"unchanged".as_ptr(), 9, b.as_ptr(), b.as_mut_ptr(), ptr::null(), ptr::null(), 0.0) };
        assert_eq!(r, 1);
        assert_eq!(b, pay(&42i32.to_ne_bytes()));
    }

    /// Exhaustive check over a value grid against an independent oracle
    /// written directly from the legacy macros, pure vs FFI.
    #[test]
    fn grid_matches_oracle() {
        let ints: [i64; 9] = [0, 1, -1, 127, -128, 255, i64::MAX, i64::MIN, 0x1234_5678_9ABC_DEF0];
        let flts: [f64; 12] = [0.0, -0.0, 1.0, -1.0, 0.5, 1e-40, f64::NAN, f64::INFINITY, f64::NEG_INFINITY, 3.4e38, 1e300, 0.1];
        let tols = [0.0, 0.0001, -1.0, f64::NAN, f64::INFINITY, 0.5];

        fn oracle(d: i32, st: &str, c: [u8; 8], p: [u8; 8], lo: [u8; 8], hi: [u8; 8], tol: f64) -> bool {
            macro_rules! int {
                ($t:ty) => {{
                    let n = std::mem::size_of::<$t>();
                    let ld = |b: [u8; 8]| <$t>::from_ne_bytes(b[..n].try_into().unwrap());
                    let (c, p, lo, hi) = (ld(c), ld(p), ld(lo), ld(hi));
                    match st {
                        "exact" | "newValue" => c == lo,
                        "range" => c >= lo && c <= hi,
                        "changed" => c != p,
                        "unchanged" => c == p,
                        "increased" => c > p,
                        "decreased" => c < p,
                        _ => false,
                    }
                }};
            }
            macro_rules! flt {
                ($t:ty) => {{
                    let n = std::mem::size_of::<$t>();
                    let ld = |b: [u8; 8]| <$t>::from_ne_bytes(b[..n].try_into().unwrap());
                    let (c, p, lo, hi) = (ld(c), ld(p), ld(lo), ld(hi));
                    if !(c as f64).is_finite() {
                        return false;
                    }
                    match st {
                        "exact" | "newValue" => ((c as f64) - (lo as f64)).abs() <= tol,
                        "range" => c >= lo && c <= hi,
                        "changed" => c != p,
                        "unchanged" => c == p,
                        "increased" => c > p,
                        "decreased" => c < p,
                        _ => false,
                    }
                }};
            }
            match d {
                dt::INT8 => int!(i8),
                dt::INT16 => int!(i16),
                dt::INT32 => int!(i32),
                dt::INT64 => int!(i64),
                dt::UINT8 => int!(u8),
                dt::UINT16 => int!(u16),
                dt::UINT32 => int!(u32),
                dt::UINT64 => int!(u64),
                dt::FLOAT => flt!(f32),
                dt::DOUBLE => flt!(f64),
                _ => false,
            }
        }

        let mut cases = 0usize;
        for d in ALL_DT {
            let vals: Vec<[u8; 8]> = match d {
                dt::FLOAT => flts.iter().map(|v| f32p(*v as f32)).collect(),
                dt::DOUBLE => flts.iter().map(|v| f64p(*v)).collect(),
                _ => ints.iter().map(|v| i64p(*v)).collect(),
            };
            let tl: &[f64] = if d >= dt::FLOAT { &tols } else { &[0.0] };
            for st in SCANS {
                for &c in &vals {
                    for &p in &vals {
                        for &lo in &vals {
                            for &hi in &vals {
                                if st != "range" && hi != vals[0] {
                                    continue;
                                }
                                for &t in tl {
                                    let want = oracle(d, st, c, p, lo, hi, t);
                                    let mut want_prev = p;
                                    if want {
                                        let n = value_data_type_size(d);
                                        want_prev[..n].copy_from_slice(&c[..n]);
                                    }
                                    assert_eq!(run(d, st, c, p, lo, hi, t), (want, want_prev));
                                    assert_eq!(run_ffi(d, st.as_bytes(), c, p, lo, hi, t), (want, want_prev));
                                    cases += 1;
                                }
                            }
                        }
                    }
                }
            }
        }
        assert!(cases > 10_000);
    }
}
