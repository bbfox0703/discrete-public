// MetadataParser.cpp — implementation.
//
// Backend-agnostic core: parses an in-memory copy of global-metadata.dat
// and exposes the prefix tables (string literals, name heap) plus the v29
// TypeDefinitions / Fields / FieldDefaults tables. No dependency on
// BackendDetector — that lives in MetadataParserHostPath.cpp so the
// metadata-cli (Phase 13) can link this file alone.
//
// Logger calls are safe whether or not Log::Init() has run; the writer
// no-ops when uninitialised, so the CLI just doesn't initialise the
// logger and these calls disappear at runtime.

#include "MetadataParser.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "MetadataLayout.h"

#include <windows.h>

#include <algorithm>
#include <cstring>
#include <mutex>

namespace UnityCEDumper::IL2CPP::Metadata {

namespace {

bool ReadEntireFile(const std::wstring& path, std::vector<std::uint8_t>& out) {
    HANDLE h = ::CreateFileW(path.c_str(), GENERIC_READ,
                             FILE_SHARE_READ | FILE_SHARE_WRITE,
                             nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER size{};
    if (!::GetFileSizeEx(h, &size) || size.QuadPart <= 0 ||
        size.QuadPart > (256ll * 1024 * 1024)) {
        ::CloseHandle(h);
        return false;
    }
    out.resize(static_cast<std::size_t>(size.QuadPart));

    DWORD totalRead = 0;
    DWORD remaining = static_cast<DWORD>(out.size());
    DWORD got = 0;
    while (remaining > 0) {
        if (!::ReadFile(h, out.data() + totalRead, remaining, &got, nullptr) || got == 0) {
            ::CloseHandle(h);
            out.clear();
            return false;
        }
        totalRead += got;
        remaining -= got;
    }
    ::CloseHandle(h);
    return true;
}

}  // namespace

// Note: Parser::Get() (the host-process singleton) lives in
// MetadataParserHostPath.cpp because it depends on Parser::Load() which in
// turn calls BackendDetector. The CLI build doesn't link either symbol.

bool Parser::LoadFromPath(const std::wstring& path) {
    if (path.empty()) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
            "MetadataParser: empty path");
        return false;
    }
    if (!ReadEntireFile(path, data_)) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
            "MetadataParser: failed to read global-metadata.dat");
        data_.clear();
        return false;
    }
    if (!LoadHeader()) {
        data_.clear();
        return false;
    }
    // Phase 14c: load TypeDefinition / Fields / FieldDefaults tables for
    // v24 / v29 / v31. v27 still falls through to the strings-only path.
    if (TypeDefSizeof() != 0) {
        tablesLoaded_ = LoadTables();
    }

    char line[320];
    ::_snprintf_s(line, sizeof(line), _TRUNCATE,
        "MetadataParser: loaded version=%d size=%zu strLitTable=%d strLitData=%d nameHeap=%d "
        "typeDefs=%d fields=%d fieldDefaults=%d (typeTablesParsed=%s)",
        version_, data_.size(),
        stringLiteralSize_, stringLiteralDataSize_, stringSize_,
        typeDefinitionsSize_, fieldsSize_, fieldDefaultsSize_,
        tablesLoaded_ ? "yes" : "no");
    UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata, line);
    loaded_ = true;
    return true;
}

std::size_t Parser::TypeDefSizeof() const noexcept {
    switch (version_) {
        case 24: return sizeof(TypeDefinition_v24);   // 92 bytes (+byrefTypeIndex)
        case 27:                                       // 88 bytes — same as v29
        case 29:
        case 31:
        case 32:                                       // v32-v34 reuse v29's layout
        case 33:
        case 34: return sizeof(TypeDefinition_v29);    // 88 bytes
        case 35:                                       // v35-v37: elementTypeIndex removed
        case 36:
        case 37: return sizeof(TypeDefinition_v35);    // 84 bytes
        case 38:                                       // v38+: variable-width indices
        case 39: {
            // 3 TypeIndex (byval / declaring / parent) + 1 GenericContainerIndex.
            // Fixed bytes: 2 uint nameIndex/namespaceIndex (8) + uint flags (4) +
            // 8 int *Start (32) + 8 ushort *_count (16) + uint bitfield (4) +
            // uint token (4) = 68.
            return 68
                 + 3 * static_cast<std::size_t>(indexSizes_.typeIndexSize)
                 +     static_cast<std::size_t>(indexSizes_.genericContainerIndexSize);
        }
        default: return 0;
    }
}

