//! Unit tests: hand-assembled code in an in-memory "module" with a minimal PE
//! header, read through a fake `dc_read_fn`.

use super::decode::{Cat, InsnDecoder, Mn, Operand, Reg};
use super::*;
use iced_x86::Register;
use std::ffi::c_void;

// --- fake module ---------------------------------------------------------------

/// Image base used by every test (fits a 32-bit usize for the i686 run).
pub(crate) const BASE: usize = 0x1000_0000;
pub(crate) const TEXT_RVA: usize = 0x1000;
pub(crate) const DATA_RVA: usize = 0x400;

pub(crate) struct FakeMem {
    pub base: usize,
    pub bytes: Vec<u8>,
    /// Unreadable [start, end) absolute ranges (an access-violation stand-in).
    pub holes: Vec<(usize, usize)>,
}

/// `dc_read_fn` over a [`FakeMem`]; mirrors `Memory::SafeReadBytes`
/// (NULL address or zero length fail).
pub(crate) unsafe extern "C" fn fake_read(ctx: *mut c_void, addr: usize, dst: *mut u8, len: usize) -> i32 {
    // SAFETY: tests pass a live FakeMem.
    let m = unsafe { &*(ctx as *const FakeMem) };
    if addr == 0 || len == 0 || dst.is_null() {
        return 0;
    }
    let Some(end) = addr.checked_add(len) else { return 0 };
    if addr < m.base || end > m.base + m.bytes.len() {
        return 0;
    }
    if m.holes.iter().any(|&(s, e)| addr < e && s < end) {
        return 0;
    }
    let off = addr - m.base;
    // SAFETY: dst has len writable bytes (Reader contract).
    unsafe { std::ptr::copy_nonoverlapping(m.bytes.as_ptr().add(off), dst, len) };
    1
}

pub(crate) struct Section {
    pub rva: usize,
    pub size: usize,
    pub exec: bool,
}

/// Builds DOS + NT64 headers + section table into a zeroed image of `total`
/// bytes.
pub(crate) fn build_image(total: usize, sections: &[Section]) -> Vec<u8> {
    let mut img = vec![0u8; total];
    img[0..2].copy_from_slice(&0x5A4Du16.to_le_bytes());
    let lfanew = 0x80usize;
    img[0x3C..0x40].copy_from_slice(&(lfanew as u32).to_le_bytes());
    img[lfanew..lfanew + 4].copy_from_slice(&0x4550u32.to_le_bytes());
    img[lfanew + 4..lfanew + 6].copy_from_slice(&0x8664u16.to_le_bytes());
    img[lfanew + 6..lfanew + 8].copy_from_slice(&(sections.len() as u16).to_le_bytes());
    let opt = 0xF0usize;
    img[lfanew + 20..lfanew + 22].copy_from_slice(&(opt as u16).to_le_bytes());
    let mut sh = lfanew + 24 + opt;
    for s in sections {
        img[sh + 8..sh + 12].copy_from_slice(&(s.size as u32).to_le_bytes());
        img[sh + 12..sh + 16].copy_from_slice(&(s.rva as u32).to_le_bytes());
        let ch: u32 = if s.exec { 0x6000_0020 } else { 0xC000_0040 };
        img[sh + 36..sh + 40].copy_from_slice(&ch.to_le_bytes());
        sh += 40;
    }
    img
}

/// A one-section module whose .text holds `code` at TEXT_RVA, plus a data
/// page at DATA_RVA for the class-pointer slots.
pub(crate) struct Module {
    pub mem: FakeMem,
}

impl Module {
    pub fn new(code: &[u8]) -> Module {
        let text_size = code.len().max(16);
        let img_len = TEXT_RVA + text_size;
        let mut bytes = build_image(
            img_len,
            &[Section { rva: TEXT_RVA, size: text_size, exec: true }, Section { rva: DATA_RVA, size: 0x400, exec: false }],
        );
        bytes[TEXT_RVA..TEXT_RVA + code.len()].copy_from_slice(code);
        Module { mem: FakeMem { base: BASE, bytes, holes: vec![] } }
    }

    pub fn reader(&self) -> Reader {
        // SAFETY: fake_read honours the contract; self outlives the Reader use.
        unsafe { Reader::new(fake_read, &self.mem as *const FakeMem as *mut c_void) }
    }

    pub fn run(&self, slots: &[usize], sfo: usize, tfo: usize, limit: usize) -> DcChainResult {
        detect(self.mem.base, Some(&self.reader()), slots, sfo, tfo, limit).expect("alloc")
    }
}

/// Slot `n` (8-byte aligned) in the data page.
pub(crate) fn slot(n: usize) -> usize {
    BASE + DATA_RVA + 8 * n
}

/// Assembles code at TEXT_RVA; `hop1` emits a RIP-relative load of a slot.
#[derive(Default)]
pub(crate) struct Asm {
    pub code: Vec<u8>,
}

