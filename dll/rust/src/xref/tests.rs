//! Unit tests: hand-assembled code in an in-memory "module", read through
//! the chain detector's fake `dc_read_fn` (holes = unreadable ranges). The
//! expected decoder facts of every instruction below were checked against
//! Zydis (tools/zydis-oracle, see `chain::decode`).

use super::*;
use crate::chain::tests::{FakeMem, fake_read};

/// Fits a 32-bit usize for the i686 run.
const BASE: usize = 0x2000_0000;

fn mem(bytes: Vec<u8>, holes: Vec<(usize, usize)>) -> FakeMem {
    FakeMem { base: BASE, bytes, holes }
}

fn reader(m: &FakeMem) -> Reader {
    // SAFETY: fake_read honours the dc_read_fn contract; `m` outlives the reader.
    unsafe { Reader::new(fake_read, m as *const FakeMem as *mut c_void) }
}

fn hex(s: &str) -> Vec<u8> {
    s.split_whitespace().map(|h| u8::from_str_radix(h, 16).unwrap()).collect()
}

/// Writes a RIP-relative instruction at `at` (`head` = REX? + opcode +
/// ModRM) whose target is `target`.
fn put_rip(buf: &mut [u8], at: usize, head: &[u8], target: usize) {
    let len = head.len() + 4;
    buf[at..at + head.len()].copy_from_slice(head);
    let next = BASE + at + len;
    let disp = target.wrapping_sub(next) as isize as i32;
    buf[at + head.len()..at + len].copy_from_slice(&disp.to_le_bytes());
}

fn rip(m: &FakeMem, secs: &[DcXrefSection], target: usize) -> Vec<(usize, u8)> {
    scan_rip(&reader(m), secs, target).unwrap().into_iter().map(|h| (h.ip - BASE, h.opcode)).collect()
}

fn inst(m: &FakeMem, ranges: &[(usize, usize)], off: usize) -> Vec<(usize, usize, bool)> {
    let r: Vec<DcXrefRange> = ranges.iter().map(|&(s, l)| DcXrefRange { start: BASE + s, len: l }).collect();
    scan_inst(&reader(m), &r, off).unwrap().into_iter().map(|h| (h.method, h.ip - BASE, h.write != 0)).collect()
}

// --- static (RIP-relative) path ------------------------------------------------

#[test]
fn rip_finds_rex_and_plain_forms_in_order() {
    let t = BASE + 0x9000;
    let mut b = vec![0x90u8; 0x200];
    put_rip(&mut b, 0x10, &[0x48, 0x8B, 0x05], t); // mov rax, [rip+x]
    put_rip(&mut b, 0x20, &[0x8D, 0x0D], t); // lea ecx, [rip+x]
    put_rip(&mut b, 0x30, &[0x4C, 0x89, 0x3D], t); // mov [rip+x], r15
    put_rip(&mut b, 0x40, &[0x8B, 0x05], t + 1); // other target
    put_rip(&mut b, 0x50, &[0x8A, 0x05], t); // 8A is not a candidate opcode
    let m = mem(b, vec![]);
    let s = [DcXrefSection { va: BASE, size: 0x200 }];
    assert_eq!(rip(&m, &s, t), vec![(0x10, 0x8B), (0x20, 0x8D), (0x30, 0x89)]);
    assert_eq!(rip(&m, &s, t + 1), vec![(0x40, 0x8B)]);
    assert!(rip(&m, &s, 0x1234).is_empty());
}

#[test]
fn rip_rex_hit_is_not_reported_again_as_six_byte_form() {
    // 48 8B 05 d32: at p the 7-byte form hits; at p+1 the 6-byte reading
    // of the same bytes (same end, same target) is skipped because buf[p]
    // is a REX byte — the rule only ever removes this duplicate.
    let t = BASE + 0x40;
    let mut b = vec![0x90u8; 0x80];
    put_rip(&mut b, 0x08, &[0x48, 0x8B, 0x05], t);
    let m = mem(b, vec![]);
    assert_eq!(rip(&m, &[DcXrefSection { va: BASE, size: 0x80 }], t), vec![(0x08, 0x8B)]);
}

#[test]
fn rip_chunk_boundary_and_overlap() {
    // Two chunks: candidates starting in the last 6 bytes of chunk 1 are
    // found once (through the 16-byte overlap), never twice.
    let size = CHUNK_SIZE + 0x100;
    let t = BASE + 0x50;
    let mut b = vec![0x90u8; size];
    put_rip(&mut b, CHUNK_SIZE - 10, &[0x48, 0x8B, 0x05], t);
    put_rip(&mut b, CHUNK_SIZE - 3, &[0x8B, 0x05], t); // straddles the boundary
    put_rip(&mut b, CHUNK_SIZE + 3, &[0x89, 0x05], t);
    let m = mem(b, vec![]);
    let s = [DcXrefSection { va: BASE, size }];
    assert_eq!(rip(&m, &s, t), vec![(CHUNK_SIZE - 10, 0x8B), (CHUNK_SIZE - 3, 0x8B), (CHUNK_SIZE + 3, 0x89)]);
}