std::size_t Parser::FieldDefSizeof() const noexcept {
    // Pre-v38: 12 bytes (uint nameIndex + int typeIndex + uint token).
    // v38+: 8 + typeIndexSize.
    if (version_ >= 38) {
        return 8u + static_cast<std::size_t>(indexSizes_.typeIndexSize);
    }
    return sizeof(FieldDefinition_v29);
}

std::size_t Parser::FieldDefaultValueSizeof() const noexcept {
    // Pre-v38: 12 bytes (int fieldIndex + int typeIndex + int dataIndex).
    // v38+: 8 + typeIndexSize (fieldIndex stays int32; typeIndex shrinks).
    if (version_ >= 38) {
        return 8u + static_cast<std::size_t>(indexSizes_.typeIndexSize);
    }
    return sizeof(FieldDefaultValue_v29);
}

bool Parser::LoadTables() {
    auto inBounds = [&](std::int32_t off, std::int32_t sz) {
        if (off < 0 || sz < 0) return false;
        std::uint64_t end = static_cast<std::uint64_t>(off) + static_cast<std::uint64_t>(sz);
        return end <= data_.size();
    };

    if (version_ >= 38) {
        // v38+ section-based header (HeaderV38) — every table is a 12-byte
        // Il2CppSectionMetadata triplet. Already partially read in
        // LoadHeader for the prefix strings; here we pull the table
        // sections we need for segment 2 plus compute IndexSizes.
        if (data_.size() < sizeof(HeaderV38)) {
            UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
                "MetadataParser: file too small for v38+ section header");
            return false;
        }
        HeaderV38 h{};
        std::memcpy(&h, data_.data(), sizeof(h));

        typeDefinitionsOffset_  = static_cast<std::int32_t>(h.typeDefinitions.offset);
        typeDefinitionsSize_    = static_cast<std::int32_t>(h.typeDefinitions.size);
        fieldsOffset_           = static_cast<std::int32_t>(h.fields.offset);
        fieldsSize_             = static_cast<std::int32_t>(h.fields.size);
        fieldDefaultsOffset_    = static_cast<std::int32_t>(h.fieldDefaultValues.offset);
        fieldDefaultsSize_      = static_cast<std::int32_t>(h.fieldDefaultValues.size);
        fieldDefaultDataOffset_ = static_cast<std::int32_t>(h.fieldAndParameterDefaultValueData.offset);
        fieldDefaultDataSize_   = static_cast<std::int32_t>(h.fieldAndParameterDefaultValueData.size);

        parametersCount_ = static_cast<std::int64_t>(h.parameters.count);

        // IndexSizes derivation. typeIndexSize comes from the parameter
        // row stride: `Il2CppParameterDefinition = uint nameIndex + uint
        // token + TypeIndex typeIndex` so size = 8 + TIS. typeDefinitionIndexSize
        // / genericContainerIndexSize / parameterIndexSize come from the
        // respective table's row count (small tables get small indices).
        if (h.parameters.count > 0 && h.parameters.size >= h.parameters.count) {
            std::int64_t parmRow = static_cast<std::int64_t>(h.parameters.size) /
                                   static_cast<std::int64_t>(h.parameters.count);
            int inferred = static_cast<int>(parmRow - 8);
            if (inferred == 1 || inferred == 2 || inferred == 4) {
                indexSizes_.typeIndexSize = inferred;
            }
        }
        indexSizes_.typeDefinitionIndexSize   = GetIndexSize(static_cast<std::int64_t>(h.typeDefinitions.count));
        indexSizes_.genericContainerIndexSize = GetIndexSize(static_cast<std::int64_t>(h.genericContainers.count));
        indexSizes_.parameterIndexSize        = GetIndexSize(static_cast<std::int64_t>(h.parameters.count));

        char ix[256];
        ::_snprintf_s(ix, sizeof(ix), _TRUNCATE,
            "MetadataParser: v%d IndexSizes — typeIndex=%d typeDefinitionIndex=%d "
            "genericContainerIndex=%d parameterIndex=%d (params row=%llu, "
            "typeDefs count=%u, genericContainers count=%u, params count=%u)",
            version_,
            indexSizes_.typeIndexSize, indexSizes_.typeDefinitionIndexSize,
            indexSizes_.genericContainerIndexSize, indexSizes_.parameterIndexSize,
            static_cast<unsigned long long>(h.parameters.count == 0 ? 0
                : h.parameters.size / h.parameters.count),
            h.typeDefinitions.count, h.genericContainers.count, h.parameters.count);
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata, ix);
    } else {
        // v24-v37 share the flat HeaderV29 prefix for the tables we care
        // about (verified empirically against SaGa F1R / SaGa F2R / FFVI PR /
        // Farthest Frontier / Emberward, plus upstream's `[Version(Max=35)]`
        // attribute on every flat-header field).
        if (data_.size() < sizeof(HeaderV29)) {
            UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
                "MetadataParser: file too small for flat header");
            return false;
        }
        HeaderV29 h{};
        std::memcpy(&h, data_.data(), sizeof(h));

        typeDefinitionsOffset_  = h.typeDefinitionsOffset;
        typeDefinitionsSize_    = h.typeDefinitionsSize;
        fieldsOffset_           = h.fieldsOffset;
        fieldsSize_             = h.fieldsSize;
        fieldDefaultsOffset_    = h.fieldDefaultValuesOffset;
        fieldDefaultsSize_      = h.fieldDefaultValuesSize;
        fieldDefaultDataOffset_ = h.fieldAndParameterDefaultValueDataOffset;
        fieldDefaultDataSize_   = h.fieldAndParameterDefaultValueDataSize;
    }

    if (!inBounds(typeDefinitionsOffset_,  typeDefinitionsSize_)  ||
        !inBounds(fieldsOffset_,           fieldsSize_)           ||
        !inBounds(fieldDefaultsOffset_,    fieldDefaultsSize_)    ||
        !inBounds(fieldDefaultDataOffset_, fieldDefaultDataSize_)) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
            "MetadataParser: tables out of bounds");
        return false;
    }
    return true;
}