impl Asm {
    pub fn ip(&self) -> usize {
        BASE + TEXT_RVA + self.code.len()
    }
    pub fn raw(&mut self, b: &[u8]) -> &mut Self {
        self.code.extend_from_slice(b);
        self
    }
    /// `[rex] 8B|8D modrm(reg, rip) disp32` targeting `target`.
    pub fn rip_load(&mut self, rex: Option<u8>, opcode: u8, reg: u8, target: usize) -> &mut Self {
        let len = 6 + usize::from(rex.is_some());
        let disp = target.wrapping_sub(self.ip() + len) as isize as i32;
        if let Some(r) = rex {
            self.code.push(r);
        }
        self.code.push(opcode);
        self.code.push(((reg & 7) << 3) | 0x05);
        self.code.extend_from_slice(&disp.to_le_bytes());
        self
    }
    /// `mov rax, [rip + slot]`
    pub fn hop1(&mut self, target: usize) -> &mut Self {
        self.rip_load(Some(0x48), 0x8B, 0, target)
    }
}

const SFO: usize = 0xB8;

/// `mov rax, [rax + 0xB8]` (hop 2, disp32 form).
const HOP2_RAX: [u8; 7] = [0x48, 0x8B, 0x80, 0xB8, 0x00, 0x00, 0x00];
/// `mov eax, [rax + 0x10]` (hop 3 read).
const HOP3_READ_RAX_10: [u8; 3] = [0x8B, 0x40, 0x10];

fn hits(r: &DcChainResult) -> Vec<(usize, usize, u8)> {
    r.matches.iter().map(|m| (m.hit_ip, m.hop_one_ip, m.is_write)).collect()
}

// --- tests -------------------------------------------------------------------------

#[test]
fn basic_chain_rex_hop_one() {
    let mut a = Asm::default();
    let h1 = a.ip();
    a.hop1(slot(0)).raw(&HOP2_RAX);
    let h3 = a.ip();
    a.raw(&HOP3_READ_RAX_10).raw(&[0xC3]);
    let r = Module::new(&a.code).run(&[slot(0)], SFO, 0x10, 100);
    let (m, st) = r.parts();
    assert_eq!((m.len(), st.valid), (1, 1));
    // The REX form also matches at the 0x8B byte (6-byte form, same target,
    // same hit) and is collapsed by the (target, hit) set: one match, at the
    // REX byte.
    assert_eq!(hits(&r), vec![(h3, h1, 0)]);
    assert_eq!(r.stats.hop_one_candidates, 2);
    assert_eq!(r.stats.chain_broken, 0);
    assert_eq!(r.stats.chains_emitted, 1);
    assert_eq!(r.stats.valid, 1);
    assert_eq!(r.stats.failed_reads, 0);
    assert_eq!(r.stats.bytes_scanned, a.code.len().max(16));
}

#[test]
fn no_rex_hop_one_dest_is_canonicalised() {
    // mov ecx, [rip+slot]; mov rdx, [rcx+0xB8]; mov eax, [rdx+0x20]
    let mut a = Asm::default();
    let h1 = a.ip();
    a.rip_load(None, 0x8B, 1, slot(3)).raw(&[0x48, 0x8B, 0x91, 0xB8, 0, 0, 0]);
    let h3 = a.ip();
    a.raw(&[0x8B, 0x42, 0x20]);
    let r = Module::new(&a.code).run(&[slot(3)], SFO, 0x20, 100);
    assert_eq!(hits(&r), vec![(h3, h1, 0)]);
    assert_eq!(r.stats.hop_one_candidates, 1);
}

#[test]
fn lea_hop_one_and_rex_r_destination() {
    // lea r9, [rip+slot]; mov r10, [r9+0xB8]; mov [r10+8], r11
    let mut a = Asm::default();
    let h1 = a.ip();
    a.rip_load(Some(0x4C), 0x8D, 1, slot(1)).raw(&[0x4D, 0x8B, 0x91, 0xB8, 0, 0, 0]);
    let h3 = a.ip();
    a.raw(&[0x4D, 0x89, 0x5A, 0x08]);
    let r = Module::new(&a.code).run(&[slot(1)], SFO, 8, 100);
    assert_eq!(hits(&r), vec![(h3, h1, 1)]);
}

#[test]
fn offset_zero_without_displacement() {
    // Offset-0 fields have no displacement byte: mov rax,[rax+0xB8]; mov eax,[rax].
    let mut a = Asm::default();
    a.hop1(slot(0)).raw(&HOP2_RAX);
    let h3 = a.ip();
    a.raw(&[0x8B, 0x00]);
    let r = Module::new(&a.code).run(&[slot(0)], SFO, 0, 100);
    assert_eq!(r.matches.len(), 1);
    assert_eq!(r.matches[0].hit_ip, h3);
    // ... and a hop 2 at static_fields offset 0 is not possible because
    // sfo == 0 is an early return (stats untouched).
    let r0 = Module::new(&a.code).run(&[slot(0)], 0, 0, 100);
    assert!(r0.matches.is_empty());
    assert_eq!(r0.stats, DcChainStats::default());
}

