//! zydis-oracle: differential tests of the discrete_core decoder adapter
//! against the Zydis commit the legacy C++ DLL used (see README.md).
//!
//! The repo modules are compiled here unchanged through `#[path]`, at the
//! same `crate::` paths they use in dll/rust (`crate::ffi`, `crate::chain`,
//! `crate::xref`), so the code under test is the working tree itself. Their
//! own `#[cfg(test)]` modules come along (the oracle reuses
//! `chain::tests::{project, FakeMem, ...}`); `build.ps1` filters the run to
//! the `oracle_*` tests below.
//!
//! The Zydis side (native/*.cpp + vendor/zydis) is a static library that
//! build.ps1 compiles and build.rs links.

#![deny(unsafe_op_in_unsafe_fn)]
// The repo modules carry items only their own tests or the C ABI use.
#![allow(dead_code)]

#[path = "../../../dll/rust/src/ffi.rs"]
pub mod ffi;

#[path = "../../../dll/rust/src/chain/mod.rs"]
pub mod chain;

#[path = "../../../dll/rust/src/xref/mod.rs"]
pub mod xref;

#[cfg(all(test, target_arch = "x86_64"))]
mod zydis;

#[cfg(all(test, target_arch = "x86_64"))]
mod oracle_chain;

#[cfg(all(test, target_arch = "x86_64"))]
mod oracle_xref;

#[cfg(all(test, target_arch = "x86_64"))]
mod oracle_sweep;

#[cfg(all(test, target_arch = "x86_64"))]
mod oracle_tables;

#[cfg(all(test, target_arch = "x86_64"))]
mod oracle_probe;