bool Parser::LoadHeader() {
    if (data_.size() < sizeof(HeaderPrefix)) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
            "MetadataParser: file too small for header prefix");
        return false;
    }
    HeaderPrefix h{};
    std::memcpy(&h, data_.data(), sizeof(h));
    if (static_cast<std::uint32_t>(h.sanity) != Sanity) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
            "MetadataParser: sanity mismatch (encrypted or corrupt)");
        return false;
    }

    version_ = h.version;

    // Defensive range: 16..39 inclusive. v16-v31 covered by Phase 14c
    // (flat header + v24/v29/v31 type layouts); v32-v34 reuse v31's
    // layout per upstream; v35-v37 drop elementTypeIndex from
    // TypeDefinition (84-byte stride) and length from StringLiteral
    // (4-byte entries); v38+ rewrite the header into Il2CppSectionMetadata
    // triplets + variable-width metadata indices; v39 adds ParamIndex to
    // the variable-width family (no other changes). Beyond v39 falls
    // through to "unsupported" — better to surface a clear error than
    // emit silently-wrong offsets via a stale layout.
    if (version_ < 16 || version_ > 39) {
        char line[256];
        ::_snprintf_s(line, sizeof(line), _TRUNCATE,
            "MetadataParser: unsupported metadata version %d (supported "
            "range is 16..39; see docs/il2cpp-metadata-crosscheck.md)",
            version_);
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata, line);
        return false;
    }

    // v35+ shrinks Il2CppStringLiteral to a single int32 dataIndex (drops
    // the `length` prefix; length is computed from the delta to the next
    // entry). Latched once at load so StringLiteralCount stays O(1).
    if (version_ >= 35) {
        stringLiteralEntryStride_ = sizeof(StringLiteralEntry_v35);
    } else {
        stringLiteralEntryStride_ = sizeof(StringLiteralEntry);
    }

    // Header shape: v16-v37 flat (HeaderV29-compatible prefix), v38+ section
    // (HeaderV38 with 30 Il2CppSectionMetadata triplets after sanity/version).
    if (version_ >= 38) {
        if (data_.size() < sizeof(HeaderV38)) {
            UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
                "MetadataParser: file too small for v38+ section header");
            return false;
        }
        HeaderV38 h38{};
        std::memcpy(&h38, data_.data(), sizeof(h38));

        stringLiteralOffset_     = static_cast<std::int32_t>(h38.stringLiterals.offset);
        stringLiteralSize_       = static_cast<std::int32_t>(h38.stringLiterals.size);
        stringLiteralDataOffset_ = static_cast<std::int32_t>(h38.stringLiteralData.offset);
        stringLiteralDataSize_   = static_cast<std::int32_t>(h38.stringLiteralData.size);
        stringOffset_            = static_cast<std::int32_t>(h38.strings.offset);
        stringSize_              = static_cast<std::int32_t>(h38.strings.size);
    } else {
        stringLiteralOffset_      = h.stringLiteralOffset;
        stringLiteralSize_        = h.stringLiteralSize;
        stringLiteralDataOffset_  = h.stringLiteralDataOffset;
        stringLiteralDataSize_    = h.stringLiteralDataSize;
        stringOffset_             = h.stringOffset;
        stringSize_               = h.stringSize;
    }

    // Verified versions: v24 / v27 / v29 / v31 / v35-v37 (struct field
    // removals only) / v38-v39 (section header + variable-width indices).
    // v32-v34 reuse v31's layout per upstream; we accept them with a soft
    // warning because we don't have a test target.
    bool isVerified =
        version_ == 24 || version_ == 27 || version_ == 29 || version_ == 31 ||
        (version_ >= 35 && version_ <= 39);
    if (!isVerified) {
        char line[200];
        ::_snprintf_s(line, sizeof(line), _TRUNCATE,
            "MetadataParser: version %d shares v31's layout per upstream "
            "Il2CppDumperV35plus — accepting with soft warning. Report any "
            "decode anomalies for cross-check.",
            version_);
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata, line);
    }

    auto inBounds = [&](std::int32_t off, std::int32_t sz) {
        if (off < 0 || sz < 0) return false;
        std::uint64_t end = static_cast<std::uint64_t>(off) + static_cast<std::uint64_t>(sz);
        return end <= data_.size();
    };
    if (!inBounds(stringLiteralOffset_,     stringLiteralSize_)     ||
        !inBounds(stringLiteralDataOffset_, stringLiteralDataSize_) ||
        !inBounds(stringOffset_,            stringSize_)) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
            "MetadataParser: prefix tables out of bounds");
        return false;
    }
    return true;
}

