// AobPattern.h — Phase 18a-3 minimal x64 length disassembler + AOB pattern
// builder. Reads N bytes via Memory::SafeReadBytes, walks them instruction
// by instruction, and emits a Cheat-Engine-style AOB pattern string with
// rel8/rel32 immediates and RIP-relative ModRM disp32 fields wildcarded.
//
// We deliberately don't pull in Zydis — for 32-byte method prologues, the
// instruction subset we need to recognise is small (PUSH/POP, MOV with stack
// offsets, SUB/ADD RSP, LEA, CALL/JMP/Jcc). Conservative wildcarding is the
// invariant: when the decoder hits an opcode it doesn't recognise, it
// emits the remaining bytes as literal hex (no wildcards). False negatives
// (less wildcarding) just make the AOB more brittle across game patches —
// users regenerate. False positives (over-wildcarding) would match multiple
// regions in the binary and produce ambiguous symbols, which is the failure
// mode we must avoid.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace Discrete::Aob {

struct PatternResult {
    std::string         pattern;        // "48 89 5C 24 ?? E8 ?? ?? ?? ??"
    std::vector<std::uint8_t> bytes;    // raw bytes that were read (always literal)
    std::vector<bool>   wildcardMask;   // parallel to bytes; true = "??" in pattern
    std::size_t         length = 0;     // bytes the pattern actually covers
    std::size_t         instructionCount = 0;
    std::size_t         relBranchCount   = 0;  // CALL/JMP/Jcc relative immediates wildcarded
    std::size_t         ripRelCount      = 0;  // RIP-relative ModRM disp32 wildcarded
    bool                decoderBailed = false; // hit unknown opcode mid-stream
    std::size_t         bailedAt = 0;          // byte offset where decoder gave up (only valid when bailed)

    // Set when the decoder reached a RET / unconditional JMP inside the window,
    // i.e. the requested length ran past the end of the code being signed.
    // `length` is then the trimmed length and `requestedLength` the original.
    bool                stoppedAtTerminal = false;
    std::size_t         requestedLength   = 0;
};

// Build an AOB pattern from `bytes[0..len)` using the minimal decoder.
//
// `stopAtTerminal` (default) truncates the pattern at the first RET /
// unconditional JMP. A caller asks for N bytes from a function entry as an upper
// bound, but a short function ends well before N, and the bytes after it are
// padding, JIT bookkeeping or the next function — none of which belong in a
// signature for THIS function. Measured before this existed: a 24-byte Mono getter
// asked for 48 bytes produced a pattern whose tail was
// `C3 01 04 01 00 04 02 00 00 …`, and those trailing zeros matched 223 places in
// 308 MB of executable memory. Padding also moves between runs, so signing it
// makes the pattern fail later for no benefit.
//
// Pass false to get exactly `len` bytes regardless (raw-window behaviour).
PatternResult BuildPattern(const std::uint8_t* bytes, std::size_t len,
                           bool stopAtTerminal = true);

}  // namespace Discrete::Aob
