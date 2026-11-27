//! The `global-metadata.dat` parser — a line-by-line port of the C++
//! `Discrete::IL2CPP::Metadata::Parser` (dll/src/il2cpp/MetadataParser.cpp,
//! removed when R1 closed; git history last in 5748299).
//!
//! Behaviour contract: every accepted / rejected input, version dispatch,
//! stride, bounds check, early-out, returned value and log line matches the
//! C++ implementation. Method docs name the C++ member they port. The only
//! intentional differences are:
//!
//! * Arithmetic that is undefined behaviour in C++ on signed overflow uses
//!   `wrapping_*` (the two's-complement result MSVC produces) where the value
//!   is only compared afterwards; every such spot carries a comment.
//! * The lazy child→parent nested-type map is built under a `OnceLock`
//!   instead of an unsynchronised `mutable` member, so a `Parser` is `Sync`.
//!
//! Strings are raw bytes: heap strings are ASCIIZ bounded by the heap end and
//! are never validated or converted as UTF-8. Every returned slice borrows the
//! parser's owned file buffer.

use std::borrow::Cow;
use std::collections::HashMap;
use std::ops::Deref;
use std::sync::OnceLock;

use super::layout::{
    self, IndexSizes, MethodDefLayout, TypeDefLayout, field_default_value_v29,
    field_definition_v24_0, field_definition_v29, field_definition_v38, get_index_size,
    header_prefix, header_v29, header_v38, image_definition_v29, image_definition_v38,
    method_definition_v38, parameter_definition_v24_0, parameter_definition_v29,
    parameter_definition_v38, section, string_literal, string_literal_v35, type_definition_tail,
    type_definition_v38,
};
use crate::ffi::{LogCategory, log, try_push};

/// Upper bound on the file size `ReadEntireFile` accepts (256 MiB).
const MAX_FILE_SIZE: u64 = 256 * 1024 * 1024;

/// The CLR canonical TypeDef[0].token (`<Module>`, TypeDef table, rid=1).
const MODULE_TOKEN: u32 = 0x0200_0001;

// ---------------------------------------------------------------------------
// Views (C++: Parser::TypeDefView etc.). Byte strings borrow the file buffer.
// ---------------------------------------------------------------------------

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TypeDefView<'a> {
    /// Global TypeDefinition index.
    pub index: i32,
    pub ns: &'a [u8],
    pub name: &'a [u8],
    /// -1 for top-level types (v38+ all-ones sentinel mapped to -1).
    pub declaring_type_index: i32,
    pub field_start: i32,
    pub field_count: u16,
    pub method_start: i32,
    pub method_count: u16,
    /// TypeAttributes (public/sealed/abstract/...).
    pub flags: u32,
    /// `Il2CppTypeDefinition::bitfield` — bit 0 = valuetype, bit 1 = enumtype.
    pub bitfield: u32,
    pub token: u32,
}

impl TypeDefView<'_> {
    pub fn is_value_type(&self) -> bool {
        (self.bitfield & 0x1) != 0
    }
    pub fn is_enum(&self) -> bool {
        (self.bitfield & 0x2) != 0
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FieldDefView<'a> {
    /// Global Field index.
    pub index: i32,
    pub name: &'a [u8],
    pub type_index: i32,
    pub token: u32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MethodDefView<'a> {
    /// Global Method index.
    pub index: i32,
    pub name: &'a [u8],
    /// TypeDefinition index.
    pub declaring_type_index: i32,
    /// Il2CppType index — opaque without the binary.
    pub return_type_index: i32,
    pub parameter_start: i32,
    pub parameter_count: u16,
    pub flags: u32,
    pub token: u32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ParameterDefView<'a> {
    /// Global Parameter index.
    pub index: i32,
    pub name: &'a [u8],
    /// Il2CppType index — opaque without the binary.
    pub type_index: i32,
    pub token: u32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ImageDefView<'a> {
    /// Image index in the metadata Images table.
    pub index: i32,
    /// Assembly name.
    pub name: &'a [u8],
    pub type_start: i32,
    pub type_count: u32,
    pub token: u32,
    /// First / last+1 global method indices belonging to this image, computed
    /// by walking the image's types. 0 / 0 if the image has no methods.
    pub method_start: i32,
    pub method_end: i32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FieldDefaultBlob<'a> {
    pub field_index: i32,
    pub type_index: i32,
    pub data_index: i32,
    /// Up to 256 bytes of the default-value blob; empty when `data_index` is
    /// out of range.
    pub data: &'a [u8],
}

/// Which v24 TypeDefinition layout to use. Latched by `detect_v24_variant` at
/// load time. Defaults to v24.2-v24.5 (the most common shape).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum V24Variant {
    /// 92-byte stride; `TYPE_DEFINITION_V24`.
    #[default]
    V24_2Through5,
    /// 100-byte stride; adds rgctxStart/rgctxCount.
    V24_1,
    /// 104-byte stride; adds customAttributeIndex too.
    V24_0,
}

// ---------------------------------------------------------------------------
// Little-endian readers. Callers only pass offsets the C++ code also reads
// (inside already-validated tables), so an out-of-range slice is a porting
// bug: it panics, and the FFI guard turns that into the failure value.
// ---------------------------------------------------------------------------

fn bytes<const N: usize>(b: &[u8], off: usize) -> [u8; N] {
    let mut a = [0u8; N];
    a.copy_from_slice(&b[off..off + N]);
    a
}
fn rd_u16(b: &[u8], off: usize) -> u16 {
    u16::from_le_bytes(bytes(b, off))
}
fn rd_u32(b: &[u8], off: usize) -> u32 {
    u32::from_le_bytes(bytes(b, off))
}
fn rd_i32(b: &[u8], off: usize) -> i32 {
    i32::from_le_bytes(bytes(b, off))
}
/// `std::uint32_t raw = 0; memcpy(&raw, p, width);` — zero-extending
/// little-endian read of a 1/2/4-byte variable-width index.
fn rd_uint_n(b: &[u8], off: usize, width: usize) -> u32 {
    b[off..off + width]
        .iter()
        .rev()
        .fold(0u32, |acc, &x| (acc << 8) | u32::from(x))
}

// ---------------------------------------------------------------------------
// Logging helpers.
// ---------------------------------------------------------------------------

fn log_md(msg: &str) {
    log(LogCategory::Metadata, msg);
}

/// `_snprintf_s(buf, cap, _TRUNCATE, ...)` + `Log::Write`: the formatted line
/// is cut to `cap - 1` bytes. The sink contract requires UTF-8, so a line
/// that is not valid UTF-8 after truncation (raw heap bytes, or a cut through
/// a multi-byte character) is passed through `from_utf8_lossy`.
fn log_md_truncated(cap: usize, msg: &[u8]) {
    let msg = &msg[..msg.len().min(cap - 1)];
    match std::str::from_utf8(msg) {
        Ok(s) => log_md(s),
        Err(_) => log_md(&String::from_utf8_lossy(msg)),
    }
}

// ---------------------------------------------------------------------------
// File read (C++: anonymous-namespace ReadEntireFile).
// ---------------------------------------------------------------------------

/// Mirrors `ReadEntireFile`: fails on open error, size <= 0, size > 256 MiB,
/// or a short read. The path is UTF-16; like `CreateFileW(path.c_str())` it
/// ends at the first embedded NUL.
#[cfg(test)]
pub(super) fn read_entire_file(path: &[u16]) -> Option<Vec<u8>> {
    let end = path.iter().position(|&c| c == 0).unwrap_or(path.len());
    read_file_impl(&path[..end])
}

/// Zero-filled buffer of `n` bytes, allocated fallibly.
///
/// C++ `out.resize(size)` throws `std::bad_alloc` on failure, which the pipe
/// server's `catch (...)` turns into an RPC error. In Rust an infallible
/// allocation failure aborts the process (it does not panic, so `ffi::guard`
/// cannot catch it). The closest non-crashing equivalent is to treat it as a
/// read failure: `None`, logged as "failed to read global-metadata.dat".
pub(super) fn alloc_zeroed(n: usize) -> Option<Vec<u8>> {
    let mut out = Vec::new();
    out.try_reserve_exact(n).ok()?;
    out.resize(n, 0); // never reallocates: capacity is already n
    Some(out)
}

/// Raw kernel32 calls, so the path reaches `CreateFileW` byte-for-byte like
/// the C++ code. `std::fs::OpenOptions::open` would rewrite paths of 248+
/// UTF-16 units into `\\?\` verbatim form and so accept long paths that a
/// non-long-path-aware process's plain `CreateFileW` rejects.
#[cfg(windows)]
mod win32 {
    use std::ffi::c_void;

    pub type Handle = *mut c_void;
    pub const INVALID_HANDLE_VALUE: Handle = -1isize as Handle;
    pub const GENERIC_READ: u32 = 0x8000_0000;
    pub const FILE_SHARE_READ: u32 = 0x1;
    pub const FILE_SHARE_WRITE: u32 = 0x2;
    pub const OPEN_EXISTING: u32 = 3;
    pub const FILE_ATTRIBUTE_NORMAL: u32 = 0x80;
    pub const PAGE_READONLY: u32 = 0x02;
    pub const FILE_MAP_READ: u32 = 0x0004;

