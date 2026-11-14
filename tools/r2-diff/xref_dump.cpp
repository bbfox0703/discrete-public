// xref_dump.cpp — regression driver for the IL2CPPFieldXref scan kernels
// (xref-dump.exe, never shipped).
//
// Links IL2CPPFieldXrefScanRs.cpp -> the Rust port in discrete_core;
// tools/verify/r2_golden.py compares its output with recorded SHA-256 hashes.
// Until the port was proven, the same source also built xref-dump-legacy
// (the legacy C++ kernels + Zydis) and tools/verify/xref_rust_diff.py
// required byte-identical exit code, stdout and stderr from the two; the
// golden hashes were recorded from that proven output. The driver calls ONLY
// the kernel API of dll/src/il2cpp/IL2CPPFieldXrefScan.h:
//   Discrete::IL2CPP::XrefScan::ScanRipRelative
//   Discrete::IL2CPP::XrefScan::ScanInstanceAccesses
//
// Output rules (what makes a hash compare meaningful):
//   * Deterministic: a local splitmix64 PRNG with fixed seeds, no clock, no
//     environment, no paths, no absolute addresses. Addresses are printed
//     relative to the mapped module (image subcommands) or to the synthetic
//     region (synthetic subcommands); synthetic displacements are computed
//     from region-relative positions, so the bytes under test do not depend
//     on where VirtualAlloc put the region.
//   * JSON lines on stdout (binary mode, '\n' only). Every subcommand ends
//     with {"rec":"end"}; a missing end record means the run died early.
//   * Exit 0 on success; 1 = usage error; 2 = image could not be mapped /
//     normalised; 3 = stdout write error; 4 = synthetic memory setup failed
//     (VirtualAlloc / VirtualProtect).
//
// Subcommands:
//   rip-synthetic             ScanRipRelative over crafted sections in a
//                             VirtualAlloc'd region (chunk boundaries, the
//                             16-byte overlap, short sections, unreadable
//                             pages / reserved memory, shifted / trimmed /
//                             duplicated / random section lists)
//   inst-synthetic            ScanInstanceAccesses over a generated x64
//                             instruction stream (legacy / VEX / EVEX / REX2 /
//                             EVEX map 4, disp8 / disp32 / disp8*N, 0xCC,
//                             junk, unreadable pages) plus a hand-written
//                             edge-case table
//   rip <GameAssembly.dll>    ScanRipRelative over the real executable
//                             sections, per harness-picked target
//   inst <GameAssembly.dll>   ScanInstanceAccesses over the .pdata function
//                             ranges: all of them x 3 field offsets, every
//                             16th x an 89-offset grid, every 16th misaligned
//                             x 4 (hits are printed as per-block counts +
//                             FNV-1a hashes)
//   inst-hits <GameAssembly.dll> <all|grid|misaligned> <fo-hex> <first-block> <nblocks>
//                             drill-down: every hit of those `inst` blocks

#include <windows.h>

#include <fcntl.h>
#include <io.h>

#include "il2cpp/IL2CPPFieldXrefScan.h"

#include <algorithm>
#include <charconv>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <limits>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace {

namespace XS = Discrete::IL2CPP::XrefScan;

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
    // True with probability pct / 100.
    bool Pct(unsigned pct) noexcept { return Below(100) < pct; }
    template <typename T, std::size_t N>
    T Pick(const T (&arr)[N]) noexcept { return arr[Below(N)]; }

private:
    std::uint64_t s_;
};

// --- JSON-line output -----------------------------------------------------------

void AppendU(std::string& s, std::uint64_t v) {
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

// Labels are harness-made printable ASCII; escape defensively anyway.
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
    Line& U(std::string_view k, std::uint64_t v)    { Key(k); AppendU(s_, v);      return *this; }
    Line& Hex(std::string_view k, std::uint64_t v)  { Key(k); AppendHexNum(s_, v); return *this; }
    Line& S(std::string_view k, std::string_view v) { Key(k); AppendStr(s_, v);    return *this; }
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
        std::fputs("xref-dump: stdout write error\n", stderr);
        return 3;
    }
    return 0;
}

// [["0x<ip-rel>",<opcode>],...] in kernel order.
std::string RipCandidatesJson(const std::vector<XS::RipCandidate>& c, std::uintptr_t base) {
    std::string s = "[";
    for (std::size_t i = 0; i < c.size(); ++i) {
        if (i) s += ',';
        s += '[';
        // Unsigned wrap is fine: both flavours wrap alike, and an IP outside
        // the region / module would itself be a finding.
        AppendHexNum(s, static_cast<std::uint64_t>(c[i].ip - base));
        s += ',';
        AppendU(s, c[i].opcode);
        s += ']';
    }
    s += ']';
    return s;
}

// FNV-1a 64 over a hit stream (inst subcommand blocks).
class Fnv64 {
public:
    void U64(std::uint64_t v) noexcept {
        for (int i = 0; i < 8; ++i) {
            h_ ^= static_cast<std::uint8_t>(v >> (8 * i));
            h_ *= 0x100000001B3ull;
        }
    }
    void U8(std::uint8_t v) noexcept {
        h_ ^= v;
        h_ *= 0x100000001B3ull;
    }
    std::uint64_t Value() const noexcept { return h_; }

private:
    std::uint64_t h_ = 0xCBF29CE484222325ull;
};

bool ParseHexU64(std::wstring_view w, std::uint64_t& out) {
    if (w.size() >= 2 && w[0] == L'0' && (w[1] == L'x' || w[1] == L'X')) w.remove_prefix(2);
    if (w.empty() || w.size() > 16) return false;
    std::uint64_t v = 0;
    for (const wchar_t c : w) {
        unsigned d;
        if (c >= L'0' && c <= L'9') d = static_cast<unsigned>(c - L'0');
        else if (c >= L'a' && c <= L'f') d = static_cast<unsigned>(c - L'a' + 10);
        else if (c >= L'A' && c <= L'F') d = static_cast<unsigned>(c - L'A' + 10);
        else return false;
        v = (v << 4) | d;
    }
    out = v;
    return true;
}

bool ParseDecU64(std::wstring_view w, std::uint64_t& out) {
    if (w.empty() || w.size() > 19) return false;
    std::uint64_t v = 0;
    for (const wchar_t c : w) {
        if (c < L'0' || c > L'9') return false;
        v = v * 10 + static_cast<unsigned>(c - L'0');
    }
    out = v;
    return true;
}

// --- GameAssembly.dll mapping -----------------------------------------------------
// Copied (not shared) from tools/r2-diff/r2_dump.cpp, whose stdout is pinned by
// tools/verify/r2_golden.json. Differences: no GetModuleHandleW name check (the
// kernels take explicit addresses), and the exception directory is recorded.

struct Section {
    std::uint32_t rva  = 0;
    std::uint32_t size = 0;  // Misc.VirtualSize — what IL2CPPFieldXref.cpp uses
};

struct MappedImage {
    std::uint8_t*        base        = nullptr;
    std::uint32_t        sizeOfImage = 0;
    std::vector<Section> exec;  // IMAGE_SCN_MEM_EXECUTE, header order
    std::vector<Section> data;  // everything else, header order
    std::uint32_t        pdataRva    = 0;  // IMAGE_DIRECTORY_ENTRY_EXCEPTION
    std::uint32_t        pdataSize   = 0;
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
// executable section to the exact bytes stored in the file, so every run
// scans identical code wherever the loader
// placed the module — base relocations patch code bytes as a function of the
// load address; file bytes do not depend on it. RIP-relative displacements
// never carry relocations, and every printed address is module-relative.
bool MapGameAssembly(const wchar_t* path, MappedImage& img, std::string& err) {
    // DONT_RESOLVE_DLL_REFERENCES: map + relocate only — no import
    // resolution, no DllMain / TLS callbacks.
    HMODULE mod = ::LoadLibraryExW(path, nullptr, DONT_RESOLVE_DLL_REFERENCES);
    if (mod == nullptr) {
        err = "LoadLibraryExW failed (error " + std::to_string(::GetLastError()) + ")";
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
    if (nt->OptionalHeader.NumberOfRvaAndSizes > IMAGE_DIRECTORY_ENTRY_EXCEPTION) {
        const IMAGE_DATA_DIRECTORY& d = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION];
        if (static_cast<std::uint64_t>(d.VirtualAddress) + d.Size <= img.sizeOfImage) {
            img.pdataRva  = d.VirtualAddress;
            img.pdataSize = d.Size;
        }
    }

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
        // Re-protect in alternating 64 KiB stripes of two READABLE protections.
        // Judgement call, harness speed only: Memory::SafeReadBytes (which
        // both flavours read through) calls VirtualQuery, whose cost grows
        // with the size of the region it lands in — one ~100 MiB region makes
        // every per-function read cost ~0.1-0.3 ms. Stripes keep regions small;
        // readability, bytes and addresses are unchanged. Best effort: the
        // harness never executes this code.
        constexpr std::size_t kStripe = 64u * 1024u;
        for (std::size_t off = 0; off < sec.size; off += kStripe) {
            const std::size_t n = (std::min)(kStripe, static_cast<std::size_t>(sec.size) - off);
            DWORD ignored = 0;
            ::VirtualProtect(static_cast<std::uint8_t*>(dst) + off, n,
                             (off / kStripe) % 2 ? PAGE_READONLY : PAGE_EXECUTE_READ, &ignored);
        }
        (void)oldProt;
    }
    ::CloseHandle(file);
    return ok;
}

