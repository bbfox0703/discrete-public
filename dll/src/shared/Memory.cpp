// Memory.cpp — implementation of safe memory ops with SEH wrappers.

#include "Memory.h"

#include <windows.h>
#include <tlhelp32.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <thread>
#include <utility>

namespace Discrete::Memory {

namespace {

bool RegionHasProtect(const void* addr, std::size_t size, DWORD requiredAny, DWORD forbiddenAny) noexcept {
    const std::uint8_t* p = static_cast<const std::uint8_t*>(addr);
    const std::uint8_t* end = p + size;
    while (p < end) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (::VirtualQuery(p, &mbi, sizeof(mbi)) == 0) return false;
        if (mbi.State != MEM_COMMIT) return false;
        if (mbi.Protect & PAGE_GUARD) return false;
        if (mbi.Protect & forbiddenAny) return false;
        if ((mbi.Protect & requiredAny) == 0) return false;

        const std::uint8_t* regionEnd = static_cast<const std::uint8_t*>(mbi.BaseAddress) + mbi.RegionSize;
        if (regionEnd <= p) return false;
        p = regionEnd;
    }
    return true;
}

#pragma warning(push)
#pragma warning(disable: 4509)  // SEH used in function with object that has destructor — none here.

bool ReadSeh(const void* addr, void* dst, std::size_t size) noexcept {
    __try {
        ::memcpy(dst, addr, size);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool WriteSeh(void* addr, const void* src, std::size_t size) noexcept {
    __try {
        ::memcpy(addr, src, size);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

#pragma warning(pop)

}  // namespace

bool IsReadable(const void* addr, std::size_t size) noexcept {
    if (!addr || size == 0) return false;
    constexpr DWORD kReadable =
        PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY |
        PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    return RegionHasProtect(addr, size, kReadable, PAGE_NOACCESS);
}

bool IsWritable(const void* addr, std::size_t size) noexcept {
    if (!addr || size == 0) return false;
    constexpr DWORD kWritable =
        PAGE_READWRITE | PAGE_WRITECOPY |
        PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    return RegionHasProtect(addr, size, kWritable, PAGE_NOACCESS);
}

bool SafeReadBytes(const void* addr, void* dst, std::size_t size) noexcept {
    if (!addr || !dst || size == 0) return false;
    if (!IsReadable(addr, size)) return false;
    return ReadSeh(addr, dst, size);
}

bool SafeWriteBytes(void* addr, const void* src, std::size_t size) noexcept {
    if (!addr || !src || size == 0) return false;
    if (!IsWritable(addr, size)) return false;
    return WriteSeh(addr, src, size);
}

const std::uint8_t* AOBScan(const std::uint8_t* base, std::size_t size,
                            const std::uint8_t* pattern, std::string_view mask) noexcept {
    if (!base || !pattern || mask.empty() || size < mask.size()) return nullptr;
    const std::size_t patLen = mask.size();
    const std::uint8_t* end = base + size - patLen;
    for (const std::uint8_t* p = base; p <= end; ++p) {
        bool matched = true;
        for (std::size_t i = 0; i < patLen; ++i) {
            if (mask[i] == 'x' && p[i] != pattern[i]) { matched = false; break; }
        }
        if (matched) return p;
    }
    return nullptr;
}

const std::uint8_t* AOBScanModule(const wchar_t* moduleName,
                                  const std::uint8_t* pattern, std::string_view mask) noexcept {
    HMODULE mod = ::GetModuleHandleW(moduleName);
    if (!mod) return nullptr;

    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(mod);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return nullptr;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(reinterpret_cast<std::uint8_t*>(mod) + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return nullptr;

    const std::uint8_t* base = reinterpret_cast<const std::uint8_t*>(mod);
    const std::size_t size = nt->OptionalHeader.SizeOfImage;
    return AOBScan(base, size, pattern, mask);
}

std::vector<CommittedRegion> EnumerateCommittedRegions() {
    std::vector<CommittedRegion> out;
    SYSTEM_INFO si{};
    ::GetSystemInfo(&si);

    const std::uint8_t* p   = static_cast<const std::uint8_t*>(si.lpMinimumApplicationAddress);
    const std::uint8_t* end = static_cast<const std::uint8_t*>(si.lpMaximumApplicationAddress);

    while (p < end) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (::VirtualQuery(p, &mbi, sizeof(mbi)) == 0) break;

        const std::uint8_t* regionEnd =
            static_cast<const std::uint8_t*>(mbi.BaseAddress) + mbi.RegionSize;

        if (mbi.State == MEM_COMMIT &&
            !(mbi.Protect & PAGE_GUARD) &&
            !(mbi.Protect & PAGE_NOACCESS)) {
            CommittedRegion r{};
            r.base    = static_cast<const std::uint8_t*>(mbi.BaseAddress);
            r.size    = static_cast<std::size_t>(mbi.RegionSize);
            r.protect = mbi.Protect;
            r.type    = mbi.Type;
            out.push_back(r);
        }

        if (regionEnd <= p) break;
        p = regionEnd;
    }
    return out;
}

#pragma warning(push)
#pragma warning(disable: 4509)
namespace {
// Inner SEH helper: scans one memory region for an aligned pointer match.
// Out parameter `matches` is appended to. Returns false on AV (region skipped).
// A match whose address lands in [excludeLow, excludeHigh) (when the range is
// non-empty) is dropped without counting toward `limit`; such hits are tallied
// into `excludedMatches` so callers can report how much arena residue was
// filtered.
bool SehScanRegion(const std::uint8_t* base, std::size_t size,
                   std::uintptr_t target,
                   std::vector<const void*>& matches,
                   std::size_t limit,
                   std::uintptr_t excludeLow, std::uintptr_t excludeHigh,
                   std::size_t& excludedMatches) {
    __try {
        const bool hasExclude = excludeHigh > excludeLow;
        const std::size_t count = size / sizeof(std::uintptr_t);
        const std::uintptr_t* arr = reinterpret_cast<const std::uintptr_t*>(base);
        for (std::size_t i = 0; i < count; ++i) {
            if (arr[i] == target) {
                auto addr = reinterpret_cast<std::uintptr_t>(base + i * sizeof(std::uintptr_t));
                if (hasExclude && addr >= excludeLow && addr < excludeHigh) {
                    ++excludedMatches;
                    continue;
                }
                matches.push_back(reinterpret_cast<const void*>(addr));
                if (matches.size() >= limit) return true;
            }
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
}  // namespace
#pragma warning(pop)

// ---------- Thread-stack enumeration ----------------------------------------
//
// Windows exposes per-thread stack base/limit via the TEB. Locating each
// thread's TEB requires `NtQueryInformationThread(ThreadBasicInformation)` —
// an ntdll export that's undocumented in winnt.h but ABI-stable across every
// Win32 release. We dynamically resolve it so the rest of the codebase doesn't
// need to link against ntdll's import lib.
//
// Each thread's TEB starts with an NT_TIB:
//   +0           ExceptionList
//   +sizeof(ptr) StackBase     (high end of the committed stack)
//   +2*sizeof(ptr) StackLimit  (low end)
// On x64: StackBase at +0x08, StackLimit at +0x10.
// On x86: StackBase at +0x04, StackLimit at +0x08.
// [StackLimit, StackBase) is the currently committed stack — exactly what we
// need to exclude. (The reserved-but-not-yet-committed pages below StackLimit
// have PAGE_GUARD and are already skipped by EnumerateCommittedRegions.)
//
// Phase 12d Legacy-Mono smoke (Endless Legend / LEAVES, 2026-05-19) surfaced
// that the offsets were hardcoded x64 — on x86 we read random TEB fields and
// produced bogus "stack" ranges that filtered out heap regions where
// instances lived. `findInstances` returned 0 matches for x86 legacy-Mono
// targets despite the class_ptr resolving cleanly. Scaling with
// sizeof(void*) makes the offsets arch-correct without per-arch branches.

namespace {

struct ClientId {
    HANDLE UniqueProcess;
    HANDLE UniqueThread;
};

struct ThreadBasicInformation {
    long       ExitStatus;
    void*      TebBaseAddress;
    ClientId   ClientId;
    std::uintptr_t AffinityMask;
    long       Priority;
    long       BasePriority;
};

using NtQueryInformationThreadFn = LONG(NTAPI*)(HANDLE, int, void*, ULONG, ULONG*);

NtQueryInformationThreadFn ResolveNtQueryInformationThread() noexcept {
    static NtQueryInformationThreadFn cached = nullptr;
    static bool tried = false;
    if (!tried) {
        tried = true;
        HMODULE nt = ::GetModuleHandleW(L"ntdll.dll");
        if (nt) {
            cached = reinterpret_cast<NtQueryInformationThreadFn>(
                ::GetProcAddress(nt, "NtQueryInformationThread"));
        }
    }
    return cached;
}

constexpr int kThreadBasicInformation = 0;

}  // namespace

std::vector<ThreadStackRange> CollectThreadStackRanges() {
    std::vector<ThreadStackRange> out;
    auto query = ResolveNtQueryInformationThread();
    if (!query) return out;  // graceful degradation — caller scans without filtering

    HANDLE snap = ::CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return out;

    const DWORD ownPid = ::GetCurrentProcessId();
    THREADENTRY32 te{};
    te.dwSize = sizeof(te);

    if (::Thread32First(snap, &te)) {
        do {
            if (te.dwSize >= FIELD_OFFSET(THREADENTRY32, th32OwnerProcessID) + sizeof(te.th32OwnerProcessID) &&
                te.th32OwnerProcessID == ownPid) {
                HANDLE th = ::OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, te.th32ThreadID);
                if (th) {
                    ThreadBasicInformation tbi{};
                    if (query(th, kThreadBasicInformation, &tbi, sizeof(tbi), nullptr) == 0 && tbi.TebBaseAddress) {
                        // NT_TIB layout: ExceptionList(+0), StackBase(+ptr),
                        // StackLimit(+2*ptr). Offsets scale with pointer width
                        // so x86 reads +0x04/+0x08 and x64 reads +0x08/+0x10
                        // from the same source. Read via SafeReadBytes so a
                        // thread exiting mid-enum doesn't AV us.
                        constexpr std::size_t kStackBaseOffset  = sizeof(void*);
                        constexpr std::size_t kStackLimitOffset = 2 * sizeof(void*);
                        void* stackBase = nullptr;
                        void* stackLimit = nullptr;
                        auto* teb = static_cast<const std::uint8_t*>(tbi.TebBaseAddress);
                        if (SafeReadBytes(teb + kStackBaseOffset,  &stackBase,  sizeof(stackBase)) &&
                            SafeReadBytes(teb + kStackLimitOffset, &stackLimit, sizeof(stackLimit)) &&
                            stackBase && stackLimit && stackLimit < stackBase) {
                            ThreadStackRange r{};
                            r.low  = static_cast<const std::uint8_t*>(stackLimit);
                            r.high = static_cast<const std::uint8_t*>(stackBase);
                            out.push_back(r);
                        }
                    }
                    ::CloseHandle(th);
                }
            }
            te.dwSize = sizeof(te);
        } while (::Thread32Next(snap, &te));
    }
    ::CloseHandle(snap);

    // Sort by `low` so the overlap-check in ScanForPointer can short-circuit.
    std::sort(out.begin(), out.end(), [](const ThreadStackRange& a, const ThreadStackRange& b) {
        return a.low < b.low;
    });
    return out;
}

namespace {

// Half-open overlap test: do [aLow, aHigh) and [bLow, bHigh) share any byte?
bool RangesOverlap(const std::uint8_t* aLow, const std::uint8_t* aHigh,
                   const std::uint8_t* bLow, const std::uint8_t* bHigh) noexcept {
    return aLow < bHigh && bLow < aHigh;
}

// ===================================================================
// Phase P1 — parallel region scan infrastructure (shared by
// ScanForPointer / ScanForValue / ScanForString).
//
// Design (see docs/Memory-Scanning-Internals.md §16):
//   * Serial pass: enumerate + filter committed regions (RW, non-image,
//     non-stack, optionally non-mapped) and accumulate region-level stats.
//   * Split accepted regions into a worklist of fixed-size chunks. Chunk
//     boundaries are aligned to the scan `stride` so the per-position grid
//     stays continuous across chunk seams; each chunk's `scanSize` includes
//     a tail overlap of `elemSize-1` bytes (clamped to region end) so an
//     element straddling a chunk seam is still found exactly once — owned
//     by the chunk that contains its START position.
//   * N worker threads grab chunks via an atomic index (work-stealing — the
//     dynamic grab balances tiny vs. huge regions). Each chunk writes its
//     matches into its own slot (perChunk[idx]) so the final concatenation
//     in index order yields address-ascending output with no sort. An
//     atomic match counter early-stops once `limit` is reached (the grabbed
//     set is always a contiguous low-index prefix → truncation keeps the
//     lowest-address matches, matching the old serial contract).
//
// No per-chunk IsReadable/VirtualQuery: the region list already excludes
// PAGE_GUARD/PAGE_NOACCESS, and the chunk scan runs inside __try so a rare
// AC-stripped page only loses that one chunk (counted into readFailureBytes).
// This removes one VirtualQuery syscall per 64 KB that the old serial path
// paid.
// ===================================================================

// One unit of scan work. Owns the positions starting in [base, base+ownLen);
// `scanSize` already folds in the tail overlap so the inner loop's
// `i + elemSize <= scanSize` condition reports exactly the owned positions.
struct ScanWorkItem {
    const std::uint8_t* base;
    std::size_t         scanSize;
};

// Target chunk size — balances work-stealing granularity (enough chunks to
// keep all threads busy) against per-chunk overhead and AV blast radius.
constexpr std::size_t kScanWorkChunk = 1u * 1024u * 1024u;   // 1 MB

// Split filtered regions into a stride-aligned chunk worklist.
//   elemSize = bytes read per candidate position (sizeof(T) / needle len / 8)
//   stride   = position step (alignment); chunk boundaries are multiples of it
std::vector<ScanWorkItem> BuildScanChunks(
    const std::vector<std::pair<const std::uint8_t*, std::size_t>>& regions,
    std::size_t elemSize, std::size_t stride) {
    std::vector<ScanWorkItem> chunks;
    if (elemSize == 0) return chunks;
    if (stride == 0) stride = 1;
    // Round the chunk granularity down to a multiple of stride so chunk start
    // offsets stay on the global position grid (rbase + k*stride).
    std::size_t C = (kScanWorkChunk / stride) * stride;
    if (C < stride) C = stride;
    for (const auto& [rbase, rsize] : regions) {
        if (rsize < elemSize) continue;
        for (std::size_t off = 0; off < rsize; off += C) {
            const std::uint8_t* base   = rbase + off;
            std::size_t         ownLen = std::min(C, rsize - off);
            // Allow reading elemSize-1 bytes past the owned range so a
            // straddling element is found, but never past the region end.
            std::size_t scanSize = std::min(ownLen + (elemSize - 1), rsize - off);
            if (scanSize < elemSize) continue;     // no full element fits
            chunks.push_back(ScanWorkItem{base, scanSize});
        }
    }
    return chunks;
}

// Generic parallel driver. `chunkScan(base, scanSize, localOut, cap)` runs the
// caller's SEH-wrapped inner loop for one chunk, appending matches to
// `localOut` (capped at `cap`), returning false if the chunk AV'd. Results
// come back in address-ascending order, truncated to `limit`.
template <typename ResultT, typename ChunkScanFn>
std::vector<ResultT> RunParallelScan(
    const std::vector<ScanWorkItem>& chunks,
    std::size_t                      limit,
    std::atomic<std::size_t>*        readFailureBytes,
    ChunkScanFn                      chunkScan) {
    std::vector<ResultT> out;
    if (chunks.empty() || limit == 0) return out;

    unsigned int nthreads = ScanThreadCount();
    if (nthreads > chunks.size()) nthreads = static_cast<unsigned int>(chunks.size());
    if (nthreads < 1) nthreads = 1;

    std::vector<std::vector<ResultT>> perChunk(chunks.size());
    std::atomic<std::size_t> nextChunk{0};
    std::atomic<std::size_t> totalMatches{0};

    auto worker = [&]() {
        for (;;) {
            if (totalMatches.load(std::memory_order_relaxed) >= limit) break;
            std::size_t idx = nextChunk.fetch_add(1, std::memory_order_relaxed);
            if (idx >= chunks.size()) break;
            auto& local = perChunk[idx];
            bool ok = chunkScan(chunks[idx].base, chunks[idx].scanSize, local, limit);
            if (!ok && readFailureBytes)
                readFailureBytes->fetch_add(chunks[idx].scanSize, std::memory_order_relaxed);
            if (!local.empty())
                totalMatches.fetch_add(local.size(), std::memory_order_relaxed);
        }
    };

    if (nthreads <= 1) {
        worker();
    } else {
        std::vector<std::thread> pool;
        pool.reserve(nthreads);
        for (unsigned int t = 0; t < nthreads; ++t) pool.emplace_back(worker);
        for (auto& th : pool) th.join();
    }

    out.reserve(std::min<std::size_t>(totalMatches.load(), limit));
    for (auto& l : perChunk) {
        for (auto& r : l) {
            if (out.size() >= limit) return out;
            out.push_back(r);
        }
    }
    return out;
}

// Serial region filter shared by the three scanners. Builds the accepted
// [base,size) region list and the region-level stat counters in one pass.
struct AcceptedRegions {
    std::vector<std::pair<const std::uint8_t*, std::size_t>> regions;
    std::size_t regionsTotal   = 0;
    std::size_t regionsScanned = 0;
    std::size_t skipImage      = 0;
    std::size_t skipMapped     = 0;
    std::size_t skipStack      = 0;
    std::size_t bytesScanned   = 0;
    std::size_t threadStacks   = 0;
};

AcceptedRegions FilterScanRegions(bool skipMapped) {
    AcceptedRegions a;
    constexpr DWORD kRW =
        PAGE_READWRITE | PAGE_WRITECOPY |
        PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    auto regions = EnumerateCommittedRegions();
    auto stacks  = CollectThreadStackRanges();
    a.threadStacks = stacks.size();
    a.regions.reserve(regions.size());
    for (const auto& r : regions) {
        if ((r.protect & kRW) == 0) continue;
        a.regionsTotal++;
        if (r.type == MEM_IMAGE) { a.skipImage++; continue; }
        if (skipMapped && r.type == MEM_MAPPED) { a.skipMapped++; continue; }
        const std::uint8_t* regionEnd = r.base + r.size;
        bool onStack = false;
        for (const auto& s : stacks) {
            if (s.low >= regionEnd) break;
            if (RangesOverlap(r.base, regionEnd, s.low, s.high)) { onStack = true; break; }
        }
        if (onStack) { a.skipStack++; continue; }
        a.regionsScanned++;
        a.bytesScanned += r.size;
        a.regions.emplace_back(r.base, r.size);
    }
    return a;
}

}  // namespace

// Worker thread count: leave 2 cores for the game + our own pipe/UI threads,
// clamp to a sane upper bound (memory-bandwidth-bound past ~8-16 threads).
// Exported so the IL2CPP snapshot heap walk (FindAllInstances) reuses the
// same policy.
unsigned int ScanThreadCount() noexcept {
    unsigned int hc = std::thread::hardware_concurrency();
    if (hc == 0) hc = 4;
    unsigned int t = (hc > 2) ? (hc - 2) : 1;
    if (t > 16) t = 16;
    return t;
}

std::vector<const void*> ScanForPointer(std::uintptr_t target, std::size_t limit, ScanStats* stats,
                                        std::uintptr_t excludeLow, std::uintptr_t excludeHigh,
                                        bool skipMapped) {
    if (target == 0 || limit == 0) return {};

    // Filter 1: module sections (.data / .bss) hold class-pointer values
    // (IL2CPP RGCTX cache, Mono class tables) → skipped. Filter 1b: file-backed
    // mappings (opt-in). Filter 2: thread stacks (stale `T*` locals).
    AcceptedRegions acc = FilterScanRegions(skipMapped);
    if (stats) {
        stats->regionsTotal      = acc.regionsTotal;
        stats->regionsScanned    = acc.regionsScanned;
        stats->regionsSkipImage  = acc.skipImage;
        stats->regionsSkipMapped = acc.skipMapped;
        stats->regionsSkipStack  = acc.skipStack;
        stats->bytesScanned      = acc.bytesScanned;
        stats->threadStacks      = acc.threadStacks;
    }

    // Object headers are 8-byte aligned; scan at qword stride.
    auto chunks = BuildScanChunks(acc.regions, sizeof(std::uintptr_t), sizeof(std::uintptr_t));

    std::atomic<std::size_t> excludedMatches{0};
    auto matches = RunParallelScan<const void*>(
        chunks, limit, /*readFailureBytes=*/nullptr,
        [target, excludeLow, excludeHigh, &excludedMatches](
            const std::uint8_t* base, std::size_t scanSize,
            std::vector<const void*>& local, std::size_t cap) -> bool {
            std::size_t exc = 0;   // per-call (per-thread) — no shared-state race
            bool ok = SehScanRegion(base, scanSize, target, local, cap,
                                    excludeLow, excludeHigh, exc);
            if (exc) excludedMatches.fetch_add(exc, std::memory_order_relaxed);
            return ok;
        });
    if (stats) stats->excludedMatches = excludedMatches.load();
    return matches;
}

// ---------- Phase 27a — Typed value scan -----------------------------------
//
// Reuses the same MEM_IMAGE + thread-stack filtering as `ScanForPointer`. Each
// region is scanned by a typed inner loop dispatched once on `req.dataType` so
// the per-byte hot path stays branch-free. Phase P1: the region walk runs
// across N worker threads via `RunParallelScan` (see the parallel-scan infra
// near the top of this file); each 1 MB chunk's typed loop is wrapped in __try
// so an AC-stripped page only loses that chunk (counted in readFailureBytes).

namespace {

// Read the lower N bytes of an 8-byte payload as type T.
template <typename T>
inline T LoadPayload(const std::uint8_t* payload) noexcept {
    static_assert(std::is_trivially_copyable_v<T>, "T must be trivially copyable");
    T v{};
    ::memcpy(&v, payload, sizeof(T));
    return v;
}

template <typename T>
inline bool MatchExactInt(const std::uint8_t* p, const std::uint8_t* lo) noexcept {
    T candidate{};
    ::memcpy(&candidate, p, sizeof(T));
    T target = LoadPayload<T>(lo);
    return candidate == target;
}

template <typename T>
inline bool MatchRangeInt(const std::uint8_t* p, const std::uint8_t* lo, const std::uint8_t* hi) noexcept {
    T candidate{};
    ::memcpy(&candidate, p, sizeof(T));
    T tlo = LoadPayload<T>(lo);
    T thi = LoadPayload<T>(hi);
    return candidate >= tlo && candidate <= thi;
}

template <typename T>
inline bool MatchExactFloat(const std::uint8_t* p, const std::uint8_t* lo, double tol) noexcept {
    T candidate{};
    ::memcpy(&candidate, p, sizeof(T));
    if (!std::isfinite(static_cast<double>(candidate))) return false;
    T target = LoadPayload<T>(lo);
    return std::fabs(static_cast<double>(candidate) - static_cast<double>(target)) <= tol;
}

template <typename T>
inline bool MatchRangeFloat(const std::uint8_t* p, const std::uint8_t* lo, const std::uint8_t* hi) noexcept {
    T candidate{};
    ::memcpy(&candidate, p, sizeof(T));
    if (!std::isfinite(static_cast<double>(candidate))) return false;
    T tlo = LoadPayload<T>(lo);
    T thi = LoadPayload<T>(hi);
    return candidate >= tlo && candidate <= thi;
}

// Per-dataType inner loop. Walks [base, base+size) at `stride` and appends
// matches with their raw value bytes. Bounded by `limit`.
template <typename T, bool IsRange, bool IsFloat>
void ScanChunk(const std::uint8_t* base,
               std::size_t          size,
               const ValueScanRequest& req,
               std::vector<ValueScanCandidate>& matches,
               std::size_t          limit) {
    const std::size_t stride = req.alignment ? req.alignment : sizeof(T);
    if (size < sizeof(T)) return;
    for (std::size_t i = 0; i + sizeof(T) <= size; i += stride) {
        const std::uint8_t* p = base + i;
        bool hit = false;
        if constexpr (IsFloat) {
            if constexpr (IsRange) hit = MatchRangeFloat<T>(p, req.lo, req.hi);
            else                   hit = MatchExactFloat<T>(p, req.lo, req.tolerance);
        } else {
            if constexpr (IsRange) hit = MatchRangeInt<T>(p, req.lo, req.hi);
            else                   hit = MatchExactInt<T>(p, req.lo);
        }
        if (hit) {
            ValueScanCandidate c{};
            c.addr = p;
            ::memcpy(c.value, p, sizeof(T));
            matches.push_back(c);
            if (matches.size() >= limit) return;
        }
    }
}

// Dispatch on dataType + scanType. Range/Exact and Int/Float branches are
// resolved at template-time so the hot loop has zero per-byte branching.
// Non-noexcept: the caller wraps this in __try and we don't want noexcept
// fighting with the SEH unwind.
void DispatchScanChunk(const std::uint8_t* base,
                       std::size_t          size,
                       const ValueScanRequest& req,
                       std::vector<ValueScanCandidate>& matches,
                       std::size_t          limit) {
    const bool isRange = (req.scanType == ValueScanType::Range);
    switch (req.dataType) {
        case ValueDataType::Int8:
            isRange ? ScanChunk<std::int8_t,   true,  false>(base, size, req, matches, limit)
                    : ScanChunk<std::int8_t,   false, false>(base, size, req, matches, limit);
            break;
        case ValueDataType::Int16:
            isRange ? ScanChunk<std::int16_t,  true,  false>(base, size, req, matches, limit)
                    : ScanChunk<std::int16_t,  false, false>(base, size, req, matches, limit);
            break;
        case ValueDataType::Int32:
            isRange ? ScanChunk<std::int32_t,  true,  false>(base, size, req, matches, limit)
                    : ScanChunk<std::int32_t,  false, false>(base, size, req, matches, limit);
            break;
        case ValueDataType::Int64:
            isRange ? ScanChunk<std::int64_t,  true,  false>(base, size, req, matches, limit)
                    : ScanChunk<std::int64_t,  false, false>(base, size, req, matches, limit);
            break;
        case ValueDataType::UInt8:
            isRange ? ScanChunk<std::uint8_t,  true,  false>(base, size, req, matches, limit)
                    : ScanChunk<std::uint8_t,  false, false>(base, size, req, matches, limit);
            break;
        case ValueDataType::UInt16:
            isRange ? ScanChunk<std::uint16_t, true,  false>(base, size, req, matches, limit)
                    : ScanChunk<std::uint16_t, false, false>(base, size, req, matches, limit);
            break;
        case ValueDataType::UInt32:
            isRange ? ScanChunk<std::uint32_t, true,  false>(base, size, req, matches, limit)
                    : ScanChunk<std::uint32_t, false, false>(base, size, req, matches, limit);
            break;
        case ValueDataType::UInt64:
            isRange ? ScanChunk<std::uint64_t, true,  false>(base, size, req, matches, limit)
                    : ScanChunk<std::uint64_t, false, false>(base, size, req, matches, limit);
            break;
        case ValueDataType::Float:
            isRange ? ScanChunk<float,         true,  true >(base, size, req, matches, limit)
                    : ScanChunk<float,         false, true >(base, size, req, matches, limit);
            break;
        case ValueDataType::Double:
            isRange ? ScanChunk<double,        true,  true >(base, size, req, matches, limit)
                    : ScanChunk<double,        false, true >(base, size, req, matches, limit);
            break;
    }
}

// SEH wrapper around DispatchScanChunk. Lives in its own function so __try
// doesn't sit in the same frame as `std::vector` destructors (C4509).
#pragma warning(push)
#pragma warning(disable: 4509)
bool SehDispatchScanChunk(const std::uint8_t* base,
                          std::size_t          size,
                          const ValueScanRequest& req,
                          std::vector<ValueScanCandidate>& matches,
                          std::size_t          limit) {
    __try {
        DispatchScanChunk(base, size, req, matches, limit);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
#pragma warning(pop)

}  // namespace

std::vector<ValueScanCandidate> ScanForValue(const ValueScanRequest& req,
                                              ValueScanStats*         stats) noexcept {
    if (req.limit == 0) return {};
    const std::size_t typeSize = ValueDataTypeSize(req.dataType);
    if (typeSize == 0) return {};

    // Value scan keeps MEM_MAPPED regions (game data the user may search) —
    // only MEM_IMAGE + thread stacks are filtered.
    AcceptedRegions acc = FilterScanRegions(/*skipMapped=*/false);
    if (stats) {
        stats->regionsTotal     = acc.regionsTotal;
        stats->regionsScanned   = acc.regionsScanned;
        stats->regionsSkipImage = acc.skipImage;
        stats->regionsSkipStack = acc.skipStack;
        stats->bytesScanned     = acc.bytesScanned;
        stats->threadStacks     = acc.threadStacks;
    }

    const std::size_t stride = req.alignment ? req.alignment : typeSize;
    auto chunks = BuildScanChunks(acc.regions, typeSize, stride);

    std::atomic<std::size_t> readFailureBytes{0};
    auto matches = RunParallelScan<ValueScanCandidate>(
        chunks, req.limit, &readFailureBytes,
        [&req](const std::uint8_t* base, std::size_t scanSize,
               std::vector<ValueScanCandidate>& local, std::size_t cap) -> bool {
            // __try-wrapped typed inner loop; a rare AC-stripped page only
            // loses this 1 MB chunk (no IsReadable pre-probe — the region list
            // already excludes guard/no-access pages).
            return SehDispatchScanChunk(base, scanSize, req, local, cap);
        });
    if (stats) stats->readFailureBytes = readFailureBytes.load();
    return matches;
}

// ---------- Phase 27b-4 — String (byte-sequence) scan -----------------------
//
// CE-style UTF-16 / UTF-8 / ASCII search. The caller pre-encodes the
// needle into bytes (e.g. wstring → UTF-16LE bytes); this scanner just
// looks for that byte sequence in committed RW memory.

namespace {

bool SehMemcmpProbe(const std::uint8_t* base, std::size_t size,
                    const std::uint8_t* needle, std::size_t needleSize,
                    std::size_t stride,
                    std::vector<StringScanCandidate>& matches,
                    std::size_t limit) {
    if (size < needleSize) return true;
    const std::size_t end = size - needleSize;
    for (std::size_t i = 0; i <= end; i += stride) {
        if (std::memcmp(base + i, needle, needleSize) == 0) {
            StringScanCandidate c{};
            c.addr   = base + i;
            c.length = needleSize;
            matches.push_back(c);
            if (matches.size() >= limit) return true;
        }
    }
    return true;
}

#pragma warning(push)
#pragma warning(disable: 4509)
bool SehScanStringChunk(const std::uint8_t* base, std::size_t size,
                        const std::uint8_t* needle, std::size_t needleSize,
                        std::size_t stride,
                        std::vector<StringScanCandidate>& matches,
                        std::size_t limit) {
    __try {
        return SehMemcmpProbe(base, size, needle, needleSize, stride, matches, limit);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
#pragma warning(pop)

}  // namespace

std::vector<StringScanCandidate> ScanForString(const StringScanRequest& req,
                                                ValueScanStats* stats) noexcept {
    if (req.needle.empty() || req.limit == 0) return {};

    std::size_t stride = req.alignment;
    if (stride == 0) {
        stride = (req.encoding == StringEncoding::Utf16Le) ? 2u : 1u;
    }

    AcceptedRegions acc = FilterScanRegions(/*skipMapped=*/false);
    if (stats) {
        stats->regionsTotal     = acc.regionsTotal;
        stats->regionsScanned   = acc.regionsScanned;
        stats->regionsSkipImage = acc.skipImage;
        stats->regionsSkipStack = acc.skipStack;
        stats->bytesScanned     = acc.bytesScanned;
        stats->threadStacks     = acc.threadStacks;
    }

    // elemSize = needle length; BuildScanChunks' tail overlap (needleSize-1)
    // catches needles straddling a chunk seam — reported once, owned by the
    // chunk holding the needle's first byte.
    auto chunks = BuildScanChunks(acc.regions, req.needle.size(), stride);

    const std::uint8_t* needle     = req.needle.data();
    const std::size_t   needleSize = req.needle.size();
    std::atomic<std::size_t> readFailureBytes{0};
    auto matches = RunParallelScan<StringScanCandidate>(
        chunks, req.limit, &readFailureBytes,
        [needle, needleSize, stride](
            const std::uint8_t* base, std::size_t scanSize,
            std::vector<StringScanCandidate>& local, std::size_t cap) -> bool {
            return SehScanStringChunk(base, scanSize, needle, needleSize, stride, local, cap);
        });
    if (stats) stats->readFailureBytes = readFailureBytes.load();
    return matches;
}

}  // namespace Discrete::Memory