#[test]
fn call_clobbers_volatile_but_rbx_survives() {
    // hop1 -> rax; mov rbx, rax; call rel32; mov rax,[rbx+0xB8]; mov eax,[rax+0x10]
    let mut a = Asm::default();
    a.hop1(slot(0)).raw(&[0x48, 0x89, 0xC3]).raw(&[0xE8, 0, 0, 0, 0]).raw(&[0x48, 0x8B, 0x83, 0xB8, 0, 0, 0]);
    let h3 = a.ip();
    a.raw(&HOP3_READ_RAX_10);
    let r = Module::new(&a.code).run(&[slot(0)], SFO, 0x10, 100);
    assert_eq!(r.matches.len(), 1);
    assert_eq!(r.matches[0].hit_ip, h3);

    // Same, but hop 2 dereferences RAX after the call: RAX lost its tag at the
    // CALL, RBX keeps the trace alive until the RET.
    let mut b = Asm::default();
    b.hop1(slot(0)).raw(&[0x48, 0x89, 0xC3]).raw(&[0xE8, 0, 0, 0, 0]).raw(&HOP2_RAX).raw(&HOP3_READ_RAX_10).raw(&[0xC3]);
    let r = Module::new(&b.code).run(&[slot(0)], SFO, 0x10, 100);
    assert!(r.matches.is_empty());
    assert_eq!(r.stats.chain_broken, 2); // the REX and the non-REX candidate
}

#[test]
fn call_alone_kills_a_volatile_only_chain() {
    let mut a = Asm::default();
    a.hop1(slot(0)).raw(&[0xFF, 0xD0]).raw(&HOP2_RAX).raw(&HOP3_READ_RAX_10);
    let r = Module::new(&a.code).run(&[slot(0)], SFO, 0x10, 100);
    assert!(r.matches.is_empty());
}

#[test]
fn reg_reg_mov_propagates_and_write_clobbers() {
    // hop1 -> rax; mov rsi, rax; xor eax, eax; mov rdi,[rsi+0xB8]; mov r8, rdi;
    // xor edi, edi; movss xmm0,[r8+0x14]
    let mut a = Asm::default();
    a.hop1(slot(2))
        .raw(&[0x48, 0x89, 0xC6])
        .raw(&[0x31, 0xC0])
        .raw(&[0x48, 0x8B, 0xBE, 0xB8, 0, 0, 0])
        .raw(&[0x49, 0x89, 0xF8])
        .raw(&[0x31, 0xFF]);
    let h3 = a.ip();
    a.raw(&[0xF3, 0x41, 0x0F, 0x10, 0x40, 0x14]);
    let r = Module::new(&a.code).run(&[slot(2)], SFO, 0x14, 100);
    assert_eq!(r.matches.len(), 1);
    assert_eq!((r.matches[0].hit_ip, r.matches[0].is_write), (h3, 0));

    // Without the copy, `xor eax, eax` kills the only tracked register.
    let mut b = Asm::default();
    b.hop1(slot(2)).raw(&[0x31, 0xC0]).raw(&HOP2_RAX).raw(&HOP3_READ_RAX_10);
    let r = Module::new(&b.code).run(&[slot(2)], SFO, 0x10, 100);
    assert!(r.matches.is_empty());
    assert_eq!(r.stats.chain_broken, 2);
}

#[test]
fn conditional_branch_is_skipped() {
    let mut a = Asm::default();
    a.hop1(slot(0)).raw(&[0x48, 0x85, 0xC0, 0x74, 0x10]).raw(&HOP2_RAX).raw(&[0x0F, 0x85, 1, 0, 0, 0]);
    let h3 = a.ip();
    a.raw(&HOP3_READ_RAX_10);
    let r = Module::new(&a.code).run(&[slot(0)], SFO, 0x10, 100);
    assert_eq!(r.matches.len(), 1);
    assert_eq!(r.matches[0].hit_ip, h3);
}

#[test]
fn ret_and_jmp_terminate() {
    for stop in [&[0xC3u8][..], &[0xEB, 0x00][..], &[0xFF, 0xE0][..], &[0xC2, 0x08, 0x00][..]] {
        let mut a = Asm::default();
        a.hop1(slot(0)).raw(stop).raw(&HOP2_RAX).raw(&HOP3_READ_RAX_10);
        let r = Module::new(&a.code).run(&[slot(0)], SFO, 0x10, 100);
        assert!(r.matches.is_empty(), "{stop:02X?}");
        assert_eq!(r.stats.chain_broken, 2);
    }
    // A RET *at* the hop-3 position is checked after the hop-3 matchers, so a
    // hop 3 right before it still matches.
    let mut a = Asm::default();
    a.hop1(slot(0)).raw(&HOP2_RAX).raw(&HOP3_READ_RAX_10).raw(&[0xC3]);
    assert_eq!(Module::new(&a.code).run(&[slot(0)], SFO, 0x10, 100).matches.len(), 1);
}

