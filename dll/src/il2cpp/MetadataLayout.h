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
#pragma pack(pop)

static_assert(sizeof(HeaderPrefix) == 8 * 4,        "HeaderPrefix must be 32 bytes");
static_assert(sizeof(StringLiteralEntry) == 8,      "StringLiteralEntry must be 8 bytes");

}  // namespace UnityCEDumper::IL2CPP::Metadata
