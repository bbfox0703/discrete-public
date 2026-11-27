//! Tests for the metadata parser over small synthetic `global-metadata.dat`
//! images, plus an optional pass over real files listed in the
//! `DISCRETE_METADATA_FIXTURES` environment variable (semicolon-separated).
//!
//! Expected values are what the C++ `MetadataParser.cpp` returns for the same
//! bytes; the comments name the C++ rule each assertion exercises.

use std::borrow::Cow;

use super::ffi::*;
use super::layout::{
    self, TypeDefLayout, field_definition_v24_0, header_v29, header_v38, method_definition_v38,
    section,
};
use super::parser::{Parser, V24Variant, alloc_zeroed, read_entire_file};

// ---------------------------------------------------------------------------
// Builders
// ---------------------------------------------------------------------------

/// Name heap: index 0 is the empty string.
struct Heap(Vec<u8>);
impl Heap {
    fn new() -> Self {
        Heap(vec![0])
    }
    fn add(&mut self, s: &[u8]) -> i32 {
        let off = self.0.len() as i32;
        self.0.extend_from_slice(s);
        self.0.push(0);
        off
    }
}

fn put_u16(b: &mut [u8], off: usize, v: u16) {
    b[off..off + 2].copy_from_slice(&v.to_le_bytes());
}
fn put_u32(b: &mut [u8], off: usize, v: u32) {
    b[off..off + 4].copy_from_slice(&v.to_le_bytes());
}
fn put_i32(b: &mut [u8], off: usize, v: i32) {
    b[off..off + 4].copy_from_slice(&v.to_le_bytes());
}
fn put_uint_n(b: &mut [u8], off: usize, width: usize, v: u32) {
    b[off..off + width].copy_from_slice(&v.to_le_bytes()[..width]);
}

/// A flat-header (v16-v37) file: header, then each table appended in order;
/// `(offset_field, bytes)` sets `offset_field` / `offset_field + 4` in the
/// header to the table's offset / size.
fn flat_file(version: i32, tables: &[(usize, Vec<u8>)]) -> Vec<u8> {
    let mut f = vec![0u8; header_v29::SIZE];
    put_u32(&mut f, header_v29::SANITY, layout::SANITY);
    put_i32(&mut f, header_v29::VERSION, version);
    for (field, bytes) in tables {
        let off = f.len();
        f.extend_from_slice(bytes);
        put_i32(&mut f, *field, off as i32);
        put_i32(&mut f, *field + 4, bytes.len() as i32);
    }
    f
}

/// A v38+ section-header file: `(section, bytes, count)`.
fn section_file(version: i32, tables: &[(usize, Vec<u8>, u32)]) -> Vec<u8> {
    let mut f = vec![0u8; header_v38::SIZE];
    put_u32(&mut f, header_v38::SANITY, layout::SANITY);
    put_i32(&mut f, header_v38::VERSION, version);
    for (sec, bytes, count) in tables {
        let off = f.len();
        f.extend_from_slice(bytes);
        put_u32(&mut f, sec + section::OFFSET, off as u32);
        put_u32(&mut f, sec + section::SIZE, bytes.len() as u32);
        put_u32(&mut f, sec + section::COUNT, *count);
    }
    f
}

#[derive(Default, Clone, Copy)]
struct Td {
    name: i32,
    ns: i32,
    declaring: i32,
    flags: u32,
    field_start: i32,
    field_count: u16,
    method_start: i32,
    method_count: u16,
    nested_start: i32,
    nested_count: u16,
    bitfield: u32,
    token: u32,
}

fn typedef_row(l: &TypeDefLayout, t: &Td) -> Vec<u8> {
    let mut r = vec![0u8; l.size];
    put_i32(&mut r, l.name_index, t.name);
    put_i32(&mut r, l.namespace_index, t.ns);
    put_i32(&mut r, l.declaring_type_index, t.declaring);
    put_u32(&mut r, l.flags, t.flags);
    put_i32(&mut r, l.field_start, t.field_start);
    put_u16(&mut r, l.field_count, t.field_count);
    put_i32(&mut r, l.method_start, t.method_start);
    put_u16(&mut r, l.method_count, t.method_count);
    put_i32(&mut r, l.size - 40, t.nested_start);
    put_u16(&mut r, l.size - 16, t.nested_count);
    put_u32(&mut r, l.bitfield, t.bitfield);
    put_u32(&mut r, l.token, t.token);
    r
}

/// v38+ TypeDefinition row (hand layout, independent of the parser's table).
fn typedef_row_v38(tis: usize, gcis: usize, t: &Td, declaring_raw: u32) -> Vec<u8> {
    let stride = 68 + 3 * tis + gcis;
    let mut r = vec![0u8; stride];
    put_i32(&mut r, 0, t.name);
    put_i32(&mut r, 4, t.ns);
    put_uint_n(&mut r, 8 + tis, tis, declaring_raw);
    let b = 8 + 3 * tis + gcis;
    put_u32(&mut r, b, t.flags);
    put_i32(&mut r, b + 4, t.field_start);
    put_i32(&mut r, b + 8, t.method_start);
    put_i32(&mut r, b + 20, t.nested_start); // 5th *Start
    put_u16(&mut r, b + 36, t.method_count);
    put_u16(&mut r, b + 40, t.field_count);
    put_u16(&mut r, b + 44, t.nested_count); // 5th *_count
    put_u32(&mut r, b + 52, t.bitfield);
    put_u32(&mut r, b + 56, t.token);
    assert_eq!(b + 60, stride);
    r
}

fn cat(rows: &[Vec<u8>]) -> Vec<u8> {
    rows.concat()
}

fn i32s(v: &[i32]) -> Vec<u8> {
    v.iter().flat_map(|x| x.to_le_bytes()).collect()
}

/// v29-style 12-byte field row: nameIndex, typeIndex, token.
fn field_row(name: i32, ty: i32, token: u32) -> Vec<u8> {
    let mut r = i32s(&[name, ty]);
    r.extend_from_slice(&token.to_le_bytes());
    r
}

/// v29 32-byte method row.
fn method_row_v29(
    name: u32,
    decl: i32,
    ret: i32,
    pstart: i32,
    token: u32,
    flags: u16,
    pcount: u16,
) -> Vec<u8> {
    let mut r = vec![0u8; 32];
    put_u32(&mut r, 0, name);
    put_i32(&mut r, 4, decl);
    put_i32(&mut r, 8, ret);
    put_i32(&mut r, 12, pstart);
    put_i32(&mut r, 16, -1);
    put_u32(&mut r, 20, token);
    put_u16(&mut r, 24, flags);
    put_u16(&mut r, 30, pcount);
    r
}

/// v31-v37 36-byte method row.
fn method_row_v31(
    name: u32,
    decl: i32,
    ret: i32,
    pstart: i32,
    token: u32,
    flags: u16,
    pcount: u16,
) -> Vec<u8> {
    let mut r = vec![0u8; 36];
    put_u32(&mut r, 0, name);
    put_i32(&mut r, 4, decl);
    put_i32(&mut r, 8, ret);
    put_i32(&mut r, 12, 0x0800_0000); // returnParameterToken
    put_i32(&mut r, 16, pstart);
    put_i32(&mut r, 20, -1);
    put_u32(&mut r, 24, token);
    put_u16(&mut r, 28, flags);
    put_u16(&mut r, 34, pcount);
    r
}

/// v29 12-byte parameter row: nameIndex, token, typeIndex.
fn param_row(name: u32, token: u32, ty: i32) -> Vec<u8> {
    let mut r = Vec::new();
    r.extend_from_slice(&name.to_le_bytes());
    r.extend_from_slice(&token.to_le_bytes());
    r.extend_from_slice(&ty.to_le_bytes());
    r
}

/// v29 40-byte image row.
fn image_row_v29(name: u32, type_start: i32, type_count: u32, token: u32) -> Vec<u8> {
    let mut r = vec![0u8; 40];
    put_u32(&mut r, 0, name);
    put_i32(&mut r, 8, type_start);
    put_u32(&mut r, 12, type_count);
    put_u32(&mut r, 28, token);
    r
}

const MODULE: u32 = 0x0200_0001;