std::uint32_t Parser::StringLiteralCount() const noexcept {
    if (!loaded_) return 0;
    if (stringLiteralEntryStride_ <= 0) return 0;
    return static_cast<std::uint32_t>(stringLiteralSize_) /
           static_cast<std::uint32_t>(stringLiteralEntryStride_);
}

std::optional<std::string> Parser::GetStringLiteral(std::uint32_t idx) const {
    if (!loaded_) return std::nullopt;
    std::uint32_t count = StringLiteralCount();
    if (idx >= count) return std::nullopt;

    const std::uint8_t* tableBase = data_.data() + stringLiteralOffset_;
    std::int32_t dataIdx = 0;
    std::uint32_t length = 0;

    if (stringLiteralEntryStride_ == sizeof(StringLiteralEntry_v35)) {
        // v35+: entry is just `int32 dataIndex`. Length is the delta to the
        // next entry's dataIndex (or stringLiteralDataSize for the last).
        std::memcpy(&dataIdx, tableBase + idx * sizeof(StringLiteralEntry_v35),
                    sizeof(dataIdx));
        std::int32_t nextDataIdx = stringLiteralDataSize_;
        if (idx + 1 < count) {
            std::memcpy(&nextDataIdx,
                        tableBase + (idx + 1) * sizeof(StringLiteralEntry_v35),
                        sizeof(nextDataIdx));
        }
        if (nextDataIdx < dataIdx) return std::nullopt;
        length = static_cast<std::uint32_t>(nextDataIdx - dataIdx);
    } else {
        // v24-v34: entry is `(uint length, int32 dataIndex)` — 8 bytes.
        StringLiteralEntry entry{};
        std::memcpy(&entry, tableBase + idx * sizeof(StringLiteralEntry), sizeof(entry));
        dataIdx = entry.dataIndex;
        length  = entry.length;
    }

    if (dataIdx < 0 || length == 0) return std::string{};
    std::uint64_t end = static_cast<std::uint64_t>(dataIdx) + length;
    if (end > static_cast<std::uint64_t>(stringLiteralDataSize_)) return std::nullopt;

    const char* p = reinterpret_cast<const char*>(
        data_.data() + stringLiteralDataOffset_ + dataIdx);
    return std::string(p, length);
}

