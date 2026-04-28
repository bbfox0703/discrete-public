// MetadataParser.cpp — implementation.

#include "MetadataParser.h"

#include "../shared/BackendDetector.h"
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

std::wstring MetadataPathForCurrentProcess() {
    auto d = BackendDetector::Detect();
    if (d.gameDataDir.empty()) return {};
    return d.gameDataDir + L"\\il2cpp_data\\Metadata\\global-metadata.dat";
}

}  // namespace

const Parser& Parser::Get() {
    static Parser instance = [] {
        Parser p;
        p.Load();
        return p;
    }();
    return instance;
}

void Parser::Load() {
    std::wstring path = MetadataPathForCurrentProcess();
    if (path.empty()) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
            "MetadataParser: no game data dir resolved");
        return;
    }
    if (!ReadEntireFile(path, data_)) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
            "MetadataParser: failed to read global-metadata.dat");
        data_.clear();
        return;
    }
    if (!LoadHeader()) {
        data_.clear();
        return;
    }
    if (version_ == 29) {
        v29Tables_ = LoadV29Tables();
    }

    char line[320];
    ::_snprintf_s(line, sizeof(line), _TRUNCATE,
        "MetadataParser: loaded version=%d size=%zu strLitTable=%d strLitData=%d nameHeap=%d "
        "typeDefs=%d fields=%d fieldDefaults=%d (v29Tables=%s)",
        version_, data_.size(),
        stringLiteralSize_, stringLiteralDataSize_, stringSize_,
        typeDefinitionsSize_, fieldsSize_, fieldDefaultsSize_,
        v29Tables_ ? "yes" : "no");
    UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata, line);
    loaded_ = true;
}

