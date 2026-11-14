//! Chain detector vs Zydis: the per-instruction projection (random fuzz +
//! the repo's golden vectors) and the whole legacy scan end to end.

use std::ffi::c_void;

use crate::chain::decode::InsnDecoder;
use crate::chain::tests::{BASE, FakeMem, Section, build_image, fake_read};
use crate::chain::{DcChainMatch, DcChainStats, detect};
use crate::ffi::Reader;
use crate::zydis::{Diffs, OracleMatch, OracleStats, Proj, Rng, env_list, env_u64, gen_insn, hex, oracle_detect, oracle_msvc_sort, repo_path, split, zy_chain};

/// Random instruction strings per generator mode 0..7 (CHAIN_GEN), CHAIN_FUZZ
/// cases per mode and seed (CHAIN_SEEDS), one thread per (mode, seed). The
/// seeding reproduces the original chain proof (`Rng(K ^ seed)` per mode).
#[test]
fn fuzz_random() {
    let iters = env_u64("CHAIN_FUZZ", 250_000);
    let modes = env_list("CHAIN_GEN", &[0, 1, 2, 3, 4, 5, 6, 7]);
    let seeds = env_list("CHAIN_SEEDS", &[1]);
    let mut all = Diffs::new(Proj::Chain);
    std::thread::scope(|s| {
        let hs: Vec<_> = seeds
            .iter()
            .flat_map(|&seed| modes.iter().map(move |&mode| (seed, mode)))
            .map(|(seed, mode)| {
                s.spawn(move || {
                    let mut r = Rng(0x9E37_79B9_7F4A_7C15 ^ seed);
                    let mut d = InsnDecoder::new();
                    let mut df = Diffs::new(Proj::Chain);
                    let mut buf = [0u8; 16];
                    for _ in 0..iters {
                        gen_insn(mode as u8, false, &mut r, &mut buf);
                        let len = 1 + r.below(16) as usize;
                        df.check(&mut d, &buf[..len]);
                    }
                    df
                })
            })
            .collect();
        for h in hs {
            all.merge(h.join().unwrap());
        }
    });
    all.finish(&format!("random fuzz, modes {modes:?}, seeds {seeds:?}, {iters} per mode+seed"));
}

/// Every golden vector in dll/rust/src/chain/tests.rs (`("hex", "projection")`
/// lines) must be what Zydis says — the unit test pins the Rust side, this
/// pins the expectation itself.
#[test]
fn golden_vectors() {
    let path = repo_path("dll/rust/src/chain/tests.rs");
    let src = std::fs::read_to_string(&path).unwrap();
    let (mut n, mut bad) = (0, 0);
    for line in src.lines() {
        let t = line.trim();
        if !t.starts_with("(\"") {
            continue;
        }
        let parts: Vec<&str> = t.split('"').collect();
        if parts.len() < 4 {
            continue;
        }
        let (h, want) = (parts[1], parts[3]);
        if !h.chars().all(|c| c.is_ascii_hexdigit() || c == ' ') {
            continue;
        }
        let z = zy_chain(&hex(h));
        n += 1;
        if split(&z) != want {
            println!("GOLDEN {h:<40} want={want:<30} zydis={z}");
            bad += 1;
        }
    }
    println!("[chain] golden vectors: {bad} mismatches / {n} vectors");
    assert!(n > 20, "no golden vectors found in {}", path.display());
    assert_eq!(bad, 0);
}

/// The e2e generator's own xorshift (byte = bits 32..40, kept so the
/// seeds reproduce the original 60k-module proof).
struct ERng(u64);
impl ERng {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }
    fn below(&mut self, n: u64) -> u64 {
        self.next() % n
    }
    fn byte(&mut self) -> u8 {
        (self.next() >> 32) as u8
    }
    fn chance(&mut self, pct: u64) -> bool {
        self.below(100) < pct
    }
}