/// A complete little v29 file exercising every table.
fn v29_file() -> Vec<u8> {
    let mut heap = Heap::new();
    let module = heap.add(b"<Module>");
    let foo = heap.add(b"Foo");
    let bar = heap.add(b"Bar");
    let f0 = heap.add(b"field0");
    let f1 = heap.add(b"field1");
    let main = heap.add(b"Main");
    let arg = heap.add(b"arg");
    let asm = heap.add(b"Asm");
    // Unterminated trailing string: bounded by the heap end.
    heap.0.extend_from_slice(b"tail");

    let l = layout::TYPE_DEFINITION_V29;
    let types = cat(&[
        typedef_row(
            &l,
            &Td {
                name: module,
                declaring: -1,
                nested_start: 0,
                nested_count: 1,
                token: MODULE,
                ..Td::default()
            },
        ),
        typedef_row(
            &l,
            &Td {
                name: bar,
                ns: foo,
                declaring: 5,
                flags: 0x0010_0001,
                field_start: 0,
                field_count: 2,
                method_start: 0,
                method_count: 1,
                bitfield: 1,
                token: 0x0200_0002,
                ..Td::default()
            },
        ),
    ]);
    let fields = cat(&[
        field_row(f0, 10, 0x0400_0001),
        field_row(f1, 11, 0x0400_0002),
    ]);
    // Default blobs: "hello" (compressed length 5 -> 10), then "ab\xFFcd".
    let mut defaults_data = vec![0x0A];
    defaults_data.extend_from_slice(b"hello");
    defaults_data.push(0x0A);
    defaults_data.extend_from_slice(b"ab\xFFcd");
    let field_defaults = i32s(&[1, 7, 0]);
    let methods = method_row_v29(main as u32, 1, 7, 0, 0x0600_0001, 0x96, 1);
    let params = param_row(arg as u32, 0x0800_0001, 3);
    let images = image_row_v29(asm as u32, 0, 2, 1);
    let nested = i32s(&[1]);
    // String literals: ("abc"), (len 0 -> ""), (len 100 -> out of range).
    let lit_table = cat(&[i32s(&[3, 0]), i32s(&[0, 3]), i32s(&[100, 0])]);

    flat_file(
        29,
        &[
            (header_v29::STRING_OFFSET, heap.0),
            (header_v29::STRING_LITERAL_OFFSET, lit_table),
            (header_v29::STRING_LITERAL_DATA_OFFSET, b"abc".to_vec()),
            (header_v29::TYPE_DEFINITIONS_OFFSET, types),
            (header_v29::FIELDS_OFFSET, fields),
            (header_v29::FIELD_DEFAULT_VALUES_OFFSET, field_defaults),
            (
                header_v29::FIELD_AND_PARAMETER_DEFAULT_VALUE_DATA_OFFSET,
                defaults_data,
            ),
            (header_v29::METHODS_OFFSET, methods),
            (header_v29::PARAMETERS_OFFSET, params),
            (header_v29::IMAGES_OFFSET, images),
            (header_v29::NESTED_TYPES_OFFSET, nested),
        ],
    )
}

// ---------------------------------------------------------------------------
// Flat header: v29
// ---------------------------------------------------------------------------

#[test]
fn v29_flat_header_full() {
    let p = Parser::load_from_bytes(v29_file());
    assert!(p.loaded());
    assert_eq!(p.version(), 29);
    assert_eq!(p.v24_sub_version_label(), b"");

    // String literals (8-byte entries).
    assert_eq!(p.string_literal_count(), 3);
    assert_eq!(p.get_string_literal(0), Some(&b"abc"[..]));
    assert_eq!(p.get_string_literal(1), Some(&b""[..]));
    assert_eq!(p.get_string_literal(2), None);
    assert_eq!(p.get_string_literal(3), None);
    assert_eq!(p.enumerate_string_literals(0), vec![&b"abc"[..], b"", b""]);
    assert_eq!(p.enumerate_string_literals(1), vec![&b"abc"[..]]);

    // Heap: ASCIIZ, bounded by the heap end.
    assert_eq!(p.get_heap_string(1), b"<Module>");
    assert_eq!(p.get_heap_string(2), b"Module>");
    assert_eq!(p.get_heap_string(0), b"");
    assert_eq!(p.get_heap_string(-1), b"");
    let heap_tail = p.get_heap_string(45);
    assert_eq!(heap_tail, b"tail");
    assert_eq!(p.get_heap_string(49), b"");

    // Types.
    assert_eq!(p.type_definition_count(), 2);
    let t0 = p.get_type_definition(0).unwrap();
    assert_eq!(
        (t0.name, t0.ns, t0.token),
        (&b"<Module>"[..], &b""[..], MODULE)
    );
    let t1 = p.get_type_definition(1).unwrap();
    assert_eq!(t1.name, b"Bar");
    assert_eq!(t1.ns, b"Foo");
    assert_eq!(t1.declaring_type_index, 5);
    assert_eq!(t1.flags, 0x0010_0001);
    assert_eq!((t1.field_start, t1.field_count), (0, 2));
    assert_eq!((t1.method_start, t1.method_count), (0, 1));
    assert!(t1.is_value_type());
    assert!(!t1.is_enum());
    assert_eq!(t1.token, 0x0200_0002);
    assert!(p.get_type_definition(2).is_none());
    assert!(p.get_type_definition(-1).is_none());
    assert_eq!(p.find_type_by_name(b"Foo", b"Bar").unwrap().index, 1);
    assert!(p.find_type_by_name(b"", b"Bar").is_none());

    // Fields.
    let fields = p.enumerate_type_fields(&t1);
    assert_eq!(fields.len(), 2);
    assert_eq!(
        (
            fields[0].index,
            fields[0].name,
            fields[0].type_index,
            fields[0].token
        ),
        (0, &b"field0"[..], 10, 0x0400_0001)
    );
    assert_eq!(fields[1].name, b"field1");
    // Range that runs off the end stops at the row count.
    assert_eq!(p.enumerate_fields(1, 5).len(), 1);
    assert_eq!(p.enumerate_fields(-1, 5).len(), 0);
    assert_eq!(p.enumerate_fields(i32::MAX, u16::MAX).len(), 0);

    // Field defaults.
    let d = p.find_field_default(1).unwrap();
    assert_eq!((d.field_index, d.type_index, d.data_index), (1, 7, 0));
    assert_eq!(d.data.len(), 12); // min(blob remaining, 256)
    assert!(p.find_field_default(0).is_none());
    assert_eq!(
        p.try_decode_string_default(0),
        Some(Cow::Borrowed(&b"hello"[..]))
    );
    // Invalid UTF-8 is sanitised to U+FFFD -> owned copy.
    let s = p.try_decode_string_default(6).unwrap();
    assert!(matches!(s, Cow::Owned(_)));
    assert_eq!(&*s, b"ab\xEF\xBF\xBDcd");
    assert_eq!(p.try_decode_string_default(12), None);
    assert_eq!(p.try_decode_string_default(-1), None);

    // Methods / parameters.
    assert_eq!(p.method_definition_count(), 1);
    let m = p.get_method_definition(0).unwrap();
    assert_eq!(m.name, b"Main");
    assert_eq!((m.declaring_type_index, m.return_type_index), (1, 7));
    assert_eq!((m.parameter_start, m.parameter_count), (0, 1));
    assert_eq!((m.flags, m.token), (0x96, 0x0600_0001));
    assert_eq!(p.enumerate_type_methods(&t1).len(), 1);
    assert_eq!(p.parameter_count(), 1);
    let params = p.enumerate_method_parameters(&m);
    assert_eq!(params.len(), 1);
    assert_eq!(
        (params[0].name, params[0].token, params[0].type_index),
        (&b"arg"[..], 0x0800_0001, 3)
    );

    // Images: method range from walking the image's types.
    assert_eq!(p.image_count(), 1);
    let img = p.get_image(0).unwrap();
    assert_eq!(img.name, b"Asm");
    assert_eq!((img.type_start, img.type_count, img.token), (0, 2, 1));
    assert_eq!((img.method_start, img.method_end), (0, 1));
    assert_eq!(p.enumerate_images().len(), 1);
    assert_eq!(p.find_image_containing_method(0).unwrap().index, 0);
    assert!(p.find_image_containing_method(1).is_none());

    // Nested types.
    assert_eq!(p.get_declaring_type_index(0), -1);
    assert_eq!(p.get_declaring_type_index(1), 0);
    assert_eq!(p.get_declaring_type_index(2), -1);
}

// ---------------------------------------------------------------------------
// v24 sub-variants
// ---------------------------------------------------------------------------

