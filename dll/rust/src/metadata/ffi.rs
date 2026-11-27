//! C ABI for the metadata parser — the "Phase R1" section of
//! `include/discrete_core.h`, implemented exactly:
//!
//! * every body runs inside [`crate::ffi::guard`] with a fallback equal to
//!   the documented failure value;
//! * a NULL handle gets the "not loaded" answer (0 / empty / -1);
//! * `int32_t` "found" results are 1 (value written to `*out`) or 0
//!   (`*out` untouched);
//! * `dc_md_enumerate_*` write `min(total, cap)` rows and return `total`;
//!   `out` may be NULL when `cap == 0`;
//! * every [`DcBytes`] points into memory owned by the handle (the file
//!   buffer, or the decoded-string cache below) or into static memory, and
//!   stays valid until [`dc_md_free`].

use std::collections::HashMap;
use std::sync::Mutex;

use super::parser::{
    FieldDefView, FieldDefaultBlob, ImageDefView, MethodDefView, ParameterDefView, Parser,
    TypeDefView,
};
use crate::ffi::guard;

/// Opaque handle behind `DcMetadata*`.
pub struct DcMetadata {
    parser: Parser,
    /// `TryDecodeStringDefault` returns a *sanitised* copy in C++ (malformed
    /// UTF-8 becomes U+FFFD). When sanitising changes the bytes there is
    /// nothing in the file buffer to point at, so the result is interned here,
    /// keyed by data index, and lives as long as the handle. Boxed slices
    /// never move when the map rehashes, so handed-out pointers stay valid.
    decoded_strings: Mutex<HashMap<i32, Box<[u8]>>>,
}

