//! IL2CPP field-xref scan kernels (Phase R2 follow-up) — port of
//! `dll/src/il2cpp/IL2CPPFieldXrefScan.cpp`, with Zydis replaced by the
//! chain detector's iced-x86 adapter (`crate::chain::decode`).
//!
//! `IL2CPPFieldXref.cpp` keeps everything that talks to the IL2CPP runtime
//! (resolver, class / method / field enumeration, kind filter, method
//! attribution, `limit`); it hands these two pure loops a list of code
//! ranges and gets raw candidates back in scan order:
//!
//! * `scan_rip` — the v1 static path: a byte-pattern scan for
//!   `REX? {8B,8D,89} ModRM(00,101) disp32` whose RIP-relative target equals
//!   the requested address. No decoder involved.
//! * `scan_inst` — the instance path: linear-decodes each method body and
//!   reports every visible `[gpr + fieldOffset]` memory operand (no index,
//!   displacement present, base not RIP / RSP / RBP) with its read / write
//!   classification.
//!
//! Both are line-by-line transcriptions of the C++ (chunking, overlap, skip
//! distances, wrapping `uintptr_t` arithmetic, loop order). The only
//! non-mechanical part is the per-instruction decoder facts of
//! `scan_inst`, which come from `chain::decode::InsnDecoder::decode_mem`
//! — Zydis' exact accept / reject and length (as for the chain detector) plus
//! the memory-operand facts the legacy loop read; see that module's
//! documentation for the mapping and how it was verified.
//!
//! Host memory is only ever read through [`Reader`] (the C++ SEH shim):
//! Rust never dereferences a game address (FFI rule 8).

pub(crate) mod apx_mem;
#[cfg(test)]
mod tests;

use std::ffi::c_void;

use crate::chain::decode::{InsnDecoder, MemAct, Reg};
use crate::ffi::{ReadFn, Reader, guard};
use iced_x86::Register;

/// The static path reads code in 1 MiB chunks ...
const CHUNK_SIZE: usize = 1024 * 1024;
/// ... plus a 16-byte overlap so a 7-byte candidate starting near the end of
/// a chunk is still complete (candidates only *start* in the owned part).
const OVERLAP: usize = 16;
/// Zydis never looks past 15 bytes; the decoder adapter is verified on
/// windows of up to 16, so longer remainders are cut to that.
const DECODE_WINDOW: usize = 16;

/// An executable section of the mapped module (absolute VA + VirtualSize) —
/// C++ `XrefScan::ExecSection`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DcXrefSection {
    pub va: usize,
    pub size: usize,
}

/// One RIP-relative candidate — C++ `XrefScan::RipCandidate`: `ip` is the
/// instruction start, `opcode` the primary opcode (8B / 8D / 89).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DcXrefRipHit {
    pub ip: usize,
    pub opcode: u8,
}

/// One method body to decode — C++ `XrefScan::MethodRange`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DcXrefRange {
    pub start: usize,
    pub len: usize,
}

/// One matching memory operand — C++ `XrefScan::InstanceHit`: `method`
/// indexes the input ranges, `ip` is the instruction start, `write` is 1 for
/// a write action (LEA counts as a read).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DcXrefInstHit {
    pub method: usize,
    pub ip: usize,
    pub write: u8,
}

/// Opaque handle behind `DcXrefRipResult*`.
pub struct DcXrefRipResult {
    hits: Vec<DcXrefRipHit>,
}

/// Opaque handle behind `DcXrefInstResult*`.
pub struct DcXrefInstResult {
    hits: Vec<DcXrefInstHit>,
}

// FFI rule 5: handles are shareable across the pipe's worker threads.
const _: () = {
    const fn assert_send_sync<T: Send + Sync>() {}
    assert_send_sync::<DcXrefRipResult>();
    assert_send_sync::<DcXrefInstResult>();
};

