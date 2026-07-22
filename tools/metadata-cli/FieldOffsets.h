// FieldOffsets.h — offline field offsets / field attributes / instance sizes.
//
// `global-metadata.dat` does NOT contain field offsets. Il2CppFieldDefinition
// is exactly {nameIndex, typeIndex, token}. The three things a dumper actually
// wants all live in `GameAssembly.dll`, hanging off the
// `Il2CppMetadataRegistration` struct that RegistrationFinder already locates:
//
//   offset        fieldOffsets[typeDefIndex][fieldIndexInType]   (+0x50 / +0x58)
//   is_static     types[field.typeIndex] -> Il2CppType.attrs     (+0x30 / +0x38)
//   instance_size typeDefinitionsSizes[typeDefIndex]             (+0x60 / +0x68)
//
// This module reads them straight out of the PE file bytes — no injection, no
// LoadLibraryEx — translating VA -> RVA -> file offset through the section
// table, exactly like the rest of the Phase D offline pipeline.
//
// Two things bite anyone implementing this from the struct definition alone:
//
//  1. `fieldOffsets` is NOT always an array of pointers. Upstream Il2CppDumper
//     carries a `fieldOffsetsArePointers` flag: nominally true for metadata
//     version > 21, but v21 needs a probe because some builds store the
//     offsets inline as plain uint32 indexed by the GLOBAL field index instead.
//     Dereferencing the wrong mode yields plausible-looking garbage rather
//     than an obvious failure, so this module probes and cross-checks instead
//     of trusting the version alone.
//
//  2. Raw offsets are not final. For an instance field of a VALUE TYPE the
//     stored offset includes the boxed object header and must have one header
//     subtracted (16 on x64, 8 on x86). Static fields index a different block
//     entirely and are never adjusted.
//
// Everything degrades to std::nullopt rather than guessing: callers emit
// `null` so a consumer can tell "unknown" from "zero".

#pragma once

#include "PEParser.h"
#include "RegistrationFinder.h"

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace Discrete::Phase_D::Binary {

// IL2CPP field attribute bits (ECMA-335 FieldAttributes, as used by IL2CPP).
constexpr std::uint16_t kFieldAttrFieldAccessMask = 0x0007;
constexpr std::uint16_t kFieldAttrStatic          = 0x0010;
constexpr std::uint16_t kFieldAttrInitOnly        = 0x0020;
constexpr std::uint16_t kFieldAttrLiteral         = 0x0040;

// How the fieldOffsets table stores its entries.
enum class OffsetMode {
    Unknown,
    Pointers,   // const int32_t** — entry per type, pointing at that type's offsets
    Values,     // const uint32_t* — flat, indexed by global field index
};

class TypeInfo {
public:
    // Bind to a parsed PE + a located Il2CppMetadataRegistration.
    // `metadataVersion` comes from the metadata parser and seeds the storage
    // -mode guess before it is verified against the actual bytes.
    // Returns nullopt only when nothing usable could be bound; `errOut` gets a
    // one-line reason.
    static std::optional<TypeInfo> Bind(const std::uint8_t*         bytes,
                                        std::size_t                 size,
                                        const PE::Image&            img,
                                        const Registration::Found&  reg,
                                        int                         metadataVersion,
                                        std::string*                errOut = nullptr);

    // Il2CppType.attrs for an index into the runtime types[] array. A field's
    // `typeIndex` from metadata indexes this same array.
    std::optional<std::uint16_t> TypeAttrs(std::int32_t typeIndex) const;

    // Il2CppType.type — the IL2CPP_TYPE_* tag (I4, String, Class, ...).
    std::optional<std::uint8_t>  TypeTag(std::int32_t typeIndex) const;

    bool IsStaticField(std::int32_t fieldTypeIndex) const;

    // The finished, adjusted field offset. `fieldIndexInType` is the field's
    // position within its declaring type (globalFieldIndex - type.fieldStart).
    std::optional<std::int32_t> FieldOffset(std::int32_t typeDefIndex,
                                            std::int32_t fieldIndexInType,
                                            std::int32_t globalFieldIndex,
                                            bool         isValueType,
                                            bool         isStatic) const;

    // Il2CppTypeDefinitionSizes::instance_size for a TypeDefinition index.
    std::optional<std::uint32_t> InstanceSize(std::int32_t typeDefIndex) const;

    OffsetMode  offsetMode()  const { return mode_; }
    const char* offsetModeName() const;
    bool        hasOffsets()  const { return mode_ != OffsetMode::Unknown; }
    bool        hasTypes()    const { return typesCount_ > 0; }
    bool        hasSizes()    const { return sizesCount_ > 0; }
    bool        is64Bit()     const { return is64_; }

private:
    TypeInfo() = default;

    // attrs and the type tag are two slices of one dword — read it once.
    std::optional<std::uint32_t> TypeBits(std::int32_t typeIndex) const;

    // Read helpers over the mapped file image.
    std::optional<std::size_t> VaToFileOffset(std::uint64_t va) const;
    bool ReadU32(std::uint64_t va, std::uint32_t* out) const;
    bool ReadI32(std::uint64_t va, std::int32_t*  out) const;
    bool ReadPtr(std::uint64_t va, std::uint64_t* out) const;

    // Decide Pointers vs Values by inspecting the actual table.
    OffsetMode ProbeOffsetMode(int metadataVersion) const;

    const std::uint8_t* bytes_ = nullptr;
    std::size_t         size_  = 0;
    const PE::Image*    img_   = nullptr;

    std::uint64_t typesVa_       = 0;
    std::uint32_t typesCount_    = 0;
    std::uint64_t offsetsVa_     = 0;
    std::uint32_t offsetsCount_  = 0;
    std::uint64_t sizesVa_       = 0;
    std::uint32_t sizesCount_    = 0;

    bool          is64_          = true;
    OffsetMode    mode_          = OffsetMode::Unknown;
};

// Human-readable IL2CPP_TYPE_* name for a tag byte ("I4", "String", ...).
// Returns nullptr for tags outside the known set.
const char* TypeTagName(std::uint8_t tag);

}  // namespace Discrete::Phase_D::Binary
