// IL2CPPLazyInitSlotScanner.cpp — implementation.

#include "IL2CPPLazyInitSlotScanner.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/Memory.h"
#include "../shared/ModuleHelper.h"

#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <mutex>

namespace UnityCEDumper::IL2CPP {

namespace {

std::once_flag                 g_once;
std::vector<LazyInitSlot>      g_slots;

// Section ranges discovered at scan time. Both ranges are absolute VAs
// (NOT RVAs); empty vector means no matching section found.
struct VaRange { std::uintptr_t lo; std::uintptr_t hi; };

std::vector<VaRange> CollectSections(void* moduleBase, DWORD requiredMask,
                                     DWORD forbiddenMask) {
    std::vector<VaRange> out;
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
        const DWORD ch = sh->Characteristics;
        if ((ch & requiredMask)  != requiredMask)  continue;
        if ((ch & forbiddenMask) != 0)             continue;
        if (sh->Misc.VirtualSize == 0) continue;
        VaRange r;
        r.lo = reinterpret_cast<std::uintptr_t>(base) + sh->VirtualAddress;
        r.hi = r.lo + sh->Misc.VirtualSize;
        out.push_back(r);
    }
    return out;
}

inline bool InRanges(const std::vector<VaRange>& rs, std::uintptr_t v) {
    for (const auto& r : rs) if (v >= r.lo && v < r.hi) return true;
    return false;
}

// REX prefix family detector — accept any 0x40..0x4F.
inline bool IsRexByte(std::uint8_t b) noexcept {
    return (b & 0xF0u) == 0x40u;
}
// mod=00, r/m=101 — the canonical RIP-relative ModRM. The reg field
// (bits 3-5) doesn't change addressing.
inline bool IsRipRelativeModrm(std::uint8_t m) noexcept {
    return (m & 0xC7u) == 0x05u;
}
inline std::int32_t ReadInt32LE(const std::uint8_t* p) noexcept {
    return  static_cast<std::int32_t>(
            static_cast<std::uint32_t>(p[0])
          | (static_cast<std::uint32_t>(p[1]) <<  8)
          | (static_cast<std::uint32_t>(p[2]) << 16)
          | (static_cast<std::uint32_t>(p[3]) << 24));
}

void DoScan() {
    void* gaBase = ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP);
    if (gaBase == nullptr) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "LazyInitSlotScanner: GameAssembly.dll not loaded");
        return;
    }

    // Executable sections — chunk source AND valid call-target destinations.
    auto execSections = CollectSections(gaBase, IMAGE_SCN_MEM_EXECUTE, 0);
    if (execSections.empty()) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "LazyInitSlotScanner: no exec sections in GameAssembly.dll");
        return;
    }

    // Initialised-data / BSS sections — valid lea-target destinations.
    // (Same filter `IL2CPPMetadataRegistration::ListInitDataSections` uses.)
    auto dataSections = CollectSections(
        gaBase,
        IMAGE_SCN_MEM_READ,
        IMAGE_SCN_MEM_EXECUTE);
    if (dataSections.empty()) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "LazyInitSlotScanner: no data sections in GameAssembly.dll");
        return;
    }

    // 1 MB chunk + small tail overlap so a 12-byte (7 lea + 5 call)
    // sequence starting near the chunk boundary still fits.
    constexpr std::size_t kChunkSize    = 1024u * 1024u;
    constexpr std::size_t kTailOverlap  = 32u;
    std::vector<std::uint8_t> buf(kChunkSize + kTailOverlap);

    std::size_t bytesScanned   = 0;
    std::size_t leaCandidates  = 0;
    std::size_t failedReads    = 0;
    std::size_t matchedPairs   = 0;

    for (const auto& sec : execSections) {
        const std::size_t size = sec.hi - sec.lo;
        std::size_t pos = 0;
        while (pos < size) {
            std::size_t want     = (size - pos) < kChunkSize
                ? (size - pos) : kChunkSize;
            std::size_t readWant = want + kTailOverlap;
            if (pos + readWant > size) readWant = size - pos;
            if (readWant < 12) break;

            const std::uint8_t* secBase =
                reinterpret_cast<const std::uint8_t*>(sec.lo);
            if (!Memory::SafeReadBytes(secBase + pos, buf.data(), readWant)) {
                ++failedReads;
                pos += 4096u;
                continue;
            }

            const std::size_t emitEnd = want;
            const std::size_t scanEnd = (readWant >= 12)
                ? std::min(emitEnd, readWant - 12 + 1u)
                : 0u;

            for (std::size_t p = 0; p < scanEnd; ++p) {
                // ---- Try to match lea reg, [rip+disp32] ------------------
                // 7-byte form (with REX) or 6-byte form (without).
                std::size_t leaLen = 0;
                std::int32_t leaDisp = 0;
                {
                    std::size_t off = 0;
                    if (IsRexByte(buf[p])) ++off;
                    if (p + off + 6 > readWant) continue;
                    if (buf[p + off] != 0x8D) continue;
                    if (!IsRipRelativeModrm(buf[p + off + 1])) continue;
                    leaDisp = ReadInt32LE(buf.data() + p + off + 2);
                    leaLen  = off + 2 + 4;
                }

                // REX-skip dedup — if previous byte was REX, skip the
                // 6-byte form here (the 7-byte form already covered it
                // at p-1). Same pattern as `IL2CPPChainDetector`.
                if (leaLen == 6 && p > 0 && IsRexByte(buf[p - 1])) continue;

                const std::uintptr_t leaIP    = sec.lo + pos + p;
                const std::uintptr_t leaAfter = leaIP + leaLen;
                const std::uintptr_t leaTarget =
                    leaAfter + static_cast<std::intptr_t>(leaDisp);

                // Lea target must land in a data section of the same module.
                if (!InRanges(dataSections, leaTarget)) continue;
                ++leaCandidates;

                // ---- Try to match immediate-next call rel32 --------------
                // 5-byte form: 0xE8 + disp32. No REX (call rel32 doesn't take it).
                const std::size_t callPos = p + leaLen;
                if (callPos + 5 > readWant) continue;
                if (buf[callPos] != 0xE8) continue;

                const std::int32_t callDisp = ReadInt32LE(buf.data() + callPos + 1);
                const std::uintptr_t callIP    = sec.lo + pos + callPos;
                const std::uintptr_t callAfter = callIP + 5;
                const std::uintptr_t callTarget =
                    callAfter + static_cast<std::intptr_t>(callDisp);

                // Call target must land in some executable section of the
                // same module (= a real IL2CPP runtime helper, not a
                // tail-call into another DLL or junk).
                if (!InRanges(execSections, callTarget)) continue;

                LazyInitSlot s;
                s.slotAddress = leaTarget;
                s.leaIP       = leaIP;
                s.callIP      = callIP;
                s.callTarget  = callTarget;
                g_slots.push_back(s);
                ++matchedPairs;
            }
            bytesScanned += want;
            pos += want;
        }
    }

    // Dedup by slotAddress — many methods reference the same slot, but
    // for v3's purposes we only need one entry per slot (the lookup is
    // "does the slot's current value match the target class ptr"
    // regardless of which method's init-call wrote it).
    std::sort(g_slots.begin(), g_slots.end(),
        [](const LazyInitSlot& a, const LazyInitSlot& b) {
            return a.slotAddress < b.slotAddress;
        });
    g_slots.erase(
        std::unique(g_slots.begin(), g_slots.end(),
            [](const LazyInitSlot& a, const LazyInitSlot& b) {
                return a.slotAddress == b.slotAddress;
            }),
        g_slots.end());

    Log::Write(MagicStrings::LogCategoryWalk,
        "LazyInitSlotScanner: scanned " + std::to_string(bytesScanned) +
        " bytes; leaCandidates=" + std::to_string(leaCandidates) +
        " matchedPairs=" + std::to_string(matchedPairs) +
        " uniqueSlots=" + std::to_string(g_slots.size()) +
        " failedReads=" + std::to_string(failedReads));
}

}  // namespace

const std::vector<LazyInitSlot>& EnumerateLazyInitSlots() {
    std::call_once(g_once, DoScan);
    return g_slots;
}

}  // namespace UnityCEDumper::IL2CPP
