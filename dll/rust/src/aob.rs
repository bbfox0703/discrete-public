//! AOB pattern builder (Phase R2) — port of `dll/src/shared/AobPattern.cpp`.
//!
//! A minimal x86-64 *length* decoder plus a Cheat-Engine-style pattern
//! builder: it walks a byte window instruction by instruction and emits
//! `"48 89 5C 24 08 E8 ?? ?? ?? ??"`, wildcarding rel8/rel32 branch
//! immediates and the disp32 of `mod=00 rm=101` ModRM operands (RIP-relative
//! in 64-bit mode). See `AobPattern.h` for the design rationale; the short
//! version is "conservative wildcarding": on any opcode the decoder does not
//! recognise it stops, and the rest of the window stays literal.
//!
//! This is a line-by-line port. It must stay behaviourally identical to the
//! legacy C++ (a differential harness compares the two byte-for-byte), so the
//! decoder deliberately keeps every quirk of the original, for example:
//!
//! * at most five legacy prefix bytes are consumed (`pos < 5`); a sixth is
//!   decoded as an opcode;
//! * a single REX byte is consumed; a second one is an "unknown" opcode;
//! * `mod=00 rm=101` is wildcarded as RIP-relative even under a 0x67 prefix;
//! * `0xA0..0xA3` always carry an 8-byte moffs, whatever the prefixes;
//! * the 0x66 demotion only touches 4-byte, non-relative immediates.
//!
//! The same code runs in the x64 DLLs and the x86 Mono DLL, and nothing here
//! depends on the pointer width except the one emulated `static_cast<int>`
//! (see [`decode_one`]), so x86_64 and i686 builds give identical results.
//!
//! The decoder is a pure function over a byte slice: it never touches host
//! memory. Its only consumer (`getMethodAobPattern` in ExportAPI.cpp) hands it
//! a buffer that was already copied out of the game with
//! `Memory::SafeReadBytes`.

use std::ffi::c_char;

use crate::ffi::guard;

/// Per-opcode metadata, mirroring the C++ `OpcodeInfo`.
#[derive(Clone, Copy, Default, Debug)]
struct OpcodeInfo {
    /// A ModRM byte follows the opcode (drives SIB / disp / RIP-relative).
    has_modrm: bool,
    /// Bytes of immediate following any displacement (0/1/2/3/4/8).
    /// `i32` like the C++ `int`, so the arithmetic below reads the same.
    imm_size: i32,
    /// The immediate is a relative branch target, to be wildcarded.
    rel_imm: bool,
    /// The decoder does not know this opcode: bail.
    unknown: bool,
    /// RET / RETF / IRET / unconditional JMP: ends the flow.
    terminal: bool,
}

impl OpcodeInfo {
    const fn imm(n: i32) -> Self {
        Self { has_modrm: false, imm_size: n, rel_imm: false, unknown: false, terminal: false }
    }
    const fn modrm(n: i32) -> Self {
        Self { has_modrm: true, imm_size: n, rel_imm: false, unknown: false, terminal: false }
    }
    const fn rel(n: i32) -> Self {
        Self { has_modrm: false, imm_size: n, rel_imm: true, unknown: false, terminal: false }
    }
    const fn unknown() -> Self {
        Self { has_modrm: false, imm_size: 0, rel_imm: false, unknown: true, terminal: false }
    }
    const fn terminal(self) -> Self {
        Self { terminal: true, ..self }
    }
}

/// One-byte opcode map. The `if` chain keeps the C++ order exactly, because
/// several tests overlap (e.g. 0xA8/0xA9 are checked before the 0xA4..0xAF
/// range) and the first match wins.
fn lookup_1b(op: u8) -> OpcodeInfo {
    // 0x00..0x3F arithmetic rows: lo 4 = AL,imm8; lo 5 = eAX,imm32; else r/m.
    // (0x0F never gets here; 0x26/2E/36/3E only reach this as the 6th+
    // prefix byte, and then decode as ModRM forms, exactly like the C++.)
    if op <= 0x3F {
        return match op & 0x7 {
            4 => OpcodeInfo::imm(1),
            5 => OpcodeInfo::imm(4),
            _ => OpcodeInfo::modrm(0),
        };
    }
    // Standalone REX (a second REX byte) is unknown.
    if (0x40..=0x4F).contains(&op) {
        return OpcodeInfo::unknown();
    }
    // PUSH / POP r64.
    if (0x50..=0x5F).contains(&op) {
        return OpcodeInfo::default();
    }
    if op == 0x68 {
        return OpcodeInfo::imm(4); // PUSH imm32
    }
    if op == 0x6A {
        return OpcodeInfo::imm(1); // PUSH imm8
    }
    if op == 0x69 {
        return OpcodeInfo::modrm(4); // IMUL r, r/m, imm32
    }
    if op == 0x6B {
        return OpcodeInfo::modrm(1); // IMUL r, r/m, imm8
    }
    if (0x60..=0x6F).contains(&op) {
        return OpcodeInfo::unknown();
    }
    // Jcc rel8.
    if (0x70..=0x7F).contains(&op) {
        return OpcodeInfo::rel(1);
    }
    // Group 1.
    if op == 0x80 {
        return OpcodeInfo::modrm(1);
    }
    if op == 0x81 {
        return OpcodeInfo::modrm(4);
    }
    if op == 0x82 {
        return OpcodeInfo::unknown(); // invalid in 64-bit mode
    }
    if op == 0x83 {
        return OpcodeInfo::modrm(1);
    }
    // TEST / XCHG / MOV / LEA / MOV seg / POP r/m.
    if (0x84..=0x8F).contains(&op) {
        return OpcodeInfo::modrm(0);
    }
    // NOP / XCHG eAX / CWD / WAIT / PUSHF / POPF / SAHF / LAHF.
    if (0x90..=0x9F).contains(&op) {
        return OpcodeInfo::default();
    }
    // MOV moffs: 8-byte offset in 64-bit mode (prefixes are not consulted,
    // same as the C++).
    if (0xA0..=0xA3).contains(&op) {
        return OpcodeInfo::imm(8);
    }
    if op == 0xA8 {
        return OpcodeInfo::imm(1);
    }
    if op == 0xA9 {
        return OpcodeInfo::imm(4);
    }
    if (0xA4..=0xAF).contains(&op) {
        return OpcodeInfo::default(); // string ops
    }
    if (0xB0..=0xB7).contains(&op) {
        return OpcodeInfo::imm(1); // MOV r8, imm8
    }
    if (0xB8..=0xBF).contains(&op) {
        return OpcodeInfo::imm(4); // MOV r32, imm32 (imm64 with REX.W, see decode_one)
    }
    if op == 0xC0 || op == 0xC1 {
        return OpcodeInfo::modrm(1); // group 2 shift, imm8
    }
    if op == 0xC2 {
        return OpcodeInfo::imm(2).terminal(); // RET imm16
    }
    if op == 0xC3 {
        return OpcodeInfo::default().terminal(); // RET
    }
    if op == 0xC6 {
        return OpcodeInfo::modrm(1); // MOV r/m8, imm8
    }
    if op == 0xC7 {
        return OpcodeInfo::modrm(4); // MOV r/m32, imm32
    }
    if op == 0xC8 {
        return OpcodeInfo::imm(3); // ENTER imm16, imm8
    }
    if op == 0xC9 {
        return OpcodeInfo::default(); // LEAVE
    }
    if op == 0xCA {
        return OpcodeInfo::imm(2).terminal(); // RETF imm16
    }
    if op == 0xCB {
        return OpcodeInfo::default().terminal(); // RETF
    }
    if op == 0xCC {
        return OpcodeInfo::default(); // INT3
    }
    if op == 0xCD {
        return OpcodeInfo::imm(1); // INT imm8
    }
    if op == 0xCF {
        return OpcodeInfo::default().terminal(); // IRET
    }
    // 0xC4 / 0xC5 (VEX) and 0xCE fall through to "unknown" at the end.
    if (0xD0..=0xD3).contains(&op) {
        return OpcodeInfo::modrm(0); // shift by 1 / CL
    }
    if op == 0xD7 {
        return OpcodeInfo::default(); // XLAT
    }
    if op == 0xD4 || op == 0xD5 || op == 0xD6 {
        return OpcodeInfo::unknown();
    }
    if (0xD8..=0xDF).contains(&op) {
        return OpcodeInfo::modrm(0); // x87
    }
    if (0xE0..=0xE3).contains(&op) {
        return OpcodeInfo::rel(1); // LOOPcc / JrCXZ rel8
    }
    if (0xE4..=0xE7).contains(&op) {
        return OpcodeInfo::imm(1); // IN / OUT imm8
    }
    if op == 0xE8 {
        return OpcodeInfo::rel(4); // CALL rel32
    }
    if op == 0xE9 {
        return OpcodeInfo::rel(4).terminal(); // JMP rel32
    }
    if op == 0xEA {
        return OpcodeInfo::unknown(); // far JMP, invalid in 64-bit mode
    }
    if op == 0xEB {
        return OpcodeInfo::rel(1).terminal(); // JMP rel8
    }
    if (0xEC..=0xEF).contains(&op) {
        return OpcodeInfo::default(); // IN / OUT DX
    }
    if op == 0xF1 || op == 0xF4 || op == 0xF5 {
        return OpcodeInfo::default(); // INT1 / HLT / CMC
    }
    if (0xF8..=0xFD).contains(&op) {
        return OpcodeInfo::default(); // CLC / STC / CLI / STI / CLD / STD
    }
    // Groups 3 / 4 / 5: ModRM; group 3's immediate and group 5's terminal
    // flag depend on ModRM.reg and are resolved in decode_one.
    if op == 0xF6 || op == 0xF7 || op == 0xFE || op == 0xFF {
        return OpcodeInfo::modrm(0);
    }
    // Everything else (incl. 0xF0/F2/F3 reached as a 6th prefix byte).
    OpcodeInfo::unknown()
}

