// Chain-detector oracle (Zydis side).
//
// * oracle_detect: the legacy Zydis-based DetectFieldAccessChains, transcribed
//   from dll/src/il2cpp/IL2CPPChainDetector.cpp (deleted after the R2 port;
//   last present in commit aae48d3) with only the host-memory and module
//   lookups swapped for a read callback. Reference for the end-to-end test.
// * oracle_project: per-instruction "behavioural projection" - exactly the
//   Zydis facts the legacy trace consulted, formatted like
//   chain::tests::project on the Rust side. Everything after " |" is
//   diagnostics and is not compared.
// * oracle_msvc_sort: MSVC std::sort reference for chain/msvc_sort.rs.
//
// Decoder: ZydisDecoderInit(LONG_64, STACK_WIDTH_64) with no mode changes,
// exactly as the legacy code initialised it.
#define NOMINMAX
#include <windows.h>

#include <Zydis/Zydis.h>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <optional>
#include <string>
#include <unordered_set>
#include <vector>

typedef int32_t (*read_fn)(void* ctx, uintptr_t addr, uint8_t* dst, size_t len);
// thread_local: oracle_detect may run on several test threads at once.
static thread_local read_fn g_read = nullptr;
static thread_local void*   g_ctx  = nullptr;

namespace Memory {
inline bool SafeReadBytes(const void* addr, void* dst, std::size_t size) noexcept {
    if (!addr || !dst || size == 0) return false;
    return g_read(g_ctx, reinterpret_cast<uintptr_t>(addr), static_cast<uint8_t*>(dst), size) == 1;
}
template <typename T>
std::optional<T> SafeRead(const void* addr) noexcept {
    T value{};
    if (!SafeReadBytes(addr, &value, sizeof(T))) return std::nullopt;
    return value;
}
}  // namespace Memory

struct ClassPointerSlot { std::uintptr_t address; std::uintptr_t sectionVa; };
struct ChainMatch { std::uintptr_t hitIP = 0; std::uintptr_t hopOneIP = 0; std::string refKind; };
struct ChainScanStats {
    std::size_t hopOneCandidates = 0, chainBroken = 0, chainsEmitted = 0, bytesScanned = 0, failedReads = 0;
};

namespace {

struct ExecSec { std::uintptr_t va; std::size_t size; };

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
        ExecSec s{ reinterpret_cast<std::uintptr_t>(base) + sh->VirtualAddress, sh->Misc.VirtualSize };
        out.push_back(s);
    }
    return out;
}

inline bool IsRexByte(std::uint8_t b) noexcept { return (b & 0xF0u) == 0x40u; }
inline bool IsRipRelativeModrm(std::uint8_t m) noexcept { return (m & 0xC7u) == 0x05u; }
inline std::int32_t ReadInt32LE(const std::uint8_t* p) noexcept {
    return static_cast<std::int32_t>(static_cast<std::uint32_t>(p[0]) | (static_cast<std::uint32_t>(p[1]) << 8)
          | (static_cast<std::uint32_t>(p[2]) << 16) | (static_cast<std::uint32_t>(p[3]) << 24));
}

ZydisRegister Canonical(ZydisRegister r) {
    if (r == ZYDIS_REGISTER_NONE) return r;
    ZydisRegister canon = ZydisRegisterGetLargestEnclosing(ZYDIS_MACHINE_MODE_LONG_64, r);
    return canon != ZYDIS_REGISTER_NONE ? canon : r;
}

std::size_t DispOrZero(const ZydisDecodedOperandMem& mem) {
    return mem.disp.size == 0 ? 0u : static_cast<std::size_t>(mem.disp.value);
}

bool MatchHopTwo(const ZydisDecodedInstruction& instr, const ZydisDecodedOperand* ops,
                 ZydisRegister classPtrReg, std::size_t staticFieldsOffset, ZydisRegister* outDestReg) {
    if (instr.mnemonic != ZYDIS_MNEMONIC_MOV) return false;
    if (instr.operand_count_visible < 2) return false;
    if (ops[0].type != ZYDIS_OPERAND_TYPE_REGISTER) return false;
    if (ops[1].type != ZYDIS_OPERAND_TYPE_MEMORY)   return false;
    if (Canonical(ops[1].mem.base) != classPtrReg)  return false;
    if (ops[1].mem.index != ZYDIS_REGISTER_NONE)    return false;
    if (DispOrZero(ops[1].mem) != staticFieldsOffset) return false;
    *outDestReg = Canonical(ops[0].reg.value);
    return true;
}

