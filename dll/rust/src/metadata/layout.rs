//! On-disk layout for `global-metadata.dat` — Rust port of
//! `dll/src/il2cpp/MetadataLayout.h`.
//!
//! global-metadata.dat is purely a tagged-offset blob — every reference is a
//! 32-bit byte offset into the file, not a runtime pointer. So the file layout
//! itself is bitness-neutral. The bitness concern only kicks in when these
//! tables are cross-linked to live runtime pointers like `Il2CppClass*`; that
//! wrapper layer lives elsewhere.
//!
//! The C++ header declares `#pragma pack(4)` structs and `memcpy`s rows into
//! them. This port never reinterprets memory: each struct becomes a table of
//! byte offsets (`*Layout` constants below) and the parser reads every field
//! with `from_le_bytes` on a bounds-checked slice. The `const` assertions at
//! the bottom pin every stride to the value the C++ `static_assert`s pin.
//!
//! Coverage history (from the C++ header):
//!
//! Phase 14c covers v24 (Unity 2018.4-2019.x, e.g. SaGa Frontier Remastered),
//! v29 (Unity 2021.x-2022.1 AND Unity 6 / 6000.0.x — Unity 6 reverted to v29,
//! e.g. SaGa Frontier 2 Remastered), and v31 (Unity 2022.2-2023.x, e.g.
//! Emberward / Farthest Frontier). v27 (Unity 2020) shares the v29 layout per
//! upstream sources. The HEADER prefix up to typeDefinitionsOffset is the same
//! across v24/v27/v29/v31 — we read just that span via `HEADER_V29` and
//! dispatch the trailing TypeDefinition layout per version.
//!
//! v32+ (Unity 2023+ / Unity 6.1+): v32-v37 keep the flat header layout but
//! v35 drops two struct fields:
//!   * `Il2CppTypeDefinition.elementTypeIndex` (was at offset 20 in v29) —
//!     stride goes 88 → 84 bytes.
//!   * `Il2CppStringLiteral.length` — entry size 8 → 4 bytes; length is now
//!     recovered from the delta to the next entry's dataIndex.
//!
//! v38 rewrites the header into `Il2CppSectionMetadata{offset,size,count}`
//! triplets per table (12 bytes each, 31 sections) AND introduces
//! variable-width "metadata indices" — TypeIndex / TypeDefinitionIndex /
//! GenericContainerIndex / ParameterIndex (v39) shrink to 1, 2, or 4 bytes
//! per field based on each table's row count. v39 is incremental: only
//! ParamIndex joins the variable-width family (per Perfare/Il2CppDumper#903).
//!
//! All layouts cross-checked against Unity's official IL2CPP source in
//! vendor/libil2cpp/ + the Il2CppDumperV35plus community fork (pinned at
//! bbd9c0c2 in vendor/Il2CppDumperV35plus/) — see
//! docs/il2cpp-metadata-crosscheck.md for the field-by-field comparison.

/// `Il2CppGlobalMetadataHeader.sanity` magic.
pub const SANITY: u32 = 0xFAB1_1BAF;

// ---------------------------------------------------------------------------
// Headers
// ---------------------------------------------------------------------------

/// `HeaderPrefix` — the leading eight int32 fields, identical across v24..v37
/// (and sanity/version for v38+). 32 bytes.
pub mod header_prefix {
    pub const SANITY: usize = 0;
    pub const VERSION: usize = 4;
    pub const STRING_LITERAL_OFFSET: usize = 8;
    pub const STRING_LITERAL_SIZE: usize = 12;
    pub const STRING_LITERAL_DATA_OFFSET: usize = 16;
    pub const STRING_LITERAL_DATA_SIZE: usize = 20;
    pub const STRING_OFFSET: usize = 24;
    pub const STRING_SIZE: usize = 28;
    pub const SIZE: usize = 32;
}

