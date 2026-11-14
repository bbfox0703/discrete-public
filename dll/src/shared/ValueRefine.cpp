// ValueRefine.cpp — legacy C++ implementation of the refine comparator (see
// ValueRefine.h). Compiled only when DISCRETE_RUST_R2 is OFF, and always
// into the R2 differential harness's legacy build. Body moved verbatim from
// ExportAPI.cpp in Phase R2.

#include "ValueRefine.h"

#include <cmath>
#include <cstring>

namespace Discrete::ValueRefine {

// Phase 27b — session-level refine comparator. Returns true (keep) or
// false (drop) for one candidate. When kept, `prev` is updated to `cur`
// so the next refine round compares against THIS round's value.
//
// scanType values:
//   exact      : cur == arg.value (range collapsed to single point)
//   range      : arg.value <= cur <= arg.valueTo (typed)
//   newValue   : cur == arg.value (alias for exact — semantic distinction
//                only matters in the UI: "I now want a different value")
//   changed    : cur != prev
//   unchanged  : cur == prev
//   increased  : cur > prev (typed)
//   decreased  : cur < prev (typed)
// `argLo` / `argHi` are 8-byte payloads packed by PackJsonValueToBytes
// (only meaningful for exact / range / newValue).
//
// `cur` is read from process memory before this is called; if the read
// failed, the caller should drop the candidate without invoking this.
bool CompareCandidateForRefine(Memory::ValueDataType dt,
                                std::string_view      scanType,
                                const std::uint8_t    cur[8],
                                std::uint8_t          prev[8],
                                const std::uint8_t    argLo[8],
                                const std::uint8_t    argHi[8],
                                double                tolerance) {
    auto updatePrev = [&](bool keep) {
        if (keep) std::memcpy(prev, cur, Memory::ValueDataTypeSize(dt));
        return keep;
    };

    #define LOAD(T, name, src) T name; std::memcpy(&name, src, sizeof(T))
    #define BODY_INT(T) {                                                         \
        LOAD(T, vCur, cur);  LOAD(T, vPrev, prev);                                \
        if (scanType == "exact" || scanType == "newValue") {                      \
            LOAD(T, vArg, argLo); return updatePrev(vCur == vArg);                \
        }                                                                          \
        if (scanType == "range") {                                                \
            LOAD(T, vLo, argLo); LOAD(T, vHi, argHi);                             \
            return updatePrev(vCur >= vLo && vCur <= vHi);                        \
        }                                                                          \
        if (scanType == "changed")    return updatePrev(vCur != vPrev);           \
        if (scanType == "unchanged")  return updatePrev(vCur == vPrev);           \
        if (scanType == "increased")  return updatePrev(vCur >  vPrev);           \
        if (scanType == "decreased")  return updatePrev(vCur <  vPrev);           \
        return false;                                                              \
    }
    #define BODY_FLT(T) {                                                         \
        LOAD(T, vCur, cur);  LOAD(T, vPrev, prev);                                \
        if (!std::isfinite(static_cast<double>(vCur))) return false;              \
        if (scanType == "exact" || scanType == "newValue") {                      \
            LOAD(T, vArg, argLo);                                                  \
            bool ok = std::fabs(static_cast<double>(vCur)                          \
                              - static_cast<double>(vArg)) <= tolerance;           \
            return updatePrev(ok);                                                 \
        }                                                                          \
        if (scanType == "range") {                                                \
            LOAD(T, vLo, argLo); LOAD(T, vHi, argHi);                             \
            return updatePrev(vCur >= vLo && vCur <= vHi);                        \
        }                                                                          \
        if (scanType == "changed")    return updatePrev(vCur != vPrev);           \
        if (scanType == "unchanged")  return updatePrev(vCur == vPrev);           \
        if (scanType == "increased")  return updatePrev(vCur >  vPrev);           \
        if (scanType == "decreased")  return updatePrev(vCur <  vPrev);           \
        return false;                                                              \
    }

    switch (dt) {
        case Memory::ValueDataType::Int8:   BODY_INT(std::int8_t);
        case Memory::ValueDataType::UInt8:  BODY_INT(std::uint8_t);
        case Memory::ValueDataType::Int16:  BODY_INT(std::int16_t);
        case Memory::ValueDataType::UInt16: BODY_INT(std::uint16_t);
        case Memory::ValueDataType::Int32:  BODY_INT(std::int32_t);
        case Memory::ValueDataType::UInt32: BODY_INT(std::uint32_t);
        case Memory::ValueDataType::Int64:  BODY_INT(std::int64_t);
        case Memory::ValueDataType::UInt64: BODY_INT(std::uint64_t);
        case Memory::ValueDataType::Float:  BODY_FLT(float);
        case Memory::ValueDataType::Double: BODY_FLT(double);
    }
    #undef BODY_INT
    #undef BODY_FLT
    #undef LOAD
    return false;
}

}  // namespace Discrete::ValueRefine
