//! IL2CPP static-field chain detector (Phase R2) — port of
//! `dll/src/il2cpp/IL2CPPChainDetector.cpp`, with Zydis replaced by iced-x86.
//!
//! Recognises the 2-hop class-pointer indirection IL2CPP AOT code uses when it
//! reads or writes a static field:
//!
//! ```text
//! mov R1, [rip + CLASS_PTR_SLOT]        ; hop 1: load Il2CppClass*
//! mov R2, [R1 + static_fields_offset]   ; hop 2: deref to static_fields
//! mov R3, [R2 + field_offset]           ; hop 3 read   (or lea / movsxd /
//! mov [R2 + field_offset], R3 / imm     ; hop 3 write   movzx / movsx /
//!                                       ;               movss / movsd)
//! ```
//!
//! The scan loop, the hop matchers, the register-tracking rules and every
//! limit are a line-by-line transcription of the C++ (see the comments on
//! each function); the C++ header `IL2CPPChainDetector.h` stays the public
//! API and `IL2CPPChainDetectorRs.cpp` forwards to [`dc_chain_detect`].
//!
//! The one non-mechanical part is instruction decoding. The legacy code asked
//! Zydis a handful of questions per instruction (mnemonic, category, visible
//! operands, which register operands are written). [`decode`] answers exactly
//! those questions with iced-x86 *in Zydis' terms*, including every place
//! where the two decoders disagree — see that module's documentation for the
//! full mapping and how it was verified against the vendored Zydis.
//!
//! Host memory (the PE headers and the code sections of GameAssembly.dll) is
//! only ever read through [`Reader`] (the C++ SEH shim); Rust never
//! dereferences a game address.

mod apx_map4;
pub(crate) mod decode;
mod msvc_sort;
#[cfg(test)]
pub(crate) mod tests;

use std::collections::HashSet;
use std::ffi::c_void;

use crate::ffi::{ReadFn, Reader, guard};
use decode::{Cat, Insn, InsnDecoder, Mn, Operand, Reg};

/// Code is scanned in 1 MiB chunks (bounds the working set on 200 MB images).
const CHUNK_SIZE: usize = 1024 * 1024;
/// Extra bytes read past each chunk so a candidate near the chunk end can
/// still be decoded and traced; candidates are only *started* in the chunk.
const TAIL_OVERLAP: usize = 128;
/// Forward-trace window per hop-1 candidate (instructions).
const MAX_FORWARD_TRACE: usize = 32;

// PE layout constants (winnt.h). Offsets are into IMAGE_DOS_HEADER,
// IMAGE_NT_HEADERS64 and IMAGE_SECTION_HEADER. The legacy code reads the
// 64-bit NT header unconditionally, so this does too.
const DOS_HEADER_SIZE: usize = 64; // sizeof(IMAGE_DOS_HEADER)
const DOS_E_LFANEW: usize = 0x3C;
const DOS_SIGNATURE: u16 = 0x5A4D; // "MZ"
const NT_HEADERS64_SIZE: usize = 264; // sizeof(IMAGE_NT_HEADERS64)
const NT_SIGNATURE: u32 = 0x0000_4550; // "PE\0\0"
const NT_NUMBER_OF_SECTIONS: usize = 4 + 2; // FileHeader.NumberOfSections
const NT_SIZE_OF_OPTIONAL_HEADER: usize = 4 + 16; // FileHeader.SizeOfOptionalHeader
const NT_OPTIONAL_HEADER: usize = 24; // offsetof(IMAGE_NT_HEADERS64, OptionalHeader)
const SECTION_HEADER_SIZE: usize = 40; // sizeof(IMAGE_SECTION_HEADER)
const SECTION_VIRTUAL_SIZE: usize = 8; // Misc.VirtualSize
const SECTION_VIRTUAL_ADDRESS: usize = 12;
const SECTION_CHARACTERISTICS: usize = 36;
const SCN_MEM_EXECUTE: u32 = 0x2000_0000; // IMAGE_SCN_MEM_EXECUTE

/// One chain hit (C layout of `DcChainMatch`).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DcChainMatch {
    pub hit_ip: usize,
    pub hop_one_ip: usize,
    /// 0 = "read", 1 = "write".
    pub is_write: u8,
}

/// Scan counters (C layout of `DcChainStats`).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DcChainStats {
    pub hop_one_candidates: usize,
    pub chain_broken: usize,
    pub chains_emitted: usize,
    pub bytes_scanned: usize,
    pub failed_reads: usize,
    /// 1 when the scan ran to completion. The legacy function writes its
    /// `ChainScanStats*` only in that case; the C++ wrapper mirrors that.
    pub valid: u8,
}