#[test]
fn rip_section_end_cuts_the_tail() {
    // Legacy quirk kept: a candidate must start at least 7 bytes before the
    // end of the bytes read, even for the 6-byte form.
    let t = BASE + 0x10;
    let mut b = vec![0x90u8; 0x40];
    put_rip(&mut b, 0x19, &[0x8B, 0x05], t); // ends at 0x1F: found
    put_rip(&mut b, 0x3A, &[0x8B, 0x05], t); // ends exactly at 0x40: not scanned
    let m = mem(b, vec![]);
    assert_eq!(rip(&m, &[DcXrefSection { va: BASE, size: 0x20 }], t), vec![(0x19, 0x8B)]);
    assert!(rip(&m, &[DcXrefSection { va: BASE + 0x20, size: 0x20 }], t).is_empty());
    let both = [DcXrefSection { va: BASE + 0x20, size: 0x20 }, DcXrefSection { va: BASE, size: 0x40 }];
    assert_eq!(rip(&m, &both, t), vec![(0x19, 0x8B)]);
}

#[test]
fn rip_unreadable_chunk_skips_4k() {
    // Hole in chunk 1: the whole first read fails, pos advances by 4 KiB at
    // a time and each retry reads [pos, pos + 1 MiB + 16) — still covering
    // the hole until pos passes it.
    let size = 0x6000;
    let t = BASE + 0x20;
    let mut b = vec![0x90u8; size];
    put_rip(&mut b, 0x0800, &[0x8B, 0x05], t); // in the first 4 KiB: lost
    put_rip(&mut b, 0x2800, &[0x8B, 0x05], t); // after the hole: found
    put_rip(&mut b, 0x5800, &[0x48, 0x8D, 0x05], t);
    let hole = (BASE + 0x1800, BASE + 0x1801);
    let m = mem(b, vec![hole]);
    // pos 0 fails, 0x1000 fails (covers 0x1800), 0x2000 reads to the end.
    assert_eq!(rip(&m, &[DcXrefSection { va: BASE, size }], t), vec![(0x2800, 0x8B), (0x5800, 0x8D)]);
}

#[test]
fn rip_tiny_and_empty_sections() {
    let t = BASE + 0x100;
    let mut b = vec![0x90u8; 0x40];
    put_rip(&mut b, 0, &[0x8B, 0x05], t);
    let m = mem(b, vec![]);
    // Fewer than 7 bytes read: nothing is scanned, even a complete 6-byte form.
    for size in 0..7 {
        assert!(rip(&m, &[DcXrefSection { va: BASE, size }], t).is_empty(), "size {size}");
    }
    assert_eq!(rip(&m, &[DcXrefSection { va: BASE, size: 7 }], t), vec![(0, 0x8B)]);
    // Unmapped section: every read fails, nothing found, terminates.
    assert!(rip(&m, &[DcXrefSection { va: 0x10, size: 0x3000 }], t).is_empty());
    assert!(rip(&m, &[], t).is_empty());
}

#[test]
fn rip_sections_scanned_in_order() {
    let t = BASE + 0x500;
    let mut b = vec![0x90u8; 0x400];
    put_rip(&mut b, 0x300, &[0x8B, 0x05], t);
    put_rip(&mut b, 0x100, &[0x89, 0x05], t);
    let m = mem(b, vec![]);
    let s = [DcXrefSection { va: BASE + 0x200, size: 0x200 }, DcXrefSection { va: BASE, size: 0x200 }];
    assert_eq!(rip(&m, &s, t), vec![(0x300, 0x8B), (0x100, 0x89)]);
}

// --- instance path ---------------------------------------------------------------

/// One method body built from hex instruction strings.
fn body(insns: &[&str]) -> Vec<u8> {
    insns.iter().flat_map(|s| hex(s)).collect()
}

