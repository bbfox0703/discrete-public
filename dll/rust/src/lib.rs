//! discrete_core — the Rust half of the Discrete DLL.
//!
//! Strangler-style migration (see docs/rust-core.md): the C++ DLL stays the
//! host, and self-contained pieces move here one at a time behind a C ABI.
//!
//! * Phase R0 — toolchain + FFI conventions (this crate's `ffi` module).
//! * Phase R1 — the IL2CPP `global-metadata.dat` parser (`metadata`).
//! * Phase R2 — pure algorithms: the AOB pattern builder (`aob`), the group
//!   scan SDR matcher (`group_match`), the value-scan refine comparator
//!   (`value`) and the IL2CPP static-field chain detector (`chain`, iced-x86).
//!   Follow-up: the IL2CPP field-xref scan kernels (`xref`, sharing the
//!   chain detector's decoder adapter).
//!   Host memory is only ever read through `ffi::Reader` (the SEH shim).
//!
//! FFI rules every `extern "C"` function in this crate follows:
//!
//! 1. The body runs inside [`ffi::guard`], so a panic becomes an error return
//!    value instead of unwinding into C++ (which would abort the host game).
//! 2. Pointers handed out point into memory owned by a handle; they stay valid
//!    until that handle is freed. Rust never transfers heap ownership to C++.
//! 3. Byte strings cross as `(ptr, len)` pairs and are NOT validated as UTF-8 —
//!    the C++ side copies them into `std::string` byte-for-byte.
//!
//! The matching C declarations live in `include/discrete_core.h`; keep the two
//! in sync by hand (there is deliberately no cbindgen dependency).

#![deny(unsafe_op_in_unsafe_fn)]

pub mod aob;
pub mod chain;
pub mod ffi;
pub mod group_match;
pub mod metadata;
pub mod value;
pub mod xref;
