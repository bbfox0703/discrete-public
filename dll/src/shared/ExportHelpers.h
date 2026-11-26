// ExportHelpers.h — pure, runtime-independent helpers shared by ExportAPI.cpp
// and the offline C++ self-test suite (dll/tests/dll_core_test.cpp).
#pragma once

#include "ManagedLayout.h"

#include <nlohmann/json.hpp>

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <type_traits>

namespace Discrete::ExportHelpers {

// Normalize a runtime field offset before emitting it to JSON (C-H1).
// IL2CPP encodes [ThreadStatic] fields with THREAD_STATIC_FIELD_OFFSET = -1
// inside std::size_t (18446744073709551615 on x64, 4294967295 on x86);
// Mono uses signed int (-1) for FieldEntry and std::size_t for FieldHit.
// Normalize any negative value or unsigned value exceeding INT32_MAX to -1
// so signed-integer JSON consumers never fail on [ThreadStatic] fields.
template <typename T>
inline std::int64_t NormalizeFieldOffset(T raw) noexcept {
    if constexpr (std::is_signed_v<T>) {
        if (raw < 0) return -1;
        if constexpr (sizeof(T) > 4) {
            if (static_cast<std::int64_t>(raw) > 0x7FFFFFFFLL) return -1;
        }
        return static_cast<std::int64_t>(raw);
    } else {
        if (raw > static_cast<T>(0x7FFFFFFFu)) return -1;
        return static_cast<std::int64_t>(raw);
    }
}

struct ArrayElementLayout {
    std::size_t elemSize  = 0;
    bool        isPointer = false;
};

// Resolve the slot stride and pointer classification for `walkArray` (C-H2).
// Value-type elements sit inline in the SZARRAY payload (stride = class_value_size
// or known primitive width); reference-type elements are ALWAYS pointer-sized
// slots (kPointerSize: 4 on x86, 8 on x64), never class_value_size.
inline ArrayElementLayout ResolveArrayElementLayout(
    std::size_t knownPrimitiveSize,
    bool        elemClassResolved,
    bool        elemIsValueType,
    int         classValueSize,
    bool        hasCallerElementSize,
    std::size_t callerElementSize,
    std::size_t pointerSize = ManagedLayout::kPointerSize) noexcept {
    std::size_t elemSize  = knownPrimitiveSize;
    bool        isPointer = false;

    if (elemClassResolved) {
        isPointer = !elemIsValueType;
        if (elemIsValueType) {
            if (classValueSize > 0) {
                elemSize = static_cast<std::size_t>(classValueSize);
            }
        } else {
            elemSize = pointerSize;
        }
    }

    if (elemSize == 0) {
        if (hasCallerElementSize && callerElementSize > 0) {
            elemSize = callerElementSize;
        } else {
            elemSize  = pointerSize;
            isPointer = true;
        }
    }

    return ArrayElementLayout{elemSize, isPointer};
}

// Shared IL2CPP FieldDefault blob decoder (C-H3): used by both
// `il2cppGetMetadataTypeFields` and `getEnumValues` so the two handlers never
// drift on metadata-version integer encoding or v29+ min/max sentinels.
//
// Per-version integer encoding (<=32-bit I1..U4):
//   * v24 / v27 (Unity 2018-2020): raw little-endian, exactly sizeof(T) bytes.
//   * v29+ (Unity 2021+): 0xFE (type_max) / 0xFF (type_min) sentinels plus
//     ECMA-335 §II.23.2 compressed signed int (1/2/4 bytes).
// I8 / U8 / R4 / R8 / Boolean / Char are raw little-endian across all versions.
inline bool DecodeCompressedSignedInt(const std::uint8_t* p, std::size_t avail,
                                      std::int64_t& outValue,
                                      std::size_t&  outBytes) noexcept {
    if (avail < 1) return false;
    std::uint8_t b0 = p[0];
    std::uint32_t enc = 0;
    std::size_t w = 0;
    std::int64_t bias = 0;
    if ((b0 & 0x80u) == 0u) {
        enc = b0; w = 1; bias = 0x40;
    } else if ((b0 & 0xC0u) == 0x80u) {
        if (avail < 2) return false;
        enc = (static_cast<std::uint32_t>(b0 & 0x3Fu) << 8) | p[1];
        w = 2; bias = 0x2000;
    } else if ((b0 & 0xE0u) == 0xC0u) {
        if (avail < 4) return false;
        enc = (static_cast<std::uint32_t>(b0 & 0x1Fu) << 24)
            | (static_cast<std::uint32_t>(p[1])       << 16)
            | (static_cast<std::uint32_t>(p[2])       <<  8)
            |  static_cast<std::uint32_t>(p[3]);
        w = 4; bias = 0x10000000;
    } else {
        return false;  // 0xE0..0xFF reserved (0xFE/0xFF handled as sentinels)
    }
    std::int64_t mag = static_cast<std::int64_t>(enc >> 1);
    outValue = (enc & 1u) ? (mag - bias) : mag;
    outBytes = w;
    return true;
}

inline std::uint8_t ClrTypeNameToIl2CppTag(const std::string& typeName) noexcept {
    if (typeName == "System.Boolean") return 0x02;
    if (typeName == "System.Char")    return 0x03;
    if (typeName == "System.SByte")   return 0x04;
    if (typeName == "System.Byte")    return 0x05;
    if (typeName == "System.Int16")   return 0x06;
    if (typeName == "System.UInt16")  return 0x07;
    if (typeName == "System.Int32")   return 0x08;
    if (typeName == "System.UInt32")  return 0x09;
    if (typeName == "System.Int64")   return 0x0A;
    if (typeName == "System.UInt64")  return 0x0B;
    if (typeName == "System.Single")  return 0x0C;
    if (typeName == "System.Double")  return 0x0D;
    return 0;
}

inline bool DecodeIl2CppFieldDefaultByTag(std::int32_t metadataVersion,
                                          std::uint8_t tag,
                                          const std::uint8_t* p,
                                          std::size_t avail,
                                          nlohmann::json& out) {
    const bool useRawLE = (metadataVersion <= 27);
    auto cmp = [&](auto&& cast1, auto&& cast2,
                   std::int64_t maxVal, std::int64_t minVal,
                   std::size_t typeSize) -> bool {
        if (useRawLE) {
            if (avail < typeSize) return false;
            std::int64_t raw = 0;
            std::memcpy(&raw, p, typeSize);
            out["value"] = cast2(cast1(raw));
            out["enc_bytes"] = typeSize;
            out["encoding"] = "raw_le";
            return true;
        }
        if (avail >= 1) {
            if (p[0] == 0xFE) {
                out["value"] = cast2(cast1(maxVal));
                out["enc_bytes"] = 1;
                out["sentinel"] = "type_max";
                out["encoding"] = "sentinel_compressed";
                return true;
            }
            if (p[0] == 0xFF) {
                out["value"] = cast2(cast1(minVal));
                out["enc_bytes"] = 1;
                out["sentinel"] = "type_min";
                out["encoding"] = "sentinel_compressed";
                return true;
            }
        }
        std::int64_t raw = 0;
        std::size_t w = 0;
        if (!DecodeCompressedSignedInt(p, avail, raw, w)) return false;
        out["value"] = cast2(cast1(raw));
        out["enc_bytes"] = w;
        out["encoding"] = "sentinel_compressed";
        return true;
    };
    switch (tag) {
        case 0x02:  // Boolean
            if (avail < 1) return false;
            out["value"] = (p[0] != 0);
            out["enc_bytes"] = 1;
            return true;
        case 0x03:  // Char
            if (avail < 2) return false;
            {
                std::uint16_t v = 0;
                std::memcpy(&v, p, 2);
                out["value"] = v;
                out["enc_bytes"] = 2;
            }
            return true;
        case 0x04:  // I1
            return cmp([](std::int64_t x){ return static_cast<std::int8_t>(x); },
                       [](std::int8_t x){ return static_cast<std::int64_t>(x); },
                       0x7F, -0x80, 1);
        case 0x05:  // U1
            return cmp([](std::int64_t x){ return static_cast<std::uint8_t>(x); },
                       [](std::uint8_t x){ return static_cast<std::uint64_t>(x); },
                       0xFF, 0, 1);
        case 0x06:  // I2
            return cmp([](std::int64_t x){ return static_cast<std::int16_t>(x); },
                       [](std::int16_t x){ return static_cast<std::int64_t>(x); },
                       0x7FFF, -0x8000, 2);
        case 0x07:  // U2
            return cmp([](std::int64_t x){ return static_cast<std::uint16_t>(x); },
                       [](std::uint16_t x){ return static_cast<std::uint64_t>(x); },
                       0xFFFF, 0, 2);
        case 0x08:  // I4
            return cmp([](std::int64_t x){ return static_cast<std::int32_t>(x); },
                       [](std::int32_t x){ return static_cast<std::int64_t>(x); },
                       0x7FFFFFFFLL, -0x80000000LL, 4);
        case 0x09:  // U4
            return cmp([](std::int64_t x){ return static_cast<std::uint32_t>(x); },
                       [](std::uint32_t x){ return static_cast<std::uint64_t>(x); },
                       0xFFFFFFFFLL, 0, 4);
        case 0x0A:  // I8
            if (avail < 8) return false;
            {
                std::int64_t v = 0;
                std::memcpy(&v, p, 8);
                out["value"] = v;
                out["enc_bytes"] = 8;
            }
            return true;
        case 0x0B:  // U8
            if (avail < 8) return false;
            {
                std::uint64_t v = 0;
                std::memcpy(&v, p, 8);
                out["value"] = v;
                out["enc_bytes"] = 8;
            }
            return true;
        case 0x0C:  // R4
            if (avail < 4) return false;
            {
                float v = 0.f;
                std::memcpy(&v, p, 4);
                out["enc_bytes"] = 4;
                if (std::isnan(v)) {
                    out["value"] = nullptr;
                    out["value_kind"] = "nan";
                } else if (std::isinf(v)) {
                    out["value"] = nullptr;
                    out["value_kind"] = (v > 0.f) ? "infinity" : "-infinity";
                } else {
                    out["value"] = v;
                    out["value_kind"] = "normal";
                }
            }
            return true;
        case 0x0D:  // R8
            if (avail < 8) return false;
            {
                double v = 0.0;
                std::memcpy(&v, p, 8);
                out["enc_bytes"] = 8;
                if (std::isnan(v)) {
                    out["value"] = nullptr;
                    out["value_kind"] = "nan";
                } else if (std::isinf(v)) {
                    out["value"] = nullptr;
                    out["value_kind"] = (v > 0.0) ? "infinity" : "-infinity";
                } else {
                    out["value"] = v;
                    out["value_kind"] = "normal";
                }
            }
            return true;
        case 0x0E:  // String — handled separately via TryDecodeStringDefault
            return false;
        default:
            return false;
    }
}

}  // namespace Discrete::ExportHelpers