std::vector<std::string> Parser::EnumerateStringLiterals(std::uint32_t maxCount) const {
    std::vector<std::string> out;
    if (!loaded_) return out;
    std::uint32_t total = StringLiteralCount();
    std::uint32_t limit = (maxCount == 0) ? total : std::min(maxCount, total);
    out.reserve(limit);
    for (std::uint32_t i = 0; i < limit; ++i) {
        if (auto s = GetStringLiteral(i)) out.push_back(std::move(*s));
        else                              out.emplace_back();
    }
    return out;
}

std::string Parser::GetHeapString(std::int32_t nameIndex) const {
    if (!loaded_) return {};
    if (nameIndex < 0 || nameIndex >= stringSize_) return {};
    const char* p = reinterpret_cast<const char*>(
        data_.data() + stringOffset_ + nameIndex);
    // ASCIIZ; bound-check by walking until either NUL or end of heap.
    std::int32_t maxLen = stringSize_ - nameIndex;
    std::int32_t len = 0;
    while (len < maxLen && p[len] != '\0') ++len;
    return std::string(p, static_cast<std::size_t>(len));
}

// --- segment 2 ---------------------------------------------------------------

std::uint32_t Parser::TypeDefinitionCount() const noexcept {
    if (!tablesLoaded_) return 0;
    std::size_t sz = TypeDefSizeof();
    if (sz == 0) return 0;
    return static_cast<std::uint32_t>(typeDefinitionsSize_) / static_cast<std::uint32_t>(sz);
}

std::optional<Parser::TypeDefView> Parser::GetTypeDefinition(std::int32_t idx) const {
    if (!tablesLoaded_) return std::nullopt;
    std::uint32_t count = TypeDefinitionCount();
    if (idx < 0 || static_cast<std::uint32_t>(idx) >= count) return std::nullopt;

    std::size_t sz = TypeDefSizeof();
    const std::uint8_t* row = data_.data() + typeDefinitionsOffset_ +
                              static_cast<std::size_t>(idx) * sz;

    TypeDefView v;
    v.index = idx;
    if (version_ == 24) {
        // v24 (Unity 2018.4-2019.x): byrefTypeIndex inserted between
        // byvalTypeIndex and declaringTypeIndex; 92-byte stride, token at
        // offset 88. Everything else aligned to v29's projected fields.
        TypeDefinition_v24 td{};
        std::memcpy(&td, row, sizeof(td));
        v.ns          = GetHeapString(td.namespaceIndex);
        v.name        = GetHeapString(td.nameIndex);
        v.fieldStart  = td.fieldStart;
        v.fieldCount  = td.field_count;
        v.methodStart = td.methodStart;
        v.methodCount = td.method_count;
        v.flags       = td.flags;
        v.token       = td.token;
    } else if (version_ >= 35 && version_ <= 37) {
        // v35-v37: identical to v29 but with elementTypeIndex removed.
        // 84-byte stride; field offsets after parentIndex shift -4 bytes.
        TypeDefinition_v35 td{};
        std::memcpy(&td, row, sizeof(td));
        v.ns          = GetHeapString(td.namespaceIndex);
        v.name        = GetHeapString(td.nameIndex);
        v.fieldStart  = td.fieldStart;
        v.fieldCount  = td.field_count;
        v.methodStart = td.methodStart;
        v.methodCount = td.method_count;
        v.flags       = td.flags;
        v.token       = td.token;
    } else if (version_ >= 38) {
        // v38+: variable-width metadata-index plumbing. Hand-walk the row
        // since the stride depends on runtime-computed sizes. Layout per
        // vendor/Il2CppDumperV35plus/Il2Cpp/MetadataClass.cs:290-378
        // (with elementTypeIndex removed per v35+).
        //
        //   uint   nameIndex                     4
        //   uint   namespaceIndex                4
        //   TI     byvalTypeIndex                typeIndexSize
        //   TI     declaringTypeIndex            typeIndexSize
        //   TI     parentIndex                   typeIndexSize
        //   GCI    genericContainerIndex         genericContainerIndexSize
        //   uint   flags                         4
        //   int    fieldStart                    4   <- needed
        //   int    methodStart                   4   <- needed
        //   int    eventStart                    4
        //   int    propertyStart                 4
        //   int    nestedTypesStart              4
        //   int    interfacesStart               4
        //   int    vtableStart                   4
        //   int    interfaceOffsetsStart         4
        //   ushort method_count                  2   <- needed
        //   ushort property_count                2
        //   ushort field_count                   2   <- needed
        //   ushort event_count                   2
        //   ushort nested_type_count             2
        //   ushort vtable_count                  2
        //   ushort interfaces_count              2
        //   ushort interface_offsets_count       2
        //   uint   bitfield                      4
        //   uint   token                         4   <- needed
        const std::size_t TIS  = static_cast<std::size_t>(indexSizes_.typeIndexSize);
        const std::size_t GCIS = static_cast<std::size_t>(indexSizes_.genericContainerIndexSize);
        const std::size_t fixedPrefix = 8;                  // nameIndex + namespaceIndex
        const std::size_t varBlock    = 3 * TIS + GCIS;
        std::int32_t nameIndex      = 0;
        std::int32_t namespaceIndex = 0;
        std::uint32_t flags         = 0;
        std::int32_t fieldStart     = 0;
        std::int32_t methodStart    = 0;
        std::uint16_t method_count  = 0;
        std::uint16_t field_count   = 0;
        std::uint32_t token         = 0;
        std::memcpy(&nameIndex,      row + 0, 4);
        std::memcpy(&namespaceIndex, row + 4, 4);
        std::memcpy(&flags,          row + fixedPrefix + varBlock + 0, 4);
        std::memcpy(&fieldStart,     row + fixedPrefix + varBlock + 4, 4);
        std::memcpy(&methodStart,    row + fixedPrefix + varBlock + 8, 4);
        // 8 *Start ints (32 bytes total) span [varBlock + 4 .. varBlock + 36].
        std::memcpy(&method_count,   row + fixedPrefix + varBlock + 36 + 0,  2);
        std::memcpy(&field_count,    row + fixedPrefix + varBlock + 36 + 4,  2);
        // 8 *_count ushorts (16 bytes total) span [+36 .. +52].
        // Then uint bitfield (+52), uint token (+56). Total stride = +60.
        std::memcpy(&token,          row + fixedPrefix + varBlock + 56,      4);

        v.ns          = GetHeapString(namespaceIndex);
        v.name        = GetHeapString(nameIndex);
        v.fieldStart  = fieldStart;
        v.fieldCount  = field_count;
        v.methodStart = methodStart;
        v.methodCount = method_count;
        v.flags       = flags;
        v.token       = token;
    } else {
        // v27 / v29 / v31 / v32-v34: same 88-byte TypeDefinition layout —
        // confirmed empirically against SaGa F2R / Farthest Frontier /
        // Emberward by hex-dumping the typeDefinitions table and locating
        // the canonical 0x02000003 token at row 2 offset 84 with stride 88.
        TypeDefinition_v29 td{};
        std::memcpy(&td, row, sizeof(td));
        v.ns          = GetHeapString(td.namespaceIndex);
        v.name        = GetHeapString(td.nameIndex);
        v.fieldStart  = td.fieldStart;
        v.fieldCount  = td.field_count;
        v.methodStart = td.methodStart;
        v.methodCount = td.method_count;
        v.flags       = td.flags;
        v.token       = td.token;
    }
    return v;
}

