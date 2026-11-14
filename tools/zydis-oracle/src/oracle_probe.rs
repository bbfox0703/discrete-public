//! `build.ps1 -Mode probe -Hex "48 8B 80 B8 00 00 00"`: both projections of
//! one byte string, Zydis vs the Rust adapter, with diagnostics.

use crate::chain::decode::InsnDecoder;
use crate::zydis::{Proj, hex, split};

#[test]
#[ignore]
fn probe() {
    let Ok(h) = std::env::var("ORACLE_HEX") else {
        println!("probe: ORACLE_HEX not set - skipped");
        return;
    };
    let b = hex(&h);
    let mut d = InsnDecoder::new();
    for p in [Proj::Chain, Proj::Xref] {
        let z = p.zydis(&b);
        let me = p.ours(&mut d, &b);
        let verdict = if split(&z) == me { "same" } else { "DIFFERENT" };
        println!("[{}] {verdict}\n  zydis=[{z}]\n  ours =[{me}]", p.label());
    }
    println!("[xref] facts: {:?}", d.decode_mem(&b).map(|(len, f)| (len, f.lea, f.operands().to_vec())));
}
