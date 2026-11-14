// ValueRefine.h — Phase 27b session-level refine comparator (value scan
// "Next Scan": exact / range / newValue / changed / unchanged / increased /
// decreased), shared by both backends' scanFieldValue + group-scan handlers.
//
// Phase R2: extracted from ExportAPI.cpp and implemented in Rust
// (dll/rust/src/value.rs) behind ValueRefineRs.cpp. The legacy C++ body
// (ValueRefine.cpp) was removed when R2 closed; it is in git history (last in aae48d3).
#pragma once

#include "Memory.h"

#include <cstdint>
#include <string_view>

namespace Discrete::ValueRefine {

// Returns true (keep) or false (drop) for one candidate. When kept, the
// first ValueDataTypeSize(dt) bytes of `prev` are overwritten with `cur`, so
// the next refine round compares against THIS round's value.
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
// Any other scanType drops the candidate. Floats/doubles: a non-finite
// `cur` always drops; exact/newValue compare with |cur - arg| <= tolerance.
// `argLo` / `argHi` are 8-byte payloads (only meaningful for exact / range /
// newValue). `cur` is read from process memory before this is called; if the
// read failed, the caller drops the candidate without invoking this.
bool CompareCandidateForRefine(Memory::ValueDataType dt,
                               std::string_view      scanType,
                               const std::uint8_t    cur[8],
                               std::uint8_t          prev[8],
                               const std::uint8_t    argLo[8],
                               const std::uint8_t    argHi[8],
                               double                tolerance);

}  // namespace Discrete::ValueRefine