impl DcMetadata {
    pub fn parser(&self) -> &Parser {
        &self.parser
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DcBytes {
    pub ptr: *const u8,
    pub len: usize,
}

impl DcBytes {
    pub const EMPTY: DcBytes = DcBytes {
        ptr: std::ptr::null(),
        len: 0,
    };

    fn from_slice(s: &[u8]) -> Self {
        if s.is_empty() {
            Self::EMPTY
        } else {
            Self {
                ptr: s.as_ptr(),
                len: s.len(),
            }
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DcTypeDef {
    pub index: i32,
    pub ns: DcBytes,
    pub name: DcBytes,
    pub declaring_type_index: i32,
    pub field_start: i32,
    pub field_count: u16,
    pub method_start: i32,
    pub method_count: u16,
    pub flags: u32,
    pub bitfield: u32,
    pub token: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DcFieldDef {
    pub index: i32,
    pub name: DcBytes,
    pub type_index: i32,
    pub token: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DcMethodDef {
    pub index: i32,
    pub name: DcBytes,
    pub declaring_type_index: i32,
    pub return_type_index: i32,
    pub parameter_start: i32,
    pub parameter_count: u16,
    pub flags: u32,
    pub token: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DcParameterDef {
    pub index: i32,
    pub name: DcBytes,
    pub type_index: i32,
    pub token: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DcImageDef {
    pub index: i32,
    pub name: DcBytes,
    pub type_start: i32,
    pub type_count: u32,
    pub token: u32,
    pub method_start: i32,
    pub method_end: i32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct DcFieldDefault {
    pub field_index: i32,
    pub type_index: i32,
    pub data_index: i32,
    /// Empty when data_index < 0 or the bytes are unavailable.
    pub data: DcBytes,
}

impl From<&TypeDefView<'_>> for DcTypeDef {
    fn from(v: &TypeDefView<'_>) -> Self {
        Self {
            index: v.index,
            ns: DcBytes::from_slice(v.ns),
            name: DcBytes::from_slice(v.name),
            declaring_type_index: v.declaring_type_index,
            field_start: v.field_start,
            field_count: v.field_count,
            method_start: v.method_start,
            method_count: v.method_count,
            flags: v.flags,
            bitfield: v.bitfield,
            token: v.token,
        }
    }
}

impl From<&FieldDefView<'_>> for DcFieldDef {
    fn from(v: &FieldDefView<'_>) -> Self {
        Self {
            index: v.index,
            name: DcBytes::from_slice(v.name),
            type_index: v.type_index,
            token: v.token,
        }
    }
}

impl From<&MethodDefView<'_>> for DcMethodDef {
    fn from(v: &MethodDefView<'_>) -> Self {
        Self {
            index: v.index,
            name: DcBytes::from_slice(v.name),
            declaring_type_index: v.declaring_type_index,
            return_type_index: v.return_type_index,
            parameter_start: v.parameter_start,
            parameter_count: v.parameter_count,
            flags: v.flags,
            token: v.token,
        }
    }
}

impl From<&ParameterDefView<'_>> for DcParameterDef {
    fn from(v: &ParameterDefView<'_>) -> Self {
        Self {
            index: v.index,
            name: DcBytes::from_slice(v.name),
            type_index: v.type_index,
            token: v.token,
        }
    }
}

impl From<&ImageDefView<'_>> for DcImageDef {
    fn from(v: &ImageDefView<'_>) -> Self {
        Self {
            index: v.index,
            name: DcBytes::from_slice(v.name),
            type_start: v.type_start,
            type_count: v.type_count,
            token: v.token,
            method_start: v.method_start,
            method_end: v.method_end,
        }
    }
}

impl From<&FieldDefaultBlob<'_>> for DcFieldDefault {
    fn from(v: &FieldDefaultBlob<'_>) -> Self {
        Self {
            field_index: v.field_index,
            type_index: v.type_index,
            data_index: v.data_index,
            data: DcBytes::from_slice(v.data),
        }
    }
}

// ---------------------------------------------------------------------------
// Pointer plumbing
// ---------------------------------------------------------------------------

/// # Safety
/// `md` must be NULL or a live handle from `dc_md_load_path_w`.
unsafe fn handle<'a>(md: *const DcMetadata) -> Option<&'a DcMetadata> {
    // SAFETY: guaranteed by the caller (header contract: NULL or live handle).
    unsafe { md.as_ref() }
}

/// # Safety
/// `md` must be NULL or a live handle from `dc_md_load_path_w`.
unsafe fn parser<'a>(md: *const DcMetadata) -> Option<&'a Parser> {
    // SAFETY: forwarded caller contract.
    unsafe { handle(md) }.map(|h| &h.parser)
}

/// Writes `v` to `out` when `v` is `Some` and `out` is non-NULL; returns the
/// "found" flag (1 / 0). A NULL `out` only reports presence.
///
/// # Safety
/// `out` must be NULL or valid for a write of `T`.
unsafe fn write_found<T>(out: *mut T, v: Option<T>) -> i32 {
    match v {
        Some(v) => {
            if !out.is_null() {
                // SAFETY: non-NULL `out` is writable per the caller contract.
                unsafe { out.write(v) };
            }
            1
        }
        None => 0,
    }
}

/// Copies `min(rows.len(), cap)` converted rows to `out`, returns the total.
///
/// # Safety
/// `out` must be NULL or valid for `cap` writes of `T`.
unsafe fn write_rows<S, T>(rows: &[S], out: *mut T, cap: usize) -> usize
where
    T: for<'x> From<&'x S>,
{
    let n = rows.len().min(cap);
    if !out.is_null() {
        for (i, r) in rows.iter().take(n).enumerate() {
            // SAFETY: i < n <= cap and `out` holds `cap` elements.
            unsafe { out.add(i).write(T::from(r)) };
        }
    }
    rows.len()
}

/// # Safety
/// `ptr` must be NULL or valid for `len` reads.
unsafe fn byte_slice<'a>(ptr: *const u8, len: usize) -> &'a [u8] {
    if ptr.is_null() || len == 0 {
        &[]
    } else {
        // SAFETY: non-NULL `ptr` covers `len` bytes per the caller contract.
        unsafe { std::slice::from_raw_parts(ptr, len) }
    }
}

// ---------------------------------------------------------------------------
// Lifetime
// ---------------------------------------------------------------------------

/// Reads and parses the file at `path` (UTF-16, `len` code units). Returns a
/// handle even when loading fails (check `dc_md_loaded`); NULL only on an
/// internal panic. A NULL `path` is treated as the empty path.
///
/// # Safety
/// `path` must be NULL or valid for `len` reads of `u16`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_load_path_w(path: *const u16, len: usize) -> *mut DcMetadata {
    guard("dc_md_load_path_w", std::ptr::null_mut(), || {
        let wide: &[u16] = if path.is_null() || len == 0 {
            &[]
        } else {
            // SAFETY: non-NULL `path` covers `len` code units per the contract.
            unsafe { std::slice::from_raw_parts(path, len) }
        };
        let md = DcMetadata {
            parser: Parser::load_from_path(wide),
            decoded_strings: Mutex::new(HashMap::new()),
        };
        Box::into_raw(Box::new(md))
    })
}

/// Frees a handle. NULL is a no-op.
///
/// # Safety
/// `md` must be NULL or a handle from `dc_md_load_path_w` not yet freed; no
/// pointer obtained from it may be used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_free(md: *mut DcMetadata) {
    guard("dc_md_free", (), || {
        if !md.is_null() {
            // SAFETY: `md` came from Box::into_raw in dc_md_load_path_w.
            drop(unsafe { Box::from_raw(md) });
        }
    });
}

// ---------------------------------------------------------------------------
// Header-level queries
// ---------------------------------------------------------------------------

/// # Safety
/// `md` must be NULL or a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_loaded(md: *const DcMetadata) -> i32 {
    guard("dc_md_loaded", 0, || {
        // SAFETY: caller contract.
        unsafe { parser(md) }.map_or(0, |p| i32::from(p.loaded()))
    })
}

/// D-M3: returns 1 when any metadata table was zeroed/demoted due to stride or
/// section-count validation failure, 0 otherwise.
///
/// # Safety
/// `md` must be NULL or a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_tables_degraded(md: *const DcMetadata) -> i32 {
    guard("dc_md_tables_degraded", 0, || {
        // SAFETY: caller contract.
        unsafe { parser(md) }.map_or(0, |p| i32::from(p.tables_degraded()))
    })
}

