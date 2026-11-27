// ExportHelpers.h — pure, runtime-independent helpers shared by ExportAPI.cpp
// and the offline C++ self-test suite (dll/tests/dll_core_test.cpp).
#pragma once

#include "ManagedLayout.h"

#include <nlohmann/json.hpp>

#include <charconv>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string>
#include <string_view>
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

// C-M6: normalize per-assembly cap into global `truncated` vs multi-assembly
// `truncatedAssemblies` matching the SearchSymbolsResult DTO contract:
// single-assembly mode sets `truncated = true` (leaving `truncatedAssemblies`
// empty), while multi-assembly mode records the capped assembly name in
// `truncatedAssemblies` so other assemblies can still be scanned.
inline void RecordSearchSymbolsAsmCap(bool multiAsm,
                                      bool thisAsmCapped,
                                      const std::string& assemblyName,
                                      bool& truncated,
                                      std::vector<std::string>& truncatedAssemblies) {
    if (!thisAsmCapped) return;
    if (multiAsm) {
        truncatedAssemblies.push_back(assemblyName);
    } else {
        truncated = true;
    }
}

// C-M8: strict JSON numeric-value parsers for value/group scans.
// Rejects non-finite doubles (NaN/Inf), fractional floats for integer scans
// (3.7 will NOT silently truncate to 3), out-of-range doubles (avoiding C++
// float-to-int UB), trailing garbage in strings ("3.7", "12abc"), negative
// strings for unsigned targets ("-1"), and >INT64_MAX unsigned values for
// signed targets. Preserves full 64-bit integer precision.
inline std::string_view TrimAsciiWs(std::string_view s) noexcept {
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t' || s.front() == '\r' || s.front() == '\n')) {
        s.remove_prefix(1);
    }
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t' || s.back() == '\r' || s.back() == '\n')) {
        s.remove_suffix(1);
    }
    return s;
}

inline bool ParseStrictInt64FromDouble(double d, std::int64_t& out) noexcept {
    if (!std::isfinite(d)) return false;
    if (std::trunc(d) != d) return false;
    // Exact IEEE-754 bounds: -2^63 is representable; +2^63 is strictly > INT64_MAX.
    if (d < -9223372036854775808.0 || d >= 9223372036854775808.0) return false;
    out = static_cast<std::int64_t>(d);
    return true;
}

inline bool ParseStrictUInt64FromDouble(double d, std::uint64_t& out) noexcept {
    if (!std::isfinite(d)) return false;
    if (d < 0.0) return false;
    if (std::trunc(d) != d) return false;
    // Exact IEEE-754 upper bound: 2^64 is strictly > UINT64_MAX.
    if (d >= 18446744073709551616.0) return false;
    out = static_cast<std::uint64_t>(d);
    return true;
}

inline bool ParseStrictInt64FromString(std::string_view raw, std::int64_t& out) noexcept {
    std::string_view s = TrimAsciiWs(raw);
    if (s.empty()) return false;
    bool neg = false;
    if (s.front() == '+') {
        s.remove_prefix(1);
    } else if (s.front() == '-') {
        neg = true;
        s.remove_prefix(1);
    }
    if (s.empty()) return false;
    int base = 10;
    if (s.size() > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) {
        base = 16;
        s.remove_prefix(2);
    }
    if (s.empty()) return false;
    std::uint64_t mag = 0;
    const char* first = s.data();
    const char* last  = s.data() + s.size();
    auto res = std::from_chars(first, last, mag, base);
    if (res.ec != std::errc{} || res.ptr != last) return false;
    if (neg) {
        constexpr std::uint64_t kAbsMin = static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()) + 1ull;
        if (mag > kAbsMin) return false;
        if (mag == kAbsMin) {
            out = std::numeric_limits<std::int64_t>::min();
        } else {
            out = -static_cast<std::int64_t>(mag);
        }
        return true;
    }
    if (mag > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max())) return false;
    out = static_cast<std::int64_t>(mag);
    return true;
}

inline bool ParseStrictUInt64FromString(std::string_view raw, std::uint64_t& out) noexcept {
    std::string_view s = TrimAsciiWs(raw);
    if (s.empty()) return false;
    if (s.front() == '+') s.remove_prefix(1);
    if (s.empty() || s.front() == '-') return false;
    int base = 10;
    if (s.size() > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) {
        base = 16;
        s.remove_prefix(2);
    }
    if (s.empty()) return false;
    std::uint64_t val = 0;
    const char* first = s.data();
    const char* last  = s.data() + s.size();
    auto res = std::from_chars(first, last, val, base);
    if (res.ec != std::errc{} || res.ptr != last) return false;
    out = val;
    return true;
}

inline bool ParseStrictJsonInt64(const nlohmann::json& v, std::int64_t& out) noexcept {
    if (v.is_number_unsigned()) {
        std::uint64_t u = v.get<std::uint64_t>();
        if (u > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max())) return false;
        out = static_cast<std::int64_t>(u);
        return true;
    }
    if (v.is_number_integer()) {
        out = v.get<std::int64_t>();
        return true;
    }
    if (v.is_number_float()) {
        return ParseStrictInt64FromDouble(v.get<double>(), out);
    }
    if (v.is_string()) {
        return ParseStrictInt64FromString(v.get_ref<const std::string&>(), out);
    }
    return false;
}

inline bool ParseStrictJsonUInt64(const nlohmann::json& v, std::uint64_t& out) noexcept {
    if (v.is_number_unsigned()) {
        out = v.get<std::uint64_t>();
        return true;
    }
    if (v.is_number_integer()) {
        std::int64_t s = v.get<std::int64_t>();
        if (s < 0) return false;
        out = static_cast<std::uint64_t>(s);
        return true;
    }
    if (v.is_number_float()) {
        return ParseStrictUInt64FromDouble(v.get<double>(), out);
    }
    if (v.is_string()) {
        return ParseStrictUInt64FromString(v.get_ref<const std::string&>(), out);
    }
    return false;
}

inline bool ParseStrictJsonDouble(const nlohmann::json& v, double& out) noexcept {
    if (v.is_number()) {
        double d = v.get<double>();
        if (!std::isfinite(d)) return false;
        out = d;
        return true;
    }
    if (v.is_string()) {
        std::string_view s = TrimAsciiWs(v.get_ref<const std::string&>());
        if (s.empty()) return false;
        double d = 0.0;
        const char* first = s.data();
        const char* last  = s.data() + s.size();
        auto res = std::from_chars(first, last, d);
        if (res.ec != std::errc{} || res.ptr != last || !std::isfinite(d)) return false;
        out = d;
        return true;
    }
    return false;
}

}  // namespace Discrete::ExportHelpers
