// ValueRefineRs.cpp — Phase R2. Implements the ValueRefine.h API over the
// Rust port (dll/rust/src/value.rs, C ABI dc_value_refine_compare in
// dll/rust/include/discrete_core.h).
//
// This file holds no comparison logic: it only marshals arguments. The Rust
// side catches its own panics (returns 0 = drop, prev untouched), so nothing
// can unwind or throw across this boundary.

#include "ValueRefine.h"

#include <discrete_core.h>

#include <cstdint>

namespace Discrete::ValueRefine {

bool CompareCandidateForRefine(Memory::ValueDataType dt,
                               std::string_view      scanType,
                               const std::uint8_t    cur[8],
                               std::uint8_t          prev[8],
                               const std::uint8_t    argLo[8],
                               const std::uint8_t    argHi[8],
                               double                tolerance) {
    // ValueDataType's underlying type is int (Int8 = 0 .. Double = 9); the
    // Rust side drops any value outside that range, exactly like the legacy
    // switch falling through to `return false`.
    // scanType is passed as raw (ptr, len) bytes and compared byte-for-byte,
    // matching the legacy std::string_view == "..." tests. A default
    // string_view (nullptr, 0) is accepted as the empty string.
    return dc_value_refine_compare(static_cast<std::int32_t>(dt),
                                   reinterpret_cast<const std::uint8_t*>(scanType.data()),
                                   scanType.size(),
                                   cur, prev, argLo, argHi, tolerance) == 1;
}

}  // namespace Discrete::ValueRefine