/// Opaque handle behind `DcChainResult*`.
#[derive(Debug, Default)]
pub struct DcChainResult {
    matches: Vec<DcChainMatch>,
    stats: DcChainStats,
}

#[cfg(test)]
pub(crate) fn msvc_sort_for_tests(v: &mut [(u64, u32)]) {
    msvc_sort::sort_by(v, |a, b| a.0 < b.0);
}

#[cfg(test)]
impl DcChainResult {
    pub(crate) fn parts(&self) -> (&[DcChainMatch], DcChainStats) {
        (&self.matches, self.stats)
    }
}

const _: () = {
    const fn assert_send_sync<T: Send + Sync>() {}
    assert_send_sync::<DcChainResult>();
};

// --- PE section enumeration ------------------------------------------------

fn u16_at(b: &[u8], off: usize) -> u16 {
    u16::from_le_bytes([b[off], b[off + 1]])
}

fn u32_at(b: &[u8], off: usize) -> u32 {
    u32::from_le_bytes([b[off], b[off + 1], b[off + 2], b[off + 3]])
}

/// `EnumerateExecutableSections`: (absolute VA, VirtualSize) of every section
/// with IMAGE_SCN_MEM_EXECUTE, in header order. Reads exactly the structs the
/// C++ `Memory::SafeRead<T>` calls read, at the same addresses, so a header
/// that faults faults identically. Address arithmetic wraps like the C++
/// pointer arithmetic does on the target (e_lfanew is a signed LONG).
fn executable_sections(base: usize, reader: &Reader) -> Vec<(usize, usize)> {
    let mut out = Vec::new();
    if base == 0 {
        return out;
    }
    let mut dos = [0u8; DOS_HEADER_SIZE];
    if !reader.read(base, &mut dos) || u16_at(&dos, 0) != DOS_SIGNATURE {
        return out;
    }
    let e_lfanew = u32_at(&dos, DOS_E_LFANEW) as i32;
    let nt_addr = base.wrapping_add(e_lfanew as isize as usize);
    let mut nt = [0u8; NT_HEADERS64_SIZE];
    if !reader.read(nt_addr, &mut nt) || u32_at(&nt, 0) != NT_SIGNATURE {
        return out;
    }
    let sections_addr = nt_addr
        .wrapping_add(NT_OPTIONAL_HEADER)
        .wrapping_add(usize::from(u16_at(&nt, NT_SIZE_OF_OPTIONAL_HEADER)));
    let n_sect = u16_at(&nt, NT_NUMBER_OF_SECTIONS);
    for i in 0..usize::from(n_sect) {
        let mut sh = [0u8; SECTION_HEADER_SIZE];
        if !reader.read(sections_addr.wrapping_add(i.wrapping_mul(SECTION_HEADER_SIZE)), &mut sh) {
            break;
        }
        if u32_at(&sh, SECTION_CHARACTERISTICS) & SCN_MEM_EXECUTE == 0 {
            continue;
        }
        let va = base.wrapping_add(u32_at(&sh, SECTION_VIRTUAL_ADDRESS) as usize);
        out.push((va, u32_at(&sh, SECTION_VIRTUAL_SIZE) as usize));
    }
    out
}

// --- hop matchers (MatchHopTwo / MatchHopThreeRead / MatchHopThreeWrite) ----

/// `[reg + off]` with no index register, where an absent displacement is
/// offset 0 (`DispOrZero`): x86 encodes offset 0 without a displacement byte,
/// and the first static field of a class sits at offset 0. The C++ compares
/// `static_cast<size_t>(disp.value)`, i.e. the sign-extended 64-bit value
/// truncated to the target's size_t; `as usize` does the same.
fn mem_is(op: Operand, reg: Reg, off: usize) -> bool {
    match op {
        Operand::Mem { base, has_index, disp } => base == reg && !has_index && disp as usize == off,
        _ => false,
    }
}

/// Hop 2: `mov R2, [classPtrReg + staticFieldsOffset]` -> Some(R2).
fn match_hop_two(i: &Insn, class_ptr: Reg, sfo: usize) -> Option<Reg> {
    if i.mn != Mn::Mov || i.vis < 2 {
        return None;
    }
    match i.ops[0] {
        Operand::Reg(dest) if mem_is(i.ops[1], class_ptr, sfo) => Some(dest),
        _ => None,
    }
}

/// Hop 3 read: mov / lea / movsxd / movzx / movsx / movss / movsd
/// `R3, [staticFieldsReg + targetFieldOffset]`.
fn match_hop_three_read(i: &Insn, sf: Reg, tfo: usize) -> bool {
    i.mn != Mn::Other && i.vis >= 2 && matches!(i.ops[0], Operand::Reg(_)) && mem_is(i.ops[1], sf, tfo)
}