const SFOS: [usize; 6] = [0xB8, 0x10, 0x8, 0x0, 0x100, 0x7F];
const TFOS: [usize; 6] = [0x0, 0x10, 0x18, 0x8, 0x200, 0x80];

/// Emits one planted chain (with noise) into `code`.
fn plant(r: &mut ERng, code: &mut Vec<u8>, code_va: usize, slot: usize, sfo: usize, tfo: usize) {
    let ip = |code: &Vec<u8>| code_va + code.len();
    // hop 1
    let reg = r.below(16) as u8;
    let lea = r.chance(15);
    let rex_w = r.chance(80);
    let rex = 0x40 | (u8::from(rex_w) << 3) | ((reg >> 3) << 2);
    let use_rex = rex != 0x40 || r.chance(30);
    let len = 6 + usize::from(use_rex);
    let disp = (slot.wrapping_sub(ip(code) + len)) as isize as i32;
    if use_rex {
        code.push(rex);
    }
    code.push(if lea { 0x8D } else { 0x8B });
    code.push(((reg & 7) << 3) | 5);
    code.extend_from_slice(&disp.to_le_bytes());
    let mut cur = reg;
    // noise / propagation
    for _ in 0..r.below(5) {
        match r.below(12) {
            0 => {
                // mov new, cur
                let n = r.below(16) as u8;
                code.extend_from_slice(&[0x48 | ((cur >> 3) << 2) | (n >> 3), 0x89, 0xC0 | ((cur & 7) << 3) | (n & 7)]);
                if r.chance(70) {
                    cur = n;
                }
            }
            1 => code.extend_from_slice(&[0xE8, 0, 0, 0, 0]),
            2 => code.extend_from_slice(&[0x74, r.byte()]),
            3 => code.extend_from_slice(&[0x48, 0x85, 0xC0]),
            4 => code.push(0x90),
            5 => {
                // xor r32, r32 on a random reg
                let n = r.below(16) as u8;
                if n >= 8 {
                    code.push(0x45);
                }
                code.extend_from_slice(&[0x31, 0xC0 | ((n & 7) << 3) | (n & 7)]);
            }
            6 => code.extend_from_slice(&[0x0F, 0x1F, 0x44, 0x00, 0x00]),
            7 => code.push(0xC3),
            8 => {
                let k = r.below(4) as usize;
                for _ in 0..k {
                    code.push(r.byte());
                }
            }
            9 => code.extend_from_slice(&[0x48, 0x83, 0xEC, 0x28]),
            10 => code.extend_from_slice(&[0xD5, r.byte(), 0x8B, r.byte()]),
            _ => code.extend_from_slice(&[0x62, r.byte(), r.byte(), r.byte(), r.byte(), r.byte()]),
        }
    }
    // hop 2: mov sf, [cur + sfo]
    let sf = r.below(16) as u8;
    let base = if r.chance(85) { cur } else { r.below(16) as u8 };
    let sfo_use = if r.chance(85) { sfo } else { SFOS[r.below(6) as usize] };
    emit_mem(r, code, &[0x8B], 0x48 | ((sf >> 3) << 2) | (base >> 3), sf, base, sfo_use);
    for _ in 0..r.below(3) {
        match r.below(4) {
            0 => code.extend_from_slice(&[0xE8, 0, 0, 0, 0]),
            1 => code.extend_from_slice(&[0x75, 0x02]),
            2 => code.push(0x90),
            _ => {
                let n = r.below(16) as u8;
                code.extend_from_slice(&[0x48 | ((sf >> 3) << 2) | (n >> 3), 0x89, 0xC0 | ((sf & 7) << 3) | (n & 7)]);
            }
        }
    }
    // hop 3
    let tfo_use = if r.chance(85) { tfo } else { TFOS[r.below(6) as usize] };
    let dst = r.below(16) as u8;
    let rexb = 0x40 | ((dst >> 3) << 2) | (sf >> 3);
    match r.below(9) {
        0 => emit_mem(r, code, &[0x8B], rexb | 8, dst, sf, tfo_use),
        1 => emit_mem(r, code, &[0x8D], rexb | 8, dst, sf, tfo_use),
        2 => emit_mem(r, code, &[0x89], rexb, dst, sf, tfo_use),
        3 => emit_mem(r, code, &[0x63], rexb | 8, dst, sf, tfo_use),
        4 => {
            code.push(0xF3);
            emit_mem(r, code, &[0x0F, 0x10], rexb, dst, sf, tfo_use)
        }
        5 => {
            code.push(0xF2);
            emit_mem(r, code, &[0x0F, 0x11], rexb, dst, sf, tfo_use)
        }
        6 => {
            emit_mem(r, code, &[0xC7], rexb & !4, 0, sf, tfo_use);
            code.extend_from_slice(&[1, 0, 0, 0]);
        }
        7 => emit_mem(r, code, &[0x0F, 0xB6], rexb, dst, sf, tfo_use),
        _ => emit_mem(r, code, &[0x03], rexb, dst, sf, tfo_use),
    }
    if r.chance(50) {
        code.push(0xC3);
    }
}

