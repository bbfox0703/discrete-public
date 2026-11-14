//! The Zydis side (FFI into build/zydis_oracle.lib) and the shared harness:
//! the two per-instruction projections, the random instruction generator and
//! the mismatch collector.
//!
//! A *projection* reduces one decode to exactly the facts a legacy consumer
//! read from Zydis, as a string. Both sides format it identically, so a
//! string compare is the whole test:
//!
//! * `Proj::Chain` — what the legacy chain detector's forward trace consulted
//!   (native `oracle_project` vs the repo's `chain::tests::project`, i.e.
//!   `InsnDecoder::decode`).
//! * `Proj::Xref` — the hit list the legacy `ScanInstanceAccesses` built per
//!   instruction (native `oxr_project` vs [`xref_project`], i.e.
//!   `InsnDecoder::decode_mem`).
//!
//! The Zydis strings carry diagnostics after `" |"`; only the part before is
//! compared.

use std::collections::BTreeMap;
use std::ffi::{CStr, c_char, c_void};

use crate::chain::decode::{InsnDecoder, MemAct, Reg};
use iced_x86::Register;

/// Mirrors `OracleMatch` in native/oracle_chain.cpp.
#[repr(C)]
#[derive(Clone, Copy, Default, Debug, PartialEq, Eq)]
pub(crate) struct OracleMatch {
    pub hit_ip: usize,
    pub hop_one_ip: usize,
    pub is_write: u8,
}

/// Mirrors `OracleStats` in native/oracle_chain.cpp (a..e = hopOneCandidates,
/// chainBroken, chainsEmitted, bytesScanned, failedReads).
#[repr(C)]
#[derive(Clone, Copy, Default, Debug, PartialEq, Eq)]
pub(crate) struct OracleStats {
    pub a: usize,
    pub b: usize,
    pub c: usize,
    pub d: usize,
    pub e: usize,
    pub valid: u8,
}

// Linked by build.rs (static zydis_oracle.lib).
unsafe extern "C" {
    // native/oracle_chain.cpp
    fn oracle_project(buf: *const u8, len: usize, out: *mut u8, cap: usize) -> usize;
    fn oracle_mnemonic_name(m: i32) -> *const c_char;
    pub(crate) fn oracle_detect(
        base: usize,
        rd: crate::ffi::ReadFn,
        ctx: *mut c_void,
        slots: *const usize,
        n: usize,
        sfo: usize,
        tfo: usize,
        limit: usize,
        out: *mut OracleMatch,
        cap: usize,
        st: *mut OracleStats,
    ) -> usize;
    pub(crate) fn oracle_msvc_sort(keys: *const u64, tags: *const u32, n: usize, out_tags: *mut u32);

    // native/oracle_xref.cpp
    fn oxr_project(buf: *const u8, len: usize, out: *mut u8, cap: usize) -> usize;
    fn oxr_memfacts(
        buf: *const u8,
        len: usize,
        acts: *mut u8,
        disps: *mut i64,
        dsz: *mut u8,
        bases: *mut i32,
        idxs: *mut i32,
        mnem: *mut i32,
    ) -> i32;
    fn oxr_mnemonic_name(m: i32) -> *const c_char;
}

/// Zydis chain projection (with diagnostics `" |m<mnemonic id> c<category>
/// v<visible> e<encoding> x<isa-ext>"`).
pub(crate) fn zy_chain(bytes: &[u8]) -> String {
    let mut out = [0u8; 512];
    // SAFETY: valid in/out buffers; the native side writes at most `cap` bytes.
    let n = unsafe { oracle_project(bytes.as_ptr(), bytes.len(), out.as_mut_ptr(), out.len()) };
    String::from_utf8_lossy(&out[..n]).into_owned()
}

/// Zydis xref projection (with diagnostics `" |<mnemonic> v<visible>
/// [memory operands ...]"`).
pub(crate) fn zy_xref(bytes: &[u8]) -> String {
    let mut out = [0u8; 1024];
    // SAFETY: as above.
    let n = unsafe { oxr_project(bytes.as_ptr(), bytes.len(), out.as_mut_ptr(), out.len()) };
    String::from_utf8_lossy(&out[..n]).into_owned()
}

