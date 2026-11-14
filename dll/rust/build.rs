//! Stamps the crate with the same version as the C++ DLLs and the UI.
//!
//! RustCore.cmake passes the values BuildInfo.h is generated from (root
//! CMakeLists.txt `project(VERSION)`, build_number.txt, git, config, configure
//! timestamp) as environment variables; this script folds them into
//! `DC_BUILD_BANNER`, formatted exactly like the C++ `DISCRETE_BUILD_BANNER`:
//!
//!     0.1.0.1469 9636b00 (Release, 2026-11-14T09:00:00Z)
//!
//! so the init log can show that both halves of a DLL came from one build.
//! A plain `cargo build` / `cargo test` (no CMake) gets `0.1.0 (cargo)`.

use std::env;

const VARS: [&str; 4] = [
    "DISCRETE_FILE_VERSION",
    "DISCRETE_GIT_SHORT",
    "DISCRETE_BUILD_CONFIG",
    "DISCRETE_BUILD_TIMESTAMP",
];

fn main() {
    for v in VARS {
        println!("cargo:rerun-if-env-changed={v}");
    }
    let pkg = env::var("CARGO_PKG_VERSION").expect("cargo sets CARGO_PKG_VERSION");
    let get = |k: &str| env::var(k).ok().filter(|s| !s.is_empty());

    let banner = match get("DISCRETE_FILE_VERSION") {
        Some(file_ver) => {
            // Cargo.toml's version must track project(VERSION) in the root
            // CMakeLists.txt; the build number is the fourth component.
            let prefix = format!("{pkg}.");
            if !file_ver.starts_with(&prefix) {
                panic!(
                    "dll/rust/Cargo.toml version {pkg} does not match the C++ project \
                     version {file_ver} (root CMakeLists.txt project(VERSION)); bump both"
                );
            }
            format!(
                "{file_ver} {} ({}, {})",
                get("DISCRETE_GIT_SHORT").unwrap_or_else(|| "unknown".into()),
                get("DISCRETE_BUILD_CONFIG").unwrap_or_else(|| "unknown".into()),
                get("DISCRETE_BUILD_TIMESTAMP").unwrap_or_else(|| "unknown".into()),
            )
        }
        None => format!("{pkg} (cargo)"),
    };
    println!("cargo:rustc-env=DC_BUILD_BANNER={banner}");
}