/// A v24 file whose TypeDefinition rows use layout `l`; methods/fields/params
/// rows use the sizes the matching variant expects.
fn v24_file(
    l: &TypeDefLayout,
    method_stride: usize,
    field_stride: usize,
    param_stride: usize,
) -> Vec<u8> {
    let mut heap = Heap::new();
    let module = heap.add(b"<Module>");
    let fld = heap.add(b"fld");
    let mth = heap.add(b"mth");
    let prm = heap.add(b"prm");
    let asm = heap.add(b"Asm");
    let types = cat(&[
        typedef_row(
            l,
            &Td {
                name: module,
                token: MODULE,
                ..Td::default()
            },
        ),
        typedef_row(
            l,
            &Td {
                name: module,
                field_count: 1,
                method_count: 1,
                token: 0x0200_0002,
                ..Td::default()
            },
        ),
    ]);
    // Fields: token is the last uint32 of the row in both 12- and 16-byte forms.
    let mut field = vec![0u8; field_stride];
    put_i32(&mut field, 0, fld);
    put_i32(&mut field, 4, 9);
    put_u32(&mut field, field_stride - 4, 0x0400_0001);
    // Methods: token sits 12 bytes from the end, parameterCount 2 from the end.
    let mut method = vec![0u8; method_stride];
    put_u32(&mut method, 0, mth as u32);
    put_i32(&mut method, 4, 1);
    put_u32(&mut method, method_stride - 12, 0x0600_0001);
    put_u16(&mut method, method_stride - 2, 1);
    // Params: typeIndex is the last int32.
    let mut param = vec![0u8; param_stride];
    put_u32(&mut param, 0, prm as u32);
    put_u32(&mut param, 4, 0x0800_0001);
    put_i32(&mut param, param_stride - 4, 42);
    let images = image_row_v29(asm as u32, 0, 2, 1);
    flat_file(
        24,
        &[
            (header_v29::STRING_OFFSET, heap.0),
            (header_v29::TYPE_DEFINITIONS_OFFSET, types),
            (header_v29::FIELDS_OFFSET, field),
            (header_v29::METHODS_OFFSET, method),
            (header_v29::PARAMETERS_OFFSET, param),
            (header_v29::IMAGES_OFFSET, images),
        ],
    )
}

#[test]
fn v24_0_vs_v24_2_strides() {
    // v24.0: 104-byte TypeDefinition, 16-byte field / param, 56-byte method.
    let p = Parser::load_from_bytes(v24_file(
        &layout::TYPE_DEFINITION_V24_0,
        56,
        field_definition_v24_0::SIZE,
        16,
    ));
    assert!(p.loaded());
    assert_eq!(p.v24_variant(), V24Variant::V24_0);
    assert_eq!(p.v24_sub_version_label(), b"v24.0");
    assert_eq!(p.type_definition_count(), 2);
    let t1 = p.get_type_definition(1).unwrap();
    assert_eq!(t1.token, 0x0200_0002);
    let f = p.enumerate_type_fields(&t1);
    assert_eq!(
        (f[0].name, f[0].type_index, f[0].token),
        (&b"fld"[..], 9, 0x0400_0001)
    );
    let m = p.get_method_definition(0).unwrap();
    assert_eq!(
        (m.name, m.token, m.parameter_count),
        (&b"mth"[..], 0x0600_0001, 1)
    );
    let prm = p.get_parameter(0).unwrap();
    assert_eq!((prm.name, prm.type_index), (&b"prm"[..], 42));
    // Images are deferred for v24.0 / v24.1 even though the header slot is set.
    assert_eq!(p.image_count(), 0);
    assert!(p.find_image_containing_method(0).is_none());

    // v24.1: 100-byte TypeDefinition, 12-byte field / param, 52-byte method.
    let p = Parser::load_from_bytes(v24_file(&layout::TYPE_DEFINITION_V24_1, 52, 12, 12));
    assert_eq!(p.v24_sub_version_label(), b"v24.1");
    assert_eq!(p.type_definition_count(), 2);
    assert_eq!(p.get_method_definition(0).unwrap().token, 0x0600_0001);
    assert_eq!(p.image_count(), 0);

    // v24.2-v24.5: 92-byte TypeDefinition, 32-byte method; images are read.
    let p = Parser::load_from_bytes(v24_file(&layout::TYPE_DEFINITION_V24, 32, 12, 12));
    assert_eq!(p.v24_variant(), V24Variant::V24_2Through5);
    assert_eq!(p.v24_sub_version_label(), b"v24");
    assert_eq!(p.type_definition_count(), 2);
    let t1 = p.get_type_definition(1).unwrap();
    assert_eq!(p.enumerate_type_fields(&t1)[0].token, 0x0400_0001);
    assert_eq!(p.get_parameter(0).unwrap().type_index, 42);
    assert_eq!(p.image_count(), 1);
    let img = p.get_image(0).unwrap();
    assert_eq!(
        (img.name, img.method_start, img.method_end),
        (&b"Asm"[..], 0, 1)
    );
}

#[test]
fn v24_no_stride_matches_demotes_to_strings_only() {
    // Token 0 everywhere: DetectV24Variant keeps the default and
    // ValidateTypeTable rejects, so tables are dropped but loaded stays true.
    let mut heap = Heap::new();
    let module = heap.add(b"<Module>");
    let types = typedef_row(
        &layout::TYPE_DEFINITION_V24,
        &Td {
            name: module,
            ..Td::default()
        },
    );
    let p = Parser::load_from_bytes(flat_file(
        24,
        &[
            (header_v29::STRING_OFFSET, heap.0),
            (header_v29::TYPE_DEFINITIONS_OFFSET, types),
        ],
    ));
    assert!(p.loaded());
    assert_eq!(p.v24_sub_version_label(), b"v24");
    assert_eq!(p.type_definition_count(), 0);
    assert_eq!(p.get_heap_string(1), b"<Module>");
    assert_eq!(p.get_declaring_type_index(0), -1);
}

// ---------------------------------------------------------------------------
// v35: delta-length string literals, 84-byte TypeDefinition
// ---------------------------------------------------------------------------

#[test]
fn v35_string_literal_delta_length() {
    let mut heap = Heap::new();
    let module = heap.add(b"<Module>");
    let mth = heap.add(b"m");
    let types = typedef_row(
        &layout::TYPE_DEFINITION_V35,
        &Td {
            name: module,
            method_count: 1,
            token: MODULE,
            ..Td::default()
        },
    );
    let methods = method_row_v31(mth as u32, 0, 3, 0, 0x0600_0001, 6, 0);
    let p = Parser::load_from_bytes(flat_file(
        35,
        &[
            (header_v29::STRING_OFFSET, heap.0),
            (header_v29::STRING_LITERAL_OFFSET, i32s(&[0, 3, 3])),
            (header_v29::STRING_LITERAL_DATA_OFFSET, b"abcde".to_vec()),
            (header_v29::TYPE_DEFINITIONS_OFFSET, types),
            (header_v29::METHODS_OFFSET, methods),
        ],
    ));
    assert!(p.loaded());
    assert_eq!(p.string_literal_count(), 3);
    assert_eq!(p.get_string_literal(0), Some(&b"abc"[..]));
    assert_eq!(p.get_string_literal(1), Some(&b""[..])); // zero delta
    assert_eq!(p.get_string_literal(2), Some(&b"de"[..])); // last: up to data size
    assert_eq!(p.type_definition_count(), 1);
    assert_eq!(p.get_type_definition(0).unwrap().method_count, 1);
    let m = p.get_method_definition(0).unwrap();
    assert_eq!(
        (m.name, m.return_type_index, m.flags, m.token),
        (&b"m"[..], 3, 6, 0x0600_0001)
    );

    // Decreasing dataIndex -> nullopt; a negative dataIndex -> "".
    let p = Parser::load_from_bytes(flat_file(
        35,
        &[
            (header_v29::STRING_LITERAL_OFFSET, i32s(&[3, 0, -5])),
            (header_v29::STRING_LITERAL_DATA_OFFSET, b"abcde".to_vec()),
        ],
    ));
    assert!(p.loaded());
    assert_eq!(p.get_string_literal(0), None);
    assert_eq!(p.get_string_literal(1), None); // next (-5) < 0
    assert_eq!(p.get_string_literal(2), Some(&b""[..]));
    // No type table in this file: v35 still has a stride, but the empty
    // table fails ValidateTypeTable.
    assert_eq!(p.type_definition_count(), 0);
}

