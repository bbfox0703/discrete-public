// IL2CPPFieldXrefScan.cpp — legacy C++ (Zydis) implementation of the two
// IL2CPPFieldXref scan kernels (see IL2CPPFieldXrefScan.h). The loops are the
// ones IL2CPPFieldXref.cpp ran inline, moved verbatim; only the kind filter,
// method attribution and limit cut-off stayed with the caller.

#include "IL2CPPFieldXrefScan.h"

#include "../shared/Memory.h"

#include <Zydis/Zydis.h>

#include <algorithm>

namespace Discrete::IL2CPP::XrefScan {

namespace {

// True iff `m` is a ModRM byte whose mod=00 + r/m=101 — meaning the
// operand uses RIP-relative addressing (disp32 follows in the next 4
// bytes). The reg field (bits 3-5) selects the destination register but
// doesn't change addressing.
inline bool IsRipRelativeModrm(std::uint8_t m) noexcept {
    return (m & 0xC7u) == 0x05u;  // mod=00 in bits 6-7, r/m=101 in bits 0-2
}

// True iff `b` is a REX byte in the 0x40..0x4F range (any combination of
// W/R/X/B). We accept any REX value since the W bit (operand-size) doesn't
// affect RIP-relative decoding.
inline bool IsRexByte(std::uint8_t b) noexcept {
    return (b & 0xF0u) == 0x40u;
}

inline bool IsCandidateOpcode(std::uint8_t b) noexcept {
    return b == 0x8B || b == 0x8D || b == 0x89;
}

inline std::int32_t ReadInt32LE(const std::uint8_t* p) noexcept {
    std::uint32_t v =
          static_cast<std::uint32_t>(p[0])
        | (static_cast<std::uint32_t>(p[1]) <<  8)
        | (static_cast<std::uint32_t>(p[2]) << 16)
        | (static_cast<std::uint32_t>(p[3]) << 24);
    return static_cast<std::int32_t>(v);
}

// Canonicalise a register to its 64-bit enclosing form so rax / eax /
// ax / al all compare equal. ZYDIS_REGISTER_NONE stays NONE.
inline ZydisRegister Canonical(ZydisRegister r) {
    if (r == ZYDIS_REGISTER_NONE) return r;
    ZydisRegister canon = ZydisRegisterGetLargestEnclosing(
        ZYDIS_MACHINE_MODE_LONG_64, r);
    return canon != ZYDIS_REGISTER_NONE ? canon : r;
}

}  // namespace

std::vector<RipCandidate> ScanRipRelative(const std::vector<ExecSection>& sections,
                                          std::uintptr_t target) {
    std::vector<RipCandidate> out;

    // Scan each executable section. Buffer 1 MB at a time to keep peak
    // memory bounded on the 170 MB GameAssembly.dll seen in the test
    // matrix (Songs of Silence). Each chunk reads `want` "owned" bytes
    // plus a 16-byte tail overlap so a 7-byte instruction starting near
    // the chunk boundary still fits without being re-emitted by the
    // next iteration.
    constexpr std::size_t kChunkSize = 1024u * 1024u;
    constexpr std::size_t kOverlap   = 16u;
    std::vector<std::uint8_t> buf(kChunkSize + kOverlap);

    for (const auto& sec : sections) {
        std::size_t pos = 0;
        while (pos < sec.size) {
            std::size_t want     = (sec.size - pos) < kChunkSize ? (sec.size - pos) : kChunkSize;
            std::size_t readWant = want + kOverlap;
            if (pos + readWant > sec.size) readWant = sec.size - pos;
            const std::uint8_t* secBase = reinterpret_cast<const std::uint8_t*>(sec.va);
            if (!Memory::SafeReadBytes(secBase + pos, buf.data(), readWant)) {
                // AC-stripped page — skip 4 KB and continue.
                pos += 4096u;
                continue;
            }
            // Only emit hits whose *start* falls within the owned portion
            // of the chunk; the overlap bytes are read for context only.
            std::size_t emitEnd = (want > 0) ? want : 0;
            // Need at least 7 bytes from p to read a candidate 7-byte
            // instruction; cap emitEnd to fit.
            if (readWant < 7u) {
                pos += want;
                continue;
            }
            std::size_t scanEnd = std::min(emitEnd, readWant - 7u + 1u);

            for (std::size_t p = 0; p < scanEnd; ++p) {
                std::uint8_t b0 = buf[p];

                // Try 7-byte form FIRST (REX + op + ModRM + disp32). This
                // is the canonical 64-bit form for static-field MOVs; trying
                // it first means the 6-byte form below can use the "prev
                // byte was REX" heuristic to skip the duplicate that would
                // otherwise fire at p+1 when a REX instruction sits at p.
                if (IsRexByte(b0)) {
                    std::uint8_t b1 = buf[p + 1];
                    if (IsCandidateOpcode(b1) && IsRipRelativeModrm(buf[p + 2])) {
                        std::int32_t disp = ReadInt32LE(buf.data() + p + 3);
                        std::uintptr_t startIP = sec.va + pos + p;
                        std::uintptr_t endIP   = startIP + 7;
                        std::uintptr_t hitTarget = endIP + static_cast<std::intptr_t>(disp);
                        if (hitTarget == target) {
                            out.push_back({startIP, b1});
                            continue;  // don't double-test as 6-byte form
                        }
                    }
                }

                // Try 6-byte form (no REX): op + ModRM + disp32. Skipped
                // when the previous byte is a REX prefix — that would mean
                // the real instruction is the 7-byte form starting one
                // byte earlier, which we already considered.
                if (p > 0 && IsRexByte(buf[p - 1])) continue;

                if (IsCandidateOpcode(b0) && IsRipRelativeModrm(buf[p + 1])) {
                    std::int32_t disp = ReadInt32LE(buf.data() + p + 2);
                    std::uintptr_t startIP = sec.va + pos + p;
                    std::uintptr_t endIP   = startIP + 6;
                    std::uintptr_t hitTarget = endIP + static_cast<std::intptr_t>(disp);
                    if (hitTarget == target) {
                        out.push_back({startIP, b0});
                    }
                }
            }
            pos += want;
        }
    }
    return out;
}

std::vector<InstanceHit> ScanInstanceAccesses(const std::vector<MethodRange>& methods,
                                              std::size_t fieldOffset) {
    std::vector<InstanceHit> out;

    ZydisDecoder decoder;
    ZydisDecoderInit(&decoder, ZYDIS_MACHINE_MODE_LONG_64, ZYDIS_STACK_WIDTH_64);

    std::size_t maxLen = 0;
    for (const auto& m : methods) maxLen = std::max(maxLen, m.len);
    std::vector<std::uint8_t> buf(maxLen);

    for (std::size_t mi = 0; mi < methods.size(); ++mi) {
        const std::uintptr_t methodStart = methods[mi].start;
        const std::size_t    rangeLen    = methods[mi].len;
        if (rangeLen == 0) continue;

        if (!Memory::SafeReadBytes(reinterpret_cast<const void*>(methodStart),
                                   buf.data(), rangeLen)) {
            // AC-stripped page or unreadable — skip this method.
            continue;
        }

        // Linear-decode the method body. Stop at the first int3 (0xCC)
        // padding run or a decode failure (clamped tail) — both signal we've
        // run off the end of the real method, which kills cross-method FPs.
        std::size_t consumed = 0;
        while (consumed < rangeLen) {
            if (buf[consumed] == 0xCC) break;  // int3 padding → end of method
            ZydisDecodedInstruction instr;
            ZydisDecodedOperand     operands[ZYDIS_MAX_OPERAND_COUNT];
            ZyanStatus status = ZydisDecoderDecodeFull(
                &decoder, buf.data() + consumed, rangeLen - consumed,
                &instr, operands);
            if (!ZYAN_SUCCESS(status)) break;  // clamped tail / bad bytes

            const std::uintptr_t insnIP = methodStart + consumed;

            for (std::uint8_t oi = 0; oi < instr.operand_count_visible; ++oi) {
                const ZydisDecodedOperand& op = operands[oi];
                if (op.type != ZYDIS_OPERAND_TYPE_MEMORY) continue;
                if (op.mem.disp.size == 0) continue;                 // no disp
                if (op.mem.index != ZYDIS_REGISTER_NONE) continue;   // [base+index*s]
                if (static_cast<std::size_t>(op.mem.disp.value) != fieldOffset) continue;
                const ZydisRegister base = Canonical(op.mem.base);
                if (base == ZYDIS_REGISTER_NONE) continue;           // [disp] absolute
                if (base == ZYDIS_REGISTER_RIP)  continue;           // static path
                if (base == ZYDIS_REGISTER_RSP)  continue;           // stack frame
                if (base == ZYDIS_REGISTER_RBP)  continue;           // frame local

                // Classify read vs write from the memory operand's action.
                // lea computes an address (AGEN, no read/write action) — treat
                // as a read, matching the static path's LEA → "read".
                bool write;
                if (instr.mnemonic == ZYDIS_MNEMONIC_LEA) {
                    write = false;
                } else if (op.actions & ZYDIS_OPERAND_ACTION_MASK_WRITE) {
                    write = true;
                } else if (op.actions & ZYDIS_OPERAND_ACTION_MASK_READ) {
                    write = false;
                } else {
                    continue;  // memory operand neither read nor written — skip
                }
                out.push_back({mi, insnIP, write});
            }

            consumed += instr.length;
        }
    }
    return out;
}

}  // namespace Discrete::IL2CPP::XrefScan