/// `HeaderV29` — the flat (offset, size) int32 pairs up to and including
/// assemblies{Offset,Size}. Layout matches `Il2CppGlobalMetadataHeader` for
/// Unity 2021.3 / metadata v29. v24 / v27 / v31 / v32-v37 differ only in
/// trailing fields and share this prefix. Every field is an int32, so the
/// byte offset of field N is `4 * N`.
///
/// Phase D-5.1 — images / assemblies follow typeDefinitions in the v24.2+
/// flat header. v24.0 / v24.1 have `rgctxEntriesOffset/Count` in between;
/// that pre-v24.2 path has no codeGenModules anyway, so the parser
/// deliberately never reads images there (EnumerateImages stays a no-op).
pub mod header_v29 {
    pub const SANITY: usize = 0;
    pub const VERSION: usize = 4;
    pub const STRING_LITERAL_OFFSET: usize = 8;
    pub const STRING_LITERAL_SIZE: usize = 12;
    pub const STRING_LITERAL_DATA_OFFSET: usize = 16;
    pub const STRING_LITERAL_DATA_SIZE: usize = 20;
    pub const STRING_OFFSET: usize = 24;
    pub const STRING_SIZE: usize = 28;
    pub const EVENTS_OFFSET: usize = 32;
    pub const EVENTS_SIZE: usize = 36;
    pub const PROPERTIES_OFFSET: usize = 40;
    pub const PROPERTIES_SIZE: usize = 44;
    pub const METHODS_OFFSET: usize = 48;
    pub const METHODS_SIZE: usize = 52;
    pub const PARAMETER_DEFAULT_VALUES_OFFSET: usize = 56;
    pub const PARAMETER_DEFAULT_VALUES_SIZE: usize = 60;
    pub const FIELD_DEFAULT_VALUES_OFFSET: usize = 64;
    pub const FIELD_DEFAULT_VALUES_SIZE: usize = 68;
    pub const FIELD_AND_PARAMETER_DEFAULT_VALUE_DATA_OFFSET: usize = 72;
    pub const FIELD_AND_PARAMETER_DEFAULT_VALUE_DATA_SIZE: usize = 76;
    pub const FIELD_MARSHALED_SIZES_OFFSET: usize = 80;
    pub const FIELD_MARSHALED_SIZES_SIZE: usize = 84;
    pub const PARAMETERS_OFFSET: usize = 88;
    pub const PARAMETERS_SIZE: usize = 92;
    pub const FIELDS_OFFSET: usize = 96;
    pub const FIELDS_SIZE: usize = 100;
    pub const GENERIC_PARAMETERS_OFFSET: usize = 104;
    pub const GENERIC_PARAMETERS_SIZE: usize = 108;
    pub const GENERIC_PARAMETER_CONSTRAINTS_OFFSET: usize = 112;
    pub const GENERIC_PARAMETER_CONSTRAINTS_SIZE: usize = 116;
    pub const GENERIC_CONTAINERS_OFFSET: usize = 120;
    pub const GENERIC_CONTAINERS_SIZE: usize = 124;
    pub const NESTED_TYPES_OFFSET: usize = 128;
    pub const NESTED_TYPES_SIZE: usize = 132;
    pub const INTERFACES_OFFSET: usize = 136;
    pub const INTERFACES_SIZE: usize = 140;
    pub const VTABLE_METHODS_OFFSET: usize = 144;
    pub const VTABLE_METHODS_SIZE: usize = 148;
    pub const INTERFACE_OFFSETS_OFFSET: usize = 152;
    pub const INTERFACE_OFFSETS_SIZE: usize = 156;
    pub const TYPE_DEFINITIONS_OFFSET: usize = 160;
    pub const TYPE_DEFINITIONS_SIZE: usize = 164;
    pub const IMAGES_OFFSET: usize = 168;
    pub const IMAGES_SIZE: usize = 172;
    pub const ASSEMBLIES_OFFSET: usize = 176;
    pub const ASSEMBLIES_SIZE: usize = 180;
    pub const SIZE: usize = 184;
}

/// `Il2CppSectionMetadata` — one v38+ header triplet (uint32 offset, size,
/// count). 12 bytes.
pub mod section {
    pub const OFFSET: usize = 0;
    pub const SIZE: usize = 4;
    pub const COUNT: usize = 8;
    pub const STRIDE: usize = 12;
}