bool Parser::LoadV29Tables() {
    if (data_.size() < sizeof(HeaderV29)) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
            "MetadataParser: file too small for v29 header");
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

    auto inBounds = [&](std::int32_t off, std::int32_t sz) {
        if (off < 0 || sz < 0) return false;
        std::uint64_t end = static_cast<std::uint64_t>(off) + static_cast<std::uint64_t>(sz);
        return end <= data_.size();
    };
    if (!inBounds(typeDefinitionsOffset_,  typeDefinitionsSize_)  ||
        !inBounds(fieldsOffset_,           fieldsSize_)           ||
        !inBounds(fieldDefaultsOffset_,    fieldDefaultsSize_)    ||
        !inBounds(fieldDefaultDataOffset_, fieldDefaultDataSize_)) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
            "MetadataParser: v29 tables out of bounds");
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

    version_                  = h.version;
    stringLiteralOffset_      = h.stringLiteralOffset;
    stringLiteralSize_        = h.stringLiteralSize;
    stringLiteralDataOffset_  = h.stringLiteralDataOffset;
    stringLiteralDataSize_    = h.stringLiteralDataSize;
    stringOffset_             = h.stringOffset;
    stringSize_               = h.stringSize;

    // Currently only v29 is verified. Other versions still load, but emit a
    // warning so the operator knows we're guessing.
    if (version_ != 29) {
        char line[160];
        ::_snprintf_s(line, sizeof(line), _TRUNCATE,
            "MetadataParser: untested version %d — string queries may misbehave",
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
    return static_cast<std::uint32_t>(stringLiteralSize_) / sizeof(StringLiteralEntry);
}

std::optional<std::string> Parser::GetStringLiteral(std::uint32_t idx) const {
    if (!loaded_) return std::nullopt;
    std::uint32_t count = StringLiteralCount();
    if (idx >= count) return std::nullopt;

    const std::uint8_t* tableBase = data_.data() + stringLiteralOffset_;
    StringLiteralEntry entry{};
    std::memcpy(&entry, tableBase + idx * sizeof(StringLiteralEntry), sizeof(entry));

    if (entry.dataIndex < 0 || entry.length == 0) return std::string{};
    std::uint64_t end = static_cast<std::uint64_t>(entry.dataIndex) + entry.length;
    if (end > static_cast<std::uint64_t>(stringLiteralDataSize_)) return std::nullopt;

    const char* p = reinterpret_cast<const char*>(
        data_.data() + stringLiteralDataOffset_ + entry.dataIndex);
    return std::string(p, entry.length);
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
    if (!v29Tables_) return 0;
    return static_cast<std::uint32_t>(typeDefinitionsSize_) / sizeof(TypeDefinition_v29);
}

std::optional<Parser::TypeDefView> Parser::GetTypeDefinition(std::int32_t idx) const {
    if (!v29Tables_) return std::nullopt;
    std::uint32_t count = TypeDefinitionCount();
    if (idx < 0 || static_cast<std::uint32_t>(idx) >= count) return std::nullopt;

    TypeDefinition_v29 td{};
    std::memcpy(&td,
                data_.data() + typeDefinitionsOffset_ +
                    static_cast<std::size_t>(idx) * sizeof(TypeDefinition_v29),
                sizeof(td));

    TypeDefView v;
    v.index       = idx;
    v.ns          = GetHeapString(td.namespaceIndex);
    v.name        = GetHeapString(td.nameIndex);
    v.fieldStart  = td.fieldStart;
    v.fieldCount  = td.field_count;
    v.methodStart = td.methodStart;
    v.methodCount = td.method_count;
    v.flags       = td.flags;
    v.token       = td.token;
    return v;
}

std::optional<Parser::TypeDefView>
Parser::FindTypeByName(const std::string& ns, const std::string& name) const {
    if (!v29Tables_) return std::nullopt;
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
    if (!v29Tables_) return out;
    if (type.fieldCount == 0) return out;

    std::uint32_t fieldRowCount = static_cast<std::uint32_t>(fieldsSize_) /
                                   sizeof(FieldDefinition_v29);
    out.reserve(type.fieldCount);
    for (std::uint16_t k = 0; k < type.fieldCount; ++k) {
        std::int32_t globalIdx = type.fieldStart + k;
        if (globalIdx < 0 || static_cast<std::uint32_t>(globalIdx) >= fieldRowCount) break;

        FieldDefinition_v29 f{};
        std::memcpy(&f,
                    data_.data() + fieldsOffset_ +
                        static_cast<std::size_t>(globalIdx) * sizeof(FieldDefinition_v29),
                    sizeof(f));

        FieldDefView v;
        v.index     = globalIdx;
        v.name      = GetHeapString(f.nameIndex);
        v.typeIndex = f.typeIndex;
        v.token     = f.token;
        out.push_back(std::move(v));
    }
    return out;
}

std::optional<std::string> Parser::TryDecodeStringDefault(std::int32_t dataIndex) const {
    if (!v29Tables_) return std::nullopt;
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
    if (!v29Tables_) return std::nullopt;
    std::uint32_t rowCount = static_cast<std::uint32_t>(fieldDefaultsSize_) /
                              sizeof(FieldDefaultValue_v29);
    if (rowCount == 0) return std::nullopt;

    // FieldDefaults is sparse and (per IL2CPP source) sorted by fieldIndex.
    // Linear scan is fine here — type lookup is the dominant cost. A future
    // optimization can lazily build an index map.
    for (std::uint32_t i = 0; i < rowCount; ++i) {
        FieldDefaultValue_v29 d{};
        std::memcpy(&d,
                    data_.data() + fieldDefaultsOffset_ +
                        static_cast<std::size_t>(i) * sizeof(FieldDefaultValue_v29),
                    sizeof(d));
        if (d.fieldIndex != globalFieldIndex) continue;

        FieldDefaultBlob b;
        b.fieldIndex = d.fieldIndex;
        b.typeIndex  = d.typeIndex;
        b.dataIndex  = d.dataIndex;
        if (d.dataIndex >= 0 && d.dataIndex < fieldDefaultDataSize_) {
            // The blob is variable-length; without parsing the types[] table
            // we don't know the exact size. Capture up to the next FieldDefault
            // boundary or 256 bytes, whichever is smaller. Type-aware decode
            // will land in segment 2c.
            std::int32_t maxAvail = fieldDefaultDataSize_ - d.dataIndex;
            std::int32_t take = (maxAvail < 256) ? maxAvail : 256;
            const std::uint8_t* p =
                data_.data() + fieldDefaultDataOffset_ + d.dataIndex;
            b.data.assign(p, p + take);
        }
        return b;
    }
    return std::nullopt;
}

}  // namespace UnityCEDumper::IL2CPP::Metadata