// ---------------------------------------------------------------------------
// v38 / v39: section header + variable-width indices
// ---------------------------------------------------------------------------

fn section_header_case(version: i32) {
    // typeDefinitions.count = 2 -> TDIS 1; genericContainers.count = 0 ->
    // GCIS 1; parameters: 1 row of 10 bytes -> TIS 2, PIS 1.
    let (tis, tdis, gcis, pis) = (
        2usize,
        1usize,
        1usize,
        if version >= 39 { 1usize } else { 4 },
    );
    let mut heap = Heap::new();
    let module = heap.add(b"<Module>");
    let ns = heap.add(b"N");
    let inner = heap.add(b"Inner");
    let fld = heap.add(b"f");
    let mth = heap.add(b"Run");
    let prm = heap.add(b"x");
    let asm = heap.add(b"Game");

    let types = cat(&[
        typedef_row_v38(
            tis,
            gcis,
            &Td {
                name: module,
                nested_start: 0,
                nested_count: 1,
                token: MODULE,
                ..Td::default()
            },
            0xFFFF,
        ),
        typedef_row_v38(
            tis,
            gcis,
            &Td {
                name: inner,
                ns,
                field_start: 0,
                field_count: 1,
                method_start: 0,
                method_count: 1,
                bitfield: 2,
                flags: 0x100,
                token: 0x0200_0002,
                ..Td::default()
            },
            0x1234,
        ),
    ]);
    // Field: int32 nameIndex + TIS typeIndex + uint32 token.
    let mut field = vec![0u8; 8 + tis];
    put_i32(&mut field, 0, fld);
    put_uint_n(&mut field, 4, tis, 0xBEEF);
    put_u32(&mut field, 4 + tis, 0x0400_0001);
    // FieldDefault: int32 fieldIndex + TIS typeIndex + int32 dataIndex.
    let mut fdef = vec![0u8; 8 + tis];
    put_i32(&mut fdef, 0, 0);
    put_uint_n(&mut fdef, 4, tis, 0x0102);
    put_i32(&mut fdef, 4 + tis, 0);
    let mut fdata = vec![0x06];
    fdata.extend_from_slice(b"abc");
    // Method: uint name + TDIS decl + TIS ret + int rpt + PIS pstart + GCIS
    // gci + uint token + 4 ushort.
    let mstride = if version >= 39 {
        method_definition_v38::V39_FIXED_BYTES + tdis + tis + pis + gcis
    } else {
        method_definition_v38::V38_FIXED_BYTES + tdis + tis + gcis
    };
    let mut method = vec![0u8; mstride];
    let mut o = 0;
    put_u32(&mut method, o, mth as u32);
    o += 4;
    put_uint_n(&mut method, o, tdis, 1);
    o += tdis;
    put_uint_n(&mut method, o, tis, 0x0203);
    o += tis;
    put_i32(&mut method, o, 0x0800_0000);
    o += 4;
    put_uint_n(&mut method, o, pis, 0);
    o += pis;
    put_uint_n(&mut method, o, gcis, 0xFF);
    o += gcis;
    put_u32(&mut method, o, 0x0600_0001);
    o += 4;
    put_u16(&mut method, o, 0x86);
    put_u16(&mut method, o + 6, 1);
    assert_eq!(o + 8, mstride);
    // Parameter: uint name + uint token + TIS typeIndex.
    let mut param = vec![0u8; 8 + tis];
    put_u32(&mut param, 0, prm as u32);
    put_u32(&mut param, 4, 0x0800_0001);
    put_uint_n(&mut param, 8, tis, 0x0304);
    // Image: 32 + 2 * TDIS.
    let mut image = vec![0u8; 32 + 2 * tdis];
    put_u32(&mut image, 0, asm as u32);
    put_uint_n(&mut image, 8, tdis, 0);
    put_u32(&mut image, 8 + tdis, 2);
    put_u32(&mut image, 8 + tdis + 4 + tdis + 4 + 4, 7);

    let p = Parser::load_from_bytes(section_file(
        version,
        &[
            (header_v38::STRINGS, heap.0, 0),
            (header_v38::STRING_LITERALS, i32s(&[0]), 1),
            (header_v38::STRING_LITERAL_DATA, b"lit".to_vec(), 0),
            (header_v38::TYPE_DEFINITIONS, types, 2),
            (header_v38::FIELDS, field, 1),
            (header_v38::FIELD_DEFAULT_VALUES, fdef, 1),
            (header_v38::FIELD_AND_PARAMETER_DEFAULT_VALUE_DATA, fdata, 0),
            (header_v38::METHODS, method, 1),
            (header_v38::PARAMETERS, param, 1),
            (header_v38::IMAGES, image, 1),
            (header_v38::NESTED_TYPES, i32s(&[1]), 1),
        ],
    ));
    assert!(p.loaded(), "v{version}");
    assert_eq!(p.version(), version);
    let ix = p.index_sizes();
    assert_eq!(
        (
            ix.type_index_size,
            ix.type_definition_index_size,
            ix.generic_container_index_size,
            ix.parameter_index_size
        ),
        (2, 1, 1, 1)
    );
    assert_eq!(p.parameters_section_count(), 1);
    assert_eq!(p.string_literal_count(), 1);
    assert_eq!(p.get_string_literal(0), Some(&b"lit"[..]));

    assert_eq!(p.type_definition_count(), 2);
    let t0 = p.get_type_definition(0).unwrap();
    assert_eq!((t0.name, t0.declaring_type_index), (&b"<Module>"[..], -1)); // 0xFFFF sentinel
    let t1 = p.get_type_definition(1).unwrap();
    assert_eq!(
        (t1.ns, t1.name, t1.declaring_type_index),
        (&b"N"[..], &b"Inner"[..], 0x1234)
    );
    assert_eq!((t1.flags, t1.bitfield, t1.token), (0x100, 2, 0x0200_0002));
    assert!(t1.is_enum());

    let f = p.enumerate_type_fields(&t1);
    assert_eq!(
        (f[0].name, f[0].type_index, f[0].token),
        (&b"f"[..], 0xBEEF, 0x0400_0001)
    );
    let d = p.find_field_default(0).unwrap();
    assert_eq!(
        (d.type_index, d.data_index, d.data),
        (0x0102, 0, &b"\x06abc"[..])
    );
    assert_eq!(p.try_decode_string_default(0).as_deref(), Some(&b"abc"[..]));

    assert_eq!(p.method_definition_count(), 1);
    let m = p.get_method_definition(0).unwrap();
    assert_eq!(
        (m.name, m.declaring_type_index, m.return_type_index),
        (&b"Run"[..], 1, 0x0203)
    );
    assert_eq!(
        (m.parameter_start, m.parameter_count, m.flags, m.token),
        (0, 1, 0x86, 0x0600_0001)
    );
    let prm = p.enumerate_method_parameters(&m);
    assert_eq!(
        (prm[0].name, prm[0].token, prm[0].type_index),
        (&b"x"[..], 0x0800_0001, 0x0304)
    );

    assert_eq!(p.image_count(), 1);
    let img = p.get_image(0).unwrap();
    assert_eq!(
        (img.name, img.type_start, img.type_count, img.token),
        (&b"Game"[..], 0, 2, 7)
    );
    assert_eq!((img.method_start, img.method_end), (0, 1));

    assert_eq!(p.get_declaring_type_index(1), 0);
    assert_eq!(p.get_declaring_type_index(0), -1);
}

#[test]
fn v39_section_header_variable_index_sizes() {
    section_header_case(39);
}

#[test]
fn v38_section_header_fixed_parameter_index() {
    section_header_case(38);
}

#[test]
fn v38_type_index_size_falls_back_to_4() {
    // parameters.size / count - 8 = 3 -> not 1/2/4, keep the default 4 and
    // mark tablesDegraded + zero the mismatched parameters table (D-M3).
    let p = Parser::load_from_bytes(section_file(
        38,
        &[(header_v38::PARAMETERS, vec![0u8; 11], 1)],
    ));
    assert_eq!(p.index_sizes().type_index_size, 4);
    // count = 0 leaves everything at its row-count width (count 0 -> 1).
    assert_eq!(p.index_sizes().type_definition_index_size, 1);
    assert!(p.tables_degraded());
    assert_eq!(p.parameter_count(), 0);
}

