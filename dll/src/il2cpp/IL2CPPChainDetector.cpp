// IL2CPPChainDetector.cpp — implementation.
//
// Scan loop architecture
// ----------------------
// 1. Walk every executable section of GameAssembly.dll in 1 MB chunks
//    (matches IL2CPPFieldXref v1 and AOBMaker's RipReferenceScanner —
//    chunk size bounds peak working-set on 200 MB Songs-of-Silence-
//    class images).
// 2. Within each chunk, byte-by-byte byte-pattern match for the hop-1
//    opcodes: `mov reg, [rip+disp32]` (0x8B or 0x8D MOV / LEA, with
//    optional REX prefix). This is the same cheap pre-filter v1 used —
//    avoids decoding every byte with Zydis.
// 3. For each candidate, check `[rip+disp]` resolves to one of the
//    supplied class-pointer slots. If yes, Zydis-decode the hop-1
//    instruction (need the destination register) then forward-trace
//    up to 8 instructions looking for hop 2 + hop 3.
// 4. Emit `ChainMatch` for each successful chain.
//
// Why pre-filter before Zydis
// ---------------------------
// Zydis decode is fast but not free (~100 ns per instruction). At
// 200 K instructions per MB × 50 MB .text, that's ~1 s of pure decode
// even before chain logic runs. Byte-pattern pre-filter narrows to
// ~0.01% of bytes; full Zydis decode only fires on actual candidates.

#include "IL2CPPChainDetector.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/Memory.h"
#include "../shared/ModuleHelper.h"

#include <windows.h>

#include <Zydis/Zydis.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <unordered_set>
#include <vector>