#[test]
fn inst_reads_writes_and_lea() {
    let code = body(&[
        "48 8B 41 10",          // 0x00 mov rax, [rcx+0x10]      read
        "48 89 41 10",          // 0x04 mov [rcx+0x10], rax      write
        "48 8D 41 10",          // 0x08 lea rax, [rcx+0x10]      read (LEA)
        "C7 41 10 01 00 00 00", // 0x0C mov dword [rcx+0x10], 1  write
        "48 8B 81 10 00 00 00", // 0x13 disp32 form              read
        "F0 0F C1 41 10",       // 0x1A lock xadd [rcx+0x10]     write (read-write)
        "0F 18 41 10",          // 0x1F prefetchnta              read (Zydis READ)
        "0F 1F 40 10",          // 0x23 nop dword [rax+0x10]     read (Zydis READ)
        "62 F1 7E 48 6F 41 01", // 0x27 vmovdqu32 zmm0, [rcx+0x40] (disp8*64), other offset
        "62 F4 7C 08 01 41 10", // 0x2E APX EVEX add [rcx+0x10], eax  write
        "D5 19 8B 41 10",       // 0x35 REX2 mov eax, [r25+0x10] read
        "67 8B 41 10",          // 0x3A mov eax, [ecx+0x10]      read
        "C3",
    ]);
    let n = code.len();
    let m = mem(code, vec![]);
    let got = inst(&m, &[(0, n)], 0x10);
    let want = vec![
        (0, 0x00, false),
        (0, 0x04, true),
        (0, 0x08, false),
        (0, 0x0C, true),
        (0, 0x13, false),
        (0, 0x1A, true),
        (0, 0x1F, false),
        (0, 0x23, false),
        (0, 0x2E, true),
        (0, 0x35, false),
        (0, 0x3A, false),
    ];
    assert_eq!(got, want);
    assert_eq!(inst(&m, &[(0, n)], 0x40), vec![(0, 0x27, false)]);
}

#[test]
fn inst_excluded_operands() {
    let code = body(&[
        "48 8B 44 24 10",                // [rsp+0x10]
        "48 8B 45 10",                   // [rbp+0x10]
        "48 8B 05 10 00 00 00",          // [rip+0x10]
        "48 8B 44 11 10",                // [rcx+rdx+0x10] (index)
        "48 A1 10 00 00 00 00 00 00 00", // mov rax, [moffs64] (no base)
        "F3 0F 1B 41 10",                // bndmk: Zydis gives the AGEN operand no action
        "48 8B 01",                      // [rcx] (no displacement)
        "C3",
    ]);
    let n = code.len();
    let m = mem(code, vec![]);
    assert!(inst(&m, &[(0, n)], 0x10).is_empty());
    // A displacement byte of 0 is "present"; no displacement is not.
    let m0 = mem(body(&["48 8B 41 00", "48 8B 01"]), vec![]);
    assert_eq!(inst(&m0, &[(0, 7)], 0), vec![(0, 0, false)]);
}

#[test]
fn inst_negative_displacement_matches_wrapped_offset() {
    let m = mem(body(&["48 8B 41 F8", "48 89 81 00 FF FF FF"]), vec![]);
    assert_eq!(inst(&m, &[(0, 11)], (-8isize) as usize), vec![(0, 0, false)]);
    assert_eq!(inst(&m, &[(0, 11)], (-0x100isize) as usize), vec![(0, 4, true)]);
}

#[test]
fn inst_stops_at_int3_and_decode_failure() {
    // int3 ends the method even though a match follows.
    let a = body(&["48 8B 41 10", "CC", "48 8B 41 10"]);
    // 0F 04 does not decode -> stop.
    let b = body(&["48 8B 41 10", "0F 04", "48 8B 41 10"]);
    // A truncated tail (range cut inside the instruction) fails to decode.
    let c = body(&["48 8B 41 10", "48 8B 41 10"]);
    for code in [a, b] {
        let n = code.len();
        let m = mem(code, vec![]);
        assert_eq!(inst(&m, &[(0, n)], 0x10), vec![(0, 0, false)]);
    }
    let m = mem(c, vec![]);
    assert_eq!(inst(&m, &[(0, 7)], 0x10), vec![(0, 0, false)]);
    assert_eq!(inst(&m, &[(0, 8)], 0x10), vec![(0, 0, false), (0, 4, false)]);
}

#[test]
fn inst_methods_indexing_unreadable_and_empty_ranges() {
    let code = body(&["48 8B 41 10", "C3", "48 89 42 10", "C3", "8B 43 10", "C3"]);
    let n = code.len();
    let hole = (BASE + 5, BASE + 6);
    let m = mem(code, vec![hole]);
    // Range 1 covers the hole: skipped. Range 2 has len 0: skipped. Range 3
    // starts past the image: unreadable. Indices are positions in the input.
    let got = inst(&m, &[(0, 5), (5, 5), (10, 0), (n + 0x100, 4), (10, 4), (0, 4)], 0x10);
    assert_eq!(got, vec![(0, 0, false), (4, 10, false), (5, 0, false)]);
}