/// `HeaderV38` — uint32 sanity + int32 version, then 31 section triplets in
/// the order upstream declares them (MetadataClass.cs lines 169-230).
/// Critical: reading a v38 header through `HEADER_V29`'s flat
/// (offset, size, offset, size, ...) shape yields silently-wrong offsets —
/// the third int32 of a v38 header is a `count`, not the expected
/// `stringLiteralDataOffset`. Constants are the byte offset of each section.
pub mod header_v38 {
    const fn sec(i: usize) -> usize {
        8 + i * super::section::STRIDE
    }
    pub const SANITY: usize = 0;
    pub const VERSION: usize = 4;
    pub const STRING_LITERALS: usize = sec(0);
    pub const STRING_LITERAL_DATA: usize = sec(1);
    pub const STRINGS: usize = sec(2);
    pub const EVENTS: usize = sec(3);
    pub const PROPERTIES: usize = sec(4);
    pub const METHODS: usize = sec(5);
    pub const PARAMETER_DEFAULT_VALUES: usize = sec(6);
    pub const FIELD_DEFAULT_VALUES: usize = sec(7);
    pub const FIELD_AND_PARAMETER_DEFAULT_VALUE_DATA: usize = sec(8);
    pub const FIELD_MARSHALED_SIZES: usize = sec(9);
    pub const PARAMETERS: usize = sec(10);
    pub const FIELDS: usize = sec(11);
    pub const GENERIC_PARAMETERS: usize = sec(12);
    pub const GENERIC_PARAMETER_CONSTRAINTS: usize = sec(13);
    pub const GENERIC_CONTAINERS: usize = sec(14);
    pub const NESTED_TYPES: usize = sec(15);
    pub const INTERFACES: usize = sec(16);
    pub const VTABLE_METHODS: usize = sec(17);
    pub const INTERFACE_OFFSETS: usize = sec(18);
    pub const TYPE_DEFINITIONS: usize = sec(19);
    pub const IMAGES: usize = sec(20);
    pub const ASSEMBLIES: usize = sec(21);
    pub const FIELD_REFS: usize = sec(22);
    pub const REFERENCED_ASSEMBLIES: usize = sec(23);
    pub const ATTRIBUTE_DATA: usize = sec(24);
    pub const ATTRIBUTE_DATA_RANGES: usize = sec(25);
    pub const UNRESOLVED_INDIRECT_CALL_PARAMETER_TYPES: usize = sec(26);
    pub const UNRESOLVED_INDIRECT_CALL_PARAMETER_RANGES: usize = sec(27);
    pub const WINDOWS_RUNTIME_TYPE_NAMES: usize = sec(28);
    pub const WINDOWS_RUNTIME_STRINGS: usize = sec(29);
    pub const EXPORTED_TYPE_DEFINITIONS: usize = sec(30);
    /// 8 (sanity + version) + 31 * 12 (sections) = 380 bytes.
    pub const SIZE: usize = sec(31);
}

// ---------------------------------------------------------------------------
// String literals
// ---------------------------------------------------------------------------

/// `StringLiteralEntry` (v24-v34): uint32 length (bytes) + int32 dataIndex
/// (offset into the stringLiteralData blob). 8 bytes.
pub mod string_literal {
    pub const LENGTH: usize = 0;
    pub const DATA_INDEX: usize = 4;
    pub const SIZE: usize = 8;
}

/// `StringLiteralEntry_v35` (v35+): drops the `length` prefix; entries hold
/// just the int32 dataIndex. Length is recovered from the delta to the next
/// entry's dataIndex (and to stringLiteralDataSize for the last entry).
/// Verified against MetadataClass.cs:474-479. 4 bytes.
pub mod string_literal_v35 {
    pub const DATA_INDEX: usize = 0;
    pub const SIZE: usize = 4;
}

// ---------------------------------------------------------------------------
// TypeDefinition
// ---------------------------------------------------------------------------

/// Byte offsets of the TypeDefinition fields the parser projects, for one
/// fixed-stride (pre-v38) layout.
#[derive(Clone, Copy, Debug)]
pub struct TypeDefLayout {
    pub size: usize,
    pub name_index: usize,
    pub namespace_index: usize,
    pub declaring_type_index: usize,
    pub flags: usize,
    pub field_start: usize,
    pub method_start: usize,
    pub method_count: usize,
    pub field_count: usize,
    pub bitfield: usize,
    pub token: usize,
}

/// `Il2CppTypeDefinition` for v29 (matches Unity 2021.3 source).
/// 16 int32 + 8 uint16 + 2 uint32 = 88 bytes:
///
/// ```text
///  0 nameIndex          4 namespaceIndex     8 byvalTypeIndex
/// 12 declaringTypeIndex 16 parentIndex       20 elementTypeIndex
/// 24 genericContainerIndex                   28 flags
/// 32 fieldStart  36 methodStart  40 eventStart  44 propertyStart
/// 48 nestedTypesStart  52 interfacesStart  56 vtableStart  60 interfaceOffsetsStart
/// 64 method_count  66 property_count  68 field_count  70 event_count
/// 72 nested_type_count  74 vtable_count  76 interfaces_count  78 interface_offsets_count
/// 80 bitfield  84 token
/// ```
///
/// v31 (Unity 2022.2+) is empirically identical to v29 (88-byte stride, IL
/// token at offset 84) — verified by hex-dumping Farthest Frontier +
/// Emberward typeDefinitions tables and locating the canonical TypeDef token
/// 0x02000003 at row 2. v31's version bump is about header / images /
/// assemblies trailing changes, not the TypeDefinition row. v27 and v32-v34
/// reuse it too (no struct-level changes per upstream Il2CppDumperV35plus).
pub const TYPE_DEFINITION_V29: TypeDefLayout = TypeDefLayout {
    size: 88,
    name_index: 0,
    namespace_index: 4,
    declaring_type_index: 12,
    flags: 28,
    field_start: 32,
    method_start: 36,
    method_count: 64,
    field_count: 68,
    bitfield: 80,
    token: 84,
};

