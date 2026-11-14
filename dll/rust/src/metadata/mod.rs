//! IL2CPP `global-metadata.dat` parser (Phase R1).
//!
//! A behaviour-identical Rust port of the C++
//! `Discrete::IL2CPP::Metadata::Parser` (dll/src/il2cpp/MetadataParser.cpp +
//! MetadataLayout.h). The C++ class is kept as a thin wrapper over the C ABI
//! in [`ffi`] (declared in `include/discrete_core.h`, "Phase R1" section), so
//! every consumer — the IL2CPP backend, `ExportAPI`, `metadata-cli` — keeps
//! its API.
//!
//! * [`layout`] — versioned on-disk row layouts, strides and index sizes
//!   (v24.0 / v24.1 / v24.2-5 / v27 / v29 / v31 / v32-37 / v38-39).
//! * [`parser`] — load, header + table binding, v24 sub-variant detection,
//!   the `<Module>` sanity check, and every getter.
//! * [`ffi`] — the `extern "C" dc_md_*` functions and `#[repr(C)]` rows.
//!
//! Byte strings are never validated as UTF-8; every slice a getter returns
//! borrows the parser's owned copy of the file.

pub mod ffi;
pub mod layout;
pub mod parser;

#[cfg(test)]
mod tests;

pub use layout::IndexSizes;
pub use parser::{
    FieldDefView, FieldDefaultBlob, ImageDefView, MethodDefView, ParameterDefView, Parser,
    TypeDefView, V24Variant,
};