    #[link(name = "kernel32")]
    unsafe extern "system" {
        pub fn CreateFileW(
            file_name: *const u16,
            desired_access: u32,
            share_mode: u32,
            security_attributes: *mut c_void,
            creation_disposition: u32,
            flags_and_attributes: u32,
            template_file: Handle,
        ) -> Handle;
        /// `LARGE_INTEGER*` — an 8-byte, 8-aligned signed integer on both
        /// x86 and x64.
        pub fn GetFileSizeEx(file: Handle, file_size: *mut i64) -> i32;
        pub fn ReadFile(
            file: Handle,
            buffer: *mut c_void,
            bytes_to_read: u32,
            bytes_read: *mut u32,
            overlapped: *mut c_void,
        ) -> i32;
        pub fn CreateFileMappingW(
            file: Handle,
            attributes: *mut c_void,
            protect: u32,
            max_size_high: u32,
            max_size_low: u32,
            name: *const u16,
        ) -> Handle;
        pub fn MapViewOfFile(
            file_mapping_object: Handle,
            desired_access: u32,
            file_offset_high: u32,
            file_offset_low: u32,
            bytes_to_map: usize,
        ) -> *mut c_void;
        pub fn UnmapViewOfFile(base_address: *const c_void) -> i32;
        pub fn CloseHandle(object: Handle) -> i32;
    }

    /// Closes the handle on every exit path, like the C++ `CloseHandle`
    /// calls before each `return`.
    pub struct OwnedHandle(pub Handle);

    impl Drop for OwnedHandle {
        fn drop(&mut self) {
            // SAFETY: self.0 is a valid handle returned by CreateFileW /
            // CreateFileMappingW and is closed exactly once.
            unsafe { CloseHandle(self.0) };
        }
    }
}

#[cfg(windows)]
struct MappedFile {
    ptr: *const u8,
    len: usize,
}

// SAFETY: MappedFile owns a read-only (PAGE_READONLY / FILE_MAP_READ)
// view that is never mutated and is unmapped exactly once on Drop.
#[cfg(windows)]
unsafe impl Send for MappedFile {}
#[cfg(windows)]
unsafe impl Sync for MappedFile {}

#[cfg(windows)]
impl Drop for MappedFile {
    fn drop(&mut self) {
        if !self.ptr.is_null() {
            // SAFETY: self.ptr was returned by MapViewOfFile.
            unsafe { win32::UnmapViewOfFile(self.ptr.cast()) };
        }
    }
}

#[cfg(windows)]
impl std::fmt::Debug for MappedFile {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MappedFile")
            .field("len", &self.len)
            .finish()
    }
}

/// Backing storage for `global-metadata.dat`: read-only file mapping when
/// loaded from disk on Windows (D-L4, avoids duplicating up to 256 MiB into
/// the game's private working set), or an owned `Vec<u8>` for in-memory
/// buffers / fallback reads.
#[derive(Debug, Default)]
enum MetadataBacking {
    #[default]
    Empty,
    Owned(Vec<u8>),
    #[cfg(windows)]
    Mapped(MappedFile),
}

impl Deref for MetadataBacking {
    type Target = [u8];

    fn deref(&self) -> &[u8] {
        match self {
            Self::Empty => &[],
            Self::Owned(v) => v.as_slice(),
            #[cfg(windows)]
            Self::Mapped(m) => {
                // SAFETY: m.ptr is a live read-only mapping of m.len bytes.
                unsafe { std::slice::from_raw_parts(m.ptr, m.len) }
            }
        }
    }
}

#[cfg(windows)]
fn read_handle_bytes(h: win32::Handle, len: usize) -> Option<Vec<u8>> {
    use win32::*;
    let mut out = alloc_zeroed(len)?;
    let mut total: usize = 0;
    let mut remaining = out.len() as u32;
    while remaining > 0 {
        let mut got: u32 = 0;
        // SAFETY: out[total..] has `remaining` writable bytes (total +
        // remaining == out.len()); got is a live u32; synchronous handle, so
        // no OVERLAPPED.
        let ok = unsafe {
            ReadFile(
                h,
                out.as_mut_ptr().add(total).cast(),
                remaining,
                &mut got,
                std::ptr::null_mut(),
            )
        };
        if ok == 0 || got == 0 {
            return None;
        }
        // ReadFile never reports more than requested; min() keeps the
        // invariant even if it did.
        let got = got.min(remaining);
        total += got as usize;
        remaining -= got;
    }
    Some(out)
}

#[cfg(windows)]
fn open_and_size(path: &[u16]) -> Option<(win32::OwnedHandle, usize)> {
    use win32::*;

    // NUL-terminated copy of the (already truncated) path, passed unchanged.
    // An empty path becomes CreateFileW(""), which fails just like C++.
    let mut wpath: Vec<u16> = Vec::with_capacity(path.len() + 1);
    wpath.extend_from_slice(path);
    wpath.push(0);

    // SAFETY: wpath is NUL-terminated and outlives the call; the remaining
    // pointer arguments are the documented NULLs.
    let h = unsafe {
        CreateFileW(
            wpath.as_ptr(),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            std::ptr::null_mut(),
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            std::ptr::null_mut(),
        )
    };
    if h == INVALID_HANDLE_VALUE {
        return None;
    }
    let h = OwnedHandle(h);

    let mut size: i64 = 0;
    // SAFETY: h is a valid file handle; size is a live, aligned i64.
    if unsafe { GetFileSizeEx(h.0, &mut size) } == 0 || size <= 0 || size as u64 > MAX_FILE_SIZE {
        return None;
    }
    // size is in (0, 256 MiB], so it fits usize on x86 and x64 and the DWORD
    // byte count.
    let len = usize::try_from(size).ok()?;
    Some((h, len))
}

#[cfg(all(windows, test))]
fn read_file_impl(path: &[u16]) -> Option<Vec<u8>> {
    let (h, len) = open_and_size(path)?;
    read_handle_bytes(h.0, len)
}

#[cfg(windows)]
fn map_file_impl(path: &[u16]) -> Option<MetadataBacking> {
    use win32::*;

    let (h, len) = open_and_size(path)?;
    // SAFETY: h.0 is a valid read-only file handle; NULL attributes & name;
    // size 0/0 maps the entire file.
    let map = unsafe {
        CreateFileMappingW(
            h.0,
            std::ptr::null_mut(),
            PAGE_READONLY,
            0,
            0,
            std::ptr::null(),
        )
    };
    if !map.is_null() {
        let map = OwnedHandle(map);
        // SAFETY: map.0 is a valid read-only section covering at least `len` bytes.
        let view = unsafe { MapViewOfFile(map.0, FILE_MAP_READ, 0, 0, len) };
        if !view.is_null() {
            // Both `map` and `h` close on drop; the mapped view holds its own
            // kernel reference until UnmapViewOfFile runs in MappedFile::drop.
            return Some(MetadataBacking::Mapped(MappedFile {
                ptr: view.cast::<u8>(),
                len,
            }));
        }
    }
    // Fallback to an owned heap copy if the file cannot be mapped.
    read_handle_bytes(h.0, len).map(MetadataBacking::Owned)
}

#[cfg(not(windows))]
fn read_file_impl(path: &[u16]) -> Option<Vec<u8>> {
    use std::io::Read;

    if path.is_empty() {
        return None;
    }
    let mut file = std::fs::File::open(String::from_utf16_lossy(path)).ok()?;
    let size = file.metadata().ok()?.len();
    if size == 0 || size > MAX_FILE_SIZE {
        return None;
    }
    let mut out = alloc_zeroed(usize::try_from(size).ok()?)?;
    file.read_exact(&mut out).ok()?;
    Some(out)
}

fn map_or_read_file(path: &[u16]) -> Option<MetadataBacking> {
    let end = path.iter().position(|&c| c == 0).unwrap_or(path.len());
    #[cfg(windows)]
    {
        map_file_impl(&path[..end])
    }
    #[cfg(not(windows))]
    {
        read_file_impl(&path[..end]).map(MetadataBacking::Owned)
    }
}

/// FNV-1a 64-bit hash of `(ns, 0xFF, name)` for the lazy type-by-name index (D-L12).
fn hash_ns_name(ns: &[u8], name: &[u8]) -> u64 {
    const OFFSET_BASIS: u64 = 0xcbf2_9ce4_8422_2325;
    const PRIME: u64 = 0x0000_0100_0000_01b3;
    let mut h = OFFSET_BASIS;
    for &b in ns {
        h ^= u64::from(b);
        h = h.wrapping_mul(PRIME);
    }
    h ^= 0xFF;
    h = h.wrapping_mul(PRIME);
    for &b in name {
        h ^= u64::from(b);
        h = h.wrapping_mul(PRIME);
    }
    h
}

// ---------------------------------------------------------------------------
// Parser
// ---------------------------------------------------------------------------

/// Port of `Discrete::IL2CPP::Metadata::Parser`. Immutable after load; the
/// only interior mutability is the lazy `OnceLock` lookup indices.
#[derive(Debug, Default)]
pub struct Parser {
    loaded: bool,
    version: i32,
    /// Owns or maps the whole file (read-only).
    data: MetadataBacking,

    // Cached prefix offsets (v29 layout, also valid for v24 / v27 / v31 /
    // v32-v37; for v38+ populated from the Il2CppSectionMetadata entries).
    string_literal_offset: i32,
    string_literal_size: i32,
    string_literal_data_offset: i32,
    string_literal_data_size: i32,
    string_offset: i32,
    string_size: i32,

    // Segment 2 — populated for v24 / v27 / v29 / v31 / v32-v39.
    tables_loaded: bool,
    /// D-M3: true when any v38+ table failed section.size == section.count * stride
    /// (or size % stride == 0), type_index_size inference failed, or TypeDef[0]
    /// validation demoted the file to strings-only.
    tables_degraded: bool,
    type_definitions_offset: i32,
    type_definitions_size: i32,
    fields_offset: i32,
    fields_size: i32,
    field_defaults_offset: i32,
    field_defaults_size: i32,
    field_default_data_offset: i32,
    field_default_data_size: i32,
    methods_offset: i32,
    methods_size: i32,
    parameters_offset: i32,
    parameters_size: i32,
    // 0/0 for v24.0/v24.1 (deferred) and whenever bounds checking rejected it.
    images_offset: i32,
    images_size: i32,
    // nestedTypeIndices[] — flat int32 TypeDefinitionIndex array.
    nested_types_offset: i32,
    nested_types_size: i32,

