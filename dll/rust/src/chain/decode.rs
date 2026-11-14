//! Zydis -> iced-x86 adapter.
//!
//! The legacy detector asked Zydis 5.0 (commit 17bc6fb, vendored then at
//! `dll/vendor/zydis`; tools/zydis-oracle now fetches that exact copy,
//! decoder in `ZYDIS_MACHINE_MODE_LONG_64` with its default decoder modes —
//! MPX, CET, LZCNT, TZCNT, CLDEMOTE, IPREFETCH and **APX** on; KNC, AMD
//! branches, WBNOINVD, UD0-compat off) a handful of questions per
//! instruction. [`InsnDecoder::decode`] answers exactly those questions with
//! iced-x86 1.22, in Zydis' terms, and reduces the instruction to an [`Insn`]:
//!
//! | Legacy (Zydis)                                   | Here                                   |
//! |--------------------------------------------------|----------------------------------------|
//! | `ZydisDecoderDecodeFull` != SUCCESS              | `decode` returns `None`                |
//! | `instr.length`                                   | `Insn::len`                            |
//! | `mnemonic` MOV/LEA/MOVSXD/MOVZX/MOVSX/MOVSS/MOVSD| [`Mn`] (iced `Mnemonic`; string MOVSD is `Movsd` in both, its operands are hidden) |
//! | `meta.category` RET/UNCOND_BR/CALL/COND_BR       | [`Cat`]                                |
//! | `operand_count_visible`                          | `Insn::vis`                            |
//! | operand 0/1 type REGISTER/MEMORY/IMMEDIATE       | [`Operand`]                            |
//! | `Canonical(reg)` = LargestEnclosing(LONG_64)     | [`canonical`] / [`Reg`]                |
//! | `mem.base` / `mem.index != NONE` / `DispOrZero`  | `Operand::Mem`                         |
//! | visible REGISTER operands with ACTION_MASK_WRITE | `Insn::writes`                         |
//!
//! Mapping decisions (each one measured against that Zydis with the
//! differential oracle, now tools/zydis-oracle: exhaustive legacy map 0 / 0F / 0F38 / 0F3A x
//! prefix combinations x REX x ModRM, exhaustive VEX2, sampled VEX3 / EVEX /
//! XOP / 3DNow! / REX2 spaces and 160 M random byte strings — 0 differences):
//!
//! * **Registers.** Zydis' LargestEnclosing maps GPR8/16/32/64 (incl. AH..BH
//!   and SPL..DIL) to the 64-bit GPR, XMM/YMM/ZMM to ZMM, EIP/IP to RIP and
//!   leaves every other class unchanged; iced `full_register()` does the same
//!   except EIP, mapped explicitly. Zydis never returns NONE for a register
//!   that can appear here, so the "keep r when NONE" branch never fires.
//! * **Category.** From the Zydis definition tables: RET = ret/retf/iret/
//!   iretd/iretq (sysret/sysexit/rsm/uiret are other categories); UNCOND_BR =
//!   every jmp form (near/far/indirect) and XABORT (and APX JMPABS);
//!   CALL = call only (not syscall/vmcall/...); COND_BR = Jcc, jcxz/jecxz/
//!   jrcxz, loop/loope/loopne, XBEGIN, XEND (and KNC jkzd/jknzd). iced's
//!   `FlowControl` differs (XEND/XABORT/VM* calls), so the mnemonic decides.
//! * **Operand types.** iced `Register`, `Memory` and `Immediate*` /
//!   `NearBranch*` correspond to REGISTER, MEMORY and IMMEDIATE (Zydis
//!   reports relative branch targets as immediates). `FarBranch*` (POINTER)
//!   and the implicit string-memory kinds map to "other".
//! * **Visible operands.** iced's `op_count` equals Zydis' visible count
//!   except for string instructions, XLATB, MONITOR(X), MWAIT(X), whose
//!   operands are all hidden in Zydis (visible count 0).
//! * **Displacement.** iced reports the displacement of a 32-bit address as
//!   a zero-extended u32; Zydis sign-extends to i64, so it is sign-extended
//!   here. An absent displacement is 0 in both. (iced's RIP-relative
//!   "displacement" is the absolute target; RIP is never a tracked register,
//!   so it never takes part in a match.)
//! * **Writes.** iced `OpAccess::{Write, CondWrite, ReadWrite,
//!   ReadCondWrite}` on a visible register operand = Zydis
//!   ACTION_MASK_WRITE; plus the operands Zydis marks written and iced does
//!   not: RAX of the SEV-SNP instructions and the register operand of
//!   LWPINS / LWPVAL / LLWPCB.
//! * **Decoder disagreements** handled explicitly: PADLOCK ModRM sets,
//!   RDPRU / PCOMMIT / PTWRITE prefix forms, LOCK on those, VEX CMPccXADD and
//!   AMX-COMPLEX (Zydis rejects), TDPFP16PS register aliasing (Zydis
//!   accepts), the KNC instructions Zydis decodes from VEX with KNC mode off,
//!   and all of APX: REX2 (rewritten to REX + 0F and decoded by iced, then
//!   R4/X4/B4 applied, with Zydis' REX2 opcode exclusions, JMPABS and the
//!   REX2 0x90 = XCHG rule), EVEX B4/X4/R4 on AVX-512 instructions
//!   (rewritten and re-applied the same way), and EVEX map 4 plus the
//!   EVEX-promoted VEX GPR instructions (no iced support at all: answered
//!   from `apx_map4`, a table generated from the vendored Zydis).
//!
//! iced is always run with `DecoderOptions::MPX | NO_WBNOINVD`, mirroring
//! Zydis' MPX-on / WBNOINVD-off defaults.
//!
//! **Memory-operand facts** ([`InsnDecoder::decode_mem`], used by
//! `crate::xref`'s instance-field scan — an on-demand addition: `decode` and
//! [`Insn`] are computed exactly as before, the extra facts are only filled
//! while `decode_mem` runs). For every *visible* memory operand the legacy
//! `ScanInstanceAccesses` read `mem.disp.size != 0`, `mem.index != NONE`,
//! `mem.disp.value`, `Canonical(mem.base)` and `actions & WRITE / READ`, plus
//! `mnemonic == LEA`; [`MemFacts`] carries exactly that:
//!
//! * Displacement presence is iced's `memory_displ_size() != 0`; the value
//!   follows the same mapping as `Operand::Mem` above (EVEX disp8*N scaled by
//!   both decoders). Base / index use the same REX2 / EVEX B4 / X4 handling.
//! * Access: iced `Write` / `CondWrite` / `ReadWrite` / `ReadCondWrite` =
//!   Zydis WRITE, `Read` / `CondRead` = READ. iced reports *no* access for the
//!   address-only operands (`NoMemAccess`: NOP Ev, PREFETCHh, INVLPG,
//!   CLDEMOTE; `None`: NOP 0F 19-1F, UD0, UD1) where Zydis says READ — except
//!   the MPX BNDMK / BNDCL / BNDCU / BNDCN / BNDLDX / BNDSTX operands, which
//!   carry no action in Zydis either.
//! * The encodings iced cannot decode are answered from the bytes: APX map 4
//!   / EVEX-promoted VEX GPR memory forms (one visible operand, unscaled
//!   displacement, access from the Zydis-generated `xref::apx_mem` table) and
//!   the KNC VPREFETCH* / CLEVICT0/1 memory forms Zydis decodes from VEX (one
//!   READ operand).
//!
//! Verified against that Zydis with tools/zydis-oracle (projection =
//! the exact hit list `ScanInstanceAccesses` builds per instruction):
//! exhaustive legacy maps 0 / 0F / 0F38 / 0F3A x 14 prefix sets x 9 REX x
//! ModRM x 4 SIB tails (132 M), exhaustive VEX2 (33.5 M), VEX3 + XOP (84 M),
//! REX2 (21 M), APX EVEX memory forms (21 M), sampled EVEX (1.6 M) and 99 M
//! random strings over the R2 generator modes (two seeds) — 0 differences.
//! `decode` itself was A/B-checked against its previous revision on 108 M
//! random strings, interleaved with `decode_mem` calls: identical.

