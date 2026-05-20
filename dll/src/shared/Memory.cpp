// Memory.cpp — implementation of safe memory ops with SEH wrappers.

#include "Memory.h"

#include <windows.h>
#include <tlhelp32.h>
#include <algorithm>
#include <cmath>

namespace UnityCEDumper::Memory {

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
bool SehScanRegion(const std::uint8_t* base, std::size_t size,
                   std::uintptr_t target,
                   std::vector<const void*>& matches,
                   std::size_t limit) {
    __try {
        const std::size_t count = size / sizeof(std::uintptr_t);
        const std::uintptr_t* arr = reinterpret_cast<const std::uintptr_t*>(base);
        for (std::size_t i = 0; i < count; ++i) {
            if (arr[i] == target) {
                matches.push_back(static_cast<const void*>(base + i * sizeof(std::uintptr_t)));
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

}  // namespace

std::vector<const void*> ScanForPointer(std::uintptr_t target, std::size_t limit, ScanStats* stats) {
    std::vector<const void*> matches;
    if (target == 0 || limit == 0) return matches;

    constexpr DWORD kRW =
        PAGE_READWRITE | PAGE_WRITECOPY |
        PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;

    auto regions = EnumerateCommittedRegions();
    auto stacks  = CollectThreadStackRanges();

    if (stats) {
        stats->threadStacks = stacks.size();
    }

    for (const auto& r : regions) {
        if (matches.size() >= limit) break;
        // Heap allocations live in RW pages. Skip RO / X-only regions to cut
        // noise (and avoid scanning module .text / .rdata sections).
        if ((r.protect & kRW) == 0) continue;

        if (stats) stats->regionsTotal++;

        // Filter 1: module sections (.data / .bss / etc.). These legitimately
        // store class-pointer values (IL2CPP RGCTX cache, Mono class tables)
        // and would otherwise be reported as instance matches.
        if (r.type == MEM_IMAGE) {
            if (stats) stats->regionsSkipImage++;
            continue;
        }

        // Filter 2: thread stacks. Stale `T*` locals from old frames pass the
        // first-qword check until overwritten.
        const std::uint8_t* regionEnd = r.base + r.size;
        bool onStack = false;
        for (const auto& s : stacks) {
            if (s.low >= regionEnd) break;  // sorted — no further overlaps possible
            if (RangesOverlap(r.base, regionEnd, s.low, s.high)) {
                onStack = true;
                break;
            }
        }
        if (onStack) {
            if (stats) stats->regionsSkipStack++;
            continue;
        }

        if (stats) {
            stats->regionsScanned++;
            stats->bytesScanned += r.size;
        }
        SehScanRegion(r.base, r.size, target, matches, limit);
    }
    return matches;
}

// ---------- Phase 27a — Typed value scan -----------------------------------
//
// Single-threaded; reuses the same MEM_IMAGE + thread-stack filtering as
// `ScanForPointer`. Each region is scanned by a typed inner loop dispatched
// once on `req.dataType` so the per-byte hot path stays branch-free.
//
// Memory safety: instead of SEH-wrapping the whole inner loop, we walk the
// region in `SafeReadBytes`-validated chunks (4 KB). Any chunk that AVs is
// counted in `stats->readFailureBytes` and skipped. This is slightly slower
// than a single __try over the whole region (one extra `VirtualQuery` per
// chunk), but the chunked path lets a single bad page in the middle of a
// 16 MB region only cost us 4 KB of scan budget instead of the whole region.

namespace {

constexpr std::size_t kScanChunkBytes = 64 * 1024;   // 64 KB per SafeReadBytes probe

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
    std::vector<ValueScanCandidate> matches;
    if (req.limit == 0) return matches;
    const std::size_t typeSize = ValueDataTypeSize(req.dataType);
    if (typeSize == 0) return matches;
    matches.reserve(std::min<std::size_t>(req.limit, 4096));

    constexpr DWORD kRW =
        PAGE_READWRITE | PAGE_WRITECOPY |
        PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;

    auto regions = EnumerateCommittedRegions();
    auto stacks  = CollectThreadStackRanges();

    if (stats) stats->threadStacks = stacks.size();

    for (const auto& r : regions) {
        if (matches.size() >= req.limit) break;
        if ((r.protect & kRW) == 0) continue;

        if (stats) stats->regionsTotal++;

        if (r.type == MEM_IMAGE) {
            if (stats) stats->regionsSkipImage++;
            continue;
        }

        const std::uint8_t* regionEnd = r.base + r.size;
        bool onStack = false;
        for (const auto& s : stacks) {
            if (s.low >= regionEnd) break;
            if (RangesOverlap(r.base, regionEnd, s.low, s.high)) {
                onStack = true;
                break;
            }
        }
        if (onStack) {
            if (stats) stats->regionsSkipStack++;
            continue;
        }

        if (stats) {
            stats->regionsScanned++;
            stats->bytesScanned += r.size;
        }

        // Walk the region in chunks so a single bad page doesn't poison the
        // whole region's scan budget. The chunk size is also our SafeReadBytes
        // probe granularity — IsReadable does one VirtualQuery per call so
        // making chunks too small (4 KB) over-pays the syscall cost.
        std::size_t offset = 0;
        while (offset < r.size) {
            std::size_t chunkSize = std::min<std::size_t>(kScanChunkBytes, r.size - offset);
            const std::uint8_t* chunkBase = r.base + offset;

            // SafeReadBytes does VirtualQuery + SEH read. We only need the
            // VirtualQuery half — IsReadable — because we're scanning live
            // memory in-place, not copying. But IsReadable doesn't SEH-cover
            // the actual access pattern, so we still need __try around the
            // dispatch. Cheaper: use IsReadable as the cheap filter; the
            // typed inner loop runs without SEH (a SafeReadBytes copy would
            // double memory traffic).
            if (!IsReadable(chunkBase, chunkSize)) {
                if (stats) stats->readFailureBytes += chunkSize;
            } else {
                // Dispatch wrapped in helper-frame SEH so a hooked page that
                // passes IsReadable but AVs on access still doesn't kill us.
                if (!SehDispatchScanChunk(chunkBase, chunkSize, req, matches, req.limit)) {
                    if (stats) stats->readFailureBytes += chunkSize;
                }
            }

            if (matches.size() >= req.limit) break;
            offset += chunkSize;
        }
    }
    return matches;
}

}  // namespace UnityCEDumper::Memory