    v24_variant: V24Variant,
    index_sizes: IndexSizes,
    /// Total parameters.count from the v38 section header.
    parameters_count: i64,
    /// 8 bytes for v24-v34, 4 bytes for v35+.
    string_literal_entry_stride: i32,

    /// Lazy child→parent map (C++: `mutable declaringTypeMap_`).
    declaring_type_map: OnceLock<Vec<i32>>,
    /// D-L12: lazy FNV-1a(ns, 0xFF, name) → ascending TypeDef indices.
    type_by_name_map: OnceLock<HashMap<u64, Vec<i32>>>,
    /// D-L12: lazy per-image `(method_start, method_end)` ranges.
    image_method_ranges: OnceLock<Vec<(i32, i32)>>,
    /// D-L12: lazy `global_field_index` → first matching FieldDefault row index.
    field_default_map: OnceLock<HashMap<i32, u32>>,
}

impl Parser {
    fn new() -> Self {
        Self {
            string_literal_entry_stride: string_literal::SIZE as i32,
            ..Self::default()
        }
    }

    /// C++ `Parser::LoadFromPath`. Always returns a parser; check `loaded()`.
    /// A failed load keeps whatever state the C++ object would have had
    /// (e.g. `version()` after an unsupported-version rejection).
    pub fn load_from_path(path: &[u16]) -> Self {
        let mut p = Self::new();
        if path.is_empty() {
            log_md("MetadataParser: empty path");
            return p;
        }
        match map_or_read_file(path) {
            Some(data) => {
                p.load_from_data(data);
            }
            None => log_md("MetadataParser: failed to read global-metadata.dat"),
        }
        p
    }

    /// Runs everything `LoadFromPath` does after `ReadEntireFile` succeeded,
    /// over an in-memory file image. Used by tests and by any caller that
    /// already holds the bytes.
    pub fn load_from_bytes(data: Vec<u8>) -> Self {
        let mut p = Self::new();
        p.load_from_data(MetadataBacking::Owned(data));
        p
    }

    fn load_from_data(&mut self, data: MetadataBacking) -> bool {
        self.data = data;
        if !self.load_header() {
            self.data = MetadataBacking::Empty;
            return false;
        }
        // Mark loaded early so get_heap_string() (called transitively by
        // validate_type_table) doesn't short-circuit on its own guard.
        self.loaded = true;
        if self.type_def_sizeof() != 0 {
            self.tables_loaded = self.load_tables();
            if self.tables_loaded && !self.validate_type_table() {
                // Stride mismatch detected — demote to strings-only so callers
                // don't consume silently-corrupt type rows. loaded stays true.
                self.tables_loaded = false;
                self.tables_degraded = true;
            }
        }

        let line = format!(
            "MetadataParser: loaded version={} size={} strLitTable={} strLitData={} nameHeap={} \
             typeDefs={} fields={} fieldDefaults={} (typeTablesParsed={})",
            self.version,
            self.data.len(),
            self.string_literal_size,
            self.string_literal_data_size,
            self.string_size,
            self.type_definitions_size,
            self.fields_size,
            self.field_defaults_size,
            if self.tables_loaded { "yes" } else { "no" }
        );
        log_md_truncated(320, line.as_bytes());
        true
    }

    pub fn loaded(&self) -> bool {
        self.loaded
    }

    pub fn version(&self) -> i32 {
        self.version
    }

    /// D-M3: true when any metadata table was zeroed/demoted due to stride/count
    /// validation failure (e.g. v38+ variable-width index drift).
    pub fn tables_degraded(&self) -> bool {
        self.tables_degraded
    }

    /// D-L2: true when the loaded metadata version has a supported
    /// TypeDefinition table layout (v24/27/29/31-39); false for strings-only
    /// versions (v16-23/25/26/28/30) or when unloaded.
    pub fn type_tables_supported(&self) -> bool {
        self.loaded && self.type_def_sizeof() > 0
    }

    #[cfg(test)]
    pub(super) fn is_mapped(&self) -> bool {
        #[cfg(windows)]
        {
            matches!(self.data, MetadataBacking::Mapped(_))
        }
        #[cfg(not(windows))]
        {
            false
        }
    }

