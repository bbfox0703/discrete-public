// MetadataLayout.h — on-disk layout for global-metadata.dat.
//
// global-metadata.dat is purely a tagged-offset blob — every reference is a
// 32-bit byte offset into the file, not a runtime pointer. So the file layout
// itself is bitness-neutral. The bitness concern (per Phase 5 Resolved
// Decisions) only kicks in when we cross-link these tables to live runtime
// pointers like Il2CppClass*; that wrapper layer lives elsewhere.
//
// Phase 14c covers v24 (Unity 2018.4-2019.x, e.g. SaGa Frontier Remastered),
// v29 (Unity 2021.x-2022.1 AND Unity 6 / 6000.0.x — Unity 6 reverted to v29,
// e.g. SaGa Frontier 2 Remastered), and v31 (Unity 2022.2-2023.x, e.g.
// Emberward / Farthest Frontier). v27 (Unity 2020) is deferred — no test
// target locally and the format shares the v29 layout per upstream sources.
// The HEADER prefix up to typeDefinitionsOffset is the same across
// v24/v27/v29/v31 — we read just that span via HeaderV29 and dispatch the
// trailing TypeDefinition layout per version.
//
// v32+ (Unity 2023+ / Unity 6.1+) coverage added below. v32-v37 keep the
// flat header layout but v35 drops two struct fields:
//   • Il2CppTypeDefinition.elementTypeIndex (was at offset 20 in v29) —
//     stride goes 88 → 84 bytes.
//   • Il2CppStringLiteral.length — entry size 8 → 4 bytes; length is now
//     recovered from the delta to the next entry's dataIndex.
// v38 rewrites the header into Il2CppSectionMetadata{offset,size,count}
// triplets per table (12 bytes each, ~30 sections) AND introduces
// variable-width "metadata indices" — TypeIndex / TypeDefinitionIndex /
// GenericContainerIndex / ParameterIndex (v39) shrink to 1, 2, or 4 bytes
// per field based on each table's row count. v39 is incremental: only
// ParamIndex joins the variable-width family (per Perfare/Il2CppDumper#903).
//
// All layouts cross-checked against Unity's official IL2CPP source in
// vendor/libil2cpp/ + the Il2CppDumperV35plus community fork (pinned at
// bbd9c0c2 in vendor/Il2CppDumperV35plus/) — see
// docs/il2cpp-metadata-crosscheck.md for the field-by-field comparison and
// source-line citations.
#pragma once

#include <cstdint>