/// D-L2: returns 1 when the loaded metadata version has a supported
/// TypeDefinition table layout (v24/27/29/31-39), 0 for strings-only versions
/// (v16-23/25/26/28/30) or an unloaded/NULL handle.
///
/// # Safety
/// `md` must be NULL or a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_type_tables_supported(md: *const DcMetadata) -> i32 {
    guard("dc_md_type_tables_supported", 0, || {
        // SAFETY: caller contract.
        unsafe { parser(md) }.map_or(0, |p| i32::from(p.type_tables_supported()))
    })
}

/// # Safety
/// `md` must be NULL or a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_version(md: *const DcMetadata) -> i32 {
    guard("dc_md_version", 0, || {
        // SAFETY: caller contract.
        unsafe { parser(md) }.map_or(0, |p| p.version())
    })
}

/// # Safety
/// `md` must be NULL or a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_v24_sub_version_label(md: *const DcMetadata) -> DcBytes {
    guard("dc_md_v24_sub_version_label", DcBytes::EMPTY, || {
        // SAFETY: caller contract.
        unsafe { parser(md) }.map_or(DcBytes::EMPTY, |p| {
            DcBytes::from_slice(p.v24_sub_version_label())
        })
    })
}

// ---------------------------------------------------------------------------
// Strings
// ---------------------------------------------------------------------------

/// # Safety
/// `md` must be NULL or a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_string_literal_count(md: *const DcMetadata) -> u32 {
    guard("dc_md_string_literal_count", 0, || {
        // SAFETY: caller contract.
        unsafe { parser(md) }.map_or(0, |p| p.string_literal_count())
    })
}

