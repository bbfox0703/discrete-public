// Instance-field xref oracle (Zydis side): the exact per-instruction hit list
// the legacy IL2CPPFieldXref.cpp ScanInstanceAccesses built from Zydis (the
// Rust port is dll/rust/src/xref, facts from InsnDecoder::decode_mem), plus
// the raw memory-operand facts the apx_mem.rs table generator reads.
//
// Decoder: ZydisDecoderInit(LONG_64, STACK_WIDTH_64) with no mode changes,
// exactly as the legacy scan initialised it.
#include <Zydis/Zydis.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <algorithm>

// Thread-safe one-time init (test threads call in concurrently); the decoder
// is read-only afterwards.
static const ZydisDecoder* Decoder() {
    static const ZydisDecoder dec = [] {
        ZydisDecoder d;
        ZydisDecoderInit(&d, ZYDIS_MACHINE_MODE_LONG_64, ZYDIS_STACK_WIDTH_64);
        return d;
    }();
    return &dec;
}

static ZydisRegister Canonical(ZydisRegister r) {
    if (r == ZYDIS_REGISTER_NONE) return r;
    ZydisRegister canon = ZydisRegisterGetLargestEnclosing(ZYDIS_MACHINE_MODE_LONG_64, r);
    return canon != ZYDIS_REGISTER_NONE ? canon : r;
}

// "X" on failure, else "L<len>" + one " <W|R><disp hex>" per operand the
// legacy loop would emit, in operand order. After " |": diagnostics.
extern "C" size_t oxr_project(const uint8_t* buf, size_t len, char* out, size_t cap) {
    ZydisDecodedInstruction instr;
    ZydisDecodedOperand ops[ZYDIS_MAX_OPERAND_COUNT];
    std::string s;
    char t[160];
    if (!ZYAN_SUCCESS(ZydisDecoderDecodeFull(Decoder(), buf, len, &instr, ops))) {
        s = "X";
    } else {
        snprintf(t, sizeof t, "L%u", instr.length); s = t;
        for (uint8_t oi = 0; oi < instr.operand_count_visible; ++oi) {
            const ZydisDecodedOperand& op = ops[oi];
            if (op.type != ZYDIS_OPERAND_TYPE_MEMORY) continue;
            if (op.mem.disp.size == 0) continue;
            if (op.mem.index != ZYDIS_REGISTER_NONE) continue;
            const ZydisRegister base = Canonical(op.mem.base);
            if (base == ZYDIS_REGISTER_NONE || base == ZYDIS_REGISTER_RIP || base == ZYDIS_REGISTER_RSP || base == ZYDIS_REGISTER_RBP) continue;
            char c;
            if (instr.mnemonic == ZYDIS_MNEMONIC_LEA) c = 'R';
            else if (op.actions & ZYDIS_OPERAND_ACTION_MASK_WRITE) c = 'W';
            else if (op.actions & ZYDIS_OPERAND_ACTION_MASK_READ) c = 'R';
            else continue;
            snprintf(t, sizeof t, " %c%llx", c, (unsigned long long)op.mem.disp.value); s += t;
        }
        // diagnostics
        snprintf(t, sizeof t, " |%s v%d", ZydisMnemonicGetString(instr.mnemonic), instr.operand_count_visible); s += t;
        for (uint8_t oi = 0; oi < instr.operand_count; ++oi) {
            const ZydisDecodedOperand& op = ops[oi];
            if (op.type != ZYDIS_OPERAND_TYPE_MEMORY) continue;
            const char* b = ZydisRegisterGetString(op.mem.base);
            const char* x = ZydisRegisterGetString(op.mem.index);
            snprintf(t, sizeof t, " [%d%s b=%s x=%s ds=%u d=%llx a=%x mt=%d]", oi,
                     oi < instr.operand_count_visible ? "" : "h", b ? b : "-", x ? x : "-",
                     op.mem.disp.size, (unsigned long long)op.mem.disp.value, op.actions, (int)op.mem.type);
            s += t;
        }
    }
    size_t n = std::min(s.size(), cap);
    memcpy(out, s.data(), n);
    return n;
}

// Raw memory-operand facts for table generation: returns -1 on failure, else
// number of visible memory operands; fills acts[i] (action mask), disps[i], dsz[i].
extern "C" int oxr_memfacts(const uint8_t* buf, size_t len, uint8_t* acts, int64_t* disps, uint8_t* dsz,
                            int* bases, int* idxs, int* mnem) {
    ZydisDecodedInstruction instr;
    ZydisDecodedOperand ops[ZYDIS_MAX_OPERAND_COUNT];
    if (!ZYAN_SUCCESS(ZydisDecoderDecodeFull(Decoder(), buf, len, &instr, ops))) return -1;
    int n = 0;
    *mnem = instr.mnemonic;
    for (uint8_t oi = 0; oi < instr.operand_count_visible; ++oi) {
        if (ops[oi].type != ZYDIS_OPERAND_TYPE_MEMORY) continue;
        acts[n] = ops[oi].actions; disps[n] = ops[oi].mem.disp.value; dsz[n] = ops[oi].mem.disp.size;
        bases[n] = Canonical(ops[oi].mem.base); idxs[n] = ops[oi].mem.index;
        ++n;
        if (n == 8) break;
    }
    return n;
}
extern "C" const char* oxr_reg_name(int r) { return ZydisRegisterGetString((ZydisRegister)r); }
extern "C" const char* oxr_mnemonic_name(int m) { return ZydisMnemonicGetString((ZydisMnemonic)m); }