/// `[rex] opcode modrm(reg, [base + disp])`, picking the shortest or a random
/// displacement encoding (and occasionally an index register).
fn emit_mem(r: &mut ERng, code: &mut Vec<u8>, opcode: &[u8], rex: u8, reg: u8, base: u8, disp: usize) {
    let mut rex = rex;
    let index = r.chance(5);
    if index {
        rex |= 0x02;
    }
    if rex != 0x40 || r.chance(20) {
        code.push(rex);
    }
    code.extend_from_slice(opcode);
    let b = base & 7;
    let form = if disp == 0 && b != 5 && r.chance(70) {
        0
    } else if disp < 0x80 && r.chance(70) {
        1
    } else {
        2
    };
    let rm = if b == 4 || index { 4 } else { b };
    code.push((form << 6) | ((reg & 7) << 3) | rm);
    if rm == 4 {
        let idx = if index { r.below(8) as u8 } else { 4 };
        code.push((r.below(4) as u8) << 6 | (idx << 3) | b);
    }
    match form {
        1 => code.push(disp as u8),
        2 => code.extend_from_slice(&(disp as u32).to_le_bytes()),
        _ => {}
    }
}

fn gen_case(r: &mut ERng) -> (FakeMem, Vec<usize>, usize, usize, usize) {
    let n_sec = 1 + r.below(3) as usize;
    let big = r.chance(1);
    let mut secs = vec![];
    let mut rva = 0x1000usize;
    for i in 0..n_sec {
        let size = if big && i == 0 { 0x10_2000 + r.below(0x1000) as usize } else { 0x100 + r.below(0x6000) as usize };
        let exec = i == 0 || r.chance(50);
        secs.push(Section { rva, size, exec });
        rva += (size + 0xFFF) & !0xFFF;
    }
    let data_rva = rva;
    let total = data_rva + 0x1000;
    let mut img = build_image(total, &secs);
    let sfo = SFOS[r.below(6) as usize];
    let tfo = TFOS[r.below(6) as usize];
    let n_slots = 1 + r.below(4) as usize;
    let slots: Vec<usize> = (0..n_slots).map(|i| BASE + data_rva + 8 * i).collect();
    for s in &secs {
        let mut code = vec![];
        let va = BASE + s.rva;
        let fill_random = r.chance(30);
        while code.len() + 64 < s.size {
            if r.chance(25) {
                let s_pick = slots[r.below(n_slots as u64) as usize];
                plant(r, &mut code, va, s_pick, sfo, tfo);
            } else {
                for _ in 0..r.below(24) {
                    code.push(if fill_random { r.byte() } else { [0x90, 0xCC, 0x48, 0x8B, 0xC3, 0x89, 0x00][r.below(7) as usize] });
                }
            }
        }
        code.truncate(s.size);
        img[s.rva..s.rva + code.len()].copy_from_slice(&code);
    }
    // header corruption
    match r.below(60) {
        0 => img[0] = 0,
        1 => img[0x80] = 0,
        2 => img[0x3C..0x40].copy_from_slice(&(0x7FFF_0000u32).to_le_bytes()),
        3 => img[0x86..0x88].copy_from_slice(&200u16.to_le_bytes()),
        _ => {}
    }
    let mut holes = vec![];
    if r.chance(10) {
        let s = &secs[r.below(n_sec as u64) as usize];
        let at = BASE + s.rva + r.below(s.size as u64) as usize;
        holes.push((at, at + 1 + r.below(0x2000) as usize));
    }
    let mut slots = slots;
    if r.chance(10) {
        slots.clear();
    }
    if r.chance(20) {
        slots.push(BASE + r.below(total as u64) as usize);
    }
    let limit = [0usize, 1, 2, 3, 5, 1000, usize::MAX][r.below(7) as usize];
    (FakeMem { base: BASE, bytes: img, holes }, slots, sfo, tfo, limit)
}

