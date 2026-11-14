//! Exhaustive / structured encoding sweeps, each run for either projection
//! (`chain_*` = `InsnDecoder::decode`, `xref_*` = `decode_mem`). All
//! `#[ignore]`: `build.ps1 -Mode full` runs them.
//!
//! The spaces are the ones the original xref proof covered (293 M cases per
//! projection; with the 99 M random fuzz of `-Mode full`, 392 M for xref):
//!
//! | sweep    | space                                                               | cases  |
//! |----------|---------------------------------------------------------------------|--------|
//! | legacy   | maps 0 / 0F / 0F38 / 0F3A x 14 prefix sets x 9 REX x op x ModRM x 4 SIB tails | 132.1 M |
//! | vex2     | C5 x every b1 x op x ModRM x 2 tails                                | 33.6 M |
//! | vex3_xop | C4 / 8F x 4 RXB x 32 maps x b2 x op x 5 ModRM                       | 83.9 M |
//! | evex     | 62 x 8 maps x 8 P0 classes x pp x W x op x 12 random P1/P2/ModRM    | 1.6 M  |
//! | rex2     | 5 prefix sets x D5 x 16 payloads x both maps x op x ModRM x 2 tails | 21.0 M |
//! | apx_mem  | APX EVEX map 4 + EXTRA x reg x pp/W/ND/NF x 9 ModRM/SIB shapes x 8 P0 x U x vvvv | 20.9 M |

use std::sync::Mutex;
use std::sync::atomic::{AtomicUsize, Ordering};

use crate::chain::decode::InsnDecoder;
use crate::oracle_tables::EXTRA_OPS;
use crate::zydis::{Diffs, Proj, Rng};

/// Runs `body(item, check)` for every item on all cores; `check` feeds one
/// byte string to the projection compare. Items are pulled dynamically.
fn par<T: Sync>(proj: Proj, what: &str, items: &[T], body: impl Fn(&T, &mut dyn FnMut(&[u8])) + Sync) {
    let next = AtomicUsize::new(0);
    let all = Mutex::new(Diffs::new(proj));
    let threads = std::thread::available_parallelism().map_or(4, |n| n.get()).min(items.len().max(1));
    let t0 = std::time::Instant::now();
    std::thread::scope(|s| {
        for _ in 0..threads {
            s.spawn(|| {
                let mut d = InsnDecoder::new();
                let mut df = Diffs::new(proj);
                loop {
                    let i = next.fetch_add(1, Ordering::Relaxed);
                    let Some(it) = items.get(i) else { break };
                    body(it, &mut |b: &[u8]| df.check(&mut d, b));
                }
                all.lock().unwrap().merge(df);
            });
        }
    });
    let all = all.into_inner().unwrap();
    println!("[{}] {what}: {:.1?}", proj.label(), t0.elapsed());
    all.finish(what);
}