#[test]
fn hop_three_forms() {
    // (bytes, tfo, is_write)
    let cases: &[(&[u8], usize, u8)] = &[
        (&[0x48, 0x8B, 0x48, 0x18], 0x18, 0),             // mov rcx,[rax+0x18]
        (&[0x48, 0x8D, 0x48, 0x18], 0x18, 0),             // lea rcx,[rax+0x18]
        (&[0x48, 0x63, 0x48, 0x18], 0x18, 0),             // movsxd rcx,dword [rax+0x18]
        (&[0x0F, 0xB6, 0x48, 0x18], 0x18, 0),             // movzx ecx,byte [rax+0x18]
        (&[0x0F, 0xBF, 0x48, 0x18], 0x18, 0),             // movsx ecx,word [rax+0x18]
        (&[0xF3, 0x0F, 0x10, 0x40, 0x18], 0x18, 0),       // movss xmm0,[rax+0x18]
        (&[0xF2, 0x0F, 0x10, 0x40, 0x18], 0x18, 0),       // movsd xmm0,[rax+0x18]
        (&[0x89, 0x48, 0x18], 0x18, 1),                   // mov [rax+0x18],ecx
        (&[0xC7, 0x40, 0x18, 1, 0, 0, 0], 0x18, 1),       // mov dword [rax+0x18],1
        (&[0xC6, 0x40, 0x18, 1], 0x18, 1),                // mov byte [rax+0x18],1
        (&[0xF3, 0x0F, 0x11, 0x40, 0x18], 0x18, 1),       // movss [rax+0x18],xmm0
        (&[0xF2, 0x0F, 0x11, 0x40, 0x18], 0x18, 1),       // movsd [rax+0x18],xmm0
        (&[0x48, 0x8B, 0x88, 0x00, 0x01, 0, 0], 0x100, 0), // disp32 form
    ];
    for &(bytes, tfo, w) in cases {
        let mut a = Asm::default();
        a.hop1(slot(0)).raw(&HOP2_RAX);
        let h3 = a.ip();
        a.raw(bytes);
        let r = Module::new(&a.code).run(&[slot(0)], SFO, tfo, 100);
        assert_eq!(hits(&r).first().map(|h| (h.0, h.2)), Some((h3, w)), "{bytes:02X?}");
    }
    // Not hop-3 forms: wrong offset, an index register, a non-MOV mnemonic,
    // string MOVSD (hidden operands), VEX vmovss.
    let misses: &[&[u8]] = &[
        &[0x8B, 0x40, 0x1C],
        &[0x8B, 0x44, 0x08, 0x18],
        &[0x03, 0x48, 0x18],
        &[0xA5],
        &[0xC5, 0xFA, 0x10, 0x40, 0x18],
    ];
    for &bytes in misses {
        let mut a = Asm::default();
        a.hop1(slot(0)).raw(&HOP2_RAX).raw(bytes).raw(&[0xC3]);
        let r = Module::new(&a.code).run(&[slot(0)], SFO, 0x18, 100);
        assert!(r.matches.is_empty(), "{bytes:02X?}");
    }
}

#[test]
fn not_a_slot_is_not_a_candidate() {
    let mut a = Asm::default();
    a.hop1(slot(5)).raw(&HOP2_RAX).raw(&HOP3_READ_RAX_10);
    let r = Module::new(&a.code).run(&[slot(4)], SFO, 0x10, 100);
    assert!(r.matches.is_empty());
    assert_eq!(r.stats.hop_one_candidates, 0);
    assert_eq!(r.stats.valid, 1);
}

#[test]
fn same_target_and_hit_is_emitted_once() {
    // Two loads of the SAME slot into RAX. The first chain dies (the second
    // load overwrites RAX); the second one is found twice — at its REX byte
    // and at its 0x8B byte — with the same (target, hit) pair, so it is
    // emitted once, at the REX byte.
    let mut a = Asm::default();
    a.hop1(slot(0));
    let second = a.ip();
    a.hop1(slot(0)).raw(&HOP2_RAX);
    let h3 = a.ip();
    a.raw(&HOP3_READ_RAX_10);
    let r = Module::new(&a.code).run(&[slot(0)], SFO, 0x10, 100);
    assert_eq!(hits(&r), vec![(h3, second, 0)]);
    assert_eq!(r.stats.hop_one_candidates, 4);
    assert_eq!(r.stats.chain_broken, 2);
}