/// Two-byte (0x0F xx) opcode map, same order as the C++ `Lookup0F`.
fn lookup_0f(op2: u8) -> OpcodeInfo {
    if (0x80..=0x8F).contains(&op2) {
        return OpcodeInfo::rel(4); // Jcc rel32
    }
    if (0x71..=0x73).contains(&op2) {
        return OpcodeInfo::modrm(1); // SSE shift-imm group
    }
    // No-operand forms: SYSCALL / CLTS / SYSRET / WBINVD / UD2 / WRMSR / RDTSC
    // / RDMSR / RDPMC / SYSENTER / SYSEXIT / PUSH-POP FS-GS / CPUID.
    if matches!(
        op2,
        0x05 | 0x07 | 0x09 | 0x0B | 0x30 | 0x31 | 0x32 | 0x33 | 0x34 | 0x35 | 0xA0 | 0xA1 | 0xA2
            | 0xA8 | 0xA9
    ) {
        return OpcodeInfo::default();
    }
    if (0x90..=0x9F).contains(&op2) {
        return OpcodeInfo::modrm(0); // SETcc
    }
    if op2 == 0xA4 || op2 == 0xAC || op2 == 0xBA {
        return OpcodeInfo::modrm(1); // SHLD / SHRD imm8, BT group imm8
    }
    if op2 == 0xA5 || op2 == 0xAD {
        return OpcodeInfo::modrm(0); // SHLD / SHRD CL
    }
    if matches!(op2, 0x70 | 0xC2 | 0xC4 | 0xC5 | 0xC6) {
        return OpcodeInfo::modrm(1); // PSHUF / CMPPS / PINSRW / PEXTRW / SHUFPS
    }
    if (0xC8..=0xCF).contains(&op2) {
        return OpcodeInfo::default(); // BSWAP
    }
    // Default: ModRM, no immediate.
    OpcodeInfo::modrm(0)
}

/// One decoded instruction, mirroring the C++ `Decoded`. Offsets are relative
/// to the instruction start; -1 = absent.
#[derive(Clone, Copy, Debug)]
struct Decoded {
    length: i32,
    imm_offset: i32,
    imm_size: i32,
    disp_offset: i32,
    disp_size: i32,
    rel_imm: bool,
    rel_disp: bool,
    valid: bool,
    terminal: bool,
}

impl Default for Decoded {
    fn default() -> Self {
        Self {
            length: 0,
            imm_offset: -1,
            imm_size: 0,
            disp_offset: -1,
            disp_size: 0,
            rel_imm: false,
            rel_disp: false,
            valid: true,
            terminal: false,
        }
    }
}

impl Decoded {
    fn invalid(mut self) -> Self {
        self.valid = false;
        self
    }
}