/// v24 has FOUR sub-version layouts per upstream Il2CppDumper
/// (vendor/Il2CppDumper/Il2CppDumper/Il2Cpp/MetadataClass.cs:166) — Unity
/// dropped fields from `Il2CppTypeDefinition` over the 2018.x release cycle
/// without bumping the metadata version number:
///
/// * v24.0 (Unity ~2017-2018.1, e.g. R-Type Dimensions 2018.1.9f2): has
///   `customAttributeIndex` + `byrefTypeIndex` + `rgctxStartIndex` +
///   `rgctxCount`. 104-byte stride.
/// * v24.1 (Unity ~2018.2): drops `customAttributeIndex`. 100-byte stride.
/// * v24.2 / v24.3 / v24.4 (Unity ~2018.3-2018.4): drops `rgctxStartIndex` +
///   `rgctxCount`. 92-byte stride.
/// * v24.5 (Unity 2019.x — SaGa Frontier Remastered / FFVI PR): same fields
///   as v24.2-24.4. 92-byte stride. Final v24 layout before v27 drops
///   `byrefTypeIndex` too.
///
/// This one is v24.2-v24.5 (the most common shape in the wild): only
/// `byrefTypeIndex` from the v24 deprecated-fields set survives, at offset
/// 12. 17 int32 + 8 uint16 + 2 uint32 = 92 bytes. `Parser::detect_v24_variant`
/// probes which stride lands TypeDef[0].token at 0x02000001.
pub const TYPE_DEFINITION_V24: TypeDefLayout = TypeDefLayout {
    size: 92,
    name_index: 0,
    namespace_index: 4,
    // 8 byvalTypeIndex, 12 byrefTypeIndex
    declaring_type_index: 16,
    // 20 parentIndex, 24 elementTypeIndex, 28 genericContainerIndex
    flags: 32,
    field_start: 36,
    method_start: 40,
    // 44..68 eventStart..interfaceOffsetsStart
    method_count: 68,
    field_count: 72,
    bitfield: 84,
    token: 88,
};

/// v24.1 (Unity ~2018.2): adds `rgctxStartIndex` + `rgctxCount` to the v24.5
/// layout, placed between `elementTypeIndex` and `genericContainerIndex`.
/// 19 int32 + 8 uint16 + 2 uint32 = 100 bytes.
pub const TYPE_DEFINITION_V24_1: TypeDefLayout = TypeDefLayout {
    size: 100,
    name_index: 0,
    namespace_index: 4,
    // 8 byvalTypeIndex, 12 byrefTypeIndex
    declaring_type_index: 16,
    // 20 parentIndex, 24 elementTypeIndex, 28 rgctxStartIndex, 32 rgctxCount,
    // 36 genericContainerIndex
    flags: 40,
    field_start: 44,
    method_start: 48,
    method_count: 76,
    field_count: 80,
    bitfield: 92,
    token: 96,
};

/// v24.0 (Unity ~2017-2018.1, e.g. R-Type Dimensions 2018.1.9f2): adds
/// `customAttributeIndex` after `namespaceIndex` to the v24.1 layout.
/// 20 int32 + 8 uint16 + 2 uint32 = 104 bytes.
pub const TYPE_DEFINITION_V24_0: TypeDefLayout = TypeDefLayout {
    size: 104,
    name_index: 0,
    namespace_index: 4,
    // 8 customAttributeIndex, 12 byvalTypeIndex, 16 byrefTypeIndex
    declaring_type_index: 20,
    // 24 parentIndex, 28 elementTypeIndex, 32 rgctxStartIndex, 36 rgctxCount,
    // 40 genericContainerIndex
    flags: 44,
    field_start: 48,
    method_start: 52,
    method_count: 80,
    field_count: 84,
    bitfield: 96,
    token: 100,
};

