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

    char line[256];
    ::_snprintf_s(line, sizeof(line), _TRUNCATE,
        "MetadataParser: loaded version=%d size=%zu strLitTable=%d strLitData=%d nameHeap=%d",
        version_, data_.size(),
        stringLiteralSize_, stringLiteralDataSize_, stringSize_);
    UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata, line);
    loaded_ = true;
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

}  // namespace UnityCEDumper::IL2CPP::Metadata
