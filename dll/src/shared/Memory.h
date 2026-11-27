// Memory.h — safe in-process memory primitives for backend code paths.
// All reads/writes operate on the host process's address space (the game we're injected into).
#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <optional>
#include <string_view>
#include <type_traits>
#include <vector>

namespace Discrete::Memory {

// Test whether [addr, addr+size) is fully readable (committed + non-PAGE_NOACCESS).
bool IsReadable(const void* addr, std::size_t size) noexcept;
bool IsWritable(const void* addr, std::size_t size) noexcept;

// SEH-protected raw read into dst. Returns false on AV; caller-supplied buffer untouched on failure.
bool SafeReadBytes(const void* addr, void* dst, std::size_t size) noexcept;
bool SafeWriteBytes(void* addr, const void* src, std::size_t size) noexcept;

// Like SafeReadBytes, but for HOT bursts of small reads from the SAME committed
// region (snapshot decode reads thousands of fields per GC-heap region). It
// consults a THREAD-LOCAL one-region readability cache so all-but-the-first read
// in a region skip the VirtualQuery syscall — which the snapshot decode-split
// telemetry showed was the dominant capture cost (~84%, ~tens of µs per call on a
// large/fragmented process). The SEH guard remains the real safety net: a stale
// cache only ever lets a bad read fall through to SEH (returns false), never an
// access beyond what SafeReadBytes already permits. Thread-local, so it composes
// with parallel scans. Use ONLY for read-heavy sequential-region decode paths.
bool SafeReadBytesSeq(const void* addr, void* dst, std::size_t size) noexcept;

// Typed read. Returns std::nullopt on AV / unreadable region.
template <typename T>
std::optional<T> SafeRead(const void* addr) noexcept {
    static_assert(std::is_trivially_copyable_v<T>, "SafeRead requires trivially copyable T");
    T value{};
    if (!SafeReadBytes(addr, &value, sizeof(T))) return std::nullopt;
    return value;
}

template <typename T>
bool SafeWrite(void* addr, const T& value) noexcept {
    static_assert(std::is_trivially_copyable_v<T>, "SafeWrite requires trivially copyable T");
    return SafeWriteBytes(addr, &value, sizeof(T));
}

// One committed memory region as reported by VirtualQueryEx.
struct CommittedRegion {
    const std::uint8_t* base;
    std::size_t         size;
    unsigned long       protect;   // PAGE_* protection mask
    unsigned long       type;      // MEM_IMAGE / MEM_MAPPED / MEM_PRIVATE
};

// Enumerates committed, readable, non-guarded regions in the host process.
std::vector<CommittedRegion> EnumerateCommittedRegions();

// One thread's stack range. `low` = StackLimit (current end of committed stack);
// `high` = StackBase (top of reserved stack). [low, high) covers everything that
// could plausibly be touched by the thread.
struct ThreadStackRange {
    const std::uint8_t* low;
    const std::uint8_t* high;
};

// Enumerates stack ranges for every live thread in the current process. Used to
// exclude stack residue from heap scans (findInstances false positives — stale
// `GameObject*` locals in old frames happen to pass the first-qword check).
std::vector<ThreadStackRange> CollectThreadStackRanges();

// Phase P1 — worker thread count for parallel heap scans. Leaves headroom for
// the game + our own threads and clamps to a memory-bandwidth-sane upper bound.
// Shared by the in-process scanners (Memory.cpp) and the IL2CPP snapshot heap
// walk (IL2CPPAddressFinder::FindAllInstances).
unsigned int ScanThreadCount() noexcept;

// Per-scan thread cap (2026-06-08; thread-local since A-L15). The Value Search
// "Parallel" toggle lets the user force single-threaded scanning on games whose
// anti-tamper reacts to many threads hammering memory in parallel, without
// touching the hardware default everywhere else. A scan handler opens a
// ScanThreadCapScope(cap) for its duration; the parallel drivers
// (RunParallelScan, IL2CPP::FindAllInstances, and IL2CPP::ReverseLookupIndex)
// consult EffectiveScanThreads() instead of ScanThreadCount().
//   cap == 0 → no cap (use ScanThreadCount, i.e. full parallel — the default).
//   cap >= 1 → clamp to that many threads (cap == 1 ⇒ serial single-thread).
// Thread-local so concurrent pipe clients never clobber each other's cap;
// ScanThreadCapScope saves and restores the previous cap on exit. NOTE: the
// Mono reverse-lookup index walk is ALWAYS single-threaded by design (BDWGC
// bandwidth-starvation freeze risk) regardless of this cap.
void         SetScanThreadCap(unsigned int cap) noexcept;
unsigned int GetScanThreadCap() noexcept;
unsigned int EffectiveScanThreads() noexcept;
struct ScanThreadCapScope {
    unsigned int prevCap_ = 0;
    explicit ScanThreadCapScope(unsigned int cap) noexcept
        : prevCap_(GetScanThreadCap()) {
        SetScanThreadCap(cap);
    }
    ~ScanThreadCapScope() noexcept { SetScanThreadCap(prevCap_); }
    ScanThreadCapScope(const ScanThreadCapScope&)            = delete;
    ScanThreadCapScope& operator=(const ScanThreadCapScope&) = delete;
};

// Telemetry for one ScanForPointer invocation (helps debug "where did the false
// positives go" without rebuilding with logging). Populated by ScanForPointer
// when caller passes a non-null pointer.
struct ScanStats {
    std::size_t regionsTotal      = 0;  // all committed RW regions enumerated
    std::size_t regionsScanned    = 0;  // regions actually scanned (after filters)
    std::size_t regionsSkipImage  = 0;  // skipped because mbi.Type == MEM_IMAGE
    std::size_t regionsSkipMapped = 0;  // skipped because mbi.Type == MEM_MAPPED (only when skipMapped)
    std::size_t regionsSkipStack  = 0;  // skipped because overlaps a thread stack
    std::size_t bytesScanned      = 0;
    std::size_t excludedMatches   = 0;  // qword==target hits dropped: addr inside [excludeLow, excludeHigh)
    std::size_t threadStacks      = 0;  // count of stack ranges collected
};

// Phase 27a — Unity-aware value scan support.
// Typed comparisons used by `ScanForValue` and the scanFieldValue RPC.
// Wire-side enums in [docs/pipe-protocol.md](../../docs/pipe-protocol.md);
// these mirror the C# `ScanDataType` / `ValueScanType` shape.
enum class ValueDataType : int {
    Int8 = 0, Int16, Int32, Int64,
    UInt8, UInt16, UInt32, UInt64,
    Float, Double,
};

inline constexpr std::size_t ValueDataTypeSize(ValueDataType t) noexcept {
    switch (t) {
        case ValueDataType::Int8:   case ValueDataType::UInt8:                                return 1;
        case ValueDataType::Int16:  case ValueDataType::UInt16:                               return 2;
        case ValueDataType::Int32:  case ValueDataType::UInt32:  case ValueDataType::Float:   return 4;
        case ValueDataType::Int64:  case ValueDataType::UInt64:  case ValueDataType::Double:  return 8;
    }
    return 0;
}

enum class ValueScanType : int {
    Exact = 0,
    Range,   // [lo, hi] inclusive; lo == hi == exact, but caller should pick Exact when possible
};

// One scan request. `lo` and `hi` are raw 8-byte payloads — the scanner
// reinterprets them as the actual dataType (so an Int32 request stores
// the value in the lower 4 bytes; Float in the lower 4 as IEEE-754; etc.).
// Caller fills via memcpy from the typed value.
struct ValueScanRequest {
    ValueDataType  dataType   = ValueDataType::Int32;
    ValueScanType  scanType   = ValueScanType::Exact;
    std::uint8_t   lo[8]      = {};
    std::uint8_t   hi[8]      = {};
    double         tolerance  = 0.0001;   // float/double epsilon (CE default)
    std::size_t    alignment  = 0;        // 0 = default (sizeof(dataType))
    std::size_t    limit      = 50000;    // cap on pre-filter hits (post-filter cap is caller's job)
};

struct ValueScanCandidate {
    const void*  addr;
    std::uint8_t value[8];   // raw current value (low N bytes valid, N = ValueDataTypeSize)
};

struct ValueScanStats {
    std::size_t regionsTotal     = 0;
    std::size_t regionsScanned   = 0;
    std::size_t regionsSkipImage = 0;
    std::size_t regionsSkipStack = 0;
    std::size_t bytesScanned     = 0;
    std::size_t readFailureBytes = 0;   // bytes inside regions where SEH read AV'd (AC-stripped)
    std::size_t threadStacks     = 0;
};

// Scans committed RW (non-image, non-stack) regions for primitive values
// matching `req`. Reuses the same MEM_IMAGE + thread-stack filtering as
// `ScanForPointer` so module .data / stack residue don't pollute results.
// Returns at most `req.limit` matches.
//
// The scan runs in-process across a bounded worker pool (or single-threaded
// when capped / on thread-creation fallback), with SEH protection covering each
// 1 MB chunk so AC-stripped pages don't crash the host. Failed chunks count
// toward `stats->readFailureBytes` without aborting the scan.
// May throw std::bad_alloc under severe memory pressure (caught by PipeServer).
//
// For float / double, comparison uses `std::fabs(value - target) <= tolerance`.
// For integers, exact bitwise comparison.
// For range, comparison is typed (lo and hi reinterpreted as the dataType).
std::vector<ValueScanCandidate> ScanForValue(const ValueScanRequest& req,
                                              ValueScanStats*         stats = nullptr);

// Convenience helpers — pack a typed value into the request's `lo` or `hi`
// bytes without the caller having to memcpy by hand.
template <typename T>
void SetScanValueLo(ValueScanRequest& req, T value) noexcept {
    static_assert(std::is_trivially_copyable_v<T>, "T must be trivially copyable");
    static_assert(sizeof(T) <= 8, "T too large for ValueScanRequest payload");
    ::memcpy(req.lo, &value, sizeof(T));
}

template <typename T>
void SetScanValueHi(ValueScanRequest& req, T value) noexcept {
    static_assert(std::is_trivially_copyable_v<T>, "T must be trivially copyable");
    static_assert(sizeof(T) <= 8, "T too large for ValueScanRequest payload");
    ::memcpy(req.hi, &value, sizeof(T));
}

// Phase 27b-4 — string scan support.
// CE's UTF-16 / UTF-8 / ASCII search. Unity managed strings are UTF-16
// length-prefixed (chars[] inline after the 16-byte object header), so
// the natural scan target is "find this UTF-16 byte sequence anywhere in
// committed RW memory" — that catches both managed `System.String`
// bodies AND unmanaged engine strings.
enum class StringEncoding : int {
    Utf16Le = 0,   // managed System.String / WCHAR_T payloads
    Utf8,
    Ascii,
};

enum class StringMatchMode : int {
    Exact = 0,     // full sequence equal (caller pre-encodes the bytes)
    Contains,      // substring search via the same byte sequence
};

struct StringScanRequest {
    StringEncoding   encoding   = StringEncoding::Utf16Le;
    StringMatchMode  matchMode  = StringMatchMode::Contains;
    std::vector<std::uint8_t> needle;        // pre-encoded payload bytes
    std::size_t      limit      = 5000;
    std::size_t      alignment  = 0;         // 0 = encoding's natural (UTF-16 → 2, others → 1)
};

struct StringScanCandidate {
    const void*  addr   = nullptr;
    std::size_t  length = 0;   // matched byte length (== needle.size())
};

// Scans committed RW pages (with the same MEM_IMAGE + thread-stack
// filtering as ScanForValue) for the needle byte sequence at the
// requested alignment. May throw std::bad_alloc under severe memory pressure.
std::vector<StringScanCandidate> ScanForString(const StringScanRequest& req,
                                                ValueScanStats* stats = nullptr);

// Pure in-memory span scan used by ScanForString (and offline unit tests).
// Scans `[base, base + size)` for `needle[0..needleSize)` at offsets
// `0, stride, 2*stride, ...` up to `limit` matches, appending to `matches`.
bool ScanStringSpan(const std::uint8_t* base, std::size_t size,
                    const std::uint8_t* needle, std::size_t needleSize,
                    std::size_t stride,
                    std::vector<StringScanCandidate>& matches,
                    std::size_t limit);

// Scans heap-like RW memory for pointer-sized values equal to `target`, at
// 8-byte alignment. Used by findInstances: in IL2CPP the first qword of an
// object is its Il2CppClass*; in Mono it's a MonoVTable*. Returns at most
// `limit` matches (use SIZE_MAX for unbounded).
//
// **Filters applied (Phase 23 — managed-heap convergence):**
//   • Skip `MEM_IMAGE` regions (DLL/EXE `.data` / `.bss` — these hold IL2CPP
//     RGCTX cache entries and Mono's class/vtable tables, which legitimately
//     store class-pointer values at +0x00 and would otherwise be false-positive
//     instance matches).
//   • Skip regions overlapping any live thread's stack (stale local-variable
//     pointers from old frames survive until overwritten by deeper calls).
//
// **Address-exclusion range (Phase 29h — findInstances metadata-arena filter):**
//   When `excludeHigh > excludeLow`, any match whose *address* lies inside
//   `[excludeLow, excludeHigh)` is dropped and does NOT count toward `limit`.
//   IL2CPP findInstances passes the Il2CppClass* metadata-arena span here so
//   that class self-references (element_class/castClass) and FieldInfo[].parent
//   slots — which legitimately hold the class pointer but are not heap objects —
//   stop crowding out real instances. Because excluded hits don't fill the
//   quota, the scan keeps walking past the low-address arena up to the GC heap
//   regions where genuine instances live (the early break then fires only on
//   `limit` *validated* matches).
//
// **`skipMapped`:** when true, also skip `MEM_MAPPED` regions (file-backed
//   game-data mappings that can contain class-pointer values). Mirrors the
//   Phase 29 snapshot sweep (FindAllInstances).
//
// Passing a non-null `stats` populates per-call telemetry.
std::vector<const void*> ScanForPointer(std::uintptr_t target,
                                        std::size_t    limit       = 256,
                                        ScanStats*     stats       = nullptr,
                                        std::uintptr_t excludeLow  = 0,
                                        std::uintptr_t excludeHigh = 0,
                                        bool           skipMapped  = false);

}  // namespace Discrete::Memory