#[test]
fn v38_mismatched_method_and_image_table_strides_zero_only_corrupt_tables() {
    // D-M3: A valid TypeDef[0] (<Module>) is NOT enough when another table's
    // stride*count disagrees with its header section size (e.g. TDIS/PIS drift).
    // The mismatched table must be zeroed and tables_degraded() set true while
    // valid tables (types/fields/params) stay intact.
    let mut heap = Heap::new();
    let mod_name = heap.add(b"<Module>");
    let param_bytes = vec![0u8; 8 + 2]; // TIS = 2, count = 1
    let types = typedef_row_v38(
        2,
        1,
        &Td {
            name: mod_name,
            token: MODULE,
            ..Td::default()
        },
        0xFFFF,
    );
    // Deliberately wrong methods byte size (3 bytes off from count=1 * stride)
    // and wrong images byte size (non-multiple of stride).
    let raw = section_file(
        39,
        &[
            (header_v38::STRINGS, heap.0, 0),
            (header_v38::TYPE_DEFINITIONS, types, 1),
            (header_v38::PARAMETERS, param_bytes, 1),
            (header_v38::METHODS, vec![0u8; 19], 1),
            (header_v38::IMAGES, vec![0u8; 17], 1),
        ],
    );
    let p = Parser::load_from_bytes(raw.clone());
    assert!(p.loaded());
    assert!(p.tables_degraded());
    assert_eq!(p.type_definition_count(), 1);
    assert_eq!(p.parameter_count(), 1);
    assert_eq!(p.method_definition_count(), 0);
    assert_eq!(p.image_count(), 0);

    // FFI dc_md_tables_degraded mirrors Parser::tables_degraded().
    let path = temp_file("v39_degraded.dat", &raw);
    let w = wide(&path);
    // SAFETY: valid UTF-16 path; handle freed below.
    unsafe {
        let md = dc_md_load_path_w(w.as_ptr(), w.len());
        assert_eq!(dc_md_loaded(md), 1);
        assert_eq!(dc_md_tables_degraded(md), 1);
        assert_eq!(dc_md_tables_degraded(std::ptr::null()), 0);
        dc_md_free(md);
    }
    let _ = std::fs::remove_file(path);
}

// ---------------------------------------------------------------------------
// Rejections: truncated / corrupt / bad magic
// ---------------------------------------------------------------------------

#[test]
fn bad_magic_is_rejected() {
    let mut f = v29_file();
    f[0] ^= 0xFF;
    let p = Parser::load_from_bytes(f);
    assert!(!p.loaded());
    assert_eq!(p.version(), 0);
    assert_eq!(p.string_literal_count(), 0);
    assert_eq!(p.get_heap_string(1), b"");
    assert_eq!(p.type_definition_count(), 0);
}

#[test]
fn truncated_and_corrupt_files() {
    // Shorter than the 32-byte header prefix.
    let p = Parser::load_from_bytes(v29_file()[..31].to_vec());
    assert!(!p.loaded());
    assert_eq!(p.version(), 0);

    // Header prefix fine but shorter than the flat header: strings only.
    let mut short = vec![0u8; 100];
    put_u32(&mut short, 0, layout::SANITY);
    put_i32(&mut short, 4, 29);
    let p = Parser::load_from_bytes(short);
    assert!(p.loaded());
    assert_eq!(p.type_definition_count(), 0);

    // Truncated mid type table: tables out of bounds, strings still work.
    let full = v29_file();
    let types_off = i32::from_le_bytes(
        full[header_v29::TYPE_DEFINITIONS_OFFSET..][..4]
            .try_into()
            .unwrap(),
    );
    let p = Parser::load_from_bytes(full[..types_off as usize + 10].to_vec());
    assert!(p.loaded());
    assert_eq!(p.version(), 29);
    assert_eq!(p.type_definition_count(), 0);
    assert_eq!(p.get_string_literal(0), Some(&b"abc"[..]));

    // Prefix table out of bounds: not loaded, but version is kept.
    let mut f = v29_file();
    put_i32(&mut f, header_v29::STRING_SIZE, i32::MAX);
    let p = Parser::load_from_bytes(f);
    assert!(!p.loaded());
    assert_eq!(p.version(), 29);

    // Negative offset.
    let mut f = v29_file();
    put_i32(&mut f, header_v29::FIELDS_OFFSET, -4);
    let p = Parser::load_from_bytes(f);
    assert!(p.loaded());
    assert_eq!(p.type_definition_count(), 0);

    // Unsupported versions keep the version number.
    for v in [15, 40] {
        let mut f = v29_file();
        put_i32(&mut f, 4, v);
        let p = Parser::load_from_bytes(f);
        assert!(!p.loaded());
        assert_eq!(p.version(), v);
    }

    // v30 is in range but has no TypeDefinition stride: strings only.
    let mut f = v29_file();
    put_i32(&mut f, 4, 30);
    let p = Parser::load_from_bytes(f);
    assert!(p.loaded());
    assert_eq!(p.type_definition_count(), 0);
    assert_eq!(p.method_definition_count(), 0);

    // v38 file shorter than the section header.
    let mut f = vec![0u8; 200];
    put_u32(&mut f, 0, layout::SANITY);
    put_i32(&mut f, 4, 38);
    assert!(!Parser::load_from_bytes(f).loaded());

    // v38 images section pointing past EOF: "tables out of bounds".
    let mut heap = Heap::new();
    let module = heap.add(b"<Module>");
    let mut f = section_file(
        39,
        &[
            (header_v38::STRINGS, heap.0, 0),
            (
                header_v38::TYPE_DEFINITIONS,
                typedef_row_v38(
                    4,
                    1,
                    &Td {
                        name: module,
                        token: MODULE,
                        ..Td::default()
                    },
                    0,
                ),
                1,
            ),
        ],
    );
    put_u32(&mut f, header_v38::IMAGES + section::OFFSET, 0x100);
    put_u32(&mut f, header_v38::IMAGES + section::SIZE, 0x10_0000);
    let p = Parser::load_from_bytes(f);
    assert!(p.loaded());
    assert_eq!(p.type_definition_count(), 0);

    // Name right, token wrong: ValidateTypeTable demotes to strings-only.
    let mut f = v29_file();
    let off = types_off as usize + layout::TYPE_DEFINITION_V29.token;
    put_u32(&mut f, off, 0);
    let p = Parser::load_from_bytes(f);
    assert!(p.loaded());
    assert_eq!(p.type_definition_count(), 0);
    assert_eq!(p.image_count(), 0);

    // Empty input (only reachable through load_from_bytes).
    assert!(!Parser::load_from_bytes(Vec::new()).loaded());
}

#[test]
fn compressed_string_default_edge_cases() {
    let mk = |blob: &[u8]| {
        let mut heap = Heap::new();
        let module = heap.add(b"<Module>");
        Parser::load_from_bytes(flat_file(
            29,
            &[
                (header_v29::STRING_OFFSET, heap.0),
                (
                    header_v29::TYPE_DEFINITIONS_OFFSET,
                    typedef_row(
                        &layout::TYPE_DEFINITION_V29,
                        &Td {
                            name: module,
                            token: MODULE,
                            ..Td::default()
                        },
                    ),
                ),
                (
                    header_v29::FIELD_AND_PARAMETER_DEFAULT_VALUE_DATA_OFFSET,
                    blob.to_vec(),
                ),
            ],
        ))
    };
    // Null-string sentinel and zero length -> "".
    assert_eq!(
        mk(&[0xFF]).try_decode_string_default(0).as_deref(),
        Some(&b""[..])
    );
    assert_eq!(
        mk(&[0x00]).try_decode_string_default(0).as_deref(),
        Some(&b""[..])
    );
    // Odd encoded value (negative) -> nullopt.
    assert_eq!(mk(&[0x03, b'a']).try_decode_string_default(0), None);
    // 2-byte header: 0x80 0x08 -> encoded 8 -> length 4.
    assert_eq!(
        mk(b"\x80\x08abcd").try_decode_string_default(0).as_deref(),
        Some(&b"abcd"[..])
    );
    // 2-byte header with only one byte available.
    assert_eq!(mk(&[0x80]).try_decode_string_default(0), None);
    // 4-byte header.
    assert_eq!(
        mk(b"\xC0\x00\x00\x04ab")
            .try_decode_string_default(0)
            .as_deref(),
        Some(&b"ab"[..])
    );
    // Invalid lead (0xE0 prefix) -> nullopt.
    assert_eq!(mk(&[0xE0, 0, 0, 0]).try_decode_string_default(0), None);
    // Length past the blob end.
    assert_eq!(mk(b"\x0Aabc").try_decode_string_default(0), None);
    // Too many control chars (> 25%).
    assert_eq!(mk(b"\x08\x01\x01ab").try_decode_string_default(0), None);
    // Truncated multi-byte sequence at the end -> U+FFFD.
    assert_eq!(
        mk(b"\x06ab\xC3").try_decode_string_default(0).as_deref(),
        Some(&b"ab\xEF\xBF\xBD"[..])
    );
    // Valid 2-byte UTF-8 passes through borrowed.
    let p = mk(b"\x06a\xC3\xA9");
    assert!(matches!(
        p.try_decode_string_default(0),
        Some(Cow::Borrowed(b"a\xC3\xA9"))
    ));
}