std::optional<Parser::TypeDefView>
Parser::FindTypeByName(const std::string& ns, const std::string& name) const {
    if (!tablesLoaded_) return std::nullopt;
    std::uint32_t count = TypeDefinitionCount();
    for (std::uint32_t i = 0; i < count; ++i) {
        if (auto v = GetTypeDefinition(static_cast<std::int32_t>(i))) {
            if (v->name == name && v->ns == ns) return v;
        }
    }
    return std::nullopt;
}

std::vector<Parser::FieldDefView>
Parser::EnumerateTypeFields(const TypeDefView& type) const {
    std::vector<FieldDefView> out;
    if (!tablesLoaded_) return out;
    if (type.fieldCount == 0) return out;

    // Pre-v38: FieldDefinition is a fixed 12-byte row (uint nameIndex +
    // int typeIndex + uint token). v38+: typeIndex shrinks to TIS bytes,
    // so the stride is 8 + TIS — hand-walk the row.
    std::size_t rowSize = FieldDefSizeof();
    if (rowSize == 0) return out;
    std::uint32_t fieldRowCount = static_cast<std::uint32_t>(fieldsSize_) /
                                   static_cast<std::uint32_t>(rowSize);
    const bool variableLayout = (version_ >= 38);

    out.reserve(type.fieldCount);
    for (std::uint16_t k = 0; k < type.fieldCount; ++k) {
        std::int32_t globalIdx = type.fieldStart + k;
        if (globalIdx < 0 || static_cast<std::uint32_t>(globalIdx) >= fieldRowCount) break;

        const std::uint8_t* row = data_.data() + fieldsOffset_ +
                                  static_cast<std::size_t>(globalIdx) * rowSize;
        FieldDefView v;
        v.index = globalIdx;
        if (variableLayout) {
            std::int32_t nameIndex = 0;
            std::int32_t typeIndex = 0;     // sign-extended to int32 below
            std::uint32_t token = 0;
            std::memcpy(&nameIndex, row, 4);
            // typeIndex is TIS bytes (1/2/4). Read unsigned then promote.
            std::uint32_t typeRaw = 0;
            std::memcpy(&typeRaw, row + 4, static_cast<std::size_t>(indexSizes_.typeIndexSize));
            typeIndex = static_cast<std::int32_t>(typeRaw);
            std::memcpy(&token, row + 4 + indexSizes_.typeIndexSize, 4);
            v.name      = GetHeapString(nameIndex);
            v.typeIndex = typeIndex;
            v.token     = token;
        } else {
            FieldDefinition_v29 f{};
            std::memcpy(&f, row, sizeof(f));
            v.name      = GetHeapString(f.nameIndex);
            v.typeIndex = f.typeIndex;
            v.token     = f.token;
        }
        out.push_back(std::move(v));
    }
    return out;
}