/// Decodes the instruction at the start of `p` (the C++ `DecodeOne(p, avail)`
/// with `avail == p.len()`).
fn decode_one(p: &[u8]) -> Decoded {
    let d = Decoded::default();
    if p.is_empty() {
        return d.invalid();
    }

    // Judgement call: the C++ compares `pos` against `static_cast<int>(avail)`.
    // MSVC defines that conversion as truncation to the low 32 bits
    // (two's complement), which is exactly what Rust's `as i32` does, so the
    // bound is computed the same way. It only matters for windows of 2 GiB
    // or more (a negative bound makes the decoder bail at once; a truncated
    // one shortens the window) and keeps every read below inside `p`, since
    // the bound is never larger than `p.len()`.
    let avail = p.len() as i32;
    let byte = |i: i32| p[i as usize];

    let mut pos: i32 = 0;
    let mut has_op_size = false;
    let mut rex: u8 = 0;

    // Legacy prefixes — at most five bytes (the C++ loop bound is `pos < 5`).
    while pos < avail && pos < 5 {
        match byte(pos) {
            0xF0 | 0xF2 | 0xF3 | 0x26 | 0x2E | 0x36 | 0x3E | 0x64 | 0x65 => pos += 1,
            0x66 => {
                has_op_size = true;
                pos += 1;
            }
            // 0x67 is consumed but (like the C++'s unused `hasAddrSize`)
            // changes nothing.
            0x67 => pos += 1,
            _ => break,
        }
    }
    if pos >= avail {
        return d.invalid();
    }

    // One REX prefix.
    if (byte(pos) & 0xF0) == 0x40 {
        rex = byte(pos);
        pos += 1;
        if pos >= avail {
            return d.invalid();
        }
    }

    // Opcode: 1, 2 or 3 bytes.
    let mut info;
    let op_len: i32;
    let op = byte(pos);
    if op == 0x0F {
        if pos + 1 >= avail {
            return d.invalid();
        }
        let op2 = byte(pos + 1);
        if op2 == 0x38 || op2 == 0x3A {
            // Three-byte escape: ModRM, plus imm8 for the 0x3A bank. The
            // third opcode byte is only bounds-checked, never inspected.
            if pos + 2 >= avail {
                return d.invalid();
            }
            info = OpcodeInfo::modrm(if op2 == 0x3A { 1 } else { 0 });
            op_len = 3;
        } else {
            info = lookup_0f(op2);
            op_len = 2;
        }
    } else {
        info = lookup_1b(op);
        op_len = 1;
    }
    pos += op_len;
    if info.unknown {
        // The C++ records the length so far, but `valid = false` makes the
        // caller bail regardless.
        return Decoded { length: pos, ..d.invalid() };
    }
    let mut d = Decoded { terminal: info.terminal, ..d };

    if info.has_modrm {
        if pos >= avail {
            return d.invalid();
        }
        let modrm = byte(pos);
        pos += 1;
        let md = modrm >> 6;
        let rm = modrm & 0x7;
        let reg = (modrm >> 3) & 0x7;

        // Group 3: only TEST (reg 0/1) carries an immediate.
        if op_len == 1 && (op == 0xF6 || op == 0xF7) && reg <= 1 {
            info.imm_size = if op == 0xF6 { 1 } else { 4 };
        }
        // Group 5: FF /4 (JMP r/m) and FF /5 (JMP far) end the flow.
        if op_len == 1 && op == 0xFF && (reg == 4 || reg == 5) {
            d.terminal = true;
        }

        let mut sib = false;
        let mut disp_size: i32 = 0;
        let mut rip_rel = false;
        match md {
            0 => {
                if rm == 4 {
                    sib = true;
                } else if rm == 5 {
                    // RIP-relative (or disp32 absolute under 0x67 — wildcarded
                    // either way, as in the C++).
                    disp_size = 4;
                    rip_rel = true;
                }
            }
            1 => {
                sib = rm == 4;
                disp_size = 1;
            }
            2 => {
                sib = rm == 4;
                disp_size = 4;
            }
            _ => {} // register operand
        }

        if sib {
            if pos >= avail {
                return d.invalid();
            }
            let base = byte(pos) & 0x7;
            pos += 1;
            // mod=00 + SIB base=101: disp32, no base. Not RIP-relative, so
            // not wildcarded.
            if md == 0 && base == 5 {
                disp_size = 4;
            }
        }

        if disp_size > 0 {
            if pos + disp_size > avail {
                return d.invalid();
            }
            d.disp_offset = pos;
            d.disp_size = disp_size;
            d.rel_disp = rip_rel;
            pos += disp_size;
        }
    }

    // REX.W + B8..BF: MOV r64, imm64.
    if (0xB8..=0xBF).contains(&op) && op_len == 1 && (rex & 0x08) != 0 {
        info.imm_size = 8;
    }

    // 0x66 demotes 32-bit (non-branch) immediates to 16-bit.
    if has_op_size && info.imm_size == 4 && !info.rel_imm {
        info.imm_size = 2;
    }

    if info.imm_size > 0 {
        if pos + info.imm_size > avail {
            return d.invalid();
        }
        d.imm_offset = pos;
        d.imm_size = info.imm_size;
        d.rel_imm = info.rel_imm;
        pos += info.imm_size;
    }

    d.length = pos;
    d
}

/// Rust mirror of the C++ `Discrete::Aob::PatternResult`. The C++ `bytes`
/// field is always `input[..length]`, so it is not duplicated here.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct PatternResult {
    /// CE-style pattern: uppercase hex, `??` for wildcards, single spaces.
    pub pattern: String,
    /// Parallel to `input[..length]`; true = `??`.
    pub wildcard_mask: Vec<bool>,
    /// Bytes the pattern covers (trimmed at a terminal instruction).
    pub length: usize,
    pub instruction_count: usize,
    /// CALL/JMP/Jcc relative immediates wildcarded.
    pub rel_branch_count: usize,
    /// RIP-relative ModRM disp32 fields wildcarded.
    pub rip_rel_count: usize,
    pub decoder_bailed: bool,
    /// Byte offset of the instruction the decoder gave up on (when bailed).
    pub bailed_at: usize,
    pub stopped_at_terminal: bool,
    /// The input length.
    pub requested_length: usize,
}

const HEX: &[u8; 16] = b"0123456789ABCDEF";

/// Builds the AOB pattern for `bytes` (the C++ `BuildPattern`). With
/// `stop_at_terminal`, the pattern ends at the first RET / RETF / IRET /
/// unconditional JMP (incl. `FF /4`, `FF /5`) that is followed by more bytes.
pub fn build_pattern(bytes: &[u8], stop_at_terminal: bool) -> PatternResult {
    let mut len = bytes.len();
    let mut r = PatternResult {
        length: len,
        requested_length: len,
        wildcard_mask: vec![false; len],
        ..PatternResult::default()
    };

    let mut cursor = 0usize;
    while cursor < len {
        let d = decode_one(&bytes[cursor..len]);
        if !d.valid || d.length == 0 {
            r.decoder_bailed = true;
            r.bailed_at = cursor;
            break; // rest stays literal
        }
        if d.rel_imm && d.imm_offset >= 0 && d.imm_size > 0 {
            for i in 0..d.imm_size {
                let idx = cursor + (d.imm_offset + i) as usize;
                if idx < len {
                    r.wildcard_mask[idx] = true;
                }
            }
            r.rel_branch_count += 1;
        }
        if d.rel_disp && d.disp_offset >= 0 && d.disp_size > 0 {
            for i in 0..d.disp_size {
                let idx = cursor + (d.disp_offset + i) as usize;
                if idx < len {
                    r.wildcard_mask[idx] = true;
                }
            }
            r.rip_rel_count += 1;
        }
        cursor += d.length as usize;
        r.instruction_count += 1;

        // Checked after this instruction's wildcards are applied, so a
        // terminal `E9 rel32` still gets its target wildcarded.
        if stop_at_terminal && d.terminal {
            if cursor < len {
                r.stopped_at_terminal = true;
                r.length = cursor;
                r.wildcard_mask.truncate(cursor);
                len = cursor;
            }
            break;
        }
    }

    let mut pattern = Vec::with_capacity(len.saturating_mul(3));
    for (i, (&b, &wild)) in bytes[..len].iter().zip(&r.wildcard_mask).enumerate() {
        if i > 0 {
            pattern.push(b' ');
        }
        if wild {
            pattern.extend_from_slice(b"??");
        } else {
            pattern.push(HEX[usize::from(b >> 4)]);
            pattern.push(HEX[usize::from(b & 0xF)]);
        }
    }
    // Only ASCII hex digits, '?' and ' ' were pushed.
    r.pattern = String::from_utf8(pattern).unwrap_or_default();
    r
}

