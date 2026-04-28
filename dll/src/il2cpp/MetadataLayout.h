// MetadataLayout.h — on-disk layout for global-metadata.dat.
//
// global-metadata.dat is purely a tagged-offset blob — every reference is a
// 32-bit byte offset into the file, not a runtime pointer. So the file layout
// itself is bitness-neutral. The bitness concern (per Phase 5 Resolved
// Decisions) only kicks in when we cross-link these tables to live runtime
// pointers like Il2CppClass*; that wrapper layer lives elsewhere.
//
// We currently model only v29 (Unity 2021.3 / 2022.1). v24 / v27 / v31 stubs
// will land alongside the games that need them.
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
#pragma pack(pop)

static_assert(sizeof(HeaderPrefix)          == 32,  "HeaderPrefix must be 32 bytes");
static_assert(sizeof(StringLiteralEntry)    == 8,   "StringLiteralEntry must be 8 bytes");
static_assert(sizeof(HeaderV29)             == 168, "HeaderV29 must be 168 bytes");
static_assert(sizeof(TypeDefinition_v29)    == 88,  "TypeDefinition_v29 must be 88 bytes");
static_assert(sizeof(FieldDefinition_v29)   == 12,  "FieldDefinition_v29 must be 12 bytes");
static_assert(sizeof(FieldDefaultValue_v29) == 12,  "FieldDefaultValue_v29 must be 12 bytes");

}  // namespace UnityCEDumper::IL2CPP::Metadata