fn legacy(proj: Proj) {
    const PFX: [&[u8]; 14] = [
        &[],
        &[0x66],
        &[0xF2],
        &[0xF3],
        &[0x67],
        &[0xF0],
        &[0x66, 0xF2],
        &[0x66, 0xF3],
        &[0xF3, 0x66],
        &[0xF2, 0xF3],
        &[0xF0, 0x66],
        &[0x67, 0x66],
        &[0x2E],
        &[0x64, 0x67],
    ];
    const REXS: [&[u8]; 9] = [&[], &[0x40], &[0x41], &[0x42], &[0x44], &[0x48], &[0x49], &[0x4A], &[0x4F]];
    const TAILS: [[u8; 10]; 4] = [
        [0x20, 0x10, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11], // SIB no index, base rax
        [0x25, 0xF0, 0xFF, 0xFF, 0xFF, 0x80, 0x01, 0x02, 0x03, 0x04], // SIB base 101
        [0x64, 0x80, 0x00, 0x00, 0x80, 0x12, 0x34, 0x56, 0x78, 0x9A], // SIB index none, scale 2, base rsp
        [0x0C, 0x7F, 0x00, 0x00, 0x00, 0xAB, 0xCD, 0xEF, 0x01, 0x23], // SIB index rcx
    ];
    const MAPS: [&[u8]; 4] = [&[], &[0x0F], &[0x0F, 0x38], &[0x0F, 0x3A]];
    let items: Vec<(usize, usize, usize)> =
        (0..MAPS.len()).flat_map(|m| (0..PFX.len()).flat_map(move |p| (0..REXS.len()).map(move |r| (m, p, r)))).collect();
    par(proj, "exhaustive legacy maps", &items, |&(m, p, r), check| {
        let mut b = Vec::with_capacity(32);
        for op in 0..=255u8 {
            for modrm in 0..=255u8 {
                for t in &TAILS {
                    b.clear();
                    b.extend_from_slice(PFX[p]);
                    b.extend_from_slice(REXS[r]);
                    b.extend_from_slice(MAPS[m]);
                    b.push(op);
                    b.push(modrm);
                    b.extend_from_slice(t);
                    b.truncate(16);
                    check(&b);
                }
            }
        }
    });
}

fn vex2(proj: Proj) {
    let items: Vec<u8> = (0..=255u8).collect();
    par(proj, "exhaustive vex2", &items, |&b1, check| {
        for op in 0..=255u8 {
            for modrm in 0..=255u8 {
                for t in [[0x24u8, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66], [0x20, 0x80, 0x01, 0x00, 0x80, 0x55, 0x66]] {
                    let mut b = [0u8; 11];
                    b[..4].copy_from_slice(&[0xC5, b1, op, modrm]);
                    b[4..].copy_from_slice(&t);
                    check(&b);
                }
            }
        }
    });
}

fn vex3_xop(proj: Proj) {
    let items: Vec<(u8, u8, u8)> = [0xC4u8, 0x8F]
        .into_iter()
        .flat_map(|esc| [0xE0u8, 0x00, 0x60, 0xA0].into_iter().flat_map(move |rxb| (0..32u8).map(move |mm| (esc, rxb, mm))))
        .collect();
    par(proj, "vex3 + xop", &items, |&(esc, rxb, mm), check| {
        for b2 in 0..=255u8 {
            for op in 0..=255u8 {
                for m in [0x43u8, 0x84, 0x04, 0xC3, 0x0D] {
                    check(&[esc, rxb | mm, b2, op, m, 0x20, 0x81, 0x22, 0x33, 0x84, 0x55, 0x66]);
                }
            }
        }
    });
}

fn evex(proj: Proj) {
    // One deterministic stream, as in the original proof (single generator
    // walked in loop order), so it runs on one thread.
    let items = [()];
    par(proj, "sampled evex", &items, |_, check| {
        let mut r = Rng(12345);
        for mmm in 0..8u8 {
            for p0hi in [0xF0u8, 0x70, 0xE8, 0xB0, 0xD0, 0x10, 0xF8, 0x00] {
                for pp in 0..4u8 {
                    for w in 0..2u8 {
                        for op in 0..=255u8 {
                            for _ in 0..12 {
                                let p1 = (w << 7) | ((r.byte() & 0xF) << 3) | (r.byte() & 4) | pp;
                                let p2 = r.byte();
                                let m = [0x43u8, 0x84, 0x04, 0xC3, 0x0C, 0x80, 0x5D, 0x45][r.below(8) as usize];
                                check(&[0x62, p0hi | mmm, p1, p2, op, m, 0x20, 0x81, 0x22, 0x33, 0x84, 0x55, 0x66, 0x77]);
                            }
                        }
                    }
                }
            }
        }
    });
}

