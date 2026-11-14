// r2_dump.cpp — Phase R2 differential harness driver.
//
// Built twice from this one source (tools/r2-diff/CMakeLists.txt):
//   r2-dump.exe         against the Rust ports (the *Rs.cpp wrappers +
//                       discrete_core, DISCRETE_RUST_R2=1)
//   r2-dump-legacy.exe  against the legacy C++ implementations
// It calls ONLY the public C++ APIs of the four R2 modules, so the two
// binaries differ in nothing but the implementation under test:
//   Discrete::Aob::BuildPattern
//   Discrete::GroupMatch::HasDistinctAssignment / ResolveAssignment
//   Discrete::ValueRefine::CompareCandidateForRefine
//   Discrete::IL2CPP::DetectFieldAccessChains
// tools/verify/r2_rust_diff.py runs both and requires byte-identical
// stdout / stderr / exit code.
//
// Output rules (what makes a byte compare meaningful):
//   * Deterministic: a local splitmix64 PRNG with fixed seeds, no clock, no
//     environment, no absolute addresses (module-relative RVAs only), no
//     paths. Floating-point inputs are printed as their bit patterns.
//   * JSON lines on stdout (binary mode, '\n' only). Every subcommand ends
//     with {"rec":"end"}; a missing end record means the run died early.
//   * Exit 0 on success; 1 = usage error; 2 = image could not be mapped /
//     normalised; 3 = stdout write error.
//
// Subcommands:
//   aob <GameAssembly.dll>    BuildPattern over real x64 code (random
//                             offsets + function-like starts)
//   aob-synthetic             BuildPattern over generated opcode-ish buffers
//   gm                        GroupMatch over random slot / leaf instances
//   refine                    CompareCandidateForRefine over random cases
//   chain <GameAssembly.dll>  DetectFieldAccessChains over harness-discovered
//                             class-pointer-like slots

#include <windows.h>

#include <fcntl.h>
#include <io.h>

#include "il2cpp/IL2CPPChainDetector.h"
#include "shared/AobPattern.h"
#include "shared/GroupMatch.h"
#include "shared/Memory.h"
#include "shared/ValueRefine.h"

#include <algorithm>
#include <charconv>
#include <climits>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <limits>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

namespace {

namespace Aob    = Discrete::Aob;
namespace GM     = Discrete::GroupMatch;
namespace IL2CPP = Discrete::IL2CPP;
namespace Mem    = Discrete::Memory;
namespace VR     = Discrete::ValueRefine;

// --- deterministic PRNG -------------------------------------------------------

// splitmix64 (Steele / Lea / Flood). Local so the case stream never depends
// on the standard library's distribution implementations.
class SplitMix64 {
public:
    explicit SplitMix64(std::uint64_t seed) noexcept : s_(seed) {}