/// v35 drops `Il2CppTypeDefinition.elementTypeIndex` — 84-byte stride instead
/// of v29's 88. The field was only consulted for enum decoding (resolved
/// from parentIndex in v35+ per upstream `GetEnumElementTypeIndex`). All
/// other fields keep their relative order: nameIndex/namespaceIndex stay at
/// offset 0/4; byvalTypeIndex/declaringTypeIndex/parentIndex/
/// genericContainerIndex are still 4-byte int32 indices in v35-v37
/// (variable-width starts at v38), the 8 int "Start" fields and 8 ushort
/// counts follow, then bitfield + token at the tail. Verified against
/// vendor/Il2CppDumperV35plus/Il2CppDumper/Il2Cpp/MetadataClass.cs:290-378.
/// v36 / v37 reuse this layout per upstream `[Version(Min=…, Max=35)]`.
pub const TYPE_DEFINITION_V35: TypeDefLayout = TypeDefLayout {
    size: 84,
    name_index: 0,
    namespace_index: 4,
    // 8 byvalTypeIndex
    declaring_type_index: 12,
    // 16 parentIndex, 20 genericContainerIndex
    flags: 24,
    field_start: 28,
    method_start: 32,
    method_count: 60,
    field_count: 64,
    bitfield: 76,
    token: 80,
};

/// v38+ TypeDefinition: hand-walked because the stride depends on the
/// runtime-variable index sizes. Layout per
/// vendor/Il2CppDumperV35plus/Il2Cpp/MetadataClass.cs:290-378 (with
/// elementTypeIndex removed per v35+):
///
/// ```text
/// uint   nameIndex                     4
/// uint   namespaceIndex                4
/// TI     byvalTypeIndex                typeIndexSize
/// TI     declaringTypeIndex            typeIndexSize
/// TI     parentIndex                   typeIndexSize
/// GCI    genericContainerIndex         genericContainerIndexSize
/// uint   flags                         4
/// int    fieldStart .. interfaceOffsetsStart   8 x 4
/// ushort method_count .. interface_offsets_count 8 x 2
/// uint   bitfield                      4
/// uint   token                         4
/// ```
///
/// Offsets below are relative to the end of the variable block
/// (`8 + 3 * TIS + GCIS`).
pub mod type_definition_v38 {
    /// nameIndex + namespaceIndex.
    pub const FIXED_PREFIX: usize = 8;
    /// Everything except the 3 TypeIndex + 1 GenericContainerIndex fields:
    /// 2 uint nameIndex/namespaceIndex (8) + uint flags (4) + 8 int *Start
    /// (32) + 8 ushort *_count (16) + uint bitfield (4) + uint token (4) = 68.
    pub const FIXED_BYTES: usize = 68;
    pub const FLAGS: usize = 0;
    pub const FIELD_START: usize = 4;
    pub const METHOD_START: usize = 8;
    /// 8 *Start ints (32 bytes total) span [+4 .. +36].
    pub const METHOD_COUNT: usize = 36;
    pub const FIELD_COUNT: usize = 40;
    /// 8 *_count ushorts (16 bytes total) span [+36 .. +52].
    pub const BITFIELD: usize = 52;
    pub const TOKEN: usize = 56;
}

/// Offsets of `nestedTypesStart` / `nested_type_count` measured back from the
/// END of a TypeDefinition row. Valid for every layout (fixed and v38+):
/// the tail is always 8 int *Start (32) + 8 ushort *_count (16) + uint
/// bitfield (4) + uint token (4) = 56 bytes. nested_type_count is the 5th
/// ushort (index 4): row_end - 8 - 16 + 4*2 = row_end - 16.
/// nestedTypesStart is the 5th *Start int (index 4): row_end - 56 + 4*4 =
/// row_end - 40.
pub mod type_definition_tail {
    pub const TAIL_BYTES: usize = 56;
    pub const NESTED_TYPES_START_FROM_END: usize = 40;
    pub const NESTED_TYPE_COUNT_FROM_END: usize = 16;
}

// ---------------------------------------------------------------------------
// FieldDefinition / FieldDefaultValue
// ---------------------------------------------------------------------------

/// `FieldDefinition_v29` (v24.1 through v37): int32 nameIndex (-> string
/// heap) + int32 typeIndex (-> types[]) + uint32 token. 12 bytes.
/// FieldDefinition + FieldDefaultValue layouts haven't changed across v24.1
/// through v37.
pub mod field_definition_v29 {
    pub const NAME_INDEX: usize = 0;
    pub const TYPE_INDEX: usize = 4;
    pub const TOKEN: usize = 8;
    pub const SIZE: usize = 12;
}