/// Legacy `DetectFieldAccessChains` (Zydis, native/oracle_chain.cpp) vs
/// `chain::detect` over generated modules: planted chains with noise,
/// several sections, unreadable holes, corrupted headers, bogus / empty
/// slots, every match limit. Match list and all stats must be identical.
#[test]
fn e2e_matches_legacy() {
    let iters: u64 = std::env::var("E2E_ITERS").ok().and_then(|v| v.parse().ok()).unwrap_or(3000);
    let seed: u64 = std::env::var("E2E_SEED").ok().and_then(|v| v.parse().ok()).unwrap_or(1);
    let mut r = ERng(0xD1B5_4A32_D192_ED03 ^ seed.wrapping_mul(0x9E37_79B9_7F4A_7C15));
    let mut total_matches = 0usize;
    let mut total_cands = 0usize;
    for case in 0..iters {
        let (mem, slots, sfo, tfo, limit) = gen_case(&mut r);
        let ctx = &mem as *const FakeMem as *mut c_void;
        // SAFETY: fake reader over a live FakeMem.
        let reader = unsafe { Reader::new(fake_read, ctx) };
        let got = detect(BASE, Some(&reader), &slots, sfo, tfo, limit).unwrap();
        let (gm, gs) = got.parts();
        let mut om = vec![OracleMatch::default(); 1 << 16];
        let mut os = OracleStats::default();
        // SAFETY: oracle contract.
        let n = unsafe {
            oracle_detect(BASE, fake_read, ctx, slots.as_ptr(), slots.len(), sfo, tfo, limit, om.as_mut_ptr(), om.len(), &mut os)
        };
        om.truncate(n);
        let gm2: Vec<OracleMatch> =
            gm.iter().map(|m: &DcChainMatch| OracleMatch { hit_ip: m.hit_ip, hop_one_ip: m.hop_one_ip, is_write: m.is_write }).collect();
        let gs2 = DcChainStats {
            hop_one_candidates: os.a,
            chain_broken: os.b,
            chains_emitted: os.c,
            bytes_scanned: os.d,
            failed_reads: os.e,
            valid: os.valid,
        };
        if gm2 != om || gs != gs2 {
            let only_rust: Vec<_> = gm2.iter().filter(|m| !om.contains(m)).collect();
            let only_legacy: Vec<_> = om.iter().filter(|m| !gm2.contains(m)).collect();
            let mut dump = String::new();
            for m in only_rust.iter().chain(only_legacy.iter()) {
                let off = m.hop_one_ip - BASE;
                let mut q = off;
                let end = (m.hit_ip - BASE + 16).min(mem.bytes.len());
                let mut d = crate::chain::decode::InsnDecoder::new();
                while q < end {
                    let b = &mem.bytes[q..(q + 15).min(mem.bytes.len())];
                    let z = zy_chain(b);
                    let me = crate::chain::tests::project(&mut d, b);
                    dump += &format!("  {:x}: {:02X?}\n     zy=[{z}]\n     me=[{me}]\n", BASE + q, &b[..b.len().min(12)]);
                    let l: usize = me.get(1..).and_then(|t| t.split(' ').next()).and_then(|t| t.parse().ok()).unwrap_or(1);
                    q += l.max(1);
                }
            }
            panic!("case {case}: sfo={sfo:#x} tfo={tfo:#x} limit={limit} slots={slots:x?}\n rust  {gs:?}\n legacy {gs2:?}\n only rust {only_rust:x?}\n only legacy {only_legacy:x?}\n{dump}");
        }
        total_matches += n;
        total_cands += os.a;
    }
    println!("e2e: {iters} cases, {total_matches} matches, {total_cands} hop-1 candidates, all identical");
}