/// Hop 3 write: mov / movss / movsd `[staticFieldsReg + targetFieldOffset], R3|imm`.
fn match_hop_three_write(i: &Insn, sf: Reg, tfo: usize) -> bool {
    matches!(i.mn, Mn::Mov | Mn::Movss | Mn::Movsd)
        && i.vis >= 2
        && matches!(i.ops[1], Operand::Reg(_) | Operand::Imm)
        && mem_is(i.ops[0], sf, tfo)
}

// --- forward trace (ChainState + TryForwardTrace) ----------------------------

/// MS x64 ABI volatile registers, dropped from the tracked sets at a CALL.
const VOLATILE_REGS: [iced_x86::Register; 7] = [
    iced_x86::Register::RAX,
    iced_x86::Register::RCX,
    iced_x86::Register::RDX,
    iced_x86::Register::R8,
    iced_x86::Register::R9,
    iced_x86::Register::R10,
    iced_x86::Register::R11,
];

/// Registers holding the class pointer / the static_fields pointer. Linear
/// vectors with insertion order preserved, like the C++ `std::vector` +
/// `std::remove` pair (the hop loops iterate them in this order).
#[derive(Default)]
struct ChainState {
    class_ptr: Vec<Reg>,
    static_fields: Vec<Reg>,
}

impl ChainState {
    fn add_class_ptr(&mut self, r: Reg) {
        if r != Reg::None && !self.class_ptr.contains(&r) {
            self.class_ptr.push(r);
        }
    }
    fn add_static_fields(&mut self, r: Reg) {
        if r != Reg::None && !self.static_fields.contains(&r) {
            self.static_fields.push(r);
        }
    }
    fn remove(&mut self, r: Reg) {
        if r == Reg::None {
            return;
        }
        self.class_ptr.retain(|&x| x != r);
        self.static_fields.retain(|&x| x != r);
    }
    fn is_empty(&self) -> bool {
        self.class_ptr.is_empty() && self.static_fields.is_empty()
    }
}

/// `TryForwardTrace`: follows the code after the hop-1 instruction. Returns
/// (hop-3 IP, is_write) or None when the chain breaks (decode failure, RET /
/// unconditional branch, no live register, window exhausted, end of bytes).
fn forward_trace(
    dec: &mut InsnDecoder,
    bytes: &[u8],
    start_ip: usize,
    initial_class_ptr: Reg,
    sfo: usize,
    tfo: usize,
) -> Option<(usize, bool)> {
    let mut state = ChainState::default();
    state.add_class_ptr(initial_class_ptr);
    let mut consumed = 0usize;
    let mut ip = start_ip;
    for _ in 0..MAX_FORWARD_TRACE {
        if consumed >= bytes.len() {
            return None;
        }
        let insn = dec.decode(&bytes[consumed..])?;

        // hop 3 (terminal): every live static-fields register, read first.
        for &sf in &state.static_fields {
            if match_hop_three_read(&insn, sf, tfo) {
                return Some((ip, false));
            }
            if match_hop_three_write(&insn, sf, tfo) {
                return Some((ip, true));
            }
        }

        // RET / unconditional branch end the trace.
        if insn.cat == Cat::Ret || insn.cat == Cat::UncondBr {
            return None;
        }

        // hop 2: first live class-pointer register that matches wins.
        let mut new_sf = Reg::None;
        let mut hop_two = false;
        for &cp in &state.class_ptr {
            if let Some(dest) = match_hop_two(&insn, cp, sfo) {
                new_sf = dest;
                hop_two = true;
                break;
            }
        }

        // register state update
        if insn.cat == Cat::Call {
            for r in VOLATILE_REGS {
                state.remove(Reg::R(r));
            }
        } else if insn.cat == Cat::CondBr {
            // Fall-through is traced; the branch writes no tracked register.
        } else if let (Mn::Mov, true, Operand::Reg(dst), Operand::Reg(src)) =
            (insn.mn, insn.vis >= 2, insn.ops[0], insn.ops[1])
        {
            // reg-reg MOV propagates the source's tags (read before clobber).
            let src_cp = state.class_ptr.contains(&src);
            let src_sf = state.static_fields.contains(&src);
            state.remove(dst);
            if src_cp {
                state.add_class_ptr(dst);
            }
            if src_sf {
                state.add_static_fields(dst);
            }
        } else {
            for &w in &insn.writes[..insn.n_writes] {
                state.remove(w);
            }
        }

        // The hop-2 destination is (re-)tagged after the clobber pass.
        if hop_two && new_sf != Reg::None {
            state.add_static_fields(new_sf);
        }

        if state.is_empty() {
            return None;
        }

        consumed += insn.len;
        ip = ip.wrapping_add(insn.len);
    }
    None
}