/// Zydis mnemonic name of a `ZydisMnemonic` id.
pub(crate) fn mnemonic_name(id: i32) -> String {
    // SAFETY: Zydis returns a static string (or NULL for an invalid id).
    let p = unsafe { oracle_mnemonic_name(id) };
    if p.is_null() { format!("#{id}") } else { unsafe { CStr::from_ptr(p) }.to_string_lossy().into_owned() }
}

/// The `ZydisMnemonic` id in a chain projection's diagnostics.
pub(crate) fn chain_mnemonic_id(z: &str) -> Option<i32> {
    z.split(" |m").nth(1)?.split(' ').next()?.parse().ok()
}

/// The comparable part of a Zydis projection.
pub(crate) fn split(s: &str) -> &str {
    s.split(" |").next().unwrap()
}

/// Raw facts of the visible memory operands (table generation).
pub(crate) struct ZFacts {
    /// -1 = decode failed, else the number of visible memory operands.
    pub n: i32,
    pub acts: [u8; 8],
    pub disps: [i64; 8],
    pub dsz: [u8; 8],
    pub bases: [i32; 8],
    pub idxs: [i32; 8],
    pub mnem: i32,
}

pub(crate) fn zfacts(b: &[u8]) -> ZFacts {
    let mut f = ZFacts { n: 0, acts: [0; 8], disps: [0; 8], dsz: [0; 8], bases: [0; 8], idxs: [0; 8], mnem: 0 };
    // SAFETY: arrays of 8, the native side stops at 8 operands.
    f.n = unsafe {
        oxr_memfacts(
            b.as_ptr(),
            b.len(),
            f.acts.as_mut_ptr(),
            f.disps.as_mut_ptr(),
            f.dsz.as_mut_ptr(),
            f.bases.as_mut_ptr(),
            f.idxs.as_mut_ptr(),
            &mut f.mnem,
        )
    };
    f
}

pub(crate) fn xref_mnemonic_name(id: i32) -> String {
    // SAFETY: as in `mnemonic_name`.
    let p = unsafe { oxr_mnemonic_name(id) };
    if p.is_null() { format!("#{id}") } else { unsafe { CStr::from_ptr(p) }.to_string_lossy().into_owned() }
}

/// Rust side of the xref projection: the per-instruction loop of
/// `xref::scan_inst` / legacy `ScanInstanceAccesses` minus the field-offset
/// filter (every candidate displacement is listed).
pub(crate) fn xref_project(d: &mut InsnDecoder, bytes: &[u8]) -> String {
    let Some((len, f)) = d.decode_mem(bytes) else { return "X".into() };
    let mut s = format!("L{len}");
    for op in f.operands() {
        if !op.has_disp || op.has_index {
            continue;
        }
        if matches!(op.base, Reg::None | Reg::R(Register::RIP | Register::RSP | Register::RBP)) {
            continue;
        }
        let c = if f.lea {
            'R'
        } else {
            match op.act {
                MemAct::Write => 'W',
                MemAct::Read => 'R',
                MemAct::None => continue,
            }
        };
        s += &format!(" {c}{:x}", op.disp as u64);
    }
    s
}

fn xref_project_dbg(d: &mut InsnDecoder, bytes: &[u8]) -> String {
    match d.decode_mem(bytes) {
        None => "X".into(),
        Some((len, f)) => format!("L{len} lea={} {:?}", f.lea, f.operands()),
    }
}

