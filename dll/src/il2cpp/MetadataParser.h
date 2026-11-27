// MetadataParser.h — opens global-metadata.dat for the host game and exposes
// string-literal / string-heap queries. Cross-link to runtime Il2CppClass*
// lands in a follow-up commit.
//
// Phase R1: the parser is implemented in Rust (dll/rust, src/metadata/) and
// MetadataParserRs.cpp adapts it to this class. The legacy C++
// implementation (MetadataParser.cpp) was removed when R1 closed; it is in
// git history (last in 5748299).
#pragma once

#include "MetadataLayout.h"

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

// Opaque Rust handle (dll/rust/include/discrete_core.h); forward-declared so
// consumers of this header don't need the Rust include path.
struct DcMetadata;

namespace Discrete::IL2CPP::Metadata {

class Parser {
public:
    // Lazily-initialised singleton. First call probes the host process's
    // <gameDataDir>/il2cpp_data/Metadata/global-metadata.dat, validates magic,
    // and maps/caches the file. Retries on subsequent calls if an earlier load
    // failed; once loaded, steady-state calls are lock-free O(1) (D-L4).
    static const Parser& Get();

    bool         loaded() const noexcept  { return loaded_; }
    bool         tablesDegraded() const noexcept;
    bool         typeTablesSupported() const noexcept;
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
        std::uint32_t flags        = 0;   // TypeAttributes (public/sealed/abstract/...)
        // Il2CppTypeDefinition::bitfield — a *different* field from `flags`.
        // Bit 0 = valuetype, bit 1 = enumtype (see upstream comment in
        // MetadataClass.cs). Needed offline because a value type's field
        // offsets include the object header and must be adjusted.
        std::uint32_t bitfield     = 0;
        std::uint32_t token        = 0;

        bool IsValueType() const { return (bitfield & 0x1u) != 0; }
        bool IsEnum()      const { return (bitfield & 0x2u) != 0; }
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

    // Phase R1: the implementation lives in the Rust core (dll/rust,
    // dll/src/il2cpp/MetadataParserRs.cpp is the wrapper). The object owns a
    // Rust handle, so it is move-only; Get()'s singleton lambda returns by
    // move.
    ~Parser();
    Parser(Parser&& other) noexcept;
    Parser& operator=(Parser&& other) noexcept;
    Parser(const Parser&)            = delete;
    Parser& operator=(const Parser&) = delete;

private:
    // Cached from the handle after each load so the inline accessors above
    // stay inline.
    bool          loaded_  = false;
    std::int32_t  version_ = 0;
    ::DcMetadata* rs_      = nullptr;
};

}  // namespace Discrete::IL2CPP::Metadata
