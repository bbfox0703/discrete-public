//! Links the Zydis side (native/*.cpp + the fetched Zydis), which build.ps1
//! compiles into build/zydis_oracle.lib, and stamps the banner the repo's
//! ffi.rs expects (the same "<version> (cargo)" a plain cargo build of
//! dll/rust gets).

use std::env;
use std::path::PathBuf;

fn main() {
    let pkg = env::var("CARGO_PKG_VERSION").expect("cargo sets CARGO_PKG_VERSION");
    println!("cargo:rustc-env=DC_BUILD_BANNER={pkg} (cargo)");

    if env::var("CARGO_CFG_TARGET_ARCH").as_deref() != Ok("x86_64") {
        // The oracle modules are x86_64-only (cfg in lib.rs); nothing to link.
        return;
    }
    println!("cargo:rerun-if-env-changed=ZYDIS_ORACLE_LIB_DIR");
    let dir = match env::var_os("ZYDIS_ORACLE_LIB_DIR") {
        Some(d) => PathBuf::from(d),
        None => PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap()).join("build"),
    };
    let lib = dir.join("zydis_oracle.lib");
    if !lib.is_file() {
        panic!("{} not found - run tools/zydis-oracle/build.ps1 (it fetches Zydis and compiles the C++ side)", lib.display());
    }
    println!("cargo:rerun-if-changed={}", lib.display());
    println!("cargo:rustc-link-search=native={}", dir.display());
    println!("cargo:rustc-link-lib=static=zydis_oracle");
}