namespace Discrete::IL2CPP {

namespace {

// Same shape as IL2CPPFieldXref::ExecutableSection / EnumerateExecutableSections.
// Duplicated rather than refactored to a header because the two files
// have slightly different defensive-read styles and the helper is
// tiny — pull into a shared header only if a 3rd consumer appears.
struct ExecSec {
    std::uintptr_t va;
    std::size_t    size;
};

std::vector<ExecSec> EnumerateExecutableSections(void* moduleBase) {
    std::vector<ExecSec> out;
    if (moduleBase == nullptr) return out;
    auto base = reinterpret_cast<std::uint8_t*>(moduleBase);

    auto dos = Memory::SafeRead<IMAGE_DOS_HEADER>(base);
    if (!dos || dos->e_magic != IMAGE_DOS_SIGNATURE) return out;
    auto nt = Memory::SafeRead<IMAGE_NT_HEADERS64>(base + dos->e_lfanew);
    if (!nt || nt->Signature != IMAGE_NT_SIGNATURE) return out;

    auto sectionsAddr = base + dos->e_lfanew
                        + offsetof(IMAGE_NT_HEADERS64, OptionalHeader)
                        + nt->FileHeader.SizeOfOptionalHeader;
    const std::uint16_t nSect = nt->FileHeader.NumberOfSections;
    out.reserve(nSect);
    for (std::uint16_t i = 0; i < nSect; ++i) {
        auto sh = Memory::SafeRead<IMAGE_SECTION_HEADER>(
            sectionsAddr + i * sizeof(IMAGE_SECTION_HEADER));
        if (!sh) break;
        if ((sh->Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0) continue;
        ExecSec s{
            reinterpret_cast<std::uintptr_t>(base) + sh->VirtualAddress,
            sh->Misc.VirtualSize,
        };
        out.push_back(s);
    }
    return out;
}

// --- Byte-pattern pre-filter helpers (same as IL2CPPFieldXref v1) ----------
inline bool IsRexByte(std::uint8_t b) noexcept {
    return (b & 0xF0u) == 0x40u;
}
inline bool IsRipRelativeModrm(std::uint8_t m) noexcept {
    return (m & 0xC7u) == 0x05u;  // mod=00, r/m=101
}
inline std::int32_t ReadInt32LE(const std::uint8_t* p) noexcept {
    return  static_cast<std::int32_t>(
            static_cast<std::uint32_t>(p[0])
          | (static_cast<std::uint32_t>(p[1]) <<  8)
          | (static_cast<std::uint32_t>(p[2]) << 16)
          | (static_cast<std::uint32_t>(p[3]) << 24));
}

// Returns true if the bytes at `p` (up to `avail` bytes) look like a
// `mov reg, [rip+disp32]` or `lea reg, [rip+disp32]` instruction whose
// target equals `expectedTarget`. Sets `*outInsnStart` to the start of
// the instruction (handles REX prefix offset) and `*outInsnLen` to the
// total length. Only the 6-byte (no REX) and 7-byte (with REX) forms
// are recognised — covers the hop-1 codegen IL2CPP emits.
bool MatchHopOneOpcode(const std::uint8_t* p, std::size_t avail,
                       std::uintptr_t      thisIP,
                       std::uintptr_t      expectedTarget,
                       std::size_t*        outInsnLen) {
    if (avail < 6) return false;
    std::size_t off = 0;
    if (IsRexByte(p[0])) { ++off; if (avail < 7) return false; }
    if (avail - off < 6) return false;
    std::uint8_t op = p[off];
    if (op != 0x8B && op != 0x8D) return false;
    if (!IsRipRelativeModrm(p[off + 1])) return false;
    std::int32_t disp = ReadInt32LE(p + off + 2);
    std::size_t  len  = off + 2 + 4;
    if (avail < len) return false;
    std::uintptr_t target = thisIP + len + static_cast<std::intptr_t>(disp);
    if (target != expectedTarget) return false;
    *outInsnLen = len;
    return true;
}

// Canonicalise a register to its 64-bit enclosing form so rax / eax /
// ax / al all compare equal. ZYDIS_REGISTER_NONE stays NONE.
ZydisRegister Canonical(ZydisRegister r) {
    if (r == ZYDIS_REGISTER_NONE) return r;
    ZydisRegister canon = ZydisRegisterGetLargestEnclosing(
        ZYDIS_MACHINE_MODE_LONG_64, r);
    return canon != ZYDIS_REGISTER_NONE ? canon : r;
}

// (former single-reg-clobber helper `WritesRegister` removed —
// multi-register ChainState below does its own per-operand iteration.)

// Tries to match the hop-2 pattern at `instr`:
//     mov R2, [classPtrReg + staticFieldsOffset]
// On match, sets `*outDestReg` to R2 (canonicalised) and returns true.
bool MatchHopTwo(const ZydisDecodedInstruction& instr,
                 const ZydisDecodedOperand*     ops,
                 ZydisRegister                  classPtrReg,
                 std::size_t                    staticFieldsOffset,
                 ZydisRegister*                 outDestReg) {
    if (instr.mnemonic != ZYDIS_MNEMONIC_MOV) return false;
    if (instr.operand_count_visible < 2) return false;
    if (ops[0].type != ZYDIS_OPERAND_TYPE_REGISTER) return false;
    if (ops[1].type != ZYDIS_OPERAND_TYPE_MEMORY)   return false;
    if (Canonical(ops[1].mem.base) != classPtrReg)  return false;
    if (ops[1].mem.index != ZYDIS_REGISTER_NONE)    return false;
    if (ops[1].mem.disp.size == 0)                  return false;  // no disp present
    if (static_cast<std::size_t>(ops[1].mem.disp.value) != staticFieldsOffset)
        return false;
    *outDestReg = Canonical(ops[0].reg.value);
    return true;
}

// Tries to match the hop-3 read pattern:
//     mov R3, [staticFieldsReg + targetFieldOffset]
//   OR
//     lea R3, [staticFieldsReg + targetFieldOffset]
//   OR
//     movsxd R3, [...]   (sign-extend load, common for int32 → int64)
//   OR
//     movss / movsd xmm, [...]  (scalar SSE load for float/double)
bool MatchHopThreeRead(const ZydisDecodedInstruction& instr,
                       const ZydisDecodedOperand*     ops,
                       ZydisRegister                  staticFieldsReg,
                       std::size_t                    targetFieldOffset) {
    const bool mnemonicOk =
        instr.mnemonic == ZYDIS_MNEMONIC_MOV    ||
        instr.mnemonic == ZYDIS_MNEMONIC_LEA    ||
        instr.mnemonic == ZYDIS_MNEMONIC_MOVSXD ||
        instr.mnemonic == ZYDIS_MNEMONIC_MOVZX  ||
        instr.mnemonic == ZYDIS_MNEMONIC_MOVSX  ||
        instr.mnemonic == ZYDIS_MNEMONIC_MOVSS  ||
        instr.mnemonic == ZYDIS_MNEMONIC_MOVSD;
    if (!mnemonicOk) return false;
    if (instr.operand_count_visible < 2) return false;
    if (ops[0].type != ZYDIS_OPERAND_TYPE_REGISTER) return false;
    if (ops[1].type != ZYDIS_OPERAND_TYPE_MEMORY)   return false;
    if (Canonical(ops[1].mem.base) != staticFieldsReg) return false;
    if (ops[1].mem.index != ZYDIS_REGISTER_NONE)    return false;
    if (ops[1].mem.disp.size == 0)                  return false;  // no disp present
    return static_cast<std::size_t>(ops[1].mem.disp.value) == targetFieldOffset;
}

// Tries to match the hop-3 write pattern:
//     mov [staticFieldsReg + targetFieldOffset], R3
//   OR
//     mov [staticFieldsReg + targetFieldOffset], imm  (constant store)
//   OR
//     movss / movsd [...], xmm
bool MatchHopThreeWrite(const ZydisDecodedInstruction& instr,
                        const ZydisDecodedOperand*     ops,
                        ZydisRegister                  staticFieldsReg,
                        std::size_t                    targetFieldOffset) {
    const bool mnemonicOk =
        instr.mnemonic == ZYDIS_MNEMONIC_MOV   ||
        instr.mnemonic == ZYDIS_MNEMONIC_MOVSS ||
        instr.mnemonic == ZYDIS_MNEMONIC_MOVSD;
    if (!mnemonicOk) return false;
    if (instr.operand_count_visible < 2) return false;
    if (ops[0].type != ZYDIS_OPERAND_TYPE_MEMORY)    return false;
    // op1 can be REGISTER or IMMEDIATE (mov [mem], imm32 form).
    if (ops[1].type != ZYDIS_OPERAND_TYPE_REGISTER &&
        ops[1].type != ZYDIS_OPERAND_TYPE_IMMEDIATE) return false;
    if (Canonical(ops[0].mem.base) != staticFieldsReg) return false;
    if (ops[0].mem.index != ZYDIS_REGISTER_NONE)    return false;
    if (ops[0].mem.disp.size == 0)                  return false;  // no disp present
    return static_cast<std::size_t>(ops[0].mem.disp.value) == targetFieldOffset;
}

// MS x64 ABI volatile registers — clobbered by CALL. Drop these from
// the chain-tracking set when we see a CALL; non-volatile registers
// (RBX/RBP/RSI/RDI/R12-R15) survive across the call and may carry the
// class-pointer or static-fields-pointer across the il2cpp_runtime_class_init
// barrier that IL2CPP inserts before lazy-init static-field accesses.
constexpr ZydisRegister kVolatileRegs[] = {
    ZYDIS_REGISTER_RAX, ZYDIS_REGISTER_RCX, ZYDIS_REGISTER_RDX,
    ZYDIS_REGISTER_R8,  ZYDIS_REGISTER_R9,  ZYDIS_REGISTER_R10,
    ZYDIS_REGISTER_R11,
};

// Multi-register tracking state. Replaces the single-register tracking
// from v2-of-v2's first cut (which couldn't handle the very common
// IL2CPP pattern where the compiler copies the class pointer to a
// non-volatile RBX before calling `il2cpp_runtime_class_init`).
//
// Two parallel sets of canonical 64-bit registers:
//   classPtrRegs    — registers currently holding the target Il2CppClass*
//   staticFieldsRegs — registers currently holding the static_fields ptr
//                      (populated only after we observe a successful hop-2)
//
// Set membership is checked / updated as each instruction is traced:
//   * MOV reg, reg → propagate source tags to dest (after clobbering dest)
//   * CALL → drop volatile regs from both sets
//   * Any reg write → remove dest from both sets (then re-add tags for
//     the special MOV-propagation / hop-2-match cases that explicitly
//     re-tag the dest)
//
// Linear vector + std::find — sets are typically 1-4 entries, the
// overhead of std::set / std::unordered_set isn't worth it.
struct ChainState {
    std::vector<ZydisRegister> classPtrRegs;
    std::vector<ZydisRegister> staticFieldsRegs;

    bool ContainsClassPtr(ZydisRegister r) const noexcept {
        return std::find(classPtrRegs.begin(), classPtrRegs.end(), r)
               != classPtrRegs.end();
    }
    bool ContainsStaticFields(ZydisRegister r) const noexcept {
        return std::find(staticFieldsRegs.begin(), staticFieldsRegs.end(), r)
               != staticFieldsRegs.end();
    }
    void AddClassPtr(ZydisRegister r) {
        if (r != ZYDIS_REGISTER_NONE && !ContainsClassPtr(r))
            classPtrRegs.push_back(r);
    }
    void AddStaticFields(ZydisRegister r) {
        if (r != ZYDIS_REGISTER_NONE && !ContainsStaticFields(r))
            staticFieldsRegs.push_back(r);
    }
    void RemoveReg(ZydisRegister r) {
        if (r == ZYDIS_REGISTER_NONE) return;
        classPtrRegs.erase(
            std::remove(classPtrRegs.begin(), classPtrRegs.end(), r),
            classPtrRegs.end());
        staticFieldsRegs.erase(
            std::remove(staticFieldsRegs.begin(), staticFieldsRegs.end(), r),
            staticFieldsRegs.end());
    }
    bool Empty() const noexcept {
        return classPtrRegs.empty() && staticFieldsRegs.empty();
    }
};

// Forward-trace from `start` (hop-1 instruction's address, register
// holding class pointer = `initialClassPtrReg`). Returns true on
// success + populates `*outHitIP` + `*outKind`. Returns false if the
// chain breaks (state empty, instruction limit, decode error).
//
// Window of 32 instructions: empirically large enough to absorb the
// init-call pattern (hop-1 → save-to-rbx → init-flag-check → cond-jmp
// → call init → re-merge → hop-2 → hop-3 ≈ 10-15 insns) plus any
// scheduling slack the compiler inserts, without admitting false
// chains across function boundaries (a RET still terminates the trace).
constexpr std::size_t kMaxForwardTrace = 32;

bool TryForwardTrace(ZydisDecoder*       decoder,
                     const std::uint8_t* startBytes,
                     std::size_t         availBytes,
                     std::uintptr_t      startIP,
                     ZydisRegister       initialClassPtrReg,
                     std::size_t         staticFieldsOffset,
                     std::size_t         targetFieldOffset,
                     std::uintptr_t*     outHitIP,
                     std::string*        outKind) {
    ChainState     state;
    state.AddClassPtr(initialClassPtrReg);
    std::size_t    consumed = 0;
    std::uintptr_t ip       = startIP;

    for (std::size_t step = 0; step < kMaxForwardTrace; ++step) {
        if (consumed >= availBytes) return false;
        ZydisDecodedInstruction instr;
        ZydisDecodedOperand     operands[ZYDIS_MAX_OPERAND_COUNT];
        ZyanStatus status = ZydisDecoderDecodeFull(
            decoder, startBytes + consumed, availBytes - consumed,
            &instr, operands);
        if (!ZYAN_SUCCESS(status)) return false;

        // --- hop-3 match (highest priority — terminal) ------------------
        // Try every live static-fields reg against the read/write patterns.
        for (auto sf : state.staticFieldsRegs) {
            if (MatchHopThreeRead(instr, operands, sf, targetFieldOffset)) {
                *outHitIP = ip;
                *outKind  = "read";
                return true;
            }
            if (MatchHopThreeWrite(instr, operands, sf, targetFieldOffset)) {
                *outHitIP = ip;
                *outKind  = "write";
                return true;
            }
        }

        // --- terminal categories ----------------------------------------
        // RET always ends the trace (left the function).
        // UNCOND_BR with unknown / off-window target also terminates —
        //   could be a jump back into a different basic block.
        if (instr.meta.category == ZYDIS_CATEGORY_RET) return false;
        if (instr.meta.category == ZYDIS_CATEGORY_UNCOND_BR) return false;

        // --- hop-2 match (records new static-fields reg) ----------------
        // Try each live class-pointer reg. First match wins — same
        // instruction can't match hop-2 with two different bases.
        ZydisRegister newSfReg = ZYDIS_REGISTER_NONE;
        bool hopTwoMatched = false;
        for (auto cp : state.classPtrRegs) {
            if (MatchHopTwo(instr, operands, cp, staticFieldsOffset,
                            &newSfReg)) {
                hopTwoMatched = true;
                break;
            }
        }

        // --- register state update --------------------------------------
        const ZydisInstructionCategory cat = instr.meta.category;

        if (cat == ZYDIS_CATEGORY_CALL) {
            // CALL clobbers volatile registers. Drop them from both
            // sets; non-volatile regs (RBX/RBP/RSI/RDI/R12-R15) keep
            // their tags. The il2cpp_runtime_class_init call site is
            // the canonical example — class ptr lives in RBX across
            // the call.
            for (auto r : kVolatileRegs) state.RemoveReg(r);
        } else if (cat == ZYDIS_CATEGORY_COND_BR) {
            // Skip — let fall-through trace continue. Init-check
            // patterns use `jnz skip_init` to bypass the init call
            // when the class is already initialized; the fall-through
            // (= init not yet done, perform init) eventually merges
            // back to the same hop-2 site. Tracing fall-through
            // catches both paths.
            //
            // Note: we don't follow the branch target. If the chain's
            // hop-2 is on the taken-branch path only, we miss it.
            // Acceptable for v1; refine if a real-world miss surfaces.
            //
            // Critical: do NOT update state (the branch insn itself
            // doesn't write any tracked reg).
        } else if (instr.mnemonic == ZYDIS_MNEMONIC_MOV &&
                   instr.operand_count_visible >= 2 &&
                   operands[0].type == ZYDIS_OPERAND_TYPE_REGISTER &&
                   operands[1].type == ZYDIS_OPERAND_TYPE_REGISTER) {
            // Register-to-register MOV — propagation. Read source tags
            // BEFORE clobbering dest (in case dest == source, which is
            // a no-op anyway but the analysis still works).
            const ZydisRegister dst = Canonical(operands[0].reg.value);
            const ZydisRegister src = Canonical(operands[1].reg.value);
            const bool srcIsClassPtr     = state.ContainsClassPtr(src);
            const bool srcIsStaticFields = state.ContainsStaticFields(src);
            state.RemoveReg(dst);
            if (srcIsClassPtr)     state.AddClassPtr(dst);
            if (srcIsStaticFields) state.AddStaticFields(dst);
        } else {
            // Standard clobber: any reg this insn writes loses its tag.
            // Iterate operands looking for register WRITEs.
            for (std::uint8_t i = 0; i < instr.operand_count_visible; ++i) {
                const auto& op = operands[i];
                if (op.type != ZYDIS_OPERAND_TYPE_REGISTER) continue;
                if ((op.actions & ZYDIS_OPERAND_ACTION_MASK_WRITE) == 0) continue;
                state.RemoveReg(Canonical(op.reg.value));
            }
        }

        // Hop-2 match's dest gets re-added AFTER the clobber pass
        // (the hop-2 instruction IS a write to its dest reg, which
        // we just removed in the standard clobber branch above). The
        // re-add re-tags the dest as the new static-fields-ptr reg.
        if (hopTwoMatched && newSfReg != ZYDIS_REGISTER_NONE) {
            state.AddStaticFields(newSfReg);
        }

        // No more live registers → chain dead, abandon.
        if (state.Empty()) return false;

        consumed += instr.length;
        ip       += instr.length;
    }
    return false;
}

// Decode the hop-1 instruction with Zydis to extract its destination
// register. The byte-pattern pre-filter already validated the opcode +
// addressing mode, but we still need Zydis to recover the dest reg
// (handles REX.R bit cleanly).
ZydisRegister ExtractHopOneDestReg(ZydisDecoder*       decoder,
                                    const std::uint8_t* bytes,
                                    std::size_t         len) {
    ZydisDecodedInstruction instr;
    ZydisDecodedOperand     operands[ZYDIS_MAX_OPERAND_COUNT];
    ZyanStatus status = ZydisDecoderDecodeFull(
        decoder, bytes, len, &instr, operands);
    if (!ZYAN_SUCCESS(status)) return ZYDIS_REGISTER_NONE;
    if (instr.operand_count_visible < 1) return ZYDIS_REGISTER_NONE;
    if (operands[0].type != ZYDIS_OPERAND_TYPE_REGISTER) return ZYDIS_REGISTER_NONE;
    return Canonical(operands[0].reg.value);
}

}  // namespace

std::vector<ChainMatch> DetectFieldAccessChains(
    const std::vector<ClassPointerSlot>& classPointerSlots,
    std::size_t                          staticFieldsOffset,
    std::size_t                          targetFieldOffset,
    std::size_t                          matchLimit,
    ChainScanStats*                      stats) {

    std::vector<ChainMatch> result;
    if (classPointerSlots.empty() || staticFieldsOffset == 0) {
        return result;
    }

    void* gaBase = ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP);
    if (gaBase == nullptr) return result;