    /// C++ `v24SubVersionLabel`: "v24.0" / "v24.1" / "v24" for v24 metadata,
    /// empty otherwise.
    pub fn v24_sub_version_label(&self) -> &'static [u8] {
        if self.version != 24 {
            return b"";
        }
        match self.v24_variant {
            V24Variant::V24_0 => b"v24.0",
            V24Variant::V24_1 => b"v24.1",
            V24Variant::V24_2Through5 => b"v24",
        }
    }

    // --- strides (C++: *Sizeof) -------------------------------------------

    fn type_def_layout_v24(&self) -> TypeDefLayout {
        match self.v24_variant {
            V24Variant::V24_0 => layout::TYPE_DEFINITION_V24_0,
            V24Variant::V24_1 => layout::TYPE_DEFINITION_V24_1,
            V24Variant::V24_2Through5 => layout::TYPE_DEFINITION_V24,
        }
    }

    fn tis(&self) -> usize {
        self.index_sizes.type_index_size as usize
    }
    fn tdis(&self) -> usize {
        self.index_sizes.type_definition_index_size as usize
    }
    fn gcis(&self) -> usize {
        self.index_sizes.generic_container_index_size as usize
    }
    fn pis(&self) -> usize {
        self.index_sizes.parameter_index_size as usize
    }

    /// C++ `TypeDefSizeof`. 0 for unsupported versions.
    fn type_def_sizeof(&self) -> usize {
        match self.version {
            24 => self.type_def_layout_v24().size,
            27 | 29 | 31 | 32 | 33 | 34 => layout::TYPE_DEFINITION_V29.size,
            35..=37 => layout::TYPE_DEFINITION_V35.size,
            // 3 TypeIndex (byval / declaring / parent) + 1 GenericContainerIndex.
            38 | 39 => type_definition_v38::FIXED_BYTES + 3 * self.tis() + self.gcis(),
            _ => 0,
        }
    }

    /// C++ `FieldDefSizeof`.
    fn field_def_sizeof(&self) -> usize {
        if self.version >= 38 {
            return field_definition_v38::FIXED_BYTES + self.tis();
        }
        if self.version == 24 && self.v24_variant == V24Variant::V24_0 {
            return field_definition_v24_0::SIZE;
        }
        field_definition_v29::SIZE
    }

    /// C++ `FieldDefaultValueSizeof`.
    fn field_default_value_sizeof(&self) -> usize {
        if self.version >= 38 {
            return 8 + self.tis();
        }
        field_default_value_v29::SIZE
    }

    fn method_def_layout_v24(&self) -> MethodDefLayout {
        match self.v24_variant {
            V24Variant::V24_0 => layout::METHOD_DEFINITION_V24_0,
            V24Variant::V24_1 => layout::METHOD_DEFINITION_V24_1,
            V24Variant::V24_2Through5 => layout::METHOD_DEFINITION_V29,
        }
    }

    /// C++ `MethodDefSizeof`.
    fn method_def_sizeof(&self) -> usize {
        match self.version {
            24 => self.method_def_layout_v24().size,
            27 | 29 => layout::METHOD_DEFINITION_V29.size,
            31..=37 => layout::METHOD_DEFINITION_V31.size,
            38 => method_definition_v38::V38_FIXED_BYTES + self.tdis() + self.tis() + self.gcis(),
            39 => {
                method_definition_v38::V39_FIXED_BYTES
                    + self.tdis()
                    + self.tis()
                    + self.pis()
                    + self.gcis()
            }
            _ => 0,
        }
    }

    /// C++ `ParameterDefSizeof`.
    fn parameter_def_sizeof(&self) -> usize {
        if self.version >= 38 {
            return parameter_definition_v38::FIXED_BYTES + self.tis();
        }
        if self.version == 24 && self.v24_variant == V24Variant::V24_0 {
            return parameter_definition_v24_0::SIZE;
        }
        parameter_definition_v29::SIZE
    }

    /// C++ `ImageDefSizeof`. 0 for v24.0 / v24.1 (deferred).
    fn image_def_sizeof(&self) -> usize {
        if self.version == 24 && self.v24_variant != V24Variant::V24_2Through5 {
            return 0;
        }
        if self.version >= 38 {
            return image_definition_v38::FIXED_BYTES + 2 * self.tdis();
        }
        image_definition_v29::SIZE
    }

    // --- load ----------------------------------------------------------------

    fn in_bounds(&self, off: i32, sz: i32) -> bool {
        if off < 0 || sz < 0 {
            return false;
        }
        let end = off as u64 + sz as u64;
        end <= self.data.len() as u64
    }

    /// Reads a v38 section field as the C++ `static_cast<std::int32_t>` of
    /// the uint32 value (modular conversion).
    fn sec_i32(&self, sec: usize, field: usize) -> i32 {
        rd_u32(&self.data, sec + field) as i32
    }

    /// C++ `LoadHeader`.
    fn load_header(&mut self) -> bool {
        if self.data.len() < header_prefix::SIZE {
            log_md("MetadataParser: file too small for header prefix");
            return false;
        }
        let sanity = rd_u32(&self.data, header_prefix::SANITY);
        if sanity != layout::SANITY {
            log_md("MetadataParser: sanity mismatch (encrypted or corrupt)");
            return false;
        }

        self.version = rd_i32(&self.data, header_prefix::VERSION);

        // Defensive range: 16..39 inclusive. Beyond v39 falls through to
        // "unsupported" — better to surface a clear error than emit
        // silently-wrong offsets via a stale layout.
        if self.version < 16 || self.version > 39 {
            let line = format!(
                "MetadataParser: unsupported metadata version {} (supported \
                 range is 16..39; see docs/il2cpp-metadata-crosscheck.md)",
                self.version
            );
            log_md_truncated(256, line.as_bytes());
            return false;
        }

        // v35+ shrinks Il2CppStringLiteral to a single int32 dataIndex.
        self.string_literal_entry_stride = if self.version >= 35 {
            string_literal_v35::SIZE as i32
        } else {
            string_literal::SIZE as i32
        };

        if self.version >= 38 {
            if self.data.len() < header_v38::SIZE {
                log_md("MetadataParser: file too small for v38+ section header");
                return false;
            }
            self.string_literal_offset = self.sec_i32(header_v38::STRING_LITERALS, section::OFFSET);
            self.string_literal_size = self.sec_i32(header_v38::STRING_LITERALS, section::SIZE);
            self.string_literal_data_offset =
                self.sec_i32(header_v38::STRING_LITERAL_DATA, section::OFFSET);
            self.string_literal_data_size =
                self.sec_i32(header_v38::STRING_LITERAL_DATA, section::SIZE);
            self.string_offset = self.sec_i32(header_v38::STRINGS, section::OFFSET);
            self.string_size = self.sec_i32(header_v38::STRINGS, section::SIZE);
        } else {
            let d = &self.data;
            self.string_literal_offset = rd_i32(d, header_prefix::STRING_LITERAL_OFFSET);
            self.string_literal_size = rd_i32(d, header_prefix::STRING_LITERAL_SIZE);
            self.string_literal_data_offset = rd_i32(d, header_prefix::STRING_LITERAL_DATA_OFFSET);
            self.string_literal_data_size = rd_i32(d, header_prefix::STRING_LITERAL_DATA_SIZE);
            self.string_offset = rd_i32(d, header_prefix::STRING_OFFSET);
            self.string_size = rd_i32(d, header_prefix::STRING_SIZE);
        }

        // Verified versions: v24 / v27 / v29 / v31 / v35-v39. Others in range
        // are accepted with a soft warning distinguishing strings-only versions
        // (no TypeDefinition layout) from unverified v32-v34 (parsed with v31).
        let is_verified = matches!(self.version, 24 | 27 | 29 | 31 | 35..=39);
        if !is_verified {
            let line = if self.type_def_sizeof() == 0 {
                format!(
                    "MetadataParser: version {} is strings-only (no TypeDefinition layout \
                     supported) — string-literal queries work, type tables skipped.",
                    self.version
                )
            } else {
                format!(
                    "MetadataParser: version {} is unverified — parsing with v31 layout \
                     (soft warning). Report any decode anomalies for cross-check.",
                    self.version
                )
            };
            log_md_truncated(200, line.as_bytes());
        }

        if !self.in_bounds(self.string_literal_offset, self.string_literal_size)
            || !self.in_bounds(
                self.string_literal_data_offset,
                self.string_literal_data_size,
            )
            || !self.in_bounds(self.string_offset, self.string_size)
        {
            log_md("MetadataParser: prefix tables out of bounds");
            return false;
        }
        true
    }

    /// C++ `LoadTables`.
    fn load_tables(&mut self) -> bool {
        if self.version >= 38 {
            if self.data.len() < header_v38::SIZE {
                log_md("MetadataParser: file too small for v38+ section header");
                return false;
            }
            use header_v38 as h;
            self.type_definitions_offset = self.sec_i32(h::TYPE_DEFINITIONS, section::OFFSET);
            self.type_definitions_size = self.sec_i32(h::TYPE_DEFINITIONS, section::SIZE);
            self.fields_offset = self.sec_i32(h::FIELDS, section::OFFSET);
            self.fields_size = self.sec_i32(h::FIELDS, section::SIZE);
            self.field_defaults_offset = self.sec_i32(h::FIELD_DEFAULT_VALUES, section::OFFSET);
            self.field_defaults_size = self.sec_i32(h::FIELD_DEFAULT_VALUES, section::SIZE);
            self.field_default_data_offset =
                self.sec_i32(h::FIELD_AND_PARAMETER_DEFAULT_VALUE_DATA, section::OFFSET);
            self.field_default_data_size =
                self.sec_i32(h::FIELD_AND_PARAMETER_DEFAULT_VALUE_DATA, section::SIZE);
            self.methods_offset = self.sec_i32(h::METHODS, section::OFFSET);
            self.methods_size = self.sec_i32(h::METHODS, section::SIZE);
            self.parameters_offset = self.sec_i32(h::PARAMETERS, section::OFFSET);
            self.parameters_size = self.sec_i32(h::PARAMETERS, section::SIZE);
            self.images_offset = self.sec_i32(h::IMAGES, section::OFFSET);
            self.images_size = self.sec_i32(h::IMAGES, section::SIZE);
            self.nested_types_offset = self.sec_i32(h::NESTED_TYPES, section::OFFSET);
            self.nested_types_size = self.sec_i32(h::NESTED_TYPES, section::SIZE);

            let params_size = rd_u32(&self.data, h::PARAMETERS + section::SIZE);
            let params_count = rd_u32(&self.data, h::PARAMETERS + section::COUNT);
            let type_defs_count = rd_u32(&self.data, h::TYPE_DEFINITIONS + section::COUNT);
            let generic_containers_count =
                rd_u32(&self.data, h::GENERIC_CONTAINERS + section::COUNT);

            self.parameters_count = i64::from(params_count);

            // typeIndexSize comes from the parameter row stride:
            // Il2CppParameterDefinition = uint nameIndex + uint token +
            // TypeIndex typeIndex, so size = 8 + TIS. The other widths come
            // from the respective table's row count.
            if params_count > 0 {
                let exact_div = params_size % params_count == 0;
                let parm_row = i64::from(params_size) / i64::from(params_count);
                // C++ `static_cast<int>(parmRow - 8)`: modular int64 -> int.
                let inferred = (parm_row - 8) as i32;
                if exact_div && matches!(inferred, 1 | 2 | 4) {
                    self.index_sizes.type_index_size = inferred;
                } else {
                    self.tables_degraded = true;
                    let warn = format!(
                        "MetadataParser: WARN v{} could not infer typeIndexSize from \
                         parameters (size={} count={} row={} rem={}) — defaulting to 4 \
                         and marking tablesDegraded",
                        self.version,
                        params_size,
                        params_count,
                        parm_row,
                        params_size % params_count
                    );
                    log_md_truncated(256, warn.as_bytes());
                }
            }
            self.index_sizes.type_definition_index_size =
                get_index_size(i64::from(type_defs_count));
            self.index_sizes.generic_container_index_size =
                get_index_size(i64::from(generic_containers_count));
            self.index_sizes.parameter_index_size = get_index_size(i64::from(params_count));

            let row = params_size.checked_div(params_count).unwrap_or(0);
            let line = format!(
                "MetadataParser: v{} IndexSizes — typeIndex={} typeDefinitionIndex={} \
                 genericContainerIndex={} parameterIndex={} (params row={}, \
                 typeDefs count={}, genericContainers count={}, params count={})",
                self.version,
                self.index_sizes.type_index_size,
                self.index_sizes.type_definition_index_size,
                self.index_sizes.generic_container_index_size,
                self.index_sizes.parameter_index_size,
                row,
                type_defs_count,
                generic_containers_count,
                params_count
            );
            log_md_truncated(256, line.as_bytes());
        } else {
            // v24-v37 share the flat HeaderV29 prefix for the tables we care
            // about.
            if self.data.len() < header_v29::SIZE {
                log_md("MetadataParser: file too small for flat header");
                return false;
            }
            use header_v29 as h;
            let d = &self.data;
            self.type_definitions_offset = rd_i32(d, h::TYPE_DEFINITIONS_OFFSET);
            self.type_definitions_size = rd_i32(d, h::TYPE_DEFINITIONS_SIZE);
            self.fields_offset = rd_i32(d, h::FIELDS_OFFSET);
            self.fields_size = rd_i32(d, h::FIELDS_SIZE);
            self.field_defaults_offset = rd_i32(d, h::FIELD_DEFAULT_VALUES_OFFSET);
            self.field_defaults_size = rd_i32(d, h::FIELD_DEFAULT_VALUES_SIZE);
            self.field_default_data_offset =
                rd_i32(d, h::FIELD_AND_PARAMETER_DEFAULT_VALUE_DATA_OFFSET);
            self.field_default_data_size =
                rd_i32(d, h::FIELD_AND_PARAMETER_DEFAULT_VALUE_DATA_SIZE);
            self.methods_offset = rd_i32(d, h::METHODS_OFFSET);
            self.methods_size = rd_i32(d, h::METHODS_SIZE);
            self.parameters_offset = rd_i32(d, h::PARAMETERS_OFFSET);
            self.parameters_size = rd_i32(d, h::PARAMETERS_SIZE);
            // images are deferred until after v24 sub-version detection (see
            // below). nestedTypes lives BEFORE the rgctx gap, so it is safe
            // to read regardless of v24 sub-variant.
            self.nested_types_offset = rd_i32(d, h::NESTED_TYPES_OFFSET);
            self.nested_types_size = rd_i32(d, h::NESTED_TYPES_SIZE);
        }

        // images / nestedTypes are only checked when populated: both
        // legitimately stay 0 on the layouts where they are absent.
        if !self.in_bounds(self.type_definitions_offset, self.type_definitions_size)
            || !self.in_bounds(self.fields_offset, self.fields_size)
            || !self.in_bounds(self.field_defaults_offset, self.field_defaults_size)
            || !self.in_bounds(self.field_default_data_offset, self.field_default_data_size)
            || !self.in_bounds(self.methods_offset, self.methods_size)
            || !self.in_bounds(self.parameters_offset, self.parameters_size)
            || (self.images_size > 0 && !self.in_bounds(self.images_offset, self.images_size))
            || (self.nested_types_size > 0
                && !self.in_bounds(self.nested_types_offset, self.nested_types_size))
        {
            log_md("MetadataParser: tables out of bounds");
            return false;
        }
        // D-M3: After bounds checking, validate EVERY v38+ table's byte size
        // against `section.count * stride` (and `size % stride == 0`) so drifted
        // TDIS/TIS/PIS/GCIS never silently misalign method/field/param/image rows.
        if self.version >= 38 {
            use header_v38 as h;
            let type_defs_count = rd_u32(&self.data, h::TYPE_DEFINITIONS + section::COUNT);
            let fields_count = rd_u32(&self.data, h::FIELDS + section::COUNT);
            let field_defaults_count =
                rd_u32(&self.data, h::FIELD_DEFAULT_VALUES + section::COUNT);
            let methods_count = rd_u32(&self.data, h::METHODS + section::COUNT);
            let params_count = rd_u32(&self.data, h::PARAMETERS + section::COUNT);
            let images_count = rd_u32(&self.data, h::IMAGES + section::COUNT);

            let check_v38_table = |name: &str,
                                   size_field: &mut i32,
                                   stride: usize,
                                   header_count: u32,
                                   degraded: &mut bool,
                                   ver: i32| {
                let sz_i32 = *size_field;
                if sz_i32 == 0 && header_count == 0 {
                    return;
                }
                let ok = sz_i32 >= 0
                    && stride > 0
                    && (sz_i32 as u64) % (stride as u64) == 0
                    && u64::from(header_count).checked_mul(stride as u64) == Some(sz_i32 as u64);
                if !ok {
                    *size_field = 0;
                    *degraded = true;
                    let warn = format!(
                        "MetadataParser: WARN v{} table '{}' stride/count mismatch \
                         (size={} headerCount={} stride={}) — zeroing table and \
                         marking tablesDegraded",
                        ver, name, sz_i32, header_count, stride
                    );
                    log_md_truncated(256, warn.as_bytes());
                }
            };
            let ver = self.version;
            let td_stride = self.type_def_sizeof();
            let f_stride = self.field_def_sizeof();
            let fd_stride = self.field_default_value_sizeof();
            let m_stride = self.method_def_sizeof();
            let p_stride = self.parameter_def_sizeof();
            let i_stride = self.image_def_sizeof();
            check_v38_table(
                "typeDefinitions",
                &mut self.type_definitions_size,
                td_stride,
                type_defs_count,
                &mut self.tables_degraded,
                ver,
            );
            check_v38_table(
                "fields",
                &mut self.fields_size,
                f_stride,
                fields_count,
                &mut self.tables_degraded,
                ver,
            );
            check_v38_table(
                "fieldDefaults",
                &mut self.field_defaults_size,
                fd_stride,
                field_defaults_count,
                &mut self.tables_degraded,
                ver,
            );
            check_v38_table(
                "methods",
                &mut self.methods_size,
                m_stride,
                methods_count,
                &mut self.tables_degraded,
                ver,
            );
            check_v38_table(
                "parameters",
                &mut self.parameters_size,
                p_stride,
                params_count,
                &mut self.tables_degraded,
                ver,
            );
            check_v38_table(
                "images",
                &mut self.images_size,
                i_stride,
                images_count,
                &mut self.tables_degraded,
                ver,
            );
        }
        if self.version == 24 {
            self.detect_v24_variant();
        }

        // Phase D-5.1 — images table read, deferred until after v24
        // sub-version detection. v24.0/v24.1 headers have
        // rgctxEntriesOffset/Count between typeDefinitions and images, so
        // images stay 0/0 there. v38+ already populated them above.
        if self.version < 38
            && (self.version >= 27
                || (self.version == 24 && self.v24_variant == V24Variant::V24_2Through5))
            && self.data.len() >= header_v29::SIZE
        {
            let off = rd_i32(&self.data, header_v29::IMAGES_OFFSET);
            let sz = rd_i32(&self.data, header_v29::IMAGES_SIZE);
            if self.in_bounds(off, sz) {
                self.images_offset = off;
                self.images_size = sz;
            }
        }
        true
    }

    /// C++ `DetectV24Variant`: probe candidate strides {92, 100, 104}; the one
    /// that lands TypeDef[0].token (row offset stride - 4) at 0x02000001
    /// wins. If none match, the default (92-byte) stays and
    /// `validate_type_table` demotes to strings-only.
    fn detect_v24_variant(&mut self) {
        let probe_token = |stride: usize| -> u32 {
            let tdo = self.type_definitions_offset;
            if tdo < 0 || tdo as usize + stride > self.data.len() {
                return 0;
            }
            rd_u32(&self.data, tdo as usize + stride - 4)
        };
        // The probes are pure reads, so evaluating all three up front is
        // equivalent to the C++ short-circuit chain.
        let t92 = probe_token(layout::TYPE_DEFINITION_V24.size);
        let t100 = probe_token(layout::TYPE_DEFINITION_V24_1.size);
        let t104 = probe_token(layout::TYPE_DEFINITION_V24_0.size);

        let picked = if t92 == MODULE_TOKEN {
            self.v24_variant = V24Variant::V24_2Through5;
            "v24.2-v24.5 (92-byte stride)"
        } else if t100 == MODULE_TOKEN {
            self.v24_variant = V24Variant::V24_1;
            "v24.1 (100-byte stride, +rgctxStart/+rgctxCount)"
        } else if t104 == MODULE_TOKEN {
            self.v24_variant = V24Variant::V24_0;
            "v24.0 (104-byte stride, +customAttributeIndex+rgctx)"
        } else {
            "v24 (no candidate stride matched — ValidateTypeTable will reject)"
        };

        let line = format!("MetadataParser: DetectV24Variant — picked {picked}");
        log_md_truncated(200, line.as_bytes());
    }

    /// C++ `ValidateTypeTable`: TypeDef[0] must be `<Module>` with token
    /// 0x02000001 — name alone survives stride drift, the tail token does not.
    fn validate_type_table(&self) -> bool {
        if self.type_definition_count() == 0 {
            log_md("MetadataParser: ValidateTypeTable — table is empty, nothing to validate");
            return false;
        }
        let Some(first) = self.get_type_definition(0) else {
            log_md("MetadataParser: ValidateTypeTable — could not read TypeDef[0]");
            return false;
        };
        let name_ok = first.name == b"<Module>";
        let token_ok = first.token == MODULE_TOKEN;
        if name_ok && token_ok {
            return true;
        }

        let mut line = format!(
            "MetadataParser: ValidateTypeTable FAILED for v{} — TypeDef[0] name='",
            self.version
        )
        .into_bytes();
        line.extend_from_slice(if first.name.is_empty() {
            b"(empty)"
        } else {
            first.name
        });
        line.extend_from_slice(
            format!(
                "' token=0x{:08X} (expected '<Module>' / 0x02000001). \
                 Layout dispatcher likely picked a stride that doesn't match this \
                 Unity build's metadata variant; demoting to strings-only.",
                first.token
            )
            .as_bytes(),
        );
        log_md_truncated(320, &line);
        false
    }

    // --- strings ---------------------------------------------------------------

    /// C++ `StringLiteralCount`.
    pub fn string_literal_count(&self) -> u32 {
        if !self.loaded {
            return 0;
        }
        if self.string_literal_entry_stride <= 0 {
            return 0;
        }
        (self.string_literal_size as u32) / (self.string_literal_entry_stride as u32)
    }

    /// C++ `GetStringLiteral`. `Some(b"")` for a negative dataIndex or a zero
    /// length; `None` on an invalid index or a bounds violation.
    pub fn get_string_literal(&self, idx: u32) -> Option<&[u8]> {
        if !self.loaded {
            return None;
        }
        let count = self.string_literal_count();
        if idx >= count {
            return None;
        }

        let table = self.string_literal_offset as usize;
        let data_idx: i32;
        let length: u32;
        if self.string_literal_entry_stride == string_literal_v35::SIZE as i32 {
            // v35+: entry is just `int32 dataIndex`. Length is the delta to
            // the next entry's dataIndex (or stringLiteralDataSize for the
            // last).
            let entry = |i: u32| {
                table + i as usize * string_literal_v35::SIZE + string_literal_v35::DATA_INDEX
            };
            data_idx = rd_i32(&self.data, entry(idx));
            let next_data_idx = if idx + 1 < count {
                rd_i32(&self.data, entry(idx + 1))
            } else {
                self.string_literal_data_size
            };
            if next_data_idx < data_idx {
                return None;
            }
            // C++ `nextDataIdx - dataIdx` is int32 arithmetic and overflows
            // (UB) only when dataIdx < 0 — a case that returns "" below no
            // matter what the length is. wrapping_sub is MSVC's result.
            length = next_data_idx.wrapping_sub(data_idx) as u32;
        } else {
            // v24-v34: entry is `(uint length, int32 dataIndex)` — 8 bytes.
            let row = table + idx as usize * string_literal::SIZE;
            length = rd_u32(&self.data, row + string_literal::LENGTH);
            data_idx = rd_i32(&self.data, row + string_literal::DATA_INDEX);
        }

        if data_idx < 0 || length == 0 {
            return Some(&[]);
        }
        let end = data_idx as u64 + u64::from(length);
        if end > self.string_literal_data_size as u64 {
            return None;
        }
        let start = self.string_literal_data_offset as usize + data_idx as usize;
        Some(&self.data[start..start + length as usize])
    }

    /// C++ `EnumerateStringLiterals`: the first `max_count` literals (0 =
    /// all); a failed literal becomes an empty entry.
    pub fn enumerate_string_literals(&self, max_count: u32) -> Vec<&[u8]> {
        if !self.loaded {
            return Vec::new();
        }
        let total = self.string_literal_count();
        let limit = if max_count == 0 {
            total
        } else {
            max_count.min(total)
        };
        let mut out = Vec::new();
        if out.try_reserve(limit as usize).is_err() {
            return out;
        }
        for i in 0..limit {
            if !try_push(&mut out, self.get_string_literal(i).unwrap_or(&[])) {
                break;
            }
        }
        out
    }

    /// C++ `GetHeapString`: the ASCIIZ entry at `name_index`, bounded by the
    /// heap end. Empty on failure.
    pub fn get_heap_string(&self, name_index: i32) -> &[u8] {
        if !self.loaded {
            return &[];
        }
        if name_index < 0 || name_index >= self.string_size {
            return &[];
        }
        let start = self.string_offset as usize + name_index as usize;
        let heap_end = self.string_offset as usize + self.string_size as usize;
        let s = &self.data[start..heap_end];
        let len = s.iter().position(|&c| c == 0).unwrap_or(s.len());
        &s[..len]
    }

    // --- type definitions -------------------------------------------------------

    /// C++ `TypeDefinitionCount`.
    pub fn type_definition_count(&self) -> u32 {
        if !self.tables_loaded {
            return 0;
        }
        let sz = self.type_def_sizeof();
        if sz == 0 {
            return 0;
        }
        (self.type_definitions_size as u32) / (sz as u32)
    }

    fn type_def_row(&self, idx: usize, sz: usize) -> &[u8] {
        let start = self.type_definitions_offset as usize + idx * sz;
        &self.data[start..start + sz]
    }

    fn project_type_def(&self, idx: i32, row: &[u8], l: TypeDefLayout) -> TypeDefView<'_> {
        TypeDefView {
            index: idx,
            ns: self.get_heap_string(rd_i32(row, l.namespace_index)),
            name: self.get_heap_string(rd_i32(row, l.name_index)),
            declaring_type_index: rd_i32(row, l.declaring_type_index),
            field_start: rd_i32(row, l.field_start),
            field_count: rd_u16(row, l.field_count),
            method_start: rd_i32(row, l.method_start),
            method_count: rd_u16(row, l.method_count),
            flags: rd_u32(row, l.flags),
            bitfield: rd_u32(row, l.bitfield),
            token: rd_u32(row, l.token),
        }
    }

    /// C++ `GetTypeDefinition`.
    pub fn get_type_definition(&self, idx: i32) -> Option<TypeDefView<'_>> {
        if !self.tables_loaded {
            return None;
        }
        let count = self.type_definition_count();
        if idx < 0 || idx as u32 >= count {
            return None;
        }
        let sz = self.type_def_sizeof();
        let row = self.type_def_row(idx as usize, sz);

        if self.version == 24 {
            // Three sub-version strides; detect_v24_variant latched the right
            // one at load time.
            Some(self.project_type_def(idx, row, self.type_def_layout_v24()))
        } else if (35..=37).contains(&self.version) {
            // v35-v37: v29 with elementTypeIndex removed (84-byte stride).
            Some(self.project_type_def(idx, row, layout::TYPE_DEFINITION_V35))
        } else if self.version >= 38 {
            // v38+: hand-walk; see layout::type_definition_v38.
            use type_definition_v38 as t;
            let tis = self.tis();
            let var_block = 3 * tis + self.gcis();
            let base = t::FIXED_PREFIX + var_block;
            let name_index = rd_i32(row, 0);
            let namespace_index = rd_i32(row, 4);
            // declaringTypeIndex is the 2nd TypeIndex in the var block. v38+
            // uses unsigned indices on the wire; the -1 "no declaring type"
            // sentinel is the all-ones value of that width.
            let raw = rd_uint_n(row, t::FIXED_PREFIX + tis, tis);
            let max_by_width = if tis >= 4 {
                u32::MAX
            } else {
                (1u32 << (tis * 8)) - 1
            };
            let declaring_type_index = if raw == max_by_width { -1 } else { raw as i32 };
            Some(TypeDefView {
                index: idx,
                ns: self.get_heap_string(namespace_index),
                name: self.get_heap_string(name_index),
                declaring_type_index,
                field_start: rd_i32(row, base + t::FIELD_START),
                field_count: rd_u16(row, base + t::FIELD_COUNT),
                method_start: rd_i32(row, base + t::METHOD_START),
                method_count: rd_u16(row, base + t::METHOD_COUNT),
                flags: rd_u32(row, base + t::FLAGS),
                bitfield: rd_u32(row, base + t::BITFIELD),
                token: rd_u32(row, base + t::TOKEN),
            })
        } else {
            // v27 / v29 / v31 / v32-v34: same 88-byte layout.
            Some(self.project_type_def(idx, row, layout::TYPE_DEFINITION_V29))
        }
    }

    /// C++ `FindTypeByName`: first type whose name and namespace match.
    /// D-L12: builds a lazy `OnceLock` hash index preserving exact first-match order.
    pub fn find_type_by_name(&self, ns: &[u8], name: &[u8]) -> Option<TypeDefView<'_>> {
        if !self.tables_loaded {
            return None;
        }
        let index = self.type_by_name_map.get_or_init(|| {
            let count = self.type_definition_count();
            let mut map: HashMap<u64, Vec<i32>> = HashMap::new();
            let _ = map.try_reserve(count as usize);
            for i in 0..count {
                if let Some(v) = self.get_type_definition(i as i32) {
                    map.entry(hash_ns_name(v.ns, v.name))
                        .or_default()
                        .push(i as i32);
                }
            }
            map
        });
        let key = hash_ns_name(ns, name);
        let candidates = index.get(&key)?;
        candidates
            .iter()
            .filter_map(|&idx| self.get_type_definition(idx))
            .find(|v| v.name == name && v.ns == ns)
    }

    /// C++ `EnumerateTypeFields`.
    pub fn enumerate_type_fields(&self, ty: &TypeDefView<'_>) -> Vec<FieldDefView<'_>> {
        self.enumerate_fields(ty.field_start, ty.field_count)
    }

    /// `EnumerateTypeFields` over the only two TypeDefView members it reads.
    pub fn enumerate_fields(&self, field_start: i32, field_count: u16) -> Vec<FieldDefView<'_>> {
        let mut out = Vec::new();
        if !self.tables_loaded || field_count == 0 {
            return out;
        }
        let row_size = self.field_def_sizeof();
        if row_size == 0 {
            return out;
        }
        let field_row_count = (self.fields_size as u32) / (row_size as u32);
        let variable_layout = self.version >= 38;

        let _ = out.try_reserve(usize::from(field_count));
        for k in 0..field_count {
            // C++ `type.fieldStart + k` is int32 arithmetic (UB on overflow).
            // Overflow needs fieldStart > INT32_MAX - 65535, which already
            // fails the row-count check at k = 0; wrapping keeps it safe.
            let global_idx = field_start.wrapping_add(i32::from(k));
            if global_idx < 0 || global_idx as u32 >= field_row_count {
                break;
            }
            let start = self.fields_offset as usize + global_idx as usize * row_size;
            let row = &self.data[start..start + row_size];
            let (name_index, type_index, token) = if variable_layout {
                let tis = self.tis();
                (
                    rd_i32(row, field_definition_v38::NAME_INDEX),
                    rd_uint_n(row, field_definition_v38::TYPE_INDEX, tis) as i32,
                    rd_u32(row, field_definition_v38::TYPE_INDEX + tis),
                )
            } else if self.version == 24 && self.v24_variant == V24Variant::V24_0 {
                (
                    rd_i32(row, field_definition_v24_0::NAME_INDEX),
                    rd_i32(row, field_definition_v24_0::TYPE_INDEX),
                    rd_u32(row, field_definition_v24_0::TOKEN),
                )
            } else {
                (
                    rd_i32(row, field_definition_v29::NAME_INDEX),
                    rd_i32(row, field_definition_v29::TYPE_INDEX),
                    rd_u32(row, field_definition_v29::TOKEN),
                )
            };
            if !try_push(
                &mut out,
                FieldDefView {
                    index: global_idx,
                    name: self.get_heap_string(name_index),
                    type_index,
                    token,
                },
            ) {
                break;
            }
        }
        out
    }

    // --- methods / parameters ---------------------------------------------------

    /// C++ `MethodDefinitionCount`.
    pub fn method_definition_count(&self) -> u32 {
        if !self.tables_loaded {
            return 0;
        }
        let sz = self.method_def_sizeof();
        if sz == 0 {
            return 0;
        }
        (self.methods_size as u32) / (sz as u32)
    }

    fn project_method_def(&self, idx: i32, row: &[u8], l: MethodDefLayout) -> MethodDefView<'_> {
        MethodDefView {
            index: idx,
            name: self.get_heap_string(rd_u32(row, l.name_index) as i32),
            declaring_type_index: rd_i32(row, l.declaring_type),
            return_type_index: rd_i32(row, l.return_type),
            parameter_start: rd_i32(row, l.parameter_start),
            parameter_count: rd_u16(row, l.parameter_count),
            flags: u32::from(rd_u16(row, l.flags)),
            token: rd_u32(row, l.token),
        }
    }

    /// C++ `GetMethodDefinition`.
    pub fn get_method_definition(&self, idx: i32) -> Option<MethodDefView<'_>> {
        if !self.tables_loaded {
            return None;
        }
        let count = self.method_definition_count();
        if idx < 0 || idx as u32 >= count {
            return None;
        }
        let sz = self.method_def_sizeof();
        let start = self.methods_offset as usize + idx as usize * sz;
        let row = &self.data[start..start + sz];

        match self.version {
            24 => Some(self.project_method_def(idx, row, self.method_def_layout_v24())),
            27 | 29 => Some(self.project_method_def(idx, row, layout::METHOD_DEFINITION_V29)),
            31..=37 => Some(self.project_method_def(idx, row, layout::METHOD_DEFINITION_V31)),
            v if v >= 38 => {
                // v38+: hand-walk; see layout::method_definition_v38.
                let tdis = self.tdis();
                let tis = self.tis();
                let pis = if v >= 39 {
                    self.pis()
                } else {
                    method_definition_v38::V38_PARAMETER_INDEX_SIZE
                };
                let gcis = self.gcis();
                let mut off = 0usize;
                let name_index = rd_u32(row, off);
                off += 4;
                let declaring_raw = rd_uint_n(row, off, tdis);
                off += tdis;
                let return_raw = rd_uint_n(row, off, tis);
                off += tis;
                off += 4; // returnParameterToken — not needed for the view
                let parm_start_raw = rd_uint_n(row, off, pis);
                off += pis;
                off += gcis; // genericContainerIndex — not needed
                let token = rd_u32(row, off);
                off += 4;
                let flags = rd_u16(row, off);
                // +2 iflags, +4 slot
                let parameter_count = rd_u16(row, off + 6);
                Some(MethodDefView {
                    index: idx,
                    name: self.get_heap_string(name_index as i32),
                    declaring_type_index: declaring_raw as i32,
                    return_type_index: return_raw as i32,
                    parameter_start: parm_start_raw as i32,
                    parameter_count,
                    flags: u32::from(flags),
                    token,
                })
            }
            _ => None, // unsupported version
        }
    }

    /// C++ `EnumerateTypeMethods`.
    pub fn enumerate_type_methods(&self, ty: &TypeDefView<'_>) -> Vec<MethodDefView<'_>> {
        self.enumerate_methods(ty.method_start, ty.method_count)
    }

    /// `EnumerateTypeMethods` over the only two TypeDefView members it reads.
    pub fn enumerate_methods(
        &self,
        method_start: i32,
        method_count: u16,
    ) -> Vec<MethodDefView<'_>> {
        let mut out = Vec::new();
        if !self.tables_loaded || method_count == 0 {
            return out;
        }
        let total = self.method_definition_count();
        let _ = out.try_reserve(usize::from(method_count));
        for k in 0..method_count {
            // C++ int32 `methodStart + k` (UB on overflow): unreachable past
            // the k = 0 bounds check, wrapping keeps it safe regardless.
            let global_idx = method_start.wrapping_add(i32::from(k));
            if global_idx < 0 || global_idx as u32 >= total {
                break;
            }
            if let Some(md) = self.get_method_definition(global_idx)
                && !try_push(&mut out, md)
            {
                break;
            }
        }
        out
    }

    /// C++ `ParameterCount`.
    pub fn parameter_count(&self) -> u32 {
        if !self.tables_loaded {
            return 0;
        }
        let sz = self.parameter_def_sizeof();
        if sz == 0 {
            return 0;
        }
        (self.parameters_size as u32) / (sz as u32)
    }

    /// C++ `GetParameter`.
    pub fn get_parameter(&self, idx: i32) -> Option<ParameterDefView<'_>> {
        if !self.tables_loaded {
            return None;
        }
        let count = self.parameter_count();
        if idx < 0 || idx as u32 >= count {
            return None;
        }
        let sz = self.parameter_def_sizeof();
        let start = self.parameters_offset as usize + idx as usize * sz;
        let row = &self.data[start..start + sz];

        let (name_index, token, type_index) = if self.version >= 38 {
            (
                rd_u32(row, parameter_definition_v38::NAME_INDEX),
                rd_u32(row, parameter_definition_v38::TOKEN),
                rd_uint_n(row, parameter_definition_v38::TYPE_INDEX, self.tis()) as i32,
            )
        } else if self.version == 24 && self.v24_variant == V24Variant::V24_0 {
            (
                rd_u32(row, parameter_definition_v24_0::NAME_INDEX),
                rd_u32(row, parameter_definition_v24_0::TOKEN),
                rd_i32(row, parameter_definition_v24_0::TYPE_INDEX),
            )
        } else {
            (
                rd_u32(row, parameter_definition_v29::NAME_INDEX),
                rd_u32(row, parameter_definition_v29::TOKEN),
                rd_i32(row, parameter_definition_v29::TYPE_INDEX),
            )
        };
        Some(ParameterDefView {
            index: idx,
            name: self.get_heap_string(name_index as i32),
            type_index,
            token,
        })
    }

    /// C++ `EnumerateMethodParameters`.
    pub fn enumerate_method_parameters(&self, m: &MethodDefView<'_>) -> Vec<ParameterDefView<'_>> {
        self.enumerate_parameters(m.parameter_start, m.parameter_count)
    }

    /// `EnumerateMethodParameters` over the only two MethodDefView members it
    /// reads.
    pub fn enumerate_parameters(
        &self,
        parameter_start: i32,
        parameter_count: u16,
    ) -> Vec<ParameterDefView<'_>> {
        let mut out = Vec::new();
        if !self.tables_loaded || parameter_count == 0 {
            return out;
        }
        let total = self.parameter_count();
        let _ = out.try_reserve(usize::from(parameter_count));
        for k in 0..parameter_count {
            // C++ int32 `parameterStart + k` (UB on overflow): unreachable
            // past the k = 0 bounds check, wrapping keeps it safe regardless.
            let global_idx = parameter_start.wrapping_add(i32::from(k));
            if global_idx < 0 || global_idx as u32 >= total {
                break;
            }
            if let Some(p) = self.get_parameter(global_idx)
                && !try_push(&mut out, p)
            {
                break;
            }
        }
        out
    }

    // --- images -------------------------------------------------------------------

    /// C++ `ImageCount`.
    pub fn image_count(&self) -> u32 {
        if !self.tables_loaded {
            return 0;
        }
        let sz = self.image_def_sizeof();
        if sz == 0 || self.images_size <= 0 {
            return 0;
        }
        (self.images_size as u32) / (sz as u32)
    }

    fn image_row_fields(&self, idx: i32) -> (u32, i32, u32, u32) {
        let sz = self.image_def_sizeof();
        let start = self.images_offset as usize + idx as usize * sz;
        let row = &self.data[start..start + sz];

        if self.version >= 38 {
            // uint nameIndex + int assemblyIndex + TDIS typeStart + uint
            // typeCount + TDIS exportedTypeStart + uint exportedTypeCount +
            // int entryPointIndex + uint token + int caStart + uint caCount.
            let tdis = self.tdis();
            let ts = image_definition_v38::TYPE_START;
            let token_off = ts + tdis + 4 + tdis + 4 + 4;
            (
                rd_u32(row, image_definition_v38::NAME_INDEX),
                rd_uint_n(row, ts, tdis) as i32,
                rd_u32(row, ts + tdis),
                rd_u32(row, token_off),
            )
        } else {
            (
                rd_u32(row, image_definition_v29::NAME_INDEX),
                rd_i32(row, image_definition_v29::TYPE_START),
                rd_u32(row, image_definition_v29::TYPE_COUNT),
                rd_u32(row, image_definition_v29::TOKEN),
            )
        }
    }

    fn compute_image_method_range(&self, type_start: i32, type_count: u32) -> (i32, i32) {
        // methodStart / methodEnd = min(type.methodStart) /
        // max(type.methodStart + methodCount) over the image's types.
        if type_count == 0 {
            return (0, 0);
        }
        let mut lo = i32::MAX;
        let mut hi = 0i32;
        let total = self.type_definition_count();
        // C++ `std::min<int32>(typeStart + (int32)typeCount, (int32)total)`:
        // the uint32 -> int32 cast is modular and the addition can
        // overflow (UB). wrapping_add is MSVC's two's-complement result;
        // the loop only calls the bounds-checked get_type_definition, so
        // any value is memory-safe.
        let end = type_start.wrapping_add(type_count as i32).min(total as i32);
        // Indices below 0 never resolve (get_type_definition rejects them),
        // so starting at max(typeStart, 0) skips only no-op iterations.
        for i in type_start.max(0)..end {
            if let Some(t) = self.get_type_definition(i) {
                if t.method_count == 0 {
                    continue;
                }
                let ms = t.method_start;
                // C++ int32 `ms + methodCount` (UB on overflow); the value
                // is only compared, wrapping is MSVC's result.
                let me = ms.wrapping_add(i32::from(t.method_count));
                if ms < lo {
                    lo = ms;
                }
                if me > hi {
                    hi = me;
                }
            }
        }
        if lo != i32::MAX && hi > lo {
            (lo, hi)
        } else {
            (0, 0)
        }
    }

    fn cached_image_method_ranges(&self) -> &[(i32, i32)] {
        self.image_method_ranges.get_or_init(|| {
            let count = self.image_count();
            let mut ranges = Vec::new();
            let _ = ranges.try_reserve(count as usize);
            for i in 0..count {
                let (_, type_start, type_count, _) = self.image_row_fields(i as i32);
                if !try_push(&mut ranges, self.compute_image_method_range(type_start, type_count)) {
                    break;
                }
            }
            ranges
        })
    }

    /// C++ `GetImage`.
    pub fn get_image(&self, idx: i32) -> Option<ImageDefView<'_>> {
        if !self.tables_loaded {
            return None;
        }
        let count = self.image_count();
        if idx < 0 || idx as u32 >= count {
            return None;
        }
        let (name_index, type_start, type_count, token) = self.image_row_fields(idx);
        let (method_start, method_end) = self
            .cached_image_method_ranges()
            .get(idx as usize)
            .copied()
            .unwrap_or((0, 0));
        Some(ImageDefView {
            index: idx,
            name: self.get_heap_string(name_index as i32),
            type_start,
            type_count,
            token,
            method_start,
            method_end,
        })
    }

    /// C++ `EnumerateImages`.
    pub fn enumerate_images(&self) -> Vec<ImageDefView<'_>> {
        let count = self.image_count();
        let mut out = Vec::new();
        let _ = out.try_reserve(count as usize);
        for i in 0..count {
            if let Some(img) = self.get_image(i as i32)
                && !try_push(&mut out, img)
            {
                break;
            }
        }
        out
    }

    /// C++ `FindImageContainingMethod`: first image whose
    /// [methodStart, methodEnd) covers `global_method_idx`.
    /// D-L12: scans the lazy cached per-image ranges instead of re-walking
    /// every type in the file on every call.
    pub fn find_image_containing_method(&self, global_method_idx: i32) -> Option<ImageDefView<'_>> {
        if !self.tables_loaded || self.image_count() == 0 {
            return None;
        }
        let idx = self
            .cached_image_method_ranges()
            .iter()
            .position(|&(ms, me)| me > ms && global_method_idx >= ms && global_method_idx < me)?;
        self.get_image(idx as i32)
    }

    // --- nested types -------------------------------------------------------------

    /// C++ `GetDeclaringTypeIndex`: parent TypeDefinition of a nested type,
    /// from the nestedTypeIndices[] table. -1 for top-level types, an
    /// out-of-range index, or when the table is unavailable.
    pub fn get_declaring_type_index(&self, type_def_idx: i32) -> i32 {
        if !self.tables_loaded {
            return -1;
        }
        let total = self.type_definition_count();
        if type_def_idx < 0 || type_def_idx as u32 >= total {
            return -1;
        }
        let map = self
            .declaring_type_map
            .get_or_init(|| self.build_declaring_type_map(total));
        map.get(type_def_idx as usize).copied().unwrap_or(-1)
    }

    fn build_declaring_type_map(&self, total: u32) -> Vec<i32> {
        let mut map = Vec::new();
        if map.try_reserve_exact(total as usize).is_err() {
            return map;
        }
        map.resize(total as usize, -1i32);
        let nto = self.nested_types_offset;
        let nts = self.nested_types_size;
        if !(nto > 0 && nts > 0 && nto as u64 + nts as u64 <= self.data.len() as u64) {
            return map;
        }
        let nested_count = (nts as u32) / 4;
        let nested_base = nto as usize;
        for pi in 0..total {
            // The C++ loop first calls GetTypeDefinition(pi) and skips on
            // nullopt; that cannot fail for pi < TypeDefinitionCount() with
            // the tables loaded, so the (side-effect-free) call is omitted.
            let sz = self.type_def_sizeof();
            if sz == 0 {
                continue;
            }
            let row = self.type_def_row(pi as usize, sz);
            // Defensive bounds (rowEnd < 56).
            if sz < type_definition_tail::TAIL_BYTES {
                continue;
            }
            let nested_start = rd_i32(row, sz - type_definition_tail::NESTED_TYPES_START_FROM_END);
            let nested_ct = rd_u16(row, sz - type_definition_tail::NESTED_TYPE_COUNT_FROM_END);
            if nested_ct == 0 || nested_start < 0 {
                continue;
            }
            // nested_start >= 0 so this uint32 sum cannot wrap.
            let end = nested_start as u32 + u32::from(nested_ct);
            if end > nested_count {
                continue;
            }
            for k in nested_start as u32..end {
                let child = rd_i32(&self.data, nested_base + k as usize * 4);
                if child >= 0 && (child as u32) < total {
                    map[child as usize] = pi as i32;
                }
            }
        }
        map
    }

    // --- field defaults -----------------------------------------------------------

    /// C++ `TryDecodeStringDefault`: decodes the FieldDefault blob at
    /// `data_index` as an ECMA-335 compressed-length string, sanitised to
    /// valid UTF-8 the same way (malformed sequences become U+FFFD).
    /// `Cow::Borrowed` points into the file buffer whenever sanitising left
    /// the bytes unchanged; `Cow::Owned` only when a replacement happened.
    pub fn try_decode_string_default(&self, data_index: i32) -> Option<Cow<'_, [u8]>> {
        if !self.tables_loaded {
            return None;
        }
        if data_index < 0 || data_index >= self.field_default_data_size {
            return None;
        }
        let base_off = self.field_default_data_offset as usize + data_index as usize;
        let avail = self.field_default_data_size - data_index;
        if avail < 1 {
            return None;
        }
        let base = &self.data[base_off..base_off + avail as usize];

        // ECMA-335 §II.23.2 compressed *signed* integer — the unsigned bits
        // give 2*length (low bit is the sign), so right-shift by 1.
        let b0 = base[0];
        if b0 == 0xFF {
            return Some(Cow::Borrowed(&[])); // null-string sentinel
        }
        let (encoded, header_len): (u32, i32) = if (b0 & 0x80) == 0 {
            (u32::from(b0), 1)
        } else if (b0 & 0xC0) == 0x80 {
            if avail < 2 {
                return None;
            }
            ((u32::from(b0 & 0x3F) << 8) | u32::from(base[1]), 2)
        } else if (b0 & 0xE0) == 0xC0 {
            if avail < 4 {
                return None;
            }
            (
                (u32::from(b0 & 0x1F) << 24)
                    | (u32::from(base[1]) << 16)
                    | (u32::from(base[2]) << 8)
                    | u32::from(base[3]),
                4,
            )
        } else {
            return None;
        };
        // bit 0 == 1 means negative — invalid for a string length.
        if (encoded & 1) != 0 {
            return None;
        }
        let length = encoded >> 1;
        if length == 0 {
            return Some(Cow::Borrowed(&[]));
        }
        if length > 1024 * 1024 {
            return None;
        }
        if i64::from(header_len) + i64::from(length) > i64::from(avail) {
            return None;
        }
        let p = &base[header_len as usize..header_len as usize + length as usize];

        // Heuristic on the first 32 bytes only.
        let sample = length.min(32);
        let mut printable = 0u32;
        let mut control_bad = 0u32;
        for &c in &p[..sample as usize] {
            if c == 0 || (c < 0x20 && c != b'\t' && c != b'\n' && c != b'\r') {
                control_bad += 1;
            } else {
                printable += 1;
            }
        }
        if sample > 0 {
            if control_bad * 4 > sample {
                return None; // > 25% control chars
            }
            if printable * 10 < sample * 7 {
                return None; // < 70% printable
            }
        }

        let out = sanitize_utf8(p);
        if out == p {
            Some(Cow::Borrowed(p))
        } else {
            Some(Cow::Owned(out))
        }
    }

    fn read_field_default_row(&self, row_idx: u32, row_size: usize) -> (i32, i32, i32) {
        let start = self.field_defaults_offset as usize + row_idx as usize * row_size;
        let row = &self.data[start..start + row_size];
        if self.version >= 38 {
            let tis = self.tis();
            (
                rd_i32(row, 0),
                rd_uint_n(row, 4, tis) as i32,
                rd_i32(row, 4 + tis),
            )
        } else {
            (
                rd_i32(row, field_default_value_v29::FIELD_INDEX),
                rd_i32(row, field_default_value_v29::TYPE_INDEX),
                rd_i32(row, field_default_value_v29::DATA_INDEX),
            )
        }
    }

    /// C++ `FindFieldDefault`: the FieldDefault row for a global field index
    /// (first match). D-L12: builds a lazy `OnceLock` index for O(1) lookup.
    pub fn find_field_default(&self, global_field_index: i32) -> Option<FieldDefaultBlob<'_>> {
        if !self.tables_loaded {
            return None;
        }
        let row_size = self.field_default_value_sizeof();
        if row_size == 0 {
            return None;
        }
        let row_count = (self.field_defaults_size as u32) / (row_size as u32);
        if row_count == 0 {
            return None;
        }

        let index = self.field_default_map.get_or_init(|| {
            let mut map = HashMap::new();
            let _ = map.try_reserve(row_count as usize);
            for i in 0..row_count {
                let (field_index, _, _) = self.read_field_default_row(i, row_size);
                // Preserve exact linear-scan first-match semantics on duplicates.
                map.entry(field_index).or_insert(i);
            }
            map
        });

        let &row_idx = index.get(&global_field_index)?;
        let (field_index, type_index, data_index) =
            self.read_field_default_row(row_idx, row_size);
        let mut data: &[u8] = &[];
        if data_index >= 0 && data_index < self.field_default_data_size {
            // Up to the blob end or 256 bytes, whichever is smaller.
            let max_avail = self.field_default_data_size - data_index;
            let take = max_avail.min(256) as usize;
            let p = self.field_default_data_offset as usize + data_index as usize;
            data = &self.data[p..p + take];
        }
        Some(FieldDefaultBlob {
            field_index,
            type_index,
            data_index,
            data,
        })
    }

    /// v38 parameters.count (C++ `parametersCount_`); 0 before v38.
    pub fn parameters_section_count(&self) -> i64 {
        self.parameters_count
    }

    /// The variable-width index sizes in effect (all 4 before v38).
    pub fn index_sizes(&self) -> IndexSizes {
        self.index_sizes
    }

    /// The latched v24 sub-variant (meaningful only when `version() == 24`).
    pub fn v24_variant(&self) -> V24Variant {
        self.v24_variant
    }
}