std::optional<std::string> Parser::TryDecodeStringDefault(std::int32_t dataIndex) const {
    if (!tablesLoaded_) return std::nullopt;
    if (dataIndex < 0 || dataIndex >= fieldDefaultDataSize_) return std::nullopt;

    const std::uint8_t* base = data_.data() + fieldDefaultDataOffset_ + dataIndex;
    std::int32_t avail = fieldDefaultDataSize_ - dataIndex;
    if (avail < 1) return std::nullopt;

    // ECMA-335 §II.23.2 compressed *signed* integer — IL2CPP encodes string
    // default lengths this way. The unsigned bits give 2*length (low bit is
    // the sign; always 0 for non-negative lengths), so we right-shift by 1.
    std::uint32_t encoded = 0;
    std::int32_t  headerLen = 0;
    std::uint8_t  b0 = base[0];

    if (b0 == 0xFFu) return std::string{};  // null-string sentinel
    if ((b0 & 0x80u) == 0u) {
        encoded = b0;
        headerLen = 1;
    } else if ((b0 & 0xC0u) == 0x80u) {
        if (avail < 2) return std::nullopt;
        encoded = (static_cast<std::uint32_t>(b0 & 0x3Fu) << 8) | base[1];
        headerLen = 2;
    } else if ((b0 & 0xE0u) == 0xC0u) {
        if (avail < 4) return std::nullopt;
        encoded = (static_cast<std::uint32_t>(b0 & 0x1Fu) << 24) |
                  (static_cast<std::uint32_t>(base[1]) << 16) |
                  (static_cast<std::uint32_t>(base[2]) << 8)  |
                   static_cast<std::uint32_t>(base[3]);
        headerLen = 4;
    } else {
        return std::nullopt;
    }
    // Decode signed: bit 0 == 0 → positive (length = encoded >> 1).
    // bit 0 == 1 means negative — invalid for a string length, reject.
    if ((encoded & 1u) != 0u) return std::nullopt;
    std::uint32_t length = encoded >> 1;

    if (length == 0) return std::string{};
    if (length > 1 * 1024 * 1024) return std::nullopt;
    if (static_cast<std::int64_t>(headerLen) + length >
        static_cast<std::int64_t>(avail)) {
        return std::nullopt;
    }

    const std::uint8_t* p = base + headerLen;

    // Heuristic on the first 32 bytes only — guards against random non-string
    // blobs whose length math happens to look reasonable.
    std::uint32_t sample = (length < 32u) ? length : 32u;
    std::uint32_t printable = 0;
    std::uint32_t controlBad = 0;
    for (std::uint32_t i = 0; i < sample; ++i) {
        std::uint8_t c = p[i];
        if (c == 0)                                           ++controlBad;
        else if (c < 0x20u && c != '\t' && c != '\n' && c != '\r') ++controlBad;
        else                                                  ++printable;
    }
    if (sample > 0) {
        if (controlBad * 4 > sample) return std::nullopt;       // > 25% control chars
        if (printable * 10 < sample * 7) return std::nullopt;   // < 70% printable
    }

    // Sanitize to valid UTF-8: malformed sequences become U+FFFD so nlohmann's
    // serializer never sees an invalid byte. Latin-1 / partial UTF-8 / weird
    // encodings end up readable enough to inspect.
    std::string out;
    out.reserve(length);
    auto pushReplacement = [&]() {
        out.push_back('\xEF'); out.push_back('\xBF'); out.push_back('\xBD');
    };
    for (std::uint32_t i = 0; i < length; ) {
        std::uint8_t c = p[i];
        if (c < 0x80u) {
            out.push_back(static_cast<char>(c));
            ++i;
            continue;
        }
        std::uint32_t need = 0;
        if      ((c & 0xE0u) == 0xC0u && c >= 0xC2u) need = 1;
        else if ((c & 0xF0u) == 0xE0u)               need = 2;
        else if ((c & 0xF8u) == 0xF0u && c <= 0xF4u) need = 3;
        else { pushReplacement(); ++i; continue; }

        if (i + need >= length) { pushReplacement(); ++i; continue; }
        bool ok = true;
        for (std::uint32_t k = 1; k <= need; ++k) {
            if ((p[i + k] & 0xC0u) != 0x80u) { ok = false; break; }
        }
        if (!ok) { pushReplacement(); ++i; continue; }
        for (std::uint32_t k = 0; k <= need; ++k) {
            out.push_back(static_cast<char>(p[i + k]));
        }
        i += need + 1;
    }
    return out;
}

