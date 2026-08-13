// AobPattern.cpp — minimal x64 length disassembler implementation. See
// AobPattern.h for the design rationale.

#include "AobPattern.h"

#include <windows.h>

#include <cstdio>

namespace Discrete::Aob {

namespace {

// Per-opcode metadata. Only what we need:
//   hasModRm   — fetch ModRM byte (drives SIB / disp / RIP-relative)
//   immSize    — bytes of immediate following any disp (0/1/2/4)
//   relImm     — true when the immediate is a rel8/rel16/rel32 to wildcard
//   unknown    — bail (caller emits remaining bytes literal)
struct OpcodeInfo {
    bool hasModRm = false;
    int  immSize  = 0;
    bool relImm   = false;
    bool unknown  = false;
    // Ends the flow: RET / RETF / IRET / unconditional JMP. Everything after one
    // of these, inside a window that started at a function entry, is no longer
    // part of the path we are signing — it is padding, JIT bookkeeping or the
    // next function. See BuildPattern.
    bool terminal = false;
};

OpcodeInfo Lookup1B(std::uint8_t op) {
    OpcodeInfo o;

    // 0x00..0x3F arithmetic: ADD / OR / ADC / SBB / AND / SUB / XOR / CMP.
    // Eight 8-byte rows. Each row has 6 ModRM-only forms + 2 immediate forms
    // (AL imm8 / EAX imm32). Segment-prefix opcodes 0x26/2E/36/3E are caught
    // earlier as legacy prefixes.
    if (op <= 0x3F) {
        const std::uint8_t lo = op & 0x7;
        if (lo == 4) { o.immSize = 1; }       // <op> AL, imm8
        else if (lo == 5) { o.immSize = 4; }  // <op> EAX, imm32
        else { o.hasModRm = true; }           // r/m forms
        return o;
    }

    // 0x40-0x4F REX prefix — should have been consumed; standalone is unknown.
    if (op >= 0x40 && op <= 0x4F) { o.unknown = true; return o; }

    // 0x50-0x5F PUSH/POP r64 — single byte, no ModRM, no imm.
    if (op >= 0x50 && op <= 0x5F) { return o; }

    // 0x60-0x6F: most invalid in 64-bit; the survivors are PUSH imm{8,32}
    // and IMUL r,r/m,imm{8,32}.
    if (op == 0x68) { o.immSize = 4; return o; }   // PUSH imm32
    if (op == 0x6A) { o.immSize = 1; return o; }   // PUSH imm8
    if (op == 0x69) { o.hasModRm = true; o.immSize = 4; return o; }
    if (op == 0x6B) { o.hasModRm = true; o.immSize = 1; return o; }
    if (op >= 0x60 && op <= 0x6F) { o.unknown = true; return o; }

    // 0x70-0x7F Jcc rel8 — wildcard the relative target.
    if (op >= 0x70 && op <= 0x7F) { o.immSize = 1; o.relImm = true; return o; }

    // 0x80-0x83 group 1 (arithmetic with immediate).
    if (op == 0x80) { o.hasModRm = true; o.immSize = 1; return o; }
    if (op == 0x81) { o.hasModRm = true; o.immSize = 4; return o; }
    if (op == 0x82) { o.unknown = true; return o; }   // not in 64-bit
    if (op == 0x83) { o.hasModRm = true; o.immSize = 1; return o; }

    // 0x84-0x8F: TEST / XCHG / MOV / LEA / MOV seg — all ModRM, no imm.
    if (op >= 0x84 && op <= 0x8F) { o.hasModRm = true; return o; }

    // 0x90-0x9F: NOP / XCHG eAX / CWD-CDQ / WAIT / PUSHF / POPF / SAHF / LAHF.
    if (op >= 0x90 && op <= 0x9F) { return o; }

    // 0xA0-0xA3: MOV moffs. In 64-bit mode the offset is 64-bit (8 bytes).
    if (op >= 0xA0 && op <= 0xA3) { o.immSize = 8; return o; }
    // 0xA4-0xA7 string ops, 0xA8/A9 TEST, 0xAA-AF more string ops.
    if (op == 0xA8) { o.immSize = 1; return o; }
    if (op == 0xA9) { o.immSize = 4; return o; }
    if (op >= 0xA4 && op <= 0xAF) { return o; }

    // 0xB0-0xB7 MOV r8, imm8 / 0xB8-0xBF MOV r32, imm32 (becomes imm64 with REX.W).
    if (op >= 0xB0 && op <= 0xB7) { o.immSize = 1; return o; }
    if (op >= 0xB8 && op <= 0xBF) { o.immSize = 4; return o; }

    // 0xC0/C1 group 2 shifts (ModRM + imm8); 0xC2 RET imm16; 0xC3 RET.
    if (op == 0xC0 || op == 0xC1) { o.hasModRm = true; o.immSize = 1; return o; }
    if (op == 0xC2) { o.immSize = 2; o.terminal = true; return o; }
    if (op == 0xC3) { o.terminal = true; return o; }
    if (op == 0xC6) { o.hasModRm = true; o.immSize = 1; return o; }   // group 11 MOV r/m8 imm8
    if (op == 0xC7) { o.hasModRm = true; o.immSize = 4; return o; }   // group 11 MOV r/m32 imm32
    if (op == 0xC8) { o.immSize = 3; return o; }   // ENTER imm16, imm8
    if (op == 0xC9) { return o; }                  // LEAVE
    if (op == 0xCA) { o.immSize = 2; o.terminal = true; return o; }   // RETF imm16
    if (op == 0xCB) { o.terminal = true; return o; }               // RETF
    if (op == 0xCC) { return o; }                  // INT3
    if (op == 0xCD) { o.immSize = 1; return o; }   // INT imm8
    if (op == 0xCF) { o.terminal = true; return o; }               // IRET

    // 0xD0-0xD3 shift by 1 / by CL (ModRM only).
    if (op >= 0xD0 && op <= 0xD3) { o.hasModRm = true; return o; }
    if (op == 0xD7) { return o; }                                       // XLAT
    if (op == 0xD4 || op == 0xD5 || op == 0xD6) { o.unknown = true; return o; }

    // 0xD8-DF: x87 FPU (ModRM, no imm).
    if (op >= 0xD8 && op <= 0xDF) { o.hasModRm = true; return o; }

    // 0xE0-E3 LOOPxx/JCXZ rel8.
    if (op >= 0xE0 && op <= 0xE3) { o.immSize = 1; o.relImm = true; return o; }
    if (op >= 0xE4 && op <= 0xE7) { o.immSize = 1; return o; }   // I/O imm8

    // 0xE8 CALL rel32, 0xE9 JMP rel32, 0xEB JMP rel8 — the headline cases.
    if (op == 0xE8) { o.immSize = 4; o.relImm = true; return o; }
    if (op == 0xE9) { o.immSize = 4; o.relImm = true; o.terminal = true; return o; }
    if (op == 0xEA) { o.unknown = true; return o; }   // far jmp invalid in 64-bit
    if (op == 0xEB) { o.immSize = 1; o.relImm = true; o.terminal = true; return o; }
    if (op >= 0xEC && op <= 0xEF) { return o; }   // I/O port

    if (op == 0xF1 || op == 0xF4 || op == 0xF5) { return o; }
    if (op >= 0xF8 && op <= 0xFD) { return o; }   // CLC/STC/CLI/STI/CLD/STD

    // Group 3: immediate presence depends on the ModRM.reg sub-opcode (only the
    // TEST forms have one), so it is resolved after ModRM is read — see the
    // group-3 fixup in DecodeOne.
    if (op == 0xF6) { o.hasModRm = true; return o; }   // group 3 byte
    if (op == 0xF7) { o.hasModRm = true; return o; }   // group 3 word/dword
    if (op == 0xFE) { o.hasModRm = true; return o; }   // group 4 INC/DEC
    if (op == 0xFF) { o.hasModRm = true; return o; }   // group 5 CALL/JMP indirect

    o.unknown = true;
    return o;
}

OpcodeInfo Lookup0F(std::uint8_t op2) {
    OpcodeInfo o;
    // Jcc rel32 — the ones we care about most after E8/E9.
    if (op2 >= 0x80 && op2 <= 0x8F) { o.immSize = 4; o.relImm = true; return o; }
    // SSE shift-imm group: ModRM + imm8.
    if (op2 >= 0x71 && op2 <= 0x73) { o.hasModRm = true; o.immSize = 1; return o; }
    // No-operand 0F xx: SYSCALL/SYSRET/RDTSC/etc.
    switch (op2) {
        case 0x05: case 0x07: case 0x09: case 0x0B:
        case 0x30: case 0x31: case 0x32: case 0x33:
        case 0x34: case 0x35:
        case 0xA0: case 0xA1: case 0xA2: case 0xA8: case 0xA9:
            return o;
    }
    // SETcc r/m8 — ModRM, no imm.
    if (op2 >= 0x90 && op2 <= 0x9F) { o.hasModRm = true; return o; }
    // SHLD / SHRD imm8.
    if (op2 == 0xA4 || op2 == 0xAC || op2 == 0xBA) { o.hasModRm = true; o.immSize = 1; return o; }
    if (op2 == 0xA5 || op2 == 0xAD) { o.hasModRm = true; return o; }
    // CMPxx / SHUFxx / PSHUFW / etc. — ModRM + imm8.
    if (op2 == 0x70 || op2 == 0xC2 || op2 == 0xC4 || op2 == 0xC5 || op2 == 0xC6) {
        o.hasModRm = true; o.immSize = 1; return o;
    }
    // BSWAP r64 — single byte after 0F.
    if (op2 >= 0xC8 && op2 <= 0xCF) { return o; }

    // Default: ModRM, no immediate. Covers MOVxx/CMOVxx/XADD/CPUID/etc.
    o.hasModRm = true;
    return o;
}

struct Decoded {
    int  length      = 0;
    int  immOffset   = -1;
    int  immSize     = 0;
    int  dispOffset  = -1;
    int  dispSize    = 0;
    bool relImm      = false;
    bool relDisp     = false;   // RIP-relative ModRM disp32
    bool valid       = true;
    bool terminal    = false;   // RET / RETF / IRET / unconditional JMP
};

Decoded DecodeOne(const std::uint8_t* p, std::size_t avail) {
    Decoded d;
    if (avail == 0) { d.valid = false; return d; }

    int pos = 0;
    bool hasOpSize   = false;
    bool hasAddrSize = false;
    std::uint8_t rex = 0;

    // Legacy prefixes — at most 4 (segment + REP + opsize + addrsize).
    while (pos < static_cast<int>(avail) && pos < 5) {
        const std::uint8_t b = p[pos];
        switch (b) {
            case 0xF0: case 0xF2: case 0xF3:
            case 0x26: case 0x2E: case 0x36: case 0x3E:
            case 0x64: case 0x65:
                ++pos; continue;
            case 0x66: hasOpSize = true; ++pos; continue;
            case 0x67: hasAddrSize = true; ++pos; continue;
            default: break;
        }
        break;
    }
    if (pos >= static_cast<int>(avail)) { d.valid = false; return d; }

    // REX prefix.
    if ((p[pos] & 0xF0) == 0x40) {
        rex = p[pos];
        ++pos;
        if (pos >= static_cast<int>(avail)) { d.valid = false; return d; }
    }

    // Opcode (1, 2 or 3 bytes).
    OpcodeInfo info;
    int opLen = 1;
    const std::uint8_t op = p[pos];
    if (op == 0x0F) {
        if (pos + 1 >= static_cast<int>(avail)) { d.valid = false; return d; }
        const std::uint8_t op2 = p[pos + 1];
        if (op2 == 0x38 || op2 == 0x3A) {
            // Three-byte SSE/AVX escape — most are ModRM-only; the 3A bank
            // also carries an imm8.
            if (pos + 2 >= static_cast<int>(avail)) { d.valid = false; return d; }
            info.hasModRm = true;
            if (op2 == 0x3A) info.immSize = 1;
            opLen = 3;
        } else {
            info = Lookup0F(op2);
            opLen = 2;
        }
    } else {
        info = Lookup1B(op);
    }
    pos += opLen;
    if (info.unknown) { d.valid = false; d.length = pos; return d; }
    d.terminal = info.terminal;

    // ModR/M byte → SIB / displacement / RIP-relative detection.
    if (info.hasModRm) {
        if (pos >= static_cast<int>(avail)) { d.valid = false; return d; }
        const std::uint8_t modrm = p[pos++];
        const std::uint8_t mod = modrm >> 6;
        const std::uint8_t rm  = modrm & 0x7;
        const std::uint8_t reg = (modrm >> 3) & 0x7;

        // Group 3 (0xF6 / 0xF7) carries an immediate ONLY for its TEST sub-ops
        // (reg 0 and 1); NOT / NEG / MUL / IMUL / DIV / IDIV (reg 2..7) do not.
        // That cannot be decided from the opcode byte alone, so Lookup() leaves
        // immSize at 0 and it has to be filled in here — which nothing did. The
        // result was a length 1 byte short for `F6 /0` and 4 short for `F7 /0`,
        // so the decoder resumed mid-instruction and every subsequent
        // instruction boundary in the window was wrong: the emitted AOB got its
        // wildcards in the wrong places and matched nothing (or, worse, matched
        // something else). The other immediate-bearing groups (0x80/81/83,
        // 0xC0/C1, 0xC6/C7) already set immSize in Lookup because theirs does
        // not depend on the ModRM.reg field.
        if (opLen == 1 && (op == 0xF6 || op == 0xF7) && reg <= 1) {
            info.immSize = (op == 0xF6) ? 1 : 4;   // 0x66 demotes F7's to 2 below
        }

        // Group 5 `FF /4` is JMP r/m64 — an indirect tail jump, which ends the
        // flow just as much as a RET does. `FF /5` is the far form. Like group 3
        // above, this depends on ModRM.reg, so Lookup1B cannot see it. IL2CPP AOT
        // and import thunks reach the end of a function this way rather than with
        // a RET, so without this the window would keep decoding into whatever
        // follows the jump.
        if (opLen == 1 && op == 0xFF && (reg == 4 || reg == 5)) {
            d.terminal = true;
        }

        bool sib = false;
        int  dispSize = 0;
        bool ripRel = false;

        if (mod == 0) {
            if (rm == 4) sib = true;
            else if (rm == 5) {
                // RIP-relative in 64-bit mode unless the 0x67 addr-size
                // override demoted us to 32-bit addressing (then it's
                // disp32 absolute, which we still wildcard since it's an
                // absolute address that may move).
                dispSize = 4;
                ripRel = true;
            }
        } else if (mod == 1) {
            if (rm == 4) sib = true;
            dispSize = 1;
        } else if (mod == 2) {
            if (rm == 4) sib = true;
            dispSize = 4;
        }
        // mod == 3: pure register, no SIB / disp.

        if (sib) {
            if (pos >= static_cast<int>(avail)) { d.valid = false; return d; }
            const std::uint8_t sibByte = p[pos++];
            const std::uint8_t base = sibByte & 0x7;
            // SIB base=5 + mod=0 → 4-byte disp32 (no base register).
            if (mod == 0 && base == 5) dispSize = 4;
        }

        if (dispSize > 0) {
            if (pos + dispSize > static_cast<int>(avail)) { d.valid = false; return d; }
            d.dispOffset = pos;
            d.dispSize   = dispSize;
            d.relDisp    = ripRel;
            pos += dispSize;
        }
        (void)hasAddrSize;
    }

    // REX.W on 0xB8-0xBF MOV r64, imm64 promotes the immediate to 8 bytes.
    if (op >= 0xB8 && op <= 0xBF && opLen == 1 && (rex & 0x08)) {
        info.immSize = 8;
    }

    // Operand-size override demotes 32-bit immediates to 16-bit. Doesn't
    // apply to relative branches (those are always rel32 under 0x66 too).
    if (hasOpSize && info.immSize == 4 && !info.relImm) {
        info.immSize = 2;
    }

    if (info.immSize > 0) {
        if (pos + info.immSize > static_cast<int>(avail)) { d.valid = false; return d; }
        d.immOffset = pos;
        d.immSize   = info.immSize;
        d.relImm    = info.relImm;
        pos += info.immSize;
    }

    d.length = pos;
    return d;
}

}  // namespace

PatternResult BuildPattern(const std::uint8_t* bytes, std::size_t len,
                           bool stopAtTerminal) {
    PatternResult r;
    r.length = len;
    r.requestedLength = len;
    r.bytes.assign(bytes, bytes + len);
    r.wildcardMask.assign(len, false);

    std::size_t cursor = 0;
    while (cursor < len) {
        Decoded d = DecodeOne(bytes + cursor, len - cursor);
        if (!d.valid || d.length == 0) {
            r.decoderBailed = true;
            r.bailedAt = cursor;
            break;  // Remaining bytes stay literal — safe but less robust.
        }
        if (d.relImm && d.immOffset >= 0 && d.immSize > 0) {
            for (int i = 0; i < d.immSize; ++i) {
                const std::size_t idx = cursor + static_cast<std::size_t>(d.immOffset + i);
                if (idx < len) r.wildcardMask[idx] = true;
            }
            ++r.relBranchCount;
        }
        if (d.relDisp && d.dispOffset >= 0 && d.dispSize > 0) {
            for (int i = 0; i < d.dispSize; ++i) {
                const std::size_t idx = cursor + static_cast<std::size_t>(d.dispOffset + i);
                if (idx < len) r.wildcardMask[idx] = true;
            }
            ++r.ripRelCount;
        }
        cursor += static_cast<std::size_t>(d.length);
        ++r.instructionCount;

        // Stop AFTER the wildcards for this instruction have been applied — a
        // terminal `E9 rel32` still needs its target wildcarded, and checking
        // before that would have emitted the jump target as a literal.
        if (stopAtTerminal && d.terminal) {
            if (cursor < len) {
                // The requested length ran past the end of this code. Keep the
                // terminal instruction, drop the padding / bookkeeping / next
                // function that follows it.
                r.stoppedAtTerminal = true;
                r.length = cursor;
                r.bytes.resize(cursor);
                r.wildcardMask.resize(cursor);
                len = cursor;
            }
            break;
        }
    }

    // CE-style pattern: "48 89 5C 24 ?? E8 ?? ?? ?? ??".
    r.pattern.reserve(len * 3);
    for (std::size_t i = 0; i < len; ++i) {
        if (i > 0) r.pattern += ' ';
        if (r.wildcardMask[i]) {
            r.pattern += "??";
        } else {
            char buf[3];
            ::_snprintf_s(buf, sizeof(buf), _TRUNCATE, "%02X", bytes[i]);
            r.pattern.append(buf, 2);
        }
    }
    return r;
}

}  // namespace Discrete::Aob