/// `chain/msvc_sort.rs` vs MSVC `std::sort` (dedup after an unstable sort
/// depends on its exact element moves).
#[test]
fn msvc_sort_matches_std_sort() {
    let mut r = ERng(42);
    for case in 0..20000u64 {
        let n = match case % 5 {
            0 => r.below(33) as usize,
            1 => 33 + r.below(64) as usize,
            2 => r.below(2000) as usize,
            _ => r.below(300) as usize,
        };
        let distinct = 1 + r.below(if case % 3 == 0 { 4 } else { 200 });
        let keys: Vec<u64> = (0..n)
            .map(|i| if case % 7 == 0 { (n - i) as u64 / 3 } else if case % 11 == 0 { i as u64 / 5 } else { r.below(distinct) })
            .collect();
        let tags: Vec<u32> = (0..n as u32).collect();
        let mut want = vec![0u32; n];
        // SAFETY: arrays of n elements.
        unsafe { oracle_msvc_sort(keys.as_ptr(), tags.as_ptr(), n, want.as_mut_ptr()) };
        let mut v: Vec<(u64, u32)> = keys.iter().copied().zip(tags.iter().copied()).collect();
        crate::chain::msvc_sort_for_tests(&mut v);
        let got: Vec<u32> = v.iter().map(|e| e.1).collect();
        assert_eq!(got, want, "case {case} n={n}");
    }
}

/// Maps a PE file into a zeroed image of SizeOfImage bytes (headers + raw
/// section data at their RVAs), the way the loader lays it out.
fn map_pe(file: &[u8]) -> Option<Vec<u8>> {
    let lfanew = u32::from_le_bytes(file.get(0x3C..0x40)?.try_into().ok()?) as usize;
    let nsec = u16::from_le_bytes(file.get(lfanew + 6..lfanew + 8)?.try_into().ok()?) as usize;
    let opt = u16::from_le_bytes(file.get(lfanew + 20..lfanew + 22)?.try_into().ok()?) as usize;
    let size_of_image = u32::from_le_bytes(file.get(lfanew + 24 + 56..lfanew + 24 + 60)?.try_into().ok()?) as usize;
    let size_of_headers = u32::from_le_bytes(file.get(lfanew + 24 + 60..lfanew + 24 + 64)?.try_into().ok()?) as usize;
    let mut img = vec![0u8; size_of_image];
    img[..size_of_headers.min(file.len())].copy_from_slice(&file[..size_of_headers.min(file.len())]);
    let sh0 = lfanew + 24 + opt;
    for i in 0..nsec {
        let sh = &file[sh0 + i * 40..sh0 + i * 40 + 40];
        let va = u32::from_le_bytes(sh[12..16].try_into().unwrap()) as usize;
        let raw_size = u32::from_le_bytes(sh[16..20].try_into().unwrap()) as usize;
        let raw_ptr = u32::from_le_bytes(sh[20..24].try_into().unwrap()) as usize;
        let n = raw_size.min(file.len().saturating_sub(raw_ptr)).min(img.len().saturating_sub(va));
        img[va..va + n].copy_from_slice(&file[raw_ptr..raw_ptr + n]);
    }
    Some(img)
}