/// # Safety
/// `md` must be NULL or a live handle; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_get_string_literal(
    md: *const DcMetadata,
    idx: u32,
    out: *mut DcBytes,
) -> i32 {
    guard("dc_md_get_string_literal", 0, || {
        // SAFETY: caller contract.
        let v = unsafe { parser(md) }
            .and_then(|p| p.get_string_literal(idx))
            .map(DcBytes::from_slice);
        // SAFETY: caller contract on `out`.
        unsafe { write_found(out, v) }
    })
}

/// # Safety
/// `md` must be NULL or a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_get_heap_string(md: *const DcMetadata, name_index: i32) -> DcBytes {
    guard("dc_md_get_heap_string", DcBytes::EMPTY, || {
        // SAFETY: caller contract.
        unsafe { parser(md) }.map_or(DcBytes::EMPTY, |p| {
            DcBytes::from_slice(p.get_heap_string(name_index))
        })
    })
}

// ---------------------------------------------------------------------------
// Type definitions / fields
// ---------------------------------------------------------------------------

/// # Safety
/// `md` must be NULL or a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_type_definition_count(md: *const DcMetadata) -> u32 {
    guard("dc_md_type_definition_count", 0, || {
        // SAFETY: caller contract.
        unsafe { parser(md) }.map_or(0, |p| p.type_definition_count())
    })
}

/// # Safety
/// `md` must be NULL or a live handle; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_get_type_definition(
    md: *const DcMetadata,
    idx: i32,
    out: *mut DcTypeDef,
) -> i32 {
    guard("dc_md_get_type_definition", 0, || {
        // SAFETY: caller contract.
        let v = unsafe { parser(md) }
            .and_then(|p| p.get_type_definition(idx))
            .map(|t| DcTypeDef::from(&t));
        // SAFETY: caller contract on `out`.
        unsafe { write_found(out, v) }
    })
}

/// # Safety
/// `md` must be NULL or a live handle; `ns` / `name` NULL or valid for
/// `ns_len` / `name_len` bytes; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_find_type_by_name(
    md: *const DcMetadata,
    ns: *const u8,
    ns_len: usize,
    name: *const u8,
    name_len: usize,
    out: *mut DcTypeDef,
) -> i32 {
    guard("dc_md_find_type_by_name", 0, || {
        // SAFETY: caller contract on the byte strings.
        let (ns, name) = unsafe { (byte_slice(ns, ns_len), byte_slice(name, name_len)) };
        // SAFETY: caller contract.
        let v = unsafe { parser(md) }
            .and_then(|p| p.find_type_by_name(ns, name))
            .map(|t| DcTypeDef::from(&t));
        // SAFETY: caller contract on `out`.
        unsafe { write_found(out, v) }
    })
}

/// Fields of `ty` (only `field_start` / `field_count` are read). A NULL `ty`
/// yields 0.
///
/// # Safety
/// `md` must be NULL or a live handle; `ty` NULL or readable; `out` NULL or
/// valid for `cap` writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_enumerate_type_fields(
    md: *const DcMetadata,
    ty: *const DcTypeDef,
    out: *mut DcFieldDef,
    cap: usize,
) -> usize {
    guard("dc_md_enumerate_type_fields", 0, || {
        // SAFETY: caller contract.
        let (Some(p), Some(ty)) = (unsafe { parser(md) }, unsafe { ty.as_ref() }) else {
            return 0;
        };
        let rows = p.enumerate_fields(ty.field_start, ty.field_count);
        // SAFETY: caller contract on `out` / `cap`.
        unsafe { write_rows(&rows, out, cap) }
    })
}