bool MatchHopThreeRead(const ZydisDecodedInstruction& instr, const ZydisDecodedOperand* ops,
                       ZydisRegister staticFieldsReg, std::size_t targetFieldOffset) {
    const bool mnemonicOk =
        instr.mnemonic == ZYDIS_MNEMONIC_MOV || instr.mnemonic == ZYDIS_MNEMONIC_LEA ||
        instr.mnemonic == ZYDIS_MNEMONIC_MOVSXD || instr.mnemonic == ZYDIS_MNEMONIC_MOVZX ||
        instr.mnemonic == ZYDIS_MNEMONIC_MOVSX || instr.mnemonic == ZYDIS_MNEMONIC_MOVSS ||
        instr.mnemonic == ZYDIS_MNEMONIC_MOVSD;
    if (!mnemonicOk) return false;
    if (instr.operand_count_visible < 2) return false;
    if (ops[0].type != ZYDIS_OPERAND_TYPE_REGISTER) return false;
    if (ops[1].type != ZYDIS_OPERAND_TYPE_MEMORY)   return false;
    if (Canonical(ops[1].mem.base) != staticFieldsReg) return false;
    if (ops[1].mem.index != ZYDIS_REGISTER_NONE)    return false;
    return DispOrZero(ops[1].mem) == targetFieldOffset;
}

bool MatchHopThreeWrite(const ZydisDecodedInstruction& instr, const ZydisDecodedOperand* ops,
                        ZydisRegister staticFieldsReg, std::size_t targetFieldOffset) {
    const bool mnemonicOk = instr.mnemonic == ZYDIS_MNEMONIC_MOV || instr.mnemonic == ZYDIS_MNEMONIC_MOVSS ||
                            instr.mnemonic == ZYDIS_MNEMONIC_MOVSD;
    if (!mnemonicOk) return false;
    if (instr.operand_count_visible < 2) return false;
    if (ops[0].type != ZYDIS_OPERAND_TYPE_MEMORY)    return false;
    if (ops[1].type != ZYDIS_OPERAND_TYPE_REGISTER && ops[1].type != ZYDIS_OPERAND_TYPE_IMMEDIATE) return false;
    if (Canonical(ops[0].mem.base) != staticFieldsReg) return false;
    if (ops[0].mem.index != ZYDIS_REGISTER_NONE)    return false;
    return DispOrZero(ops[0].mem) == targetFieldOffset;
}

constexpr ZydisRegister kVolatileRegs[] = {
    ZYDIS_REGISTER_RAX, ZYDIS_REGISTER_RCX, ZYDIS_REGISTER_RDX, ZYDIS_REGISTER_R8, ZYDIS_REGISTER_R9,
    ZYDIS_REGISTER_R10, ZYDIS_REGISTER_R11,
};

struct ChainState {
    std::vector<ZydisRegister> classPtrRegs;
    std::vector<ZydisRegister> staticFieldsRegs;
    bool ContainsClassPtr(ZydisRegister r) const noexcept {
        return std::find(classPtrRegs.begin(), classPtrRegs.end(), r) != classPtrRegs.end();
    }
    bool ContainsStaticFields(ZydisRegister r) const noexcept {
        return std::find(staticFieldsRegs.begin(), staticFieldsRegs.end(), r) != staticFieldsRegs.end();
    }
    void AddClassPtr(ZydisRegister r) { if (r != ZYDIS_REGISTER_NONE && !ContainsClassPtr(r)) classPtrRegs.push_back(r); }
    void AddStaticFields(ZydisRegister r) { if (r != ZYDIS_REGISTER_NONE && !ContainsStaticFields(r)) staticFieldsRegs.push_back(r); }
    void RemoveReg(ZydisRegister r) {
        if (r == ZYDIS_REGISTER_NONE) return;
        classPtrRegs.erase(std::remove(classPtrRegs.begin(), classPtrRegs.end(), r), classPtrRegs.end());
        staticFieldsRegs.erase(std::remove(staticFieldsRegs.begin(), staticFieldsRegs.end(), r), staticFieldsRegs.end());
    }
    bool Empty() const noexcept { return classPtrRegs.empty() && staticFieldsRegs.empty(); }
};

