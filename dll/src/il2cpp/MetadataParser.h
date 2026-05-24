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

    // For v24 metadata, returns the auto-detected sub-version label
    // ("v24.0" / "v24.1" / "v24" for v24.2-v24.5). Empty string
    // otherwise. Useful for logs / CLI diagnostics — Unity dropped
    // fields from Il2CppTypeDefinition over the 2018.x release cycle
    // without bumping the metadata version number, and the parser
    // probes the right stride at load time.
    std::string v24SubVersionLabel() const;

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
        // v0.2 — declaringTypeIndex (-1 for top-level types). Used
        // offline to walk the nested-type chain so labels carry the
        // full `Outer/Inner/Leaf` shape (matches the live UI's
        // il2cpp_class_get_declaring_type walk in IL2CPPClassWalker).
        std::int32_t  declaringTypeIndex = -1;
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

    struct MethodDefView {
        std::int32_t  index;             // global Method index
        std::string   name;
        std::int32_t  declaringTypeIndex = 0;  // TypeDefinition index (use GetTypeDefinition to resolve)
        std::int32_t  returnTypeIndex    = 0;  // Il2CppType index — opaque without binary parse (Phase D)
        std::int32_t  parameterStart     = 0;
        std::uint16_t parameterCount     = 0;
        std::uint32_t flags              = 0;
        std::uint32_t token              = 0;
    };

    struct ParameterDefView {
        std::int32_t  index;             // global Parameter index
        std::string   name;
        std::int32_t  typeIndex          = 0;  // Il2CppType index — opaque without binary parse
        std::uint32_t token              = 0;
    };

    // Phase D-5.1 — Image (assembly DLL) descriptor. Used offline to map
    // a global method index to its owning image so the symbol resolver
    // can look up codeGenModules[image_name].methodPointers[localIdx].
    struct ImageDefView {
        std::int32_t  index;             // image index in metadata Images table
        std::string   name;              // assembly name (without ".dll" suffix)
        std::int32_t  typeStart   = 0;   // first TypeDefinition belonging to this image
        std::uint32_t typeCount   = 0;
        std::uint32_t token       = 0;
        // First / last+1 global method indices belonging to this image —
        // computed by walking type.methodStart across the image's types.
        // 0 / 0 if the image has no methods.
        std::int32_t  methodStart = 0;
        std::int32_t  methodEnd   = 0;   // exclusive upper bound
    };

    struct FieldDefaultBlob {
        std::int32_t              fieldIndex = 0;
        std::int32_t              typeIndex  = 0;
        std::int32_t              dataIndex  = 0;
        std::vector<std::uint8_t> data;       // empty if dataIndex < 0 or unavailable
    };

    std::uint32_t TypeDefinitionCount() const noexcept;
    std::optional<TypeDefView> GetTypeDefinition(std::int32_t idx) const;

    // Phase B (Il2CppDumper feature parity): Method + Parameter enumeration.
    // Same version dispatch as TypeDefinition — v24.0 / v24.1 / v24.2-v24.5
    // / v29-v34 / v35-v37 share fixed strides; v38+ hand-walks based on
    // IndexSizes. Parameter type resolution stays opaque (the Il2CppType
    // table lives in the runtime binary, not metadata.dat — Phase D will
    // cross-link).
    std::uint32_t MethodDefinitionCount() const noexcept;
    std::optional<MethodDefView> GetMethodDefinition(std::int32_t idx) const;
    std::vector<MethodDefView>   EnumerateTypeMethods(const TypeDefView& type) const;

    std::uint32_t ParameterCount() const noexcept;
    std::optional<ParameterDefView> GetParameter(std::int32_t idx) const;
    std::vector<ParameterDefView>   EnumerateMethodParameters(const MethodDefView& method) const;

    // Phase D-5.1: Image table enumeration. Populated for v24.2+ (the
    // versions where the codeGenModules cross-link is viable); v24.0 /
    // v24.1 read at a wrong offset because rgctxEntriesOffset/Count
    // sits between typeDefinitions and images in those older headers
    // — those builds return ImageCount() == 0 by default (deferred).
    std::uint32_t ImageCount() const noexcept;
    std::optional<ImageDefView> GetImage(std::int32_t idx) const;
    std::vector<ImageDefView>   EnumerateImages() const;

    // Find the image whose [methodStart, methodEnd) range covers
    // `globalMethodIdx`. O(N) linear scan — fine for the typical
    // 20-100 images and one-time symbol-dump pipelines. Returns
    // std::nullopt on miss (method belongs to no image, or images
    // table not loaded).
    std::optional<ImageDefView> FindImageContainingMethod(std::int32_t globalMethodIdx) const;

    // Phase D v0.2.2 — child→parent TypeDefinition map for nested types.
    // The on-disk `declaringTypeIndex` field is actually a TypeIndex
    // (points into the runtime types[] table which lives in the binary,
    // not metadata.dat), so we can't follow it offline directly. Instead
    // we walk metadata's `nestedTypeIndices[]` table — the parent→child
    // direction is fully expressible in metadata. Build that map once
    // and look up the parent of a given child cheaply.
    //
    // Returns -1 when the type is top-level OR when the nestedTypes
    // table hasn't been populated (only available for v24.2+ via the
    // same gating as the Image table).
    std::int32_t GetDeclaringTypeIndex(std::int32_t typeDefIdx) const;

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

    // Post-LoadTables() sanity check: the first TypeDefinition in any
    // well-formed IL2CPP metadata is `<Module>` with IL token 0x02000001.
    // If our version dispatch picked the wrong stride (e.g. a Unity-2018.1
    // v24 build whose TypeDefinition layout differs from the 2019.x v24
    // we calibrated against), the name happens to still land at offset 0
    // but token drifts — so checking token guarantees alignment. Returns
    // true when the row looks sane, false when the stride is mis-matched.
    bool ValidateTypeTable();

    // v24 has 3 distinct strides (92 / 100 / 104 — see MetadataLayout.h
    // §TypeDefinition_v24*). Called from LoadTables() for v24 only;
    // probes which candidate stride lands TypeDef[0].token at the CLR
    // canonical 0x02000001, and latches the matching variant. If none
    // match (encrypted metadata, brand new sub-version) the variant
    // stays at v24.5 and ValidateTypeTable() will demote to strings-only.
    void DetectV24Variant();

    // Which v24 TypeDefinition layout to use. Latched by DetectV24Variant()
    // at load time. Defaults to v24.2-v24.5 (the most common shape).
    enum class V24Variant {
        V24_2_THROUGH_5 = 0,  // 92-byte stride; current TypeDefinition_v24
        V24_1,                // 100-byte stride; adds rgctxStart/rgctxCount
        V24_0,                // 104-byte stride; adds customAttributeIndex too
    };
    V24Variant v24Variant_ = V24Variant::V24_2_THROUGH_5;

    // Returns sizeof(TypeDefinition) for the loaded metadata version.
    // For v38+ this is computed from the runtime-variable index sizes
    // (typeIndexSize, genericContainerIndexSize); for v24-v37 it's a fixed
    // compile-time constant. 0 for unsupported versions.
    std::size_t TypeDefSizeof() const noexcept;

    // v38+ row strides depend on runtime-variable typeIndexSize.
    // For v24-v37 these are fixed at 12 bytes (matches the legacy structs).
    std::size_t FieldDefSizeof() const noexcept;
    std::size_t FieldDefaultValueSizeof() const noexcept;

    // Phase B — Method / Parameter row strides. Same dispatch story as
    // TypeDefSizeof / FieldDefSizeof: v24 sub-variants pick from
    // MethodDefinition_v24_0/_v24_1/_v29; v31-v37 use MethodDefinition_v31;
    // v38+ computes from IndexSizes (typeIndexSize / typeDefinitionIndexSize
    // / parameterIndexSize / genericContainerIndexSize). 0 for unsupported.
    std::size_t MethodDefSizeof() const noexcept;
    std::size_t ParameterDefSizeof() const noexcept;

    // Phase D-5.1 — Image table row stride. Fixed 40 bytes for v24.2-v37
    // (Min=24.1 customAttribute pair shipped by the time we care). v38+
    // has variable-width typeStart/exportedTypeStart (TDIS bytes each).
    std::size_t ImageDefSizeof() const noexcept;

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
    // Phase B: methods / parameters table offsets (both v29 flat header
    // and v38+ section header populate these in LoadTables).
    std::int32_t methodsOffset_          = 0;
    std::int32_t methodsSize_            = 0;
    std::int32_t parametersOffset_       = 0;
    std::int32_t parametersSize_         = 0;

    // Phase D-5.1: images table offsets. 0/0 for v24.0/v24.1 (deferred —
    // see ImageCount comment) and for any version where bounds checking
    // rejected the field. Populated for v24.2-v37 (flat header) and
    // v38+ (section header).
    std::int32_t imagesOffset_           = 0;
    std::int32_t imagesSize_             = 0;

    // Phase D v0.2.2: nestedTypeIndices[] table — flat array of int32
    // TypeDefinitionIndex values. Each TypeDef carries nestedTypesStart
    // + nested_type_count pointing into this table; the entries are the
    // global TypeDef indices of the children. Same gating as images
    // (deferred for v24.0/v24.1).
    std::int32_t nestedTypesOffset_      = 0;
    std::int32_t nestedTypesSize_        = 0;

    // Lazy-built child→parent map. -1 entries mean "top-level or not
    // yet built". Mutable so const accessors can populate it on demand.
    mutable std::vector<std::int32_t> declaringTypeMap_;
    mutable bool                       declaringTypeMapBuilt_ = false;

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