#[test]
fn different_targets_with_one_hit_keep_the_first_after_sort() {
    // Chain A loads slot 0 into RAX, then executes `mov rcx, imm64` whose
    // immediate bytes contain a second hop-1 encoding (`8B 05 disp32`, slot
    // 1, EAX) — chain A decodes it as part of the MOV, chain B starts inside
    // it. Both reach the same hop 3 with different targets: two emitted
    // chains, then sort + unique by hit IP keeps the first (chain A).
    let mut a = Asm::default();
    let ip_a = a.ip();
    a.hop1(slot(0)).raw(&[0x48, 0xB9]);
    let ip_b = a.ip();
    a.rip_load(None, 0x8B, 0, slot(1)).raw(&[0x90, 0x90]).raw(&HOP2_RAX);
    let h3 = a.ip();
    a.raw(&HOP3_READ_RAX_10);
    let r = Module::new(&a.code).run(&[slot(0), slot(1)], SFO, 0x10, 100);
    assert_eq!(hits(&r), vec![(h3, ip_a, 0)]);
    // chains_emitted counts the deduplicated result, like the C++.
    assert_eq!(r.stats.chains_emitted, 1);
    // Only slot 1 requested: chain B alone.
    let r = Module::new(&a.code).run(&[slot(1)], SFO, 0x10, 100);
    assert_eq!(hits(&r), vec![(h3, ip_b, 0)]);
}

#[test]
fn match_limit_truncation() {
    // Three independent chains.
    let mut a = Asm::default();
    let mut expect = vec![];
    for s in 0..3 {
        let h1 = a.ip();
        a.hop1(slot(s)).raw(&HOP2_RAX);
        expect.push((a.ip(), h1, 0u8));
        a.raw(&HOP3_READ_RAX_10).raw(&[0xC3]);
    }
    let m = Module::new(&a.code);
    let slots = [slot(0), slot(1), slot(2)];
    assert_eq!(hits(&m.run(&slots, SFO, 0x10, 100)), expect);
    assert_eq!(hits(&m.run(&slots, SFO, 0x10, 2)), expect[..2].to_vec());
    // Limit 0: the check runs after the push, so one match is kept.
    let r0 = m.run(&slots, SFO, 0x10, 0);
    assert_eq!(hits(&r0), expect[..1].to_vec());
    assert_eq!(r0.stats.valid, 1);
    // Truncation stops the scan: the bytes of the current chunk are still
    // counted, nothing after it.
    assert_eq!(r0.stats.bytes_scanned, a.code.len());
    assert_eq!(r0.stats.hop_one_candidates, 1);
}

#[test]
fn early_returns_leave_stats_invalid() {
    let mut a = Asm::default();
    a.hop1(slot(0)).raw(&HOP2_RAX).raw(&HOP3_READ_RAX_10);
    let m = Module::new(&a.code);
    let rd = m.reader();
    for r in [
        detect(BASE, Some(&rd), &[], SFO, 0x10, 10),
        detect(BASE, Some(&rd), &[slot(0)], 0, 0x10, 10),
        detect(0, Some(&rd), &[slot(0)], SFO, 0x10, 10),
        detect(BASE, None, &[slot(0)], SFO, 0x10, 10),
        detect(BASE + 0x10, Some(&rd), &[slot(0)], SFO, 0x10, 10), // no MZ
    ] {
        let r = r.unwrap();
        assert!(r.matches.is_empty());
        assert_eq!(r.stats, DcChainStats::default());
    }
    // No executable section: valid stays 0 as well.
    let mut img = build_image(0x3000, &[Section { rva: 0x1000, size: 0x100, exec: false }]);
    img[0x1000..0x1000 + a.code.len()].copy_from_slice(&a.code);
    let mem = FakeMem { base: BASE, bytes: img, holes: vec![] };
    // SAFETY: fake_read + live FakeMem.
    let rd = unsafe { Reader::new(fake_read, &mem as *const FakeMem as *mut c_void) };
    let r = detect(BASE, Some(&rd), &[slot(0)], SFO, 0x10, 10).unwrap();
    assert_eq!(r.stats, DcChainStats::default());
}

#[test]
fn unreadable_chunk_counts_failed_read_and_skips_a_page() {
    // A 3-page section whose first page is unreadable: the whole-chunk read
    // fails, pos advances by 4096, the rest is read in one chunk.
    let mut code = vec![0x90u8; 0x3000];
    let mut a = Asm { code: vec![0x90; 0x1800] };
    let h1 = a.ip();
    a.hop1(slot(0)).raw(&HOP2_RAX);
    let h3 = a.ip();
    a.raw(&HOP3_READ_RAX_10);
    code[..a.code.len()].copy_from_slice(&a.code);
    let mut m = Module::new(&code);
    m.mem.holes.push((BASE + TEXT_RVA, BASE + TEXT_RVA + 0x10));
    let r = m.run(&[slot(0)], SFO, 0x10, 100);
    assert_eq!(hits(&r), vec![(h3, h1, 0)]);
    assert_eq!(r.stats.failed_reads, 1);
    assert_eq!(r.stats.bytes_scanned, 0x2000);
}