fn iced_code(bytes: &[u8]) -> String {
    let mut dd = iced_x86::Decoder::new(64, bytes, iced_x86::DecoderOptions::MPX);
    format!("{:?}/{:?}", dd.decode().code(), dd.last_error())
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Proj {
    Chain,
    Xref,
}

impl Proj {
    pub fn label(self) -> &'static str {
        match self {
            Proj::Chain => "chain",
            Proj::Xref => "xref",
        }
    }

    pub fn zydis(self, b: &[u8]) -> String {
        match self {
            Proj::Chain => zy_chain(b),
            Proj::Xref => zy_xref(b),
        }
    }

    pub fn ours(self, d: &mut InsnDecoder, b: &[u8]) -> String {
        match self {
            Proj::Chain => crate::chain::tests::project(d, b),
            Proj::Xref => xref_project(d, b),
        }
    }

    /// Mismatch class: Zydis mnemonic (or X) x our verdict.
    fn class(self, z: &str, me: &str, b: &[u8]) -> String {
        let ours = if me == "X" { "meX" } else { "meOK" };
        match self {
            Proj::Chain => {
                let zm = if z == "X" { "X".into() } else { chain_mnemonic_id(z).map(mnemonic_name).unwrap_or_default() };
                let im = iced_code(b);
                format!("zy:{zm} {ours} iced:{}", im.split('_').next().unwrap_or(""))
            }
            Proj::Xref => {
                let zm = if z == "X" { "X" } else { z.split(" |").nth(1).and_then(|t| t.split(' ').next()).unwrap_or("") };
                format!("zy:{zm} {ours}")
            }
        }
    }

    fn dbg(self, d: &mut InsnDecoder, b: &[u8]) -> String {
        match self {
            Proj::Chain => format!("iced={}", iced_code(b)),
            Proj::Xref => format!("dbg={}", xref_project_dbg(d, b)),
        }
    }
}

/// Mismatch collector: counts per class, keeps a few examples of each.
pub(crate) struct Diffs {
    proj: Proj,
    map: BTreeMap<String, (u64, String)>,
    pub total: u64,
    pub n: u64,
}

impl Diffs {
    pub fn new(proj: Proj) -> Self {
        Self { proj, map: BTreeMap::new(), total: 0, n: 0 }
    }

    pub fn check(&mut self, d: &mut InsnDecoder, b: &[u8]) {
        self.n += 1;
        let z = self.proj.zydis(b);
        let me = self.proj.ours(d, b);
        if split(&z) != me {
            self.total += 1;
            let e = self.map.entry(self.proj.class(&z, &me, b)).or_insert((0, String::new()));
            e.0 += 1;
            if e.0 <= 4 {
                e.1 += &format!("\n        {b:02X?}\n          zydis=[{z}]\n          ours =[{me}] {}", self.proj.dbg(d, b));
            }
        }
    }

    pub fn merge(&mut self, o: Diffs) {
        self.total += o.total;
        self.n += o.n;
        for (k, v) in o.map {
            let e = self.map.entry(k).or_insert((0, String::new()));
            e.0 += v.0;
            if e.1.matches('\n').count() < 12 {
                e.1 += &v.1;
            }
        }
    }

    /// Prints the classes and the summary line, then asserts 0 mismatches.
    pub fn finish(&self, what: &str) {
        for (k, v) in &self.map {
            println!("{:>9} {k}{}", v.0, v.1);
        }
        println!("[{}] {what}: {} mismatches / {} cases", self.proj.label(), self.total, self.n);
        assert_eq!(self.total, 0, "[{}] {what}: Zydis and the Rust adapter disagree", self.proj.label());
    }
}

/// xorshift64 — deterministic across runs and machines.
pub(crate) struct Rng(pub u64);

impl Rng {
    pub fn next(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }
    pub fn byte(&mut self) -> u8 {
        (self.next() >> 24) as u8
    }
    pub fn below(&mut self, n: u64) -> u64 {
        self.next() % n
    }
}

const PREFIXES: [u8; 11] = [0x66, 0x67, 0xF2, 0xF3, 0x2E, 0x3E, 0x26, 0x64, 0x65, 0x36, 0xF0];

