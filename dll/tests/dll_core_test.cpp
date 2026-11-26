// dll_core_test.cpp — offline C++ self-tests for the Discrete DLL core.
//
// Built and executed on BOTH x64 and x86 (under WOW64) by `build.cmd test` and
// `build.cmd publish`. Zero external test-framework dependencies; exits 0 when
// every check passes and 1 on the first failure (or summary failure).
//
// Coverage:
//   * C-H1 — NormalizeFieldOffset ([ThreadStatic] SIZE_MAX / uint32 max / negative / normal)
//   * C-H2 — ResolveArrayElementLayout (reference-type stride never overwritten by class_value_size)
//   * C-H3 — DecodeIl2CppFieldDefaultByTag & ClrTypeNameToIl2CppTag (v24/v27 raw LE + v29+ sentinels/compressed)
//   * A-H2 — ScanForValue / ScanForString non-noexcept + RunWorkerPool spawn-failure & worker-exception safety
//            + live ScanForValue / ScanForString over VirtualAlloc RW pages
//   * B-H1 — MonoVtableFilter x86 4-mod-8 IMT-prefixed vtable acceptance vs x64 8-byte alignment

#include "ExportHelpers.h"
#include "ManagedLayout.h"
#include "Memory.h"
#include "MonoVtableFilter.h"
#include "ParallelScanDriver.h"

#include <windows.h>

#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <new>
#include <stdexcept>
#include <string>
#include <system_error>
#include <thread>
#include <type_traits>
#include <vector>