/// FieldDefinition also gained/lost a `customAttributeIndex` over the v24
/// cycle. `[Version(Max = 24)]` per upstream — present at v24.0 only, gone
/// by v24.1. Token slips from offset +8 to +12. 16 bytes.
pub mod field_definition_v24_0 {
    pub const NAME_INDEX: usize = 0;
    pub const TYPE_INDEX: usize = 4;
    pub const CUSTOM_ATTRIBUTE_INDEX: usize = 8;
    pub const TOKEN: usize = 12;
    pub const SIZE: usize = 16;
}

/// v38+ FieldDefinition: int32 nameIndex + TIS typeIndex + uint32 token
/// (stride 8 + TIS). Offsets after `nameIndex` are computed at runtime.
pub mod field_definition_v38 {
    pub const NAME_INDEX: usize = 0;
    pub const TYPE_INDEX: usize = 4;
    pub const FIXED_BYTES: usize = 8;
}

/// `FieldDefaultValue_v29`: encodes the constant value for a single field.
/// dataIndex points into fieldAndParameterDefaultValueData; -1 = no data.
/// The blob's interpretation requires the typeIndex. 12 bytes. v38+ shrinks
/// typeIndex to TIS bytes (stride 8 + TIS; fieldIndex stays int32).
pub mod field_default_value_v29 {
    pub const FIELD_INDEX: usize = 0;
    pub const TYPE_INDEX: usize = 4;
    pub const DATA_INDEX: usize = 8;
    pub const SIZE: usize = 12;
}

// ---------------------------------------------------------------------------
// MethodDefinition
// ---------------------------------------------------------------------------

/// MethodDefinition — phase B (Il2CppDumper feature parity). Layout per
/// vendor/Il2CppDumperV35plus/Il2CppDumper/Il2Cpp/MetadataClass.cs:381-407.
/// Same v24 sub-version field-drop story as TypeDefinition:
///
/// * v24.0: 56 bytes (customAttributeIndex + 5 v24.1 fields)
/// * v24.1: 52 bytes (drops customAttributeIndex)
/// * v24.2-v24.5, v27, v29: 32 bytes (drops all 5 v24.1 fields)
/// * v31, v32-v37: 36 bytes (adds returnParameterToken at offset 12)
/// * v38+: hand-walked (typed indices go variable-width per IndexSizes)
#[derive(Clone, Copy, Debug)]
pub struct MethodDefLayout {
    pub size: usize,
    pub name_index: usize,
    pub declaring_type: usize,
    pub return_type: usize,
    pub parameter_start: usize,
    pub token: usize,
    pub flags: usize,
    pub parameter_count: usize,
}

/// v29 / v27 / v24.2-v24.5: the canonical "modern" 32-byte layout.
/// uint nameIndex, int declaringType, int returnType, int parameterStart,
/// int genericContainerIndex, uint token, ushort flags / iflags / slot /
/// parameterCount. 6 int32/uint32 + 4 uint16 = 32 bytes.
pub const METHOD_DEFINITION_V29: MethodDefLayout = MethodDefLayout {
    size: 32,
    name_index: 0,
    declaring_type: 4,
    return_type: 8,
    parameter_start: 12,
    // 16 genericContainerIndex
    token: 20,
    flags: 24,
    // 26 iflags, 28 slot
    parameter_count: 30,
};

/// v31-v37: adds `returnParameterToken` after `returnType`. 7 int32/uint32 +
/// 4 uint16 = 36 bytes. v32-v34 reuse v31; v35-v37 same as v31 (typed-index
/// changes for v35+ don't affect MethodDefinition pre-v38).
pub const METHOD_DEFINITION_V31: MethodDefLayout = MethodDefLayout {
    size: 36,
    name_index: 0,
    declaring_type: 4,
    return_type: 8,
    // 12 returnParameterToken
    parameter_start: 16,
    // 20 genericContainerIndex
    token: 24,
    flags: 28,
    parameter_count: 34,
};

/// v24.1 (Unity ~2018.2): the v29 layout plus the five deprecated indices
/// (methodIndex, invokerIndex, delegateWrapperIndex, rgctxStartIndex,
/// rgctxCount) inserted between `genericContainerIndex` and `token`.
/// 11 int32/uint32 + 4 uint16 = 52 bytes.
pub const METHOD_DEFINITION_V24_1: MethodDefLayout = MethodDefLayout {
    size: 52,
    name_index: 0,
    declaring_type: 4,
    return_type: 8,
    parameter_start: 12,
    // 16 genericContainerIndex, 20..40 the five <=v24.1 indices
    token: 40,
    flags: 44,
    parameter_count: 50,
};