/// Allocates a zeroed buffer of `len` bytes, or `None` when the allocation
/// fails (the legacy `std::vector` would have thrown `bad_alloc`).
fn try_buffer(len: usize) -> Option<Vec<u8>> {
    let mut v = Vec::new();
    v.try_reserve_exact(len).ok()?;
    v.resize(len, 0);
    Some(v)
}

fn is_rex(b: u8) -> bool {
    b & 0xF0 == 0x40
}

/// ModRM with mod = 00 and r/m = 101: RIP-relative disp32 follows.
fn is_rip_relative_modrm(m: u8) -> bool {
    m & 0xC7 == 0x05
}

fn is_candidate_opcode(b: u8) -> bool {
    matches!(b, 0x8B | 0x8D | 0x89)
}

fn read_i32_le(b: &[u8]) -> i32 {
    i32::from_le_bytes([b[0], b[1], b[2], b[3]])
}

/// C++ `ScanRipRelative`. Every section in order, 1 MiB chunks + 16-byte
/// overlap; returns ALL candidates in scan order. `None` only when the chunk
/// buffer cannot be allocated.
pub(crate) fn scan_rip(reader: &Reader, sections: &[DcXrefSection], target: usize) -> Option<Vec<DcXrefRipHit>> {
    let mut out = Vec::new();
    // Allocated up front, like the legacy vector (even with no sections).
    let mut buf = try_buffer(CHUNK_SIZE + OVERLAP)?;

    for sec in sections {
        let mut failed_pages = 0usize;
        reader.scan_readable_spans(
            sec.va,
            sec.size,
            CHUNK_SIZE,
            OVERLAP,
            &mut buf,
            &mut failed_pages,
            |span_va, span, owned_len| {
                let read_want = span.len();
                // Candidates start only in the owned part; a 7-byte read must fit.
                if read_want < 7 {
                    return true;
                }
                let scan_end = owned_len.min(read_want - 7 + 1);

                for p in 0..scan_end {
                    let b0 = span[p];

                    // 7-byte REX form first, so the 6-byte form can skip the
                    // duplicate one byte later.
                    if is_rex(b0) {
                        let b1 = span[p + 1];
                        if is_candidate_opcode(b1) && is_rip_relative_modrm(span[p + 2]) {
                            let disp = read_i32_le(&span[p + 3..p + 7]);
                            let start_ip = span_va.wrapping_add(p);
                            let end_ip = start_ip.wrapping_add(7);
                            let hit_target = end_ip.wrapping_add(disp as isize as usize);
                            if hit_target == target {
                                out.push(DcXrefRipHit { ip: start_ip, opcode: b1 });
                                continue;
                            }
                        }
                    }

                    // 6-byte form, skipped right after a REX byte (that would be
                    // the 7-byte form starting one byte earlier).
                    if p > 0 && is_rex(span[p - 1]) {
                        continue;
                    }

                    if is_candidate_opcode(b0) && is_rip_relative_modrm(span[p + 1]) {
                        let disp = read_i32_le(&span[p + 2..p + 6]);
                        let start_ip = span_va.wrapping_add(p);
                        let end_ip = start_ip.wrapping_add(6);
                        let hit_target = end_ip.wrapping_add(disp as isize as usize);
                        if hit_target == target {
                            out.push(DcXrefRipHit { ip: start_ip, opcode: b0 });
                        }
                    }
                }
                true
            },
        );
    }
    Some(out)
}