/// `ExtractHopOneDestReg`: the canonical destination register of the hop-1
/// MOV / LEA, or None.
fn hop_one_dest(dec: &mut InsnDecoder, bytes: &[u8]) -> Option<Reg> {
    let insn = dec.decode(bytes)?;
    match (insn.vis >= 1, insn.ops[0]) {
        (true, Operand::Reg(r)) if r != Reg::None => Some(r),
        _ => None,
    }
}

fn is_rex(b: u8) -> bool {
    b & 0xF0 == 0x40
}

fn is_rip_relative_modrm(m: u8) -> bool {
    m & 0xC7 == 0x05
}

// --- the scan ----------------------------------------------------------------

/// `DetectFieldAccessChains` minus the module lookup (the caller passes the
/// GameAssembly.dll base). Returns None only when the 1 MiB chunk buffer
/// cannot be allocated (the C++ `std::vector` would have thrown).
pub(crate) fn detect(
    module_base: usize,
    reader: Option<&Reader>,
    slots: &[usize],
    sfo: usize,
    tfo: usize,
    match_limit: usize,
) -> Option<DcChainResult> {
    let mut result = DcChainResult::default();
    // Same early returns, same order: these leave the stats "not written".
    if slots.is_empty() || sfo == 0 || module_base == 0 {
        return Some(result);
    }
    let Some(reader) = reader else { return Some(result) };
    let sections = executable_sections(module_base, reader);
    if sections.is_empty() {
        return Some(result);
    }

    let slot_set: HashSet<usize> = slots.iter().copied().collect();
    let mut dec = InsnDecoder::new();
    let mut buf: Vec<u8> = Vec::new();
    buf.try_reserve_exact(CHUNK_SIZE + TAIL_OVERLAP).ok()?;
    buf.resize(CHUNK_SIZE + TAIL_OVERLAP, 0);

    let mut stats = DcChainStats::default();
    let mut truncated = false;
    // (resolved target, hit IP) pairs already emitted: collapses the REX /
    // non-REX double match of one instruction across the whole scan.
    let mut emitted: HashSet<(usize, usize)> = HashSet::new();

    for &(sec_va, sec_size) in &sections {
        if truncated {
            break;
        }
        reader.scan_readable_spans(
            sec_va,
            sec_size,
            CHUNK_SIZE,
            TAIL_OVERLAP,
            &mut buf,
            &mut stats.failed_reads,
            |span_va, span, owned_len| {
                let read_want = span.len();
                if read_want < 6 {
                    return true;
                }
                // Candidates start only in the owned part of the span; the
                // overlap is decode context.
                let scan_end = owned_len.min(read_want - 6 + 1);
                for p in 0..scan_end {
                    if truncated {
                        break;
                    }
                    let avail = read_want - p;
                    let insn_ip = span_va.wrapping_add(p);

                    // Byte-pattern pre-filter: [REX] 8B|8D modrm(rip) disp32.
                    let mut off = 0usize;
                    if is_rex(span[p]) {
                        if avail < 7 {
                            continue;
                        }
                        off += 1;
                    }
                    if avail - off < 6 {
                        continue;
                    }
                    let op = span[p + off];
                    if op != 0x8B && op != 0x8D {
                        continue;
                    }
                    if !is_rip_relative_modrm(span[p + off + 1]) {
                        continue;
                    }
                    let d = p + off + 2;
                    let disp =
                        i32::from_le_bytes([span[d], span[d + 1], span[d + 2], span[d + 3]]);
                    let insn_len = off + 2 + 4;
                    if avail < insn_len {
                        continue;
                    }
                    let target = insn_ip.wrapping_add(insn_len).wrapping_add(disp as isize as usize);
                    if !slot_set.contains(&target) {
                        continue;
                    }

                    stats.hop_one_candidates += 1;
                    let Some(class_ptr) = hop_one_dest(&mut dec, &span[p..p + insn_len]) else {
                        continue;
                    };
                    let found = forward_trace(
                        &mut dec,
                        &span[p + insn_len..read_want],
                        insn_ip.wrapping_add(insn_len),
                        class_ptr,
                        sfo,
                        tfo,
                    );
                    let Some((hit_ip, is_write)) = found else {
                        stats.chain_broken += 1;
                        continue;
                    };
                    if !emitted.insert((target, hit_ip)) {
                        continue;
                    }
                    result.matches.push(DcChainMatch {
                        hit_ip,
                        hop_one_ip: insn_ip,
                        is_write: u8::from(is_write),
                    });
                    // Checked after the push: match_limit 0 still yields one match.
                    if result.matches.len() >= match_limit {
                        truncated = true;
                        break;
                    }
                }
                stats.bytes_scanned += owned_len;
                !truncated
            },
        );
    }

    // Converging hop-1 loads reach the same hop-3 instruction: sort by hit IP
    // (MSVC std::sort, not stable — see msvc_sort) and keep the first of each
    // run of equal hit IPs (std::unique).
    if result.matches.len() > 1 {
        msvc_sort::sort_by(&mut result.matches, |a, b| a.hit_ip < b.hit_ip);
        result.matches.dedup_by(|cur, kept| cur.hit_ip == kept.hit_ip);
    }

    stats.chains_emitted = result.matches.len();
    stats.valid = 1;
    result.stats = stats;
    Some(result)
}