namespace UnityCEDumper::IL2CPP::Metadata {

inline constexpr std::uint32_t Sanity = 0xFAB11BAFu;

// The header begins identically across v24..v31 in the prefix that we care
// about for string literal / string heap access. We read the leading fields
// only and assert the version separately.
#pragma pack(push, 4)
struct HeaderPrefix {
    std::int32_t sanity;
    std::int32_t version;
    std::int32_t stringLiteralOffset;
    std::int32_t stringLiteralSize;
    std::int32_t stringLiteralDataOffset;
    std::int32_t stringLiteralDataSize;
    std::int32_t stringOffset;
    std::int32_t stringSize;
};

struct StringLiteralEntry {
    std::uint32_t length;       // bytes, UTF-8
    std::int32_t  dataIndex;    // offset into stringLiteralData blob
};

// HeaderV29 covers the leading fields up to typeDefinitions{Offset,Size},
// which is everything segment 2 needs. Layout matches Il2CppGlobalMetadata
// Header for Unity 2021.3 / metadata v29. v27 / v31 differ in trailing
// fields but share this prefix.
struct HeaderV29 {
    std::int32_t sanity;
    std::int32_t version;
    std::int32_t stringLiteralOffset;
    std::int32_t stringLiteralSize;
    std::int32_t stringLiteralDataOffset;
    std::int32_t stringLiteralDataSize;
    std::int32_t stringOffset;
    std::int32_t stringSize;
    std::int32_t eventsOffset;
    std::int32_t eventsSize;
    std::int32_t propertiesOffset;
    std::int32_t propertiesSize;
    std::int32_t methodsOffset;
    std::int32_t methodsSize;
    std::int32_t parameterDefaultValuesOffset;
    std::int32_t parameterDefaultValuesSize;
    std::int32_t fieldDefaultValuesOffset;
    std::int32_t fieldDefaultValuesSize;
    std::int32_t fieldAndParameterDefaultValueDataOffset;
    std::int32_t fieldAndParameterDefaultValueDataSize;
    std::int32_t fieldMarshaledSizesOffset;
    std::int32_t fieldMarshaledSizesSize;
    std::int32_t parametersOffset;
    std::int32_t parametersSize;
    std::int32_t fieldsOffset;
    std::int32_t fieldsSize;
    std::int32_t genericParametersOffset;
    std::int32_t genericParametersSize;
    std::int32_t genericParameterConstraintsOffset;
    std::int32_t genericParameterConstraintsSize;
    std::int32_t genericContainersOffset;
    std::int32_t genericContainersSize;
    std::int32_t nestedTypesOffset;
    std::int32_t nestedTypesSize;
    std::int32_t interfacesOffset;
    std::int32_t interfacesSize;
    std::int32_t vtableMethodsOffset;
    std::int32_t vtableMethodsSize;
    std::int32_t interfaceOffsetsOffset;
    std::int32_t interfaceOffsetsSize;
    std::int32_t typeDefinitionsOffset;
    std::int32_t typeDefinitionsSize;
};

// Il2CppTypeDefinition for v29 (matches Unity 2021.3 source).
// 16 int32 + 8 uint16 + 2 uint32 = 88 bytes.
struct TypeDefinition_v29 {
    std::int32_t  nameIndex;             // -> string heap
    std::int32_t  namespaceIndex;        // -> string heap
    std::int32_t  byvalTypeIndex;
    std::int32_t  declaringTypeIndex;
    std::int32_t  parentIndex;
    std::int32_t  elementTypeIndex;
    std::int32_t  genericContainerIndex;
    std::uint32_t flags;
    std::int32_t  fieldStart;            // -> fields[]
    std::int32_t  methodStart;           // -> methods[]
    std::int32_t  eventStart;
    std::int32_t  propertyStart;
    std::int32_t  nestedTypesStart;
    std::int32_t  interfacesStart;
    std::int32_t  vtableStart;
    std::int32_t  interfaceOffsetsStart;
    std::uint16_t method_count;
    std::uint16_t property_count;
    std::uint16_t field_count;
    std::uint16_t event_count;
    std::uint16_t nested_type_count;
    std::uint16_t vtable_count;
    std::uint16_t interfaces_count;
    std::uint16_t interface_offsets_count;
    std::uint32_t bitfield;
    std::uint32_t token;
};

struct FieldDefinition_v29 {
    std::int32_t  nameIndex;   // -> string heap
    std::int32_t  typeIndex;   // -> types[] (parsed in segment 2c)
    std::uint32_t token;
};

// FieldDefault encodes the constant value for a single field. dataIndex
// points into fieldAndParameterDefaultValueData. The blob's interpretation
// requires the typeIndex (decoded in segment 2c).
struct FieldDefaultValue_v29 {
    std::int32_t fieldIndex;   // global index into fields[]
    std::int32_t typeIndex;    // -> types[]
    std::int32_t dataIndex;    // -> fieldAndParameterDefaultValueData blob; -1 = no data
};

// v24 has FOUR sub-version layouts per upstream Il2CppDumper
// (vendor/Il2CppDumper/Il2CppDumper/Il2Cpp/MetadataClass.cs:166) —
// Unity dropped fields from `Il2CppTypeDefinition` over the 2018.x
// release cycle without bumping the metadata version number:
//
//   v24.0 (Unity ~2017-2018.1, e.g. R-Type Dimensions 2018.1.9f2):
//     has `customAttributeIndex` + `byrefTypeIndex` +
//     `rgctxStartIndex` + `rgctxCount`. 104-byte stride.
//   v24.1 (Unity ~2018.2):
//     drops `customAttributeIndex`. 100-byte stride.
//   v24.2 / v24.3 / v24.4 (Unity ~2018.3-2018.4):
//     drops `rgctxStartIndex` + `rgctxCount`. 92-byte stride.
//   v24.5 (Unity 2019.x — SaGa Frontier Remastered / FFVI PR):
//     same fields as v24.2-24.4. 92-byte stride. Final v24 layout
//     before v27 drops `byrefTypeIndex` too.
//
// `TypeDefinition_v24` below is v24.2-v24.5 (92-byte stride) — the
// most common shape in the wild and the one our test corpus validated
// against. The variants for older Unity 2018 builds follow below as
// `TypeDefinition_v24_0` (104 bytes) and `TypeDefinition_v24_1`
// (100 bytes); `Parser::DetectV24Variant()` probes which one matches
// by checking which stride lands TypeDef[0].token at 0x02000001.

// v24.2-v24.5 (Unity ~2018.3-2019.x): only `byrefTypeIndex` from the
// v24 deprecated-fields set survives. 17 int32 + 8 uint16 + 2 uint32 = 92 bytes.
struct TypeDefinition_v24 {
    std::int32_t  nameIndex;
    std::int32_t  namespaceIndex;
    std::int32_t  byvalTypeIndex;
    std::int32_t  byrefTypeIndex;        // present in v24, absent in v29
    std::int32_t  declaringTypeIndex;
    std::int32_t  parentIndex;
    std::int32_t  elementTypeIndex;
    std::int32_t  genericContainerIndex;
    std::uint32_t flags;
    std::int32_t  fieldStart;
    std::int32_t  methodStart;
    std::int32_t  eventStart;
    std::int32_t  propertyStart;
    std::int32_t  nestedTypesStart;
    std::int32_t  interfacesStart;
    std::int32_t  vtableStart;
    std::int32_t  interfaceOffsetsStart;
    std::uint16_t method_count;
    std::uint16_t property_count;
    std::uint16_t field_count;
    std::uint16_t event_count;
    std::uint16_t nested_type_count;
    std::uint16_t vtable_count;
    std::uint16_t interfaces_count;
    std::uint16_t interface_offsets_count;
    std::uint32_t bitfield;
    std::uint32_t token;
};

// v24.1 (Unity ~2018.2): adds `rgctxStartIndex` + `rgctxCount` to the v24.5
// layout, placed between `elementTypeIndex` and `genericContainerIndex`.
// 19 int32 + 8 uint16 + 2 uint32 = 100 bytes.
struct TypeDefinition_v24_1 {
    std::int32_t  nameIndex;
    std::int32_t  namespaceIndex;
    std::int32_t  byvalTypeIndex;
    std::int32_t  byrefTypeIndex;
    std::int32_t  declaringTypeIndex;
    std::int32_t  parentIndex;
    std::int32_t  elementTypeIndex;
    std::int32_t  rgctxStartIndex;       // present only ≤v24.1
    std::int32_t  rgctxCount;            // present only ≤v24.1
    std::int32_t  genericContainerIndex;
    std::uint32_t flags;
    std::int32_t  fieldStart;
    std::int32_t  methodStart;
    std::int32_t  eventStart;
    std::int32_t  propertyStart;
    std::int32_t  nestedTypesStart;
    std::int32_t  interfacesStart;
    std::int32_t  vtableStart;
    std::int32_t  interfaceOffsetsStart;
    std::uint16_t method_count;
    std::uint16_t property_count;
    std::uint16_t field_count;
    std::uint16_t event_count;
    std::uint16_t nested_type_count;
    std::uint16_t vtable_count;
    std::uint16_t interfaces_count;
    std::uint16_t interface_offsets_count;
    std::uint32_t bitfield;
    std::uint32_t token;
};

// v24.0 (Unity ~2017-2018.1, e.g. R-Type Dimensions 2018.1.9f2): adds
// `customAttributeIndex` after `namespaceIndex` to the v24.1 layout.
// 20 int32 + 8 uint16 + 2 uint32 = 104 bytes.
struct TypeDefinition_v24_0 {
    std::int32_t  nameIndex;
    std::int32_t  namespaceIndex;
    std::int32_t  customAttributeIndex;  // present only ≤v24.0
    std::int32_t  byvalTypeIndex;
    std::int32_t  byrefTypeIndex;
    std::int32_t  declaringTypeIndex;
    std::int32_t  parentIndex;
    std::int32_t  elementTypeIndex;
    std::int32_t  rgctxStartIndex;
    std::int32_t  rgctxCount;
    std::int32_t  genericContainerIndex;
    std::uint32_t flags;
    std::int32_t  fieldStart;
    std::int32_t  methodStart;
    std::int32_t  eventStart;
    std::int32_t  propertyStart;
    std::int32_t  nestedTypesStart;
    std::int32_t  interfacesStart;
    std::int32_t  vtableStart;
    std::int32_t  interfaceOffsetsStart;
    std::uint16_t method_count;
    std::uint16_t property_count;
    std::uint16_t field_count;
    std::uint16_t event_count;
    std::uint16_t nested_type_count;
    std::uint16_t vtable_count;
    std::uint16_t interfaces_count;
    std::uint16_t interface_offsets_count;
    std::uint32_t bitfield;
    std::uint32_t token;
};

// FieldDefinition also gained/lost a `customAttributeIndex` over the v24
// cycle. `[Version(Max = 24)]` per upstream — present at v24.0 only,
// gone by v24.1. v24.1+ matches the v29 12-byte layout.
struct FieldDefinition_v24_0 {
    std::int32_t  nameIndex;
    std::int32_t  typeIndex;
    std::int32_t  customAttributeIndex;  // present only ≤v24.0
    std::uint32_t token;
};

// v31 (Unity 2022.2+) TypeDefinition layout — empirically identical to v29
// (88-byte stride, IL token at offset 84). Verified by hex-dumping
// Farthest Frontier + Emberward typeDefinitions tables and locating the
// canonical TypeDef token 0x02000003 at row 2: only stride=88 / token@+84
// produced clean alignment for both files. v31's "31" version bump is
// likely about header / images / assemblies trailing changes we don't
// touch, not the TypeDefinition row itself.
using TypeDefinition_v31 = TypeDefinition_v29;

// FieldDefinition + FieldDefaultValue layouts haven't changed across v24
// through v31 — same 12-byte rows. Aliasing rather than duplicating to keep
// the dispatch simple.
using FieldDefinition_v24      = FieldDefinition_v29;
using FieldDefinition_v31      = FieldDefinition_v29;
using FieldDefaultValue_v24    = FieldDefaultValue_v29;
using FieldDefaultValue_v31    = FieldDefaultValue_v29;

// v32-v34: identical to v31 (no struct-level changes per upstream
// Il2CppDumperV35plus). Aliasing for clarity at call sites.
using TypeDefinition_v32       = TypeDefinition_v29;
using TypeDefinition_v34       = TypeDefinition_v29;
using FieldDefinition_v32      = FieldDefinition_v29;
using FieldDefaultValue_v32    = FieldDefaultValue_v29;

// v35 drops Il2CppTypeDefinition.elementTypeIndex — 84-byte stride instead
// of v29's 88. The field was only consulted for enum decoding (resolved
// from parentIndex in v35+ per upstream `GetEnumElementTypeIndex`). All
// other fields keep their absolute layout: nameIndex/namespaceIndex stay at
// offset 0/4; byvalTypeIndex/declaringTypeIndex/parentIndex/
// genericContainerIndex are still 4-byte int32 indices in v35-v37
// (variable-width starts at v38), the 8 int "Start" fields and 8 ushort
// counts follow, then bitfield + token at the tail. Verified against
// vendor/Il2CppDumperV35plus/Il2CppDumper/Il2Cpp/MetadataClass.cs:290-378.
struct TypeDefinition_v35 {
    std::int32_t  nameIndex;
    std::int32_t  namespaceIndex;
    std::int32_t  byvalTypeIndex;
    std::int32_t  declaringTypeIndex;
    std::int32_t  parentIndex;
    // elementTypeIndex REMOVED in v35
    std::int32_t  genericContainerIndex;
    std::uint32_t flags;
    std::int32_t  fieldStart;
    std::int32_t  methodStart;
    std::int32_t  eventStart;
    std::int32_t  propertyStart;
    std::int32_t  nestedTypesStart;
    std::int32_t  interfacesStart;
    std::int32_t  vtableStart;
    std::int32_t  interfaceOffsetsStart;
    std::uint16_t method_count;
    std::uint16_t property_count;
    std::uint16_t field_count;
    std::uint16_t event_count;
    std::uint16_t nested_type_count;
    std::uint16_t vtable_count;
    std::uint16_t interfaces_count;
    std::uint16_t interface_offsets_count;
    std::uint32_t bitfield;
    std::uint32_t token;
};

// v35 also shrinks Il2CppStringLiteral: drops the `length` prefix; entries
// now hold just the `dataIndex` int32. Length is recovered from the delta
// to the next entry's dataIndex (and to stringLiteralDataSize for the
// last entry). Verified against MetadataClass.cs:474-479.
struct StringLiteralEntry_v35 {
    std::int32_t dataIndex;
};

// v36 / v37 reuse v35's layout per upstream `[Version(Min=…, Max=35)]`
// attributes (the version bumps don't change the row shape — incremental
// runtime-side changes only).
using TypeDefinition_v36       = TypeDefinition_v35;
using TypeDefinition_v37       = TypeDefinition_v35;
using FieldDefinition_v35      = FieldDefinition_v29;
using FieldDefinition_v36      = FieldDefinition_v29;
using FieldDefinition_v37      = FieldDefinition_v29;
using FieldDefaultValue_v35    = FieldDefaultValue_v29;
using FieldDefaultValue_v36    = FieldDefaultValue_v29;
using FieldDefaultValue_v37    = FieldDefaultValue_v29;

// v38 introduces the section-based header. Every (offset, size) pair from
// the flat v29-v37 header becomes a 12-byte `Il2CppSectionMetadata` triplet
// carrying offset / size / count. Critical: reading a v38 header through
// HeaderV29's flat (offset, size, offset, size, ...) shape yields silently-
// wrong offsets — the third int32 of a v38 header is a `count`, not the
// expected `stringLiteralDataOffset`. Field order matches MetadataClass.cs
// lines 169-230.
struct Il2CppSectionMetadata {
    std::uint32_t offset;
    std::uint32_t size;
    std::uint32_t count;
};

struct HeaderV38 {
    std::uint32_t sanity;
    std::int32_t  version;
    // 30 section triplets, in the order upstream declares them.
    Il2CppSectionMetadata stringLiterals;
    Il2CppSectionMetadata stringLiteralData;
    Il2CppSectionMetadata strings;
    Il2CppSectionMetadata events;
    Il2CppSectionMetadata properties;
    Il2CppSectionMetadata methods;
    Il2CppSectionMetadata parameterDefaultValues;
    Il2CppSectionMetadata fieldDefaultValues;
    Il2CppSectionMetadata fieldAndParameterDefaultValueData;
    Il2CppSectionMetadata fieldMarshaledSizes;
    Il2CppSectionMetadata parameters;
    Il2CppSectionMetadata fields;
    Il2CppSectionMetadata genericParameters;
    Il2CppSectionMetadata genericParameterConstraints;
    Il2CppSectionMetadata genericContainers;
    Il2CppSectionMetadata nestedTypes;
    Il2CppSectionMetadata interfaces;
    Il2CppSectionMetadata vtableMethods;
    Il2CppSectionMetadata interfaceOffsets;
    Il2CppSectionMetadata typeDefinitions;
    Il2CppSectionMetadata images;
    Il2CppSectionMetadata assemblies;
    Il2CppSectionMetadata fieldRefs;
    Il2CppSectionMetadata referencedAssemblies;
    Il2CppSectionMetadata attributeData;
    Il2CppSectionMetadata attributeDataRanges;
    Il2CppSectionMetadata unresolvedIndirectCallParameterTypes;
    Il2CppSectionMetadata unresolvedIndirectCallParameterRanges;
    Il2CppSectionMetadata windowsRuntimeTypeNames;
    Il2CppSectionMetadata windowsRuntimeStrings;
    Il2CppSectionMetadata exportedTypeDefinitions;
};
#pragma pack(pop)

// Variable-width metadata-index sizes inferred at load time. Pre-v38 these
// are all 4 (int32 always); v38+ shrinks them to 1/2/4 bytes per field based
// on each table's row count (small tables get small indices).
//
// Width derivation (from vendor/Il2CppDumperV35plus/Il2Cpp/Metadata.cs:101-114):
//   typeDefinitionIndexSize  = GetIndexSize(typeDefinitions.count)
//   genericContainerIndexSize = GetIndexSize(genericContainers.count)
//   parameterIndexSize       = GetIndexSize(parameters.count)
//   typeIndexSize            = sizeof(ParameterDefinition_row) - 8
//                              (Il2CppParameterDefinition has uint nameIndex +
//                               uint token + TypeIndex typeIndex = 8 + TIS)
// where GetIndexSize(n) = n ≤ 0xFF ? 1 : n ≤ 0xFFFF ? 2 : 4.
//
// v39 just adds ParamIndex to this family — no other structural changes
// per Perfare/Il2CppDumper#903.
struct IndexSizes {
    int typeIndexSize             = 4;
    int typeDefinitionIndexSize   = 4;
    int genericContainerIndexSize = 4;
    int parameterIndexSize        = 4;
};

inline int GetIndexSize(std::int64_t count) noexcept {
    if (count <= 0xFF)   return 1;
    if (count <= 0xFFFF) return 2;
    return 4;
}

static_assert(sizeof(HeaderPrefix)            == 32,  "HeaderPrefix must be 32 bytes");
static_assert(sizeof(StringLiteralEntry)      == 8,   "StringLiteralEntry must be 8 bytes");
static_assert(sizeof(StringLiteralEntry_v35)  == 4,   "StringLiteralEntry_v35 must be 4 bytes");
static_assert(sizeof(HeaderV29)               == 168, "HeaderV29 must be 168 bytes");
static_assert(sizeof(TypeDefinition_v29)      == 88,  "TypeDefinition_v29 must be 88 bytes");
static_assert(sizeof(TypeDefinition_v24)      == 92,  "TypeDefinition_v24 (v24.2-v24.5) must be 92 bytes");
static_assert(sizeof(TypeDefinition_v24_1)    == 100, "TypeDefinition_v24_1 must be 100 bytes (v24 + rgctx pair)");
static_assert(sizeof(TypeDefinition_v24_0)    == 104, "TypeDefinition_v24_0 must be 104 bytes (v24.1 + customAttributeIndex)");
static_assert(sizeof(FieldDefinition_v24_0)   == 16,  "FieldDefinition_v24_0 must be 16 bytes (v29 + customAttributeIndex)");
static_assert(sizeof(TypeDefinition_v31)      == 88,  "TypeDefinition_v31 must be 88 bytes (== v29)");
static_assert(sizeof(TypeDefinition_v35)      == 84,  "TypeDefinition_v35 must be 84 bytes (v29 minus elementTypeIndex)");
static_assert(sizeof(FieldDefinition_v29)     == 12,  "FieldDefinition_v29 must be 12 bytes");
static_assert(sizeof(FieldDefaultValue_v29)   == 12,  "FieldDefaultValue_v29 must be 12 bytes");
static_assert(sizeof(Il2CppSectionMetadata)   == 12,  "Il2CppSectionMetadata must be 12 bytes");
static_assert(sizeof(HeaderV38)               == 8 + 31 * 12,
              "HeaderV38 must be 8 (sanity+version) + 31 * 12 (sections) = 380 bytes");

}  // namespace UnityCEDumper::IL2CPP::Metadata