int MapOrFail(const wchar_t* path, MappedImage& img) {
    std::string err;
    if (!MapGameAssembly(path, img, err)) {
        // No path in the message: the two flavours must print identical bytes.
        std::fprintf(stderr, "xref-dump: cannot map image: %s\n", err.c_str());
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

std::vector<XS::ExecSection> AbsExecSections(const MappedImage& img) {
    std::vector<XS::ExecSection> out;
    for (const Section& s : img.exec) {
        out.push_back({reinterpret_cast<std::uintptr_t>(img.base) + s.rva, s.size});
    }
    return out;
}

// --- synthetic region -------------------------------------------------------------

// One VirtualAlloc reservation; parts are committed / protected by the
// subcommands. Everything printed is relative to `base`.
class Region {
public:
    explicit Region(std::size_t size) : size_(size) {
        base_ = static_cast<std::uint8_t*>(::VirtualAlloc(nullptr, size, MEM_RESERVE, PAGE_NOACCESS));
    }
    ~Region() {
        if (base_) ::VirtualFree(base_, 0, MEM_RELEASE);
    }
    Region(const Region&) = delete;
    Region& operator=(const Region&) = delete;

    bool Ok() const noexcept { return base_ != nullptr; }
    std::uint8_t* Base() const noexcept { return base_; }
    std::uintptr_t Addr(std::size_t rel) const noexcept {
        return reinterpret_cast<std::uintptr_t>(base_) + rel;
    }
    bool Commit(std::size_t rel, std::size_t len) const noexcept {
        return ::VirtualAlloc(base_ + rel, len, MEM_COMMIT, PAGE_READWRITE) != nullptr;
    }
    bool Protect(std::size_t rel, std::size_t len, DWORD prot) const noexcept {
        DWORD old = 0;
        return ::VirtualProtect(base_ + rel, len, prot, &old) != 0;
    }

private:
    std::uint8_t* base_ = nullptr;
    std::size_t   size_ = 0;
};

int SetupFailed(const char* what) {
    std::fprintf(stderr, "xref-dump: synthetic setup failed: %s (error %lu)\n", what,
                 static_cast<unsigned long>(::GetLastError()));
    return 4;
}

// --- rip-synthetic ----------------------------------------------------------------

constexpr std::size_t kMiB = 1024u * 1024u;

// Region layout (offsets relative to the region base).
constexpr std::size_t kRipRegion = 16 * kMiB;
// Area A: committed [0, 0x310000). SA starts unaligned; its 1 MiB chunk
// boundaries sit at SA + k MiB = 0x100100, 0x200100.
constexpr std::size_t kAreaA = 0, kAreaALen = 0x310000;
constexpr std::size_t kSA = 0x100, kSALen = 0x300000 - 0x37;
// Area B: committed [0x400000, 0x600000) with a NOACCESS page in the middle of
// SB's first chunk and a GUARD page in its second.
constexpr std::size_t kAreaB = 0x400000, kAreaBLen = 0x200000;
constexpr std::size_t kBNoAccess = 0x480000, kBGuard = 0x5A0000;
constexpr std::size_t kSB = 0x400010, kSBLen = 0x1FFFD0;
// Area C: committed [0x600000, 0x800000) with a NOACCESS page right where
// SC's first chunk's 16-byte overlap lands (owned bytes readable, overlap not).
constexpr std::size_t kAreaC = 0x600000, kAreaCLen = 0x200000;
constexpr std::size_t kCNoAccess = 0x702000;
constexpr std::size_t kSC = kCNoAccess - kMiB - 8, kSCLen = 0x180000;
// SC2 ends exactly where the NOACCESS page begins.
constexpr std::size_t kSC2 = kCNoAccess - 0x10000, kSC2Len = 0x10000;
// Area D: 64 KiB committed at 0x800000, reserved-only after it.
constexpr std::size_t kAreaD = 0x800000, kAreaDLen = 0x10000;
constexpr std::size_t kSE2 = 0x808000, kSE2Len = 0x18000;  // runs into reserved memory
// Area E: reserved only (never committed).
constexpr std::size_t kSE = 0xA00000, kSELen = 0x130000;

// Targets (region-relative unless absolute). They only have to be numbers:
// most lie in reserved memory nobody reads.
constexpr std::size_t kTA1 = 0xC00000;                       // boundary / edge plants in SA
constexpr std::size_t kTFuzz[] = {0xC00008, 0xC00010, 0xC00018, 0xC00020};  // dense fuzz zone
constexpr std::size_t kTB  = 0xC00100;                       // plants in B / C / D / E
constexpr std::size_t kTN  = 0xC00200;                       // never planted
constexpr std::size_t kTS  = kSA;                            // SA's own first byte

inline bool IsRex(std::uint8_t b) { return (b & 0xF0u) == 0x40u; }

constexpr std::uint8_t kRipOps[] = {0x8B, 0x8D, 0x89};

// Writes one candidate encoding at `pos` aimed at `target`:
//   form 7: REX op modrm(rip) disp32, disp relative to pos + 7
//   form 6:     op modrm(rip) disp32, disp relative to pos + 6
// Returns the length written.
std::size_t PlantRip(std::uint8_t* r, std::size_t pos, int form, std::uint8_t op,
                     std::uint8_t reg, std::size_t target, SplitMix64& rng) {
    std::size_t p = pos;
    if (form == 7) r[p++] = static_cast<std::uint8_t>(0x40 | (rng.Byte() & 0x0F));
    r[p++] = op;
    r[p++] = static_cast<std::uint8_t>(((reg & 7) << 3) | 0x05);
    const std::int64_t disp = static_cast<std::int64_t>(target) - static_cast<std::int64_t>(p + 4);
    const auto d = static_cast<std::uint32_t>(static_cast<std::int32_t>(disp));
    std::memcpy(r + p, &d, 4);
    return p + 4 - pos;
}

std::size_t PlantRandomRip(std::uint8_t* r, std::size_t pos, std::size_t target, SplitMix64& rng) {
    const int form = rng.Pct(55) ? 7 : 6;
    return PlantRip(r, pos, form, rng.Pick(kRipOps), rng.Byte(), target, rng);
}

// Back-to-back plants (0..2 noise bytes between them) over [from, to).
void PlantZone(std::uint8_t* r, std::size_t from, std::size_t to, std::size_t target, SplitMix64& rng) {
    std::size_t p = from;
    while (p + 7 <= to) {
        p += PlantRandomRip(r, p, target, rng);
        const std::size_t gap = rng.Below(3);
        for (std::size_t i = 0; i < gap && p < to; ++i) r[p++] = rng.Byte();
    }
}

// Dense mixture of hits, near misses and decoys aimed at the fuzz targets.
void FuzzZone(std::uint8_t* r, std::size_t from, std::size_t to, SplitMix64& rng) {
    std::size_t p = from;
    while (p + 9 <= to) {
        const std::size_t t = rng.Pick(kTFuzz);
        const std::uint64_t k = rng.Below(100);
        if (k < 30) {
            p += PlantRip(r, p, 7, rng.Pick(kRipOps), rng.Byte(), t, rng);
        } else if (k < 55) {
            p += PlantRip(r, p, 6, rng.Pick(kRipOps), rng.Byte(), t, rng);
        } else if (k < 62) {
            // REX REX op modrm disp: the 7-byte form starts at p + 1.
            r[p] = static_cast<std::uint8_t>(0x40 | (rng.Byte() & 0x0F));
            p += 1 + PlantRip(r, p + 1, 7, rng.Pick(kRipOps), rng.Byte(), t, rng);
        } else if (k < 67) {
            // Non-REX prefix + 6-byte form: legacy sees the 6-byte form at p + 1.
            static constexpr std::uint8_t kPfx[] = {0x66, 0xF0, 0xF2, 0xF3, 0x2E, 0x64, 0x65, 0x67};
            r[p] = rng.Pick(kPfx);
            p += 1 + PlantRip(r, p + 1, 6, rng.Pick(kRipOps), rng.Byte(), t, rng);
        } else if (k < 72) {
            // REX + 6-byte form aimed one byte short of the target: the 7-byte
            // form at p misses, and the 6-byte form at p + 1 (which would hit
            // t - 1) is skipped because the previous byte is REX.
            r[p] = static_cast<std::uint8_t>(0x40 | (rng.Byte() & 0x0F));
            p += 1 + PlantRip(r, p + 1, 6, rng.Pick(kRipOps), rng.Byte(), t - 1, rng);
        } else if (k < 77) {
            // Near miss: right opcode, ModRM not RIP-relative.
            const std::size_t n = PlantRip(r, p, rng.Pct(50) ? 7 : 6, rng.Pick(kRipOps), rng.Byte(), t, rng);
            const std::size_t mpos = p + n - 5;
            r[mpos] = static_cast<std::uint8_t>(r[mpos] ^ (rng.Pct(50) ? 0x40 : (1 + rng.Below(7))));
            p += n;
        } else if (k < 80) {
            // Near miss: neighbouring opcodes.
            static constexpr std::uint8_t kOps[] = {0x88, 0x8A, 0x8C, 0x8E, 0x8F, 0x0B, 0xC7};
            p += PlantRip(r, p, rng.Pct(50) ? 7 : 6, rng.Pick(kOps), rng.Byte(), t, rng);
        } else {
            const std::size_t n = 1 + rng.Below(3);
            for (std::size_t i = 0; i < n; ++i) r[p++] = rng.Byte();
        }
    }
}

struct RipTarget {
    std::string_view name;
    bool             absolute;  // value is an absolute address, not region-relative
    std::uint64_t    value;
};

struct RelSection {
    std::size_t rel;
    std::size_t len;
};

void RunRip(const Region& rg, std::string_view scenario, const std::vector<RelSection>& secs,
            const std::vector<RipTarget>& targets) {
    std::vector<XS::ExecSection> abs;
    abs.reserve(secs.size());
    std::string sj = "[";
    for (std::size_t i = 0; i < secs.size(); ++i) {
        abs.push_back({rg.Addr(secs[i].rel), secs[i].len});
        if (i) sj += ',';
        sj += '[';
        AppendHexNum(sj, secs[i].rel);
        sj += ',';
        AppendU(sj, secs[i].len);
        sj += ']';
    }
    sj += ']';
    Line("rsc").S("sc", scenario).Raw("secs", sj).Emit();
    for (const RipTarget& t : targets) {
        const std::uintptr_t target = t.absolute ? static_cast<std::uintptr_t>(t.value) : rg.Addr(t.value);
        const std::vector<XS::RipCandidate> c = XS::ScanRipRelative(abs, target);
        Line("rip").S("sc", scenario).S("t", t.name).U("n", c.size())
            .Raw("c", RipCandidatesJson(c, reinterpret_cast<std::uintptr_t>(rg.Base()))).Emit();
    }
}

int CmdRipSynthetic() {
    Region rg(kRipRegion);
    if (!rg.Ok()) return SetupFailed("VirtualAlloc reserve");
    if (!rg.Commit(kAreaA, kAreaALen) || !rg.Commit(kAreaB, kAreaBLen) ||
        !rg.Commit(kAreaC, kAreaCLen) || !rg.Commit(kAreaD, kAreaDLen)) {
        return SetupFailed("VirtualAlloc commit");
    }
    std::uint8_t* r = rg.Base();
    SplitMix64 rng(0x5EC7105CA4A11CE5ull);

    // Noise everywhere committed.
    for (const auto& [from, len] : {std::pair{kAreaA, kAreaALen}, std::pair{kAreaB, kAreaBLen},
                                    std::pair{kAreaC, kAreaCLen}, std::pair{kAreaD, kAreaDLen}}) {
        for (std::size_t i = 0; i < len; i += 8) {
            const std::uint64_t v = rng.Next();
            std::memcpy(r + from + i, &v, 8);
        }
    }

    // --- SA: start, both chunk boundaries, end; plus the fuzz zone across
    // the second boundary. Shift / trim scenarios below move the boundaries
    // and the end across these plants one byte at a time.
    const std::size_t saEnd = kSA + kSALen;
    PlantZone(r, kSA, kSA + 64, kTA1, rng);
    for (const std::size_t b : {kSA + kMiB, kSA + 2 * kMiB}) PlantZone(r, b - 48, b + 48, kTA1, rng);
    PlantZone(r, saEnd - 64, saEnd, kTA1, rng);
    // Exact-end forms: a 7-byte one ending at saEnd and a 6-byte one ending
    // 9 bytes earlier (whether the trimmed end sees them depends on trim).
    PlantRip(r, saEnd - 7, 7, 0x8B, 0, kTA1, rng);
    PlantRip(r, saEnd - 16, 6, 0x89, 1, kTA1, rng);
    // Sparse plants across the whole section.
    for (int i = 0; i < 64; ++i) {
        const std::size_t p = kSA + 64 + rng.Below(kSALen - 200);
        PlantRandomRip(r, p, kTA1, rng);
    }
    FuzzZone(r, kSA + 2 * kMiB - 8 * 1024, kSA + 2 * kMiB + 8 * 1024, rng);
    FuzzZone(r, kSA + kMiB - 2 * 1024, kSA + kMiB - 64, rng);

    // --- SB: plants around the NOACCESS page, the GUARD page, SB's chunk
    // boundary and its end, plus random ones.
    const std::size_t sbEnd = kSB + kSBLen;
    PlantZone(r, kSB, kSB + 48, kTB, rng);
    PlantZone(r, kBNoAccess - 64, kBNoAccess, kTB, rng);
    PlantZone(r, kBNoAccess + 0x1000, kBNoAccess + 0x1000 + 64, kTB, rng);
    PlantZone(r, kSB + kMiB - 48, kSB + kMiB + 48, kTB, rng);
    PlantZone(r, kBGuard - 64, kBGuard, kTB, rng);
    PlantZone(r, kBGuard + 0x1000, kBGuard + 0x1000 + 64, kTB, rng);
    PlantZone(r, sbEnd - 48, sbEnd, kTB, rng);
    for (int i = 0; i < 200; ++i) PlantRandomRip(r, kSB + rng.Below(kSBLen - 8), kTB, rng);

    // --- SC / SC2: plants just before SC's first owned end (lost: the read
    // including the overlap fails), around the NOACCESS page, at SC2's end.
    PlantZone(r, kSC, kSC + 48, kTB, rng);
    PlantZone(r, kSC + kMiB - 48, kSC + kMiB, kTB, rng);
    PlantZone(r, kCNoAccess + 0x1000, kCNoAccess + 0x1000 + 64, kTB, rng);
    PlantZone(r, kSC2 + kSC2Len - 64, kSC2 + kSC2Len - 7, kTB, rng);
    PlantRip(r, kSC2 + kSC2Len - 7, 7, 0x8D, 2, kTB, rng);
    for (int i = 0; i < 100; ++i) PlantRandomRip(r, kSC + rng.Below(kSCLen - 8), kTB, rng);

    // --- D: tiny sections, each crafted (offsets are section-relative).
    struct Tiny { std::size_t len; std::vector<std::pair<std::size_t, int>> plants; };
    const std::vector<Tiny> tiny = {
        {0, {}},
        {1, {}},
        {5, {}},
        {6, {{0, 6}}},           // exact 6-byte form: readWant < 7 -> never scanned
        {7, {{0, 7}}},           // exact 7-byte form
        {7, {{1, 6}}},           // 6-byte form at 1: scanEnd = 1 -> missed
        {8, {{2, 6}}},
        {8, {{0, 6}}},
        {12, {{0, 6}, {6, 6}}},  // second one ends at the end: scanEnd = 6 -> missed
        {13, {{0, 6}, {6, 7}}},
        {14, {{0, 7}, {7, 7}}},
        {16, {{0, 7}, {9, 7}}},
        {17, {{1, 7}, {10, 7}}},
        {23, {{0, 6}, {6, 6}, {12, 6}, {16, 7}}},
        {24, {{0, 7}, {7, 6}, {13, 7}}},
    };
    std::vector<RelSection> dSecs;
    for (std::size_t i = 0; i < tiny.size(); ++i) {
        const std::size_t at = kAreaD + i * 0x100;
        for (const auto& [off, form] : tiny[i].plants) {
            PlantRip(r, at + off, form, rng.Pick(kRipOps), rng.Byte(), kTB, rng);
        }
        dSecs.push_back({at, tiny[i].len});
    }
    // Adjacent sections with an instruction straddling their shared edge.
    const std::size_t adj = kAreaD + 0x1000;
    PlantRip(r, adj + 0, 7, 0x8B, 3, kTB, rng);
    PlantRip(r, adj + 6, 7, 0x8B, 4, kTB, rng);   // crosses adj + 10
    PlantRip(r, adj + 13, 6, 0x89, 5, kTB, rng);
    PlantRip(r, adj + 23, 7, 0x8D, 6, kTB, rng);
    dSecs.push_back({adj, 10});
    dSecs.push_back({adj + 10, 20});
    // SE2's committed head (lost: every read reaches the reserved tail).
    PlantZone(r, kSE2, kSE2 + 64, kTB, rng);

    // Protections last (the noise / plants above wrote through them).
    if (!rg.Protect(kBNoAccess, 0x1000, PAGE_NOACCESS) ||
        !rg.Protect(kBGuard, 0x1000, PAGE_READWRITE | PAGE_GUARD) ||
        !rg.Protect(kCNoAccess, 0x1000, PAGE_NOACCESS) ||
        !rg.Protect(kAreaA, kAreaALen, PAGE_EXECUTE_READ)) {
        return SetupFailed("VirtualProtect");
    }

    const std::vector<RipTarget> tA = {
        {"TA1", false, kTA1}, {"TF0", false, kTFuzz[0]}, {"TF1", false, kTFuzz[1]},
        {"TF2", false, kTFuzz[2]}, {"TF3", false, kTFuzz[3]}, {"TN", false, kTN}, {"TS", false, kTS}};
    const std::vector<RipTarget> tB = {{"TB", false, kTB}, {"TN", false, kTN}};
    std::vector<RipTarget> tAll = tA;
    tAll.push_back({"TB", false, kTB});
    tAll.push_back({"abs0", true, 0});
    tAll.push_back({"absMax", true, UINTPTR_MAX});

    Line("rlayout").Hex("region", kRipRegion).Hex("sa", kSA).U("saLen", kSALen)
        .Hex("sb", kSB).U("sbLen", kSBLen).Hex("sc", kSC).U("scLen", kSCLen)
        .Hex("sc2", kSC2).U("sc2Len", kSC2Len).Hex("se", kSE).U("seLen", kSELen)
        .Hex("se2", kSE2).U("se2Len", kSE2Len).Emit();

    const RelSection sa{kSA, kSALen}, sb{kSB, kSBLen}, sc{kSC, kSCLen}, sc2{kSC2, kSC2Len},
        se{kSE, kSELen}, se2{kSE2, kSE2Len};

    RunRip(rg, "A", {sa}, tA);
    std::vector<std::size_t> shifts;
    for (std::size_t s = 1; s <= 17; ++s) shifts.push_back(s);
    for (const std::size_t s : {std::size_t{40}, std::size_t{4095}, std::size_t{4096},
                                std::size_t{kMiB - 7}, std::size_t{kMiB - 1}, std::size_t{kMiB}}) {
        shifts.push_back(s);
    }
    for (const std::size_t s : shifts) {
        RunRip(rg, "A-shift-" + std::to_string(s), {{kSA + s, kSALen - s}}, tA);
    }
    for (std::size_t t = 1; t <= 17; ++t) {
        RunRip(rg, "A-trim-" + std::to_string(t), {{kSA, kSALen - t}}, tA);
    }
    // Sections that end inside / at the first chunk's overlap.
    for (const std::size_t extra : {std::size_t{0}, std::size_t{1}, std::size_t{6}, std::size_t{7},
                                    std::size_t{15}, std::size_t{16}, std::size_t{17}}) {
        RunRip(rg, "A-len-1MiB+" + std::to_string(extra), {{kSA, kMiB + extra}}, tA);
    }

    RunRip(rg, "B", {sb}, tB);
    for (const std::size_t s : {std::size_t{1}, std::size_t{7}, std::size_t{0xFF0},
                                std::size_t{kBNoAccess - kSB - 0x800}, std::size_t{kBNoAccess - kSB + 0x1000}}) {
        RunRip(rg, "B-shift-" + std::to_string(s), {{kSB + s, kSBLen - s}}, tB);
    }
    RunRip(rg, "C", {sc, sc2}, tB);
    // Move SC so the NOACCESS page starts d bytes after its first chunk's
    // owned end: the read covers the owned bytes plus up to 16 overlap bytes,
    // so d <= 15 fails the chunk (4 KiB skip loop) and d >= 16 does not.
    for (std::size_t d = 0; d <= 17; ++d) {
        const std::size_t start = kCNoAccess - kMiB - d;
        RunRip(rg, "C-page-at-end+" + std::to_string(d), {{start, kSCLen}}, tB);
    }
    RunRip(rg, "D", dSecs, tB);
    RunRip(rg, "E", {se, se2}, tB);

    std::vector<RelSection> all = {sa, sb, sc, sc2};
    all.insert(all.end(), dSecs.begin(), dSecs.end());
    all.push_back(se);
    all.push_back(se2);
    RunRip(rg, "all", all, tAll);
    RunRip(rg, "all-rev", std::vector<RelSection>(all.rbegin(), all.rend()), tAll);
    RunRip(rg, "dup", {sa, dSecs[4], sa, dSecs[4]}, tAll);
    RunRip(rg, "empty", {}, tAll);

    // Random section lists over the committed areas (crossing NOACCESS /
    // GUARD / reserved memory at will).
    struct Area { std::size_t from, len; };
    static constexpr Area kAreas[] = {{kAreaA, kAreaALen}, {kAreaB, kAreaBLen}, {kAreaC, kAreaCLen},
                                      {kAreaD, kAreaDLen}};
    for (int sc_i = 0; sc_i < 32; ++sc_i) {
        std::vector<RelSection> secs;
        const std::size_t n = 1 + rng.Below(4);
        for (std::size_t k = 0; k < n; ++k) {
            const Area a = rng.Pick(kAreas);
            const std::size_t start = a.from + rng.Below(a.len);
            std::size_t len;
            switch (rng.Below(4)) {
                case 0:  len = rng.Below(32); break;
                case 1:  len = rng.Below(0x4000); break;
                case 2:  len = kMiB - 32 + rng.Below(64); break;
                default: len = rng.Below(5 * kMiB / 2); break;
            }
            if (start + len > kRipRegion) len = kRipRegion - start;
            secs.push_back({start, len});
        }
        RunRip(rg, "rnd-" + std::to_string(sc_i), secs, tAll);
    }
    return Finish();
}

// --- instruction generator (inst-synthetic) ---------------------------------------

// Displacements biased toward the field offsets the scans ask for.
constexpr std::uint8_t kDisp8Pool[] = {0x00, 0x01, 0x04, 0x08, 0x10, 0x18, 0x20, 0x40, 0x58,
                                       0x7F, 0x80, 0xF8, 0xC0};
constexpr std::uint32_t kDisp32Pool[] = {0x0, 0x4, 0x10, 0x40, 0x58, 0x80, 0xB8, 0x100, 0x1000,
                                         0x10001, 0x7FFFFFFF, 0x80000000u, 0xFFFFFFFFu,
                                         0xFFFFFFF8u, 0xFFFFFF80u};

void PushU32(std::vector<std::uint8_t>& v, std::uint32_t x) {
    for (int i = 0; i < 4; ++i) v.push_back(static_cast<std::uint8_t>(x >> (8 * i)));
}
void PushRandom(SplitMix64& rng, std::vector<std::uint8_t>& v, std::size_t n) {
    for (std::size_t i = 0; i < n; ++i) v.push_back(rng.Byte());
}

// ModRM (+ SIB + displacement). `memOnly` forbids mod=11; `vsib` forces a
// SIB byte (VSIB gathers / scatters).
void PushModrm(SplitMix64& rng, std::vector<std::uint8_t>& v, std::uint8_t reg, bool memOnly,
               bool vsib = false) {
    static constexpr unsigned kModW[] = {15, 55, 85, 100};  // cumulative: 00, 01, 10, 11
    unsigned mod = 0;
    const std::uint64_t k = rng.Below(memOnly || vsib ? 85 : 100);
    while (k >= kModW[mod]) ++mod;
    const auto rm = static_cast<std::uint8_t>(vsib ? 4 : rng.Below(8));
    v.push_back(static_cast<std::uint8_t>((mod << 6) | ((reg & 7) << 3) | rm));
    if (mod == 3) return;
    bool disp32NoBase = false;
    if (rm == 4) {
        const auto ss = static_cast<std::uint8_t>(rng.Below(4));
        const auto idx = static_cast<std::uint8_t>(!vsib && rng.Pct(55) ? 4 : rng.Below(8));
        const auto base = static_cast<std::uint8_t>(rng.Below(8));
        v.push_back(static_cast<std::uint8_t>((ss << 6) | (idx << 3) | base));
        if (mod == 0 && base == 5) disp32NoBase = true;
    }
    if (mod == 1) {
        v.push_back(rng.Pct(75) ? rng.Pick(kDisp8Pool) : rng.Byte());
    } else if (mod == 2 || disp32NoBase || (mod == 0 && rm == 5)) {
        PushU32(v, rng.Pct(70) ? rng.Pick(kDisp32Pool) : static_cast<std::uint32_t>(rng.Next()));
    }
}

// Immediate size codes.
enum : std::uint8_t { I0 = 0, I8 = 1, IZ = 2 };

struct OpDef {
    std::uint8_t op;
    std::uint8_t imm;
};

// Map 0 opcodes with a ModRM byte.
constexpr OpDef kMap0[] = {
    {0x00, I0}, {0x01, I0}, {0x02, I0}, {0x03, I0}, {0x08, I0}, {0x09, I0}, {0x0A, I0}, {0x0B, I0},
    {0x10, I0}, {0x11, I0}, {0x12, I0}, {0x13, I0}, {0x18, I0}, {0x19, I0}, {0x1A, I0}, {0x1B, I0},
    {0x20, I0}, {0x21, I0}, {0x22, I0}, {0x23, I0}, {0x28, I0}, {0x29, I0}, {0x2A, I0}, {0x2B, I0},
    {0x30, I0}, {0x31, I0}, {0x32, I0}, {0x33, I0}, {0x38, I0}, {0x39, I0}, {0x3A, I0}, {0x3B, I0},
    {0x63, I0}, {0x69, IZ}, {0x6B, I8}, {0x80, I8}, {0x81, IZ}, {0x83, I8}, {0x84, I0}, {0x85, I0},
    {0x86, I0}, {0x87, I0}, {0x88, I0}, {0x89, I0}, {0x8A, I0}, {0x8B, I0}, {0x8B, I0}, {0x89, I0},
    {0x8C, I0}, {0x8D, I0}, {0x8D, I0}, {0x8E, I0}, {0x8F, I0}, {0xC0, I8}, {0xC1, I8}, {0xC6, I8},
    {0xC7, IZ}, {0xD0, I0}, {0xD1, I0}, {0xD2, I0}, {0xD3, I0}, {0xD8, I0}, {0xD9, I0}, {0xDB, I0},
    {0xDD, I0}, {0xDF, I0}, {0xF6, I0}, {0xF7, I0}, {0xFE, I0}, {0xFF, I0}, {0xFF, I0},
};
// Map 1 (0F xx) opcodes with a ModRM byte.
constexpr OpDef kMap1[] = {
    {0x00, I0}, {0x01, I0}, {0x0D, I0}, {0x10, I0}, {0x11, I0}, {0x12, I0}, {0x13, I0}, {0x14, I0},
    {0x16, I0}, {0x17, I0}, {0x18, I0}, {0x19, I0}, {0x1A, I0}, {0x1B, I0}, {0x1C, I0}, {0x1E, I0},
    {0x1F, I0}, {0x1F, I0}, {0x28, I0}, {0x29, I0}, {0x2A, I0}, {0x2B, I0}, {0x2C, I0}, {0x2E, I0},
    {0x2F, I0}, {0x40, I0}, {0x44, I0}, {0x45, I0}, {0x4C, I0}, {0x4F, I0}, {0x51, I0}, {0x54, I0},
    {0x57, I0}, {0x58, I0}, {0x59, I0}, {0x5A, I0}, {0x5B, I0}, {0x5C, I0}, {0x5E, I0}, {0x60, I0},
    {0x62, I0}, {0x66, I0}, {0x6E, I0}, {0x6F, I0}, {0x70, I8}, {0x71, I8}, {0x72, I8}, {0x73, I8},
    {0x74, I0}, {0x76, I0}, {0x7E, I0}, {0x7F, I0}, {0x90, I0}, {0x94, I0}, {0x95, I0}, {0x9F, I0},
    {0xA3, I0}, {0xA4, I8}, {0xA5, I0}, {0xAB, I0}, {0xAC, I8}, {0xAD, I0}, {0xAE, I0}, {0xAF, I0},
    {0xB0, I0}, {0xB1, I0}, {0xB3, I0}, {0xB6, I0}, {0xB7, I0}, {0xB8, I0}, {0xBA, I8}, {0xBB, I0},
    {0xBC, I0}, {0xBD, I0}, {0xBE, I0}, {0xBF, I0}, {0xC0, I0}, {0xC1, I0}, {0xC2, I8}, {0xC3, I0},
    {0xC4, I8}, {0xC5, I8}, {0xC6, I8}, {0xC7, I0}, {0xD4, I0}, {0xD6, I0}, {0xDB, I0}, {0xE6, I0},
    {0xE7, I0}, {0xEF, I0}, {0xF0, I0}, {0xFE, I0}, {0xFF, I0},
};

// Opcodes that accept LOCK with a memory destination (map 0, then 0F).
constexpr std::uint8_t kLockMap0[] = {0x00, 0x01, 0x08, 0x09, 0x10, 0x11, 0x18, 0x19, 0x20, 0x21,
                                      0x28, 0x29, 0x30, 0x31, 0x80, 0x81, 0x83, 0x86, 0x87, 0xF6,
                                      0xF7, 0xFE, 0xFF};
constexpr std::uint8_t kLockMap1[] = {0xAB, 0xB0, 0xB1, 0xB3, 0xBB, 0xBA, 0xC0, 0xC1, 0xC7};

template <std::size_t N>
bool InSet(const std::uint8_t (&set)[N], std::uint8_t b) {
    for (const std::uint8_t x : set) {
        if (x == b) return true;
    }
    return false;
}

// Template of a VEX / EVEX / 0F38 / 0F3A instruction. pp: 0 none, 1 66,
// 2 F3, 3 F2. w / l: 0, 1, or kAny. nds: vvvv names a source (random)
// instead of being 1111. mem: memory form only. vsib: VSIB addressing.
constexpr std::uint8_t kAny = 2;
struct VecT {
    std::uint8_t map, pp, w, l, nds, op, imm, mem, vsib;
};

// 0F38 / 0F3A (legacy SSE family): map 2 = 0F38, map 3 = 0F3A (imm8).
constexpr VecT kSse38[] = {
    {2, 1, 0, 0, 0, 0x00, 0, 0, 0}, {2, 1, 0, 0, 0, 0x01, 0, 0, 0}, {2, 1, 0, 0, 0, 0x04, 0, 0, 0},
    {2, 1, 0, 0, 0, 0x08, 0, 0, 0}, {2, 1, 0, 0, 0, 0x0B, 0, 0, 0}, {2, 1, 0, 0, 0, 0x10, 0, 0, 0},
    {2, 1, 0, 0, 0, 0x14, 0, 0, 0}, {2, 1, 0, 0, 0, 0x17, 0, 0, 0}, {2, 1, 0, 0, 0, 0x1C, 0, 0, 0},
    {2, 1, 0, 0, 0, 0x1E, 0, 0, 0}, {2, 1, 0, 0, 0, 0x20, 0, 0, 0}, {2, 1, 0, 0, 0, 0x23, 0, 0, 0},
    {2, 1, 0, 0, 0, 0x25, 0, 0, 0}, {2, 1, 0, 0, 0, 0x28, 0, 0, 0}, {2, 1, 0, 0, 0, 0x29, 0, 0, 0},
    {2, 1, 0, 0, 0, 0x2A, 0, 1, 0}, {2, 1, 0, 0, 0, 0x2B, 0, 0, 0}, {2, 1, 0, 0, 0, 0x30, 0, 0, 0},
    {2, 1, 0, 0, 0, 0x35, 0, 0, 0}, {2, 1, 0, 0, 0, 0x37, 0, 0, 0}, {2, 1, 0, 0, 0, 0x38, 0, 0, 0},
    {2, 1, 0, 0, 0, 0x3B, 0, 0, 0}, {2, 1, 0, 0, 0, 0x3F, 0, 0, 0}, {2, 1, 0, 0, 0, 0x40, 0, 0, 0},
    {2, 1, 0, 0, 0, 0x41, 0, 0, 0}, {2, 0, 0, 0, 0, 0x00, 0, 0, 0}, {2, 0, 0, 0, 0, 0x0B, 0, 0, 0},
    {2, 0, 0, 0, 0, 0xC8, 0, 0, 0}, {2, 0, 0, 0, 0, 0xC9, 0, 0, 0}, {2, 0, 0, 0, 0, 0xCB, 0, 0, 0},
    {2, 0, 0, 0, 0, 0xCD, 0, 0, 0}, {2, 0, 0, 0, 0, 0xF0, 0, 1, 0}, {2, 0, 0, 0, 0, 0xF1, 0, 1, 0},
    {2, 3, 0, 0, 0, 0xF0, 0, 0, 0}, {2, 3, 0, 0, 0, 0xF1, 0, 0, 0}, {2, 1, 0, 0, 0, 0xF6, 0, 0, 0},
    {2, 2, 0, 0, 0, 0xF6, 0, 0, 0}, {2, 1, 0, 0, 0, 0xF8, 0, 1, 0}, {2, 3, 0, 0, 0, 0xF8, 0, 1, 0},
    {2, 2, 0, 0, 0, 0xF8, 0, 1, 0}, {2, 0, 0, 0, 0, 0xF9, 0, 1, 0}, {2, 1, 0, 0, 0, 0xDB, 0, 0, 0},
    {2, 1, 0, 0, 0, 0xDC, 0, 0, 0}, {2, 1, 0, 0, 0, 0xDF, 0, 0, 0}, {2, 1, 0, 0, 0, 0x80, 0, 1, 0},
    {2, 1, 0, 0, 0, 0x82, 0, 1, 0},
    {3, 1, 0, 0, 0, 0x08, 1, 0, 0}, {3, 1, 0, 0, 0, 0x0A, 1, 0, 0}, {3, 1, 0, 0, 0, 0x0C, 1, 0, 0},
    {3, 1, 0, 0, 0, 0x0E, 1, 0, 0}, {3, 1, 0, 0, 0, 0x0F, 1, 0, 0}, {3, 0, 0, 0, 0, 0x0F, 1, 0, 0},
    {3, 1, 0, 0, 0, 0x14, 1, 0, 0}, {3, 1, 0, 0, 0, 0x15, 1, 0, 0}, {3, 1, 0, 0, 0, 0x16, 1, 0, 0},
    {3, 1, 0, 0, 0, 0x17, 1, 0, 0}, {3, 1, 0, 0, 0, 0x20, 1, 0, 0}, {3, 1, 0, 0, 0, 0x21, 1, 0, 0},
    {3, 1, 0, 0, 0, 0x22, 1, 0, 0}, {3, 1, 0, 0, 0, 0x40, 1, 0, 0}, {3, 1, 0, 0, 0, 0x42, 1, 0, 0},
    {3, 1, 0, 0, 0, 0x44, 1, 0, 0}, {3, 1, 0, 0, 0, 0x60, 1, 0, 0}, {3, 1, 0, 0, 0, 0x63, 1, 0, 0},
    {3, 0, 0, 0, 0, 0xCC, 1, 0, 0}, {3, 1, 0, 0, 0, 0xCE, 1, 0, 0}, {3, 1, 0, 0, 0, 0xDF, 1, 0, 0},
};

constexpr VecT kVex[] = {
    // map 1
    {1, 0, kAny, kAny, 0, 0x10, 0, 0, 0}, {1, 0, kAny, kAny, 0, 0x11, 0, 0, 0},
    {1, 0, kAny, kAny, 0, 0x28, 0, 0, 0}, {1, 0, kAny, kAny, 0, 0x29, 0, 0, 0},
    {1, 0, kAny, kAny, 0, 0x2B, 0, 1, 0}, {1, 0, kAny, 0, 0, 0x2E, 0, 0, 0},
    {1, 0, kAny, kAny, 1, 0x58, 0, 0, 0}, {1, 0, kAny, kAny, 1, 0x59, 0, 0, 0},
    {1, 0, kAny, kAny, 1, 0x5C, 0, 0, 0}, {1, 0, kAny, kAny, 1, 0x5E, 0, 0, 0},
    {1, 0, kAny, kAny, 1, 0x54, 0, 0, 0}, {1, 0, kAny, kAny, 1, 0x57, 0, 0, 0},
    {1, 0, kAny, kAny, 0, 0x51, 0, 0, 0}, {1, 0, kAny, kAny, 1, 0x14, 0, 0, 0},
    {1, 0, kAny, kAny, 1, 0xC6, 1, 0, 0}, {1, 0, kAny, kAny, 0, 0x5B, 0, 0, 0},
    {1, 1, kAny, kAny, 0, 0x10, 0, 0, 0}, {1, 1, kAny, kAny, 0, 0x11, 0, 0, 0},
    {1, 1, kAny, kAny, 0, 0x6F, 0, 0, 0}, {1, 1, kAny, kAny, 0, 0x7F, 0, 0, 0},
    {1, 1, kAny, kAny, 0, 0xE7, 0, 1, 0}, {1, 1, kAny, 0, 0, 0xD6, 0, 0, 0},
    {1, 1, 0, 0, 0, 0x6E, 0, 0, 0}, {1, 1, 1, 0, 0, 0x7E, 0, 0, 0},
    {1, 1, kAny, kAny, 1, 0xEF, 0, 0, 0}, {1, 1, kAny, kAny, 1, 0xDB, 0, 0, 0},
    {1, 1, kAny, kAny, 1, 0xFE, 0, 0, 0}, {1, 1, kAny, kAny, 1, 0xD4, 0, 0, 0},
    {1, 1, kAny, kAny, 0, 0x70, 1, 0, 0}, {1, 1, kAny, kAny, 1, 0x76, 0, 0, 0},
    {1, 1, kAny, kAny, 1, 0x58, 0, 0, 0}, {1, 1, kAny, kAny, 0, 0xE6, 0, 0, 0},
    {1, 2, kAny, kAny, 0, 0x10, 0, 1, 0}, {1, 2, kAny, kAny, 0, 0x11, 0, 1, 0},
    {1, 2, kAny, kAny, 0, 0x6F, 0, 0, 0}, {1, 2, kAny, kAny, 0, 0x7F, 0, 0, 0},
    {1, 2, kAny, kAny, 1, 0x58, 0, 0, 0}, {1, 2, kAny, kAny, 1, 0x59, 0, 0, 0},
    {1, 2, kAny, kAny, 1, 0x5E, 0, 0, 0}, {1, 2, kAny, kAny, 1, 0x2A, 0, 0, 0},
    {1, 2, kAny, kAny, 0, 0x2C, 0, 0, 0}, {1, 2, kAny, kAny, 1, 0x5A, 0, 0, 0},
    {1, 2, kAny, 0, 0, 0x7E, 0, 0, 0}, {1, 2, kAny, kAny, 0, 0x16, 0, 0, 0},
    {1, 3, kAny, kAny, 0, 0x10, 0, 1, 0}, {1, 3, kAny, kAny, 0, 0x11, 0, 1, 0},
    {1, 3, kAny, kAny, 1, 0x58, 0, 0, 0}, {1, 3, kAny, kAny, 1, 0x5C, 0, 0, 0},
    {1, 3, kAny, kAny, 0, 0x2D, 0, 0, 0}, {1, 3, kAny, kAny, 0, 0x12, 0, 0, 0},
    {1, 3, kAny, kAny, 0, 0xF0, 0, 1, 0}, {1, 3, kAny, kAny, 1, 0x7C, 0, 0, 0},
    // map 2
    {2, 1, kAny, kAny, 1, 0x00, 0, 0, 0}, {2, 1, kAny, kAny, 1, 0x0B, 0, 0, 0},
    {2, 1, 0, kAny, 0, 0x18, 0, 0, 0}, {2, 1, 0, 1, 0, 0x19, 0, 0, 0},
    {2, 1, 0, 1, 0, 0x1A, 0, 1, 0}, {2, 1, kAny, kAny, 0, 0x1C, 0, 0, 0},
    {2, 1, kAny, kAny, 0, 0x20, 0, 0, 0}, {2, 1, kAny, kAny, 0, 0x25, 0, 0, 0},
    {2, 1, kAny, kAny, 1, 0x28, 0, 0, 0}, {2, 1, kAny, kAny, 0, 0x2A, 0, 1, 0},
    {2, 1, 0, kAny, 1, 0x2C, 0, 1, 0}, {2, 1, 0, kAny, 1, 0x2E, 0, 1, 0},
    {2, 1, kAny, kAny, 0, 0x30, 0, 0, 0}, {2, 1, 0, 1, 1, 0x36, 0, 0, 0},
    {2, 1, kAny, kAny, 1, 0x39, 0, 0, 0}, {2, 1, kAny, kAny, 1, 0x40, 0, 0, 0},
    {2, 1, kAny, kAny, 1, 0x45, 0, 0, 0}, {2, 1, 0, kAny, 1, 0x46, 0, 0, 0},
    {2, 1, 0, kAny, 0, 0x58, 0, 0, 0}, {2, 1, 0, kAny, 0, 0x59, 0, 0, 0},
    {2, 1, 0, 1, 0, 0x5A, 0, 1, 0}, {2, 1, 0, kAny, 0, 0x78, 0, 0, 0},
    {2, 1, 0, kAny, 0, 0x79, 0, 0, 0}, {2, 1, kAny, kAny, 1, 0x8C, 0, 1, 0},
    {2, 1, kAny, kAny, 1, 0x8E, 0, 1, 0}, {2, 1, kAny, kAny, 1, 0x90, 0, 1, 1},
    {2, 1, kAny, kAny, 1, 0x92, 0, 1, 1}, {2, 1, kAny, kAny, 1, 0x96, 0, 0, 0},
    {2, 1, kAny, kAny, 1, 0x98, 0, 0, 0}, {2, 1, kAny, kAny, 1, 0xA8, 0, 0, 0},
    {2, 1, kAny, kAny, 1, 0xB8, 0, 0, 0}, {2, 1, kAny, kAny, 1, 0xBF, 0, 0, 0},
    {2, 1, kAny, 0, 0, 0xDB, 0, 0, 0}, {2, 1, kAny, kAny, 1, 0xDC, 0, 0, 0},
    {2, 0, kAny, 0, 1, 0xF2, 0, 0, 0}, {2, 0, kAny, 0, 1, 0xF5, 0, 0, 0},
    {2, 0, kAny, 0, 1, 0xF7, 0, 0, 0}, {2, 1, kAny, 0, 1, 0xF7, 0, 0, 0},
    {2, 2, kAny, 0, 1, 0xF5, 0, 0, 0}, {2, 2, kAny, 0, 1, 0xF7, 0, 0, 0},
    {2, 3, kAny, 0, 1, 0xF5, 0, 0, 0}, {2, 3, kAny, 0, 1, 0xF6, 0, 0, 0},
    {2, 3, kAny, 0, 1, 0xF7, 0, 0, 0},
    // map 3 (imm8)
    {3, 1, 1, 1, 0, 0x00, 1, 0, 0}, {3, 1, 0, kAny, 1, 0x02, 1, 0, 0},
    {3, 1, 0, kAny, 0, 0x04, 1, 0, 0}, {3, 1, 0, 1, 1, 0x06, 1, 0, 0},
    {3, 1, kAny, kAny, 0, 0x08, 1, 0, 0}, {3, 1, kAny, kAny, 1, 0x0C, 1, 0, 0},
    {3, 1, kAny, kAny, 1, 0x0F, 1, 0, 0}, {3, 1, kAny, 0, 0, 0x16, 1, 0, 0},
    {3, 1, kAny, 0, 0, 0x17, 1, 0, 0}, {3, 1, 0, 1, 1, 0x18, 1, 0, 0},
    {3, 1, 0, 1, 0, 0x19, 1, 0, 0}, {3, 1, 0, kAny, 0, 0x1D, 1, 0, 0},
    {3, 1, kAny, 0, 1, 0x20, 1, 0, 0}, {3, 1, kAny, 0, 1, 0x22, 1, 0, 0},
    {3, 1, 0, 1, 1, 0x38, 1, 0, 0}, {3, 1, 0, 1, 0, 0x39, 1, 0, 0},
    {3, 1, kAny, kAny, 1, 0x42, 1, 0, 0}, {3, 1, kAny, 0, 1, 0x44, 1, 0, 0},
    {3, 1, 0, 1, 1, 0x46, 1, 0, 0}, {3, 1, 0, kAny, 1, 0x4A, 1, 0, 0},
    {3, 1, kAny, 0, 0, 0x60, 1, 0, 0}, {3, 1, kAny, 0, 0, 0x63, 1, 0, 0},
    {3, 1, kAny, 0, 0, 0xDF, 1, 0, 0}, {3, 3, kAny, 0, 0, 0xF0, 1, 0, 0},
};

// EVEX (AVX-512 maps 1..3, FP16 maps 5 / 6). l: 0 = 128 only, 1 = 256/512
// only, kAny = 128/256/512.
constexpr VecT kEvex[] = {
    // map 1
    {1, 0, 0, kAny, 0, 0x10, 0, 0, 0}, {1, 0, 0, kAny, 0, 0x11, 0, 0, 0},
    {1, 0, 0, kAny, 0, 0x28, 0, 0, 0}, {1, 0, 0, kAny, 0, 0x29, 0, 0, 0},
    {1, 0, 0, kAny, 1, 0x58, 0, 0, 0}, {1, 0, 0, kAny, 1, 0x59, 0, 0, 0},
    {1, 0, 0, kAny, 1, 0x5C, 0, 0, 0}, {1, 0, 0, kAny, 1, 0x5E, 0, 0, 0},
    {1, 0, 0, kAny, 1, 0x54, 0, 0, 0}, {1, 0, 0, kAny, 1, 0x57, 0, 0, 0},
    {1, 0, 0, kAny, 0, 0x51, 0, 0, 0}, {1, 0, 0, kAny, 1, 0x14, 0, 0, 0},
    {1, 0, 0, kAny, 1, 0xC6, 1, 0, 0}, {1, 0, 0, kAny, 0, 0x5B, 0, 0, 0},
    {1, 1, 1, kAny, 0, 0x10, 0, 0, 0}, {1, 1, 1, kAny, 0, 0x11, 0, 0, 0},
    {1, 1, 1, kAny, 1, 0x58, 0, 0, 0}, {1, 1, 1, kAny, 1, 0x5E, 0, 0, 0},
    {1, 1, kAny, kAny, 0, 0x6F, 0, 0, 0}, {1, 1, kAny, kAny, 0, 0x7F, 0, 0, 0},
    {1, 1, kAny, kAny, 1, 0xEF, 0, 0, 0}, {1, 1, 0, kAny, 1, 0xFE, 0, 0, 0},
    {1, 1, 1, kAny, 1, 0xD4, 0, 0, 0}, {1, 1, 0, kAny, 0, 0xE7, 0, 1, 0},
    {1, 1, 1, 0, 0, 0xD6, 0, 0, 0}, {1, 1, kAny, 0, 0, 0x6E, 0, 0, 0},
    {1, 1, kAny, 0, 0, 0x7E, 0, 0, 0}, {1, 1, 0, kAny, 1, 0x76, 0, 0, 0},
    {1, 1, kAny, kAny, 1, 0x72, 1, 0, 0},
    {1, 2, kAny, kAny, 0, 0x6F, 0, 0, 0}, {1, 2, kAny, kAny, 0, 0x7F, 0, 0, 0},
    {1, 2, 0, kAny, 0, 0x10, 0, 1, 0}, {1, 2, 0, kAny, 0, 0x11, 0, 1, 0},
    {1, 2, 0, kAny, 1, 0x58, 0, 0, 0}, {1, 2, 0, kAny, 1, 0x5A, 0, 0, 0},
    {1, 2, 0, kAny, 0, 0xE6, 0, 0, 0}, {1, 2, 1, 0, 0, 0x7E, 0, 0, 0},
    {1, 3, kAny, kAny, 0, 0x6F, 0, 0, 0}, {1, 3, kAny, kAny, 0, 0x7F, 0, 0, 0},
    {1, 3, 1, kAny, 0, 0x10, 0, 1, 0}, {1, 3, 1, kAny, 0, 0x11, 0, 1, 0},
    {1, 3, 1, kAny, 1, 0x59, 0, 0, 0}, {1, 3, 1, kAny, 0, 0x12, 0, 0, 0},
    // map 2
    {2, 1, kAny, kAny, 1, 0x00, 0, 0, 0}, {2, 1, 0, kAny, 0, 0x18, 0, 0, 0},
    {2, 1, kAny, 1, 0, 0x19, 0, 0, 0}, {2, 1, kAny, 1, 0, 0x1A, 0, 1, 0},
    {2, 1, kAny, 1, 0, 0x1B, 0, 1, 0}, {2, 1, kAny, kAny, 0, 0x1C, 0, 0, 0},
    {2, 1, kAny, kAny, 0, 0x1F, 0, 0, 0}, {2, 1, 0, kAny, 0, 0x21, 0, 0, 0},
    {2, 1, kAny, kAny, 1, 0x2C, 0, 0, 0}, {2, 1, kAny, 1, 1, 0x36, 0, 0, 0},
    {2, 1, kAny, kAny, 1, 0x40, 0, 0, 0}, {2, 1, kAny, kAny, 1, 0x45, 0, 0, 0},
    {2, 1, 0, kAny, 0, 0x58, 0, 0, 0}, {2, 1, kAny, kAny, 0, 0x59, 0, 0, 0},
    {2, 1, kAny, kAny, 1, 0x64, 0, 0, 0}, {2, 1, kAny, kAny, 1, 0x76, 0, 0, 0},
    {2, 1, kAny, kAny, 0, 0x88, 0, 0, 0}, {2, 1, kAny, kAny, 0, 0x8A, 0, 1, 0},
    {2, 1, kAny, kAny, 1, 0x90, 0, 1, 1}, {2, 1, kAny, kAny, 1, 0x92, 0, 1, 1},
    {2, 1, kAny, kAny, 0, 0xA0, 0, 1, 1}, {2, 1, kAny, kAny, 0, 0xA2, 0, 1, 1},
    {2, 1, kAny, kAny, 1, 0x98, 0, 0, 0}, {2, 1, kAny, kAny, 1, 0xA8, 0, 0, 0},
    {2, 1, kAny, kAny, 1, 0xB8, 0, 0, 0}, {2, 1, kAny, kAny, 0, 0xC4, 0, 0, 0},
    // map 3 (imm8)
    {3, 1, 1, 1, 0, 0x00, 1, 0, 0}, {3, 1, kAny, kAny, 1, 0x03, 1, 0, 0},
    {3, 1, 0, kAny, 0, 0x04, 1, 0, 0}, {3, 1, 0, kAny, 0, 0x08, 1, 0, 0},
    {3, 1, kAny, 1, 1, 0x18, 1, 0, 0}, {3, 1, kAny, 1, 0, 0x19, 1, 0, 0},
    {3, 1, kAny, 1, 0, 0x1B, 1, 0, 0}, {3, 1, kAny, kAny, 1, 0x1E, 1, 0, 0},
    {3, 1, kAny, 1, 1, 0x23, 1, 0, 0}, {3, 1, kAny, kAny, 1, 0x25, 1, 0, 0},
    {3, 1, kAny, kAny, 0, 0x26, 1, 0, 0}, {3, 1, kAny, 1, 1, 0x38, 1, 0, 0},
    {3, 1, kAny, 1, 0, 0x39, 1, 0, 0}, {3, 1, kAny, 1, 0, 0x3B, 1, 0, 0},
    {3, 1, kAny, kAny, 1, 0x3E, 1, 0, 0}, {3, 1, kAny, kAny, 1, 0x50, 1, 0, 0},
    {3, 1, kAny, kAny, 1, 0x54, 1, 0, 0}, {3, 1, kAny, kAny, 0, 0x56, 1, 0, 0},
    {3, 1, kAny, kAny, 0, 0x66, 1, 0, 0},
    // map 5 / 6 (FP16)
    {5, 0, 0, kAny, 1, 0x58, 0, 0, 0}, {5, 0, 0, kAny, 1, 0x59, 0, 0, 0},
    {5, 0, 0, kAny, 1, 0x5C, 0, 0, 0}, {5, 0, 0, kAny, 0, 0x51, 0, 0, 0},
    {5, 2, 0, kAny, 0, 0x10, 0, 1, 0}, {5, 2, 0, kAny, 0, 0x11, 0, 1, 0},
    {5, 2, 0, kAny, 1, 0x58, 0, 0, 0}, {5, 1, 0, 0, 0, 0x6E, 0, 0, 0},
    {6, 1, 0, kAny, 1, 0x2C, 0, 0, 0}, {6, 1, 0, kAny, 0, 0x42, 0, 0, 0},
    {6, 1, 0, kAny, 0, 0x4C, 0, 0, 0}, {6, 1, 0, kAny, 1, 0x98, 0, 0, 0},
    {6, 1, 0, kAny, 1, 0xA8, 0, 0, 0}, {6, 1, 0, kAny, 1, 0xB8, 0, 0, 0},
};

// EVEX map 4 (APX promoted legacy). nd: may take a new data destination
// (vvvv); nf: may set NF; pp as in VecT.
struct ApxT {
    std::uint8_t op, imm, nd, nf, pp;
};
constexpr ApxT kApx[] = {
    {0x00, I0, 1, 1, 0}, {0x01, I0, 1, 1, 0}, {0x02, I0, 1, 1, 0}, {0x03, I0, 1, 1, 0},
    {0x08, I0, 1, 1, 0}, {0x09, I0, 1, 1, 0}, {0x0B, I0, 1, 1, 0}, {0x10, I0, 1, 0, 0},
    {0x11, I0, 1, 0, 0}, {0x13, I0, 1, 0, 0}, {0x18, I0, 1, 0, 0}, {0x19, I0, 1, 0, 0},
    {0x20, I0, 1, 1, 0}, {0x21, I0, 1, 1, 0}, {0x23, I0, 1, 1, 0}, {0x28, I0, 1, 1, 0},
    {0x29, I0, 1, 1, 0}, {0x2B, I0, 1, 1, 0}, {0x30, I0, 1, 1, 0}, {0x31, I0, 1, 1, 0},
    {0x33, I0, 1, 1, 0}, {0x24, I8, 1, 1, 0}, {0x2C, I8, 1, 1, 0}, {0xA5, I0, 1, 1, 0},
    {0xAD, I0, 1, 1, 0}, {0x40, I0, 1, 0, 0}, {0x44, I0, 1, 0, 0}, {0x4F, I0, 1, 0, 0},
    {0x60, I0, 0, 0, 0}, {0x61, I0, 0, 0, 0}, {0x66, I0, 1, 0, 1}, {0x66, I0, 1, 0, 2},
    {0x69, IZ, 0, 1, 0}, {0x6B, I8, 0, 1, 0}, {0x80, I8, 1, 1, 0}, {0x81, IZ, 1, 1, 0},
    {0x83, I8, 1, 1, 0}, {0xAF, I0, 1, 1, 0}, {0xC0, I8, 1, 1, 0}, {0xC1, I8, 1, 1, 0},
    {0xD0, I0, 1, 1, 0}, {0xD1, I0, 1, 1, 0}, {0xD3, I0, 1, 1, 0}, {0xF0, I0, 0, 0, 0},
    {0xF1, I0, 0, 0, 0}, {0xF6, I0, 1, 1, 0}, {0xF7, I0, 1, 1, 0}, {0xF8, I0, 0, 0, 1},
    {0xF8, I0, 0, 0, 3}, {0xF9, I0, 0, 0, 0}, {0xFE, I0, 1, 1, 0}, {0xFF, I0, 1, 1, 0},
    {0x38, I0, 0, 0, 0}, {0x39, I0, 0, 0, 0}, {0x84, I0, 0, 0, 0}, {0x85, I0, 0, 0, 0},
};

void PushImm(SplitMix64& rng, std::vector<std::uint8_t>& v, std::uint8_t imm, bool opsize16) {
    if (imm == I8) PushRandom(rng, v, 1);
    else if (imm == IZ) PushRandom(rng, v, opsize16 ? 2 : 4);
}

std::uint8_t PickBit(SplitMix64& rng, unsigned pctOne) { return rng.Pct(pctOne) ? 1 : 0; }

void PushPp(std::vector<std::uint8_t>& v, std::uint8_t pp) {
    static constexpr std::uint8_t kPfx[] = {0, 0x66, 0xF3, 0xF2};
    if (pp) v.push_back(kPfx[pp]);
}

// Legacy map 0 / 0F with optional prefixes, REX and LOCK.
void GenLegacy(SplitMix64& rng, std::vector<std::uint8_t>& v) {
    const bool map1 = rng.Pct(32);
    const OpDef d = map1 ? rng.Pick(kMap1) : rng.Pick(kMap0);
    // LOCK only on lockable opcodes with a memory form (plus a rare stray one).
    bool lock = rng.Pct(6) && (map1 ? InSet(kLockMap1, d.op) : InSet(kLockMap0, d.op));
    if (rng.Below(1000) < 3) lock = true;
    bool opsize16 = false;
    if (rng.Pct(15)) { v.push_back(0x66); opsize16 = true; }
    if (rng.Pct(6)) v.push_back(0xF3);
    else if (rng.Pct(5)) v.push_back(0xF2);
    if (lock) v.push_back(0xF0);
    if (rng.Pct(4)) v.push_back(rng.Pct(50) ? 0x64 : (rng.Pct(50) ? 0x65 : 0x2E));
    if (rng.Pct(3)) v.push_back(0x67);
    if (rng.Pct(55)) v.push_back(static_cast<std::uint8_t>(0x40 | rng.Below(16)));
    if (map1) v.push_back(0x0F);
    v.push_back(d.op);
    std::uint8_t imm = d.imm;
    auto reg = static_cast<std::uint8_t>(rng.Below(8));
    if (!map1) {
        // Group opcodes whose validity / immediate depends on ModRM.reg.
        if ((d.op == 0xF6 || d.op == 0xF7) && rng.Pct(40)) reg = 0;
        if ((d.op == 0xC6 || d.op == 0xC7) && rng.Pct(90)) reg = 0;
        if (d.op == 0x8F && rng.Pct(90)) reg = 0;
        if (d.op == 0xFE && rng.Pct(90)) reg &= 1;
        if (d.op == 0xF6 || d.op == 0xF7) imm = reg <= 1 ? (d.op == 0xF6 ? I8 : IZ) : I0;
    }
    PushModrm(rng, v, reg, lock && rng.Pct(97));
    PushImm(rng, v, imm, opsize16);
}

void GenSse38(SplitMix64& rng, std::vector<std::uint8_t>& v) {
    const VecT t = rng.Pick(kSse38);
    PushPp(v, t.pp);
    if (rng.Pct(3)) v.push_back(0x67);
    if (rng.Pct(50)) v.push_back(static_cast<std::uint8_t>(0x40 | rng.Below(16)));
    v.push_back(0x0F);
    v.push_back(t.map == 2 ? 0x38 : 0x3A);
    v.push_back(t.op);
    PushModrm(rng, v, static_cast<std::uint8_t>(rng.Below(8)), t.mem != 0);
    if (t.imm) PushRandom(rng, v, 1);
}

std::uint8_t TemplW(SplitMix64& rng, std::uint8_t w) { return w == kAny ? PickBit(rng, 50) : w; }

void GenVex(SplitMix64& rng, std::vector<std::uint8_t>& v) {
    const VecT t = rng.Pick(kVex);
    const std::uint8_t w = TemplW(rng, t.w);
    const std::uint8_t l = t.l == kAny ? PickBit(rng, 50) : t.l;
    const std::uint8_t vvvv = t.nds ? static_cast<std::uint8_t>(rng.Below(16)) : 0x0F;
    const std::uint8_t rbar = PickBit(rng, 80), xbar = PickBit(rng, 85), bbar = PickBit(rng, 80);
    if (rng.Pct(3)) v.push_back(0x67);
    if (t.map == 1 && w == 0 && xbar && bbar && rng.Pct(50)) {
        v.push_back(0xC5);
        v.push_back(static_cast<std::uint8_t>((rbar << 7) | (vvvv << 3) | (l << 2) | t.pp));
    } else {
        v.push_back(0xC4);
        v.push_back(static_cast<std::uint8_t>((rbar << 7) | (xbar << 6) | (bbar << 5) | t.map));
        v.push_back(static_cast<std::uint8_t>((w << 7) | (vvvv << 3) | (l << 2) | t.pp));
    }
    v.push_back(t.op);
    PushModrm(rng, v, static_cast<std::uint8_t>(rng.Below(8)), t.mem != 0, t.vsib != 0);
    if (t.imm) PushRandom(rng, v, 1);
}

void GenEvex(SplitMix64& rng, std::vector<std::uint8_t>& v) {
    const VecT t = rng.Pick(kEvex);
    const std::uint8_t w = TemplW(rng, t.w);
    std::uint8_t ll;
    if (t.l == 0) ll = 0;
    else if (t.l == 1) ll = static_cast<std::uint8_t>(1 + rng.Below(2));
    else ll = static_cast<std::uint8_t>(rng.Below(3));
    const std::uint8_t vvvv = t.nds ? static_cast<std::uint8_t>(rng.Below(16)) : 0x0F;
    const std::uint8_t vbar2 = t.nds ? PickBit(rng, 70) : 1;
    std::uint8_t aaa = rng.Pct(30) ? static_cast<std::uint8_t>(1 + rng.Below(7)) : 0;
    if (t.vsib) aaa = static_cast<std::uint8_t>(1 + rng.Below(7));
    const std::uint8_t z = (aaa && !t.mem && rng.Pct(30)) ? 1 : 0;
    const std::uint8_t b = rng.Pct(10) ? 1 : 0;
    std::uint8_t p0 = static_cast<std::uint8_t>((PickBit(rng, 80) << 7) | (PickBit(rng, 85) << 6) |
                                                (PickBit(rng, 80) << 5) | (PickBit(rng, 85) << 4) | t.map);
    if (rng.Pct(4)) p0 |= 0x08;  // B4 on an AVX-512 instruction (APX) / reserved
    std::uint8_t p1 = static_cast<std::uint8_t>((w << 7) | (vvvv << 3) | 0x04 | t.pp);
    if (rng.Pct(3)) p1 &= static_cast<std::uint8_t>(~0x04);  // X4 / reserved bit
    const auto p2 = static_cast<std::uint8_t>((z << 7) | (ll << 5) | (b << 4) | (vbar2 << 3) | aaa);
    if (rng.Pct(3)) v.push_back(0x67);
    v.push_back(0x62);
    v.push_back(p0);
    v.push_back(p1);
    v.push_back(p2);
    v.push_back(t.op);
    PushModrm(rng, v, static_cast<std::uint8_t>(rng.Below(8)), t.mem != 0, t.vsib != 0);
    if (t.imm) PushRandom(rng, v, 1);
}

void GenApx(SplitMix64& rng, std::vector<std::uint8_t>& v) {
    const ApxT t = rng.Pick(kApx);
    const std::uint8_t nd = t.nd && rng.Pct(40) ? 1 : 0;
    const std::uint8_t nf = t.nf && rng.Pct(30) ? 1 : 0;
    std::uint8_t pp = t.pp;
    if (pp == 0 && t.op != 0xF8 && rng.Pct(15)) pp = 1;  // 16-bit operand size
    const std::uint8_t w = PickBit(rng, 50);
    const std::uint8_t vvvv = nd ? static_cast<std::uint8_t>(rng.Below(16)) : 0x0F;
    // P0 = R3' X3' B3' R4' B4 mmm(100); P1 = W vvvv' U pp; P2 = 000 ND V4' NF 00.
    std::uint8_t p0 = static_cast<std::uint8_t>((PickBit(rng, 80) << 7) | (PickBit(rng, 85) << 6) |
                                                (PickBit(rng, 80) << 5) | (PickBit(rng, 80) << 4) | 0x04);
    if (rng.Pct(20)) p0 |= 0x08;
    const auto p1 = static_cast<std::uint8_t>((w << 7) | (vvvv << 3) | (PickBit(rng, 85) << 2) | pp);
    const auto p2 = static_cast<std::uint8_t>((nd << 4) | (PickBit(rng, 85) << 3) | (nf << 2));
    v.push_back(0x62);
    v.push_back(p0);
    v.push_back(p1);
    v.push_back(p2);
    v.push_back(t.op);
    auto reg = static_cast<std::uint8_t>(rng.Below(8));
    std::uint8_t imm = t.imm;
    if (t.op == 0xF6 || t.op == 0xF7) {
        if (rng.Pct(40)) reg = 0;
        imm = reg <= 1 ? (t.op == 0xF6 ? I8 : IZ) : I0;
    }
    if (t.op == 0xFE || t.op == 0xFF) reg &= 1;
    PushModrm(rng, v, reg, t.op == 0xF8 || t.op == 0xF9 || t.op == 0x60 || t.op == 0x61);
    PushImm(rng, v, imm, pp == 1);
}

// Appends one generated instruction (or a deliberate non-instruction).
void GenInsn(SplitMix64& rng, std::vector<std::uint8_t>& v) {
    const std::uint64_t k = rng.Below(1000);
    if (k < 400) {
        GenLegacy(rng, v);
    } else if (k < 460) {
        GenSse38(rng, v);
    } else if (k < 580) {
        // Common instructions without a ModRM byte.
        switch (rng.Below(16)) {
            case 0:  if (rng.Pct(40)) v.push_back(0x41); v.push_back(static_cast<std::uint8_t>(0x50 + rng.Below(16))); break;
            case 1:  v.push_back(0xC3); break;
            case 2:  v.push_back(0xE8); PushRandom(rng, v, 4); break;
            case 3:  v.push_back(0xE9); PushRandom(rng, v, 4); break;
            case 4:  v.push_back(static_cast<std::uint8_t>(0x70 + rng.Below(16))); PushRandom(rng, v, 1); break;
            case 5:  v.push_back(0x0F); v.push_back(static_cast<std::uint8_t>(0x80 + rng.Below(16))); PushRandom(rng, v, 4); break;
            case 6:  v.push_back(static_cast<std::uint8_t>(0xB8 + rng.Below(8))); PushRandom(rng, v, 4); break;
            case 7:  v.push_back(0x48); v.push_back(static_cast<std::uint8_t>(0xB8 + rng.Below(8))); PushRandom(rng, v, 8); break;
            case 8:  v.push_back(0x90); break;
            case 9:  v.insert(v.end(), {0x0F, 0x1F, 0x44, 0x00, 0x00}); break;
            case 10: v.insert(v.end(), {0xF3, 0x0F, 0x1E, 0xFA}); break;
            case 11: v.push_back(0x48); v.push_back(rng.Pct(50) ? 0x98 : 0x99); break;
            case 12: if (rng.Pct(50)) v.push_back(0x48); v.push_back(static_cast<std::uint8_t>(0xA0 + rng.Below(4))); PushRandom(rng, v, 8); break;
            case 13: if (rng.Pct(50)) v.push_back(0xF3); if (rng.Pct(50)) v.push_back(0x48); v.push_back(static_cast<std::uint8_t>(0xA4 + rng.Below(4) + (rng.Pct(50) ? 6 : 0))); break;
            case 14: v.push_back(0xEB); PushRandom(rng, v, 1); break;
            default: v.push_back(0x48); v.push_back(0x83); v.push_back(0xEC); PushRandom(rng, v, 1); break;
        }
    } else if (k < 680) {
        GenVex(rng, v);
    } else if (k < 780) {
        GenEvex(rng, v);
    } else if (k < 840) {
        GenApx(rng, v);
    } else if (k < 900) {
        // REX2 (APX): D5 payload, then a map 0 or map 1 opcode (no 0F escape).
        const std::uint8_t payload = rng.Byte();
        v.push_back(0xD5);
        v.push_back(payload);
        const OpDef d = (payload & 0x80) ? rng.Pick(kMap1) : rng.Pick(kMap0);
        v.push_back(d.op);
        PushModrm(rng, v, static_cast<std::uint8_t>(rng.Below(8)), false);
        PushImm(rng, v, d.imm, false);
    } else if (k < 985) {
        // Memory-operand heavy: mov / lea / store / ALU with a pool displacement.
        if (rng.Pct(60)) v.push_back(static_cast<std::uint8_t>(0x48 | rng.Below(8)));
        static constexpr std::uint8_t kOps[] = {0x8B, 0x89, 0x8D, 0x3B, 0x39, 0x85, 0x01, 0x03, 0x87, 0x88, 0x8A};
        v.push_back(rng.Pick(kOps));
        PushModrm(rng, v, static_cast<std::uint8_t>(rng.Below(8)), true);
    } else if (k < 991) {
        PushRandom(rng, v, 1 + rng.Below(4));  // junk
    } else if (k < 995) {
        // Raw VEX / EVEX / REX2 / XOP escape with a random payload (mostly invalid).
        static constexpr std::uint8_t kEsc[] = {0xC4, 0xC5, 0x62, 0xD5, 0x8F};
        v.push_back(rng.Pick(kEsc));
        PushRandom(rng, v, 3);
        PushModrm(rng, v, static_cast<std::uint8_t>(rng.Below(8)), false);
    } else {
        v.push_back(0xCC);
    }
}

// Hand-written edge cases (hex), one range each. Comments name the intent;
// what the kernel reports is whatever the legacy (Zydis) decoder said.
constexpr std::string_view kInstEdges[] = {
    "488B4010",               // mov rax,[rax+0x10]          read
    "48894810",               // mov [rax+0x10],rcx          write
    "488D4810",               // lea rcx,[rax+0x10]          LEA -> read
    "0F1F4000",               // nop dword [rax+0]           action?
    "0F1F8010000000",         // nop dword [rax+0x10] disp32
    "0F184810",               // prefetcht0 [rax+0x10]
    "0F0D4810",               // prefetchw [rax+0x10]
    "0FAE7810",               // clflush
    "660FAE7810",             // clflushopt
    "660FAE7010",             // clwb
    "0F1C4010",               // cldemote
    "488B0424",               // mov rax,[rsp]               no disp
    "488B442410",             // [rsp+0x10]                  excluded
    "488B4510",               // [rbp+0x10]                  excluded
    "498B4510",               // [r13+0x10]
    "498B442410",             // [r12+0x10]
    "498B4500",               // [r13+0]                     explicit disp8 0
    "678B4010",               // [eax+0x10]
    "678B4510",               // [ebp+0x10]
    "678B442410",             // [esp+0x10]
    "678B0510000000",         // [eip+0x10]
    "488B0510000000",         // [rip+0x10]
    "488B042510000000",       // [0x10]                      no base
    "65488B042558000000",     // gs:[0x58]
    "64488B4010",             // fs:[rax+0x10]
    "488B440810",             // [rax+rcx+0x10]              index
    "488B44E010",             // [rax+riz*8+0x10]? (index=100 -> none)
    "48874810",               // xchg
    "F0480FB14810",           // lock cmpxchg
    "480F444810",             // cmovz                       cond read
    "0F944010",               // sete                        write
    "48394810",               // cmp                         read
    "48854810",               // test                        read
    "FF7010",                 // push [rax+0x10]
    "8F4010",                 // pop [rax+0x10]
    "FF5010",                 // call [rax+0x10]
    "FF6010",                 // jmp [rax+0x10]
    "48FF4010",               // inc
    "C5F8114010",             // vmovups [rax+0x10],xmm0     VEX disp8 unscaled
    "C5FC104010",             // vmovups ymm0,[rax+0x10]
    "62F17C48114001",         // vmovups [rax+0x40],zmm0     EVEX disp8*64
    "62F17C49114001",         // ... {k1}                    cond write
    "62F17C48104001",         // vmovups zmm0,[rax+0x40]
    "62F27D48184001",         // vbroadcastss zmm0,[rax+4]   disp8*4
    "62F1FD48D64001",         // vmovq? (w1, d6)
    "C4E2799004C8",           // vpgatherdd                  VSIB
    "62F27D49A04C0801",       // vpscatterdd                 VSIB
    "C4E2792E4010",           // vmaskmovps store            cond write
    "0F017810",               // invlpg
    "0FAE4010",               // fxsave                      write
    "0FAE4810",               // fxrstor                     read
    "0FAE5010",               // ldmxcsr
    "0FAE5810",               // stmxcsr
    "0FAE6010",               // xsave
    "0FAE6810",               // xrstor
    "0F014010",               // sgdt
    "0F015010",               // lgdt
    "D94010",                 // fld dword [rax+0x10]
    "D95810",                 // fstp
    "DD7010",                 // fnsave
    "0FC74810",               // cmpxchg8b
    "480FC74810",             // cmpxchg16b
    "F30FB84010",             // popcnt
    "F20F38F14010",           // crc32
    "0F38F04010",             // movbe load
    "0F38F14010",             // movbe store
    "660F38F84010",           // movdir64b
    "F20F38F84010",           // enqcmd
    "0F1A4010",               // bndldx (MIB)
    "0F1B4010",               // bndstx (MIB)
    "F30F1B4010",             // bndmk                       agen
    "660F1A4010",             // bndmov
    "660FE74010",             // movntdq
    "6B401005",               // imul eax,[rax+0x10],5
    "0FA4401003",             // shld [rax+0x10],eax,3
    "D5188B4010",             // REX2 mov rax,[r16+0x10]
    "D5198B4010",             // REX2 [r17+0x10]
    "D5108B442410",           // REX2 SIB base
    "D5808B4010",             // REX2 M0=1 -> map 1 op 8B
    "62F47C08014010",         // APX EVEX map4 add [rax+0x10],eax
    "62F47C18014010",         // ... ND
    "62F47C0C014010",         // ... NF
    "62D47C08014010",         // ... B3
    "62FC7C08014010",         // ... B4?
    "62F4FC088B4010",         // map4 8B
    "62F47C088F4010",         // map4 8F
    "62F47C08884010",         // map4 88
    "62F47C08F74010",         // map4 F7 /0? (no imm)
    "62F47C08FF7010",         // map4 FF /6
    "48A11000000000000000",   // mov rax,moffs64             no base
    "48A31000000000000000",   // mov moffs64,rax
    "488B4000",               // disp8 0
    "488B00",                 // no disp
    "488B8000000000",         // disp32 0
    "488B40F8",               // -8
    "488B8000000080",         // -0x80000000
    "67488B8000000080",       // [eax-0x80000000]
    "488B80FFFFFF7F",         // 0x7FFFFFFF
    "48C7401001000000",       // mov qword [rax+0x10],1
    "C6401001",               // mov byte [rax+0x10],1
    "F6401001",               // test byte [rax+0x10],1
    "F6501000",               // not byte [rax+0x10]
    "66C740100100",           // mov word [rax+0x10],1
    "F3A4",                   // rep movsb                   hidden operands
    "D7",                     // xlat
    "0F01C8",                 // monitor
    "488B4010488B4810",       // two hits, two instructions
    "48894010CC48894010",     // second after 0xCC is never decoded
    "0F0B488B4010",           // ud2, then a hit
    "FFFF488B4010",           // decode failure first
};

// Field offsets inst-synthetic asks for.
constexpr std::size_t kSynFo[] = {
    0x0, 0x1, 0x4, 0x8, 0x10, 0x18, 0x20, 0x40, 0x58, 0x7F, 0x80, 0xB8, 0x100, 0x1000, 0x10001,
    0x7FFFFFFF, 0x80000000u, 0xFFFFFFFFu,
    static_cast<std::size_t>(-8), static_cast<std::size_t>(-0x40), static_cast<std::size_t>(-0x80),
    static_cast<std::size_t>(-0x80000000ll), SIZE_MAX};

void EmitInstHits(std::string_view run, std::size_t fo, const std::vector<XS::InstanceHit>& hits,
                  std::uintptr_t base) {
    std::size_t writes = 0;
    for (const auto& h : hits) writes += h.write ? 1 : 0;
    Line("inst").S("run", run).Hex("fo", fo).U("n", hits.size()).U("w", writes).Emit();
    // Consecutive hits of one method share a line; a kernel that broke the
    // method-by-method order would split groups and change the output.
    std::size_t i = 0;
    while (i < hits.size()) {
        const std::size_t m = hits[i].method;
        std::string h = "[";
        bool first = true;
        for (; i < hits.size() && hits[i].method == m; ++i) {
            if (!first) h += ',';
            first = false;
            h += '[';
            AppendHexNum(h, static_cast<std::uint64_t>(hits[i].ip - base));
            h += hits[i].write ? ",1]" : ",0]";
        }
        h += ']';
        Line("ih").S("run", run).Hex("fo", fo).U("m", m).Raw("h", h).Emit();
    }
}

bool ParseHexBytes(std::string_view s, std::vector<std::uint8_t>& out) {
    auto nib = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        return -1;
    };
    if (s.size() % 2) return false;
    for (std::size_t i = 0; i < s.size(); i += 2) {
        const int a = nib(s[i]), b = nib(s[i + 1]);
        if (a < 0 || b < 0) return false;
        out.push_back(static_cast<std::uint8_t>((a << 4) | b));
    }
    return true;
}

// Region layout for inst-synthetic.
constexpr std::size_t kInstRegion = 10 * kMiB;
constexpr std::size_t kInstStream = 0, kInstStreamLen = 8 * kMiB;   // committed, generated
constexpr std::size_t kInstEdgeArea = 9 * kMiB, kInstEdgeLen = 0x10000;  // committed
// Unreadable pages inside the stream: NOACCESS x3, GUARD x1, and a
// decommitted page (reserved, not committed).
constexpr std::size_t kInstNoAccess[] = {0x100000, 0x333000, 0x5FF000};
constexpr std::size_t kInstGuard = 0x480000;
constexpr std::size_t kInstDecommit = 0x6A0000;
// The stream ends at 8 MiB; [8 MiB, 9 MiB) is reserved only.

int CmdInstSynthetic() {
    Region rg(kInstRegion);
    if (!rg.Ok()) return SetupFailed("VirtualAlloc reserve");
    if (!rg.Commit(kInstStream, kInstStreamLen) || !rg.Commit(kInstEdgeArea, kInstEdgeLen)) {
        return SetupFailed("VirtualAlloc commit");
    }
    std::uint8_t* r = rg.Base();
    const auto base = reinterpret_cast<std::uintptr_t>(r);
    SplitMix64 rng(0x1A57A9CE5D15C0DEull);

    // --- generated stream, instruction starts recorded.
    std::vector<std::uint8_t> stream;
    stream.reserve(kInstStreamLen + 64);
    std::vector<std::uint32_t> starts;
    starts.reserve(kInstStreamLen / 3);
    while (stream.size() < kInstStreamLen) {
        starts.push_back(static_cast<std::uint32_t>(stream.size()));
        GenInsn(rng, stream);
    }
    stream.resize(kInstStreamLen);
    while (!starts.empty() && starts.back() >= kInstStreamLen) starts.pop_back();
    std::memcpy(r + kInstStream, stream.data(), kInstStreamLen);

    // --- edge table, packed back to back (ranges cover exactly one entry).
    struct Edge { std::size_t rel, len; };
    std::vector<Edge> edges;
    {
        std::size_t at = kInstEdgeArea;
        for (const std::string_view hex : kInstEdges) {
            std::vector<std::uint8_t> b;
            if (!ParseHexBytes(hex, b)) return SetupFailed("bad edge hex");
            std::memcpy(r + at, b.data(), b.size());
            edges.push_back({at, b.size()});
            at += b.size();
        }
    }

    // --- unreadable pages.
    for (const std::size_t p : kInstNoAccess) {
        if (!rg.Protect(p, 0x1000, PAGE_NOACCESS)) return SetupFailed("VirtualProtect");
    }
    if (!rg.Protect(kInstGuard, 0x1000, PAGE_READWRITE | PAGE_GUARD)) return SetupFailed("VirtualProtect");
    if (!::VirtualFree(r + kInstDecommit, 0x1000, MEM_DECOMMIT)) return SetupFailed("VirtualFree decommit");
    if (!rg.Protect(kInstEdgeArea, kInstEdgeLen, PAGE_EXECUTE_READ)) return SetupFailed("VirtualProtect");

    // --- ranges.
    std::vector<XS::MethodRange> ranges;
    std::vector<std::size_t> unread = {kInstNoAccess[0], kInstNoAccess[1], kInstNoAccess[2], kInstGuard,
                                       kInstDecommit};
    auto pickLen = [&rng]() -> std::size_t {
        switch (rng.Below(6)) {
            case 0:  return 1 + rng.Below(16);
            case 1:  return 1 + rng.Below(256);
            case 2:  return 1 + rng.Below(2048);
            case 3:  return 8192;
            default: return 1 + rng.Below(8192);
        }
    };
    constexpr std::size_t kRanges = 24000;
    for (std::size_t i = 0; i < kRanges; ++i) {
        std::size_t start;
        std::size_t len = pickLen();
        const std::uint64_t k = rng.Below(100);
        if (k < 70) {
            start = kInstStream + starts[rng.Below(starts.size())];
        } else if (k < 88) {
            start = kInstStream + rng.Below(kInstStreamLen);
        } else {
            // Around an unreadable page: end exactly at it, cross it by one
            // byte, start inside it, start right after it, or straddle it with
            // a 0xCC / decode stop well before the bad page.
            const std::size_t p = unread[rng.Below(unread.size())];
            switch (rng.Below(5)) {
                case 0:  len = 1 + rng.Below(4096); start = p - len; break;
                case 1:  len = 2 + rng.Below(4096); start = p - len + 1; break;
                case 2:  start = p + rng.Below(4096); break;
                case 3:  start = p + 0x1000; break;
                default: start = p - 64 - rng.Below(256); len = 4096 + rng.Below(4096); break;
            }
        }
        // Keep inside the stream (the stream end is followed by reserved memory,
        // so ranges ending exactly there are fine, crossing it is unreadable).
        if (start >= kInstStreamLen) start = kInstStreamLen - 1;
        if (rng.Pct(2)) {
            // Run into the reserved tail.
            start = kInstStreamLen - 1 - rng.Below(64);
            len = 1 + rng.Below(256);
        } else if (start + len > kInstStreamLen) {
            len = kInstStreamLen - start;
        }
        ranges.push_back({rg.Addr(start), len});
    }

    // Edge ranges: each entry exactly, and each entry one byte short.
    std::vector<XS::MethodRange> edgeRanges, edgeShort;
    for (const Edge& e : edges) {
        edgeRanges.push_back({rg.Addr(e.rel), e.len});
        if (e.len > 1) edgeShort.push_back({rg.Addr(e.rel), e.len - 1});
    }

    Line("ilayout").Hex("region", kInstRegion).U("streamLen", kInstStreamLen).U("starts", starts.size())
        .U("ranges", ranges.size()).U("edges", edges.size()).Emit();

    for (const std::size_t fo : kSynFo) {
        EmitInstHits("edges", fo, XS::ScanInstanceAccesses(edgeRanges, fo), base);
        EmitInstHits("edges-short", fo, XS::ScanInstanceAccesses(edgeShort, fo), base);
    }
    for (const std::size_t fo : kSynFo) {
        EmitInstHits("stream", fo, XS::ScanInstanceAccesses(ranges, fo), base);
    }
    // Order / duplicates must only permute / repeat the output.
    {
        std::vector<XS::MethodRange> rev(ranges.rbegin(), ranges.rbegin() + 2000);
        rev.insert(rev.end(), ranges.begin(), ranges.begin() + 500);
        rev.insert(rev.end(), ranges.begin(), ranges.begin() + 500);
        EmitInstHits("rev-dup", 0x10, XS::ScanInstanceAccesses(rev, 0x10), base);
    }
    EmitInstHits("empty", 0x10, XS::ScanInstanceAccesses({}, 0x10), base);
    {
        // One huge range list entry over the whole edge area (stops at the first
        // 0xCC / decode failure) and single tiny ranges.
        const std::vector<XS::MethodRange> whole = {{rg.Addr(kInstEdgeArea), kInstEdgeLen}};
        EmitInstHits("edge-area", 0x10, XS::ScanInstanceAccesses(whole, 0x10), base);
    }
    return Finish();
}

// --- rip <image> ------------------------------------------------------------------

struct RefTarget {
    std::uint32_t rva;
    std::uint32_t refs;
};

int CmdRip(const wchar_t* path) {
    MappedImage img;
    if (const int rc = MapOrFail(path, img); rc != 0) return rc;
    const auto base = reinterpret_cast<std::uintptr_t>(img.base);

    // Harness-owned reference count (deliberately not the code under test):
    // every `{8B,8D,89} modrm(rip) disp32` at opcode offset i targets
    // rva + i + 6 + disp (a REX prefix sits before the opcode, so the end is
    // the same). Also remember targets whose instruction starts within 16
    // bytes of a 1 MiB chunk boundary of its section.
    std::unordered_map<std::uint32_t, std::uint32_t> counts;
    counts.reserve(1u << 18);
    std::vector<std::uint32_t> boundary;
    std::uint64_t ripRefs = 0, execBytes = 0;
    for (const Section& sec : img.exec) {
        execBytes += sec.size;
        if (sec.size < 6) continue;
        const std::uint8_t* p = img.base + sec.rva;
        for (std::uint32_t i = 0; i + 6 <= sec.size; ++i) {
            if (p[i] != 0x8B && p[i] != 0x8D && p[i] != 0x89) continue;
            if ((p[i + 1] & 0xC7) != 0x05) continue;
            std::int32_t disp;
            std::memcpy(&disp, p + i + 2, 4);
            const std::int64_t target = static_cast<std::int64_t>(sec.rva) + i + 6 + disp;
            if (target < 0 || target >= static_cast<std::int64_t>(img.sizeOfImage)) continue;
            ++ripRefs;
            ++counts[static_cast<std::uint32_t>(target)];
            const std::uint32_t start = (i > 0 && IsRex(p[i - 1])) ? i - 1 : i;
            const std::uint32_t off = start % kMiB;
            if (boundary.size() < 64 && start >= kMiB && (off >= kMiB - 16 || off < 16)) {
                boundary.push_back(static_cast<std::uint32_t>(target));
            }
        }
    }

    auto inSecs = [](const std::vector<Section>& secs, std::uint32_t rva) {
        for (const Section& s : secs) {
            if (rva >= s.rva && rva < static_cast<std::uint64_t>(s.rva) + s.size) return true;
        }
        return false;
    };
    std::vector<RefTarget> dataT, execT;
    for (const auto& [rva, refs] : counts) {
        if (inSecs(img.data, rva)) dataT.push_back({rva, refs});
        else if (inSecs(img.exec, rva)) execT.push_back({rva, refs});
    }
    // Total order (refs desc, rva asc): independent of hash-map iteration order.
    auto byRefs = [](const RefTarget& a, const RefTarget& b) {
        return a.refs != b.refs ? a.refs > b.refs : a.rva < b.rva;
    };
    std::sort(dataT.begin(), dataT.end(), byRefs);
    std::sort(execT.begin(), execT.end(), byRefs);

    // `absolute`: the target is a fixed address (printed as is), not a
    // module-relative one.
    struct Pick { std::string kind; std::uint64_t addr; std::uint32_t refs; bool absolute; };
    std::vector<Pick> picks;
    std::unordered_set<std::uint64_t> seen;
    auto add = [&](std::string kind, std::uint64_t addr, std::uint32_t refs, bool absolute = false) {
        if (seen.insert(addr).second) picks.push_back({std::move(kind), addr, refs, absolute});
    };
    for (std::size_t i = 0; i < dataT.size() && i < 12; ++i) add("top-data", base + dataT[i].rva, dataT[i].refs);
    for (std::size_t i = 0; i < execT.size() && i < 4; ++i) add("top-exec", base + execT[i].rva, execT[i].refs);
    {
        std::vector<RefTarget> single, mid;
        for (const RefTarget& t : dataT) {
            if (t.refs == 1) single.push_back(t);
            else if (t.refs <= 10) mid.push_back(t);
        }
        auto byRva = [](const RefTarget& a, const RefTarget& b) { return a.rva < b.rva; };
        std::sort(single.begin(), single.end(), byRva);
        std::sort(mid.begin(), mid.end(), byRva);
        const std::size_t ns = (std::min)(std::size_t{8}, single.size());
        for (std::size_t i = 0; i < ns; ++i) {
            const RefTarget& t = single[i * single.size() / ns];
            add("single", base + t.rva, t.refs);
        }
        const std::size_t nm = (std::min)(std::size_t{4}, mid.size());
        for (std::size_t i = 0; i < nm; ++i) {
            const RefTarget& t = mid[i * mid.size() / nm];
            add("mid", base + t.rva, t.refs);
        }
    }
    {
        std::size_t nb = 0;
        for (const std::uint32_t rva : boundary) {
            if (nb >= 8) break;
            if (seen.count(base + rva)) continue;
            add("boundary", base + rva, counts[rva]);
            ++nb;
        }
    }
    {
        // Unreferenced data addresses.
        SplitMix64 rng(0x0DDBA11C0FFEE000ull);
        std::uint64_t dataBytes = 0;
        for (const Section& s : img.data) dataBytes += s.size;
        std::size_t got = 0;
        for (int tries = 0; tries < 4096 && got < 4 && dataBytes > 0; ++tries) {
            std::uint64_t at = rng.Below(dataBytes);
            std::size_t si = 0;
            while (at >= img.data[si].size) { at -= img.data[si].size; ++si; }
            const auto rva = static_cast<std::uint32_t>((img.data[si].rva + at) & ~std::uint64_t{7});
            if (counts.count(rva)) continue;
            add("none", base + rva, 0);
            ++got;
        }
    }
    add("special-base", base, counts.count(0) ? counts[0] : 0);
    add("special-end", base + img.sizeOfImage, 0);
    add("special-0", 0, 0, true);
    add("special-max", UINTPTR_MAX, 0, true);

    Line("img").Hex("sizeOfImage", img.sizeOfImage).Raw("exec", SectionsJson(img.exec))
        .Raw("data", SectionsJson(img.data)).U("execBytes", execBytes).Emit();
    Line("refs").U("ripRefs", ripRefs).U("targets", counts.size()).U("dataTargets", dataT.size())
        .U("execTargets", execT.size()).U("boundary", boundary.size()).U("picked", picks.size()).Emit();

    const std::vector<XS::ExecSection> secs = AbsExecSections(img);
    for (std::size_t i = 0; i < picks.size(); ++i) {
        const Pick& pk = picks[i];
        const auto c = XS::ScanRipRelative(secs, static_cast<std::uintptr_t>(pk.addr));
        Line("rip").U("t", i).S("kind", pk.kind).Hex("target", pk.absolute ? pk.addr : pk.addr - base)
            .U("refs", pk.refs)
            .U("n", c.size()).Raw("c", RipCandidatesJson(c, base)).Emit();
    }
    // Section-list variants with the most-referenced data target.
    if (!dataT.empty() && !secs.empty()) {
        const std::uintptr_t t = base + dataT[0].rva;
        const std::vector<XS::ExecSection> rev(secs.rbegin(), secs.rend());
        const std::vector<XS::ExecSection> first = {secs[0]};
        std::vector<XS::ExecSection> shifted;
        for (const auto& s : secs) {
            if (s.size > 3) shifted.push_back({s.va + 3, s.size - 3});
        }
        using Variant = std::pair<std::string_view, const std::vector<XS::ExecSection>*>;
        const Variant variants[] = {{"rev", &rev}, {"first-only", &first}, {"shift-3", &shifted}};
        for (const auto& [label, list] : variants) {
            const auto c = XS::ScanRipRelative(*list, t);
            Line("ripv").S("v", label).Hex("target", t - base).U("n", c.size())
                .Raw("c", RipCandidatesJson(c, base)).Emit();
        }
    }
    return Finish();
}

// --- inst <image> -----------------------------------------------------------------

constexpr std::size_t kMaxMethodBytes = 8u * 1024u;  // IL2CPPFieldXref.cpp's cap
constexpr std::size_t kInstBlock = 1024;             // ranges per hashed block
constexpr std::size_t kGridStride = 16;              // `grid` run: every 16th range
constexpr std::size_t kMisStride = 16;               // `misaligned` run: every 16th range

// .pdata RUNTIME_FUNCTION.BeginAddress values, sorted, clamped exactly like
// IL2CPPFieldXref.cpp clamps its method index: end = next greater entry
// (if within the cap), capped at start + 8 KiB and at the section end;
// entries outside every executable section are dropped.
bool BuildPdataRanges(const MappedImage& img, std::vector<XS::MethodRange>& out, std::size_t& entries) {
    entries = 0;
    if (img.pdataRva == 0 || img.pdataSize < sizeof(RUNTIME_FUNCTION)) return false;
    const auto* rf = reinterpret_cast<const RUNTIME_FUNCTION*>(img.base + img.pdataRva);
    entries = img.pdataSize / sizeof(RUNTIME_FUNCTION);
    std::vector<std::uint32_t> begins;
    begins.reserve(entries);
    for (std::size_t i = 0; i < entries; ++i) begins.push_back(rf[i].BeginAddress);
    std::sort(begins.begin(), begins.end());

    const auto base = reinterpret_cast<std::uintptr_t>(img.base);
    for (std::size_t i = 0; i < begins.size(); ++i) {
        const std::uintptr_t start = base + begins[i];
        std::uintptr_t end = start + kMaxMethodBytes;
        if (i + 1 < begins.size()) {
            const std::uintptr_t next = base + begins[i + 1];
            if (next > start && next < end) end = next;
        }
        std::uintptr_t secEnd = 0;
        for (const Section& s : img.exec) {
            const std::uintptr_t sva = base + s.rva;
            if (start >= sva && start < sva + s.size) { secEnd = sva + s.size; break; }
        }
        if (secEnd == 0) continue;
        if (end > secEnd) end = secEnd;
        std::size_t len = static_cast<std::size_t>(end - start);
        if (len == 0) continue;
        if (len > kMaxMethodBytes) len = kMaxMethodBytes;
        out.push_back({start, len});
    }
    return true;
}

// The range list of one `inst` run, derived from the full .pdata list:
//   all         every range (decoder coverage of the whole image)
//   grid        every kGridStride-th range (the field-offset grid)
//   misaligned  every kMisStride-th range started 1..3 bytes late, so the
//               decoders see real bytes from odd positions (prefixes,
//               mid-instruction ModRMs, early decode failures)
bool InstRunRanges(std::wstring_view run, const std::vector<XS::MethodRange>& all,
                   std::vector<XS::MethodRange>& out) {
    out.clear();
    if (run == L"all") {
        out = all;
    } else if (run == L"grid") {
        for (std::size_t i = 0; i < all.size(); i += kGridStride) out.push_back(all[i]);
    } else if (run == L"misaligned") {
        for (std::size_t i = 0; i < all.size(); i += kMisStride) {
            const std::size_t skew = 1 + (i / kMisStride) % 3;
            if (all[i].len > skew) out.push_back({all[i].start + skew, all[i].len - skew});
        }
    } else {
        return false;
    }
    return true;
}

std::vector<std::size_t> InstGridOffsets() {
    std::vector<std::size_t> fo;
    for (std::size_t v = 0x10; v <= 0x200; v += 8) fo.push_back(v);
    for (std::size_t v = 0x11; v <= 0x3F; v += 2) fo.push_back(v);  // 0x18 odd offsets
    fo.push_back(0);
    fo.push_back(0x10001);
    return fo;
}

// Per block of kInstBlock ranges with at least one hit: count, writes and an
// FNV-1a hash of (method, ip-rel, write) in kernel order; then the totals and
// a hash of the whole hit stream for the field offset.
void EmitInstBlocks(std::string_view run, std::size_t fo, const std::vector<XS::InstanceHit>& hits,
                    std::uintptr_t base) {
    Fnv64 all;
    std::size_t writes = 0;
    std::size_t i = 0;
    while (i < hits.size()) {
        const std::size_t blk = hits[i].method / kInstBlock;
        Fnv64 h;
        std::size_t n = 0, w = 0;
        for (; i < hits.size() && hits[i].method / kInstBlock == blk; ++i) {
            const auto ipRel = static_cast<std::uint64_t>(hits[i].ip - base);
            const std::uint8_t wr = hits[i].write ? 1 : 0;
            h.U64(hits[i].method);
            h.U64(ipRel);
            h.U8(wr);
            all.U64(hits[i].method);
            all.U64(ipRel);
            all.U8(wr);
            ++n;
            w += wr;
        }
        writes += w;
        Line("ib").S("run", run).Hex("fo", fo).U("blk", blk).U("n", n).U("w", w).Hex("h", h.Value()).Emit();
    }
    Line("inst").S("run", run).Hex("fo", fo).U("n", hits.size()).U("w", writes).Hex("h", all.Value()).Emit();
}

int CmdInst(const wchar_t* path) {
    MappedImage img;
    if (const int rc = MapOrFail(path, img); rc != 0) return rc;
    const auto base = reinterpret_cast<std::uintptr_t>(img.base);

    std::vector<XS::MethodRange> ranges;
    std::size_t entries = 0;
    const bool havePdata = BuildPdataRanges(img, ranges, entries);
    std::uint64_t bytes = 0;
    for (const auto& r : ranges) bytes += r.len;
    Line("img").Hex("sizeOfImage", img.sizeOfImage).Raw("exec", SectionsJson(img.exec))
        .Hex("pdata", img.pdataRva).U("pdataSize", img.pdataSize).U("entries", entries)
        .U("ranges", ranges.size()).U("rangeBytes", bytes).U("block", kInstBlock).Emit();
    if (!havePdata || ranges.empty()) return Finish();

    struct Pass { const wchar_t* run; std::string_view name; std::vector<std::size_t> fos; };
    const Pass passes[] = {
        {L"all", "all", {0x10, 0x18, 0x0}},
        {L"grid", "grid", InstGridOffsets()},
        {L"misaligned", "misaligned", {0x8, 0x10, 0x18, 0x20}},
    };
    std::vector<XS::MethodRange> runRanges;
    for (const Pass& p : passes) {
        InstRunRanges(p.run, ranges, runRanges);
        Line("irun").S("run", p.name).U("ranges", runRanges.size()).U("nfo", p.fos.size()).Emit();
        for (const std::size_t fo : p.fos) {
            EmitInstBlocks(p.name, fo, XS::ScanInstanceAccesses(runRanges, fo), base);
        }
    }
    return Finish();
}

// Drill-down for a differing `inst` block: every hit of blocks
// [firstBlk, firstBlk + nBlk) of one run for one field offset, grouped per
// method (global range indices of that run), plus the slice's range table.
int CmdInstHits(const wchar_t* path, std::wstring_view run, std::size_t fo, std::size_t firstBlk,
                std::size_t nBlk) {
    MappedImage img;
    if (const int rc = MapOrFail(path, img); rc != 0) return rc;
    const auto base = reinterpret_cast<std::uintptr_t>(img.base);
    std::vector<XS::MethodRange> all, ranges;
    std::size_t entries = 0;
    BuildPdataRanges(img, all, entries);
    InstRunRanges(run, all, ranges);
    const std::size_t from = (std::min)(ranges.size(), firstBlk * kInstBlock);
    const std::size_t to = (std::min)(ranges.size(), (firstBlk + nBlk) * kInstBlock);
    const std::vector<XS::MethodRange> slice(ranges.begin() + static_cast<std::ptrdiff_t>(from),
                                             ranges.begin() + static_cast<std::ptrdiff_t>(to));
    std::vector<XS::InstanceHit> hits = XS::ScanInstanceAccesses(slice, fo);
    for (auto& h : hits) h.method += from;  // report the run's range indices
    Line("slice").U("from", from).U("to", to).Emit();
    for (std::size_t i = from; i < to && i - from < 4096; ++i) {
        Line("range").U("m", i).Hex("start", ranges[i].start - base).U("len", ranges[i].len).Emit();
    }
    std::string runName;  // ASCII by construction (validated in wmain)
    for (const wchar_t c : run) runName += static_cast<char>(c);
    EmitInstHits(runName, fo, hits, base);
    return Finish();
}

int Usage() {
    std::fputs(
        "usage: xref-dump <subcommand> [args]\n"
        "  rip-synthetic\n"
        "  inst-synthetic\n"
        "  rip <GameAssembly.dll>\n"
        "  inst <GameAssembly.dll>\n"
        "  inst-hits <GameAssembly.dll> <all|grid|misaligned> <fo-hex> <first-block> <nblocks>\n",
        stderr);
    return 1;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    // No Windows error dialogs on a bad image — fail with an exit code.
    ::SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
    // Binary stdout: '\n' stays '\n', so the byte compare sees exactly what
    // was written.
    (void)_setmode(_fileno(stdout), _O_BINARY);
    static char outBuf[1 << 20];
    std::setvbuf(stdout, outBuf, _IOFBF, sizeof(outBuf));

    if (argc < 2) return Usage();
    const std::wstring_view cmd = argv[1];
    if (cmd == L"rip-synthetic" && argc == 2) return CmdRipSynthetic();
    if (cmd == L"inst-synthetic" && argc == 2) return CmdInstSynthetic();
    if (cmd == L"rip" && argc == 3) return CmdRip(argv[2]);
    if (cmd == L"inst" && argc == 3) return CmdInst(argv[2]);
    if (cmd == L"inst-hits" && argc == 7) {
        const std::wstring_view run = argv[3];
        std::uint64_t fo = 0, first = 0, n = 0;
        if ((run != L"all" && run != L"grid" && run != L"misaligned") || !ParseHexU64(argv[4], fo) ||
            !ParseDecU64(argv[5], first) || !ParseDecU64(argv[6], n) || first > (1u << 24) ||
            n > (1u << 24)) {
            return Usage();
        }
        return CmdInstHits(argv[2], run, static_cast<std::size_t>(fo), static_cast<std::size_t>(first),
                           static_cast<std::size_t>(n));
    }
    return Usage();
}