/// # Safety
/// `md` must be NULL or a live handle; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_find_field_default(
    md: *const DcMetadata,
    global_field_index: i32,
    out: *mut DcFieldDefault,
) -> i32 {
    guard("dc_md_find_field_default", 0, || {
        // SAFETY: caller contract.
        let v = unsafe { parser(md) }
            .and_then(|p| p.find_field_default(global_field_index))
            .map(|d| DcFieldDefault::from(&d));
        // SAFETY: caller contract on `out`.
        unsafe { write_found(out, v) }
    })
}

/// # Safety
/// `md` must be NULL or a live handle; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_try_decode_string_default(
    md: *const DcMetadata,
    data_index: i32,
    out: *mut DcBytes,
) -> i32 {
    guard("dc_md_try_decode_string_default", 0, || {
        // SAFETY: caller contract.
        let Some(h) = (unsafe { handle(md) }) else {
            return 0;
        };
        let v = h
            .parser
            .try_decode_string_default(data_index)
            .and_then(|cow| match cow {
                std::borrow::Cow::Borrowed(s) => Some(DcBytes::from_slice(s)),
                std::borrow::Cow::Owned(v) => {
                    let mut cache = h.decoded_strings.lock().unwrap_or_else(|e| e.into_inner());
                    if let Some(existing) = cache.get(&data_index) {
                        return Some(DcBytes::from_slice(existing));
                    }
                    // D-L3: fallible reserve so OOM returns 0 instead of aborting the host.
                    if cache.try_reserve(1).is_err() {
                        return None;
                    }
                    let entry = cache
                        .entry(data_index)
                        .or_insert_with(|| v.into_boxed_slice());
                    // The boxed bytes are owned by the handle and never removed
                    // or mutated until dc_md_free, so the pointer outlives the
                    // lock guard.
                    Some(DcBytes::from_slice(entry))
                }
            });
        // SAFETY: caller contract on `out`.
        unsafe { write_found(out, v) }
    })
}

/// # Safety
/// `md` must be NULL or a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_get_declaring_type_index(
    md: *const DcMetadata,
    type_def_idx: i32,
) -> i32 {
    guard("dc_md_get_declaring_type_index", -1, || {
        // SAFETY: caller contract.
        unsafe { parser(md) }.map_or(-1, |p| p.get_declaring_type_index(type_def_idx))
    })
}

// ---------------------------------------------------------------------------
// Methods / parameters
// ---------------------------------------------------------------------------

/// # Safety
/// `md` must be NULL or a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_method_definition_count(md: *const DcMetadata) -> u32 {
    guard("dc_md_method_definition_count", 0, || {
        // SAFETY: caller contract.
        unsafe { parser(md) }.map_or(0, |p| p.method_definition_count())
    })
}

/// # Safety
/// `md` must be NULL or a live handle; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_get_method_definition(
    md: *const DcMetadata,
    idx: i32,
    out: *mut DcMethodDef,
) -> i32 {
    guard("dc_md_get_method_definition", 0, || {
        // SAFETY: caller contract.
        let v = unsafe { parser(md) }
            .and_then(|p| p.get_method_definition(idx))
            .map(|m| DcMethodDef::from(&m));
        // SAFETY: caller contract on `out`.
        unsafe { write_found(out, v) }
    })
}

/// Methods of `ty` (only `method_start` / `method_count` are read). A NULL
/// `ty` yields 0.
///
/// # Safety
/// `md` must be NULL or a live handle; `ty` NULL or readable; `out` NULL or
/// valid for `cap` writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_enumerate_type_methods(
    md: *const DcMetadata,
    ty: *const DcTypeDef,
    out: *mut DcMethodDef,
    cap: usize,
) -> usize {
    guard("dc_md_enumerate_type_methods", 0, || {
        // SAFETY: caller contract.
        let (Some(p), Some(ty)) = (unsafe { parser(md) }, unsafe { ty.as_ref() }) else {
            return 0;
        };
        let rows = p.enumerate_methods(ty.method_start, ty.method_count);
        // SAFETY: caller contract on `out` / `cap`.
        unsafe { write_rows(&rows, out, cap) }
    })
}