/// C++ `ScanInstanceAccesses`. Linear-decodes each range (stops at the first
/// 0xCC or decode failure; an unreadable range is skipped) and returns every
/// matching operand in order. `None` only when the read buffer cannot be
/// allocated.
pub(crate) fn scan_inst(reader: &Reader, ranges: &[DcXrefRange], field_offset: usize) -> Option<Vec<DcXrefInstHit>> {
    let mut out = Vec::new();
    let mut dec = InsnDecoder::new();

    let max_len = ranges.iter().map(|m| m.len).max().unwrap_or(0);
    let mut buf = try_buffer(max_len)?;

    for (mi, m) in ranges.iter().enumerate() {
        let method_start = m.start;
        let range_len = m.len;
        if range_len == 0 {
            continue;
        }
        if !reader.read(method_start, &mut buf[..range_len]) {
            // Unreadable range: skip this method.
            continue;
        }
        let body = &buf[..range_len];

        // int3 padding or a decode failure (clamped tail) ends the method.
        let mut consumed = 0usize;
        while consumed < range_len {
            if body[consumed] == 0xCC {
                break;
            }
            let window = &body[consumed..range_len.min(consumed + DECODE_WINDOW)];
            let Some((len, facts)) = dec.decode_mem(window) else { break };
            let insn_ip = method_start.wrapping_add(consumed);

            for op in facts.operands() {
                if !op.has_disp || op.has_index {
                    continue;
                }
                // static_cast<std::size_t>(int64): truncates on 32-bit, like C++.
                if op.disp as usize != field_offset {
                    continue;
                }
                match op.base {
                    Reg::None => continue,
                    Reg::R(Register::RIP | Register::RSP | Register::RBP) => continue,
                    _ => {}
                }
                let write = if facts.lea {
                    false
                } else {
                    match op.act {
                        MemAct::Write => true,
                        MemAct::Read => false,
                        MemAct::None => continue,
                    }
                };
                out.push(DcXrefInstHit { method: mi, ip: insn_ip, write: u8::from(write) });
            }

            consumed += len;
        }
    }
    Some(out)
}

/// Borrows `(ptr, n)` as a slice; NULL or `n == 0` is the empty slice.
///
/// # Safety
/// A non-null `ptr` must point to `n` readable, initialised `T`.
unsafe fn input_slice<'a, T>(ptr: *const T, n: usize) -> &'a [T] {
    if ptr.is_null() || n == 0 {
        &[]
    } else {
        // SAFETY: caller contract.
        unsafe { std::slice::from_raw_parts(ptr, n) }
    }
}

// --- C ABI -------------------------------------------------------------------

/// Runs `scan_rip` over `sections[0..n)` (C++ caller's own array). Returns a
/// handle, or NULL on an internal panic, a NULL `read`, or when the 1 MiB
/// chunk buffer cannot be allocated; free it with
/// [`dc_xref_rip_result_free`].
///
/// # Safety
/// `read` / `read_ctx` must honour the `dc_read_fn` contract for the duration
/// of the call; `sections` must be NULL or point to `n` readable entries.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_xref_rip_scan(
    sections: *const DcXrefSection,
    n: usize,
    target: usize,
    read: Option<ReadFn>,
    read_ctx: *mut c_void,
) -> *mut DcXrefRipResult {
    guard("dc_xref_rip_scan", std::ptr::null_mut(), || {
        let Some(read) = read else { return std::ptr::null_mut() };
        // SAFETY: caller contract on `sections`.
        let sections = unsafe { input_slice(sections, n) };
        // SAFETY: caller contract on `read` / `read_ctx`.
        let reader = unsafe { Reader::new(read, read_ctx) };
        match scan_rip(&reader, sections, target) {
            Some(hits) => Box::into_raw(Box::new(DcXrefRipResult { hits })),
            None => std::ptr::null_mut(),
        }
    })
}

/// Number of candidates (0 for NULL).
///
/// # Safety
/// `r` must be NULL or a live handle from [`dc_xref_rip_scan`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_xref_rip_result_len(r: *const DcXrefRipResult) -> usize {
    guard("dc_xref_rip_result_len", 0, || {
        // SAFETY: caller contract.
        unsafe { r.as_ref() }.map_or(0, |r| r.hits.len())
    })
}

