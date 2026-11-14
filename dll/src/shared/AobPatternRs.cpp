// AobPatternRs.cpp — Phase R2. Implements Discrete::Aob::BuildPattern
// (AobPattern.h) over the Rust port in dll/rust/src/aob.rs (C ABI:
// dc_aob_build_pattern in dll/rust/include/discrete_core.h), so callers keep
// the existing API.
//
// The legacy C++ decoder (AobPattern.cpp) was removed when R2 closed; it is
// in git history (last in aae48d3). This file holds no decoding logic: it sizes the output buffers, makes one call and
// copies the result into PatternResult exactly as the legacy code filled it:
//   * pattern       — the Rust pattern bytes (uppercase hex / "??", spaces);
//   * bytes         — input[0 .. length), i.e. trimmed like the legacy resize;
//   * wildcardMask  — mask[0 .. length) as bool;
//   * length / requestedLength / counters / decoderBailed / bailedAt /
//     stoppedAtTerminal — copied field for field.
//
// `bytes` is the caller's buffer (ExportAPI copies it out of the game with
// Memory::SafeReadBytes first); the Rust side reads it as a plain slice and
// never touches game memory.

#include "AobPattern.h"

#include <discrete_core.h>

#include <utility>

namespace Discrete::Aob {

namespace {

// What BuildPattern returns if dc_aob_build_pattern reports failure. That
// only happens on an internal Rust panic or an undersized buffer, neither of
// which this file can cause, so it should never run. The answer chosen is
// the legacy decoder's own conservative bail-out applied at offset 0: the
// whole window as literal hex, nothing wildcarded, decoderBailed = true,
// bailedAt = 0. A literal pattern is never over-wildcarded (the failure mode
// the design must avoid), keeps the full requested window, and tells callers
// that look at decoderBailed that no decoding happened. It is exactly what
// the legacy code produces for a window whose first instruction it cannot
// decode.
PatternResult LiteralFallback(const std::uint8_t* bytes, std::size_t len) {
    static constexpr char kHex[] = "0123456789ABCDEF";
    PatternResult r;
    r.length          = len;
    r.requestedLength = len;
    if (len > 0) r.bytes.assign(bytes, bytes + len);
    r.wildcardMask.assign(len, false);
    r.decoderBailed = true;
    r.bailedAt      = 0;
    r.pattern.reserve(len * 3);
    for (std::size_t i = 0; i < len; ++i) {
        if (i > 0) r.pattern += ' ';
        r.pattern += kHex[bytes[i] >> 4];
        r.pattern += kHex[bytes[i] & 0x0F];
    }
    return r;
}

}  // namespace

PatternResult BuildPattern(const std::uint8_t* bytes, std::size_t len,
                           bool stopAtTerminal) {
    // Contract sizes: pattern >= 3*len bytes, mask >= len bytes.
    std::string               pattern(len * 3, '\0');
    std::vector<std::uint8_t> mask(len, 0);
    DcAobResult               out{};

    const std::int32_t ok = dc_aob_build_pattern(
        bytes, len, stopAtTerminal ? 1 : 0,
        pattern.data(), pattern.size(),
        mask.data(), mask.size(),
        &out);

    // The bound checks only guard against a contract violation on the Rust
    // side; with ok == 1 they always hold.
    if (ok != 1 || out.length > len || out.pattern_len > pattern.size()) {
        return LiteralFallback(bytes, len);
    }

    PatternResult r;
    pattern.resize(out.pattern_len);
    r.pattern = std::move(pattern);
    if (out.length > 0) r.bytes.assign(bytes, bytes + out.length);
    r.wildcardMask.resize(out.length);
    for (std::size_t i = 0; i < out.length; ++i) {
        r.wildcardMask[i] = (mask[i] != 0);
    }
    r.length            = out.length;
    r.instructionCount  = out.instruction_count;
    r.relBranchCount    = out.rel_branch_count;
    r.ripRelCount       = out.rip_rel_count;
    r.decoderBailed     = (out.decoder_bailed != 0);
    r.bailedAt          = out.bailed_at;
    r.stoppedAtTerminal = (out.stopped_at_terminal != 0);
    r.requestedLength   = out.requested_length;
    return r;
}

}  // namespace Discrete::Aob