/// Random instruction bytes. Modes: 0 mixed (legacy maps, VEX/EVEX/XOP/REX2
/// escapes, hop opcodes, branches), 1 REX2, 2 VEX, 3 EVEX, 4 XOP, 5 3DNow!,
/// 6 0F 01 / 0F AE style system groups, 7 pure random, 8 legacy memory forms
/// (ModRM mod 1/2). `mem_bias` biases modes 1 and 3 toward memory forms —
/// off reproduces the original chain fuzz generator exactly, on the xref one.
pub(crate) fn gen_insn(mode: u8, mem_bias: bool, r: &mut Rng, buf: &mut [u8; 16]) {
    for b in buf.iter_mut() {
        *b = r.byte();
    }
    if mode == 7 {
        return;
    }
    let mut p = 0;
    let np = r.below(4) as usize;
    for _ in 0..np {
        if r.below(3) == 0 {
            buf[p] = PREFIXES[r.below(PREFIXES.len() as u64) as usize];
            p += 1;
        }
    }
    if r.below(2) == 0 {
        buf[p] = 0x40 | (r.byte() & 0xF);
        p += 1;
    }
    match mode {
        1 => {
            buf[p] = 0xD5;
            if mem_bias && r.below(2) == 0 {
                buf[p + 3] = (buf[p + 3] & 0x3F) | (((1 + r.below(2)) as u8) << 6);
            }
            return;
        }
        2 => {
            buf[p] = [0xC4, 0xC5][r.below(2) as usize];
            if buf[p] == 0xC4 && r.below(2) == 0 {
                buf[p + 1] = (buf[p + 1] & 0xE0) | (1 + r.below(3) as u8);
            }
            return;
        }
        3 => {
            buf[p] = 0x62;
            if mem_bias && r.below(2) == 0 {
                buf[p + 5] = (buf[p + 5] & 0x3F) | (((1 + r.below(2)) as u8) << 6);
            }
            return;
        }
        4 => {
            buf[p] = 0x8F;
            return;
        }
        5 => {
            buf[p] = 0x0F;
            buf[p + 1] = 0x0F;
            return;
        }
        6 => {
            buf[p] = 0x0F;
            buf[p + 1] = [0x01, 0xAE, 0xC7, 0x00, 0x1E, 0xA6, 0xA7, 0xB8, 0xBC, 0xBD, 0x18, 0x0D][r.below(12) as usize];
            return;
        }
        8 => {
            let mut q = p;
            match r.below(4) {
                0 => {}
                1 => {
                    buf[q] = 0x0F;
                    q += 1;
                }
                2 => {
                    buf[q] = 0x0F;
                    buf[q + 1] = 0x38;
                    q += 2;
                }
                _ => {
                    buf[q] = 0x0F;
                    buf[q + 1] = 0x3A;
                    q += 2;
                }
            }
            let m = buf[q + 1];
            buf[q + 1] = (m & 0x3F) | (((1 + r.below(2)) as u8) << 6);
            return;
        }
        _ => {}
    }
    match r.below(10) {
        0..=2 => {}
        3 | 4 => buf[p] = 0x0F,
        5 => {
            buf[p] = 0x0F;
            buf[p + 1] = 0x38;
        }
        6 => {
            buf[p] = 0x0F;
            buf[p + 1] = 0x3A;
        }
        7 => buf[p] = [0xC4, 0xC5, 0x62, 0x8F, 0xD5][r.below(5) as usize],
        8 => buf[p] = [0x8B, 0x89, 0x8D, 0xC7, 0xC6, 0x88, 0x8A, 0x63][r.below(8) as usize],
        _ => buf[p] = [0xFF, 0xE8, 0xE9, 0xEB, 0xC3, 0xC2, 0xCB, 0xCA, 0xCF, 0x9A, 0xEA, 0xE0, 0xE1, 0xE2, 0xE3][r.below(15) as usize],
    }
}

pub(crate) fn env_u64(k: &str, default: u64) -> u64 {
    std::env::var(k).ok().and_then(|v| v.trim().parse().ok()).unwrap_or(default)
}

/// Comma-separated list from the environment, or `default`.
pub(crate) fn env_list(k: &str, default: &[u64]) -> Vec<u64> {
    match std::env::var(k) {
        Ok(v) if !v.trim().is_empty() => v.split(',').map(|x| x.trim().parse().expect(k)).collect(),
        _ => default.to_vec(),
    }
}

pub(crate) fn hex(s: &str) -> Vec<u8> {
    s.split_whitespace().map(|h| u8::from_str_radix(h, 16).unwrap()).collect()
}

/// Path of a file in the repo (relative to the repo root).
pub(crate) fn repo_path(rel: &str) -> std::path::PathBuf {
    std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..").join(rel)
}