std::optional<Parser::FieldDefaultBlob>
Parser::FindFieldDefault(std::int32_t globalFieldIndex) const {
    if (!tablesLoaded_) return std::nullopt;
    // Row stride: 12 bytes pre-v38 (int fieldIndex + int typeIndex + int dataIndex);
    // v38+ shrinks typeIndex to TIS bytes (stride = 8 + TIS).
    std::size_t rowSize = FieldDefaultValueSizeof();
    if (rowSize == 0) return std::nullopt;
    std::uint32_t rowCount = static_cast<std::uint32_t>(fieldDefaultsSize_) /
                              static_cast<std::uint32_t>(rowSize);
    if (rowCount == 0) return std::nullopt;
    const bool variableLayout = (version_ >= 38);

    // FieldDefaults is sparse and (per IL2CPP source) sorted by fieldIndex.
    // Linear scan is fine here — type lookup is the dominant cost. A future
    // optimization can lazily build an index map.
    for (std::uint32_t i = 0; i < rowCount; ++i) {
        const std::uint8_t* row = data_.data() + fieldDefaultsOffset_ +
                                  static_cast<std::size_t>(i) * rowSize;
        std::int32_t fieldIndex = 0;
        std::int32_t typeIndex  = 0;
        std::int32_t dataIndex  = 0;
        if (variableLayout) {
            std::memcpy(&fieldIndex, row + 0, 4);
            std::uint32_t typeRaw = 0;
            std::memcpy(&typeRaw, row + 4, static_cast<std::size_t>(indexSizes_.typeIndexSize));
            typeIndex = static_cast<std::int32_t>(typeRaw);
            std::memcpy(&dataIndex, row + 4 + indexSizes_.typeIndexSize, 4);
        } else {
            FieldDefaultValue_v29 d{};
            std::memcpy(&d, row, sizeof(d));
            fieldIndex = d.fieldIndex;
            typeIndex  = d.typeIndex;
            dataIndex  = d.dataIndex;
        }
        if (fieldIndex != globalFieldIndex) continue;

        FieldDefaultBlob b;
        b.fieldIndex = fieldIndex;
        b.typeIndex  = typeIndex;
        b.dataIndex  = dataIndex;
        if (dataIndex >= 0 && dataIndex < fieldDefaultDataSize_) {
            // The blob is variable-length; without parsing the types[] table
            // we don't know the exact size. Capture up to the next FieldDefault
            // boundary or 256 bytes, whichever is smaller. Type-aware decode
            // will land in segment 2c.
            std::int32_t maxAvail = fieldDefaultDataSize_ - dataIndex;
            std::int32_t take = (maxAvail < 256) ? maxAvail : 256;
            const std::uint8_t* p =
                data_.data() + fieldDefaultDataOffset_ + dataIndex;
            b.data.assign(p, p + take);
        }
        return b;
    }
    return std::nullopt;
}

}  // namespace UnityCEDumper::IL2CPP::Metadata