constexpr std::size_t kMaxForwardTrace = 32;

bool TryForwardTrace(ZydisDecoder* decoder, const std::uint8_t* startBytes, std::size_t availBytes,
                     std::uintptr_t startIP, ZydisRegister initialClassPtrReg, std::size_t staticFieldsOffset,
                     std::size_t targetFieldOffset, std::uintptr_t* outHitIP, std::string* outKind) {
    ChainState state;
    state.AddClassPtr(initialClassPtrReg);
    std::size_t consumed = 0;
    std::uintptr_t ip = startIP;
    for (std::size_t step = 0; step < kMaxForwardTrace; ++step) {
        if (consumed >= availBytes) return false;
        ZydisDecodedInstruction instr;
        ZydisDecodedOperand operands[ZYDIS_MAX_OPERAND_COUNT];
        ZyanStatus status = ZydisDecoderDecodeFull(decoder, startBytes + consumed, availBytes - consumed, &instr, operands);
        if (!ZYAN_SUCCESS(status)) return false;
        for (auto sf : state.staticFieldsRegs) {
            if (MatchHopThreeRead(instr, operands, sf, targetFieldOffset)) { *outHitIP = ip; *outKind = "read"; return true; }
            if (MatchHopThreeWrite(instr, operands, sf, targetFieldOffset)) { *outHitIP = ip; *outKind = "write"; return true; }
        }
        if (instr.meta.category == ZYDIS_CATEGORY_RET) return false;
        if (instr.meta.category == ZYDIS_CATEGORY_UNCOND_BR) return false;
        ZydisRegister newSfReg = ZYDIS_REGISTER_NONE;
        bool hopTwoMatched = false;
        for (auto cp : state.classPtrRegs) {
            if (MatchHopTwo(instr, operands, cp, staticFieldsOffset, &newSfReg)) { hopTwoMatched = true; break; }
        }
        const ZydisInstructionCategory cat = instr.meta.category;
        if (cat == ZYDIS_CATEGORY_CALL) {
            for (auto r : kVolatileRegs) state.RemoveReg(r);
        } else if (cat == ZYDIS_CATEGORY_COND_BR) {
        } else if (instr.mnemonic == ZYDIS_MNEMONIC_MOV && instr.operand_count_visible >= 2 &&
                   operands[0].type == ZYDIS_OPERAND_TYPE_REGISTER && operands[1].type == ZYDIS_OPERAND_TYPE_REGISTER) {
            const ZydisRegister dst = Canonical(operands[0].reg.value);
            const ZydisRegister src = Canonical(operands[1].reg.value);
            const bool srcIsClassPtr = state.ContainsClassPtr(src);
            const bool srcIsStaticFields = state.ContainsStaticFields(src);
            state.RemoveReg(dst);
            if (srcIsClassPtr) state.AddClassPtr(dst);
            if (srcIsStaticFields) state.AddStaticFields(dst);
        } else {
            for (std::uint8_t i = 0; i < instr.operand_count_visible; ++i) {
                const auto& op = operands[i];
                if (op.type != ZYDIS_OPERAND_TYPE_REGISTER) continue;
                if ((op.actions & ZYDIS_OPERAND_ACTION_MASK_WRITE) == 0) continue;
                state.RemoveReg(Canonical(op.reg.value));
            }
        }
        if (hopTwoMatched && newSfReg != ZYDIS_REGISTER_NONE) state.AddStaticFields(newSfReg);
        if (state.Empty()) return false;
        consumed += instr.length;
        ip += instr.length;
    }
    return false;
}

ZydisRegister ExtractHopOneDestReg(ZydisDecoder* decoder, const std::uint8_t* bytes, std::size_t len) {
    ZydisDecodedInstruction instr;
    ZydisDecodedOperand operands[ZYDIS_MAX_OPERAND_COUNT];
    ZyanStatus status = ZydisDecoderDecodeFull(decoder, bytes, len, &instr, operands);
    if (!ZYAN_SUCCESS(status)) return ZYDIS_REGISTER_NONE;
    if (instr.operand_count_visible < 1) return ZYDIS_REGISTER_NONE;
    if (operands[0].type != ZYDIS_OPERAND_TYPE_REGISTER) return ZYDIS_REGISTER_NONE;
    return Canonical(operands[0].reg.value);
}

}  // namespace

