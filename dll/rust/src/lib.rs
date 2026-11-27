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

// D-M2: Compile-time C ABI layout checks for EVERY #[repr(C)] struct in
// `include/discrete_core.h`, pinned identically on x64 and x86 (mirrored by
// `static_assert` in `dll/src/shared/RustCore.cpp`). Any field addition,
// reordering, or padding drift fails the build immediately.
const _: () = {
    use core::mem::{align_of, offset_of, size_of};
    use crate::aob::DcAobResult;
    use crate::chain::{DcChainMatch, DcChainStats};
    use crate::metadata::ffi::{
        DcBytes, DcFieldDef, DcFieldDefault, DcImageDef, DcMethodDef, DcParameterDef, DcTypeDef,
    };
    use crate::xref::{DcXrefInstHit, DcXrefRange, DcXrefRipHit, DcXrefSection};

    #[cfg(target_pointer_width = "64")]
    {
        assert!(size_of::<DcBytes>() == 16 && align_of::<DcBytes>() == 8);
        assert!(offset_of!(DcBytes, ptr) == 0 && offset_of!(DcBytes, len) == 8);

        assert!(size_of::<DcTypeDef>() == 72 && align_of::<DcTypeDef>() == 8);
        assert!(offset_of!(DcTypeDef, index) == 0);
        assert!(offset_of!(DcTypeDef, ns) == 8);
        assert!(offset_of!(DcTypeDef, name) == 24);
        assert!(offset_of!(DcTypeDef, declaring_type_index) == 40);
        assert!(offset_of!(DcTypeDef, field_start) == 44);
        assert!(offset_of!(DcTypeDef, field_count) == 48);
        assert!(offset_of!(DcTypeDef, method_start) == 52);
        assert!(offset_of!(DcTypeDef, method_count) == 56);
        assert!(offset_of!(DcTypeDef, flags) == 60);
        assert!(offset_of!(DcTypeDef, bitfield) == 64);
        assert!(offset_of!(DcTypeDef, token) == 68);

        assert!(size_of::<DcFieldDef>() == 32 && align_of::<DcFieldDef>() == 8);
        assert!(offset_of!(DcFieldDef, index) == 0);
        assert!(offset_of!(DcFieldDef, name) == 8);
        assert!(offset_of!(DcFieldDef, type_index) == 24);
        assert!(offset_of!(DcFieldDef, token) == 28);

        assert!(size_of::<DcMethodDef>() == 48 && align_of::<DcMethodDef>() == 8);
        assert!(offset_of!(DcMethodDef, index) == 0);
        assert!(offset_of!(DcMethodDef, name) == 8);
        assert!(offset_of!(DcMethodDef, declaring_type_index) == 24);
        assert!(offset_of!(DcMethodDef, return_type_index) == 28);
        assert!(offset_of!(DcMethodDef, parameter_start) == 32);
        assert!(offset_of!(DcMethodDef, parameter_count) == 36);
        assert!(offset_of!(DcMethodDef, flags) == 40);
        assert!(offset_of!(DcMethodDef, token) == 44);

        assert!(size_of::<DcParameterDef>() == 32 && align_of::<DcParameterDef>() == 8);
        assert!(offset_of!(DcParameterDef, index) == 0);
        assert!(offset_of!(DcParameterDef, name) == 8);
        assert!(offset_of!(DcParameterDef, type_index) == 24);
        assert!(offset_of!(DcParameterDef, token) == 28);

        assert!(size_of::<DcImageDef>() == 48 && align_of::<DcImageDef>() == 8);
        assert!(offset_of!(DcImageDef, index) == 0);
        assert!(offset_of!(DcImageDef, name) == 8);
        assert!(offset_of!(DcImageDef, type_start) == 24);
        assert!(offset_of!(DcImageDef, type_count) == 28);
        assert!(offset_of!(DcImageDef, token) == 32);
        assert!(offset_of!(DcImageDef, method_start) == 36);
        assert!(offset_of!(DcImageDef, method_end) == 40);

        assert!(size_of::<DcFieldDefault>() == 32 && align_of::<DcFieldDefault>() == 8);
        assert!(offset_of!(DcFieldDefault, field_index) == 0);
        assert!(offset_of!(DcFieldDefault, type_index) == 4);
        assert!(offset_of!(DcFieldDefault, data_index) == 8);
        assert!(offset_of!(DcFieldDefault, data) == 16);

        assert!(size_of::<DcAobResult>() == 64 && align_of::<DcAobResult>() == 8);
        assert!(offset_of!(DcAobResult, length) == 0);
        assert!(offset_of!(DcAobResult, instruction_count) == 8);
        assert!(offset_of!(DcAobResult, rel_branch_count) == 16);
        assert!(offset_of!(DcAobResult, rip_rel_count) == 24);
        assert!(offset_of!(DcAobResult, bailed_at) == 32);
        assert!(offset_of!(DcAobResult, requested_length) == 40);
        assert!(offset_of!(DcAobResult, pattern_len) == 48);
        assert!(offset_of!(DcAobResult, decoder_bailed) == 56);
        assert!(offset_of!(DcAobResult, stopped_at_terminal) == 57);

        assert!(size_of::<DcChainMatch>() == 24 && align_of::<DcChainMatch>() == 8);
        assert!(offset_of!(DcChainMatch, hit_ip) == 0);
        assert!(offset_of!(DcChainMatch, hop_one_ip) == 8);
        assert!(offset_of!(DcChainMatch, is_write) == 16);

        assert!(size_of::<DcChainStats>() == 48 && align_of::<DcChainStats>() == 8);
        assert!(offset_of!(DcChainStats, hop_one_candidates) == 0);
        assert!(offset_of!(DcChainStats, chain_broken) == 8);
        assert!(offset_of!(DcChainStats, chains_emitted) == 16);
        assert!(offset_of!(DcChainStats, bytes_scanned) == 24);
        assert!(offset_of!(DcChainStats, failed_reads) == 32);
        assert!(offset_of!(DcChainStats, valid) == 40);

        assert!(size_of::<DcXrefSection>() == 16 && align_of::<DcXrefSection>() == 8);
        assert!(offset_of!(DcXrefSection, va) == 0 && offset_of!(DcXrefSection, size) == 8);

        assert!(size_of::<DcXrefRipHit>() == 16 && align_of::<DcXrefRipHit>() == 8);
        assert!(offset_of!(DcXrefRipHit, ip) == 0 && offset_of!(DcXrefRipHit, opcode) == 8);

        assert!(size_of::<DcXrefRange>() == 16 && align_of::<DcXrefRange>() == 8);
        assert!(offset_of!(DcXrefRange, start) == 0 && offset_of!(DcXrefRange, len) == 8);

        assert!(size_of::<DcXrefInstHit>() == 24 && align_of::<DcXrefInstHit>() == 8);
        assert!(offset_of!(DcXrefInstHit, method) == 0);
        assert!(offset_of!(DcXrefInstHit, ip) == 8);
        assert!(offset_of!(DcXrefInstHit, write) == 16);
    }

    #[cfg(target_pointer_width = "32")]
    {
        assert!(size_of::<DcBytes>() == 8 && align_of::<DcBytes>() == 4);
        assert!(offset_of!(DcBytes, ptr) == 0 && offset_of!(DcBytes, len) == 4);

        assert!(size_of::<DcTypeDef>() == 52 && align_of::<DcTypeDef>() == 4);
        assert!(offset_of!(DcTypeDef, index) == 0);
        assert!(offset_of!(DcTypeDef, ns) == 4);
        assert!(offset_of!(DcTypeDef, name) == 12);
        assert!(offset_of!(DcTypeDef, declaring_type_index) == 20);
        assert!(offset_of!(DcTypeDef, field_start) == 24);
        assert!(offset_of!(DcTypeDef, field_count) == 28);
        assert!(offset_of!(DcTypeDef, method_start) == 32);
        assert!(offset_of!(DcTypeDef, method_count) == 36);
        assert!(offset_of!(DcTypeDef, flags) == 40);
        assert!(offset_of!(DcTypeDef, bitfield) == 44);
        assert!(offset_of!(DcTypeDef, token) == 48);

        assert!(size_of::<DcFieldDef>() == 20 && align_of::<DcFieldDef>() == 4);
        assert!(offset_of!(DcFieldDef, index) == 0);
        assert!(offset_of!(DcFieldDef, name) == 4);
        assert!(offset_of!(DcFieldDef, type_index) == 12);
        assert!(offset_of!(DcFieldDef, token) == 16);

        assert!(size_of::<DcMethodDef>() == 36 && align_of::<DcMethodDef>() == 4);
        assert!(offset_of!(DcMethodDef, index) == 0);
        assert!(offset_of!(DcMethodDef, name) == 4);
        assert!(offset_of!(DcMethodDef, declaring_type_index) == 12);
        assert!(offset_of!(DcMethodDef, return_type_index) == 16);
        assert!(offset_of!(DcMethodDef, parameter_start) == 20);
        assert!(offset_of!(DcMethodDef, parameter_count) == 24);
        assert!(offset_of!(DcMethodDef, flags) == 28);
        assert!(offset_of!(DcMethodDef, token) == 32);

        assert!(size_of::<DcParameterDef>() == 20 && align_of::<DcParameterDef>() == 4);
        assert!(offset_of!(DcParameterDef, index) == 0);
        assert!(offset_of!(DcParameterDef, name) == 4);
        assert!(offset_of!(DcParameterDef, type_index) == 12);
        assert!(offset_of!(DcParameterDef, token) == 16);

        assert!(size_of::<DcImageDef>() == 32 && align_of::<DcImageDef>() == 4);
        assert!(offset_of!(DcImageDef, index) == 0);
        assert!(offset_of!(DcImageDef, name) == 4);
        assert!(offset_of!(DcImageDef, type_start) == 12);
        assert!(offset_of!(DcImageDef, type_count) == 16);
        assert!(offset_of!(DcImageDef, token) == 20);
        assert!(offset_of!(DcImageDef, method_start) == 24);
        assert!(offset_of!(DcImageDef, method_end) == 28);

        assert!(size_of::<DcFieldDefault>() == 20 && align_of::<DcFieldDefault>() == 4);
        assert!(offset_of!(DcFieldDefault, field_index) == 0);
        assert!(offset_of!(DcFieldDefault, type_index) == 4);
        assert!(offset_of!(DcFieldDefault, data_index) == 8);
        assert!(offset_of!(DcFieldDefault, data) == 12);

        assert!(size_of::<DcAobResult>() == 32 && align_of::<DcAobResult>() == 4);
        assert!(offset_of!(DcAobResult, length) == 0);
        assert!(offset_of!(DcAobResult, instruction_count) == 4);
        assert!(offset_of!(DcAobResult, rel_branch_count) == 8);
        assert!(offset_of!(DcAobResult, rip_rel_count) == 12);
        assert!(offset_of!(DcAobResult, bailed_at) == 16);
        assert!(offset_of!(DcAobResult, requested_length) == 20);
        assert!(offset_of!(DcAobResult, pattern_len) == 24);
        assert!(offset_of!(DcAobResult, decoder_bailed) == 28);
        assert!(offset_of!(DcAobResult, stopped_at_terminal) == 29);

        assert!(size_of::<DcChainMatch>() == 12 && align_of::<DcChainMatch>() == 4);
        assert!(offset_of!(DcChainMatch, hit_ip) == 0);
        assert!(offset_of!(DcChainMatch, hop_one_ip) == 4);
        assert!(offset_of!(DcChainMatch, is_write) == 8);

        assert!(size_of::<DcChainStats>() == 24 && align_of::<DcChainStats>() == 4);
        assert!(offset_of!(DcChainStats, hop_one_candidates) == 0);
        assert!(offset_of!(DcChainStats, chain_broken) == 4);
        assert!(offset_of!(DcChainStats, chains_emitted) == 8);
        assert!(offset_of!(DcChainStats, bytes_scanned) == 12);
        assert!(offset_of!(DcChainStats, failed_reads) == 16);
        assert!(offset_of!(DcChainStats, valid) == 20);

        assert!(size_of::<DcXrefSection>() == 8 && align_of::<DcXrefSection>() == 4);
        assert!(offset_of!(DcXrefSection, va) == 0 && offset_of!(DcXrefSection, size) == 4);

        assert!(size_of::<DcXrefRipHit>() == 8 && align_of::<DcXrefRipHit>() == 4);
        assert!(offset_of!(DcXrefRipHit, ip) == 0 && offset_of!(DcXrefRipHit, opcode) == 4);

        assert!(size_of::<DcXrefRange>() == 8 && align_of::<DcXrefRange>() == 4);
        assert!(offset_of!(DcXrefRange, start) == 0 && offset_of!(DcXrefRange, len) == 4);

        assert!(size_of::<DcXrefInstHit>() == 12 && align_of::<DcXrefInstHit>() == 4);
        assert!(offset_of!(DcXrefInstHit, method) == 0);
        assert!(offset_of!(DcXrefInstHit, ip) == 4);
        assert!(offset_of!(DcXrefInstHit, write) == 8);
    }
};