use iced_x86::{
    Decoder, DecoderOptions, Instruction, InstructionInfoFactory, InstructionInfoOptions, Mnemonic, OpAccess, OpKind,
    Register,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Mn {
    Other,
    Mov,
    Lea,
    Movsxd,
    Movzx,
    Movsx,
    Movss,
    Movsd,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Cat {
    Other,
    Ret,
    UncondBr,
    Call,
    CondBr,
}

/// A canonical (largest-enclosing) register.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Reg {
    None,
    R(Register),
    /// APX extended GPR r16..r31 (iced has no such registers).
    Egpr(u8),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Operand {
    Unused,
    Reg(Reg),
    Mem { base: Reg, has_index: bool, disp: i64 },
    Imm,
    Other,
}

#[derive(Clone, Copy, Debug)]
pub(crate) struct Insn {
    pub len: usize,
    pub mn: Mn,
    pub cat: Cat,
    pub vis: usize,
    pub ops: [Operand; 2],
    pub writes: [Reg; 5],
    pub n_writes: usize,
}

impl Insn {
    fn bare(len: usize) -> Insn {
        Insn {
            len,
            mn: Mn::Other,
            cat: Cat::Other,
            vis: 0,
            ops: [Operand::Unused; 2],
            writes: [Reg::None; 5],
            n_writes: 0,
        }
    }
}

/// Zydis' access to a memory operand, as `IL2CPPFieldXref`'s instance scan
/// tests it: `actions & ACTION_MASK_WRITE` (write / conditional write /
/// read-write), else `actions & ACTION_MASK_READ`, else neither.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum MemAct {
    None,
    Read,
    Write,
}

/// One visible MEMORY operand in Zydis' terms.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct MemOperand {
    /// `Canonical(mem.base)` (EIP -> RIP, r16..r31 as [`Reg::Egpr`]).
    pub base: Reg,
    /// `mem.index != NONE` (VSIB included).
    pub has_index: bool,
    /// `mem.disp.size != 0`: the encoding carries a displacement (even 0).
    pub has_disp: bool,
    /// `mem.disp.value`: sign-extended, already scaled for EVEX disp8*N.
    pub disp: i64,
    pub act: MemAct,
}

/// What the instance-field xref scan reads from one decoded instruction:
/// `mnemonic == LEA` and the visible memory operands, in operand order.
/// Filled by [`InsnDecoder::decode_mem`] only (additive to [`Insn`]: the
/// chain detector's facts are untouched).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct MemFacts {
    pub lea: bool,
    pub n: usize,
    pub ops: [MemOperand; 5],
}

impl MemFacts {
    pub const EMPTY: MemFacts = MemFacts {
        lea: false,
        n: 0,
        ops: [MemOperand { base: Reg::None, has_index: false, has_disp: false, disp: 0, act: MemAct::None }; 5],
    };

    fn push(&mut self, op: MemOperand) {
        if self.n < self.ops.len() {
            self.ops[self.n] = op;
            self.n += 1;
        }
    }

    pub fn operands(&self) -> &[MemOperand] {
        &self.ops[..self.n]
    }
}

pub(crate) fn canonical(r: Register) -> Reg {
    match r {
        Register::None => Reg::None,
        Register::EIP => Reg::R(Register::RIP),
        _ => Reg::R(r.full_register()),
    }
}

const DECODER_OPTIONS: u32 = DecoderOptions::MPX | DecoderOptions::NO_WBNOINVD;
const MAX_LEN: usize = 15;

fn is_rex(b: u8) -> bool {
    b & 0xF0 == 0x40
}

fn gpr64(id: u8) -> Register {
    const T: [Register; 16] = [
        Register::RAX,
        Register::RCX,
        Register::RDX,
        Register::RBX,
        Register::RSP,
        Register::RBP,
        Register::RSI,
        Register::RDI,
        Register::R8,
        Register::R9,
        Register::R10,
        Register::R11,
        Register::R12,
        Register::R13,
        Register::R14,
        Register::R15,
    ];
    T[(id & 15) as usize]
}

fn gpr_number(r: Register) -> Option<u8> {
    if r.is_gpr() {
        let full = r.full_register();
        Some((full as u32 - Register::RAX as u32) as u8)
    } else {
        None
    }
}

/// Legacy-prefix facts, collected the way Zydis' `ZydisCollectOptionalPrefixes` does.
#[derive(Clone, Copy, Default)]
struct Pfx {
    /// Index of the first byte that is neither a legacy prefix nor REX.
    k: usize,
    has_66: bool,
    /// Last F2/F3 seen, else 0.
    group1: u8,
    has_67: bool,
    has_lock: bool,
}

impl Pfx {
    fn scan(bytes: &[u8]) -> Pfx {
        let lim = bytes.len().min(MAX_LEN);
        let mut p = Pfx::default();
        while p.k < lim {
            match bytes[p.k] {
                0x66 => p.has_66 = true,
                0x67 => p.has_67 = true,
                0xF0 => p.has_lock = true,
                0xF2 | 0xF3 => p.group1 = bytes[p.k],
                0x2E | 0x36 | 0x3E | 0x26 | 0x64 | 0x65 => {}
                b if is_rex(b) => {}
                _ => break,
            }
            p.k += 1;
        }
        p
    }