    std::uint64_t Next() noexcept {
        std::uint64_t z = (s_ += 0x9E3779B97F4A7C15ull);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
        return z ^ (z >> 31);
    }
    // Modulo bias is irrelevant here: we need coverage, not uniformity.
    std::uint64_t Below(std::uint64_t n) noexcept { return n == 0 ? 0 : Next() % n; }
    std::uint8_t  Byte() noexcept { return static_cast<std::uint8_t>(Next() >> 56); }

private:
    std::uint64_t s_;
};

// --- JSON-line output -----------------------------------------------------------

void AppendU(std::string& s, std::uint64_t v) {
    char b[24];
    const auto r = std::to_chars(b, b + sizeof(b), v);
    s.append(b, static_cast<std::size_t>(r.ptr - b));
}

void AppendI(std::string& s, std::int64_t v) {
    char b[24];
    const auto r = std::to_chars(b, b + sizeof(b), v);
    s.append(b, static_cast<std::size_t>(r.ptr - b));
}

void AppendHexNum(std::string& s, std::uint64_t v) {
    char b[20];
    const auto r = std::to_chars(b, b + sizeof(b), v, 16);
    s += "\"0x";
    s.append(b, static_cast<std::size_t>(r.ptr - b));
    s += '"';
}

void AppendHexBytes(std::string& s, const std::uint8_t* p, std::size_t n) {
    static constexpr char kDigits[] = "0123456789ABCDEF";
    s += '"';
    for (std::size_t i = 0; i < n; ++i) {
        s += kDigits[p[i] >> 4];
        s += kDigits[p[i] & 0x0F];
    }
    s += '"';
}

// Escapes byte-wise: anything outside printable ASCII becomes \u00XX. The
// refine subcommand feeds deliberately garbage scanType bytes (NUL, >= 0x80),
// and those must print identically and unambiguously.
void AppendStr(std::string& s, std::string_view v) {
    static constexpr char kDigits[] = "0123456789ABCDEF";
    s += '"';
    for (const char ch : v) {
        const auto c = static_cast<unsigned char>(ch);
        if (c < 0x20 || c >= 0x7F || c == '"' || c == '\\') {
            s += "\\u00";
            s += kDigits[c >> 4];
            s += kDigits[c & 0x0F];
        } else {
            s += ch;
        }
    }
    s += '"';
}

class Line {
public:
    explicit Line(std::string_view rec) {
        s_.reserve(256);
        s_ += "{\"rec\":";
        AppendStr(s_, rec);
    }
    Line& U(std::string_view k, std::uint64_t v)   { Key(k); AppendU(s_, v);      return *this; }
    Line& I(std::string_view k, std::int64_t v)    { Key(k); AppendI(s_, v);      return *this; }
    Line& B(std::string_view k, bool v)            { Key(k); s_ += v ? '1' : '0'; return *this; }
    Line& Hex(std::string_view k, std::uint64_t v) { Key(k); AppendHexNum(s_, v); return *this; }
    Line& S(std::string_view k, std::string_view v) { Key(k); AppendStr(s_, v);   return *this; }
    Line& Bytes(std::string_view k, const std::uint8_t* p, std::size_t n) {
        Key(k);
        AppendHexBytes(s_, p, n);
        return *this;
    }
    Line& Raw(std::string_view k, std::string_view json) { Key(k); s_ += json; return *this; }
    void Emit() {
        s_ += "}\n";
        std::fwrite(s_.data(), 1, s_.size(), stdout);
    }

private:
    void Key(std::string_view k) {
        s_ += ",\"";
        s_ += k;
        s_ += "\":";
    }
    std::string s_;
};

int Finish() {
    Line("end").Emit();
    if (std::fflush(stdout) != 0 || std::ferror(stdout) != 0) {
        std::fputs("r2-dump: stdout write error\n", stderr);
        return 3;
    }
    return 0;
}

// --- GameAssembly.dll mapping -----------------------------------------------------

struct Section {
    std::uint32_t rva  = 0;
    std::uint32_t size = 0;  // Misc.VirtualSize — what both chain implementations use
};

struct MappedImage {
    std::uint8_t*        base        = nullptr;
    std::uint32_t        sizeOfImage = 0;
    std::vector<Section> exec;  // IMAGE_SCN_MEM_EXECUTE, header order
    std::vector<Section> data;  // everything else, header order
};

bool ReadFileAt(HANDLE file, std::uint64_t offset, void* dst, std::uint32_t len) {
    LARGE_INTEGER li;
    li.QuadPart = static_cast<LONGLONG>(offset);
    if (!::SetFilePointerEx(file, li, nullptr, FILE_BEGIN)) return false;
    auto* out = static_cast<std::uint8_t*>(dst);
    std::uint32_t done = 0;
    while (done < len) {
        DWORD got = 0;
        if (!::ReadFile(file, out + done, len - done, &got, nullptr)) return false;
        if (got == 0) return false;  // short file: the image is not what its headers claim
        done += got;
    }
    return true;
}

// Maps GameAssembly.dll without running any of its code, then NORMALISES every
// executable section to the exact bytes stored in the file.
//
// Judgement call — why normalise: r2-dump and r2-dump-legacy are two separate
// processes, and nothing guarantees that both see the image at the same base.
// Base relocations (and any load-time dynamic relocations) patch code bytes as
// a function of the load address, so the two runs could feed DIFFERENT bytes to
// the code under test and report a "difference" that is not one. File bytes are
// base-independent (they are the image as linked for its preferred ImageBase),
// so copying them over the mapped executable sections makes both processes scan
// identical code no matter where the loader put the module. RIP-relative
// targets are unaffected by this (they never carry relocations), and every
// address the harness prints is module-relative.
bool MapGameAssembly(const wchar_t* path, MappedImage& img, std::string& err) {
    // DONT_RESOLVE_DLL_REFERENCES: map + relocate only — no import resolution,
    // no DllMain / TLS callbacks. The module is still registered with the
    // loader, which is what lets GetModuleHandleW(L"GameAssembly.dll") (the
    // lookup BOTH chain implementations do via ModuleHelper) find it.
    HMODULE mod = ::LoadLibraryExW(path, nullptr, DONT_RESOLVE_DLL_REFERENCES);
    if (mod == nullptr) {
        err = "LoadLibraryExW failed (error " + std::to_string(::GetLastError()) + ")";
        return false;
    }
    if (::GetModuleHandleW(L"GameAssembly.dll") != mod) {
        err = "the mapped image is not reachable as GameAssembly.dll (the file must be named GameAssembly.dll)";
        return false;
    }
    img.base = reinterpret_cast<std::uint8_t*>(mod);

    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(img.base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0) {
        err = "bad DOS header";
        return false;
    }
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(img.base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE
        || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64
        || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) {
        err = "not an x64 PE image";
        return false;
    }
    img.sizeOfImage = nt->OptionalHeader.SizeOfImage;

    const auto* sh = reinterpret_cast<const IMAGE_SECTION_HEADER*>(
        reinterpret_cast<const std::uint8_t*>(&nt->OptionalHeader)
        + nt->FileHeader.SizeOfOptionalHeader);
    const std::uint16_t nSect = nt->FileHeader.NumberOfSections;

    HANDLE file = ::CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
                                nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        err = "cannot open the image file for reading (error " + std::to_string(::GetLastError()) + ")";
        return false;
    }