/// The UTF-8 sanitiser from C++ `TryDecodeStringDefault`, byte for byte:
/// ASCII passes, a lead byte with the right number of continuation bytes
/// passes (overlong 3/4-byte forms and surrogates included, exactly as the
/// C++ accepts them), anything else becomes U+FFFD and advances one byte.
fn sanitize_utf8(p: &[u8]) -> Vec<u8> {
    const REPLACEMENT: [u8; 3] = [0xEF, 0xBF, 0xBD];
    let length = p.len();
    let mut out = Vec::with_capacity(length);
    let mut i = 0usize;
    while i < length {
        let c = p[i];
        if c < 0x80 {
            out.push(c);
            i += 1;
            continue;
        }
        let need = if (c & 0xE0) == 0xC0 && c >= 0xC2 {
            1
        } else if (c & 0xF0) == 0xE0 {
            2
        } else if (c & 0xF8) == 0xF0 && c <= 0xF4 {
            3
        } else {
            out.extend_from_slice(&REPLACEMENT);
            i += 1;
            continue;
        };
        if i + need >= length {
            out.extend_from_slice(&REPLACEMENT);
            i += 1;
            continue;
        }
        if !p[i + 1..=i + need].iter().all(|&b| (b & 0xC0) == 0x80) {
            out.extend_from_slice(&REPLACEMENT);
            i += 1;
            continue;
        }
        out.extend_from_slice(&p[i..=i + need]);
        i += need + 1;
    }
    out
}