    /// Zydis' effective mandatory prefix: the last F2/F3, else 66, else none.
    fn mandatory(&self) -> u8 {
        if self.group1 != 0 {
            self.group1
        } else if self.has_66 {
            0x66
        } else {
            0
        }
    }
}

/// APX register-extension bits (REX2 / EVEX) that iced cannot express.
#[derive(Clone, Copy, Default)]
struct ApxExt {
    r4: bool,
    x4: bool,
    b4: bool,
    /// (byte index, bit) in the rewritten buffer whose flip changes the
    /// ModRM.reg-encoded register (R3) resp. the ModRM.rm / base / opcode
    /// register (B3).
    r_toggle: (usize, u8),
    b_toggle: (usize, u8),
    /// ModRM byte of the original instruction (if present in the buffer).
    modrm: Option<u8>,
}

/// Length of ModRM + SIB + displacement starting at `m` (64-bit mode; the
/// 32-bit addressing selected by 67 has the same layout).
fn modrm_len(bytes: &[u8], m: usize) -> Option<usize> {
    let modrm = *bytes.get(m)?;
    let md = modrm >> 6;
    let rm = modrm & 7;
    if md == 3 {
        return Some(1);
    }
    let mut n = 1;
    let mut base5 = rm == 5;
    if rm == 4 {
        let sib = *bytes.get(m + 1)?;
        n += 1;
        base5 = sib & 7 == 5;
    }
    n += match md {
        0 if base5 => 4,
        0 => 0,
        1 => 1,
        _ => 4,
    };
    Some(n)
}

/// VEX encodings where the vendored Zydis and iced disagree on validity:
/// the Knights Corner instructions Zydis decodes from plain VEX although KNC
/// mode is off, and TDPFP16PS register aliasing. Returns `Some(result)` when
/// `bytes` is one of them (exact prefix / ModRM sets measured against the
/// vendored Zydis), `None` to fall through to the normal decode.
fn vex_quirks(bytes: &[u8], pfx: Pfx) -> Option<Option<Insn>> {
    let k = pfx.k;
    // Zydis rejects VEX after REX / LOCK / 66 / F2 / F3 (iced does too).
    if pfx.has_lock || pfx.has_66 || pfx.group1 != 0 || (k > 0 && is_rex(bytes[k - 1])) {
        return None;
    }
    let vex3 = bytes[k] == 0xC4;
    let b1 = *bytes.get(k + 1)?;
    let (map, w, vvvv_inv, l, pp, hdr) = if vex3 {
        let b2 = *bytes.get(k + 2)?;
        (b1 & 0x1F, b2 >> 7, (b2 >> 3) & 0xF, (b2 >> 2) & 1, b2 & 3, 3)
    } else {
        (1, 0, (b1 >> 3) & 0xF, (b1 >> 2) & 1, b1 & 3, 2)
    };
    // VEX.R is stored inverted.
    let r_bar_set = b1 >> 7 != 0;
    let op_at = k + hdr;
    let op = *bytes.get(op_at)?;
    let modrm = bytes.get(op_at + 1).copied();
    let mod3 = modrm.is_some_and(|m| m >> 6 == 3);
    let finish = |len: usize, mut insn: Insn| -> Option<Option<Insn>> {
        if len > MAX_LEN || len > bytes.len() {
            return Some(None);
        }
        insn.len = len;
        Some(Some(insn))
    };
    let gpr_write = |len: usize| -> Option<Option<Insn>> {
        let m = modrm?;
        let id = (u8::from(!r_bar_set) << 3) | ((m >> 3) & 7);
        let mut insn = Insn::bare(len);
        insn.writes[0] = canonical(gpr64(id));
        insn.n_writes = 1;
        finish(len, insn)
    };
    let cond_br = |len: usize| -> Option<Option<Insn>> {
        let mut insn = Insn::bare(len);
        insn.cat = Cat::CondBr;
        finish(len, insn)
    };
    // vvvv (inverted) selects a mask register k0..k7.
    let k_mask_vvvv = vvvv_inv & 0x8 != 0;
    match (map, op) {
        // VPREFETCH* (memory forms only).
        (1, 0x18) if vvvv_inv == 0xF && l == 0 && pp == 0 && modrm.is_some() && !mod3 => {
            let len = op_at + 1 + modrm_len(bytes, op_at + 1)?;
            finish(len, Insn::bare(len))
        }
        // KAND / KANDN / KANDNR / KOR / KXNOR / KXOR / KMERGE2L1H / KMERGE2L1L.
        (1, 0x41 | 0x42 | 0x43 | 0x45 | 0x46 | 0x47 | 0x48 | 0x49)
            if r_bar_set && w == 0 && vvvv_inv == 0xF && l == 0 && pp == 0 && mod3 =>
        {
            finish(op_at + 2, Insn::bare(op_at + 2))
        }
        // JKZD / JKNZD rel32.
        (1, 0x84 | 0x85) if w == 0 && k_mask_vvvv && l == 0 && pp == 0 => cond_br(op_at + 5),
        // JKZD / JKNZD rel8 (VEX map 0).
        (0, 0x74 | 0x75) if vex3 && w == 0 && k_mask_vvvv && l == 0 && pp == 0 => cond_br(op_at + 2),
        // KCONCATH / KCONCATL: GPR destination in ModRM.reg.
        (1, 0x95 | 0x97) if w == 0 && k_mask_vvvv && l == 0 && pp == 0 && mod3 => gpr_write(op_at + 2),
        // CLEVICT0 / CLEVICT1 (memory /7) and the register form /6.
        (1, 0xAE) if vvvv_inv == 0xF && l == 0 && (pp == 2 || pp == 3) && modrm.is_some() => {
            let m = modrm?;
            let reg = (m >> 3) & 7;
            if (!mod3 && reg == 7) || (mod3 && reg == 6) {
                let len = op_at + 1 + modrm_len(bytes, op_at + 1)?;
                finish(len, Insn::bare(len))
            } else {
                None
            }
        }
        // POPCNT / LZCNT (F3) and TZCNT / TZCNTI (F3 / F2), register forms.
        (1, 0xB8 | 0xBD) if vvvv_inv == 0xF && l == 0 && pp == 2 && mod3 => gpr_write(op_at + 2),
        (1, 0xBC) if vvvv_inv == 0xF && l == 0 && (pp == 2 || pp == 3) && mod3 => gpr_write(op_at + 2),
        // KEXTRACT imm8.
        (3, 0x3E) if vex3 && r_bar_set && w == 0 && vvvv_inv == 0xF && l == 0 && pp == 1 && mod3 => {
            finish(op_at + 3, Insn::bare(op_at + 3))
        }
        // TDPFP16PS (AMX-FP16): Zydis does not require the three tile
        // registers to differ (iced does); tmm0..7 only (R = B = 0, vvvv < 8).
        (2, 0x5C) if vex3 && r_bar_set && b1 & 0x20 != 0 && w == 0 && k_mask_vvvv && l == 0 && pp == 3 && mod3 => {
            finish(op_at + 2, Insn::bare(op_at + 2))
        }
        _ => None,
    }
}