// --- C ABI -------------------------------------------------------------------

/// Runs the chain detector over the GameAssembly.dll image at `module_base`.
/// Returns a handle (NULL only on an internal panic or when the chunk buffer
/// cannot be allocated); free it with [`dc_chain_result_free`].
///
/// # Safety
/// `read` / `read_ctx` must honour the `dc_read_fn` contract for the duration
/// of the call; `slots` must point to `n_slots` readable `uintptr_t` (it may
/// be NULL when `n_slots == 0`; a NULL `slots` is treated as an empty list).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_chain_detect(
    module_base: usize,
    read: Option<ReadFn>,
    read_ctx: *mut c_void,
    slots: *const usize,
    n_slots: usize,
    static_fields_offset: usize,
    target_field_offset: usize,
    match_limit: usize,
) -> *mut DcChainResult {
    guard("dc_chain_detect", std::ptr::null_mut(), || {
        let slots: &[usize] = if slots.is_null() || n_slots == 0 {
            &[]
        } else {
            // SAFETY: caller contract — `slots` points to n_slots elements.
            unsafe { std::slice::from_raw_parts(slots, n_slots) }
        };
        // SAFETY: caller contract on `read` / `read_ctx`.
        let reader = read.map(|f| unsafe { Reader::new(f, read_ctx) });
        match detect(module_base, reader.as_ref(), slots, static_fields_offset, target_field_offset, match_limit) {
            Some(r) => Box::into_raw(Box::new(r)),
            None => std::ptr::null_mut(),
        }
    })
}

/// Number of matches (0 for NULL).
///
/// # Safety
/// `r` must be NULL or a live handle from [`dc_chain_detect`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_chain_result_len(r: *const DcChainResult) -> usize {
    guard("dc_chain_result_len", 0, || {
        // SAFETY: caller contract.
        unsafe { r.as_ref() }.map_or(0, |r| r.matches.len())
    })
}

/// Copies match `i` into `*out`; returns 1, or 0 (out untouched) for a NULL
/// handle / NULL `out` / `i` out of range.
///
/// # Safety
/// `r` must be NULL or a live handle; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_chain_result_get(r: *const DcChainResult, i: usize, out: *mut DcChainMatch) -> i32 {
    guard("dc_chain_result_get", 0, || {
        // SAFETY: caller contract.
        let (Some(r), false) = (unsafe { r.as_ref() }, out.is_null()) else { return 0 };
        match r.matches.get(i) {
            Some(m) => {
                // SAFETY: out is non-null and writable (caller contract).
                unsafe { out.write(*m) };
                1
            }
            None => 0,
        }
    })
}

/// Copies the scan counters into `*out` (all zero, `valid = 0`, for NULL).
///
/// # Safety
/// `r` must be NULL or a live handle; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_chain_result_stats(r: *const DcChainResult, out: *mut DcChainStats) {
    guard("dc_chain_result_stats", (), || {
        if out.is_null() {
            return;
        }
        // SAFETY: caller contract.
        let stats = unsafe { r.as_ref() }.map_or_else(DcChainStats::default, |r| r.stats);
        // SAFETY: out is non-null and writable (caller contract).
        unsafe { out.write(stats) };
    })
}

/// Frees a handle (NULL is a no-op).
///
/// # Safety
/// `r` must be NULL or a live handle that is not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_chain_result_free(r: *mut DcChainResult) {
    guard("dc_chain_result_free", (), || {
        if !r.is_null() {
            // SAFETY: r came from Box::into_raw in dc_chain_detect.
            drop(unsafe { Box::from_raw(r) });
        }
    })
}
