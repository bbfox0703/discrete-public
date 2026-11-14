// IL2CPPFieldXrefScan.h — the two pure scan kernels behind IL2CPPFieldXref.
//
// Split out of IL2CPPFieldXref.cpp so they can be swapped for the Rust ports
// and compared byte for byte (tools/r2-diff, Phase R2 follow-up). Everything
// that talks to the IL2CPP runtime (resolver, domain + thread attach, class /
// method / field enumeration, static data, method index, logging) stays in
// IL2CPPFieldXref.cpp; these functions only read GameAssembly.dll code through
// Memory::SafeReadBytes (or, in the Rust port, the SEH read shim) and return
// raw candidates in scan order. Kind filtering, method attribution and the
// `limit` cut-off are applied by the caller, in that order, exactly as the
// original single-function loops did.
//
// x64 only (IL2CPP backend).

#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

namespace Discrete::IL2CPP::XrefScan {

// An executable section of the mapped module (absolute VA + VirtualSize).
struct ExecSection {
    std::uintptr_t va;
    std::size_t    size;
};

// v1 static path. One single-hop RIP-relative candidate whose absolute target
// equals the requested address: `ip` is the instruction start, `opcode` the
// primary opcode (0x8B MOV r,m / 0x8D LEA = read; 0x89 MOV m,r = write).
struct RipCandidate {
    std::uintptr_t ip;
    std::uint8_t   opcode;
};

// Scans every section in order (1 MiB chunks + 16-byte overlap; an unreadable
// chunk skips 4 KiB) for REX? + {8B,8D,89} + ModRM(mod=00,rm=101) + disp32
// whose RIP-relative target == `target`. Returns ALL candidates, in the order
// the legacy loop met them (the caller stops at its own limit).
std::vector<RipCandidate> ScanRipRelative(const std::vector<ExecSection>& sections,
                                          std::uintptr_t target);

// Instance path. One method body to decode: [start, start + len), len already
// clamped by the caller (next method, 8 KiB cap, section end) and > 0.
struct MethodRange {
    std::uintptr_t start;
    std::size_t    len;
};

// One `[gpr + fieldOffset]` memory-operand access (base not RIP/RSP/RBP, no
// index, displacement present). `method` indexes the input vector, `ip` is the
// instruction start, `write` is the operand's write action (LEA = read).
struct InstanceHit {
    std::size_t    method;
    std::uintptr_t ip;
    bool           write;
};

// Linear-decodes each range (stops at the first 0xCC or decode failure; an
// unreadable range is skipped) and returns every matching operand, in order:
// method by method, instruction by instruction, operand by operand. An
// instruction with two matching operands yields two hits (the caller dedups by
// IP after its limit cut-off).
std::vector<InstanceHit> ScanInstanceAccesses(const std::vector<MethodRange>& methods,
                                              std::size_t fieldOffset);

}  // namespace Discrete::IL2CPP::XrefScan