/// APX EVEX encodings iced cannot decode at all — map 4 (promoted legacy
/// instructions) and the EVEX-promoted VEX GPR instructions listed in
/// `apx_map4::EXTRA` — answered from the table generated from the vendored
/// Zydis. None of these instructions is a MOV-family mnemonic or a branch, so
/// only validity, length and the written GPRs matter. Returns None when the
/// encoding is not one of them (normal EVEX path).
fn decode_evex_apx_table(bytes: &[u8], pfx: Pfx) -> Option<Option<Insn>> {
    use super::apx_map4::{EXTRA, GROUP};
    let k = pfx.k;
    let p0 = *bytes.get(k + 1)?;
    let mmm = p0 & 0x07;
    let op = *bytes.get(k + 4)?;
    let row: &[u8] = if mmm == 4 {
        let base = usize::from(op) << 4;
        &GROUP[base..base + 16]
    } else {
        &EXTRA.iter().find(|(m, o, _)| *m == mmm && *o == op)?.2
    };
    Some(decode_apx_row(bytes, pfx, row))
}

/// The memory operand encoded by the ModRM byte at `m` (+ SIB +
/// displacement), Zydis style, for the encodings iced cannot answer. `b3` /
/// `x3` / `b4` / `x4` are the (already un-inverted) register-extension bits
/// of the prefix; the displacement is never scaled. `None` for a register
/// form (mod = 3).
fn modrm_mem_operand(bytes: &[u8], m: usize, (b3, x3, b4, x4): (u8, u8, u8, u8), act: MemAct) -> Option<MemOperand> {
    let modrm = *bytes.get(m)?;
    let md = modrm >> 6;
    if md == 3 {
        return None;
    }
    let rm = modrm & 7;
    let gpr = |id: u8| if id >= 16 { Reg::Egpr(id) } else { canonical(gpr64(id)) };
    let disp8 = |at: usize| bytes.get(at).map_or(0, |&b| i64::from(b as i8));
    let disp32 = |at: usize| match bytes.get(at..at + 4) {
        Some(d) => i64::from(i32::from_le_bytes([d[0], d[1], d[2], d[3]])),
        None => 0,
    };
    let (base, has_index, disp_at, disp32_form) = if rm == 4 {
        let sib = *bytes.get(m + 1)?;
        let base_f = sib & 7;
        let idx = (x4 << 4) | (x3 << 3) | ((sib >> 3) & 7);
        let base = if base_f == 5 && md == 0 { Reg::None } else { gpr((b4 << 4) | (b3 << 3) | base_f) };
        (base, idx != 4, m + 2, base_f == 5 && md == 0)
    } else if rm == 5 && md == 0 {
        (Reg::R(Register::RIP), false, m + 1, true)
    } else {
        (gpr((b4 << 4) | (b3 << 3) | rm), false, m + 1, false)
    };
    let (has_disp, disp) = match md {
        1 => (true, disp8(disp_at)),
        2 => (true, disp32(disp_at)),
        _ if disp32_form => (true, disp32(disp_at)),
        _ => (false, 0),
    };
    Some(MemOperand { base, has_index, has_disp, disp, act })
}

/// Memory-operand facts of an APX EVEX encoding answered by
/// [`decode_evex_apx_table`] (only called once that decode succeeded, so the
/// ModRM / SIB / displacement bytes are inside `bytes`). Register forms have
/// no memory operand. Zydis decodes the single memory operand from ModRM /
/// SIB extended by EVEX.B (inverted) + B4 (base) and EVEX.X (inverted) + X4
/// (= !EVEX.U, index); the displacement is never scaled (disp8 * 1); the
/// access comes from the table generated from Zydis (`xref::apx_mem`).
fn apx_mem_facts(bytes: &[u8], pfx: Pfx) -> MemFacts {
    let k = pfx.k;
    let mut f = MemFacts::EMPTY;
    let (Some(&p0), Some(&p1), Some(&p2), Some(&op), Some(&modrm)) =
        (bytes.get(k + 1), bytes.get(k + 2), bytes.get(k + 3), bytes.get(k + 4), bytes.get(k + 5))
    else {
        return f;
    };
    let reg = (modrm >> 3) & 7;
    let ext = ((!p0 >> 5) & 1, (!p0 >> 6) & 1, (p0 >> 3) & 1, (!p1 >> 2) & 1);
    let sel = ((p1 & 3) << 3) | ((p1 >> 7) << 2) | (((p2 >> 4) & 1) << 1) | ((p2 >> 2) & 1);
    let act = crate::xref::apx_mem::mem_act(p0 & 7, op, reg, sel);
    if let Some(op) = modrm_mem_operand(bytes, k + 5, ext, act) {
        f.push(op);
    }
    f
}

/// Memory-operand facts of an encoding answered by [`vex_quirks`] (only
/// called once it succeeded). The only memory forms there are the Knights
/// Corner VPREFETCH* (VEX map 1, 0x18) and CLEVICT0 / CLEVICT1 (map 1, 0xAE
/// /7): one visible memory operand, READ, unscaled displacement, base / index
/// extended by VEX.B / VEX.X (inverted; VEX2 has neither).
fn vex_quirk_mem_facts(bytes: &[u8], pfx: Pfx) -> MemFacts {
    let k = pfx.k;
    let mut f = MemFacts::EMPTY;
    let vex3 = bytes.get(k) == Some(&0xC4);
    let Some(&b1) = bytes.get(k + 1) else { return f };
    let (map, op_at, ext) = if vex3 {
        (b1 & 0x1F, k + 3, ((!b1 >> 5) & 1, (!b1 >> 6) & 1, 0, 0))
    } else {
        (1, k + 2, (0, 0, 0, 0))
    };
    if map == 1
        && matches!(bytes.get(op_at), Some(0x18 | 0xAE))
        && let Some(op) = modrm_mem_operand(bytes, op_at + 1, ext, MemAct::Read)
    {
        f.push(op);
    }
    f
}