    bool ok = true;
    for (std::uint16_t i = 0; i < nSect && ok; ++i) {
        const IMAGE_SECTION_HEADER& s = sh[i];
        Section sec;
        sec.rva  = s.VirtualAddress;
        sec.size = s.Misc.VirtualSize;
        if ((s.Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0) {
            img.data.push_back(sec);
            continue;
        }
        img.exec.push_back(sec);
        if (sec.size == 0) continue;
        if (static_cast<std::uint64_t>(sec.rva) + sec.size > img.sizeOfImage) {
            err = "executable section extends past SizeOfImage";
            ok = false;
            break;
        }

        // File bytes for [0, min(SizeOfRawData, VirtualSize)), zeros after —
        // exactly how the loader initialises a section.
        std::vector<std::uint8_t> bytes(sec.size, 0);
        const std::uint32_t fromFile =
            s.PointerToRawData == 0 ? 0u : (std::min)(static_cast<std::uint32_t>(s.SizeOfRawData), sec.size);
        if (fromFile != 0 && !ReadFileAt(file, s.PointerToRawData, bytes.data(), fromFile)) {
            err = "cannot read an executable section from the image file";
            ok = false;
            break;
        }

        void* dst = img.base + sec.rva;
        DWORD oldProt = 0;
        if (!::VirtualProtect(dst, sec.size, PAGE_EXECUTE_READWRITE, &oldProt)) {
            err = "VirtualProtect failed (error " + std::to_string(::GetLastError()) + ")";
            ok = false;
            break;
        }
        std::memcpy(dst, bytes.data(), sec.size);
        DWORD ignored = 0;
        // Restore is best effort: the harness never executes this code, and a
        // writable mapping changes nothing the implementations can observe.
        ::VirtualProtect(dst, sec.size, oldProt, &ignored);
    }
    ::CloseHandle(file);
    return ok;
}

int MapOrFail(const wchar_t* path, MappedImage& img) {
    std::string err;
    if (!MapGameAssembly(path, img, err)) {
        // No path in the message: the two flavours must print identical bytes.
        std::fprintf(stderr, "r2-dump: cannot map image: %s\n", err.c_str());
        return 2;
    }
    return 0;
}

std::string SectionsJson(const std::vector<Section>& secs) {
    std::string s = "[";
    for (std::size_t i = 0; i < secs.size(); ++i) {
        if (i) s += ',';
        s += "{\"rva\":";
        AppendHexNum(s, secs[i].rva);
        s += ",\"size\":";
        AppendU(s, secs[i].size);
        s += '}';
    }
    s += ']';
    return s;
}

// --- aob ---------------------------------------------------------------------------

// Every PatternResult field. `bytes` is not printed raw: by contract it is the
// first bytes.size() input bytes, so "nb" (its size) + "bok" (it equals that
// input prefix) characterise it completely while keeping the output small.
void AppendPatternResult(std::string& s, const Aob::PatternResult& r, bool stop,
                         const std::uint8_t* in, std::size_t inLen) {
    s += "{\"t\":";
    s += stop ? '1' : '0';
    s += ",\"in\":";
    AppendU(s, inLen);
    s += ",\"p\":";
    AppendStr(s, r.pattern);
    s += ",\"m\":\"";
    for (std::size_t i = 0; i < r.wildcardMask.size(); ++i) s += r.wildcardMask[i] ? '1' : '0';
    s += "\",\"len\":";
    AppendU(s, r.length);
    s += ",\"ic\":";
    AppendU(s, r.instructionCount);
    s += ",\"rb\":";
    AppendU(s, r.relBranchCount);
    s += ",\"rr\":";
    AppendU(s, r.ripRelCount);
    s += ",\"bail\":";
    s += r.decoderBailed ? '1' : '0';
    s += ",\"ba\":";
    AppendU(s, r.bailedAt);
    s += ",\"st\":";
    s += r.stoppedAtTerminal ? '1' : '0';
    s += ",\"rq\":";
    AppendU(s, r.requestedLength);
    s += ",\"nb\":";
    AppendU(s, r.bytes.size());
    const bool bok = r.bytes.size() <= inLen
        && (r.bytes.empty() || std::memcmp(r.bytes.data(), in, r.bytes.size()) == 0);
    s += ",\"bok\":";
    s += bok ? '1' : '0';
    s += '}';
}

constexpr std::size_t kAobLengths[] = {8, 16, 24, 32, 48, 64, 96, 128};

struct CodePos {
    std::uint32_t sec;  // index into MappedImage::exec
    std::uint32_t off;  // offset inside that section
};

void EmitAobSample(const MappedImage& img, std::string_view kind, std::size_t idx,
                   const CodePos& pos) {
    const Section& sec = img.exec[pos.sec];
    const std::uint8_t* p = img.base + sec.rva + pos.off;
    const std::size_t remain = sec.size - pos.off;

    std::string r = "[";
    bool first = true;
    for (const std::size_t want : kAobLengths) {
        // Never read past the section: clip to what is left (the clipped
        // length is printed as "in").
        const std::size_t len = (std::min)(want, remain);
        for (const bool stop : {false, true}) {
            const Aob::PatternResult res = Aob::BuildPattern(p, len, stop);
            if (!first) r += ',';
            first = false;
            AppendPatternResult(r, res, stop, p, len);
        }
    }
    r += ']';
    Line("aob").S("kind", kind).U("i", idx).U("sec", pos.sec)
        .Hex("rva", static_cast<std::uint64_t>(sec.rva) + pos.off).Raw("r", r).Emit();
}

int CmdAob(const wchar_t* path) {
    MappedImage img;
    if (const int rc = MapOrFail(path, img); rc != 0) return rc;

    constexpr std::size_t kRandomStarts   = 5000;
    constexpr std::size_t kFunctionStarts = 5000;

    // Function-like starts: the first byte after a run of >= 2 padding bytes
    // (0xCC int3 / 0x90 nop, mixed). MSVC pads between functions with 0xCC,
    // so this lands on real prologues far more often than a random offset.
    std::vector<CodePos> fnStarts;
    std::uint64_t totalExec = 0;
    for (std::uint32_t si = 0; si < img.exec.size(); ++si) {
        const Section& sec = img.exec[si];
        totalExec += sec.size;
        const std::uint8_t* p = img.base + sec.rva;
        std::uint32_t run = 0;
        for (std::uint32_t i = 0; i < sec.size; ++i) {
            const std::uint8_t b = p[i];
            if (b == 0xCC || b == 0x90) {
                if (run < UINT32_MAX) ++run;
                continue;
            }
            if (run >= 2) fnStarts.push_back({si, i});
            run = 0;
        }
    }

    Line("img").Hex("sizeOfImage", img.sizeOfImage).Raw("exec", SectionsJson(img.exec))
        .U("execBytes", totalExec).U("fnStarts", fnStarts.size()).Emit();

    if (totalExec == 0) return Finish();

    SplitMix64 rng(0xA0B0C0D0E0F01234ull);
    for (std::size_t i = 0; i < kRandomStarts; ++i) {
        std::uint64_t at = rng.Below(totalExec);
        std::uint32_t si = 0;
        while (at >= img.exec[si].size) {
            at -= img.exec[si].size;
            ++si;
        }
        EmitAobSample(img, "rnd", i, CodePos{si, static_cast<std::uint32_t>(at)});
    }

    // Evenly spaced over all function-like starts (sorted by section, then
    // offset, by construction) — deterministic and spread over the image.
    const std::size_t nFn = (std::min)(kFunctionStarts, fnStarts.size());
    for (std::size_t i = 0; i < nFn; ++i) {
        const std::size_t k = static_cast<std::size_t>(
            static_cast<std::uint64_t>(i) * fnStarts.size() / nFn);
        EmitAobSample(img, "fn", i, fnStarts[k]);
    }
    return Finish();
}

// --- aob-synthetic -------------------------------------------------------------------

void PushRandom(SplitMix64& rng, std::vector<std::uint8_t>& v, std::size_t n) {
    for (std::size_t i = 0; i < n; ++i) v.push_back(rng.Byte());
}

// A ModRM byte (+ SIB + displacement) biased toward the forms an x64 length
// decoder must get right: RIP-relative, SIB with base=101, disp8 / disp32.
void PushModrm(SplitMix64& rng, std::vector<std::uint8_t>& v) {
    const auto reg = static_cast<std::uint8_t>(rng.Below(8) << 3);
    switch (rng.Below(7)) {
        case 0:  // mod=00 r/m=101: RIP-relative disp32
        case 1:
            v.push_back(static_cast<std::uint8_t>(reg | 0x05));
            PushRandom(rng, v, 4);
            break;
        case 2:  // register direct
            v.push_back(static_cast<std::uint8_t>(0xC0 | reg | rng.Below(8)));
            break;
        case 3: {  // mod=01 disp8 (SIB when r/m=100)
            const auto rm = static_cast<std::uint8_t>(rng.Below(8));
            v.push_back(static_cast<std::uint8_t>(0x40 | reg | rm));
            if (rm == 4) v.push_back(rng.Byte());
            PushRandom(rng, v, 1);
            break;
        }
        case 4: {  // mod=10 disp32 (SIB when r/m=100)
            const auto rm = static_cast<std::uint8_t>(rng.Below(8));
            v.push_back(static_cast<std::uint8_t>(0x80 | reg | rm));
            if (rm == 4) v.push_back(rng.Byte());
            PushRandom(rng, v, 4);
            break;
        }
        case 5: {  // mod=00 r/m=100: SIB, base=101 adds disp32
            v.push_back(static_cast<std::uint8_t>(reg | 0x04));
            const std::uint8_t sib = rng.Byte();
            v.push_back(sib);
            if ((sib & 7) == 5) PushRandom(rng, v, 4);
            break;
        }
        default:
            v.push_back(rng.Byte());
            break;
    }
}

template <std::size_t N>
std::uint8_t Pick(SplitMix64& rng, const std::uint8_t (&arr)[N]) {
    return arr[rng.Below(N)];
}

// One instruction-shaped piece. Not necessarily valid x64 — the point is to
// hit every branch of a length decoder (prefixes, REX, 0F maps, rel8/rel32,
// RIP ModRM, immediates) far more often than uniform random bytes would.
void PushPiece(SplitMix64& rng, std::vector<std::uint8_t>& v) {
    static constexpr std::uint8_t kPrefixes[] = {0x66, 0xF2, 0xF3, 0xF0, 0x2E, 0x3E, 0x26, 0x64, 0x65, 0x67};
    static constexpr std::uint8_t k0F[] = {
        0x05, 0x0B, 0x10, 0x11, 0x1F, 0x28, 0x29, 0x2E, 0x2F, 0x40, 0x44, 0x45, 0x4C, 0x4F,
        0x57, 0x58, 0x59, 0x5A, 0x5C, 0x6E, 0x7E, 0x7F, 0x80, 0x84, 0x85, 0x8F, 0x94, 0x95,
        0x9F, 0xA2, 0xA3, 0xAB, 0xAF, 0xB6, 0xB7, 0xBE, 0xBF, 0xC8, 0xCF, 0x38, 0x3A, 0xFF,
        0x0F, 0x01, 0x00, 0xC2, 0xC6, 0xBA};
    static constexpr std::uint8_t kModrmOps[] = {
        0x00, 0x01, 0x02, 0x03, 0x08, 0x09, 0x0B, 0x10, 0x11, 0x13, 0x18, 0x19, 0x20, 0x21,
        0x23, 0x28, 0x29, 0x2B, 0x30, 0x31, 0x33, 0x38, 0x39, 0x3B, 0x63, 0x69, 0x6B, 0x84,
        0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8D, 0x8F, 0xC0, 0xC1, 0xC6, 0xC7, 0xD0,
        0xD1, 0xD3, 0xF6, 0xF7, 0xFE, 0x80, 0x81, 0x83};
    static constexpr std::uint8_t kTerminalish[] = {0xC3, 0xC2, 0xCC, 0x90, 0xC9, 0xCB, 0xCA};
    static constexpr std::uint8_t kAccImm[] = {
        0xA8, 0xA9, 0x3C, 0x3D, 0x04, 0x05, 0x24, 0x25, 0x2C, 0x2D, 0x34, 0x35, 0x0C, 0x0D, 0x68, 0x6A};

    switch (rng.Below(16)) {
        case 0:
            v.push_back(rng.Byte());
            break;
        case 1:
            v.push_back(Pick(rng, kPrefixes));
            break;
        case 2:  // REX; the next piece is (usually) its opcode
            v.push_back(static_cast<std::uint8_t>(0x40 | rng.Below(16)));
            break;
        case 3: {  // two/three-byte opcode map
            v.push_back(0x0F);
            std::uint8_t op = rng.Below(4) == 0 ? rng.Byte() : Pick(rng, k0F);
            if (op == 0x40 || op == 0x44 || op == 0x45 || op == 0x4C || op == 0x4F) {
                op = static_cast<std::uint8_t>(0x40 | rng.Below(16));  // cmovcc
            } else if (op == 0x80 || op == 0x84 || op == 0x85 || op == 0x8F) {
                op = static_cast<std::uint8_t>(0x80 | rng.Below(16));  // jcc rel32
            } else if (op == 0x94 || op == 0x95 || op == 0x9F) {
                op = static_cast<std::uint8_t>(0x90 | rng.Below(16));  // setcc
            } else if (op == 0xC8 || op == 0xCF) {
                op = static_cast<std::uint8_t>(0xC8 | rng.Below(8));   // bswap
            }
            v.push_back(op);
            if (op == 0x38 || op == 0x3A) {
                v.push_back(rng.Byte());
                PushModrm(rng, v);
                if (op == 0x3A) PushRandom(rng, v, 1);
            } else if (op >= 0x80 && op <= 0x8F) {
                PushRandom(rng, v, 4);
            } else if (op != 0x05 && op != 0x0B && op != 0xA2 && !(op >= 0xC8 && op <= 0xCF)) {
                PushModrm(rng, v);
                if (op == 0xC2 || op == 0xC6 || op == 0xBA) PushRandom(rng, v, 1);
            }
            break;
        }
        case 4:  // call / jmp rel32
            v.push_back(rng.Below(2) ? std::uint8_t{0xE8} : std::uint8_t{0xE9});
            PushRandom(rng, v, 4);
            break;
        case 5:  // jmp rel8 / jcc rel8
            v.push_back(rng.Below(3) == 0 ? std::uint8_t{0xEB}
                                          : static_cast<std::uint8_t>(0x70 | rng.Below(16)));
            PushRandom(rng, v, 1);
            break;
        case 6:  // FF group (inc/dec/call/jmp/push, incl. jmp [rip+x])
            v.push_back(0xFF);
            PushModrm(rng, v);
            break;
        case 7: {
            const std::uint8_t op = Pick(rng, kTerminalish);
            v.push_back(op);
            if (op == 0xC2 || op == 0xCA) PushRandom(rng, v, 2);
            break;
        }
        case 8:
        case 9:
        case 10:
        case 11: {  // ModRM ALU / MOV forms (+ a plausible immediate)
            const std::uint8_t op = Pick(rng, kModrmOps);
            v.push_back(op);
            PushModrm(rng, v);
            if (op == 0x80 || op == 0x83 || op == 0xC0 || op == 0xC1 || op == 0xC6 || op == 0x6B) {
                PushRandom(rng, v, 1);
            } else if (op == 0x81 || op == 0xC7 || op == 0x69) {
                PushRandom(rng, v, 4);
            } else if (op == 0xF6 || op == 0xF7) {
                PushRandom(rng, v, static_cast<std::size_t>(rng.Below(5)));
            }
            break;
        }
        case 12:  // mov r, imm (imm32, or imm64 when the preceding byte was REX.W)
            if (rng.Below(2)) {
                v.push_back(static_cast<std::uint8_t>(0xB8 | rng.Below(8)));
                PushRandom(rng, v, rng.Below(2) ? 4 : 8);
            } else {
                v.push_back(static_cast<std::uint8_t>(0xB0 | rng.Below(8)));
                PushRandom(rng, v, 1);
            }
            break;
        case 13:  // push / pop r
            v.push_back(static_cast<std::uint8_t>((rng.Below(2) ? 0x50 : 0x58) | rng.Below(8)));
            break;
        case 14: {  // accumulator / push immediates
            const std::uint8_t op = Pick(rng, kAccImm);
            v.push_back(op);
            const bool imm8 = op == 0xA8 || op == 0x3C || op == 0x04 || op == 0x24
                || op == 0x2C || op == 0x34 || op == 0x0C || op == 0x6A;
            PushRandom(rng, v, imm8 ? 1 : 4);
            break;
        }
        default:  // VEX (C5 2-byte / C4 3-byte) — typically outside a minimal decoder
            if (rng.Below(2)) {
                v.push_back(0xC5);
                PushRandom(rng, v, 2);
            } else {
                v.push_back(0xC4);
                PushRandom(rng, v, 3);
            }
            PushModrm(rng, v);
            break;
    }
}

int CmdAobSynthetic() {
    constexpr std::size_t kBuffers = 50000;
    SplitMix64 rng(0x5EED0A0B5EED0A0Bull);
    std::vector<std::uint8_t> buf;
    buf.reserve(512);

    // Fixed edge cases first: a NULL pointer with len 0 (allowed by the
    // contract) and a non-NULL pointer with len 0.
    {
        static constexpr std::uint8_t kOne[1] = {0xC3};
        const std::uint8_t* ptrs[] = {nullptr, kOne};
        for (std::size_t i = 0; i < 2; ++i) {
            std::string r = "[";
            for (const bool stop : {false, true}) {
                const Aob::PatternResult res = Aob::BuildPattern(ptrs[i], 0, stop);
                if (stop) r += ',';
                AppendPatternResult(r, res, stop, ptrs[i], 0);
            }
            r += ']';
            Line("aobs").S("case", i == 0 ? "null-empty" : "empty").Raw("r", r).Emit();
        }
    }

    for (std::size_t i = 0; i < kBuffers; ++i) {
        const auto len = static_cast<std::size_t>(rng.Below(201));  // 0..200
        buf.clear();
        const std::uint64_t mode = rng.Below(100);
        if (mode < 8) {
            PushRandom(rng, buf, len);  // uniform noise
        } else if (mode < 10) {
            static constexpr std::uint8_t kFill[] = {0xCC, 0x90, 0x00, 0xC3};
            buf.assign(len, Pick(rng, kFill));
        } else {
            while (buf.size() < len + 16) PushPiece(rng, buf);
        }
        // Cutting at an arbitrary point leaves the last instruction truncated
        // most of the time — exactly the end-of-buffer edge a decoder must
        // handle without reading past `len`.
        buf.resize(len);

        std::string r = "[";
        for (const bool stop : {false, true}) {
            const Aob::PatternResult res = Aob::BuildPattern(buf.data(), len, stop);
            if (stop) r += ',';
            AppendPatternResult(r, res, stop, buf.data(), len);
        }
        r += ']';
        Line("aobs").U("i", i).Bytes("x", buf.data(), len).Raw("r", r).Emit();
    }
    return Finish();
}

// --- gm -------------------------------------------------------------------------------

int GenLeaf(SplitMix64& rng, int nLeaves) {
    const std::uint64_t r = rng.Below(20);
    if (r < 14) return static_cast<int>(rng.Below(static_cast<std::uint64_t>((std::max)(nLeaves, 1))));
    if (r < 16) return -1 - static_cast<int>(rng.Below(3));        // negative
    if (r < 18) return nLeaves + static_cast<int>(rng.Below(3));   // >= nLeaves
    if (r == 18) return INT_MIN;
    return INT_MAX;
}

std::string IntListJson(const std::vector<int>& v) {
    std::string s = "[";
    for (std::size_t i = 0; i < v.size(); ++i) {
        if (i) s += ',';
        AppendI(s, v[i]);
    }
    s += ']';
    return s;
}

int CmdGm() {
    constexpr std::size_t kCases = 200000;
    SplitMix64 rng(0x6A7C3D19E05F4B21ull);
    std::vector<GM::SlotMatches> m;
    std::vector<int> out;

    for (std::size_t i = 0; i < kCases; ++i) {
        const auto nSlots = static_cast<std::size_t>(rng.Below(7));  // 0..6
        const int nLeaves = static_cast<int>(rng.Below(13)) - 2;     // -2..10
        m.assign(nSlots, GM::SlotMatches{});
        std::string slots = "[";
        for (std::size_t s = 0; s < nSlots; ++s) {
            const auto cnt = static_cast<std::size_t>(rng.Below(7));  // 0..6, empty lists included
            for (std::size_t j = 0; j < cnt; ++j) m[s].leafIdx.push_back(GenLeaf(rng, nLeaves));
            if (s) slots += ',';
            slots += IntListJson(m[s].leafIdx);
        }
        slots += ']';

        const bool has = GM::HasDistinctAssignment(m, nLeaves);
        // Pre-fill `out` with a stale size and sentinel values: the contract
        // says ResolveAssignment re-sizes it to m.size() on every return.
        out.assign(static_cast<std::size_t>(rng.Below(8)), 0x5A5A);
        const bool res = GM::ResolveAssignment(m, nLeaves, out);

        Line("gm").U("i", i).I("n", nLeaves).Raw("s", slots).B("has", has).B("res", res)
            .Raw("out", IntListJson(out)).Emit();
    }
    return Finish();
}

// --- refine ---------------------------------------------------------------------------

constexpr std::uint32_t kFloatSpecials[] = {
    0x00000000u, 0x80000000u,              // +0, -0
    0x7F800000u, 0xFF800000u,              // +inf, -inf
    0x7FC00000u, 0xFFC00000u, 0x7FC00001u, // quiet NaNs
    0x7FA00000u, 0x7F800001u, 0xFF800001u, // signalling NaNs
    0x00000001u, 0x807FFFFFu, 0x007FFFFFu, // denormals
    0x7F7FFFFFu, 0xFF7FFFFFu,              // +-max
    0x00800000u, 0x80800000u,              // +-min normal
    0x3F800000u, 0xBF800000u, 0x3F000000u, // 1, -1, 0.5
    0x4B800000u,                           // 2^24
};

constexpr std::uint64_t kDoubleSpecials[] = {
    0x0000000000000000ull, 0x8000000000000000ull,
    0x7FF0000000000000ull, 0xFFF0000000000000ull,
    0x7FF8000000000000ull, 0xFFF8000000000000ull, 0x7FF8000000000001ull,
    0x7FF4000000000000ull, 0x7FF0000000000001ull, 0xFFF0000000000001ull,
    0x0000000000000001ull, 0x800FFFFFFFFFFFFFull, 0x000FFFFFFFFFFFFFull,
    0x7FEFFFFFFFFFFFFFull, 0xFFEFFFFFFFFFFFFFull,
    0x0010000000000000ull, 0x8010000000000000ull,
    0x3FF0000000000000ull, 0xBFF0000000000000ull, 0x3FE0000000000000ull,
    0x4340000000000000ull,  // 2^53
};

constexpr double kSmallValues[] = {0.0, 0.5, 1.0, 1.5, 2.0, -1.0, -0.5, 100.25, 1e-5, 3.0e9, -7.75};
// Perturbations applied to a reference payload: straddle the tolerance list
// (0, 1e-4, 0.5, 1e30) so the `<= tolerance` boundary is hit exactly.
constexpr double kDeltas[] = {0.0, 0.5, -0.5, 1e-4, -1e-4, 5e-5, 0.25, 0.6, 1e30, -1e30, 2e-4};

void StoreF(std::uint8_t* p, float f) { std::memcpy(p, &f, 4); }
void StoreD(std::uint8_t* p, double d) { std::memcpy(p, &d, 8); }

// One 8-byte payload. `ref` (may be null) lets cases correlate with an
// earlier payload — without that, equal / adjacent values (changed vs
// unchanged, increased by one, |cur-arg| == tolerance) would almost never
// occur in random data.
void GenPayload(SplitMix64& rng, std::uint8_t out[8], const std::uint8_t* ref) {
    std::uint64_t mode = rng.Below(12);
    if (ref == nullptr && mode >= 7) mode = rng.Below(7);
    switch (mode) {
        case 0:
        case 1:
            for (int i = 0; i < 8; ++i) out[i] = rng.Byte();
            break;
        case 2: {  // small signed integer, sign-extended to 8 bytes
            const std::int64_t v = static_cast<std::int64_t>(rng.Below(7)) - 3;
            std::memcpy(out, &v, 8);
            break;
        }
        case 3: {  // special float in the low 4 bytes
            for (int i = 4; i < 8; ++i) out[i] = rng.Below(2) ? rng.Byte() : std::uint8_t{0};
            const std::uint32_t bits = kFloatSpecials[rng.Below(std::size(kFloatSpecials))];
            std::memcpy(out, &bits, 4);
            break;
        }
        case 4: {
            const std::uint64_t bits = kDoubleSpecials[rng.Below(std::size(kDoubleSpecials))];
            std::memcpy(out, &bits, 8);
            break;
        }
        case 5:
            std::memset(out, 0, 8);
            StoreF(out, static_cast<float>(kSmallValues[rng.Below(std::size(kSmallValues))]));
            break;
        case 6:
            StoreD(out, kSmallValues[rng.Below(std::size(kSmallValues))]);
            break;
        case 7:
        case 8: {  // copy of ref, optionally +-1 as a 64-bit integer
            std::uint64_t v;
            std::memcpy(&v, ref, 8);
            const std::uint64_t k = rng.Below(3);
            if (k == 1) ++v;
            else if (k == 2) --v;
            std::memcpy(out, &v, 8);
            break;
        }
        case 9:
        case 10: {  // ref as float, perturbed
            float f;
            std::memcpy(&f, ref, 4);
            std::memcpy(out, ref, 8);
            StoreF(out, static_cast<float>(static_cast<double>(f) + kDeltas[rng.Below(std::size(kDeltas))]));
            break;
        }
        default: {  // ref as double, perturbed
            double d;
            std::memcpy(&d, ref, 8);
            StoreD(out, d + kDeltas[rng.Below(std::size(kDeltas))]);
            break;
        }
    }
}

int CmdRefine() {
    constexpr std::size_t kCases = 500000;
    // Embedded NUL and near-misses on purpose: the comparison must be exact
    // over (ptr, len), not C-string or case-insensitive.
    static constexpr std::string_view kScanTypes[] = {
        "exact", "newValue", "range", "changed", "unchanged", "increased", "decreased",
        "", "EXACT", "exact ", "newvalue", std::string_view("exact\0", 6), "rang", "Changed",
    };
    constexpr std::size_t kNamed = std::size(kScanTypes);
    const double kTols[] = {
        0.0, 1e-4, 0.5, 1e30, -1.0,
        std::numeric_limits<double>::quiet_NaN(),
        std::numeric_limits<double>::infinity(),
    };

    SplitMix64 rng(0x7E1F0E5ACA1AB1E5ull);
    std::string garbage;
    for (std::size_t i = 0; i < kCases; ++i) {
        const int dt = static_cast<int>(rng.Below(12)) - 1;  // -1..10 (valid: 0..9)

        std::string_view st;
        // Weighted: the 7 real scan types dominate; the near-misses and
        // garbage still appear tens of thousands of times.
        const std::uint64_t pick = rng.Below(kNamed + 8);
        if (pick < kNamed) {
            st = kScanTypes[pick];
        } else if (pick < kNamed + 6) {
            st = kScanTypes[rng.Below(7)];
        } else {
            garbage.clear();
            const auto glen = static_cast<std::size_t>(1 + rng.Below(12));
            for (std::size_t g = 0; g < glen; ++g) {
                garbage += rng.Below(2) ? static_cast<char>(rng.Byte())
                                        : static_cast<char>('a' + rng.Below(26));
            }
            st = garbage;
        }

        std::uint8_t cur[8], prev[8], lo[8], hi[8], prevBefore[8];
        GenPayload(rng, cur, nullptr);
        GenPayload(rng, prev, cur);
        GenPayload(rng, lo, cur);
        GenPayload(rng, hi, rng.Below(2) ? lo : cur);
        std::memcpy(prevBefore, prev, 8);
        const std::size_t tol = static_cast<std::size_t>(rng.Below(std::size(kTols)));

        const bool keep = VR::CompareCandidateForRefine(
            static_cast<Mem::ValueDataType>(dt), st, cur, prev, lo, hi, kTols[tol]);

        Line("rf").U("i", i).I("dt", dt).S("st", st).Bytes("c", cur, 8).Bytes("p", prevBefore, 8)
            .Bytes("lo", lo, 8).Bytes("hi", hi, 8).U("tol", tol).B("k", keep).Bytes("pa", prev, 8)
            .Emit();
    }
    return Finish();
}

// --- chain ----------------------------------------------------------------------------

// Value no implementation would ever write into a ChainScanStats field on its
// own: a field still holding it after a call means "stats not written" (the
// legacy early-return paths leave the caller's struct untouched).
constexpr std::size_t kStatsSentinel = 0xA5A5A5A5u;

struct SlotPick {
    std::uint32_t rva;
    std::uint32_t refs;
    std::uint32_t dataSec;
};

// Harness-owned discovery of class-pointer-LIKE slots — deliberately not the
// code under test (nor IL2CPPClassPointerScanner, which needs a live runtime).
// Every `[REX] 8B/8D modrm(rip)` in the executable sections is a candidate
// RIP-relative load; its target counts as a slot when it is 8-aligned and
// lies inside a non-executable section. The 64 most-referenced targets (the
// shape of real class-pointer caches) plus 64 evenly spaced others are kept.
std::vector<SlotPick> DiscoverSlots(const MappedImage& img, std::uint64_t& ripRefs,
                                    std::size_t& eligible) {
    std::unordered_map<std::uint32_t, std::uint32_t> counts;
    counts.reserve(1u << 16);
    ripRefs = 0;
    for (const Section& sec : img.exec) {
        if (sec.size < 6) continue;
        const std::uint8_t* p = img.base + sec.rva;
        for (std::uint32_t i = 0; i + 6 <= sec.size; ++i) {
            if (p[i] != 0x8B && p[i] != 0x8D) continue;
            if ((p[i + 1] & 0xC7) != 0x05) continue;  // mod=00 r/m=101
            std::int32_t disp;
            std::memcpy(&disp, p + i + 2, 4);
            // A REX prefix (if any) sits BEFORE the opcode, so the
            // instruction always ends at opcode + 6 — one count per load.
            const std::int64_t target = static_cast<std::int64_t>(sec.rva) + i + 6 + disp;
            if (target < 0 || target + 8 > static_cast<std::int64_t>(img.sizeOfImage)) continue;
            ++ripRefs;
            if ((target & 7) != 0) continue;
            ++counts[static_cast<std::uint32_t>(target)];
        }
    }

    std::vector<SlotPick> cands;
    cands.reserve(counts.size());
    for (const auto& [rva, refs] : counts) {
        for (std::uint32_t d = 0; d < img.data.size(); ++d) {
            const Section& ds = img.data[d];
            if (rva >= ds.rva && static_cast<std::uint64_t>(rva) + 8 <= static_cast<std::uint64_t>(ds.rva) + ds.size) {
                cands.push_back({rva, refs, d});
                break;
            }
        }
    }
    eligible = cands.size();

    // Total order (refs desc, rva asc): independent of hash-map iteration order.
    std::sort(cands.begin(), cands.end(), [](const SlotPick& a, const SlotPick& b) {
        return a.refs != b.refs ? a.refs > b.refs : a.rva < b.rva;
    });
    constexpr std::size_t kTop = 64;
    constexpr std::size_t kSpread = 64;
    std::vector<SlotPick> picked(cands.begin(), cands.begin() + static_cast<std::ptrdiff_t>((std::min)(kTop, cands.size())));
    std::vector<SlotPick> rest(cands.begin() + static_cast<std::ptrdiff_t>(picked.size()), cands.end());
    std::sort(rest.begin(), rest.end(), [](const SlotPick& a, const SlotPick& b) { return a.rva < b.rva; });
    const std::size_t nSpread = (std::min)(kSpread, rest.size());
    for (std::size_t i = 0; i < nSpread; ++i) {
        picked.push_back(rest[static_cast<std::size_t>(static_cast<std::uint64_t>(i) * rest.size() / nSpread)]);
    }
    std::stable_sort(picked.begin(), picked.end(), [](const SlotPick& a, const SlotPick& b) { return a.rva < b.rva; });
    return picked;
}

void RunChain(const MappedImage& img, std::string_view label,
              const std::vector<IL2CPP::ClassPointerSlot>& slots,
              std::size_t sfo, std::size_t fo, std::size_t limit, bool withStats) {
    IL2CPP::ChainScanStats st;
    st.hopOneCandidates = kStatsSentinel;
    st.chainBroken      = kStatsSentinel;
    st.chainsEmitted    = kStatsSentinel;
    st.bytesScanned     = kStatsSentinel;
    st.failedReads      = kStatsSentinel;

    const std::vector<IL2CPP::ChainMatch> res =
        IL2CPP::DetectFieldAccessChains(slots, sfo, fo, limit, withStats ? &st : nullptr);

    const auto base = reinterpret_cast<std::uintptr_t>(img.base);
    Line("chain").S("run", label).U("nslots", slots.size()).Hex("sfo", sfo).Hex("fo", fo)
        .U("lim", limit).B("withStats", withStats).U("n", res.size()).Emit();
    for (std::size_t i = 0; i < res.size(); ++i) {
        // Module-relative (unsigned wrap is fine: both flavours wrap alike,
        // and a hit outside the module would itself be a finding).
        Line("cm").S("run", label).U("i", i)
            .Hex("hit", static_cast<std::uint64_t>(res[i].hitIP - base))
            .Hex("hop1", static_cast<std::uint64_t>(res[i].hopOneIP - base))
            .S("kind", res[i].refKind).Emit();
    }
    Line("cs").S("run", label).U("hopOneCandidates", st.hopOneCandidates)
        .U("chainBroken", st.chainBroken).U("chainsEmitted", st.chainsEmitted)
        .U("bytesScanned", st.bytesScanned).U("failedReads", st.failedReads).Emit();
}

std::string HexLabel(std::string_view prefix, std::size_t sfo, std::size_t fo) {
    std::string s(prefix);
    s += ":sfo=";
    AppendHexNum(s, sfo);
    s += ",fo=";
    AppendHexNum(s, fo);
    // AppendHexNum quotes its output; labels are plain strings.
    s.erase(std::remove(s.begin(), s.end(), '"'), s.end());
    return s;
}

int CmdChain(const wchar_t* path) {
    MappedImage img;
    if (const int rc = MapOrFail(path, img); rc != 0) return rc;

    std::uint64_t ripRefs = 0;
    std::size_t eligible = 0;
    const std::vector<SlotPick> picks = DiscoverSlots(img, ripRefs, eligible);

    const auto base = reinterpret_cast<std::uintptr_t>(img.base);
    std::vector<IL2CPP::ClassPointerSlot> slots;
    slots.reserve(picks.size());
    std::string pj = "[";
    for (std::size_t i = 0; i < picks.size(); ++i) {
        slots.push_back({base + picks[i].rva, base + img.data[picks[i].dataSec].rva});
        if (i) pj += ',';
        pj += '[';
        AppendHexNum(pj, picks[i].rva);
        pj += ',';
        AppendU(pj, picks[i].refs);
        pj += ']';
    }
    pj += ']';
    Line("img").Hex("sizeOfImage", img.sizeOfImage).Raw("exec", SectionsJson(img.exec))
        .Raw("data", SectionsJson(img.data)).Emit();
    Line("slots").U("ripRefs", ripRefs).U("eligible", eligible).U("picked", picks.size())
        .Raw("slots", pj).Emit();

    constexpr std::size_t kLimit = 100000;
    static constexpr std::size_t kSfo[] = {0xB8, 0x8, 0x18};
    static constexpr std::size_t kFo[]  = {0x0, 0x4, 0x8, 0x10, 0x18, 0x20, 0x30, 0x48};

    for (const std::size_t sfo : kSfo) {
        for (const std::size_t fo : kFo) RunChain(img, HexLabel("grid", sfo, fo), slots, sfo, fo, kLimit, true);
    }
    // Match-limit edges. The legacy loop checks the limit only AFTER pushing a
    // match, so limit 0 still yields one — the port must agree.
    for (const std::size_t lim : {std::size_t{0}, std::size_t{1}, std::size_t{5}}) {
        RunChain(img, "limit=" + std::to_string(lim), slots, 0xB8, 0x0, lim, true);
    }
    // Early-return paths: stats must stay untouched (sentinels survive).
    RunChain(img, "empty-slots", {}, 0xB8, 0x0, kLimit, true);
    RunChain(img, "sfo0", slots, 0, 0x0, kLimit, true);
    RunChain(img, "sfo0-empty", {}, 0, 0x10, kLimit, true);
    // NULL stats pointer.
    RunChain(img, "null-stats", slots, 0xB8, 0x10, kLimit, false);
    // Slot order / duplicates must not change anything observable.
    {
        std::vector<IL2CPP::ClassPointerSlot> rev(slots.rbegin(), slots.rend());
        rev.insert(rev.end(), slots.begin(), slots.end());
        RunChain(img, "reversed-dup", rev, 0xB8, 0x0, kLimit, true);
    }
    // Slots no instruction can reference (null, odd, all-ones, the header).
    {
        const std::vector<IL2CPP::ClassPointerSlot> bogus = {
            {0, 0}, {1, 0}, {UINTPTR_MAX, 0}, {base, base}};
        RunChain(img, "bogus-slots", bogus, 0xB8, 0x0, kLimit, true);
    }
    // A single slot: the lowest-RVA one, and the most referenced one.
    if (!slots.empty()) {
        RunChain(img, "single-lowest-rva", {slots.front()}, 0xB8, 0x0, kLimit, true);
        std::size_t topIdx = 0;
        for (std::size_t i = 1; i < picks.size(); ++i) {
            if (picks[i].refs > picks[topIdx].refs) topIdx = i;
        }
        RunChain(img, "single-most-referenced", {slots[topIdx]}, 0xB8, 0x10, kLimit, true);
    }
    return Finish();
}

int Usage() {
    std::fputs(
        "usage: r2-dump <subcommand> [args]\n"
        "  aob <GameAssembly.dll>\n"
        "  aob-synthetic\n"
        "  gm\n"
        "  refine\n"
        "  chain <GameAssembly.dll>\n",
        stderr);
    return 1;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    // No Windows error dialogs on a bad image — fail with an exit code.
    ::SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
    // Binary stdout: '\n' stays '\n', so the byte compare sees exactly what
    // was written. Large buffer: the refine dump alone is ~500k lines.
    (void)_setmode(_fileno(stdout), _O_BINARY);
    static char outBuf[1 << 20];
    std::setvbuf(stdout, outBuf, _IOFBF, sizeof(outBuf));

    if (argc < 2) return Usage();
    const std::wstring_view cmd = argv[1];
    if (cmd == L"aob-synthetic" && argc == 2) return CmdAobSynthetic();
    if (cmd == L"gm" && argc == 2) return CmdGm();
    if (cmd == L"refine" && argc == 2) return CmdRefine();
    if (cmd == L"aob" && argc == 3) return CmdAob(argv[2]);
    if (cmd == L"chain" && argc == 3) return CmdChain(argv[2]);
    return Usage();
}
