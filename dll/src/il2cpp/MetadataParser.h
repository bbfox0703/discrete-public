// MetadataParser.h — opens global-metadata.dat for the host game and exposes
// string-literal / string-heap queries. Cross-link to runtime Il2CppClass*
// lands in a follow-up commit.
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace UnityCEDumper::IL2CPP::Metadata {

class Parser {
public:
    // Lazily-initialised singleton. First call probes the host process's
    // <gameDataDir>/il2cpp_data/Metadata/global-metadata.dat, validates magic,
    // and caches the file in memory. Subsequent calls are O(1).
    static const Parser& Get();

    bool         loaded() const noexcept  { return loaded_; }
    std::int32_t version() const noexcept { return version_; }

    std::uint32_t StringLiteralCount() const noexcept;

    // Returns the i-th string literal, or std::nullopt on invalid index /
    // bounds violation. Strings are returned as UTF-8 std::string.
    std::optional<std::string> GetStringLiteral(std::uint32_t idx) const;

    // Convenience: return the first `maxCount` string literals (0 = all).
    std::vector<std::string> EnumerateStringLiterals(std::uint32_t maxCount = 0) const;

    // Returns the ASCIIZ entry at `nameIndex` from the string heap (used for
    // type / field / method names). Empty string on failure.
    std::string GetHeapString(std::int32_t nameIndex) const;

    // Internal — invoked once by Get(). Public so the singleton helper in the
    // .cpp can construct/initialise without `friend` boilerplate.
    Parser() = default;
    void Load();

private:
    bool LoadHeader();

    bool                      loaded_  = false;
    std::int32_t              version_ = 0;
    std::vector<std::uint8_t> data_;  // owns the whole file

    // Cached prefix offsets (v29 layout, also valid for v24 / v27 / v31).
    std::int32_t stringLiteralOffset_     = 0;
    std::int32_t stringLiteralSize_       = 0;
    std::int32_t stringLiteralDataOffset_ = 0;
    std::int32_t stringLiteralDataSize_   = 0;
    std::int32_t stringOffset_            = 0;
    std::int32_t stringSize_              = 0;
};

}  // namespace UnityCEDumper::IL2CPP::Metadata