#[test]
fn unreadable_middle_page_preserves_preceding_and_following_chains() {
    // D-M1: A bad page in the middle of a chunk (page 1 = 0x1000..0x2000) must
    // NOT wipe out readable chains on page 0 before it or page 2 after it.
    let mut code = vec![0x90u8; 0x3000];
    let mut a0 = Asm { code: vec![0x90; 0x100] };
    let h1_before = a0.ip();
    a0.hop1(slot(0)).raw(&HOP2_RAX);
    let h3_before = a0.ip();
    a0.raw(&HOP3_READ_RAX_10);
    code[..a0.code.len()].copy_from_slice(&a0.code);

    let mut a2 = Asm { code: vec![0x90; 0x2100] };
    let h1_after = a2.ip();
    a2.hop1(slot(0)).raw(&HOP2_RAX);
    let h3_after = a2.ip();
    a2.raw(&HOP3_READ_RAX_10);
    code[0x2100..a2.code.len()].copy_from_slice(&a2.code[0x2100..]);

    let mut m = Module::new(&code);
    // Mark middle page (page 1) unreadable.
    m.mem.holes.push((BASE + TEXT_RVA + 0x1500, BASE + TEXT_RVA + 0x1504));
    let r = m.run(&[slot(0)], SFO, 0x10, 100);
    assert_eq!(hits(&r), vec![(h3_before, h1_before, 0), (h3_after, h1_after, 0)]);
    assert_eq!(r.stats.failed_reads, 1);
    assert_eq!(r.stats.bytes_scanned, 0x2000);
}

#[test]
fn candidate_in_chunk_overlap_is_traced_with_context() {
    // A chain whose hop 1 starts a few bytes before the 1 MiB chunk end: the
    // trace reads into the 128-byte overlap; the next chunk does not re-emit
    // it (it starts after the hop 1).
    let mut code = vec![0x90u8; CHUNK_SIZE + 0x100];
    let start = CHUNK_SIZE - 3;
    let mut a = Asm { code: code[..start].to_vec() };
    let h1 = a.ip();
    a.hop1(slot(0)).raw(&HOP2_RAX);
    let h3 = a.ip();
    a.raw(&HOP3_READ_RAX_10);
    code[..a.code.len()].copy_from_slice(&a.code);
    let r = Module::new(&code).run(&[slot(0)], SFO, 0x10, 100);
    assert_eq!(hits(&r), vec![(h3, h1, 0)]);
    // The REX candidate is in chunk 0, its 0x8B twin in chunk 1.
    assert_eq!(r.stats.hop_one_candidates, 2);
}

#[test]
fn ffi_round_trip() {
    let mut a = Asm::default();
    let h1 = a.ip();
    a.hop1(slot(0)).raw(&HOP2_RAX);
    let h3 = a.ip();
    a.raw(&[0x89, 0x48, 0x10]);
    let m = Module::new(&a.code);
    let slots = [slot(0)];
    // SAFETY: valid fake reader / ctx / slot array; handle freed below.
    unsafe {
        let r = dc_chain_detect(BASE, Some(fake_read), &m.mem as *const FakeMem as *mut c_void, slots.as_ptr(), 1, SFO, 0x10, 10);
        assert!(!r.is_null());
        assert_eq!(dc_chain_result_len(r), 1);
        let mut out = DcChainMatch::default();
        assert_eq!(dc_chain_result_get(r, 0, &mut out), 1);
        assert_eq!(out, DcChainMatch { hit_ip: h3, hop_one_ip: h1, is_write: 1 });
        assert_eq!(dc_chain_result_get(r, 1, &mut out), 0);
        assert_eq!(dc_chain_result_get(r, 0, std::ptr::null_mut()), 0);
        let mut st = DcChainStats::default();
        dc_chain_result_stats(r, &mut st);
        assert_eq!((st.valid, st.chains_emitted, st.hop_one_candidates), (1, 1, 2));
        dc_chain_result_free(r);

        // NULL handles.
        assert_eq!(dc_chain_result_len(std::ptr::null()), 0);
        assert_eq!(dc_chain_result_get(std::ptr::null(), 0, &mut out), 0);
        let mut st = DcChainStats { valid: 7, ..Default::default() };
        dc_chain_result_stats(std::ptr::null(), &mut st);
        assert_eq!(st, DcChainStats::default());
        dc_chain_result_free(std::ptr::null_mut());

        // NULL slots / no reader: an empty, "not scanned" result.
        let r = dc_chain_detect(BASE, None, std::ptr::null_mut(), std::ptr::null(), 5, SFO, 0x10, 10);
        assert_eq!(dc_chain_result_len(r), 0);
        dc_chain_result_stats(r, &mut st);
        assert_eq!(st.valid, 0);
        assert_eq!(dc_chain_result_cancelled(r), 0);
        assert_eq!(dc_chain_result_truncated(r), 0);
        dc_chain_result_free(r);

        // D-L6: cooperative cancellation stops a multi-chunk scan early and sets cancelled = 1.
        assert_eq!(dc_chain_result_cancelled(std::ptr::null()), 0);
        assert_eq!(dc_chain_result_truncated(std::ptr::null()), 0);
        unsafe extern "C" fn cancel_after_first(ctx: *mut c_void) -> i32 {
            // SAFETY: points to a local AtomicUsize below.
            let n = unsafe { &*(ctx as *const std::sync::atomic::AtomicUsize) };
            i32::from(n.fetch_add(1, std::sync::atomic::Ordering::Relaxed) >= 1)
        }
        let mut two_chunks = vec![0x90u8; CHUNK_SIZE + 0x200];
        let mut a2 = Asm { code: two_chunks[..CHUNK_SIZE + 0x40].to_vec() };
        a2.hop1(slot(0)).raw(&HOP2_RAX).raw(&HOP3_READ_RAX_10);
        two_chunks[..a2.code.len()].copy_from_slice(&a2.code);
        let m2 = Module::new(&two_chunks);
        let polls = std::sync::atomic::AtomicUsize::new(0);
        let r = dc_chain_detect_cancellable(
            BASE,
            Some(fake_read),
            &m2.mem as *const FakeMem as *mut c_void,
            slots.as_ptr(),
            1,
            SFO,
            0x10,
            10,
            Some(cancel_after_first),
            &polls as *const std::sync::atomic::AtomicUsize as *mut c_void,
        );
        assert!(!r.is_null());
        assert_eq!(dc_chain_result_cancelled(r), 1);
        // Chunk 1 (holding the chain) must never have been scanned.
        assert_eq!(dc_chain_result_len(r), 0);
        dc_chain_result_free(r);
    }
}