fn decode_apx_row(bytes: &[u8], pfx: Pfx, row: &[u8]) -> Option<Insn> {
    use super::apx_map4::PROFILES;
    let k = pfx.k;
    // Zydis rejects EVEX after REX / LOCK / 66 / F2 / F3.
    if pfx.has_lock || pfx.has_66 || pfx.group1 != 0 || (k > 0 && is_rex(bytes[k - 1])) {
        return None;
    }
    let p0 = *bytes.get(k + 1)?;
    let p1 = *bytes.get(k + 2)?;
    let p2 = *bytes.get(k + 3)?;
    let modrm = *bytes.get(k + 5)?;
    let mod3 = modrm >> 6 == 3;
    let reg = (modrm >> 3) & 7;
    // EVEX.z, EVEX.L'L must be 0; EVEX.U must be 1 for register forms (it is
    // X4 for memory forms).
    if p2 & 0xE0 != 0 || (mod3 && p1 & 0x04 == 0) {
        return None;
    }
    let pp = p1 & 3;
    let w = p1 >> 7;
    let nd = (p2 >> 4) & 1;
    let nf = (p2 >> 2) & 1;
    let group = row[(usize::from(mod3) << 3) | usize::from(reg)];
    let e = PROFILES[usize::from(group)][usize::from((pp << 3) | (w << 2) | (nd << 1) | nf)];
    if e & 0x8000 == 0 {
        return None;
    }
    // EVEX.R / R' are stored inverted: R = !P0[7], R' = !P0[4].
    if (e & 0x1000 != 0 && p0 & 0x80 == 0) || (e & 0x2000 != 0 && p0 & 0x10 == 0) {
        return None;
    }
    let vvvv = (!p1 >> 3) & 0xF;
    let v4 = (!p2 >> 3) & 1;
    let scc = e & 0x2 != 0;
    if !scc {
        // aaa[1:0] must be 0; an unused vvvv must be 0 with V' = 1.
        if p2 & 0x03 != 0 || (e & 0x1 == 0 && (vvvv != 0 || v4 != 0)) {
            return None;
        }
    }
    let imm = [0usize, 1, 2, 4][usize::from((e >> 2) & 3)];
    let len = k + 5 + modrm_len(bytes, k + 5)? + imm;
    if len > MAX_LEN || len > bytes.len() {
        return None;
    }
    let gpr = |id: u8| if id >= 16 { Reg::Egpr(id) } else { canonical(gpr64(id)) };
    let mut insn = Insn::bare(len);
    let mut push = |r: Reg| {
        if insn.n_writes < insn.writes.len() && !insn.writes[..insn.n_writes].contains(&r) {
            insn.writes[insn.n_writes] = r;
            insn.n_writes += 1;
        }
    };
    // EVEX.R / R' / B (inverted) and B4 (plain) extend ModRM.reg / ModRM.rm.
    if e & 0x10 != 0 {
        push(gpr((((!p0 >> 4) & 1) << 4) | (((!p0 >> 7) & 1) << 3) | reg));
    }
    if e & 0x20 != 0 && mod3 {
        push(gpr((((p0 >> 3) & 1) << 4) | (((!p0 >> 5) & 1) << 3) | (modrm & 7)));
    }
    if e & 0x40 != 0 {
        push(gpr((v4 << 4) | vvvv));
    }
    let lit = (e >> 7) & 0x1F;
    if lit != 0 {
        push(gpr((lit - 1) as u8));
    }
    Some(insn)
}

fn is_cmpccxadd(m: Mnemonic) -> bool {
    matches!(
        m,
        Mnemonic::Cmpbexadd
            | Mnemonic::Cmpbxadd
            | Mnemonic::Cmplexadd
            | Mnemonic::Cmplxadd
            | Mnemonic::Cmpnbexadd
            | Mnemonic::Cmpnbxadd
            | Mnemonic::Cmpnlexadd
            | Mnemonic::Cmpnlxadd
            | Mnemonic::Cmpnoxadd
            | Mnemonic::Cmpnpxadd
            | Mnemonic::Cmpnsxadd
            | Mnemonic::Cmpnzxadd
            | Mnemonic::Cmpoxadd
            | Mnemonic::Cmppxadd
            | Mnemonic::Cmpsxadd
            | Mnemonic::Cmpzxadd
    )
}

/// True when flipping the R3 bit changes a GPR operand, i.e. ModRM.reg
/// encodes a general-purpose register.
fn reg_field_has_gpr(ins: &Instruction, buf: &[u8], (pos, bit): (usize, u8)) -> bool {
    let mut alt = [0u8; 32];
    alt[..buf.len()].copy_from_slice(buf);
    alt[pos] ^= bit;
    let Some(t) = iced_decode(&alt[..buf.len()]) else { return false };
    (0..ins.op_count().min(5)).any(|i| {
        ins.op_kind(i) == OpKind::Register
            && t.op_kind(i) == OpKind::Register
            && ins.op_register(i).is_gpr()
            && t.op_register(i) != ins.op_register(i)
    })
}

fn iced_decode(bytes: &[u8]) -> Option<Instruction> {
    let mut dec = Decoder::new(64, bytes, DECODER_OPTIONS);
    let ins = dec.decode();
    if ins.is_invalid() { None } else { Some(ins) }
}

pub(crate) struct InsnDecoder {
    factory: InstructionInfoFactory,
    /// Set only for the duration of [`InsnDecoder::decode_mem`]: the decode
    /// paths then also fill `facts`. The chain detector never sets it, so
    /// its [`Insn`] results are computed exactly as before.
    collect: bool,
    facts: MemFacts,
}

impl InsnDecoder {
    pub fn new() -> Self {
        Self { factory: InstructionInfoFactory::new(), collect: false, facts: MemFacts::EMPTY }
    }

    /// The instance-field xref scan's view of one instruction (see
    /// [`MemFacts`]): `None` exactly when [`InsnDecoder::decode`] fails (=
    /// `ZydisDecoderDecodeFull` fails), else the Zydis length and the
    /// visible memory operands.
    pub fn decode_mem(&mut self, bytes: &[u8]) -> Option<(usize, MemFacts)> {
        self.collect = true;
        self.facts = MemFacts::EMPTY;
        let r = self.decode(bytes);
        self.collect = false;
        r.map(|i| (i.len, self.facts))
    }