    auto sections = EnumerateExecutableSections(gaBase);
    if (sections.empty()) return result;

    // O(1) slot lookup. Collect targets into a hash set.
    std::unordered_set<std::uintptr_t> slotSet;
    slotSet.reserve(classPointerSlots.size() * 2);
    for (const auto& s : classPointerSlots) slotSet.insert(s.address);

    ZydisDecoder decoder;
    ZydisDecoderInit(&decoder, ZYDIS_MACHINE_MODE_LONG_64, ZYDIS_STACK_WIDTH_64);

    // 1 MB chunk + small tail overlap (16 bytes for the max forward-trace
    // window — 8 instructions × max 15 bytes each ≈ 120 bytes, but most
    // chains complete in 3 instructions ≈ 21 bytes). 64 bytes is plenty.
    constexpr std::size_t kChunkSize = 1024u * 1024u;
    constexpr std::size_t kTailOverlap = 128u;
    std::vector<std::uint8_t> buf(kChunkSize + kTailOverlap);

    std::size_t hopOneCandidates = 0;
    std::size_t chainBroken      = 0;
    std::size_t failedReads      = 0;
    std::size_t bytesScanned     = 0;
    bool truncated = false;

    for (const auto& sec : sections) {
        if (truncated) break;
        std::size_t pos = 0;
        while (pos < sec.size && !truncated) {
            std::size_t want     = (sec.size - pos) < kChunkSize
                ? (sec.size - pos) : kChunkSize;
            std::size_t readWant = want + kTailOverlap;
            if (pos + readWant > sec.size) readWant = sec.size - pos;
            if (readWant < 6) break;

            const std::uint8_t* secBase =
                reinterpret_cast<const std::uint8_t*>(sec.va);
            if (!Memory::SafeReadBytes(secBase + pos, buf.data(), readWant)) {
                ++failedReads;
                pos += 4096u;
                continue;
            }

            // Only emit candidates whose start IP falls within the
            // "owned" portion of the chunk; overlap bytes are read
            // for context only.
            const std::size_t emitEnd = want;
            const std::size_t scanEnd = (readWant >= 6)
                ? std::min(emitEnd, readWant - 6 + 1u)
                : 0u;

            for (std::size_t p = 0; p < scanEnd; ++p) {
                if (truncated) break;
                const std::size_t avail = readWant - p;
                const std::uintptr_t insnIP = sec.va + pos + p;

                // Build #565-followup bugfix — REX-skip dedup. A 7-byte
                // REX-prefixed insn `48 8B 05 ...` would otherwise match
                // TWICE: once at the REX byte (7-byte form), once at the
                // op byte (6-byte form would interpret the 0x8B as a
                // standalone opcode). Same disp, same resolved target,
                // same hit. Detected live on SaGa F2R: SteamManager
                // hits showed up doubled (4 rows for get_Instance when
                // real chain count was 2). Skipping when the previous
                // byte was a REX prefix means the 7-byte form already
                // covered this instruction at p-1.
                if (p > 0 && IsRexByte(buf[p - 1])) continue;

                // Try every slot's target (still O(1) per slot via hash).
                // In practice we just check if THIS insn's resolved
                // target is in slotSet — cheaper than nested loop.
                std::size_t insnLen = 0;
                // The pre-filter doesn't know our target set, so we
                // compute the resolved target speculatively then check.
                // Inline the same checks as MatchHopOneOpcode but skip
                // the target equality (we'll do it via slotSet).
                std::size_t off = 0;
                if (IsRexByte(buf[p])) {
                    if (avail < 7) continue;
                    ++off;
                }
                if (avail - off < 6) continue;
                std::uint8_t op = buf[p + off];
                if (op != 0x8B && op != 0x8D) continue;
                if (!IsRipRelativeModrm(buf[p + off + 1])) continue;
                std::int32_t disp = ReadInt32LE(buf.data() + p + off + 2);
                insnLen = off + 2 + 4;
                if (avail < insnLen) continue;
                std::uintptr_t target = insnIP + insnLen
                                        + static_cast<std::intptr_t>(disp);
                if (slotSet.find(target) == slotSet.end()) continue;

                // Hop-1 candidate confirmed. Extract destination reg.
                ++hopOneCandidates;
                ZydisRegister classPtrReg =
                    ExtractHopOneDestReg(&decoder, buf.data() + p, insnLen);
                if (classPtrReg == ZYDIS_REGISTER_NONE) continue;

                // Forward-trace from the next instruction onwards.
                std::uintptr_t hitIP = 0;
                std::string    kind;
                bool found = TryForwardTrace(
                    &decoder,
                    buf.data() + p + insnLen,
                    avail - insnLen,
                    insnIP + insnLen,
                    classPtrReg,
                    staticFieldsOffset,
                    targetFieldOffset,
                    &hitIP, &kind);

                if (!found) {
                    ++chainBroken;
                    continue;
                }

                ChainMatch m;
                m.hitIP    = hitIP;
                m.hopOneIP = insnIP;
                m.refKind  = std::move(kind);
                result.push_back(std::move(m));
                if (result.size() >= matchLimit) {
                    truncated = true;
                    break;
                }
            }
            bytesScanned += want;
            pos += want;
        }
    }

    // Build #565-followup — dedup by hitIP. Multiple hop-1 candidates
    // can converge on the same hop-3 instruction when the compiler
    // emits 2+ class-pointer loads at different IPs that all fall
    // through to the same field-access basic block (canonical IL2CPP
    // pattern: pre-init check load + post-init reload, both reach the
    // same `mov rax, [rbx + 0xB8]` → same hop-3 IP). Without dedup the
    // user sees N copies of every match. Stable sort + unique by hitIP
    // preserves insertion order (first-detected match wins).
    if (result.size() > 1) {
        std::sort(result.begin(), result.end(),
            [](const ChainMatch& a, const ChainMatch& b) {
                return a.hitIP < b.hitIP;
            });
        result.erase(
            std::unique(result.begin(), result.end(),
                [](const ChainMatch& a, const ChainMatch& b) {
                    return a.hitIP == b.hitIP;
                }),
            result.end());
    }

    if (stats != nullptr) {
        stats->hopOneCandidates = hopOneCandidates;
        stats->chainBroken      = chainBroken;
        stats->chainsEmitted    = result.size();
        stats->bytesScanned     = bytesScanned;
        stats->failedReads      = failedReads;
    }
    return result;
}

}  // namespace Discrete::IL2CPP