#[test]
fn inst_long_range_is_decoded_through_a_16_byte_window() {
    // 2000 instructions in one range (well past any decoder window).
    let mut code = Vec::new();
    for i in 0..2000u32 {
        if i % 3 == 0 {
            code.extend(hex("48 89 41 10"));
        } else {
            code.extend(hex("66 0F 1F 84 00 00 00 00 00")); // nop word [rax+rax+0] (index)
        }
    }
    let n = code.len();
    let m = mem(code, vec![]);
    let got = inst(&m, &[(0, n)], 0x10);
    assert_eq!(got.len(), 667);
    assert!(got.iter().all(|&(mi, _, w)| mi == 0 && w));
}

// --- C ABI -----------------------------------------------------------------------

#[test]
fn ffi_round_trip_and_null_handling() {
    let t = BASE + 0x80;
    let mut b = vec![0x90u8; 0x100];
    put_rip(&mut b, 0x10, &[0x48, 0x8B, 0x05], t);
    b[0x40..0x44].copy_from_slice(&hex("48 89 41 10"));
    let m = mem(b, vec![]);
    let ctx = &m as *const FakeMem as *mut c_void;
    let secs = [DcXrefSection { va: BASE, size: 0x100 }];
    let ranges = [DcXrefRange { start: BASE + 0x40, len: 4 }];
    // SAFETY: valid arrays, live context, handles freed below.
    unsafe {
        let r = dc_xref_rip_scan(secs.as_ptr(), secs.len(), t, Some(fake_read), ctx);
        assert!(!r.is_null());
        assert_eq!(dc_xref_rip_result_len(r), 1);
        let mut h = DcXrefRipHit::default();
        assert_eq!(dc_xref_rip_result_get(r, 0, &mut h), 1);
        assert_eq!(h, DcXrefRipHit { ip: BASE + 0x10, opcode: 0x8B });
        assert_eq!(dc_xref_rip_result_get(r, 1, &mut h), 0);
        assert_eq!(dc_xref_rip_result_get(r, 0, std::ptr::null_mut()), 0);
        dc_xref_rip_result_free(r);

        let r = dc_xref_inst_scan(ranges.as_ptr(), ranges.len(), 0x10, Some(fake_read), ctx);
        assert!(!r.is_null());
        assert_eq!(dc_xref_inst_result_len(r), 1);
        let mut h = DcXrefInstHit::default();
        assert_eq!(dc_xref_inst_result_get(r, 0, &mut h), 1);
        assert_eq!(h, DcXrefInstHit { method: 0, ip: BASE + 0x40, write: 1 });
        assert_eq!(dc_xref_inst_result_get(r, 1, &mut h), 0);
        dc_xref_inst_result_free(r);

        // NULL read -> NULL handle; NULL arrays -> empty results.
        assert!(dc_xref_rip_scan(secs.as_ptr(), 1, t, None, ctx).is_null());
        assert!(dc_xref_inst_scan(ranges.as_ptr(), 1, 0x10, None, ctx).is_null());
        let r = dc_xref_rip_scan(std::ptr::null(), 5, t, Some(fake_read), ctx);
        assert_eq!(dc_xref_rip_result_len(r), 0);
        dc_xref_rip_result_free(r);
        let r = dc_xref_inst_scan(std::ptr::null(), 5, 0x10, Some(fake_read), ctx);
        assert_eq!(dc_xref_inst_result_len(r), 0);
        dc_xref_inst_result_free(r);

        // NULL handles everywhere.
        assert_eq!(dc_xref_rip_result_len(std::ptr::null()), 0);
        assert_eq!(dc_xref_inst_result_len(std::ptr::null()), 0);
        let mut rh = DcXrefRipHit::default();
        let mut ih = DcXrefInstHit::default();
        assert_eq!(dc_xref_rip_result_get(std::ptr::null(), 0, &mut rh), 0);
        assert_eq!(dc_xref_inst_result_get(std::ptr::null(), 0, &mut ih), 0);
        dc_xref_rip_result_free(std::ptr::null_mut());
        dc_xref_inst_result_free(std::ptr::null_mut());
    }
}

#[test]
fn abi_struct_layout() {
    use std::mem::{align_of, size_of};
    let w = size_of::<usize>();
    assert_eq!(size_of::<DcXrefSection>(), 2 * w);
    assert_eq!(size_of::<DcXrefRange>(), 2 * w);
    assert_eq!(size_of::<DcXrefRipHit>(), 2 * w);
    assert_eq!(size_of::<DcXrefInstHit>(), 3 * w);
    assert_eq!(align_of::<DcXrefInstHit>(), w);
}