/// v24.0 (Unity ~2017-2018.1, e.g. R-Type Dimensions): the v24.1 layout plus
/// `customAttributeIndex` inserted after `parameterStart`.
/// 12 int32/uint32 + 4 uint16 = 56 bytes.
pub const METHOD_DEFINITION_V24_0: MethodDefLayout = MethodDefLayout {
    size: 56,
    name_index: 0,
    declaring_type: 4,
    return_type: 8,
    parameter_start: 12,
    // 16 customAttributeIndex, 20 genericContainerIndex, 24..44 the five
    // <=v24.1 indices
    token: 44,
    flags: 48,
    parameter_count: 54,
};

/// v38+ MethodDefinition (hand-walked; typed-index version):
///
/// ```text
/// uint  nameIndex                4
/// TDI   declaringType            TDIS
/// TI    returnType               TIS
/// int   returnParameterToken     4   (>=v31; always present at v38+)
/// PI    parameterStart           PIS (= 4 at v38, variable at v39+)
/// GCI   genericContainerIndex    GCIS
/// uint  token                    4
/// ushort flags / iflags / slot / parameterCount   4 x 2
/// ```
///
/// v38 stride = 24 + TDIS + TIS + GCIS; v39 stride = 20 + TDIS + TIS + PIS +
/// GCIS.
pub mod method_definition_v38 {
    pub const V38_FIXED_BYTES: usize = 24;
    pub const V39_FIXED_BYTES: usize = 20;
    /// ParameterIndex width before v39.
    pub const V38_PARAMETER_INDEX_SIZE: usize = 4;
}

// ---------------------------------------------------------------------------
// ImageDefinition
// ---------------------------------------------------------------------------

/// `Il2CppImageDefinition` — assembly-file (DLL/EXE) descriptor in metadata.
/// Phase D-5 needs this to map a global method index to its owning image, so
/// the cross-link to `codeGenModules[image_name].methodPointers[]` can happen
/// offline. Layout cross-checked against
/// vendor/Il2CppDumperV35plus/Il2Cpp/MetadataClass.cs:267-288. v24.0 / v24.1
/// variants differ (never read — see the parser); this layout covers v24.2
/// through v37. 10 int32/uint32 = 40 bytes:
///
/// ```text
///  0 nameIndex  4 assemblyIndex  8 typeStart  12 typeCount
/// 16 exportedTypeStart  20 exportedTypeCount  24 entryPointIndex
/// 28 token  32 customAttributeStart  36 customAttributeCount
/// ```
pub mod image_definition_v29 {
    pub const NAME_INDEX: usize = 0;
    pub const TYPE_START: usize = 8;
    pub const TYPE_COUNT: usize = 12;
    pub const TOKEN: usize = 28;
    pub const SIZE: usize = 40;
}

/// v38+ ImageDefinition: typeStart / exportedTypeStart shrink to
/// variable-width TypeDefinitionIndex (TDIS):
/// uint(4) + int(4) + TDIS + uint(4) + TDIS + uint(4) + int(4) + uint(4) +
/// int(4) + uint(4) = 32 + 2 * TDIS.
pub mod image_definition_v38 {
    pub const NAME_INDEX: usize = 0;
    pub const TYPE_START: usize = 8;
    pub const FIXED_BYTES: usize = 32;
}

// ---------------------------------------------------------------------------
// ParameterDefinition
// ---------------------------------------------------------------------------

/// ParameterDefinition — same v24.0-only customAttributeIndex story.
/// * v24.0: 16 bytes (with customAttributeIndex)
/// * v24.1 through v37: 12 bytes (uint nameIndex + uint token + int typeIndex)
/// * v38+: 8 + typeIndexSize (typeIndex becomes variable-width)
pub mod parameter_definition_v29 {
    pub const NAME_INDEX: usize = 0;
    pub const TOKEN: usize = 4;
    pub const TYPE_INDEX: usize = 8;
    pub const SIZE: usize = 12;
}

pub mod parameter_definition_v24_0 {
    pub const NAME_INDEX: usize = 0;
    pub const TOKEN: usize = 4;
    pub const CUSTOM_ATTRIBUTE_INDEX: usize = 8;
    pub const TYPE_INDEX: usize = 12;
    pub const SIZE: usize = 16;
}

pub mod parameter_definition_v38 {
    pub const NAME_INDEX: usize = 0;
    pub const TOKEN: usize = 4;
    pub const TYPE_INDEX: usize = 8;
    pub const FIXED_BYTES: usize = 8;
}

// ---------------------------------------------------------------------------
// Variable-width index sizes
// ---------------------------------------------------------------------------