/// C mirror: `DcAobResult` in `include/discrete_core.h`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DcAobResult {
    pub length: usize,
    pub instruction_count: usize,
    pub rel_branch_count: usize,
    pub rip_rel_count: usize,
    pub bailed_at: usize,
    pub requested_length: usize,
    pub pattern_len: usize,
    pub decoder_bailed: u8,
    pub stopped_at_terminal: u8,
}

/// `int32_t dc_aob_build_pattern(...)` — see `include/discrete_core.h`.
///
/// Returns 1 on success, 0 when a buffer is too small, a required pointer is
/// NULL, or on an internal panic. `*out` is zeroed before anything else, so
/// every 0 return leaves it zeroed (the panic case included; with `out` NULL
/// there is nothing to zero and the call simply returns 0). `stop_at_terminal`
/// is a C boolean: any non-zero value means true.
///
/// # Safety
/// `bytes` must point to `len` readable bytes (it may be NULL when `len == 0`),
/// `pattern_out` / `mask_out` to `pattern_cap` / `mask_cap` writable bytes,
/// and `out` to a writable `DcAobResult`. `bytes` is a caller-owned buffer
/// (the only consumer passes a `Memory::SafeReadBytes` copy), never live game
/// memory.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_aob_build_pattern(
    bytes: *const u8,
    len: usize,
    stop_at_terminal: i32,
    pattern_out: *mut c_char,
    pattern_cap: usize,
    mask_out: *mut u8,
    mask_cap: usize,
    out: *mut DcAobResult,
) -> i32 {
    guard("dc_aob_build_pattern", 0, || {
        if out.is_null() {
            return 0;
        }
        // SAFETY: `out` is non-null and writable per the contract.
        unsafe { out.write(DcAobResult::default()) };

        // The header asks for >= 3*len pattern bytes (one more than the
        // 3*len-1 a full pattern needs) and >= len mask bytes.
        let Some(need) = len.checked_mul(3) else { return 0 };
        if pattern_cap < need || mask_cap < len {
            return 0;
        }
        if len > 0 && (bytes.is_null() || pattern_out.is_null() || mask_out.is_null()) {
            return 0;
        }
        // `slice::from_raw_parts` requires len <= isize::MAX; no real buffer
        // is that large anyway.
        if len > isize::MAX as usize {
            return 0;
        }
        let input: &[u8] = if len == 0 {
            &[]
        } else {
            // SAFETY: non-null, `len` readable bytes per the contract.
            unsafe { std::slice::from_raw_parts(bytes, len) }
        };

        let r = build_pattern(input, stop_at_terminal != 0);

        // pattern.len() <= 3*length - 1 <= pattern_cap; length <= len <= mask_cap.
        let pat = r.pattern.as_bytes();
        if !pat.is_empty() {
            // SAFETY: pattern_out holds pattern_cap >= pat.len() bytes and
            // does not overlap the freshly allocated `pat`.
            unsafe { std::ptr::copy_nonoverlapping(pat.as_ptr(), pattern_out.cast::<u8>(), pat.len()) };
        }
        for (i, &w) in r.wildcard_mask.iter().enumerate() {
            // SAFETY: i < length <= len <= mask_cap.
            unsafe { mask_out.add(i).write(u8::from(w)) };
        }

        let res = DcAobResult {
            length: r.length,
            instruction_count: r.instruction_count,
            rel_branch_count: r.rel_branch_count,
            rip_rel_count: r.rip_rel_count,
            bailed_at: r.bailed_at,
            requested_length: r.requested_length,
            pattern_len: pat.len(),
            decoder_bailed: u8::from(r.decoder_bailed),
            stopped_at_terminal: u8::from(r.stopped_at_terminal),
        };
        // SAFETY: as above.
        unsafe { out.write(res) };
        1
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Shorthand: parse "48 89 5C" into bytes.
    fn hx(s: &str) -> Vec<u8> {
        s.split_whitespace().map(|t| u8::from_str_radix(t, 16).unwrap()).collect()
    }

    fn pat(s: &str) -> PatternResult {
        build_pattern(&hx(s), true)
    }

    /// Asserts the pattern equals the input with `??` wherever `expect` has it.
    fn check(input: &str, expect: &str) -> PatternResult {
        let r = pat(input);
        assert_eq!(r.pattern, expect, "input: {input}");
        r
    }

    /// Single instruction, followed by a NOP so we can see where it ended:
    /// asserts the instruction decodes to exactly `len_expected` bytes.
    fn len_of(insn: &str) -> usize {
        let mut b = hx(insn);
        b.push(0x90);
        let r = build_pattern(&b, false);
        assert!(!r.decoder_bailed, "bailed on {insn}: {r:?}");
        assert_eq!(r.instruction_count, 2, "{insn} did not end on a boundary: {r:?}");
        b.len() - 1
    }

    fn bails(insn: &str) {
        let r = pat(insn);
        assert!(r.decoder_bailed, "expected bail on {insn}: {r:?}");
        assert_eq!(r.bailed_at, 0);
        assert_eq!(r.instruction_count, 0);
        assert!(r.wildcard_mask.iter().all(|&w| !w));
    }

    // --- Examples from the legacy comments ----------------------------------

    #[test]
    fn header_example_prologue_with_call() {
        // AobPattern.h shows "48 89 5C 24 ?? E8 ?? ?? ?? ??". The disp8 of a
        // SIB stack operand is NOT wildcarded by the legacy decoder (only
        // RIP-relative disp32 is), so the real output keeps it literal; the
        // CALL rel32 is wildcarded.
        let r = check("48 89 5C 24 08 E8 11 22 33 44", "48 89 5C 24 08 E8 ?? ?? ?? ??");
        assert_eq!(r.instruction_count, 2);
        assert_eq!(r.rel_branch_count, 1);
        assert_eq!(r.rip_rel_count, 0);
        assert!(!r.decoder_bailed && !r.stopped_at_terminal);
        assert_eq!(r.length, 10);
    }

    #[test]
    fn terminal_trim_mono_getter() {
        // AobPattern.h: a 24-byte Mono getter asked for 48 bytes used to sign
        // its JIT bookkeeping tail `C3 01 04 01 00 04 02 00 00 …`.
        let getter = "48 83 EC 08 48 89 0C 24 48 8B 05 10 20 30 40 48 8B 40 10 48 83 C4 08 C3";
        let tail = "01 04 01 00 04 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00";
        let input = format!("{getter} {tail}");
        assert_eq!(hx(getter).len(), 24);
        assert_eq!(hx(&input).len(), 48);

        let r = pat(&input);
        assert!(r.stopped_at_terminal);
        assert!(!r.decoder_bailed);
        assert_eq!(r.length, 24);
        assert_eq!(r.requested_length, 48);
        assert_eq!(r.wildcard_mask.len(), 24);
        assert_eq!(r.instruction_count, 6);
        assert_eq!(r.rip_rel_count, 1);
        assert_eq!(
            r.pattern,
            "48 83 EC 08 48 89 0C 24 48 8B 05 ?? ?? ?? ?? 48 8B 40 10 48 83 C4 08 C3"
        );

        // Raw-window behaviour: the full 48 bytes; the tail `01 04` decodes
        // as ADD [rsp+rax], eax ... until something bails or the window ends.
        let raw = build_pattern(&hx(&input), false);
        assert_eq!(raw.length, 48);
        assert!(!raw.stopped_at_terminal);
        assert!(raw.pattern.starts_with("48 83 EC 08 48 89 0C 24 48 8B 05 ?? ?? ?? ?? "));
        assert!(raw.pattern.ends_with(" 00 00"));
    }

    #[test]
    fn terminal_e9_keeps_wildcarded_target() {
        // "a terminal `E9 rel32` still needs its target wildcarded".
        let r = check("90 E9 01 02 03 04 CC CC", "90 E9 ?? ?? ?? ??");
        assert!(r.stopped_at_terminal);
        assert_eq!(r.rel_branch_count, 1);
        assert_eq!(r.length, 6);
    }

    #[test]
    fn terminal_at_window_end_is_not_a_trim() {
        let r = check("55 C3", "55 C3");
        assert!(!r.stopped_at_terminal);
        assert_eq!(r.length, 2);
        assert_eq!(r.instruction_count, 2);
    }

    #[test]
    fn group5_ff4_ff5_are_terminal() {
        // JMP [rip+disp32] (FF /4, import thunk) — terminal + RIP-relative.
        let r = check("FF 25 10 20 30 40 CC CC", "FF 25 ?? ?? ?? ??");
        assert!(r.stopped_at_terminal);
        assert_eq!(r.rip_rel_count, 1);
        // JMP rax (FF E0) with REX.
        let r = check("48 FF E0 00 00", "48 FF E0");
        assert!(r.stopped_at_terminal);
        // FF /5 (far JMP m16:32).
        let r = check("FF 28 90", "FF 28");
        assert!(r.stopped_at_terminal);
        // FF /2 (CALL r/m) and FF /6 (PUSH r/m) are not terminal.
        let r = check("FF D0 90", "FF D0 90");
        assert!(!r.stopped_at_terminal);
        assert_eq!(r.instruction_count, 2);
        let r = check("FF 35 01 02 03 04 90", "FF 35 ?? ?? ?? ?? 90");
        assert!(!r.stopped_at_terminal);
        // FE /4 is NOT group 5: no terminal.
        assert!(!pat("FE E0 90").stopped_at_terminal);
    }

    #[test]
    fn every_terminal_opcode() {
        for (insn, n) in
            [("C3", 1), ("C2 08 00", 3), ("CB", 1), ("CA 08 00", 3), ("CF", 1), ("EB 05", 2), ("E9 00 00 00 00", 5)]
        {
            let mut b = hx(insn);
            b.extend_from_slice(&[0xCC, 0xCC]);
            let r = build_pattern(&b, true);
            assert!(r.stopped_at_terminal, "{insn}");
            assert_eq!(r.length, n, "{insn}");
            // stop_at_terminal = false ignores it.
            let r = build_pattern(&b, false);
            assert!(!r.stopped_at_terminal, "{insn}");
            assert_eq!(r.length, b.len(), "{insn}");
            assert_eq!(r.instruction_count, 3, "{insn}");
        }
    }

    #[test]
    fn group3_test_immediate_fixup() {
        // F6 /0 ib, F7 /0 id, F7 /1 id; 0x66 demotes F7's immediate to 2.
        assert_eq!(len_of("F6 C1 7F"), 3);
        assert_eq!(len_of("F6 C9 7F"), 3);
        assert_eq!(len_of("F7 C1 01 02 03 04"), 6);
        assert_eq!(len_of("F7 C9 01 02 03 04"), 6);
        assert_eq!(len_of("66 F7 C1 01 02"), 5);
        // reg 2..7 (NOT/NEG/MUL/IMUL/DIV/IDIV): no immediate.
        for reg in 2u8..8 {
            let m = 0xC0 | (reg << 3);
            assert_eq!(len_of(&format!("F6 {m:02X}")), 2);
            assert_eq!(len_of(&format!("48 F7 {m:02X}")), 3);
        }
        // Group 3 behind 0F is not a group 3 (0F F7 = MASKMOVQ, ModRM only).
        assert_eq!(len_of("0F F7 C1"), 3);
    }

    // --- Lengths per opcode class -------------------------------------------

    #[test]
    fn arithmetic_rows_00_3f() {
        for row in 0u8..8 {
            let base = row * 8;
            for lo in 0u8..8 {
                let op = base | lo;
                // Skip the opcodes that are prefixes or escapes in the prefix loop.
                if matches!(op, 0x0F | 0x26 | 0x2E | 0x36 | 0x3E) {
                    continue;
                }
                let insn = match lo {
                    4 => format!("{op:02X} 11"),
                    5 => format!("{op:02X} 11 22 33 44"),
                    _ => format!("{op:02X} C0"),
                };
                assert_eq!(len_of(&insn), hx(&insn).len(), "{insn}");
            }
        }
        // Remaining 0x06/07/0E/16/17/1E/1F/27/2F/37/3F (invalid in 64-bit)
        // still decode as ModRM forms, like the C++.
        assert_eq!(len_of("06 C0"), 2);
        assert_eq!(len_of("3F C0"), 2);
    }

    #[test]
    fn push_pop_and_one_byte_ops() {
        for op in (0x50u8..=0x5F).chain(0x90..=0x9F).chain(0xA4..=0xA7).chain(0xAA..=0xAF) {
            assert_eq!(len_of(&format!("{op:02X}")), 1, "{op:02X}");
        }
        for op in [0xC9u8, 0xCC, 0xD7, 0xEC, 0xED, 0xEE, 0xEF, 0xF1, 0xF4, 0xF5, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD] {
            assert_eq!(len_of(&format!("{op:02X}")), 1, "{op:02X}");
        }
        assert_eq!(len_of("41 57"), 2); // push r15
    }

    #[test]
    fn immediates() {
        assert_eq!(len_of("68 01 02 03 04"), 5);
        assert_eq!(len_of("6A 01"), 2);
        assert_eq!(len_of("69 C0 01 02 03 04"), 6);
        assert_eq!(len_of("6B C0 01"), 3);
        assert_eq!(len_of("80 C0 01"), 3);
        assert_eq!(len_of("81 C0 01 02 03 04"), 6);
        assert_eq!(len_of("83 C0 01"), 3);
        assert_eq!(len_of("48 81 EC 00 01 00 00"), 7);
        assert_eq!(len_of("A8 01"), 2);
        assert_eq!(len_of("A9 01 02 03 04"), 5);
        assert_eq!(len_of("B0 01"), 2);
        assert_eq!(len_of("B8 01 02 03 04"), 5);
        assert_eq!(len_of("41 BF 01 02 03 04"), 6); // REX.B without W: imm32
        assert_eq!(len_of("48 B8 01 02 03 04 05 06 07 08"), 10);
        assert_eq!(len_of("49 BF 01 02 03 04 05 06 07 08"), 10);
        assert_eq!(len_of("C0 E0 01"), 3);
        assert_eq!(len_of("C1 E0 01"), 3);
        assert_eq!(len_of("C6 00 01"), 3);
        assert_eq!(len_of("C7 00 01 02 03 04"), 6);
        assert_eq!(len_of("48 C7 44 24 20 01 02 03 04"), 9);
        assert_eq!(len_of("C8 10 00 01"), 4);
        assert_eq!(len_of("CD 2E"), 2);
        assert_eq!(len_of("E4 01"), 2);
        assert_eq!(len_of("E7 01"), 2);
        for op in [0xA0u8, 0xA1, 0xA2, 0xA3] {
            assert_eq!(len_of(&format!("{op:02X} 01 02 03 04 05 06 07 08")), 9);
        }
        // MOV moffs keeps its 8-byte offset under 0x67 (legacy quirk).
        assert_eq!(len_of("67 A1 01 02 03 04 05 06 07 08"), 10);
    }

    #[test]
    fn operand_size_override() {
        assert_eq!(len_of("66 05 01 02"), 4);
        assert_eq!(len_of("66 68 01 02"), 4);
        assert_eq!(len_of("66 81 C0 01 02"), 5);
        assert_eq!(len_of("66 C7 00 01 02"), 5);
        assert_eq!(len_of("66 B8 01 02"), 4);
        assert_eq!(len_of("66 A9 01 02"), 4);
        // REX.W imm64 is not demoted (it is 8, not 4).
        assert_eq!(len_of("66 48 B8 01 02 03 04 05 06 07 08"), 11);
        // imm8 forms unaffected.
        assert_eq!(len_of("66 83 C0 01"), 4);
        // ENTER's 3-byte immediate unaffected.
        assert_eq!(len_of("66 C8 01 02 03"), 5);
        // Relative branches are never demoted.
        let r = check("66 E8 01 02 03 04 90", "66 E8 ?? ?? ?? ?? 90");
        assert_eq!(r.instruction_count, 2);
        let r = check("66 0F 84 01 02 03 04 90", "66 0F 84 ?? ?? ?? ?? 90");
        assert_eq!(r.instruction_count, 2);
    }

    #[test]
    fn relative_branches_wildcarded() {
        for op in 0x70u8..=0x7F {
            let r = check(&format!("{op:02X} 05 90"), &format!("{op:02X} ?? 90"));
            assert_eq!(r.rel_branch_count, 1);
        }
        for op in 0xE0u8..=0xE3 {
            let r = check(&format!("{op:02X} 05 90"), &format!("{op:02X} ?? 90"));
            assert_eq!(r.rel_branch_count, 1);
        }
        for op2 in 0x80u8..=0x8F {
            let r = check(&format!("0F {op2:02X} 01 02 03 04 90"), &format!("0F {op2:02X} ?? ?? ?? ?? 90"));
            assert_eq!(r.rel_branch_count, 1);
        }
        let r = check("E8 01 02 03 04 E8 05 06 07 08", "E8 ?? ?? ?? ?? E8 ?? ?? ?? ??");
        assert_eq!(r.rel_branch_count, 2);
        assert_eq!(r.instruction_count, 2);
    }

    #[test]
    fn modrm_addressing_forms() {
        // mod 00: plain, SIB, RIP-relative, SIB base=5 disp32 (not wildcarded).
        assert_eq!(len_of("48 8B 00"), 3);
        assert_eq!(len_of("48 8B 04 24"), 4);
        let r = check("48 8B 05 01 02 03 04 90", "48 8B 05 ?? ?? ?? ?? 90");
        assert_eq!(r.rip_rel_count, 1);
        let r = check("48 8B 04 25 01 02 03 04 90", "48 8B 04 25 01 02 03 04 90");
        assert_eq!(r.rip_rel_count, 0);
        assert_eq!(r.instruction_count, 2);
        // mod 01: disp8, with and without SIB.
        assert_eq!(len_of("48 8B 40 10"), 4);
        assert_eq!(len_of("48 8B 44 24 10"), 5);
        // mod 01 SIB base=5 is [rbp+idx+disp8] — no disp32.
        assert_eq!(len_of("48 8B 44 25 10"), 5);
        // mod 10: disp32, with and without SIB; never wildcarded.
        let r = check("48 8B 80 01 02 03 04 90", "48 8B 80 01 02 03 04 90");
        assert_eq!(r.rip_rel_count, 0);
        assert_eq!(len_of("48 8B 84 24 01 02 03 04"), 8);
        // mod 11: register.
        assert_eq!(len_of("48 8B C1"), 3);
        // RIP-relative + immediate: disp then imm.
        let r = check("C7 05 01 02 03 04 AA BB CC DD 90", "C7 05 ?? ?? ?? ?? AA BB CC DD 90");
        assert_eq!(r.rip_rel_count, 1);
        // 0x67 + mod00 rm101 is still wildcarded (legacy comment).
        let r = check("67 8B 05 01 02 03 04 90", "67 8B 05 ?? ?? ?? ?? 90");
        assert_eq!(r.rip_rel_count, 1);
        // LEA rcx, [rip+x].
        check("48 8D 0D 01 02 03 04", "48 8D 0D ?? ?? ?? ??");
        // 0x84..0x8F all ModRM.
        for op in 0x84u8..=0x8F {
            assert_eq!(len_of(&format!("{op:02X} C0")), 2);
        }
        for op in (0xD0u8..=0xD3).chain(0xD8..=0xDF) {
            assert_eq!(len_of(&format!("{op:02X} C0")), 2);
        }
        assert_eq!(len_of("FE C0"), 2);
    }

    #[test]
    fn two_and_three_byte_opcodes() {
        // 0F 71..73 ModRM + imm8.
        for op2 in 0x71u8..=0x73 {
            assert_eq!(len_of(&format!("66 0F {op2:02X} D0 04")), 5);
        }
        // No-operand 0F xx.
        for op2 in [0x05u8, 0x07, 0x09, 0x0B, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0xA0, 0xA1, 0xA2, 0xA8, 0xA9] {
            assert_eq!(len_of(&format!("0F {op2:02X}")), 2, "0F {op2:02X}");
        }
        // SETcc.
        for op2 in 0x90u8..=0x9F {
            assert_eq!(len_of(&format!("0F {op2:02X} C0")), 3);
        }
        for op2 in [0xA4u8, 0xAC, 0xBA, 0x70, 0xC2, 0xC4, 0xC5, 0xC6] {
            assert_eq!(len_of(&format!("0F {op2:02X} C0 01")), 4, "0F {op2:02X}");
        }
        for op2 in [0xA5u8, 0xAD] {
            assert_eq!(len_of(&format!("0F {op2:02X} C0")), 3);
        }
        for op2 in 0xC8u8..=0xCF {
            assert_eq!(len_of(&format!("0F {op2:02X}")), 2);
        }
        // Default: ModRM, no imm (MOVUPS, CMOVcc, MOVZX, 0F 0F, 0F FF ...).
        for op2 in [0x10u8, 0x11, 0x28, 0x40, 0x4F, 0xB6, 0xB7, 0xC0, 0xC1, 0x0F, 0xFF, 0x1F] {
            assert_eq!(len_of(&format!("0F {op2:02X} C0")), 3, "0F {op2:02X}");
        }
        // 0F 1F 44 00 00 — the common 5-byte NOP.
        assert_eq!(len_of("0F 1F 44 00 00"), 5);
        // 0F 38 xx: ModRM only; 0F 3A xx: ModRM + imm8.
        assert_eq!(len_of("66 0F 38 00 C1"), 5);
        assert_eq!(len_of("66 0F 3A 0F C1 08"), 6);
        // RIP-relative inside an SSE op.
        let r = check("F3 0F 10 05 01 02 03 04 90", "F3 0F 10 05 ?? ?? ?? ?? 90");
        assert_eq!(r.rip_rel_count, 1);
    }

    #[test]
    fn prefixes() {
        assert_eq!(len_of("F0 48 0F B1 0A"), 5); // lock cmpxchg
        assert_eq!(len_of("F3 48 AB"), 3); // rep stosq
        assert_eq!(len_of("65 48 8B 04 25 30 00 00 00"), 9); // mov rax, gs:[30]
        assert_eq!(len_of("64 8B 00"), 3);
        assert_eq!(len_of("2E 3E 26 36 90"), 5);
        assert_eq!(len_of("66 66 66 66 66 0F 1F 84 00 00 00 00 00"), 13);
        // Five prefixes consumed; a sixth prefix byte is decoded as an opcode.
        // 0x66 as an opcode is in 0x60..0x6F → unknown.
        bails("66 66 66 66 66 66 90");
        bails("F3 F3 F3 F3 F3 F0 90");
        bails("64 64 64 64 64 65 90");
        // A sixth 0x2E is opcode 0x2E: a ModRM row form (legacy quirk).
        assert_eq!(len_of("2E 2E 2E 2E 2E 2E C0"), 7);
        // A sixth 0x67 → unknown opcode (0x60..0x6F).
        bails("67 67 67 67 67 67 90");
        // REX then a legacy prefix: the prefix is the opcode → unknown.
        bails("48 66 90");
        // Two REX bytes: the second is an unknown opcode.
        bails("48 48 89 C0");
    }

    #[test]
    fn unknown_opcodes_bail() {
        for op in [
            0x60u8, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x6C, 0x6D, 0x6E, 0x6F, 0x82, 0xC4, 0xC5, 0xCE, 0xD4,
            0xD5, 0xD6, 0xEA,
        ] {
            // 0x64..0x67 are prefixes; alone (with nothing after) they bail
            // on "ran out of bytes".
            let r = build_pattern(&[op], true);
            assert!(r.decoder_bailed, "{op:02X}");
            assert_eq!(r.pattern, format!("{op:02X}"));
        }
        // Mid-stream bail: the prefix of the window is decoded, the rest is
        // literal, `length` stays the full window.
        let r = check("E8 01 02 03 04 C4 E1 F9 7E C0 E8 01 02 03 04", "E8 ?? ?? ?? ?? C4 E1 F9 7E C0 E8 01 02 03 04");
        assert!(r.decoder_bailed);
        assert_eq!(r.bailed_at, 5);
        assert_eq!(r.instruction_count, 1);
        assert_eq!(r.length, 15);
        // A bail before a terminal means no trim.
        let r = check("62 C3 00", "62 C3 00");
        assert!(!r.stopped_at_terminal);
    }

    #[test]
    fn truncated_instructions_bail() {
        for insn in [
            "48",             // REX only
            "66",             // prefix only
            "0F",             // escape only
            "0F 38",          // three-byte escape without third byte
            "48 8B",          // missing ModRM
            "48 8B 04",       // missing SIB
            "48 8B 05 01 02", // short disp32
            "48 8B 40",       // short disp8
            "E8 01 02 03",    // short rel32
            "48 B8 01 02 03 04 05 06 07", // short imm64
            "F7 C1 01 02 03", // short group-3 imm32
            "C8 01 02",       // short ENTER
        ] {
            let mut b = hx("90");
            b.extend(hx(insn));
            let r = build_pattern(&b, true);
            assert!(r.decoder_bailed, "{insn}");
            assert_eq!(r.bailed_at, 1, "{insn}");
            assert_eq!(r.instruction_count, 1, "{insn}");
            assert_eq!(r.length, b.len());
            assert!(r.wildcard_mask.iter().all(|&w| !w), "{insn}");
        }
    }

    #[test]
    fn empty_input() {
        let r = build_pattern(&[], true);
        assert_eq!(r, PatternResult::default());
    }

    #[test]
    fn pattern_format() {
        let r = build_pattern(&[0x0A, 0xFF, 0x00], false);
        // 0A FF is OR r8, r/m8 with ModRM FF; 00 then bails (needs ModRM).
        assert_eq!(r.pattern, "0A FF 00");
        assert!(!r.pattern.ends_with(' '));
    }

    #[test]
    fn real_il2cpp_prologue() {
        // sub rsp,28 / cmp byte [rip+x],0 / jne +x / mov rcx,[rip+x] / call x / ...
        let r = check(
            "48 83 EC 28 80 3D 01 02 03 04 00 75 13 48 8B 0D 05 06 07 08 E8 09 0A 0B 0C C6 05 0D 0E 0F 10 01 48 83 C4 28 C3",
            "48 83 EC 28 80 3D ?? ?? ?? ?? 00 75 ?? 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? C6 05 ?? ?? ?? ?? 01 48 83 C4 28 C3",
        );
        assert_eq!(r.instruction_count, 8);
        assert_eq!(r.rel_branch_count, 2);
        assert_eq!(r.rip_rel_count, 3);
        assert!(!r.stopped_at_terminal);
    }

    // --- FFI ------------------------------------------------------------------

    fn call_ffi(input: &[u8], stop: i32, pcap: usize, mcap: usize) -> (i32, DcAobResult, Vec<u8>, Vec<u8>) {
        let mut pbuf = vec![0xEEu8; pcap];
        let mut mbuf = vec![0xEEu8; mcap];
        let mut out = DcAobResult { length: 99, ..Default::default() };
        let ptr = if input.is_empty() { std::ptr::null() } else { input.as_ptr() };
        // SAFETY: buffers sized as passed.
        let rc = unsafe {
            dc_aob_build_pattern(ptr, input.len(), stop, pbuf.as_mut_ptr().cast(), pcap, mbuf.as_mut_ptr(), mcap, &mut out)
        };
        (rc, out, pbuf, mbuf)
    }

    #[test]
    fn ffi_round_trip() {
        let input = hx("48 8B 05 01 02 03 04 C3 CC CC");
        let (rc, out, pbuf, mbuf) = call_ffi(&input, 1, 30, 10);
        assert_eq!(rc, 1);
        let want = build_pattern(&input, true);
        assert_eq!(out.length, 8);
        assert_eq!(out.requested_length, 10);
        assert_eq!(out.stopped_at_terminal, 1);
        assert_eq!(out.decoder_bailed, 0);
        assert_eq!(out.instruction_count, 2);
        assert_eq!(out.rip_rel_count, 1);
        assert_eq!(out.pattern_len, want.pattern.len());
        assert_eq!(&pbuf[..out.pattern_len], want.pattern.as_bytes());
        assert_eq!(&mbuf[..8], &[0, 0, 0, 1, 1, 1, 1, 0]);
        assert_eq!(mbuf[8], 0xEE); // not written past `length`

        // Any non-zero stop flag means true; 0 means raw window.
        let (_, out, ..) = call_ffi(&input, -7, 30, 10);
        assert_eq!(out.length, 8);
        let (_, out, ..) = call_ffi(&input, 0, 30, 10);
        assert_eq!(out.length, 10);
    }

    #[test]
    fn ffi_buffer_checks() {
        let input = hx("90 90");
        let (rc, out, ..) = call_ffi(&input, 1, 5, 2);
        assert_eq!(rc, 0);
        assert_eq!(out, DcAobResult::default());
        let (rc, out, ..) = call_ffi(&input, 1, 6, 1);
        assert_eq!(rc, 0);
        assert_eq!(out, DcAobResult::default());
        let (rc, ..) = call_ffi(&input, 1, 6, 2);
        assert_eq!(rc, 1);
        // Empty input with NULL bytes / zero caps.
        let mut out = DcAobResult { length: 5, ..Default::default() };
        // SAFETY: len 0, nothing is read or written except *out.
        let rc = unsafe {
            dc_aob_build_pattern(std::ptr::null(), 0, 1, std::ptr::null_mut(), 0, std::ptr::null_mut(), 0, &mut out)
        };
        assert_eq!(rc, 1);
        assert_eq!(out, DcAobResult::default());
        // NULL bytes with len > 0, NULL out.
        let mut p = [0u8; 6];
        let mut m = [0u8; 2];
        // SAFETY: bytes NULL is rejected before any read.
        let rc = unsafe {
            dc_aob_build_pattern(std::ptr::null(), 2, 1, p.as_mut_ptr().cast(), 6, m.as_mut_ptr(), 2, &mut out)
        };
        assert_eq!(rc, 0);
        // SAFETY: out NULL is rejected first.
        let rc = unsafe {
            dc_aob_build_pattern(input.as_ptr(), 2, 1, p.as_mut_ptr().cast(), 6, m.as_mut_ptr(), 2, std::ptr::null_mut())
        };
        assert_eq!(rc, 0);
        // 3*len overflow.
        // SAFETY: rejected before any read.
        let rc = unsafe {
            dc_aob_build_pattern(input.as_ptr(), usize::MAX, 1, p.as_mut_ptr().cast(), usize::MAX, m.as_mut_ptr(), usize::MAX, &mut out)
        };
        assert_eq!(rc, 0);
    }

    #[test]
    fn dc_aob_result_layout() {
        // size_t x7 + 2 bytes, padded to pointer alignment.
        let w = std::mem::size_of::<usize>();
        assert_eq!(std::mem::size_of::<DcAobResult>(), 8 * w);
        assert_eq!(std::mem::offset_of!(DcAobResult, pattern_len), 6 * w);
        assert_eq!(std::mem::offset_of!(DcAobResult, decoder_bailed), 7 * w);
        assert_eq!(std::mem::offset_of!(DcAobResult, stopped_at_terminal), 7 * w + 1);
    }

    /// Exhaustive-ish robustness: every 1-, 2- and 3-byte window decodes
    /// without panicking, keeps its invariants, and the FFI agrees with the
    /// pure function.
    #[test]
    fn invariants_small_windows() {
        let mut buf = Vec::new();
        for a in 0u16..256 {
            for b in (0u16..256).step_by(7) {
                for c in [0u8, 0x05, 0x25, 0x44, 0xC0, 0xFF] {
                    buf.clear();
                    buf.extend_from_slice(&[a as u8, b as u8, c, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);
                    for n in 0..=buf.len() {
                        for stop in [false, true] {
                            let r = build_pattern(&buf[..n], stop);
                            assert!(r.length <= n);
                            assert_eq!(r.wildcard_mask.len(), r.length);
                            assert_eq!(r.requested_length, n);
                            let expect_len = if r.length == 0 { 0 } else { 3 * r.length - 1 };
                            assert_eq!(r.pattern.len(), expect_len);
                            if r.stopped_at_terminal {
                                assert!(stop && r.length < n && !r.decoder_bailed);
                            }
                        }
                    }
                }
            }
        }
    }
}
