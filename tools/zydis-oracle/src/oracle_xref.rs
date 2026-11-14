//! Instance-field xref facts (`InsnDecoder::decode_mem`) vs Zydis: random
//! fuzz. The exhaustive / structured sweeps are in `oracle_sweep`.

use crate::chain::decode::InsnDecoder;
use crate::zydis::{Diffs, Proj, Rng, env_list, env_u64, gen_insn};

/// Random instruction strings per generator mode 0..8 (XF_MODES, with the
/// memory-form bias on), XF_ITERS cases per mode and seed (XF_SEEDS), one
/// thread per (mode, seed). Seeding as in the original xref proof.
#[test]
fn fuzz_random() {
    let iters = env_u64("XF_ITERS", 200_000);
    let modes = env_list("XF_MODES", &[0, 1, 2, 3, 4, 5, 6, 7, 8]);
    let seeds = env_list("XF_SEEDS", &[1]);
    let mut all = Diffs::new(Proj::Xref);
    std::thread::scope(|s| {
        let hs: Vec<_> = seeds
            .iter()
            .flat_map(|&seed| modes.iter().map(move |&mode| (seed, mode)))
            .map(|(seed, mode)| {
                s.spawn(move || {
                    let mut r = Rng(0x9E37_79B9_7F4A_7C15 ^ seed.wrapping_mul(0x1000_0001).wrapping_add(mode * 7919));
                    let mut d = InsnDecoder::new();
                    let mut df = Diffs::new(Proj::Xref);
                    let mut buf = [0u8; 16];
                    for _ in 0..iters {
                        gen_insn(mode as u8, true, &mut r, &mut buf);
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