// --- decoder mapping (Zydis semantics) ---------------------------------------

/// Behavioural projection of one decoded instruction — exactly the facts the
/// legacy trace consults. Shared with the scratch differential harness.
pub(crate) fn project(d: &mut InsnDecoder, bytes: &[u8]) -> String {
    fn name(r: Reg) -> String {
        match r {
            Reg::None => "none".into(),
            Reg::R(r) => format!("{r:?}").to_lowercase(),
            Reg::Egpr(n) => format!("r{n}"),
        }
    }
    fn trackable(r: Reg) -> bool {
        match r {
            Reg::None => false,
            Reg::R(r) => r.is_gpr64() || r.is_segment_register() || r.is_cr() || r.is_dr(),
            Reg::Egpr(_) => true,
        }
    }
    fn mem_str(base: Reg, disp: i64) -> String {
        let s = name(base);
        if base == Reg::None || base == Reg::R(Register::RIP) {
            return s;
        }
        format!("{s}@{:x}", disp as usize)
    }
    let Some(i) = d.decode(bytes) else { return "X".into() };
    let mut s = format!("L{}", i.len);
    if i.mn != Mn::Other
        && i.vis >= 2
        && let (Operand::Reg(_), Operand::Mem { base, has_index: false, disp }) = (i.ops[0], i.ops[1])
    {
        s += &format!(" RD{}", mem_str(base, disp));
    }
    if matches!(i.mn, Mn::Mov | Mn::Movss | Mn::Movsd)
        && i.vis >= 2
        && let Operand::Mem { base, has_index: false, disp } = i.ops[0]
        && matches!(i.ops[1], Operand::Reg(_) | Operand::Imm)
    {
        s += &format!(" WR{}", mem_str(base, disp));
    }
    if i.cat == Cat::Ret || i.cat == Cat::UncondBr {
        s += " T";
        return s;
    }
    if i.mn == Mn::Mov
        && i.vis >= 2
        && let (Operand::Reg(d0), Operand::Mem { base, has_index: false, disp }) = (i.ops[0], i.ops[1])
    {
        s += &format!(" H2{}>{}", mem_str(base, disp), name(d0));
    }
    if i.cat == Cat::Call {
        s += " C";
    } else if i.cat == Cat::CondBr {
        s += " B";
    } else if let (true, Operand::Reg(a), Operand::Reg(b)) = (i.mn == Mn::Mov && i.vis >= 2, i.ops[0], i.ops[1]) {
        s += &format!(" MV{}<{}", name(a), name(b));
    } else {
        let mut w: Vec<String> = i.writes[..i.n_writes].iter().filter(|r| trackable(**r)).map(|r| name(*r)).collect();
        w.sort();
        w.dedup();
        if !w.is_empty() {
            s += " W";
            for x in w {
                s += ":";
                s += &x;
            }
        }
    }
    s
}