/// # Safety
/// `md` must be NULL or a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_parameter_count(md: *const DcMetadata) -> u32 {
    guard("dc_md_parameter_count", 0, || {
        // SAFETY: caller contract.
        unsafe { parser(md) }.map_or(0, |p| p.parameter_count())
    })
}

/// # Safety
/// `md` must be NULL or a live handle; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_get_parameter(
    md: *const DcMetadata,
    idx: i32,
    out: *mut DcParameterDef,
) -> i32 {
    guard("dc_md_get_parameter", 0, || {
        // SAFETY: caller contract.
        let v = unsafe { parser(md) }
            .and_then(|p| p.get_parameter(idx))
            .map(|x| DcParameterDef::from(&x));
        // SAFETY: caller contract on `out`.
        unsafe { write_found(out, v) }
    })
}

/// Parameters of `method` (only `parameter_start` / `parameter_count` are
/// read). A NULL `method` yields 0.
///
/// # Safety
/// `md` must be NULL or a live handle; `method` NULL or readable; `out` NULL
/// or valid for `cap` writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_enumerate_method_parameters(
    md: *const DcMetadata,
    method: *const DcMethodDef,
    out: *mut DcParameterDef,
    cap: usize,
) -> usize {
    guard("dc_md_enumerate_method_parameters", 0, || {
        // SAFETY: caller contract.
        let (Some(p), Some(m)) = (unsafe { parser(md) }, unsafe { method.as_ref() }) else {
            return 0;
        };
        let rows = p.enumerate_parameters(m.parameter_start, m.parameter_count);
        // SAFETY: caller contract on `out` / `cap`.
        unsafe { write_rows(&rows, out, cap) }
    })
}

// ---------------------------------------------------------------------------
// Images
// ---------------------------------------------------------------------------

/// # Safety
/// `md` must be NULL or a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_image_count(md: *const DcMetadata) -> u32 {
    guard("dc_md_image_count", 0, || {
        // SAFETY: caller contract.
        unsafe { parser(md) }.map_or(0, |p| p.image_count())
    })
}

/// # Safety
/// `md` must be NULL or a live handle; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_get_image(
    md: *const DcMetadata,
    idx: i32,
    out: *mut DcImageDef,
) -> i32 {
    guard("dc_md_get_image", 0, || {
        // SAFETY: caller contract.
        let v = unsafe { parser(md) }
            .and_then(|p| p.get_image(idx))
            .map(|i| DcImageDef::from(&i));
        // SAFETY: caller contract on `out`.
        unsafe { write_found(out, v) }
    })
}

/// # Safety
/// `md` must be NULL or a live handle; `out` NULL or valid for `cap` writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_enumerate_images(
    md: *const DcMetadata,
    out: *mut DcImageDef,
    cap: usize,
) -> usize {
    guard("dc_md_enumerate_images", 0, || {
        // SAFETY: caller contract.
        let Some(p) = (unsafe { parser(md) }) else {
            return 0;
        };
        let rows = p.enumerate_images();
        // SAFETY: caller contract on `out` / `cap`.
        unsafe { write_rows(&rows, out, cap) }
    })
}

/// # Safety
/// `md` must be NULL or a live handle; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dc_md_find_image_containing_method(
    md: *const DcMetadata,
    global_method_idx: i32,
    out: *mut DcImageDef,
) -> i32 {
    guard("dc_md_find_image_containing_method", 0, || {
        // SAFETY: caller contract.
        let v = unsafe { parser(md) }
            .and_then(|p| p.find_image_containing_method(global_method_idx))
            .map(|i| DcImageDef::from(&i));
        // SAFETY: caller contract on `out`.
        unsafe { write_found(out, v) }
    })
}

// A handle is shared across threads by the C++ side; keep that a compile-time
// guarantee.
const _: () = {
    const fn assert_send_sync<T: Send + Sync>() {}
    assert_send_sync::<DcMetadata>();
};
