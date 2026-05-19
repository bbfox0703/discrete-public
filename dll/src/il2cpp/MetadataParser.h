// MetadataParser.h — opens global-metadata.dat for the host game and exposes
// string-literal / string-heap queries. Cross-link to runtime Il2CppClass*
// lands in a follow-up commit.
#pragma once

#include "MetadataLayout.h"

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

    // --- segment 2: TypeDefinitions / Fields / FieldDefaults ---------------

    struct TypeDefView {
        std::int32_t  index;        // global TypeDefinition index
        std::string   ns;
        std::string   name;
        std::int32_t  fieldStart   = 0;
        std::uint16_t fieldCount   = 0;
        std::int32_t  methodStart  = 0;
        std::uint16_t methodCount  = 0;
        std::uint32_t flags        = 0;
        std::uint32_t token        = 0;
    };

    struct FieldDefView {
        std::int32_t  index;        // global Field index
        std::string   name;
        std::int32_t  typeIndex   = 0;
        std::uint32_t token       = 0;
    };

    struct FieldDefaultBlob {
        std::int32_t              fieldIndex = 0;
        std::int32_t              typeIndex  = 0;
        std::int32_t              dataIndex  = 0;
        std::vector<std::uint8_t> data;       // empty if dataIndex < 0 or unavailable
    };

    std::uint32_t TypeDefinitionCount() const noexcept;
    std::optional<TypeDefView> GetTypeDefinition(std::int32_t idx) const;

    // Linear search by namespace + name. Returns the first match or nullopt.
    std::optional<TypeDefView> FindTypeByName(const std::string& ns, const std::string& name) const;

    // Lists the fields belonging to a TypeDefinition (uses fieldStart + fieldCount).
    std::vector<FieldDefView> EnumerateTypeFields(const TypeDefView& type) const;

    // Returns the FieldDefault blob for a given global field index, or nullopt
    // if that field has no default. (FieldDefaults is sparse — most fields don't.)
    std::optional<FieldDefaultBlob> FindFieldDefault(std::int32_t globalFieldIndex) const;

    // Tries to decode the FieldDefault data at `dataIndex` as an
    // ECMA-335 compressed-length UTF-8 string. Returns std::nullopt when the
    // bytes don't look string-shaped (length out of range, content not mostly
    // printable). Call sites should treat success as "almost certainly a string
    // const"; numeric / enum const decoding requires the runtime types[] table
    // (segment 2d).
    std::optional<std::string> TryDecodeStringDefault(std::int32_t dataIndex) const;

    // Internal — invoked once by Get(). Public so the singleton helper in the
    // .cpp can construct/initialise without `friend` boilerplate.
    Parser() = default;

    /// <summary>
    /// In-process ctor: resolves the current process's
    /// <c>&lt;gameDir&gt;/il2cpp_data/Metadata/global-metadata.dat</c> via
    /// <see cref="BackendDetector"/> and loads it. Used by the DLL build.
    /// CLI builds (Phase 13) skip this and call <see cref="LoadFromPath"/>
    /// directly so they don't pull <c>BackendDetector</c> into the link.
    /// </summary>
    void Load();

    /// <summary>
    /// Load the parser from an explicit path on disk. Returns true on
    /// success (header validated, file cached). Also used by the offline
    /// <c>metadata-cli</c> tool which has no host process to detect.
    /// </summary>
    bool LoadFromPath(const std::wstring& path);

private:
    bool LoadHeader();
    bool LoadTables();

    // Returns sizeof(TypeDefinition) for the loaded metadata version.
    // For v38+ this is computed from the runtime-variable index sizes
    // (typeIndexSize, genericContainerIndexSize); for v24-v37 it's a fixed
    // compile-time constant. 0 for unsupported versions.
    std::size_t TypeDefSizeof() const noexcept;

    // v38+ row strides depend on runtime-variable typeIndexSize.
    // For v24-v37 these are fixed at 12 bytes (matches the legacy structs).
    std::size_t FieldDefSizeof() const noexcept;
    std::size_t FieldDefaultValueSizeof() const noexcept;

    bool                      loaded_  = false;
    std::int32_t              version_ = 0;
    std::vector<std::uint8_t> data_;  // owns the whole file

    // Cached prefix offsets (v29 layout, also valid for v24 / v27 / v31 /
    // v32-v37; for v38+ these are populated from the corresponding
    // Il2CppSectionMetadata entries by LoadHeader so the rest of the
    // parser can stay version-agnostic).
    std::int32_t stringLiteralOffset_     = 0;
    std::int32_t stringLiteralSize_       = 0;
    std::int32_t stringLiteralDataOffset_ = 0;
    std::int32_t stringLiteralDataSize_   = 0;
    std::int32_t stringOffset_            = 0;
    std::int32_t stringSize_              = 0;

    // Segment 2 — populated for v24 / v29 / v31 / v32-v39.
    // tablesLoaded_ goes true when the offsets are bound and the
    // version-specific TypeDefinition layout is known.
    bool         tablesLoaded_           = false;
    std::int32_t typeDefinitionsOffset_  = 0;
    std::int32_t typeDefinitionsSize_    = 0;
    std::int32_t fieldsOffset_           = 0;
    std::int32_t fieldsSize_             = 0;
    std::int32_t fieldDefaultsOffset_    = 0;
    std::int32_t fieldDefaultsSize_      = 0;
    std::int32_t fieldDefaultDataOffset_ = 0;
    std::int32_t fieldDefaultDataSize_   = 0;

    // For v38+ derive the variable-width metadata-index sizes at load time
    // from each table's row count (and the parameter row's TIS-inferred
    // size). v24-v37 leave the defaults of 4 bytes each. See
    // MetadataLayout.h §IndexSizes for the derivation.
    Metadata::IndexSizes indexSizes_;

    // Total parameters.count emitted by the v38 section header — the
    // parameter row stride is `8 + typeIndexSize`, so the row reader
    // (used by metadata-cli only today) needs this to walk the table.
    std::int64_t parametersCount_       = 0;

    // String-literal table entry size: 8 bytes for v24-v34 (length +
    // dataIndex pair), 4 bytes for v35+ (just dataIndex; length comes
    // from the delta to the next entry). Cached so StringLiteralCount
    // doesn't repeat the version check.
    std::int32_t stringLiteralEntryStride_ = 8;
};

}  // namespace UnityCEDumper::IL2CPP::Metadata