// ---------------------------------------------------------------------------
// FFI
// ---------------------------------------------------------------------------

fn wide(s: &std::path::Path) -> Vec<u16> {
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        s.as_os_str().encode_wide().collect()
    }
    #[cfg(not(windows))]
    {
        s.to_string_lossy().encode_utf16().collect()
    }
}

fn temp_file(name: &str, bytes: &[u8]) -> std::path::PathBuf {
    let path = std::env::temp_dir().join(format!("discrete_core_{}_{name}", std::process::id()));
    std::fs::write(&path, bytes).unwrap();
    path
}

fn as_slice<'a>(b: DcBytes) -> &'a [u8] {
    if b.len == 0 {
        &[]
    } else {
        // SAFETY: test-only; the handle outlives every use.
        unsafe { std::slice::from_raw_parts(b.ptr, b.len) }
    }
}

#[test]
fn ffi_null_handle_answers_not_loaded() {
    let null = std::ptr::null();
    // SAFETY: NULL handle and NULL out pointers are part of the contract.
    unsafe {
        assert_eq!(dc_md_loaded(null), 0);
        assert_eq!(dc_md_version(null), 0);
        assert_eq!(dc_md_v24_sub_version_label(null).len, 0);
        assert_eq!(dc_md_string_literal_count(null), 0);
        assert_eq!(dc_md_get_string_literal(null, 0, std::ptr::null_mut()), 0);
        assert_eq!(dc_md_get_heap_string(null, 1).len, 0);
        assert_eq!(dc_md_type_definition_count(null), 0);
        assert_eq!(dc_md_get_type_definition(null, 0, std::ptr::null_mut()), 0);
        assert_eq!(
            dc_md_find_type_by_name(
                null,
                std::ptr::null(),
                0,
                std::ptr::null(),
                0,
                std::ptr::null_mut()
            ),
            0
        );
        assert_eq!(
            dc_md_enumerate_type_fields(null, std::ptr::null(), std::ptr::null_mut(), 0),
            0
        );
        assert_eq!(dc_md_find_field_default(null, 0, std::ptr::null_mut()), 0);
        assert_eq!(
            dc_md_try_decode_string_default(null, 0, std::ptr::null_mut()),
            0
        );
        assert_eq!(dc_md_get_declaring_type_index(null, 0), -1);
        assert_eq!(dc_md_method_definition_count(null), 0);
        assert_eq!(
            dc_md_get_method_definition(null, 0, std::ptr::null_mut()),
            0
        );
        assert_eq!(
            dc_md_enumerate_type_methods(null, std::ptr::null(), std::ptr::null_mut(), 0),
            0
        );
        assert_eq!(dc_md_parameter_count(null), 0);
        assert_eq!(dc_md_get_parameter(null, 0, std::ptr::null_mut()), 0);
        assert_eq!(
            dc_md_enumerate_method_parameters(null, std::ptr::null(), std::ptr::null_mut(), 0),
            0
        );
        assert_eq!(dc_md_image_count(null), 0);
        assert_eq!(dc_md_get_image(null, 0, std::ptr::null_mut()), 0);
        assert_eq!(dc_md_enumerate_images(null, std::ptr::null_mut(), 0), 0);
        assert_eq!(
            dc_md_find_image_containing_method(null, 0, std::ptr::null_mut()),
            0
        );
        dc_md_free(std::ptr::null_mut());
    }
}

#[test]
fn ffi_load_and_enumerate() {
    let path = temp_file("v29.dat", &v29_file());
    let w = wide(&path);
    // SAFETY: valid UTF-16 buffer; the handle is freed at the end.
    unsafe {
        let md = dc_md_load_path_w(w.as_ptr(), w.len());
        assert!(!md.is_null());
        assert_eq!(dc_md_loaded(md), 1);
        assert_eq!(dc_md_version(md), 29);
        assert_eq!(dc_md_type_definition_count(md), 2);

        let mut lit = DcBytes::EMPTY;
        assert_eq!(dc_md_get_string_literal(md, 0, &mut lit), 1);
        assert_eq!(as_slice(lit), b"abc");
        assert_eq!(dc_md_get_string_literal(md, 2, &mut lit), 0);

        let mut td = std::mem::zeroed::<DcTypeDef>();
        assert_eq!(
            dc_md_find_type_by_name(md, b"Foo".as_ptr(), 3, b"Bar".as_ptr(), 3, &mut td),
            1
        );
        assert_eq!(
            (td.index, as_slice(td.name), as_slice(td.ns)),
            (1, &b"Bar"[..], &b"Foo"[..])
        );

        // cap 0 + NULL out -> total only; partial cap copies min(total, cap).
        assert_eq!(
            dc_md_enumerate_type_fields(md, &td, std::ptr::null_mut(), 0),
            2
        );
        let mut fields = [std::mem::zeroed::<DcFieldDef>(); 1];
        assert_eq!(
            dc_md_enumerate_type_fields(md, &td, fields.as_mut_ptr(), 1),
            2
        );
        assert_eq!(as_slice(fields[0].name), b"field0");

        let mut methods = [std::mem::zeroed::<DcMethodDef>(); 4];
        assert_eq!(
            dc_md_enumerate_type_methods(md, &td, methods.as_mut_ptr(), 4),
            1
        );
        let mut params = [std::mem::zeroed::<DcParameterDef>(); 4];
        assert_eq!(
            dc_md_enumerate_method_parameters(md, &methods[0], params.as_mut_ptr(), 4),
            1
        );
        assert_eq!(as_slice(params[0].name), b"arg");

        let mut fd = std::mem::zeroed::<DcFieldDefault>();
        assert_eq!(dc_md_find_field_default(md, 1, &mut fd), 1);
        assert_eq!(fd.data.len, 12);

        // Sanitised strings are interned: same pointer on every call.
        let (mut a, mut b) = (DcBytes::EMPTY, DcBytes::EMPTY);
        assert_eq!(dc_md_try_decode_string_default(md, 6, &mut a), 1);
        assert_eq!(dc_md_try_decode_string_default(md, 6, &mut b), 1);
        assert_eq!(as_slice(a), b"ab\xEF\xBF\xBDcd");
        assert_eq!(a.ptr, b.ptr);

        let mut imgs = [std::mem::zeroed::<DcImageDef>(); 2];
        assert_eq!(dc_md_enumerate_images(md, imgs.as_mut_ptr(), 2), 1);
        assert_eq!((imgs[0].method_start, imgs[0].method_end), (0, 1));
        let mut img = std::mem::zeroed::<DcImageDef>();
        assert_eq!(dc_md_find_image_containing_method(md, 0, &mut img), 1);
        assert_eq!(dc_md_get_declaring_type_index(md, 1), 0);
        dc_md_free(md);
    }
    let _ = std::fs::remove_file(path);
}

#[test]
fn ffi_failed_loads_still_return_a_handle() {
    // SAFETY: valid (or NULL with len 0) buffers; handles are freed.
    unsafe {
        let md = dc_md_load_path_w(std::ptr::null(), 0);
        assert!(!md.is_null());
        assert_eq!(dc_md_loaded(md), 0);
        dc_md_free(md);

        let missing = wide(&std::env::temp_dir().join("discrete_core_does_not_exist.dat"));
        let md = dc_md_load_path_w(missing.as_ptr(), missing.len());
        assert_eq!(dc_md_loaded(md), 0);
        assert_eq!(dc_md_version(md), 0);
        dc_md_free(md);

        // Empty file: size <= 0 is a read failure.
        let empty = temp_file("empty.dat", b"");
        let w = wide(&empty);
        let md = dc_md_load_path_w(w.as_ptr(), w.len());
        assert_eq!(dc_md_loaded(md), 0);
        dc_md_free(md);
        let _ = std::fs::remove_file(empty);

        // Unsupported version: not loaded, version reflects the header.
        let mut f = v29_file();
        put_i32(&mut f, 4, 40);
        let bad = temp_file("v40.dat", &f);
        let w = wide(&bad);
        let md = dc_md_load_path_w(w.as_ptr(), w.len());
        assert_eq!(dc_md_loaded(md), 0);
        assert_eq!(dc_md_version(md), 40);
        dc_md_free(md);
        let _ = std::fs::remove_file(bad);
    }
}

