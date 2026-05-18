// Memory.cpp — implementation of safe memory ops with SEH wrappers.

#include "Memory.h"

#include <windows.h>
#include <tlhelp32.h>
#include <algorithm>

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
// Each thread's TEB layout (x64): StackBase at +0x08, StackLimit at +0x10.
// [StackLimit, StackBase) is the currently committed stack — exactly what we
// need to exclude. (The reserved-but-not-yet-committed pages below StackLimit
// have PAGE_GUARD and are already skipped by EnumerateCommittedRegions.)

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
                        // TEB layout: +0x08 = StackBase (high), +0x10 = StackLimit (low).
                        // Read via SafeReadBytes so a thread exiting mid-enum doesn't AV us.
                        void* stackBase = nullptr;
                        void* stackLimit = nullptr;
                        auto* teb = static_cast<const std::uint8_t*>(tbi.TebBaseAddress);
                        if (SafeReadBytes(teb + 0x08, &stackBase, sizeof(stackBase)) &&
                            SafeReadBytes(teb + 0x10, &stackLimit, sizeof(stackLimit)) &&
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

}  // namespace UnityCEDumper::Memory