/// Copies candidate `i` into `*out`; returns 1, or 0 (out untouched) for a
/// NULL handle / NULL `out` / `i` out of range.
///
/// # Safety
/// `r` must be NULL or a live handle; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_xref_rip_result_get(r: *const DcXrefRipResult, i: usize, out: *mut DcXrefRipHit) -> i32 {
    guard("dc_xref_rip_result_get", 0, || {
        // SAFETY: caller contract.
        let (Some(r), false) = (unsafe { r.as_ref() }, out.is_null()) else { return 0 };
        match r.hits.get(i) {
            Some(h) => {
                // SAFETY: out is non-null and writable (caller contract).
                unsafe { out.write(*h) };
                1
            }
            None => 0,
        }
    })
}

/// Frees a handle (NULL is a no-op).
///
/// # Safety
/// `r` must be NULL or a live handle that is not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_xref_rip_result_free(r: *mut DcXrefRipResult) {
    guard("dc_xref_rip_result_free", (), || {
        if !r.is_null() {
            // SAFETY: r came from Box::into_raw in dc_xref_rip_scan.
            drop(unsafe { Box::from_raw(r) });
        }
    })
}

/// Runs `scan_inst` over `ranges[0..n)` (C++ caller's own array). Returns a
/// handle, or NULL on an internal panic, a NULL `read`, or when the read
/// buffer (longest range) cannot be allocated; free it with
/// [`dc_xref_inst_result_free`].
///
/// # Safety
/// `read` / `read_ctx` must honour the `dc_read_fn` contract for the duration
/// of the call; `ranges` must be NULL or point to `n` readable entries.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_xref_inst_scan(
    ranges: *const DcXrefRange,
    n: usize,
    field_offset: usize,
    read: Option<ReadFn>,
    read_ctx: *mut c_void,
) -> *mut DcXrefInstResult {
    guard("dc_xref_inst_scan", std::ptr::null_mut(), || {
        let Some(read) = read else { return std::ptr::null_mut() };
        // SAFETY: caller contract on `ranges`.
        let ranges = unsafe { input_slice(ranges, n) };
        // SAFETY: caller contract on `read` / `read_ctx`.
        let reader = unsafe { Reader::new(read, read_ctx) };
        match scan_inst(&reader, ranges, field_offset) {
            Some(hits) => Box::into_raw(Box::new(DcXrefInstResult { hits })),
            None => std::ptr::null_mut(),
        }
    })
}

/// Number of hits (0 for NULL).
///
/// # Safety
/// `r` must be NULL or a live handle from [`dc_xref_inst_scan`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_xref_inst_result_len(r: *const DcXrefInstResult) -> usize {
    guard("dc_xref_inst_result_len", 0, || {
        // SAFETY: caller contract.
        unsafe { r.as_ref() }.map_or(0, |r| r.hits.len())
    })
}

/// Copies hit `i` into `*out`; returns 1, or 0 (out untouched) for a NULL
/// handle / NULL `out` / `i` out of range.
///
/// # Safety
/// `r` must be NULL or a live handle; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_xref_inst_result_get(r: *const DcXrefInstResult, i: usize, out: *mut DcXrefInstHit) -> i32 {
    guard("dc_xref_inst_result_get", 0, || {
        // SAFETY: caller contract.
        let (Some(r), false) = (unsafe { r.as_ref() }, out.is_null()) else { return 0 };
        match r.hits.get(i) {
            Some(h) => {
                // SAFETY: out is non-null and writable (caller contract).
                unsafe { out.write(*h) };
                1
            }
            None => 0,
        }
    })
}

/// Frees a handle (NULL is a no-op).
///
/// # Safety
/// `r` must be NULL or a live handle that is not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_xref_inst_result_free(r: *mut DcXrefInstResult) {
    guard("dc_xref_inst_result_free", (), || {
        if !r.is_null() {
            // SAFETY: r came from Box::into_raw in dc_xref_inst_scan.
            drop(unsafe { Box::from_raw(r) });
        }
    })
}