    pub fn decode(&mut self, bytes: &[u8]) -> Option<Insn> {
        let pfx = Pfx::scan(bytes);
        if pfx.k < bytes.len().min(MAX_LEN) && bytes[pfx.k] == 0xD5 {
            return self.decode_rex2(bytes, pfx);
        }
        if pfx.k < bytes.len().min(MAX_LEN) && bytes[pfx.k] == 0x62 && bytes.len() >= pfx.k + 4 {
            return self.decode_evex(bytes, pfx);
        }
        if pfx.k < bytes.len().min(MAX_LEN)
            && matches!(bytes[pfx.k], 0xC4 | 0xC5)
            && let Some(r) = vex_quirks(bytes, pfx)
        {
            if self.collect && r.is_some() {
                self.facts = vex_quirk_mem_facts(bytes, pfx);
            }
            return r;
        }
        self.decode_legacy(bytes, pfx, 0, None)
    }

    /// Decodes a buffer without REX2. `extra` is added to the length (the
    /// REX2 rewrite can shorten the buffer by one byte).
    fn decode_legacy(&mut self, buf: &[u8], pfx: Pfx, extra: usize, rex2: Option<ApxExt>) -> Option<Insn> {
        let k = pfx.k;
        let mut add_rax_write = false;
        if buf.get(k) == Some(&0x0F) && k + 2 < buf.len() {
            let op = buf[k + 1];
            let modrm = buf[k + 2];
            let mod3 = modrm >> 6 == 3;
            let reg = (modrm >> 3) & 7;
            let mand = pfx.mandatory();
            let manual = |valid: bool| -> Option<Insn> {
                let len = k + 3 + extra;
                // These instructions do not accept LOCK in Zydis.
                if !valid || len > MAX_LEN || pfx.has_lock {
                    return None;
                }
                Some(Insn::bare(len))
            };
            // Padlock (0F A6 / 0F A7, register form): Zydis knows exactly
            // these ModRM values; iced accepts whole ranges.
            if (op == 0xA6 || op == 0xA7) && mod3 {
                let valid = match (op, mand) {
                    (0xA6, 0xF3) => matches!(modrm, 0xC0 | 0xC8 | 0xD0),
                    (0xA7, 0x00 | 0x66) => modrm == 0xC0,
                    (0xA7, 0xF3) => matches!(modrm, 0xC0 | 0xC8 | 0xD0 | 0xD8 | 0xE0 | 0xE8),
                    _ => false,
                };
                return manual(valid);
            }
            // RDPRU (0F 01 FD) with a 66 / F2 mandatory prefix.
            if op == 0x01 && modrm == 0xFD && (mand == 0x66 || mand == 0xF2) {
                return manual(true);
            }
            // PCOMMIT (66 0F AE /7, register form): Zydis ignores ModRM.rm.
            if op == 0xAE && mod3 && reg == 7 && mand == 0x66 {
                return manual(true);
            }
            // PTWRITE (F3 0F AE /4) with an additional 66.
            if op == 0xAE && reg == 4 && mand == 0xF3 && pfx.has_66 {
                let mut tmp = [0u8; 32];
                let mut n = 0;
                let mut removed = 0;
                for (i, &b) in buf.iter().enumerate().take(MAX_LEN + 1) {
                    if i < k && b == 0x66 {
                        removed += 1;
                        continue;
                    }
                    tmp[n] = b;
                    n += 1;
                }
                let ins = iced_decode(&tmp[..n])?;
                let len = ins.len() + removed + extra;
                if len > MAX_LEN {
                    return None;
                }
                return self.adapt(&ins, len, None, false);
            }
            // SEV-SNP instructions (F2/F3 0F 01 FE/FF) write RAX.
            if op == 0x01 && matches!(modrm, 0xFE | 0xFF) && (mand == 0xF2 || mand == 0xF3) {
                add_rax_write = true;
            }
        }
        let ins = iced_decode(buf)?;
        let len = ins.len() + extra;
        if len > MAX_LEN {
            return None;
        }
        self.adapt(&ins, len, rex2.map(|e| (e, buf)), add_rax_write)
    }

    fn decode_evex(&mut self, bytes: &[u8], pfx: Pfx) -> Option<Insn> {
        let k = pfx.k;
        let p0 = bytes[k + 1];
        let p1 = bytes[k + 2];
        if let Some(r) = decode_evex_apx_table(bytes, pfx) {
            if self.collect && r.is_some() {
                self.facts = apx_mem_facts(bytes, pfx);
            }
            return r;
        }
        let b4 = p0 & 0x08 != 0;
        let r4 = p0 & 0x10 == 0;
        // EVEX.U doubles as X4 only for memory forms (ModRM.mod != 3); for
        // register forms U stays a plain "must be 1" bit (Zydis EVEX_U node).
        let mem_form = bytes.get(k + 5).is_some_and(|m| m >> 6 != 3);
        let x4 = mem_form && p1 & 0x04 == 0;
        let mut tmp = [0u8; 32];
        let n = bytes.len().min(k + MAX_LEN + 1);
        tmp[..n].copy_from_slice(&bytes[..n]);
        tmp[k + 1] &= !0x08;
        if mem_form {
            tmp[k + 2] |= 0x04;
        }
        // iced rejects EVEX.R' = 0 (R4) when ModRM.reg names a GPR; Zydis
        // reads it as r16..r31. Retry with R' = 1 and require a GPR there.
        let mut need_gpr_reg = false;
        let ins = match iced_decode(&tmp[..n]) {
            Some(i) => i,
            None if r4 => {
                tmp[k + 1] |= 0x10;
                need_gpr_reg = true;
                iced_decode(&tmp[..n])?
            }
            None => return None,
        };
        let tmp = &tmp[..n];
        if need_gpr_reg && !reg_field_has_gpr(&ins, tmp, (k + 1, 0x80)) {
            return None;
        }
        let ext = ApxExt {
            r4,
            x4,
            b4,
            r_toggle: (k + 1, 0x80),
            b_toggle: (k + 1, 0x20),
            modrm: bytes.get(k + 5).copied(),
        };
        let len = ins.len();
        self.adapt(&ins, len, Some((ext, tmp)), false)
    }