namespace {

int g_checks   = 0;
int g_failures = 0;

void CheckImpl(bool cond, const char* expr, const char* file, int line) {
    ++g_checks;
    if (!cond) {
        ++g_failures;
        std::fprintf(stderr, "FAIL %s:%d: %s\n", file, line, expr);
    }
}

#define CHECK(expr) CheckImpl(static_cast<bool>(expr), #expr, __FILE__, __LINE__)

// ============================================================================
// C-H1 — Field offset normalization ([ThreadStatic] SIZE_MAX -> -1)
// ============================================================================
void Test_CH1_NormalizeFieldOffset() {
    using Discrete::ExportHelpers::NormalizeFieldOffset;

    // Normal offsets stay intact across signed and unsigned types.
    CHECK(NormalizeFieldOffset(std::size_t{0}) == 0);
    CHECK(NormalizeFieldOffset(std::size_t{8}) == 8);
    CHECK(NormalizeFieldOffset(std::size_t{0x1C0}) == 0x1C0);
    CHECK(NormalizeFieldOffset(std::size_t{0x7FFFFFFFu}) == 0x7FFFFFFFLL);
    CHECK(NormalizeFieldOffset(0) == 0);
    CHECK(NormalizeFieldOffset(24) == 24);
    CHECK(NormalizeFieldOffset(INT32_MAX) == INT32_MAX);

    // Unsigned [ThreadStatic] sentinels normalize to -1 on both x64 and x86.
    CHECK(NormalizeFieldOffset(static_cast<std::size_t>(-1)) == -1);
    CHECK(NormalizeFieldOffset(static_cast<std::size_t>(0xFFFFFFFFu)) == -1);
    CHECK(NormalizeFieldOffset(static_cast<std::size_t>(0x80000000u)) == -1);
    CHECK(NormalizeFieldOffset(std::uint64_t{0xFFFFFFFFFFFFFFFFULL}) == -1);
    CHECK(NormalizeFieldOffset(std::uint64_t{0x00000000FFFFFFFFULL}) == -1);
    CHECK(NormalizeFieldOffset(std::uint64_t{0x0000000100000000ULL}) == -1);

    // Signed negative or oversized values normalize to -1.
    CHECK(NormalizeFieldOffset(-1) == -1);
    CHECK(NormalizeFieldOffset(-16) == -1);
    CHECK(NormalizeFieldOffset(INT32_MIN) == -1);
    CHECK(NormalizeFieldOffset(std::int64_t{-1}) == -1);
    CHECK(NormalizeFieldOffset(std::int64_t{0x80000000LL}) == -1);
}

// ============================================================================
// C-H2 — walkArray element layout (reference types never take class_value_size)
// ============================================================================
void Test_CH2_ResolveArrayElementLayout() {
    using Discrete::ExportHelpers::ResolveArrayElementLayout;

    // Reference-type class (e.g. UserClass[]) where runtime class_value_size
    // returns instance_size - header (0x30): stride MUST be pointerSize, not 0x30.
    {
        auto x64 = ResolveArrayElementLayout(
            /*knownPrimitiveSize=*/0, /*elemClassResolved=*/true,
            /*elemIsValueType=*/false, /*classValueSize=*/0x30,
            /*hasCallerElementSize=*/false, /*callerElementSize=*/0,
            /*pointerSize=*/8);
        CHECK(x64.isPointer == true);
        CHECK(x64.elemSize == 8);

        auto x86 = ResolveArrayElementLayout(
            0, true, false, 0x30, false, 0, 4);
        CHECK(x86.isPointer == true);
        CHECK(x86.elemSize == 4);

        // Even if caller supplied a bogus elementSize hint, resolved ref type wins.
        auto refWithHint = ResolveArrayElementLayout(
            0, true, false, 0x48, true, 64, 8);
        CHECK(refWithHint.isPointer == true);
        CHECK(refWithHint.elemSize == 8);
    }

    // Value-type struct (e.g. UnityEngine.Vector3[], 12 bytes) sits inline.
    {
        auto vec3 = ResolveArrayElementLayout(
            0, true, true, 12, false, 0, 8);
        CHECK(vec3.isPointer == false);
        CHECK(vec3.elemSize == 12);
    }

    // Enum (value type, 4 bytes) sits inline.
    {
        auto e = ResolveArrayElementLayout(
            0, true, true, 4, false, 0, 8);
        CHECK(e.isPointer == false);
        CHECK(e.elemSize == 4);
    }

    // Primitive (System.Int32) resolved or unresolved keeps width 4, non-pointer.
    {
        auto primResolved = ResolveArrayElementLayout(4, true, true, 4, false, 0, 8);
        CHECK(primResolved.isPointer == false);
        CHECK(primResolved.elemSize == 4);

        auto primUnresolved = ResolveArrayElementLayout(4, false, false, 0, false, 0, 8);
        CHECK(primUnresolved.isPointer == false);
        CHECK(primUnresolved.elemSize == 4);
    }

    // Unresolved class with no hint defaults to pointer slot of current arch width.
    {
        auto fallback = ResolveArrayElementLayout(0, false, false, 0, false, 0);
        CHECK(fallback.isPointer == true);
        CHECK(fallback.elemSize == Discrete::ManagedLayout::kPointerSize);
    }

    // Unresolved struct with explicit caller elementSize uses caller hint inline.
    {
        auto hinted = ResolveArrayElementLayout(0, false, false, 0, true, 24, 8);
        CHECK(hinted.isPointer == false);
        CHECK(hinted.elemSize == 24);
    }

    // Unresolved with caller elementSize=0 safely falls back to pointer slot.
    {
        auto zeroHint = ResolveArrayElementLayout(0, false, false, 0, true, 0, 4);
        CHECK(zeroHint.isPointer == true);
        CHECK(zeroHint.elemSize == 4);
    }
}

// ============================================================================
// C-H3 — IL2CPP FieldDefault decoder (v24/v27 raw LE vs v29+ sentinels/compressed)
// ============================================================================
void Test_CH3_Il2CppFieldDefaultDecoder() {
    using Discrete::ExportHelpers::ClrTypeNameToIl2CppTag;
    using Discrete::ExportHelpers::DecodeIl2CppFieldDefaultByTag;
    using nlohmann::json;

    // ClrTypeNameToIl2CppTag mapping.
    CHECK(ClrTypeNameToIl2CppTag("System.Boolean") == 0x02);
    CHECK(ClrTypeNameToIl2CppTag("System.Char")    == 0x03);
    CHECK(ClrTypeNameToIl2CppTag("System.SByte")   == 0x04);
    CHECK(ClrTypeNameToIl2CppTag("System.Byte")    == 0x05);
    CHECK(ClrTypeNameToIl2CppTag("System.Int16")   == 0x06);
    CHECK(ClrTypeNameToIl2CppTag("System.UInt16")  == 0x07);
    CHECK(ClrTypeNameToIl2CppTag("System.Int32")   == 0x08);
    CHECK(ClrTypeNameToIl2CppTag("System.UInt32")  == 0x09);
    CHECK(ClrTypeNameToIl2CppTag("System.Int64")   == 0x0A);
    CHECK(ClrTypeNameToIl2CppTag("System.UInt64")  == 0x0B);
    CHECK(ClrTypeNameToIl2CppTag("System.Single")  == 0x0C);
    CHECK(ClrTypeNameToIl2CppTag("System.Double")  == 0x0D);
    CHECK(ClrTypeNameToIl2CppTag("System.String")  == 0x00);
    CHECK(ClrTypeNameToIl2CppTag("Custom.Unknown") == 0x00);

    // ---- v24 / v27 raw little-endian (Unity 2018-2020) ----
    for (std::int32_t ver : {24, 27}) {
        // Exact C-H3 repro: `01 00 00 00` for Int32 must decode as 1 (NOT -64).
        {
            const std::uint8_t blob[] = {0x01, 0x00, 0x00, 0x00};
            json out;
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x08, blob, sizeof(blob), out));
            CHECK(out["value"].get<std::int64_t>() == 1);
            CHECK(out["enc_bytes"].get<std::size_t>() == 4);
            CHECK(out["encoding"].get<std::string>() == "raw_le");
        }
        // FF VI (Unity 2019.4) regression: `FF FF FF 7F` is Int32.MaxValue.
        {
            const std::uint8_t blob[] = {0xFF, 0xFF, 0xFF, 0x7F};
            json out;
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x08, blob, sizeof(blob), out));
            CHECK(out["value"].get<std::int64_t>() == 2147483647LL);
        }
        // Signed negative -1 (`FF FF FF FF`) vs unsigned max (`FF FF FF FF`).
        {
            const std::uint8_t blob[] = {0xFF, 0xFF, 0xFF, 0xFF};
            json outI4, outU4;
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x08, blob, sizeof(blob), outI4));
            CHECK(outI4["value"].get<std::int64_t>() == -1);
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x09, blob, sizeof(blob), outU4));
            CHECK(outU4["value"].get<std::uint64_t>() == 4294967295ULL);
        }
        // SByte (-1) and Byte (255) raw 1-byte LE.
        {
            const std::uint8_t blob[] = {0xFF};
            json outI1, outU1;
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x04, blob, sizeof(blob), outI1));
            CHECK(outI1["value"].get<std::int64_t>() == -1);
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x05, blob, sizeof(blob), outU1));
            CHECK(outU1["value"].get<std::uint64_t>() == 255ULL);
        }
        // Truncated raw-LE buffer fails cleanly.
        {
            const std::uint8_t blob[] = {0x01, 0x00};
            json out;
            CHECK(!DecodeIl2CppFieldDefaultByTag(ver, 0x08, blob, sizeof(blob), out));
        }
    }

    // ---- v29+ sentinel + ECMA-335 compressed signed int ----
    for (std::int32_t ver : {29, 31, 39}) {
        // 0xFE = type_max, 0xFF = type_min (previously decode_error in getEnumValues).
        {
            const std::uint8_t maxB[] = {0xFE};
            const std::uint8_t minB[] = {0xFF};
            json outMax, outMin;
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x08, maxB, 1, outMax));
            CHECK(outMax["value"].get<std::int64_t>() == 2147483647LL);
            CHECK(outMax["sentinel"].get<std::string>() == "type_max");
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x08, minB, 1, outMin));
            CHECK(outMin["value"].get<std::int64_t>() == -2147483648LL);
            CHECK(outMin["sentinel"].get<std::string>() == "type_min");
        }
        // U4 sentinels: 0xFE -> 4294967295, 0xFF -> 0.
        {
            const std::uint8_t maxB[] = {0xFE};
            const std::uint8_t minB[] = {0xFF};
            json outMax, outMin;
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x09, maxB, 1, outMax));
            CHECK(outMax["value"].get<std::uint64_t>() == 4294967295ULL);
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x09, minB, 1, outMin));
            CHECK(outMin["value"].get<std::uint64_t>() == 0ULL);
        }
        // 1-byte compressed: 0x00 -> 0, 0x02 -> 1, 0x7F -> -1 (63 - 64).
        {
            const std::uint8_t b0[] = {0x00};
            const std::uint8_t b1[] = {0x02};
            const std::uint8_t bm1[] = {0x7F};
            json j0, j1, jm1;
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x08, b0, 1, j0));
            CHECK(j0["value"].get<std::int64_t>() == 0);
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x08, b1, 1, j1));
            CHECK(j1["value"].get<std::int64_t>() == 1);
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x08, bm1, 1, jm1));
            CHECK(jm1["value"].get<std::int64_t>() == -1);
        }
        // 2-byte compressed: 0x80 0x80 -> enc=0x0080 -> +64.
        {
            const std::uint8_t b2[] = {0x80, 0x80};
            json j2;
            CHECK(DecodeIl2CppFieldDefaultByTag(ver, 0x08, b2, 2, j2));
            CHECK(j2["value"].get<std::int64_t>() == 64);
            CHECK(j2["enc_bytes"].get<std::size_t>() == 2);
        }
        // Truncated 2-byte compressed fails cleanly.
        {
            const std::uint8_t b2trunc[] = {0x80};
            json out;
            CHECK(!DecodeIl2CppFieldDefaultByTag(ver, 0x08, b2trunc, 1, out));
        }
    }

    // ---- Version-independent types: I8, U8, R4 NaN/Inf ----
    {
        std::int64_t v64 = -123456789012345LL;
        std::uint8_t buf[8];
        std::memcpy(buf, &v64, 8);
        json out;
        CHECK(DecodeIl2CppFieldDefaultByTag(27, 0x0A, buf, 8, out));
        CHECK(out["value"].get<std::int64_t>() == v64);
    }
    {
        float inf = std::numeric_limits<float>::infinity();
        std::uint8_t buf[4];
        std::memcpy(buf, &inf, 4);
        json out;
        CHECK(DecodeIl2CppFieldDefaultByTag(31, 0x0C, buf, 4, out));
        CHECK(out["value"].is_null());
        CHECK(out["value_kind"].get<std::string>() == "infinity");
    }
}