/// Variable-width metadata-index sizes inferred at load time. Pre-v38 these
/// are all 4 (int32 always); v38+ shrinks them to 1/2/4 bytes per field based
/// on each table's row count (small tables get small indices).
///
/// Width derivation (from vendor/Il2CppDumperV35plus/Il2Cpp/Metadata.cs:101-114):
///
/// ```text
/// typeDefinitionIndexSize   = GetIndexSize(typeDefinitions.count)
/// genericContainerIndexSize = GetIndexSize(genericContainers.count)
/// parameterIndexSize        = GetIndexSize(parameters.count)
/// typeIndexSize             = sizeof(ParameterDefinition_row) - 8
///                             (Il2CppParameterDefinition has uint nameIndex +
///                              uint token + TypeIndex typeIndex = 8 + TIS)
/// ```
///
/// v39 just adds ParamIndex to this family — no other structural changes
/// per Perfare/Il2CppDumper#903.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct IndexSizes {
    pub type_index_size: i32,
    pub type_definition_index_size: i32,
    pub generic_container_index_size: i32,
    pub parameter_index_size: i32,
}

impl Default for IndexSizes {
    fn default() -> Self {
        Self {
            type_index_size: 4,
            type_definition_index_size: 4,
            generic_container_index_size: 4,
            parameter_index_size: 4,
        }
    }
}

/// `GetIndexSize(n) = n <= 0xFF ? 1 : n <= 0xFFFF ? 2 : 4`.
pub const fn get_index_size(count: i64) -> i32 {
    if count <= 0xFF {
        1
    } else if count <= 0xFFFF {
        2
    } else {
        4
    }
}

// Mirrors of the C++ static_asserts — every stride must match exactly.
const _: () = {
    assert!(header_prefix::SIZE == 32);
    assert!(string_literal::SIZE == 8);
    assert!(string_literal_v35::SIZE == 4);
    assert!(header_v29::SIZE == 184);
    assert!(TYPE_DEFINITION_V29.size == 88);
    assert!(TYPE_DEFINITION_V24.size == 92);
    assert!(TYPE_DEFINITION_V24_1.size == 100);
    assert!(TYPE_DEFINITION_V24_0.size == 104);
    assert!(TYPE_DEFINITION_V35.size == 84);
    assert!(field_definition_v24_0::SIZE == 16);
    assert!(METHOD_DEFINITION_V29.size == 32);
    assert!(METHOD_DEFINITION_V31.size == 36);
    assert!(METHOD_DEFINITION_V24_1.size == 52);
    assert!(METHOD_DEFINITION_V24_0.size == 56);
    assert!(parameter_definition_v29::SIZE == 12);
    assert!(parameter_definition_v24_0::SIZE == 16);
    assert!(image_definition_v29::SIZE == 40);
    assert!(field_definition_v29::SIZE == 12);
    assert!(field_default_value_v29::SIZE == 12);
    assert!(section::STRIDE == 12);
    assert!(header_v38::SIZE == 8 + 31 * 12);
};

// Every fixed TypeDefinition layout shares the same 60-byte tail (flags +
// 8 *Start + 8 *_count + bitfield + token); check the per-layout offsets
// against it so a typo in one table cannot slip through.
const fn tail_ok(l: TypeDefLayout) -> bool {
    l.flags == l.size - 60
        && l.field_start == l.size - 56
        && l.method_start == l.size - 52
        && l.method_count == l.size - 24
        && l.field_count == l.size - 20
        && l.bitfield == l.size - 8
        && l.token == l.size - 4
}
const fn method_tail_ok(l: MethodDefLayout) -> bool {
    l.token == l.size - 12 && l.flags == l.size - 8 && l.parameter_count == l.size - 2
}
const _: () = {
    assert!(tail_ok(TYPE_DEFINITION_V29));
    assert!(tail_ok(TYPE_DEFINITION_V24));
    assert!(tail_ok(TYPE_DEFINITION_V24_1));
    assert!(tail_ok(TYPE_DEFINITION_V24_0));
    assert!(tail_ok(TYPE_DEFINITION_V35));
    assert!(type_definition_v38::FIXED_BYTES == type_definition_v38::FIXED_PREFIX + 60);
    assert!(type_definition_v38::TOKEN + 4 == 60);
    assert!(method_tail_ok(METHOD_DEFINITION_V29));
    assert!(method_tail_ok(METHOD_DEFINITION_V31));
    assert!(method_tail_ok(METHOD_DEFINITION_V24_1));
    assert!(method_tail_ok(METHOD_DEFINITION_V24_0));
};