static std::vector<ChainMatch> DetectFieldAccessChains(void* gaBase,
    const std::vector<ClassPointerSlot>& classPointerSlots, std::size_t staticFieldsOffset,
    std::size_t targetFieldOffset, std::size_t matchLimit, ChainScanStats* stats) {
    std::vector<ChainMatch> result;
    if (classPointerSlots.empty() || staticFieldsOffset == 0) return result;
    if (gaBase == nullptr) return result;
    auto sections = EnumerateExecutableSections(gaBase);
    if (sections.empty()) return result;
    std::unordered_set<std::uintptr_t> slotSet;
    slotSet.reserve(classPointerSlots.size() * 2);
    for (const auto& s : classPointerSlots) slotSet.insert(s.address);
    ZydisDecoder decoder;
    ZydisDecoderInit(&decoder, ZYDIS_MACHINE_MODE_LONG_64, ZYDIS_STACK_WIDTH_64);
    constexpr std::size_t kChunkSize = 1024u * 1024u;
    constexpr std::size_t kTailOverlap = 128u;
    std::vector<std::uint8_t> buf(kChunkSize + kTailOverlap);
    std::size_t hopOneCandidates = 0, chainBroken = 0, failedReads = 0, bytesScanned = 0;
    bool truncated = false;
    struct PairHash {
        std::size_t operator()(const std::pair<std::uintptr_t, std::uintptr_t>& v) const noexcept {
            return std::hash<std::uintptr_t>{}(v.first) ^ (std::hash<std::uintptr_t>{}(v.second) << 1);
        }
    };
    std::unordered_set<std::pair<std::uintptr_t, std::uintptr_t>, PairHash> emitted;
    for (const auto& sec : sections) {
        if (truncated) break;
        std::size_t pos = 0;
        while (pos < sec.size && !truncated) {
            std::size_t want = (sec.size - pos) < kChunkSize ? (sec.size - pos) : kChunkSize;
            std::size_t readWant = want + kTailOverlap;
            if (pos + readWant > sec.size) readWant = sec.size - pos;
            if (readWant < 6) break;
            const std::uint8_t* secBase = reinterpret_cast<const std::uint8_t*>(sec.va);
            if (!Memory::SafeReadBytes(secBase + pos, buf.data(), readWant)) { ++failedReads; pos += 4096u; continue; }
            const std::size_t emitEnd = want;
            const std::size_t scanEnd = (readWant >= 6) ? std::min(emitEnd, readWant - 6 + 1u) : 0u;
            for (std::size_t p = 0; p < scanEnd; ++p) {
                if (truncated) break;
                const std::size_t avail = readWant - p;
                const std::uintptr_t insnIP = sec.va + pos + p;
                std::size_t insnLen = 0;
                std::size_t off = 0;
                if (IsRexByte(buf[p])) { if (avail < 7) continue; ++off; }
                if (avail - off < 6) continue;
                std::uint8_t op = buf[p + off];
                if (op != 0x8B && op != 0x8D) continue;
                if (!IsRipRelativeModrm(buf[p + off + 1])) continue;
                std::int32_t disp = ReadInt32LE(buf.data() + p + off + 2);
                insnLen = off + 2 + 4;
                if (avail < insnLen) continue;
                std::uintptr_t target = insnIP + insnLen + static_cast<std::intptr_t>(disp);
                if (slotSet.find(target) == slotSet.end()) continue;
                ++hopOneCandidates;
                ZydisRegister classPtrReg = ExtractHopOneDestReg(&decoder, buf.data() + p, insnLen);
                if (classPtrReg == ZYDIS_REGISTER_NONE) continue;
                std::uintptr_t hitIP = 0;
                std::string kind;
                bool found = TryForwardTrace(&decoder, buf.data() + p + insnLen, avail - insnLen, insnIP + insnLen,
                                             classPtrReg, staticFieldsOffset, targetFieldOffset, &hitIP, &kind);
                if (!found) { ++chainBroken; continue; }
                if (!emitted.insert({target, hitIP}).second) continue;
                ChainMatch m;
                m.hitIP = hitIP; m.hopOneIP = insnIP; m.refKind = std::move(kind);
                result.push_back(std::move(m));
                if (result.size() >= matchLimit) { truncated = true; break; }
            }
            bytesScanned += want;
            pos += want;
        }
    }
    if (result.size() > 1) {
        std::sort(result.begin(), result.end(), [](const ChainMatch& a, const ChainMatch& b) { return a.hitIP < b.hitIP; });
        result.erase(std::unique(result.begin(), result.end(),
                         [](const ChainMatch& a, const ChainMatch& b) { return a.hitIP == b.hitIP; }), result.end());
    }
    if (stats != nullptr) {
        stats->hopOneCandidates = hopOneCandidates; stats->chainBroken = chainBroken;
        stats->chainsEmitted = result.size(); stats->bytesScanned = bytesScanned; stats->failedReads = failedReads;
    }
    return result;
}