// ============================================================================
// A-H2 — Non-noexcept scanners + RunWorkerPool exception / fallback safety
// ============================================================================
void Test_AH2_ScannerExceptionSafety() {
    using namespace Discrete::Memory;

    // Compile-time pin: ScanForValue and ScanForString MUST NOT be noexcept.
    static_assert(!noexcept(ScanForValue(std::declval<const ValueScanRequest&>(), nullptr)),
                  "ScanForValue must not be noexcept (A-H2)");
    static_assert(!noexcept(ScanForString(std::declval<const StringScanRequest&>(), nullptr)),
                  "ScanForString must not be noexcept (A-H2)");

    // 1. Normal parallel pool: all 64 work items processed exactly once.
    {
        constexpr int kItems = 64;
        std::atomic<int> next{0};
        std::atomic<int> seen[kItems];
        for (auto& s : seen) s.store(0);

        Detail::RunWorkerPool(
            4,
            [&]() {
                for (;;) {
                    int idx = next.fetch_add(1, std::memory_order_relaxed);
                    if (idx >= kItems) break;
                    seen[idx].fetch_add(1, std::memory_order_relaxed);
                }
            },
            [](auto fn) { return std::thread(fn); });

        for (int i = 0; i < kItems; ++i) CHECK(seen[i].load() == 1);
    }

    // 2. Thread creation fails mid-pool (after 2 real std::threads already started):
    //    must join started threads without std::terminate and drain remaining items inline.
    {
        constexpr int kItems = 100;
        std::atomic<int> next{0};
        std::atomic<int> seen[kItems];
        for (auto& s : seen) s.store(0);
        int spawnCalls = 0;

        Detail::RunWorkerPool(
            6,
            [&]() {
                for (;;) {
                    int idx = next.fetch_add(1, std::memory_order_relaxed);
                    if (idx >= kItems) break;
                    seen[idx].fetch_add(1, std::memory_order_relaxed);
                }
            },
            [&](auto fn) -> std::thread {
                if (spawnCalls++ == 2) {
                    throw std::system_error(std::make_error_code(std::errc::resource_unavailable_try_again));
                }
                return std::thread(fn);
            });

        CHECK(spawnCalls == 3);
        for (int i = 0; i < kItems; ++i) CHECK(seen[i].load() == 1);
    }

    // 3. Thread creation fails on the very first thread (pool empty):
    //    must fall back to inline single-threaded execution and finish all items.
    {
        constexpr int kItems = 32;
        std::atomic<int> next{0};
        std::atomic<int> count{0};

        Detail::RunWorkerPool(
            4,
            [&]() {
                for (;;) {
                    int idx = next.fetch_add(1, std::memory_order_relaxed);
                    if (idx >= kItems) break;
                    count.fetch_add(1, std::memory_order_relaxed);
                }
            },
            [](auto) -> std::thread { throw std::bad_alloc(); });

        CHECK(count.load() == kItems);
    }

    // 4. Worker throws std::bad_alloc inside a spawned std::thread:
    //    must be caught inside safeWorker without calling std::terminate.
    {
        std::atomic<int> runs{0};
        Detail::RunWorkerPool(
            2,
            [&]() {
                runs.fetch_add(1, std::memory_order_relaxed);
                throw std::bad_alloc();
            },
            [](auto fn) { return std::thread(fn); });
        CHECK(runs.load() == 2);
    }

    // 5. Live end-to-end ScanForValue + ScanForString over a VirtualAlloc RW page.
    {
        void* page = ::VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        CHECK(page != nullptr);
        if (page != nullptr) {
            auto* bytes = static_cast<std::uint8_t*>(page);
            // Unique 64-bit integer sentinel unlikely to appear elsewhere in test process.
            const std::uint64_t magicVal = 0x5A3C9E17D4B2816FULL;
            std::memcpy(bytes + 64, &magicVal, sizeof(magicVal));

            // Unique ASCII needle at +256.
            const char needle[] = "DISCRETE_TEST_NEEDLE_7F42A91E";
            const std::size_t needleLen = sizeof(needle) - 1;
            std::memcpy(bytes + 256, needle, needleLen);

            // ScanForValue (UInt64 exact).
            ValueScanRequest vreq{};
            vreq.dataType  = ValueDataType::UInt64;
            vreq.scanType  = ValueScanType::Exact;
            vreq.limit     = 64;
            vreq.alignment = 8;
            SetScanValueLo(vreq, magicVal);

            ValueScanStats vstats{};
            auto vhits = ScanForValue(vreq, &vstats);
            bool foundVal = false;
            for (const auto& h : vhits) {
                if (h.addr == bytes + 64) { foundVal = true; break; }
            }
            CHECK(foundVal);
            CHECK(vstats.regionsScanned > 0);

            // ScanForString (ASCII exact, single-threaded cap path as well).
            StringScanRequest sreq{};
            sreq.encoding  = StringEncoding::Ascii;
            sreq.matchMode = StringMatchMode::Exact;
            sreq.needle.assign(reinterpret_cast<const std::uint8_t*>(needle),
                               reinterpret_cast<const std::uint8_t*>(needle) + needleLen);
            sreq.limit     = 64;
            sreq.alignment = 1;

            ValueScanStats sstats{};
            auto shits = ScanForString(sreq, &sstats);
            bool foundStr = false;
            for (const auto& h : shits) {
                if (h.addr == bytes + 256 && h.length == needleLen) { foundStr = true; break; }
            }
            CHECK(foundStr);

            ::VirtualFree(page, 0, MEM_RELEASE);
        }
    }
}