/// Golden vectors: (bytes, projection the vendored Zydis 5.0 produces), one
/// per Zydis/iced divergence handled in `decode` plus common forms.
#[test]
fn zydis_golden_projections() {
    let cases: &[(&str, &str)] = &[
        ("48 8B 80 B8 00 00 00", "L7 RDrax@b8 H2rax@b8>rax W:rax"),
        ("8B 40 10", "L3 RDrax@10 H2rax@10>rax W:rax"),
        ("8B 00", "L2 RDrax@0 H2rax@0>rax W:rax"),
        ("67 8B 40 F0", "L4 RDrax@fffffffffffffff0 H2rax@fffffffffffffff0>rax W:rax"),
        ("48 89 C3", "L3 MVrbx<rax"),
        ("8E D8", "L2 MVds<rax"),
        ("C7 40 10 01 00 00 00", "L7 WRrax@10"),
        ("F3 0F 10 40 18", "L5 RDrax@18"),
        ("F2 0F 11 40 18", "L5 WRrax@18"),
        ("A5", "L1"),
        ("AC", "L1"),
        ("31 C0", "L2 W:rax"),
        ("E8 00 00 00 00", "L5 C"),
        ("FF D0", "L2 C"),
        ("74 05", "L2 B"),
        ("E3 05", "L2 B"),
        ("E2 05", "L2 B"),
        ("C7 F8 00 00 00 00", "L6 B"),
        ("0F 01 D5", "L3 B"),
        ("C6 F8 01", "L3 T"),
        ("C3", "L1 T"),
        ("CB", "L1 T"),
        ("48 CF", "L2 T"),
        ("EB 00", "L2 T"),
        ("FF E0", "L2 T"),
        ("0F 05", "L2"),
        ("CC", "L1"),
        // REX2 (APX)
        ("D5 18 58", "L3 W:r16"),
        ("D5 40 63 37", "L4 RDrdi@0 W:r22"),
        ("D5 59 90", "L3 W:r24:rax"),
        ("D5 4A 90", "L3"),
        ("D5 00 A1 11 22 33 44 55 66 77 88", "L11 T"),
        ("66 D5 00 A1 11 22 33 44 55 66 77 88", "X"),
        ("D5 00 74 05", "X"),
        ("48 D5 00 8B 00", "X"),
        ("D5 AD 20 40", "L4 MVr8<cr8"),
        ("D5 C8 22 D5", "X"),
        // EVEX with APX register extension bits
        ("3E 62 C1 7E 08 2C 9F A9 DF F1 D4", "L11 W:r19"),
        ("62 E1 BB 79 5E CB", "X"),
        // padlock / system specials
        ("F3 0F A6 C0", "L4"),
        ("F3 0F A6 C9", "X"),
        ("0F A7 C1", "X"),
        ("66 0F AE F9", "L4"),
        ("F3 66 0F AE 20", "L5"),
        ("F2 0F 01 FD", "L4"),
        ("F3 0F 01 FF", "L4 W:rax"),
        ("F0 0F A7 C0", "X"),
        // KNC encodings Zydis decodes from VEX
        ("C5 40 95 C0", "L4 W:r8"),
        ("C5 40 84 00 00 00 00", "L7 B"),
        ("C4 E0 40 74 05", "L5 B"),
        ("C5 7A B8 C0", "L4 W:r8"),
        // VEX CMPccXADD is rejected by the vendored Zydis
        ("C4 E2 F9 E6 5F 64", "X"),
        // XOP LWP: register operand written
        ("8F EA 50 12 C0 45 E9 6B 65", "L9 W:rbp"),
    ];
    let mut d = InsnDecoder::new();
    for (hex, want) in cases {
        let b: Vec<u8> = hex.split_whitespace().map(|h| u8::from_str_radix(h, 16).unwrap()).collect();
        let got = project(&mut d, &b);
        if cfg!(target_pointer_width = "32") && want.contains("@fffffffffffffff0") {
            // size_t truncation of the displacement on 32-bit hosts.
            assert_eq!(got, want.replace("@fffffffffffffff0", "@fffffff0"), "{hex}");
        } else {
            assert_eq!(&got, want, "{hex}");
        }
    }
}

#[test]
fn msvc_sort_small_ranges_are_stable_and_large_ones_sorted() {
    #[derive(Clone, Copy, Debug, PartialEq)]
    struct E {
        k: u32,
        tag: u32,
    }
    let mut x: u64 = 0x1234_5678;
    let mut rnd = || {
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        x
    };
    for n in [0usize, 1, 2, 5, 31, 32, 33, 41, 100, 1000] {
        let mut v: Vec<E> = (0..n).map(|i| E { k: (rnd() % 7) as u32, tag: i as u32 }).collect();
        msvc_sort::sort_by(&mut v, |a, b| a.k < b.k);
        assert!(v.windows(2).all(|w| w[0].k <= w[1].k));
        if n <= 32 {
            assert!(v.windows(2).all(|w| w[0].k < w[1].k || w[0].tag < w[1].tag), "insertion sort is stable");
        }
        // The (key, tag) helper used by the scratch differential harness
        // sorts identically.
        let mut pairs: Vec<(u64, u32)> = v.iter().map(|e| (u64::from(e.k), e.tag)).collect();
        let before = pairs.clone();
        msvc_sort_for_tests(&mut pairs);
        assert!(pairs.windows(2).all(|w| w[0].0 <= w[1].0));
        assert_eq!(pairs.len(), before.len());
        let mut tags: Vec<u32> = v.iter().map(|e| e.tag).collect();
        tags.sort();
        assert_eq!(tags, (0..n as u32).collect::<Vec<_>>());
    }
}