// ---------------------------------------------------------------------------
struct OracleMatch { uintptr_t hit_ip; uintptr_t hop_one_ip; uint8_t is_write; };
struct OracleStats { size_t a, b, c, d, e; uint8_t valid; };

extern "C" size_t oracle_detect(uintptr_t base, read_fn rd, void* ctx, const uintptr_t* slots, size_t n,
                                size_t sfo, size_t tfo, size_t limit, OracleMatch* out, size_t cap,
                                OracleStats* st) {
    g_read = rd; g_ctx = ctx;
    std::vector<ClassPointerSlot> v;
    for (size_t i = 0; i < n; ++i) v.push_back({slots[i], 0});
    ChainScanStats s{};
    s.hopOneCandidates = (size_t)-1;  // sentinel: untouched
    auto r = DetectFieldAccessChains(reinterpret_cast<void*>(base), v, sfo, tfo, limit, &s);
    st->valid = s.hopOneCandidates != (size_t)-1;
    st->a = st->valid ? s.hopOneCandidates : 0; st->b = s.chainBroken; st->c = s.chainsEmitted;
    st->d = s.bytesScanned; st->e = s.failedReads;
    if (!st->valid) { st->b = st->c = st->d = st->e = 0; }
    for (size_t i = 0; i < r.size() && i < cap; ++i) {
        out[i].hit_ip = r[i].hitIP; out[i].hop_one_ip = r[i].hopOneIP; out[i].is_write = r[i].refKind == "write";
    }
    return r.size();
}

// --- behavioural projection -------------------------------------------------
static bool Trackable(ZydisRegister canon) {
    switch (ZydisRegisterGetClass(canon)) {
        case ZYDIS_REGCLASS_GPR8: case ZYDIS_REGCLASS_GPR16: case ZYDIS_REGCLASS_GPR32: case ZYDIS_REGCLASS_GPR64:
        case ZYDIS_REGCLASS_SEGMENT: case ZYDIS_REGCLASS_CONTROL: case ZYDIS_REGCLASS_DEBUG:
            return true;
        default: return false;
    }
}
static std::string Name(ZydisRegister r) { const char* s = ZydisRegisterGetString(r); return s ? s : "?"; }
static std::string MemStr(const ZydisDecodedOperand& o) {
    ZydisRegister b = Canonical(o.mem.base);
    std::string s = Name(b);
    if (b == ZYDIS_REGISTER_NONE || b == ZYDIS_REGISTER_RIP) return s;
    char t[32]; snprintf(t, sizeof t, "@%llx", (unsigned long long)DispOrZero(o.mem)); return s + t;
}