    fn decode_rex2(&mut self, bytes: &[u8], pfx: Pfx) -> Option<Insn> {
        let k = pfx.k;
        if k > 0 && is_rex(bytes[k - 1]) {
            return None;
        }
        let pl = *bytes.get(k + 1)?;
        let op = *bytes.get(k + 2)?;
        let m0 = pl >> 7 != 0;
        let w = (pl >> 3) & 1 != 0;
        if !m0 {
            if op == 0xA1 {
                // JMPABS imm64.
                if w || pfx.has_66 || pfx.group1 != 0 || pfx.has_67 || pfx.has_lock {
                    return None;
                }
                let len = k + 3 + 8;
                if len > MAX_LEN || len > bytes.len() {
                    return None;
                }
                let mut insn = Insn::bare(len);
                insn.cat = Cat::UncondBr;
                insn.vis = 1;
                insn.ops[0] = Operand::Imm;
                return Some(insn);
            }
            if op == 0x90 {
                // XCHG r, rAX for every B value (no NOP form); PAUSE with F3.
                let len = k + 3;
                if len > MAX_LEN || pfx.has_lock {
                    return None;
                }
                let mut insn = Insn::bare(len);
                let id = (((pl >> 4) & 1) << 4) | ((pl & 1) << 3);
                if pfx.group1 != 0xF3 && id != 0 {
                    let r = if id >= 16 { Reg::Egpr(id) } else { canonical(gpr64(id)) };
                    let rax = Reg::R(Register::RAX);
                    insn.vis = 2;
                    insn.ops = [Operand::Reg(r), Operand::Reg(rax)];
                    insn.writes[0] = r;
                    insn.writes[1] = rax;
                    insn.n_writes = 2;
                }
                return Some(insn);
            }
            // Zydis' REX2 exclusions in map 0 (measured: identical to the APX
            // spec): rows 4/7/A/E, the 0F escape, legacy prefixes as opcodes,
            // VEX/EVEX escapes and a second REX2.
            if matches!(op >> 4, 0x4 | 0x7 | 0xA | 0xE)
                || matches!(op, 0x0F | 0x26 | 0x2E | 0x36 | 0x3E | 0x64 | 0x65 | 0x66 | 0x67 | 0xF0 | 0xF2 | 0xF3)
                || matches!(op, 0xC4 | 0xC5 | 0x62 | 0xD5)
            {
                return None;
            }
        } else {
            // Map 1 exclusions: 3-byte escapes (and 0F 0F), 0F 30-37
            // (WRMSR..GETSEC), Jcc rel32. (MOV CR/DR stays valid unless R4.)
            if matches!(op, 0x0F | 0x38 | 0x3A) || matches!(op, 0x30..=0x37 | 0x80..=0x8F) {
                return None;
            }
            // XSAVE / XRSTOR / XSAVEOPT (0F AE /4-/6) and XRSTORS / XSAVEC /
            // XSAVES (0F C7 /3-/5), memory forms without a mandatory prefix.
            if matches!(op, 0xAE | 0xC7)
                && let Some(&modrm) = bytes.get(k + 3)
            {
                let reg = (modrm >> 3) & 7;
                let mem = modrm >> 6 != 3;
                let has_mandatory = pfx.has_66 || pfx.group1 != 0;
                if !has_mandatory
                    && mem
                    && ((op == 0xAE && (4..=6).contains(&reg)) || (op == 0xC7 && (3..=5).contains(&reg)))
                {
                    return None;
                }
            }
        }
        let mut tmp = [0u8; 32];
        let mut n = 0;
        for &b in &bytes[..k] {
            tmp[n] = b;
            n += 1;
        }
        let rex_pos = n;
        tmp[n] = 0x40 | (pl & 0x0F);
        n += 1;
        if m0 {
            tmp[n] = 0x0F;
            n += 1;
        }
        let rest = &bytes[k + 2..bytes.len().min(k + 2 + MAX_LEN)];
        tmp[n..n + rest.len()].copy_from_slice(rest);
        n += rest.len();
        let tmp = &tmp[..n];
        let ext = ApxExt {
            r4: (pl >> 6) & 1 != 0,
            x4: (pl >> 5) & 1 != 0,
            b4: (pl >> 4) & 1 != 0,
            r_toggle: (rex_pos, 0x04),
            b_toggle: (rex_pos, 0x01),
            modrm: bytes.get(k + 3).copied(),
        };
        let tpfx = Pfx::scan(tmp);
        self.decode_legacy(tmp, tpfx, usize::from(!m0), Some(ext))
    }

