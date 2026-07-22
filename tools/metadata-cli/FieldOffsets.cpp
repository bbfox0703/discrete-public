// FieldOffsets.cpp — see FieldOffsets.h for the contract and the two traps.

#include "FieldOffsets.h"

#include <cstring>

namespace Discrete::Phase_D::Binary {

namespace {

// Il2CppType (x64): 8-byte data union, then a 4-byte bitfield.
//   attrs : 16   <- ECMA field/param attributes
//   type  :  8   <- IL2CPP_TYPE_* tag
//   ...
constexpr std::size_t kTypeBitsOffset = 8;

// Il2CppTypeDefinitionSizes { uint32 instance_size; int32 native_size;
//                             uint32 static_fields_size;
//                             uint32 thread_static_fields_size; }
constexpr std::size_t kInstanceSizeOffset = 0;

}  // namespace

const char* TypeTagName(std::uint8_t tag) {
    switch (tag) {
        case 0x01: return "Void";
        case 0x02: return "Boolean";
        case 0x03: return "Char";
        case 0x04: return "SByte";
        case 0x05: return "Byte";
        case 0x06: return "Int16";
        case 0x07: return "UInt16";
        case 0x08: return "Int32";
        case 0x09: return "UInt32";
        case 0x0A: return "Int64";
        case 0x0B: return "UInt64";
        case 0x0C: return "Single";
        case 0x0D: return "Double";
        case 0x0E: return "String";
        case 0x0F: return "Ptr";
        case 0x10: return "ByRef";
        case 0x11: return "ValueType";
        case 0x12: return "Class";
        case 0x13: return "Var";
        case 0x14: return "Array";
        case 0x15: return "GenericInst";
        case 0x16: return "TypedByRef";
        case 0x18: return "IntPtr";
        case 0x19: return "UIntPtr";
        case 0x1B: return "FnPtr";
        case 0x1C: return "Object";
        case 0x1D: return "SzArray";
        case 0x1E: return "MVar";
        default:   return nullptr;
    }
}

const char* TypeInfo::offsetModeName() const {
    switch (mode_) {
        case OffsetMode::Pointers: return "pointers";
        case OffsetMode::Values:   return "values";
        default:                   return "unknown";
    }
}

std::optional<std::size_t> TypeInfo::VaToFileOffset(std::uint64_t va) const {
    if (img_ == nullptr || va < img_->imageBase) return std::nullopt;
    auto off = img_->RvaToFileOffset(va - img_->imageBase);
    if (!off) return std::nullopt;
    return static_cast<std::size_t>(*off);
}

bool TypeInfo::ReadU32(std::uint64_t va, std::uint32_t* out) const {
    auto off = VaToFileOffset(va);
    if (!off || *off + 4 > size_) return false;
    std::memcpy(out, bytes_ + *off, 4);
    return true;
}

bool TypeInfo::ReadI32(std::uint64_t va, std::int32_t* out) const {
    std::uint32_t u = 0;
    if (!ReadU32(va, &u)) return false;
    std::memcpy(out, &u, 4);
    return true;
}

bool TypeInfo::ReadPtr(std::uint64_t va, std::uint64_t* out) const {
    auto off = VaToFileOffset(va);
    std::size_t width = is64_ ? 8u : 4u;
    if (!off || *off + width > size_) return false;
    if (is64_) {
        std::memcpy(out, bytes_ + *off, 8);
    } else {
        std::uint32_t u = 0;
        std::memcpy(&u, bytes_ + *off, 4);
        *out = u;
    }
    return true;
}

// Distinguishing Pointers from Values matters because a wrong guess produces
// plausible garbage, not an error. Version is only the starting hypothesis;
// what settles it is whether the leading entries actually behave like VAs that
// resolve inside this image.
OffsetMode TypeInfo::ProbeOffsetMode(int metadataVersion) const {
    if (offsetsVa_ == 0 || offsetsCount_ == 0) return OffsetMode::Unknown;

    std::size_t ptrWidth = is64_ ? 8u : 4u;
    std::size_t probeN   = (offsetsCount_ < 16u) ? offsetsCount_ : 16u;

    // Count how many of the first entries look like a resolvable pointer.
    // Null entries are legitimate (a type with no fields, or one whose offsets
    // the linker dropped), so they neither confirm nor deny.
    int plausiblePtr = 0, nonNull = 0;
    for (std::size_t i = 0; i < probeN; ++i) {
        std::uint64_t entry = 0;
        if (!ReadPtr(offsetsVa_ + static_cast<std::uint64_t>(i) * ptrWidth, &entry)) break;
        if (entry == 0) continue;
        ++nonNull;
        if (VaToFileOffset(entry)) ++plausiblePtr;
    }

    // Upstream Il2CppDumper: pointers for version > 21; v21 probes because
    // some builds of that vintage inline the values. We keep the version as
    // the prior but let the bytes overrule it either way.
    bool versionSaysPointers = metadataVersion > 21;

    if (nonNull > 0) {
        // Every non-null entry resolving as a VA is decisive.
        if (plausiblePtr == nonNull) return OffsetMode::Pointers;
        // None resolving means these are not addresses at all.
        if (plausiblePtr == 0)       return OffsetMode::Values;
    }
    return versionSaysPointers ? OffsetMode::Pointers : OffsetMode::Values;
}

std::optional<TypeInfo> TypeInfo::Bind(const std::uint8_t*        bytes,
                                       std::size_t                size,
                                       const PE::Image&           img,
                                       const Registration::Found& reg,
                                       int                        metadataVersion,
                                       std::string*               errOut) {
    TypeInfo t;
    t.bytes_        = bytes;
    t.size_         = size;
    t.img_          = &img;
    t.is64_         = img.is64Bit;
    t.typesVa_      = reg.typesArrayVa;
    t.typesCount_   = reg.typesCount;
    t.offsetsVa_    = reg.fieldOffsetsVa;
    t.offsetsCount_ = reg.fieldOffsetsCount;
    t.sizesVa_      = reg.typeDefSizesVa;
    t.sizesCount_   = reg.typeDefSizesCount;

    // Sanity-gate each table independently — a target may have a usable
    // types[] but an unusable fieldOffsets[], and partial enrichment beats
    // none. Absurd counts mean we latched onto the wrong window.
    constexpr std::uint32_t kSaneMax = 4'000'000u;
    if (t.typesCount_   > kSaneMax || !t.VaToFileOffset(t.typesVa_))   { t.typesCount_   = 0; }
    if (t.offsetsCount_ > kSaneMax || !t.VaToFileOffset(t.offsetsVa_)) { t.offsetsCount_ = 0; }
    if (t.sizesCount_   > kSaneMax || !t.VaToFileOffset(t.sizesVa_))   { t.sizesCount_   = 0; }

    t.mode_ = t.ProbeOffsetMode(metadataVersion);

    if (t.typesCount_ == 0 && t.mode_ == OffsetMode::Unknown && t.sizesCount_ == 0) {
        if (errOut) {
            *errOut = "Il2CppMetadataRegistration found, but none of types[] / "
                      "fieldOffsets[] / typeDefinitionsSizes[] resolved inside the image";
        }
        return std::nullopt;
    }
    return t;
}

// attrs and the type tag are two slices of one dword — read it once.
std::optional<std::uint32_t> TypeInfo::TypeBits(std::int32_t typeIndex) const {
    if (typeIndex < 0 || static_cast<std::uint32_t>(typeIndex) >= typesCount_) return std::nullopt;
    std::size_t ptrWidth = is64_ ? 8u : 4u;
    std::uint64_t typePtr = 0;
    if (!ReadPtr(typesVa_ + static_cast<std::uint64_t>(typeIndex) * ptrWidth, &typePtr)) return std::nullopt;
    if (typePtr == 0) return std::nullopt;
    std::uint32_t bits = 0;
    if (!ReadU32(typePtr + kTypeBitsOffset, &bits)) return std::nullopt;
    return bits;
}

std::optional<std::uint16_t> TypeInfo::TypeAttrs(std::int32_t typeIndex) const {
    auto bits = TypeBits(typeIndex);
    if (!bits) return std::nullopt;
    return static_cast<std::uint16_t>(*bits & 0xFFFFu);
}

std::optional<std::uint8_t> TypeInfo::TypeTag(std::int32_t typeIndex) const {
    auto bits = TypeBits(typeIndex);
    if (!bits) return std::nullopt;
    return static_cast<std::uint8_t>((*bits >> 16) & 0xFFu);
}

bool TypeInfo::IsStaticField(std::int32_t fieldTypeIndex) const {
    auto attrs = TypeAttrs(fieldTypeIndex);
    return attrs && (*attrs & kFieldAttrStatic) != 0;
}

std::optional<std::int32_t> TypeInfo::FieldOffset(std::int32_t typeDefIndex,
                                                  std::int32_t fieldIndexInType,
                                                  std::int32_t globalFieldIndex,
                                                  bool         isValueType,
                                                  bool         isStatic) const {
    if (mode_ == OffsetMode::Unknown) return std::nullopt;
    if (fieldIndexInType < 0) return std::nullopt;

    // A type that was never laid out — an interface, an open generic
    // definition, or one the linker stripped — reports instance_size 0 and
    // carries an all-zero fieldOffsets row. That is "no layout", not
    // "offset 0", and reporting 0 would be indistinguishable from a real
    // first field. Statics are exempt: they index a separate block that
    // instance_size does not describe.
    if (!isStatic) {
        if (auto size = InstanceSize(typeDefIndex); size && *size == 0) {
            return std::nullopt;
        }
    }

    std::int32_t offset = -1;

    if (mode_ == OffsetMode::Pointers) {
        if (typeDefIndex < 0 || static_cast<std::uint32_t>(typeDefIndex) >= offsetsCount_) {
            return std::nullopt;
        }
        std::size_t ptrWidth = is64_ ? 8u : 4u;
        std::uint64_t rowVa = 0;
        if (!ReadPtr(offsetsVa_ + static_cast<std::uint64_t>(typeDefIndex) * ptrWidth, &rowVa)) {
            return std::nullopt;
        }
        // A null row is normal: the type contributes no laid-out fields.
        if (rowVa == 0) return std::nullopt;
        if (!ReadI32(rowVa + 4ull * static_cast<std::uint64_t>(fieldIndexInType), &offset)) {
            return std::nullopt;
        }
    } else {
        // Flat table indexed by the GLOBAL field index, not the per-type one.
        if (globalFieldIndex < 0 ||
            static_cast<std::uint32_t>(globalFieldIndex) >= offsetsCount_) {
            return std::nullopt;
        }
        std::uint32_t raw = 0;
        if (!ReadU32(offsetsVa_ + 4ull * static_cast<std::uint64_t>(globalFieldIndex), &raw)) {
            return std::nullopt;
        }
        offset = static_cast<std::int32_t>(raw);
    }

    // Upstream treats <= 0 as "no usable offset" (-1 is the explicit sentinel
    // for a field the layout engine skipped). Report it as unknown rather than
    // as a real offset of 0, which would be indistinguishable from a genuine
    // first-field-of-a-value-type.
    if (offset < 0) return std::nullopt;

    const std::int32_t header = is64_ ? 16 : 8;

    // An instance field of a REFERENCE type physically cannot sit inside the
    // object header (klass pointer + monitor). Anything that low is a row we
    // misread or a type without a real layout — report unknown rather than a
    // number a caller would happily poke memory with. Upstream passes such
    // values through unchecked; we don't.
    if (!isStatic && !isValueType && offset < header) return std::nullopt;

    // A value type's stored offset includes the boxed object header; the
    // header comes off for instance fields only. Matches Il2CppDumper's
    // GetFieldOffsetFromIndex.
    if (offset > 0 && isValueType && !isStatic) {
        offset -= header;
    }
    return offset;
}

std::optional<std::uint32_t> TypeInfo::InstanceSize(std::int32_t typeDefIndex) const {
    if (typeDefIndex < 0 || static_cast<std::uint32_t>(typeDefIndex) >= sizesCount_) {
        return std::nullopt;
    }
    std::size_t ptrWidth = is64_ ? 8u : 4u;
    std::uint64_t rowVa = 0;
    if (!ReadPtr(sizesVa_ + static_cast<std::uint64_t>(typeDefIndex) * ptrWidth, &rowVa)) {
        return std::nullopt;
    }
    if (rowVa == 0) return std::nullopt;
    std::uint32_t sz = 0;
    if (!ReadU32(rowVa + kInstanceSizeOffset, &sz)) return std::nullopt;
    return sz;
}

}  // namespace Discrete::Phase_D::Binary