// ---------------------------------------------------------------------------
// Log text
// ---------------------------------------------------------------------------

static LOG_LINES: std::sync::Mutex<Vec<String>> = std::sync::Mutex::new(Vec::new());

unsafe extern "C" fn capture_sink(_category: u32, msg: *const std::ffi::c_char, len: usize) {
    // SAFETY: the sink contract guarantees (msg, len) is readable.
    let bytes = unsafe { std::slice::from_raw_parts(msg.cast::<u8>(), len) };
    LOG_LINES
        .lock()
        .unwrap()
        .push(String::from_utf8(bytes.to_vec()).expect("sink lines are UTF-8"));
}

#[test]
fn log_lines_match_cpp_text() {
    // SAFETY: capture_sink is a valid 'static function.
    unsafe { crate::ffi::dc_core_set_log_sink(Some(capture_sink)) };

    // Long, wrong-token <Module>-like name: the ValidateTypeTable line is
    // cut at 319 bytes like _snprintf_s(line[320], _TRUNCATE).
    let long_name = vec![b'Q'; 400];
    let mut heap = Heap::new();
    let name = heap.add(&long_name);
    let f = flat_file(
        29,
        &[
            (header_v29::STRING_OFFSET, heap.0),
            (
                header_v29::TYPE_DEFINITIONS_OFFSET,
                typedef_row(
                    &layout::TYPE_DEFINITION_V29,
                    &Td {
                        name,
                        token: 7,
                        ..Td::default()
                    },
                ),
            ),
        ],
    );
    let size = f.len();
    let p = Parser::load_from_bytes(f);
    assert!(p.loaded());

    let lines = LOG_LINES.lock().unwrap().clone();
    let failed = lines
        .iter()
        .find(|l| {
            l.starts_with("MetadataParser: ValidateTypeTable FAILED for v29 — TypeDef[0] name='QQQ")
        })
        .expect("ValidateTypeTable line");
    assert_eq!(failed.len(), 319);
    let loaded = format!(
        "MetadataParser: loaded version=29 size={size} strLitTable=0 strLitData=0 nameHeap=402 \
         typeDefs=88 fields=0 fieldDefaults=0 (typeTablesParsed=no)"
    );
    assert!(lines.contains(&loaded), "{loaded}");
}

// ---------------------------------------------------------------------------
// Real files
// ---------------------------------------------------------------------------

/// Loads every file in `DISCRETE_METADATA_FIXTURES` (semicolon-separated).
/// Prints `SKIPPED` to stderr when the variable is unset or empty (D-M4).
#[test]
fn real_metadata_fixtures() {
    let Ok(list) = std::env::var("DISCRETE_METADATA_FIXTURES") else {
        eprintln!("SKIPPED real_metadata_fixtures: DISCRETE_METADATA_FIXTURES is unset");
        return;
    };
    let paths: Vec<&str> = list
        .split(';')
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .collect();
    if paths.is_empty() {
        eprintln!("SKIPPED real_metadata_fixtures: DISCRETE_METADATA_FIXTURES is empty");
        return;
    }
    for path in paths {
        let w = wide(std::path::Path::new(path));
        let p = Parser::load_from_path(&w);
        assert!(p.loaded(), "{path}: not loaded");
        assert!(p.version() > 0, "{path}: version");
        let types = p.type_definition_count();
        assert!(types > 0, "{path}: no types (v{})", p.version());
        let t0 = p.get_type_definition(0).unwrap();
        assert_eq!(t0.name, b"<Module>", "{path}");
        assert_eq!(t0.token, MODULE, "{path}");

        // Light consistency checks over the whole file.
        let methods = p.method_definition_count();
        let mut seen_methods = 0usize;
        for i in 0..types as i32 {
            let t = p.get_type_definition(i).unwrap();
            seen_methods += p.enumerate_type_methods(&t).len();
            let _ = p.enumerate_type_fields(&t);
            let _ = p.get_declaring_type_index(i);
        }
        assert!(seen_methods <= methods as usize, "{path}");
        let images = p.enumerate_images();
        if p.version() != 24 || p.v24_sub_version_label() == b"v24" {
            assert!(!images.is_empty(), "{path}: no images");
        }
        let lits = p.enumerate_string_literals(0);
        assert_eq!(lits.len(), p.string_literal_count() as usize);
        eprintln!(
            "{path}: v{} {} types={types} methods={methods} params={} images={} literals={}",
            p.version(),
            String::from_utf8_lossy(p.v24_sub_version_label()),
            p.parameter_count(),
            images.len(),
            lits.len()
        );
    }
}

// ---------------------------------------------------------------------------
// File read parity (ReadEntireFile)
// ---------------------------------------------------------------------------

#[test]
fn buffer_allocation_failure_is_a_read_failure_not_an_abort() {
    // An impossible size must come back as None (C++: bad_alloc caught by the
    // pipe server) instead of aborting the process via handle_alloc_error.
    assert!(alloc_zeroed(usize::MAX).is_none());
    assert!(alloc_zeroed(isize::MAX as usize).is_none());
    assert_eq!(alloc_zeroed(0), Some(Vec::new()));
    assert_eq!(alloc_zeroed(16), Some(vec![0u8; 16]));
}

#[test]
fn read_entire_file_stops_at_embedded_nul_and_rejects_empty() {
    let f = temp_file("nul_cut.dat", b"abc");
    let mut w = wide(&f);
    assert_eq!(read_entire_file(&w).as_deref(), Some(&b"abc"[..]));
    // CreateFileW(path.c_str()) ignores everything after the first NUL.
    w.push(0);
    w.extend("garbage".encode_utf16());
    assert_eq!(read_entire_file(&w).as_deref(), Some(&b"abc"[..]));
    // A path that is empty once cut at the NUL is CreateFileW("") -> failure.
    assert!(read_entire_file(&[0, b'x' as u16]).is_none());
    assert!(read_entire_file(&[]).is_none());
    let _ = std::fs::remove_file(f);
}

/// The path must reach CreateFileW unchanged: no `\\?\` rewriting of long
/// paths (std::fs does that for 248+ UTF-16 units). A plain long path then
/// opens only if the process itself is long-path enabled (manifest AND
/// policy), exactly what the C++ build sees.
#[cfg(windows)]
#[test]
fn long_paths_are_not_rewritten_to_verbatim_form() {
    // ntdll.lib does not export RtlAreLongPathsEnabled, so resolve it at run
    // time. Absent (pre-1607 Windows) means no long-path support at all.
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetModuleHandleW(name: *const u16) -> *mut std::ffi::c_void;
        fn GetProcAddress(
            module: *mut std::ffi::c_void,
            name: *const std::ffi::c_char,
        ) -> Option<unsafe extern "system" fn() -> u8>;
    }
    let ntdll: Vec<u16> = "ntdll.dll".encode_utf16().chain(Some(0)).collect();
    // SAFETY: NUL-terminated names; ntdll is always loaded; the export (when
    // present) is `BOOLEAN NTAPI RtlAreLongPathsEnabled(void)`.
    let long_ok = unsafe {
        let m = GetModuleHandleW(ntdll.as_ptr());
        assert!(!m.is_null());
        GetProcAddress(m, c"RtlAreLongPathsEnabled".as_ptr()).is_some_and(|f| f() != 0)
    };

    // Build a > 300-character directory chain under %TEMP% (std may create
    // it via \\?\, which is fine — only the read under test must not).
    let base = std::env::temp_dir().join(format!("discrete_core_{}_long", std::process::id()));
    let mut dir = base.clone();
    while dir.as_os_str().len() < 320 {
        dir.push("abcdefghijklmnopqrstuvwxyz0123456789");
    }
    std::fs::create_dir_all(&dir).unwrap();
    let file = dir.join("global-metadata.dat");
    std::fs::write(&file, b"payload").unwrap();

    let plain = wide(&file);
    assert!(plain.len() >= 260);
    let mut verbatim: Vec<u16> = r"\\?\".encode_utf16().collect();
    verbatim.extend_from_slice(&plain);

    assert_eq!(read_entire_file(&plain).is_some(), long_ok);
    // An explicit \\?\ prefix is honoured by CreateFileW itself.
    assert_eq!(read_entire_file(&verbatim).as_deref(), Some(&b"payload"[..]));

    let _ = std::fs::remove_dir_all(&base);
}