    fn adapt(
        &mut self,
        ins: &Instruction,
        len: usize,
        rex2: Option<(ApxExt, &[u8])>,
        add_rax_write: bool,
    ) -> Option<Insn> {
        // The vendored Zydis rejects every VEX CMPccXADD encoding.
        // ... and has no AMX-COMPLEX (TCMMIMFP16PS / TCMMRLFP16PS).
        if is_cmpccxadd(ins.mnemonic()) || matches!(ins.mnemonic(), Mnemonic::Tcmmimfp16ps | Mnemonic::Tcmmrlfp16ps) {
            return None;
        }
        let mn = match ins.mnemonic() {
            Mnemonic::Mov => Mn::Mov,
            Mnemonic::Lea => Mn::Lea,
            Mnemonic::Movsxd => Mn::Movsxd,
            Mnemonic::Movzx => Mn::Movzx,
            Mnemonic::Movsx => Mn::Movsx,
            Mnemonic::Movss => Mn::Movss,
            Mnemonic::Movsd => Mn::Movsd,
            _ => Mn::Other,
        };
        let cat = match ins.mnemonic() {
            Mnemonic::Ret | Mnemonic::Retf | Mnemonic::Iret | Mnemonic::Iretd | Mnemonic::Iretq => Cat::Ret,
            Mnemonic::Jmp | Mnemonic::Xabort => Cat::UncondBr,
            Mnemonic::Call => Cat::Call,
            Mnemonic::Xbegin | Mnemonic::Xend => Cat::CondBr,
            _ if ins.flow_control() == iced_x86::FlowControl::ConditionalBranch => Cat::CondBr,
            _ => Cat::Other,
        };
        let hidden = ins.is_string_instruction()
            || matches!(
                ins.mnemonic(),
                Mnemonic::Xlatb | Mnemonic::Monitor | Mnemonic::Monitorx | Mnemonic::Mwait | Mnemonic::Mwaitx
            );
        let vis = if hidden { 0 } else { ins.op_count() as usize };

        // REX2: which operands come from ModRM.reg (R) / ModRM.rm, base or
        // opcode register (B)? Found by re-decoding with the REX bit flipped.
        let mut r_field = [false; 5];
        let mut b_field = [false; 5];
        let mut base_b = false;
        let mut x4_index = false;
        if let Some((ext, tmp)) = rex2 {
            let mut alt = [0u8; 32];
            alt[..tmp.len()].copy_from_slice(tmp);
            for (is_r, (pos, bit), want) in [(true, ext.r_toggle, ext.r4), (false, ext.b_toggle, ext.b4)] {
                if !want {
                    continue;
                }
                alt[pos] = tmp[pos] ^ bit;
                if let Some(t) = iced_decode(&alt[..tmp.len()]) {
                    for i in 0..(ins.op_count().min(5)) {
                        if ins.op_kind(i) == OpKind::Register
                            && t.op_kind(i) == OpKind::Register
                            && t.op_register(i) != ins.op_register(i)
                        {
                            if is_r {
                                r_field[i as usize] = true;
                            } else {
                                b_field[i as usize] = true;
                            }
                        }
                        if !is_r
                            && ins.op_kind(i) == OpKind::Memory
                            && t.op_kind(i) == OpKind::Memory
                            && t.memory_base() != ins.memory_base()
                        {
                            base_b = true;
                        }
                    }
                }
                alt[pos] = tmp[pos];
            }
            // X4 = 1 with a SIB byte: Zydis' index id is >= 16, never the
            // "no index" value 4, so the operand always has an index.
            if ext.x4 && let Some(modrm) = ext.modrm {
                let has_mem = (0..ins.op_count()).any(|i| ins.op_kind(i) == OpKind::Memory);
                if has_mem && modrm >> 6 != 3 && modrm & 7 == 4 {
                    x4_index = true;
                }
            }
            // CR / DR ids >= 16 are BAD_REGISTER in Zydis (CR/DR only ever
            // come from ModRM.reg).
            if ext.r4 {
                for i in 0..(ins.op_count().min(5)) {
                    let r = ins.op_register(i);
                    if ins.op_kind(i) == OpKind::Register && (r.is_cr() || r.is_dr()) {
                        return None;
                    }
                }
            }
        }
        // R4 / B4 add 16 to GPR ids only (Zydis masks them off for every
        // other register kind except CR/DR, handled above).
        let ext_reg = |r: Register, i: usize| -> Reg {
            if (r_field.get(i).copied().unwrap_or(false) || b_field.get(i).copied().unwrap_or(false))
                && let Some(num) = gpr_number(r)
            {
                return Reg::Egpr(16 + (num & 15));
            }
            canonical(r)
        };

        let mut ops = [Operand::Unused; 2];
        for (i, slot) in ops.iter_mut().enumerate().take(vis.min(2)) {
            *slot = match ins.op_kind(i as u32) {
                OpKind::Register => Operand::Reg(ext_reg(ins.op_register(i as u32), i)),
                OpKind::Memory => {
                    let raw = ins.memory_displacement64();
                    let disp = if ins.memory_base().is_gpr32() { raw as u32 as i32 as i64 } else { raw as i64 };
                    let base = match (base_b, gpr_number(ins.memory_base())) {
                        (true, Some(num)) => Reg::Egpr(16 + (num & 15)),
                        _ => canonical(ins.memory_base()),
                    };
                    Operand::Mem { base, has_index: ins.memory_index() != Register::None || x4_index, disp }
                }
                OpKind::Immediate8
                | OpKind::Immediate8_2nd
                | OpKind::Immediate16
                | OpKind::Immediate32
                | OpKind::Immediate64
                | OpKind::Immediate8to16
                | OpKind::Immediate8to32
                | OpKind::Immediate8to64
                | OpKind::Immediate32to64
                | OpKind::NearBranch16
                | OpKind::NearBranch32
                | OpKind::NearBranch64 => Operand::Imm,
                _ => Operand::Other,
            };
        }
        let mut writes = [Reg::None; 5];
        let mut n_writes = 0;
        let info = self
            .factory
            .info_options(ins, InstructionInfoOptions::NO_MEMORY_USAGE | InstructionInfoOptions::NO_REGISTER_USAGE);
        for i in 0..vis.min(5) {
            if ins.op_kind(i as u32) != OpKind::Register {
                continue;
            }
            match info.op_access(i as u32) {
                OpAccess::Write | OpAccess::CondWrite | OpAccess::ReadWrite | OpAccess::ReadCondWrite => {
                    writes[n_writes] = ext_reg(ins.op_register(i as u32), i);
                    n_writes += 1;
                }
                _ => {}
            }
        }
        // LWPINS / LWPVAL / LLWPCB: Zydis marks the register operand written.
        if matches!(ins.mnemonic(), Mnemonic::Lwpins | Mnemonic::Lwpval | Mnemonic::Llwpcb)
            && ins.op_count() > 0
            && ins.op_kind(0) == OpKind::Register
            && n_writes < writes.len()
            && !writes[..n_writes].contains(&canonical(ins.op_register(0)))
        {
            writes[n_writes] = canonical(ins.op_register(0));
            n_writes += 1;
        }
        if add_rax_write && n_writes < writes.len() {
            writes[n_writes] = Reg::R(Register::RAX);
            n_writes += 1;
        }
        if self.collect {
            // Every visible memory operand, with the same base / index /
            // displacement mapping as `ops` above plus the presence of the
            // displacement and the operand's access.
            let mut f = MemFacts { lea: ins.mnemonic() == Mnemonic::Lea, ..MemFacts::EMPTY };
            for i in 0..vis.min(5) {
                if ins.op_kind(i as u32) != OpKind::Memory {
                    continue;
                }
                let raw = ins.memory_displacement64();
                let disp = if ins.memory_base().is_gpr32() { raw as u32 as i32 as i64 } else { raw as i64 };
                let base = match (base_b, gpr_number(ins.memory_base())) {
                    (true, Some(num)) => Reg::Egpr(16 + (num & 15)),
                    _ => canonical(ins.memory_base()),
                };
                let act = match info.op_access(i as u32) {
                    OpAccess::Write | OpAccess::CondWrite | OpAccess::ReadWrite | OpAccess::ReadCondWrite => {
                        MemAct::Write
                    }
                    OpAccess::Read | OpAccess::CondRead => MemAct::Read,
                    // iced has no access for the "address only" operands
                    // (NOP Ev, PREFETCHh, INVLPG, CLDEMOTE: NoMemAccess; NOP
                    // 0F 19-1F, UD0 / UD1: None). Zydis marks every one READ
                    // except its AGEN / MIB operands: LEA (handled by the
                    // caller) and the MPX BNDMK / BNDCL / BNDCU / BNDCN /
                    // BNDLDX / BNDSTX, which carry no action at all.
                    OpAccess::NoMemAccess | OpAccess::None => match ins.mnemonic() {
                        Mnemonic::Bndmk
                        | Mnemonic::Bndcl
                        | Mnemonic::Bndcu
                        | Mnemonic::Bndcn
                        | Mnemonic::Bndldx
                        | Mnemonic::Bndstx => MemAct::None,
                        _ => MemAct::Read,
                    },
                };
                f.push(MemOperand {
                    base,
                    has_index: ins.memory_index() != Register::None || x4_index,
                    has_disp: ins.memory_displ_size() != 0,
                    disp,
                    act,
                });
            }
            self.facts = f;
        }
        Some(Insn { len, mn, cat, vis, ops, writes, n_writes })
    }
}
