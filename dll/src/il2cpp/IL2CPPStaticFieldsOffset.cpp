// IL2CPPStaticFieldsOffset.cpp — implementation.

#include "IL2CPPStaticFieldsOffset.h"

#include "IL2CPPClassWalker.h"
#include "IL2CPPDomain.h"
#include "IL2CPPResolver.h"
#include "IL2CPPThreadAttach.h"
#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/Memory.h"

#include <windows.h>

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <mutex>
#include <string>

namespace UnityCEDumper::IL2CPP {

namespace {

// SEH leaf — mirrors the pattern from IL2CPPFieldXref.cpp. Duplicated
// rather than refactored out because __try frames can't share a stack
// with C++ objects holding non-trivial destructors, and a per-TU copy
// keeps each leaf tightly scoped.
#pragma warning(push)
#pragma warning(disable: 4509)
void* SehStaticDataProbe(fn_class_get_static_data fn, Il2CppClass* k) {
    __try { return fn(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
#pragma warning(pop)

std::once_flag g_once;
std::size_t    g_offset = 0;  // 0 = unset / discovery failed

// Search window. Empirical bounds: Il2CppClass is ~232 bytes on v29
// (Unity 2021.3), ~280 bytes on v39 (Unity 6.1). 1024 bytes gives us
// 128 8-byte-aligned probes — wide enough to absorb any reasonable
// future struct growth without falsely matching unrelated heap data
// just past the struct's tail.
constexpr std::size_t kScanLimit = 1024;
constexpr std::size_t kStepSize  = sizeof(void*);

// How many classes we're willing to probe before giving up. Most BCL
// classes (System.Object, System.String, primitive wrappers) are
// runtime-initialised early and have static_fields populated; usually
// we hit one within the first ~20-50 classes walked. Hard cap at 1024
// keeps the discovery latency bounded on weird targets (e.g. an image
// full of pure interfaces or generic open types).
constexpr std::size_t kMaxClassesToProbe = 1024;

// Hex-print helper for log lines.
std::string Hex(std::uintptr_t v) {
    char buf[24];
    std::snprintf(buf, sizeof(buf), "0x%llX",
                  static_cast<unsigned long long>(v));
    return buf;
}

void DiscoverOffset() {
    const auto& r = GetResolver();
    if (!r.resolved || r.class_get_static_data == nullptr) {
        Log::Write(MagicStrings::LogCategoryInit,
            "IL2CPPStaticFieldsOffset: il2cpp_class_get_static_data unbound — "
            "Phase 28b-v2 chain detection unavailable");
        return;
    }

    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) {
        Log::Write(MagicStrings::LogCategoryInit,
            "IL2CPPStaticFieldsOffset: il2cpp domain unavailable");
        return;
    }

    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) {
        Log::Write(MagicStrings::LogCategoryInit,
            "IL2CPPStaticFieldsOffset: il2cpp thread_attach failed");
        return;
    }

    auto assemblies = EnumerateAssemblies();
    std::size_t classesProbed = 0;

    for (const auto& asmEntry : assemblies) {
        if (asmEntry.image == nullptr) continue;
        auto classes = EnumerateClasses(asmEntry.image);
        for (const auto& ce : classes) {
            if (ce.klass == nullptr) continue;
            if (++classesProbed > kMaxClassesToProbe) {
                Log::Write(MagicStrings::LogCategoryInit,
                    "IL2CPPStaticFieldsOffset: probed " +
                    std::to_string(kMaxClassesToProbe) +
                    " classes, none had non-null static_fields — "
                    "giving up (Phase 28b-v2 unavailable on this target)");
                return;
            }

            void* staticData = SehStaticDataProbe(r.class_get_static_data, ce.klass);
            if (staticData == nullptr) continue;

            // Found a class with allocated static storage. Linear-scan
            // its struct memory for the returned pointer value. The
            // offset where it sits IS `offsetof(Il2CppClass, static_fields)`
            // — same byte layout applies to every class because IL2CPP
            // doesn't vary the struct per-class.
            const std::uintptr_t targetVal =
                reinterpret_cast<std::uintptr_t>(staticData);
            const auto* klassBytes =
                reinterpret_cast<const std::uint8_t*>(ce.klass);

            for (std::size_t off = 0; off + kStepSize <= kScanLimit;
                 off += kStepSize) {
                std::uintptr_t probedVal = 0;
                if (!Memory::SafeReadBytes(klassBytes + off,
                                            &probedVal,
                                            sizeof(probedVal))) {
                    // Page boundary or AC-stripped page — stop probing.
                    // Class struct should fit entirely in one mapped
                    // page, so a read fault means we've walked into
                    // uncommitted memory past the struct's tail.
                    break;
                }
                if (probedVal == targetVal) {
                    g_offset = off;
                    Log::Write(MagicStrings::LogCategoryInit,
                        "IL2CPPStaticFieldsOffset: discovered offsetof(static_fields)=" +
                        Hex(off) + " (probed via " +
                        (ce.ns.empty() ? ce.name : ce.ns + "." + ce.name) +
                        ", class " + std::to_string(classesProbed) +
                        " in scan, static_fields=" + Hex(targetVal) + ")");
                    return;
                }
            }

            // Pointer not found within scan window. Highly unusual —
            // this means the class struct is larger than kScanLimit OR
            // IL2CPP stores static_fields via some indirection rather
            // than a direct member. Don't keep probing other classes;
            // same struct layout applies everywhere, so a second probe
            // would hit the same wall.
            Log::Write(MagicStrings::LogCategoryInit,
                "IL2CPPStaticFieldsOffset: probe via " +
                (ce.ns.empty() ? ce.name : ce.ns + "." + ce.name) +
                " has static_fields=" + Hex(targetVal) +
                " but pointer not found in first " +
                std::to_string(kScanLimit) +
                " bytes of class struct — discovery failed (Phase "
                "28b-v2 unavailable; report this Unity version + IL2CPP "
                "metadata version for investigation)");
            return;
        }
    }
    Log::Write(MagicStrings::LogCategoryInit,
        "IL2CPPStaticFieldsOffset: walked all " +
        std::to_string(classesProbed) +
        " classes across " + std::to_string(assemblies.size()) +
        " assemblies, none had non-null static_fields — discovery failed");
}

}  // namespace

std::size_t GetStaticFieldsOffsetInClass() {
    std::call_once(g_once, DiscoverOffset);
    return g_offset;
}

}  // namespace UnityCEDumper::IL2CPP