#[test]
fn version_type_tables_supported_and_split_warnings() {
    // SAFETY: capture_sink is a valid 'static function.
    unsafe { crate::ffi::dc_core_set_log_sink(Some(capture_sink)) };

    // Strings-only versions (e.g. v23, v30): loaded, type_tables_supported == false,
    // and the log explicitly reports strings-only instead of claiming v31 layout.
    for v in [23, 30] {
        let mut f = v29_file();
        put_i32(&mut f, 4, v);
        let p = Parser::load_from_bytes(f.clone());
        assert!(p.loaded(), "v{v} should load strings-only");
        assert!(!p.type_tables_supported(), "v{v} has no type tables");
        assert_eq!(p.type_definition_count(), 0);
        assert_eq!(p.get_string_literal(0), Some(&b"abc"[..]));

        let path = temp_file(&format!("v{v}_strings_only.dat"), &f);
        let w = wide(&path);
        // SAFETY: valid UTF-16 path; handle freed before removing file.
        unsafe {
            let md = dc_md_load_path_w(w.as_ptr(), w.len());
            assert_eq!(dc_md_loaded(md), 1);
            assert_eq!(dc_md_type_tables_supported(md), 0);
            assert_eq!(dc_md_type_tables_supported(std::ptr::null()), 0);
            dc_md_free(md);
        }
        let _ = std::fs::remove_file(path);
    }

    // Unverified v32 uses v31 layout with an explicit unverified soft warning
    // and reports type_tables_supported() == true.
    let mut f32 = v29_file();
    put_i32(&mut f32, 4, 32);
    let p32 = Parser::load_from_bytes(f32);
    assert!(p32.loaded());
    assert!(p32.type_tables_supported());
    assert_eq!(p32.type_definition_count(), 2);

    // Verified v29 reports type_tables_supported() == true.
    let p29 = Parser::load_from_bytes(v29_file());
    assert!(p29.type_tables_supported());

    let lines = LOG_LINES.lock().unwrap().clone();
    assert!(
        lines.iter().any(|l| l.contains("version 23 is strings-only")),
        "expected strings-only warning for v23"
    );
    assert!(
        lines.iter().any(|l| l.contains("version 30 is strings-only")),
        "expected strings-only warning for v30"
    );
    assert!(
        lines
            .iter()
            .any(|l| l.contains("version 32 is unverified — parsing with v31 layout")),
        "expected unverified v31-layout warning for v32"
    );
}

#[cfg(windows)]
#[test]
fn load_from_path_uses_readonly_file_mapping_and_releases_on_drop() {
    let path = temp_file("mapped_v29.dat", &v29_file());
    let w = wide(&path);
    {
        let p = Parser::load_from_path(&w);
        assert!(p.loaded());
        assert!(p.is_mapped());
        assert_eq!(p.get_string_literal(0), Some(&b"abc"[..]));
        assert_eq!(p.find_type_by_name(b"Foo", b"Bar").unwrap().index, 1);
    }
    // Dropping Parser must unmap the view and release the file so deletion succeeds.
    std::fs::remove_file(&path).expect("mapped metadata file should be removable after drop");
}

#[test]
fn lazy_indices_preserve_first_match_semantics() {
    // D-L12: duplicate (ns, name), duplicate field_index defaults, and multiple
    // images (including an empty-method image and overlapping method ranges)
    // must match the exact linear-scan first-match contract.
    let mut heap = Heap::new();
    let mod_name = heap.add(b"<Module>");
    let ns_a = heap.add(b"Game");
    let ns_b = heap.add(b"Other");
    let dup_name = heap.add(b"Hero");
    let img_empty = heap.add(b"EmptyAsm");
    let img_first = heap.add(b"FirstAsm");
    let img_second = heap.add(b"SecondAsm");

    let mut types = Vec::new();
    types.extend(typedef_row(
        &layout::TYPE_DEFINITION_V29,
        &Td {
            name: mod_name,
            token: MODULE,
            ..Td::default()
        },
    ));
    // idx 1: Game.Hero (methods 0..2)
    types.extend(typedef_row(
        &layout::TYPE_DEFINITION_V29,
        &Td {
            name: dup_name,
            ns: ns_a,
            method_start: 0,
            method_count: 2,
            token: 0x0200_0002,
            ..Td::default()
        },
    ));
    // idx 2: duplicate Game.Hero (methods 2..4)
    types.extend(typedef_row(
        &layout::TYPE_DEFINITION_V29,
        &Td {
            name: dup_name,
            ns: ns_a,
            method_start: 2,
            method_count: 2,
            token: 0x0200_0003,
            ..Td::default()
        },
    ));
    // idx 3: Other.Hero (same name, different namespace; methods 1..5 overlapping)
    types.extend(typedef_row(
        &layout::TYPE_DEFINITION_V29,
        &Td {
            name: dup_name,
            ns: ns_b,
            method_start: 1,
            method_count: 4,
            token: 0x0200_0004,
            ..Td::default()
        },
    ));

    let mut defaults = Vec::new();
    defaults.extend(i32s(&[7, 10, 0])); // first entry for field 7
    defaults.extend(i32s(&[7, 20, 4])); // duplicate entry for field 7
    defaults.extend(i32s(&[9, 30, 4]));

    let default_data = b"AAAABBBB".to_vec();

    let mut imgs = Vec::new();
    imgs.extend(image_row_v29(img_empty as u32, 0, 1, 1)); // <Module>: 0 methods
    imgs.extend(image_row_v29(img_first as u32, 1, 2, 2)); // types 1..3 -> methods [0, 4)
    imgs.extend(image_row_v29(img_second as u32, 3, 1, 3)); // type 3 -> methods [1, 5)

    let p = Parser::load_from_bytes(flat_file(
        29,
        &[
            (header_v29::STRING_OFFSET, heap.0),
            (header_v29::TYPE_DEFINITIONS_OFFSET, types),
            (header_v29::FIELD_DEFAULT_VALUES_OFFSET, defaults),
            (
                header_v29::FIELD_AND_PARAMETER_DEFAULT_VALUE_DATA_OFFSET,
                default_data,
            ),
            (header_v29::IMAGES_OFFSET, imgs),
        ],
    ));
    assert!(p.loaded());

    // Duplicate (ns, name) returns the FIRST matching type (index 1, not 2).
    assert_eq!(p.find_type_by_name(b"Game", b"Hero").unwrap().index, 1);
    // Repeated lookup uses cached index and returns the same result.
    assert_eq!(p.find_type_by_name(b"Game", b"Hero").unwrap().index, 1);
    assert_eq!(p.find_type_by_name(b"Other", b"Hero").unwrap().index, 3);
    assert!(p.find_type_by_name(b"Game", b"Missing").is_none());

    // Duplicate field_index returns the FIRST default row (type_index 10, data_index 0).
    let d7 = p.find_field_default(7).unwrap();
    assert_eq!((d7.field_index, d7.type_index, d7.data_index), (7, 10, 0));
    assert_eq!(d7.data, b"AAAABBBB");
    let d9 = p.find_field_default(9).unwrap();
    assert_eq!((d9.field_index, d9.type_index, d9.data_index), (9, 30, 4));
    assert_eq!(d9.data, b"BBBB");
    assert!(p.find_field_default(8).is_none());

    // Multiple images: method 2 is covered by both FirstAsm [0, 4) and SecondAsm [1, 5);
    // first match must win. Method 4 belongs only to SecondAsm; 5 and -1 miss.
    assert_eq!(
        p.find_image_containing_method(0).unwrap().name,
        b"FirstAsm"
    );
    assert_eq!(
        p.find_image_containing_method(2).unwrap().name,
        b"FirstAsm"
    );
    assert_eq!(
        p.find_image_containing_method(4).unwrap().name,
        b"SecondAsm"
    );
    assert!(p.find_image_containing_method(5).is_none());
    assert!(p.find_image_containing_method(-1).is_none());
    // EmptyAsm has (0, 0) range and must not claim method 0.
    let e = p.get_image(0).unwrap();
    assert_eq!((e.method_start, e.method_end), (0, 0));
}