fn rex2(proj: Proj) {
    const PREFIXES: [&[u8]; 5] = [&[], &[0x66], &[0xF2], &[0xF3], &[0x67]];
    const PAYLOADS: [u8; 16] = [0x00, 0x08, 0x40, 0x10, 0x20, 0x07, 0x7F, 0x01, 0x02, 0x04, 0x30, 0x50, 0x60, 0x11, 0x22, 0x3C];
    let items: Vec<(u8, u8)> = (0..2u8).flat_map(|map| (0..=255u8).map(move |op| (map, op))).collect();
    par(proj, "exhaustive rex2", &items, |&(map, op), check| {
        let mut b = Vec::with_capacity(32);
        for modrm in 0..=255u8 {
            for pf in PREFIXES {
                for pl in PAYLOADS {
                    for t in [[0x24u8, 0x11, 0x22, 0x33, 0x44, 0x55], [0x64, 0x80, 0x00, 0x00, 0x80, 0x12]] {
                        b.clear();
                        b.extend_from_slice(pf);
                        b.extend([0xD5, pl | (map << 7), op, modrm]);
                        b.extend_from_slice(&t);
                        b.extend([0x66, 0x77, 0x88, 0x99]);
                        b.truncate(16);
                        check(&b);
                    }
                }
            }
        }
    });
}

fn apx_mem(proj: Proj) {
    const SHAPES: [&[u8]; 9] = [
        &[0x43, 0x11],
        &[0x83, 0x44, 0x33, 0x22, 0x91],
        &[0x03],
        &[0x05, 0x10, 0x00, 0x00, 0x00],
        &[0x44, 0x20, 0xF0],
        &[0x44, 0x24, 0x08],
        &[0x84, 0x65, 0x00, 0x01, 0x00, 0x00],
        &[0x04, 0x25, 0x00, 0x10, 0x00, 0x00],
        &[0x45, 0x80],
    ];
    let mut items: Vec<(u8, u8)> = (0..=255u8).map(|o| (4u8, o)).collect();
    items.extend_from_slice(&EXTRA_OPS);
    par(proj, "apx evex memory forms", &items, |&(mmm, op), check| {
        let mut b = Vec::with_capacity(32);
        for reg in 0..8u8 {
            for sel in 0..32u8 {
                let (pp, w, nd, nf) = (sel >> 3, (sel >> 2) & 1, (sel >> 1) & 1, sel & 1);
                for sh in SHAPES {
                    for p0x in [0xF0u8, 0x70, 0xB0, 0xD0, 0xF8, 0x78, 0x98, 0x08] {
                        for u in [0x04u8, 0x00] {
                            for vv in [0u8, 6] {
                                let p0 = (p0x & 0xF8) | mmm;
                                let p1 = (w << 7) | ((!vv & 0xF) << 3) | u | pp;
                                let p2 = (nd << 4) | 0x08 | (nf << 2);
                                b.clear();
                                b.extend([0x62, p0, p1, p2, op, sh[0] | (reg << 3)]);
                                b.extend_from_slice(&sh[1..]);
                                b.extend([0x55, 0x66, 0x77, 0x88]);
                                b.truncate(16);
                                check(&b);
                            }
                        }
                    }
                }
            }
        }
    });
}

macro_rules! sweeps {
    ($($name:ident: $f:ident($p:expr);)*) => {
        $(
            #[test]
            #[ignore]
            fn $name() {
                $f($p);
            }
        )*
    };
}

sweeps! {
    xref_legacy: legacy(Proj::Xref);
    xref_vex2: vex2(Proj::Xref);
    xref_vex3_xop: vex3_xop(Proj::Xref);
    xref_evex: evex(Proj::Xref);
    xref_rex2: rex2(Proj::Xref);
    xref_apx_mem: apx_mem(Proj::Xref);
    chain_legacy: legacy(Proj::Chain);
    chain_vex2: vex2(Proj::Chain);
    chain_vex3_xop: vex3_xop(Proj::Chain);
    chain_evex: evex(Proj::Chain);
    chain_rex2: rex2(Proj::Chain);
    chain_apx_mem: apx_mem(Proj::Chain);
}
