// IL2CPPAddressFinder.cpp — Phase 17a implementation.

#include "IL2CPPAddressFinder.h"

#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"
#include "../shared/Memory.h"
#include "IL2CPPClassWalker.h"
#include "IL2CPPDomain.h"
#include "IL2CPPResolver.h"
#include "IL2CPPThreadAttach.h"

#include <windows.h>

#include <algorithm>
#include <mutex>
#include <vector>

namespace UnityCEDumper::IL2CPP {

namespace {

// Cache of every Il2CppClass* observed during enumeration. Sorted ascending
// for O(log N) binary-search membership.
std::mutex                   g_cacheMutex;
std::vector<std::uintptr_t>  g_classSet;
std::uintptr_t               g_minKlass = 0;
std::uintptr_t               g_maxKlass = 0;
bool                         g_built    = false;

// SEH-protected name probe. il2cpp_class_get_name dereferences several layers
// of pointer state — when the candidate isn't a real Il2CppClass*, the chain
// faults at unpredictable depth. We catch the AV here so the worker thread
// keeps running.
#pragma warning(push)
#pragma warning(disable: 4509)
const char* SehClassNameProbe(fn_class_get_name fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehClassNamespaceProbe(fn_class_get_namespace fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const Il2CppImage* SehClassImageProbe(fn_class_get_image fn, Il2CppClass* k) {
    __try { return fn(k); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
const char* SehImageNameProbe(fn_image_get_name fn, const Il2CppImage* img) {
    __try { return fn(img); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
}
#pragma warning(pop)

bool ContainsClass(std::uintptr_t p) {
    if (g_classSet.empty()) return false;
    if (p < g_minKlass || p > g_maxKlass) return false;
    return std::binary_search(g_classSet.begin(), g_classSet.end(), p);
}

// Strip ".dll" suffix from image name so it round-trips against the
// EnumerateAssemblies output (which strips the suffix).
std::string StripDllSuffix(const std::string& s) {
    if (s.size() >= 4) {
        const std::string tail = s.substr(s.size() - 4);
        if (tail == ".dll" || tail == ".DLL") return s.substr(0, s.size() - 4);
    }
    return s;
}

}  // namespace

bool BuildClassRangeCache() {
    std::lock_guard<std::mutex> lk(g_cacheMutex);
    if (g_built) return !g_classSet.empty();

    if (!IsResolved()) {
        Log::Write(MagicStrings::LogCategoryWalk,
                   "AddrFinder: BuildClassRangeCache: il2cpp not resolved");
        return false;
    }

    g_classSet.clear();
    g_minKlass = static_cast<std::uintptr_t>(-1);
    g_maxKlass = 0;

    auto assemblies = EnumerateAssemblies();
    for (const auto& a : assemblies) {
        if (a.image == nullptr) continue;
        auto classes = EnumerateClasses(a.image);
        for (const auto& c : classes) {
            if (c.klass == nullptr) continue;
            auto p = reinterpret_cast<std::uintptr_t>(c.klass);
            g_classSet.push_back(p);
            if (p < g_minKlass) g_minKlass = p;
            if (p > g_maxKlass) g_maxKlass = p;
        }
    }
    std::sort(g_classSet.begin(), g_classSet.end());
    g_classSet.erase(std::unique(g_classSet.begin(), g_classSet.end()),
                     g_classSet.end());
    if (g_classSet.empty()) {
        g_minKlass = 0;
        g_maxKlass = 0;
    }
    g_built = true;

    Log::Write(MagicStrings::LogCategoryWalk,
               "AddrFinder: cached " + std::to_string(g_classSet.size()) +
               " Il2CppClass* pointers, range [" +
               std::to_string(g_minKlass) + ", " + std::to_string(g_maxKlass) + "]");
    return !g_classSet.empty();
}

void InvalidateClassRangeCache() {
    std::lock_guard<std::mutex> lk(g_cacheMutex);
    g_classSet.clear();
    g_minKlass = 0;
    g_maxKlass = 0;
    g_built    = false;
}

ClassRangeStats GetClassRangeStats() {
    std::lock_guard<std::mutex> lk(g_cacheMutex);
    ClassRangeStats s;
    s.built      = g_built;
    s.classCount = g_classSet.size();
    s.minKlass   = g_minKlass;
    s.maxKlass   = g_maxKlass;
    return s;
}

AddressLookup WhatIsAt(std::uintptr_t addr, std::size_t maxBackwardScan) {
    AddressLookup out;
    out.queriedAddr = addr;

    if (addr == 0) {
        out.reason = "address is null";
        return out;
    }

    // Make sure the cache is ready. BuildClassRangeCache is idempotent.
    if (!BuildClassRangeCache()) {
        out.reason = "class_range_cache_unavailable";
        return out;
    }

    // 4 KB default per the spec; cap at 16 KB so a malformed request can't
    // pin the worker thread for long.
    if (maxBackwardScan == 0)       maxBackwardScan = 4096;
    if (maxBackwardScan > 16 * 1024) maxBackwardScan = 16 * 1024;

    // Round down to 8-byte boundary. Object headers are pointer-aligned.
    constexpr std::size_t kStride = 8;
    std::uintptr_t start = addr & ~static_cast<std::uintptr_t>(kStride - 1);

    // scannedFrom is the lowest address we'll try; scannedTo is `start + 8`
    // (one past the highest, exclusive) so the range matches the spec's
    // {from, to} convention.
    std::size_t maxSteps = maxBackwardScan / kStride;
    if (maxSteps == 0) maxSteps = 1;
    out.scannedFrom = (start >= maxSteps * kStride) ? (start - (maxSteps - 1) * kStride) : 0;
    out.scannedTo   = start + kStride;

    const auto& r = GetResolver();

    // We need a single thread-attach scope for the SEH-protected name probes
    // — every probe potentially calls back into managed-state code that
    // expects the thread to be attached.
    Il2CppDomain* domain = GetDomain();
    if (domain == nullptr) {
        out.reason = "no_il2cpp_domain";
        return out;
    }
    ThreadAttachGuard guard(domain);
    if (!guard.Attached()) {
        out.reason = "il2cpp_thread_attach_failed";
        return out;
    }

    for (std::size_t i = 0; i < maxSteps; ++i) {
        std::uintptr_t p = start - i * kStride;
        out.scannedSteps = i + 1;

        // Read the candidate header qword. SafeReadBytes shields us from
        // unmapped pages; if the read fails, treat it as "scanned past the
        // start of the heap region" and stop.
        std::uintptr_t klassCandidate = 0;
        if (!Memory::SafeReadBytes(reinterpret_cast<void*>(p),
                                   &klassCandidate, sizeof(klassCandidate))) {
            // Unmapped — keep walking. In practice, the heap allocator
            // keeps live objects in committed regions, so this almost
            // always means we walked off the start of a smaller commit.
            continue;
        }
        if (klassCandidate == 0) continue;
        if ((klassCandidate & 7u) != 0) continue;  // klass pointers are 8-aligned
        if (!ContainsClass(klassCandidate))  continue;

        // Range hit + alignment OK. Final filter: make sure the class object
        // still looks alive. il2cpp_class_get_name dereferences a chain of
        // metadata pointers, so an SEH catch is mandatory here.
        Il2CppClass* k = reinterpret_cast<Il2CppClass*>(klassCandidate);
        const char* name = (r.class_get_name != nullptr)
            ? SehClassNameProbe(r.class_get_name, k) : nullptr;
        if (name == nullptr || *name == '\0') continue;

        // Got it. Populate the result.
        out.found       = true;
        out.instance    = p;
        out.klassPtr    = klassCandidate;
        out.fieldOffset = static_cast<std::ptrdiff_t>(addr - p);
        out.className   = name;

        if (r.class_get_namespace != nullptr) {
            const char* nsStr = SehClassNamespaceProbe(r.class_get_namespace, k);
            if (nsStr != nullptr) out.ns = nsStr;
        }
        if (r.class_get_image != nullptr && r.image_get_name != nullptr) {
            const Il2CppImage* img = SehClassImageProbe(r.class_get_image, k);
            if (img != nullptr) {
                const char* imgName = SehImageNameProbe(r.image_get_name, img);
                if (imgName != nullptr) out.assembly = StripDllSuffix(imgName);
            }
        }
        return out;
    }

    out.reason = "no_klass_in_range";
    return out;
}

}  // namespace UnityCEDumper::IL2CPP