// ============================================================================
// B-H1 — x86 Mono vtable pointer alignment (IMT-prefixed 4-mod-8 vtables)
// ============================================================================
void Test_BH1_MonoVtableAlignment() {
    using Discrete::Mono::IsAlignedMonoVtablePtr;
    using Discrete::Mono::IsPlausibleMonoVtableCandidate;
    using Discrete::Mono::kMonoVtableAlignMask;

    // 8-byte-aligned mempool base + Mono's 19-slot IMT prefix.
    constexpr std::uintptr_t kMempoolBase = 0x10000000u;
    constexpr std::size_t    kMonoImtSize = 19u;
    constexpr std::uintptr_t kImtPrefixedVtable =
        kMempoolBase + kMonoImtSize * sizeof(void*);

    // Plain class (no interfaces): 8-byte aligned on both x86 and x64.
    CHECK(IsAlignedMonoVtablePtr(kMempoolBase));
    CHECK(IsPlausibleMonoVtableCandidate(kMempoolBase));

    // Interface-implementing class (T[], List<T>, Dictionary<K,V>, IComparable...):
    // MUST be accepted on both architectures.
    CHECK(IsAlignedMonoVtablePtr(kImtPrefixedVtable));
    CHECK(IsPlausibleMonoVtableCandidate(kImtPrefixedVtable));

#if defined(DISCRETE_ARCH_X86)
    // On 32-bit Mono, 19 * 4 = 76 bytes -> kImtPrefixedVtable % 8 == 4.
    CHECK(kMonoVtableAlignMask == 3u);
    CHECK((kImtPrefixedVtable & 7u) == 4u);
    CHECK(IsPlausibleMonoVtableCandidate(0x10000004u));
    CHECK(!IsAlignedMonoVtablePtr(0x10000001u));
    CHECK(!IsAlignedMonoVtablePtr(0x10000002u));
    CHECK(!IsAlignedMonoVtablePtr(0x10000003u));
#else
    // On 64-bit Mono, 19 * 8 = 152 bytes -> kImtPrefixedVtable % 8 == 0.
    CHECK(kMonoVtableAlignMask == 7u);
    CHECK((kImtPrefixedVtable & 7u) == 0u);
    CHECK(!IsAlignedMonoVtablePtr(0x10000004u));
#endif

    // Null and below-0x10000 small integers are always rejected.
    CHECK(!IsAlignedMonoVtablePtr(0));
    CHECK(!IsPlausibleMonoVtableCandidate(0));
    CHECK(!IsPlausibleMonoVtableCandidate(0x00000008u));
    CHECK(!IsPlausibleMonoVtableCandidate(0x0000FFF8u));
}

}  // namespace

int main() {
    Test_CH1_NormalizeFieldOffset();
    Test_CH2_ResolveArrayElementLayout();
    Test_CH3_Il2CppFieldDefaultDecoder();
    Test_AH2_ScannerExceptionSafety();
    Test_BH1_MonoVtableAlignment();

    const char* arch = (sizeof(void*) == 4) ? "x86" : "x64";
    if (g_failures == 0) {
        std::printf("discrete_dll_test (%s): %d checks passed, 0 failed\n", arch, g_checks);
        return 0;
    }
    std::fprintf(stderr, "discrete_dll_test (%s): %d/%d checks FAILED\n", arch, g_failures, g_checks);
    return 1;
}