extern "C" size_t oracle_project(const uint8_t* buf, size_t len, char* out, size_t cap) {
    // Thread-safe one-time init (test threads call this concurrently); the
    // decoder is read-only afterwards.
    static const ZydisDecoder decoder = [] {
        ZydisDecoder d;
        ZydisDecoderInit(&d, ZYDIS_MACHINE_MODE_LONG_64, ZYDIS_STACK_WIDTH_64);
        return d;
    }();
    ZydisDecodedInstruction instr;
    ZydisDecodedOperand ops[ZYDIS_MAX_OPERAND_COUNT];
    std::string s;
    if (!ZYAN_SUCCESS(ZydisDecoderDecodeFull(&decoder, buf, len, &instr, ops))) {
        s = "X";
    } else {
        char t[64]; snprintf(t, sizeof t, "L%u", instr.length); s = t;
        auto m = instr.mnemonic;
        bool rdSet = m == ZYDIS_MNEMONIC_MOV || m == ZYDIS_MNEMONIC_LEA || m == ZYDIS_MNEMONIC_MOVSXD ||
                     m == ZYDIS_MNEMONIC_MOVZX || m == ZYDIS_MNEMONIC_MOVSX || m == ZYDIS_MNEMONIC_MOVSS ||
                     m == ZYDIS_MNEMONIC_MOVSD;
        bool wrSet = m == ZYDIS_MNEMONIC_MOV || m == ZYDIS_MNEMONIC_MOVSS || m == ZYDIS_MNEMONIC_MOVSD;
        int vis = instr.operand_count_visible;
        if (rdSet && vis >= 2 && ops[0].type == ZYDIS_OPERAND_TYPE_REGISTER && ops[1].type == ZYDIS_OPERAND_TYPE_MEMORY &&
            ops[1].mem.index == ZYDIS_REGISTER_NONE)
            s += " RD" + MemStr(ops[1]);
        if (wrSet && vis >= 2 && ops[0].type == ZYDIS_OPERAND_TYPE_MEMORY &&
            (ops[1].type == ZYDIS_OPERAND_TYPE_REGISTER || ops[1].type == ZYDIS_OPERAND_TYPE_IMMEDIATE) &&
            ops[0].mem.index == ZYDIS_REGISTER_NONE)
            s += " WR" + MemStr(ops[0]);
        auto cat = instr.meta.category;
        if (cat == ZYDIS_CATEGORY_RET || cat == ZYDIS_CATEGORY_UNCOND_BR) {
            s += " T";
        } else {
            if (m == ZYDIS_MNEMONIC_MOV && vis >= 2 && ops[0].type == ZYDIS_OPERAND_TYPE_REGISTER &&
                ops[1].type == ZYDIS_OPERAND_TYPE_MEMORY && ops[1].mem.index == ZYDIS_REGISTER_NONE)
                s += " H2" + MemStr(ops[1]) + ">" + Name(Canonical(ops[0].reg.value));
            if (cat == ZYDIS_CATEGORY_CALL) s += " C";
            else if (cat == ZYDIS_CATEGORY_COND_BR) s += " B";
            else if (m == ZYDIS_MNEMONIC_MOV && vis >= 2 && ops[0].type == ZYDIS_OPERAND_TYPE_REGISTER &&
                     ops[1].type == ZYDIS_OPERAND_TYPE_REGISTER)
                s += " MV" + Name(Canonical(ops[0].reg.value)) + "<" + Name(Canonical(ops[1].reg.value));
            else {
                std::vector<std::string> w;
                for (int i = 0; i < vis; ++i) {
                    if (ops[i].type != ZYDIS_OPERAND_TYPE_REGISTER) continue;
                    if ((ops[i].actions & ZYDIS_OPERAND_ACTION_MASK_WRITE) == 0) continue;
                    ZydisRegister c = Canonical(ops[i].reg.value);
                    if (!Trackable(c)) continue;
                    w.push_back(Name(c));
                }
                std::sort(w.begin(), w.end());
                w.erase(std::unique(w.begin(), w.end()), w.end());
                if (!w.empty()) { s += " W"; for (auto& x : w) s += ":" + x; }
            }
        }
        // Raw diagnostics after '|' (ignored by the comparison): mnemonic id + category id.
        snprintf(t, sizeof t, " |m%d c%d v%d e%d x%s", (int)m, (int)cat, vis, (int)instr.encoding, ZydisISAExtGetString(instr.meta.isa_ext)); s += t;
    }
    size_t n = std::min(s.size(), cap);
    memcpy(out, s.data(), n);
    return n;
}

extern "C" const char* oracle_mnemonic_name(int m) { return ZydisMnemonicGetString((ZydisMnemonic)m); }

// std::sort reference for the msvc_sort transcription: sorts (key, tag) by key
// with the same comparator shape as the legacy code and returns the tag order.
extern "C" void oracle_msvc_sort(const uint64_t* keys, const uint32_t* tags, size_t n, uint32_t* out_tags) {
    struct E { uint64_t k; uint32_t t; std::string pad; };
    std::vector<E> v;
    for (size_t i = 0; i < n; ++i) v.push_back({keys[i], tags[i], std::string()});
    std::sort(v.begin(), v.end(), [](const E& a, const E& b) { return a.k < b.k; });
    for (size_t i = 0; i < n; ++i) out_tags[i] = v[i].t;
}
