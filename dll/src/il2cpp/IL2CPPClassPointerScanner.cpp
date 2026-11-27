// IL2CPPClassPointerScanner.cpp — implementation.

#include "IL2CPPClassPointerScanner.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/Memory.h"
#include "../shared/ModuleHelper.h"
#include "../shared/SafeChunkRead.h"

#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <vector>

namespace Discrete::IL2CPP {

namespace {

// PE section we care about. Mirrors `ExecutableSection` in IL2CPPFieldXref.cpp
// but inverts the selector — we want readable, NOT executable sections
// (typically `.rdata`, `.data`, sometimes `.bss` / `.pdata`).
struct DataSection {
    std::uintptr_t va;
    std::size_t    size;
    char           name[9];  // section name, NUL-terminated for log lines
};

// Walk the PE headers + section table, return every section flagged
// MEM_READ and NOT MEM_EXECUTE. Matches IL2CPPFieldXref.cpp's
// EnumerateExecutableSections pattern (same SafeRead guard against
// AC-stripped headers, though headers are below 4 KB so this is
// defensive — never observed live).
std::vector<DataSection> EnumerateDataSections(void* moduleBase) {
    std::vector<DataSection> out;
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
        // Skip executable sections (.text / .textbss). We want anything
        // readable that's not code — class pointers live in `.rdata`
        // (typed metadata tables) and sometimes `.data` (mutable per-
        // TU caches like `Il2CppClass* g_class_X_cache`).
        if ((sh->Characteristics & IMAGE_SCN_MEM_EXECUTE) != 0) continue;
        if ((sh->Characteristics & IMAGE_SCN_MEM_READ)    == 0) continue;
        // Discard zero-sized sections (some toolchains emit empty
        // `.bss` / `.tls` shells).
        if (sh->Misc.VirtualSize == 0) continue;

        DataSection s{};
        s.va   = reinterpret_cast<std::uintptr_t>(base) + sh->VirtualAddress;
        s.size = sh->Misc.VirtualSize;
        std::memcpy(s.name, sh->Name, sizeof(sh->Name));
        s.name[8] = '\0';
        out.push_back(s);
    }
    return out;
}

}  // namespace

std::vector<ClassPointerSlot> EnumerateClassPointerSlots(
    Il2CppClass* targetClass) {

    std::vector<ClassPointerSlot> result;
    if (targetClass == nullptr) return result;

    void* gaBase = ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP);
    if (gaBase == nullptr) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "ClassPointerScanner: GameAssembly.dll not loaded");
        return result;
    }

    auto sections = EnumerateDataSections(gaBase);
    if (sections.empty()) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "ClassPointerScanner: no data sections in GameAssembly.dll");
        return result;
    }

    const std::uintptr_t targetVal =
        reinterpret_cast<std::uintptr_t>(targetClass);

    // 1 MB chunk size matches IL2CPPFieldXref.cpp's executable scanner
    // (peak working-set bounded at ~1 MB even when scanning a 200 MB
    // Songs-of-Silence-class `.rdata`). 8-byte tail overlap unnecessary
    // here — every slot is 8-byte aligned at section start, so chunk
    // boundaries fall on slot boundaries when `want` is a multiple of 8.
    constexpr std::size_t kChunkSize = 1024u * 1024u;
    std::vector<std::uint8_t> buf;
    buf.reserve(kChunkSize);

    // B-L5: Anti-cheat page-strip tolerance via SafePeScan::ScanReadableSpans —
    // when a 1 MiB chunk read fails due to a single PAGE_NOACCESS / PAGE_GUARD
    // page, falls back to 4 KiB page-by-page reads so readable pages BEFORE
    // the bad page are never dropped.
    std::size_t failedReads = 0;
    std::size_t totalScanned = 0;

    for (const auto& sec : sections) {
        SafePeScan::ScanReadableSpans(
            reinterpret_cast<const std::uint8_t*>(sec.va),
            sec.size,
            kChunkSize,
            /*overlapBytes=*/0u,
            buf,
            [&](const std::uint8_t* localSpan,
                const std::uint8_t* realSpanBase,
                std::size_t spanLen) -> bool {
                const std::uintptr_t realAddr0 = reinterpret_cast<std::uintptr_t>(realSpanBase);
                const std::size_t alignPad =
                    (sizeof(std::uintptr_t) - (realAddr0 & (sizeof(std::uintptr_t) - 1u))) &
                    (sizeof(std::uintptr_t) - 1u);
                for (std::size_t i = alignPad; i + sizeof(std::uintptr_t) <= spanLen;
                     i += sizeof(std::uintptr_t)) {
                    std::uintptr_t val = 0;
                    std::memcpy(&val, localSpan + i, sizeof(val));
                    if (val == targetVal) {
                        ClassPointerSlot slot{};
                        slot.address   = realAddr0 + i;
                        slot.sectionVa = sec.va;
                        result.push_back(slot);
                    }
                }
                totalScanned += spanLen;
                return true;
            },
            &failedReads);
    }

    // Tighten log volume — only log when caller will care (zero hits is
    // interesting; non-zero we just summarise).
    if (result.empty()) {
        Log::Write(MagicStrings::LogCategoryWalk,
            "ClassPointerScanner: 0 slots holding class@" +
            [](std::uintptr_t v) {
                char b[24];
                std::snprintf(b, sizeof(b), "0x%llX",
                    static_cast<unsigned long long>(v));
                return std::string(b);
            }(targetVal) +
            " (sections=" + std::to_string(sections.size()) +
            " bytes=" + std::to_string(totalScanned) +
            " failedReads=" + std::to_string(failedReads) +
            ") — class not referenced from compiled code, or "
            "GameAssembly.dll uses a non-standard storage scheme");
    } else {
        Log::Write(MagicStrings::LogCategoryWalk,
            "ClassPointerScanner: " + std::to_string(result.size()) +
            " slot(s) found (bytes=" + std::to_string(totalScanned) +
            " failedReads=" + std::to_string(failedReads) + ")");
    }
    return result;
}

}  // namespace Discrete::IL2CPP