/// Real `GameAssembly.dll` files (REAL_DLLS="a;b;c", set by `build.ps1 -Mode
/// real -Dlls ...`), mapped like the loader would, scanned with sampled
/// class-pointer-like slots under four configurations.
#[test]
#[ignore]
fn real_game_assembly_images() {
    let Ok(list) = std::env::var("REAL_DLLS") else {
        println!("real_game_assembly_images: REAL_DLLS not set - skipped");
        return;
    };
    let base: usize = 0x7FF6_0000_0000;
    let mut r = ERng(0xA5A5_1234);
    for path in list.split(';').filter(|s| !s.is_empty()) {
        let file = std::fs::read(path).unwrap();
        let img = map_pe(&file).unwrap();
        // Candidate slots: the targets of RIP-relative 8B/8D loads (what the
        // class-pointer slots look like), sampled.
        let mut targets: Vec<usize> = vec![];
        for p in 0..img.len().saturating_sub(7) {
            if (img[p] == 0x48 || img[p] == 0x4C) && (img[p + 1] == 0x8B || img[p + 1] == 0x8D) && img[p + 2] & 0xC7 == 5 {
                let d = i32::from_le_bytes(img[p + 3..p + 7].try_into().unwrap());
                let t = (base + p + 7).wrapping_add(d as isize as usize);
                if t >= base && t < base + img.len() { targets.push(t); }
            }
        }
        targets.sort();
        targets.dedup();
        let mem = FakeMem { base, bytes: img, holes: vec![] };
        let ctx = &mem as *const FakeMem as *mut c_void;
        // SAFETY: fake reader over a live FakeMem.
        let reader = unsafe { Reader::new(fake_read, ctx) };
        for cfg in 0..4 {
            let n_slots = [50usize, 400, 3000, 20000][cfg];
            let slots: Vec<usize> = (0..n_slots).map(|_| targets[r.below(targets.len() as u64) as usize]).collect();
            let (sfo, tfo) = [(0xB8usize, 0x0usize), (0xB8, 0x10), (0xB8, 0x8), (0xC0, 0x18)][cfg];
            let limit = [usize::MAX, 1000, 100000, 1][cfg];
            let t0 = std::time::Instant::now();
            let got = detect(base, Some(&reader), &slots, sfo, tfo, limit).unwrap();
            let t_rust = t0.elapsed();
            let (gm, gs) = got.parts();
            let mut om = vec![OracleMatch::default(); 1 << 20];
            let mut os = OracleStats::default();
            let t1 = std::time::Instant::now();
            // SAFETY: oracle contract.
            let n = unsafe { oracle_detect(base, fake_read, ctx, slots.as_ptr(), slots.len(), sfo, tfo, limit, om.as_mut_ptr(), om.len(), &mut os) };
            let t_zy = t1.elapsed();
            om.truncate(n);
            let gm2: Vec<OracleMatch> = gm.iter().map(|m| OracleMatch { hit_ip: m.hit_ip, hop_one_ip: m.hop_one_ip, is_write: m.is_write }).collect();
            let gs2 = DcChainStats { hop_one_candidates: os.a, chain_broken: os.b, chains_emitted: os.c, bytes_scanned: os.d, failed_reads: os.e, valid: os.valid };
            assert_eq!(gs, gs2, "{path} cfg {cfg}");
            assert!(gm2 == om, "{path} cfg {cfg}: match lists differ");
            println!("{} cfg{cfg}: slots={n_slots} matches={} candidates={} broken={} identical (rust {:?}, legacy {:?})", path.rsplit(std::path::MAIN_SEPARATOR).nth(1).unwrap_or(path), n, os.a, os.b, t_rust, t_zy);
        }
    }
}
