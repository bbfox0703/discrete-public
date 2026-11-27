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

#include "AddressBounds.h"
#include "ExportHelpers.h"
#include "FieldXrefHelpers.h"
#include "MainThreadDispatcher.h"
#include "ManagedLayout.h"
#include "Memory.h"
#include "MethodInvokeValidation.h"
#include "MethodTokenIndex.h"
#include "ModuleLifecycle.h"
#include "MonoVtableFilter.h"
#include "ParallelScanDriver.h"
#include "PipeLineBuffer.h"
#include "PipeSecurity.h"
#include "RuntimeHealth.h"
#include "SafeChunkRead.h"
#include "ScanCancel.h"
#include "SehFilter.h"
#include "SnapshotSession.h"

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

// ============================================================================
// A-H1 — Module pinning (GET_MODULE_HANDLE_EX_FLAG_PIN) & safe FreeLibrary
// ============================================================================
void Test_AH1_PinnedModuleSurvivesFreeLibrary() {
    using Discrete::ModuleLifecycle::PinModuleContaining;
    using Discrete::ModuleLifecycle::TryClaimProcessMutex;

    // 1. Null / empty input guard checks.
    CHECK(!PinModuleContaining(nullptr));
    CHECK(TryClaimProcessMutex(nullptr) == nullptr);
    CHECK(TryClaimProcessMutex(L"") == nullptr);

    // 2. Single-init mutex gate: first claim succeeds, duplicate fails,
    //    bInitialOwner=FALSE means another thread can acquire without
    //    WAIT_ABANDONED, and closing releases the name cleanly.
    {
        wchar_t mutexName[96];
        ::_snwprintf_s(mutexName, _countof(mutexName), _TRUNCATE,
                       L"Local\\Discrete-AH1-SelfTest-%lu", ::GetCurrentProcessId());
        HANDLE h1 = TryClaimProcessMutex(mutexName);
        CHECK(h1 != nullptr);
        HANDLE h2 = TryClaimProcessMutex(mutexName);
        CHECK(h2 == nullptr);

        // Because bInitialOwner is FALSE, another thread can immediately lock
        // and unlock the mutex without seeing WAIT_ABANDONED or timeout.
        DWORD waitResult = WAIT_FAILED;
        std::thread checker([h1, &waitResult]() {
            waitResult = ::WaitForSingleObject(h1, 200);
            if (waitResult == WAIT_OBJECT_0) {
                ::ReleaseMutex(h1);
            }
        });
        checker.join();
        CHECK(waitResult == WAIT_OBJECT_0);

        if (h1 != nullptr) ::CloseHandle(h1);

        // Once closed, the name is free to claim again.
        HANDLE h3 = TryClaimProcessMutex(mutexName);
        CHECK(h3 != nullptr);
        if (h3 != nullptr) ::CloseHandle(h3);
    }

    // 3. Real OS loader test via discrete_pin_probe.dll (same directory as exe):
    //    LoadLibraryW -> worker running -> 3x FreeLibrary() does not deadlock,
    //    does not fire DLL_PROCESS_DETACH, and keeps the DLL mapped -> explicit
    //    off-DllMain Probe_Shutdown() cleanly joins the worker thread.
    {
        wchar_t exePath[MAX_PATH] = {};
        DWORD len = ::GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        CHECK(len > 0 && len < MAX_PATH);
        std::wstring probePath(exePath, len);
        std::size_t slash = probePath.find_last_of(L"\\/");
        CHECK(slash != std::wstring::npos);
        probePath = probePath.substr(0, slash + 1) + L"discrete_pin_probe.dll";

        HMODULE mod = ::LoadLibraryW(probePath.c_str());
        CHECK(mod != nullptr);
        if (mod == nullptr) return;

        using fn_int_t = int (*)();
        auto fnIsPinned      = reinterpret_cast<fn_int_t>(::GetProcAddress(mod, "Probe_IsPinned"));
        auto fnWorkerRunning = reinterpret_cast<fn_int_t>(::GetProcAddress(mod, "Probe_IsWorkerRunning"));
        auto fnDetachCount   = reinterpret_cast<fn_int_t>(::GetProcAddress(mod, "Probe_DetachCount"));
        auto fnShutdown      = reinterpret_cast<fn_int_t>(::GetProcAddress(mod, "Probe_Shutdown"));
        CHECK(fnIsPinned != nullptr);
        CHECK(fnWorkerRunning != nullptr);
        CHECK(fnDetachCount != nullptr);
        CHECK(fnShutdown != nullptr);
        if (!fnIsPinned || !fnWorkerRunning || !fnDetachCount || !fnShutdown) return;

        // Wait up to 1s for the probe's worker thread to reach its wait loop.
        for (int i = 0; i < 100 && fnWorkerRunning() == 0; ++i) {
            ::Sleep(10);
        }
        CHECK(fnIsPinned() == 1);
        CHECK(fnWorkerRunning() == 1);

        // Simulate aggressive external FreeLibrary calls (CE / Process Hacker).
        const ULONGLONG t0 = ::GetTickCount64();
        CHECK(::FreeLibrary(mod) != 0);
        CHECK(::FreeLibrary(mod) != 0);
        CHECK(::FreeLibrary(mod) != 0);
        const ULONGLONG elapsedMs = ::GetTickCount64() - t0;

        CHECK(elapsedMs < 1000);
        CHECK(::GetModuleHandleW(L"discrete_pin_probe.dll") == mod);
        CHECK(fnDetachCount() == 0);
        CHECK(fnWorkerRunning() == 1);

        // Explicit off-DllMain teardown (mirrors Discrete_Shutdown) joins cleanly.
        CHECK(fnShutdown() == 1);
        CHECK(fnWorkerRunning() == 0);
    }
}

// ============================================================================
// B-H2 — Object-extent bounds checking & pure-memory dynamic klass validation
// ============================================================================
void Test_BH2_AddressBoundsAndDynamicKlass() {
    using namespace Discrete::AddressBounds;

    // 1. ComputeObjectExtent + IsOffsetWithinExtent (regular, array, string).
    {
        ObjectExtentSpec reg64;
        reg64.instanceSize = 0x30;
        reg64.pointerSize  = 8;
        CHECK(ComputeObjectExtent(reg64) == 0x30u);
        CHECK(IsOffsetWithinExtent(0, 0x30u));
        CHECK(IsOffsetWithinExtent(0x2F, 0x30u));
        CHECK(!IsOffsetWithinExtent(0x30, 0x30u));
        CHECK(!IsOffsetWithinExtent(0x100, 0x30u));
        CHECK(!IsOffsetWithinExtent(-4, 0x30u));

        // Tiny instanceSize clamps up to object header (2*ptr); 0 stays unknown.
        ObjectExtentSpec tiny64;
        tiny64.instanceSize = 4;
        tiny64.pointerSize  = 8;
        CHECK(ComputeObjectExtent(tiny64) == 16u);

        ObjectExtentSpec unknown;
        unknown.instanceSize = 0;
        CHECK(ComputeObjectExtent(unknown) == 0u);
        CHECK(IsOffsetWithinExtent(0x120, 0u));
        CHECK(!IsOffsetWithinExtent(-1, 0u));

        // SZARRAY: int[10] on x64 (0x20 + 40 = 0x48) and x86 (0x10 + 40 = 0x38).
        ObjectExtentSpec arr64;
        arr64.isArray       = true;
        arr64.arrayLength   = 10;
        arr64.arrayElemSize = 4;
        arr64.pointerSize   = 8;
        CHECK(ComputeObjectExtent(arr64) == 0x48u);

        ObjectExtentSpec arr32 = arr64;
        arr32.pointerSize = 4;
        CHECK(ComputeObjectExtent(arr32) == 0x38u);

        // Corrupted huge array length bounds strictly to dataOff (header only).
        ObjectExtentSpec badArr;
        badArr.isArray       = true;
        badArr.arrayLength   = kMaxPlausibleArrayLen + 1u;
        badArr.arrayElemSize = 8;
        badArr.pointerSize   = 8;
        CHECK(ComputeObjectExtent(badArr) == 0x20u);

        // System.String: "Hello" (5 UTF-16 chars + NUL = 12 bytes).
        ObjectExtentSpec str64;
        str64.isString    = true;
        str64.stringChars = 5;
        str64.pointerSize = 8;
        CHECK(ComputeObjectExtent(str64) == 0x14u + 12u);

        ObjectExtentSpec str32 = str64;
        str32.pointerSize = 4;
        CHECK(ComputeObjectExtent(str32) == 0x0Cu + 12u);
    }

    // 2. IntersectCandidateOffsets convergence across samples.
    {
        std::vector<std::size_t> cand;
        bool seeded = false;
        IntersectCandidateOffsets(cand, seeded, {0, 16, 40});
        CHECK(cand.size() == 3u);
        IntersectCandidateOffsets(cand, seeded, {16, 40, 80});
        CHECK(cand.size() == 2u && cand[0] == 16u && cand[1] == 40u);
        IntersectCandidateOffsets(cand, seeded, {40});
        CHECK(cand.size() == 1u && cand[0] == 40u);
    }

    // 3. LooksLikePlausibleClassName (ASCII, UTF-8, and invalid inputs).
    {
        const char ascii[] = "List`1";
        CHECK(LooksLikePlausibleClassName(ascii, sizeof(ascii)));
        const char arr[] = "Enemy[]";
        CHECK(LooksLikePlausibleClassName(arr, sizeof(arr)));
        // Valid UTF-8 CJK class name ("\xE8\xA7\x92\xE8\x89\xB2" = "角色").
        const char utf8[] = "\xE8\xA7\x92\xE8\x89\xB2";
        CHECK(LooksLikePlausibleClassName(utf8, sizeof(utf8)));

        const char empty[] = "";
        CHECK(!LooksLikePlausibleClassName(empty, sizeof(empty)));
        const char noNul[4] = {'A', 'B', 'C', 'D'};
        CHECK(!LooksLikePlausibleClassName(noNul, sizeof(noNul)));
        const char ctrl[] = "Foo\x01" "Bar";
        CHECK(!LooksLikePlausibleClassName(ctrl, sizeof(ctrl)));
        const char badUtf8[] = "Foo\xFF\xFE";
        CHECK(!LooksLikePlausibleClassName(badUtf8, sizeof(badUtf8)));
    }

    // 4. ValidateDynamicKlassMemory against real in-memory fake klass layouts.
    {
        struct alignas(8) FakeKlass {
            std::uintptr_t image;
            std::uintptr_t elementClass;
            std::uintptr_t name;
            std::uintptr_t reserved;
        };
        KlassFieldOffsets offs;
        offs.imageOffset     = offsetof(FakeKlass, image);
        offs.hasImageOffset  = true;
        offs.elemClassOffset = offsetof(FakeKlass, elementClass);
        offs.hasElemOffset   = true;
        offs.nameOffset      = offsetof(FakeKlass, name);
        offs.hasNameOffset   = true;

        int fakeImageObj = 0;
        const auto knownImage = reinterpret_cast<std::uintptr_t>(&fakeImageObj);
        const char listName[64] = "List`1";
        const char arrName[64]  = "Enemy[]";
        const char elemName[64] = "Enemy";
        const char badName[64]  = "\x01\x02";

        FakeKlass elemKlass{knownImage, 0, reinterpret_cast<std::uintptr_t>(elemName), 0};
        elemKlass.elementClass = reinterpret_cast<std::uintptr_t>(&elemKlass);

        FakeKlass genericKlass{knownImage, 0, reinterpret_cast<std::uintptr_t>(listName), 0};
        genericKlass.elementClass = reinterpret_cast<std::uintptr_t>(&genericKlass);

        FakeKlass arrayKlass{knownImage, reinterpret_cast<std::uintptr_t>(&elemKlass),
                             reinterpret_cast<std::uintptr_t>(arrName), 0};

        // Jagged / generic array: List<T>[] whose element is genericKlass (not in TypeDef set,
        // but carries knownImage).
        FakeKlass genericArrayKlass{knownImage, reinterpret_cast<std::uintptr_t>(&genericKlass),
                                    reinterpret_cast<std::uintptr_t>(arrName), 0};

        FakeKlass badImageKlass{0xDEADBEEFu, 0, reinterpret_cast<std::uintptr_t>(listName), 0};
        badImageKlass.elementClass = reinterpret_cast<std::uintptr_t>(&badImageKlass);

        FakeKlass badNameKlass{knownImage, 0, reinterpret_cast<std::uintptr_t>(badName), 0};
        badNameKlass.elementClass = reinterpret_cast<std::uintptr_t>(&badNameKlass);

        auto readPtr = [](std::uintptr_t a, std::uintptr_t& out) -> bool {
            return Discrete::Memory::SafeReadBytes(reinterpret_cast<const void*>(a), &out, sizeof(out));
        };
        auto readBytes = [](std::uintptr_t a, void* dst, std::size_t n) -> bool {
            return Discrete::Memory::SafeReadBytes(reinterpret_cast<const void*>(a), dst, n);
        };
        auto containsImage = [knownImage](std::uintptr_t img) -> bool {
            return img == knownImage;
        };
        const auto elemAddr = reinterpret_cast<std::uintptr_t>(&elemKlass);
        auto containsClass = [elemAddr](std::uintptr_t k) -> bool {
            return k == elemAddr;
        };

        bool isArr = false;
        std::uintptr_t outElem = 0;

        // Generic instantiation (List<T>): accepted, isArray == false.
        CHECK(ValidateDynamicKlassMemory(
            reinterpret_cast<std::uintptr_t>(&genericKlass), offs,
            readPtr, readBytes, containsImage, containsClass, isArr, outElem));
        CHECK(!isArr);
        CHECK(outElem == reinterpret_cast<std::uintptr_t>(&genericKlass));

        // Array of TypeDef class (Enemy[]): accepted, isArray == true.
        CHECK(ValidateDynamicKlassMemory(
            reinterpret_cast<std::uintptr_t>(&arrayKlass), offs,
            readPtr, readBytes, containsImage, containsClass, isArr, outElem));
        CHECK(isArr);
        CHECK(outElem == elemAddr);

        // Array of generic class (List<T>[]): accepted, isArray == true.
        CHECK(ValidateDynamicKlassMemory(
            reinterpret_cast<std::uintptr_t>(&genericArrayKlass), offs,
            readPtr, readBytes, containsImage, containsClass, isArr, outElem));
        CHECK(isArr);
        CHECK(outElem == reinterpret_cast<std::uintptr_t>(&genericKlass));

        // Invalid image, control-char name, misaligned ptr, null ptr: all rejected.
        CHECK(!ValidateDynamicKlassMemory(
            reinterpret_cast<std::uintptr_t>(&badImageKlass), offs,
            readPtr, readBytes, containsImage, containsClass, isArr, outElem));
        CHECK(!ValidateDynamicKlassMemory(
            reinterpret_cast<std::uintptr_t>(&badNameKlass), offs,
            readPtr, readBytes, containsImage, containsClass, isArr, outElem));
        CHECK(!ValidateDynamicKlassMemory(
            reinterpret_cast<std::uintptr_t>(&genericKlass) + 4u, offs,
            readPtr, readBytes, containsImage, containsClass, isArr, outElem));
        CHECK(!ValidateDynamicKlassMemory(
            0u, offs, readPtr, readBytes, containsImage, containsClass, isArr, outElem));
    }

    // 5. SelectContainingEntry: rejects neighbor hits, backtracks over embedded headers.
    {
        struct FakeEntry {
            std::uintptr_t base;
            std::size_t    extent;
        };
        std::vector<FakeEntry> bases = {
            {0x1000u, 0x20u},   // small object A: [0x1000, 0x1020)
            {0x2000u, 0x200u},  // large object Q: [0x2000, 0x2200)
            {0x2080u, 0x18u},   // embedded false-positive header inside Q: [0x2080, 0x2098)
            {0x3000u, 0u},      // object with unknown extent (0)
        };
        auto extFn = [](const FakeEntry& e) { return e.extent; };

        // Exact B-H2 repro: addr=0x1038 sits past object A's end (0x1020) -> MUST NOT match A!
        auto missNeighbor = SelectContainingEntry(bases, 0x1038u, 0x1038u, 0x0100u, extFn);
        CHECK(!missNeighbor.found);

        // Exact boundary of A: 0x101F hits A, 0x1020 misses A.
        auto hitEdge = SelectContainingEntry(bases, 0x101Fu, 0x1018u, 0x0100u, extFn);
        CHECK(hitEdge.found && hitEdge.entry.base == 0x1000u && hitEdge.nextBase == 0x2000u);
        auto missEdge = SelectContainingEntry(bases, 0x1020u, 0x1020u, 0x0100u, extFn);
        CHECK(!missEdge.found);

        // Embedded false-positive at 0x2080 (extent 0x18) does NOT hide enclosing Q at 0x2000
        // when querying 0x2100 -> backtracks to Q!
        auto hitEnclosing = SelectContainingEntry(bases, 0x2100u, 0x2100u, 0x1100u, extFn);
        CHECK(hitEnclosing.found && hitEnclosing.entry.base == 0x2000u && hitEnclosing.extent == 0x200u);

        // Unknown extent (0) still matches within the backward window.
        auto hitUnknown = SelectContainingEntry(bases, 0x3040u, 0x3040u, 0x2100u, extFn);
        CHECK(hitUnknown.found && hitUnknown.entry.base == 0x3000u);
    }
}

// ============================================================================
// A-M1 + A-L7 — Per-client scan-monitor slot & atomic ScanCancel state
// ============================================================================
void Test_AM1_PerClientScanCancelAndAtomicState() {
    namespace SC = Discrete::ScanCancel;

    CHECK(SC::ActiveCount() == 0ull);
    CHECK(!SC::IsCancelRequested());

    // Stray RequestCancel with no active scope must NOT arm the cancel flag.
    SC::RequestCancel();
    CHECK(!SC::IsCancelRequested());

    // Simulate two concurrent pipe clients: Client A enters a scan Scope while
    // Client B stays in a non-scan RPC. Client B's monitor checks depthB (0)
    // instead of global ActiveCount (1), so Client B never cancels Client A.
    std::atomic<int>  depthA{0};
    std::atomic<int>  depthB{0};
    std::atomic<bool> aEntered{false};
    std::atomic<bool> bChecked{false};

    std::thread clientA([&]() {
        SC::ClientSlotGuard slotA(depthA);
        CHECK(depthA.load() == 0);
        {
            SC::Scope outer;
            CHECK(depthA.load() == 1);
            CHECK(SC::ActiveCount() == 1ull);
            CHECK(!SC::IsCancelRequested());
            aEntered.store(true, std::memory_order_release);
            while (!bChecked.load(std::memory_order_acquire)) {
                ::Sleep(1);
            }
            // Client B disconnected without holding a Scope -> A stays uncancelled.
            CHECK(!SC::IsCancelRequested());
            {
                SC::Scope inner;
                CHECK(depthA.load() == 2);
                CHECK(SC::ActiveCount() == 2ull);
                SC::RequestCancel();
                CHECK(SC::IsCancelRequested());
            }
            // Inner scope left, outer still active -> cancel remains armed.
            CHECK(depthA.load() == 1);
            CHECK(SC::ActiveCount() == 1ull);
            CHECK(SC::IsCancelRequested());
        }
        // Outermost scope left -> both counters zero and cancel cleared.
        CHECK(depthA.load() == 0);
    });

    std::thread clientB([&]() {
        SC::ClientSlotGuard slotB(depthB);
        while (!aEntered.load(std::memory_order_acquire)) {
            ::Sleep(1);
        }
        // Global scan is active, but Client B's own depth is 0 -> monitor MUST NOT probe/cancel.
        CHECK(SC::ActiveCount() == 1ull);
        CHECK(depthB.load(std::memory_order_acquire) == 0);
        bChecked.store(true, std::memory_order_release);
    });

    clientA.join();
    clientB.join();

    CHECK(SC::ActiveCount() == 0ull);
    CHECK(!SC::IsCancelRequested());

    // A-L7 race test: concurrent Scope enter/exit vs RequestCancel must never
    // leave a leaked armed cancel flag once all scopes have exited.
    std::atomic<bool> done{false};
    std::thread canceller([&]() {
        while (!done.load(std::memory_order_relaxed)) {
            SC::RequestCancel();
        }
    });
    for (int i = 0; i < 20000; ++i) {
        SC::Scope s;
    }
    done.store(true, std::memory_order_relaxed);
    canceller.join();
    CHECK(SC::ActiveCount() == 0ull);
    CHECK(!SC::IsCancelRequested());
}

// ============================================================================
// A-M2 + A-L5 — PipeLineBuffer preserves pipelined bytes & caps line size
// ============================================================================
void Test_AM2_PipeLineBufferPreservesPipelinedBytes() {
    using Discrete::PipeFraming::LineBuffer;

    // 1. Two pipelined JSON-RPC lines delivered in a single ReadFile chunk.
    {
        LineBuffer lb;
        const char chunk[] = "{\"id\":1,\"cmd\":\"a\"}\n{\"id\":2,\"cmd\":\"b\"}\n";
        lb.Append(chunk, sizeof(chunk) - 1u);

        std::string line;
        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::LineReady);
        CHECK(line == "{\"id\":1,\"cmd\":\"a\"}");
        CHECK(lb.PendingBytes() > 0u);

        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::LineReady);
        CHECK(line == "{\"id\":2,\"cmd\":\"b\"}");
        CHECK(lb.PendingBytes() == 0u);

        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::NeedMore);
    }

    // 2. Lines split arbitrarily across multiple ReadFile chunks + CRLF stripping.
    {
        LineBuffer lb;
        std::string line;

        const char p1[] = "{\"id\":1";
        lb.Append(p1, sizeof(p1) - 1u);
        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::NeedMore);

        const char p2[] = ",\"x\":10}\r\n{\"id\":2";
        lb.Append(p2, sizeof(p2) - 1u);
        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::LineReady);
        CHECK(line == "{\"id\":1,\"x\":10}");
        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::NeedMore);

        const char p3[] = ",\"x\":20}\r\n";
        lb.Append(p3, sizeof(p3) - 1u);
        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::LineReady);
        CHECK(line == "{\"id\":2,\"x\":20}");
        CHECK(lb.PendingBytes() == 0u);
    }

    // 3. Empty lines interspersed with real lines do not swallow following lines.
    {
        LineBuffer lb;
        const char chunk[] = "\n\r\n{\"id\":3}\n";
        lb.Append(chunk, sizeof(chunk) - 1u);

        std::string line;
        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::LineReady);
        CHECK(line.empty());
        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::LineReady);
        CHECK(line.empty());
        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::LineReady);
        CHECK(line == "{\"id\":3}");
        CHECK(lb.PendingBytes() == 0u);
    }

    // 4. Max-size cap (A-L5): exact cap succeeds; cap + 1 reports TooLarge.
    {
        LineBuffer lb(16u);
        std::string line;
        const std::string exact = std::string(16u, 'A') + "\n";
        lb.Append(exact.data(), exact.size());
        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::LineReady);
        CHECK(line.size() == 16u);

        const std::string overNoNl(17u, 'B');
        lb.Append(overNoNl.data(), overNoNl.size());
        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::TooLarge);

        const std::string overWithNl = std::string(17u, 'C') + "\n{\"ok\":1}\n";
        lb.Append(overWithNl.data(), overWithNl.size());
        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::TooLarge);
        // Subsequent line after an oversized line still decodes cleanly if caller continues.
        CHECK(lb.TryPopLine(line) == LineBuffer::ExtractStatus::LineReady);
        CHECK(line == "{\"ok\":1}");
    }
}

// ============================================================================
// A-M11 — AVX2 string-scan scalar tail preserves non-dividing stride grid
// ============================================================================
void Test_AM11_StringScanAvx2TailAlignment() {
    using namespace Discrete::Memory;

    // 1. Targeted regression: buffer of 60 bytes with stride=6 and needle="XY".
    //    AVX2 covers [0, 32) -> hits at 0, 6, 12, 18, 24, 30.
    //    Scalar tail MUST resume at 36 (NOT 32!), so:
    //      - unaligned decoy at 32 is NOT matched,
    //      - aligned hit at 36 and 48 ARE matched.
    {
        std::vector<std::uint8_t> buf(60, static_cast<std::uint8_t>('.'));
        const std::uint8_t needle[2] = {'X', 'Y'};
        // Place decoy at 32 (off stride=6 grid) and real hits at 30, 36, 48.
        buf[30] = 'X'; buf[31] = 'Y';
        buf[32] = 'X'; buf[33] = 'Y';  // off-grid!
        buf[36] = 'X'; buf[37] = 'Y';
        buf[48] = 'X'; buf[49] = 'Y';

        std::vector<StringScanCandidate> hits;
        CHECK(ScanStringSpan(buf.data(), buf.size(), needle, 2, /*stride=*/6, hits, 64));
        CHECK(hits.size() == 3u);
        if (hits.size() == 3u) {
            CHECK(static_cast<std::size_t>(
                static_cast<const std::uint8_t*>(hits[0].addr) - buf.data()) == 30u);
            CHECK(static_cast<std::size_t>(
                static_cast<const std::uint8_t*>(hits[1].addr) - buf.data()) == 36u);
            CHECK(static_cast<std::size_t>(
                static_cast<const std::uint8_t*>(hits[2].addr) - buf.data()) == 48u);
        }
    }

    // 2. Exhaustive oracle comparison across non-dividing and >32 strides
    //    and varied tail lengths (sizes 33..95).
    {
        const std::uint8_t needle[3] = {'A', 'B', 'C'};
        const std::size_t strides[] = {1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 32, 33, 64};

        // Construct a dense buffer where "ABC" occurs at every possible offset
        // via overlapping/non-overlapping placements using a 1-byte needle and
        // 3-byte needle.
        std::vector<std::uint8_t> dense(96);
        for (std::size_t k = 0; k < dense.size(); ++k) {
            dense[k] = static_cast<std::uint8_t>((k % 3 == 0) ? 'A' : ((k % 3 == 1) ? 'B' : 'C'));
        }
        // Also test a buffer of all 'A's with needle "AA" so EVERY offset matches
        // the byte pattern — any grid drift immediately shows up!
        std::vector<std::uint8_t> allA(96, static_cast<std::uint8_t>('A'));
        const std::uint8_t aa[2] = {'A', 'A'};

        bool allMatchedOracle = true;
        for (std::size_t stride : strides) {
            for (std::size_t sz = 33; sz <= 95; sz += 7) {
                // Compare against scalar reference on `allA` with needle "AA".
                std::vector<std::size_t> expected;
                for (std::size_t i = 0; i + 2 <= sz; i += stride) expected.push_back(i);

                std::vector<StringScanCandidate> got;
                ScanStringSpan(allA.data(), sz, aa, 2, stride, got, 1024);
                if (got.size() != expected.size()) {
                    allMatchedOracle = false;
                } else {
                    for (std::size_t j = 0; j < got.size(); ++j) {
                        const auto off = static_cast<std::size_t>(
                            static_cast<const std::uint8_t*>(got[j].addr) - allA.data());
                        if (off != expected[j]) {
                            allMatchedOracle = false;
                            break;
                        }
                    }
                }

                // Compare against scalar reference on `dense` with needle "ABC".
                std::vector<std::size_t> exp3;
                for (std::size_t i = 0; i + 3 <= sz; i += stride) {
                    if (std::memcmp(dense.data() + i, needle, 3) == 0) exp3.push_back(i);
                }
                std::vector<StringScanCandidate> got3;
                ScanStringSpan(dense.data(), sz, needle, 3, stride, got3, 1024);
                if (got3.size() != exp3.size()) {
                    allMatchedOracle = false;
                } else {
                    for (std::size_t j = 0; j < got3.size(); ++j) {
                        const auto off = static_cast<std::size_t>(
                            static_cast<const std::uint8_t*>(got3[j].addr) - dense.data());
                        if (off != exp3[j]) {
                            allMatchedOracle = false;
                            break;
                        }
                    }
                }
            }
        }
        CHECK(allMatchedOracle);
    }
}

void Test_CM3_ClampMainThreadTimeoutMs() {
    using namespace Discrete::MainThreadDispatcher;
    CHECK(ClampMainThreadTimeoutMs((std::numeric_limits<std::int32_t>::min)()) == kMinMainThreadTimeoutMs);
    CHECK(ClampMainThreadTimeoutMs(-500)      == kMinMainThreadTimeoutMs);
    CHECK(ClampMainThreadTimeoutMs(-1)        == kMinMainThreadTimeoutMs);
    CHECK(ClampMainThreadTimeoutMs(0)         == kMinMainThreadTimeoutMs);
    CHECK(ClampMainThreadTimeoutMs(1)         == 1);
    CHECK(ClampMainThreadTimeoutMs(5000)      == 5000);
    CHECK(ClampMainThreadTimeoutMs(60000)     == kMaxMainThreadTimeoutMs);
    CHECK(ClampMainThreadTimeoutMs(60001)     == kMaxMainThreadTimeoutMs);
    CHECK(ClampMainThreadTimeoutMs((std::numeric_limits<std::int32_t>::max)()) == kMaxMainThreadTimeoutMs);
}

// ============================================================================
// B-M10 + A-L15 + C-M1 — Indexed worker pool exception safety, thread-local
// ScanThreadCapScope isolation, and cooperative ScanCancel abort on pointer scan
// ============================================================================
void Test_BM10_AL15_CM1_IndexedPoolThreadCapAndCancel() {
    using namespace Discrete::Memory;
    using namespace Discrete::Memory::Detail;

    // 1. RunIndexedWorkerPool: normal parallel run partitions all 200 items cleanly.
    {
        constexpr std::size_t kItems = 200;
        constexpr unsigned int kThreads = 4;
        std::vector<std::vector<std::size_t>> perThread(kThreads);
        std::vector<std::size_t> qScanned(kThreads, 0);
        std::atomic<std::size_t> next{0};

        RunIndexedWorkerPool(kThreads, [&](unsigned int tid) {
            std::size_t localCount = 0;
            for (;;) {
                const std::size_t idx = next.fetch_add(1, std::memory_order_relaxed);
                if (idx >= kItems) break;
                perThread[tid].push_back(idx);
                ++localCount;
            }
            qScanned[tid] += localCount;
        });

        std::size_t totalQ = 0;
        std::vector<std::size_t> merged;
        for (unsigned int t = 0; t < kThreads; ++t) {
            totalQ += qScanned[t];
            merged.insert(merged.end(), perThread[t].begin(), perThread[t].end());
        }
        std::sort(merged.begin(), merged.end());
        CHECK(totalQ == kItems);
        CHECK(merged.size() == kItems);
        bool allExact = (merged.size() == kItems);
        for (std::size_t i = 0; allExact && i < kItems; ++i) {
            if (merged[i] != i) allExact = false;
        }
        CHECK(allExact);
    }

    // 2. RunIndexedWorkerPool: mid-pool std::system_error joins started workers
    //    and drains remaining items inline on slot 0 without overwriting slot 0's
    //    earlier counts or calling std::terminate.
    {
        constexpr std::size_t kItems = 200;
        constexpr unsigned int kThreads = 4;
        std::vector<std::vector<std::size_t>> perThread(kThreads);
        std::vector<std::size_t> qScanned(kThreads, 0);
        std::atomic<std::size_t> next{0};
        unsigned int spawnCount = 0;

        RunIndexedWorkerPool(
            kThreads,
            [&](unsigned int tid) {
                std::size_t localCount = 0;
                for (;;) {
                    const std::size_t idx = next.fetch_add(1, std::memory_order_relaxed);
                    if (idx >= kItems) break;
                    perThread[tid].push_back(idx);
                    ++localCount;
                    // Give earlier threads a tiny window so slot 0 collects some
                    // items before fallback also drains into slot 0.
                    if (localCount == 10 && tid == 0) break;
                }
                qScanned[tid] += localCount;
            },
            [&](auto&& fn) -> std::thread {
                if (spawnCount++ == 2) {
                    throw std::system_error(
                        std::make_error_code(std::errc::resource_unavailable_try_again),
                        "simulated x86 thread exhaustion");
                }
                return std::thread(std::forward<decltype(fn)>(fn));
            });

        std::size_t totalQ = 0;
        std::vector<std::size_t> merged;
        for (unsigned int t = 0; t < kThreads; ++t) {
            totalQ += qScanned[t];
            merged.insert(merged.end(), perThread[t].begin(), perThread[t].end());
        }
        std::sort(merged.begin(), merged.end());
        CHECK(totalQ == kItems);
        CHECK(merged.size() == kItems);
        CHECK(qScanned[0] >= 10u);
    }

    // 3. RunIndexedWorkerPool: first-thread std::bad_alloc falls back inline on slot 0.
    {
        std::vector<std::size_t> qScanned(4, 0);
        std::atomic<std::size_t> next{0};
        RunIndexedWorkerPool(
            4,
            [&](unsigned int tid) {
                std::size_t c = 0;
                while (next.fetch_add(1, std::memory_order_relaxed) < 50) ++c;
                qScanned[tid] += c;
            },
            [](auto&&) -> std::thread { throw std::bad_alloc(); });
        CHECK(qScanned[0] == 50u);
    }

    // 4. RunIndexedWorkerPool: worker-body exception is contained (no std::terminate).
    {
        std::atomic<int> completed{0};
        RunIndexedWorkerPool(3, [&](unsigned int tid) {
            if (tid == 1) throw std::bad_alloc();
            completed.fetch_add(1, std::memory_order_relaxed);
        });
        CHECK(completed.load() == 2);
    }

    // 5. A-L15: ScanThreadCapScope is strictly thread-local and nests cleanly.
    {
        CHECK(GetScanThreadCap() == 0u);
        ScanThreadCapScope mainCap(1u);
        CHECK(GetScanThreadCap() == 1u);
        CHECK(EffectiveScanThreads() == 1u);

        bool otherSawUncapped = false;
        bool otherNestedOk    = false;
        std::thread other([&]() {
            otherSawUncapped = (GetScanThreadCap() == 0u) &&
                               (EffectiveScanThreads() == ScanThreadCount());
            {
                ScanThreadCapScope outer(2u);
                const bool s1 = (GetScanThreadCap() == 2u);
                {
                    ScanThreadCapScope inner(1u);
                    otherNestedOk = s1 && (GetScanThreadCap() == 1u) && (EffectiveScanThreads() == 1u);
                }
                otherNestedOk = otherNestedOk && (GetScanThreadCap() == 2u);
            }
            otherNestedOk = otherNestedOk && (GetScanThreadCap() == 0u);
        });
        other.join();

        CHECK(otherSawUncapped);
        CHECK(otherNestedOk);
        // Main thread's cap was untouched by the other thread's scopes.
        CHECK(GetScanThreadCap() == 1u);
        CHECK(EffectiveScanThreads() == 1u);
    }
    CHECK(GetScanThreadCap() == 0u);

    // 6. C-M1: ScanForPointer honours ScanCancel::Scope + RequestCancel and
    //    recovers cleanly after the scope exits.
    {
        void* page = ::VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        CHECK(page != nullptr);
        if (page != nullptr) {
            const std::uintptr_t sentinel =
                static_cast<std::uintptr_t>(sizeof(void*) == 8 ? 0x13579BDF2468ACE0ull : 0x2468ACE0u);
            auto* slot = reinterpret_cast<std::uintptr_t*>(static_cast<std::uint8_t*>(page) + 64);
            *slot = sentinel;

            {
                Discrete::ScanCancel::Scope scope;
                Discrete::ScanCancel::RequestCancel();
                CHECK(Discrete::ScanCancel::IsCancelRequested());
                auto cancelledMatches = ScanForPointer(sentinel, 16, nullptr, 0, 0, true);
                CHECK(cancelledMatches.empty());
            }
            // Scope exit atomically clears cancel bit; subsequent uncancelled scan finds it.
            CHECK(!Discrete::ScanCancel::IsCancelRequested());
            {
                Discrete::ScanCancel::Scope scope;
                auto liveMatches = ScanForPointer(sentinel, 16, nullptr, 0, 0, true);
                bool foundOurSlot = false;
                for (const void* p : liveMatches) {
                    if (p == static_cast<const void*>(slot)) { foundOurSlot = true; break; }
                }
                CHECK(foundOurSlot);
            }
            ::VirtualFree(page, 0, MEM_RELEASE);
        }
    }
}

// ============================================================================
// A-M7 + A-M8 + A-L16 — Wall-clock detour/client drain deadline, AbortPending
// & Shutdown waking stalled SubmitAndWait workers, and inline main-thread flag
// ============================================================================
void Test_AM7_AM8_AL16_TeardownAndMainThreadDispatcher() {
    using namespace Discrete::MainThreadDispatcher;

    // 1. A-M7: WaitUntilZeroWithDeadline returns true immediately on zero,
    //    drains when a side thread decrements to 0, and respects real wall-clock
    //    timeout when stuck (never 15x timer-tick iteration inflation).
    {
        std::atomic<int> zero{0};
        CHECK(WaitUntilZeroWithDeadline(zero, 2000, 1));

        std::atomic<int> busy{1};
        std::thread releaser([&busy]() {
            ::Sleep(30);
            busy.fetch_sub(1, std::memory_order_release);
        });
        const std::uint64_t t0 = ::GetTickCount64();
        CHECK(WaitUntilZeroWithDeadline(busy, 2000, 1));
        CHECK((::GetTickCount64() - t0) < 1000u);
        releaser.join();

        std::atomic<int> stuck{1};
        const std::uint64_t t1 = ::GetTickCount64();
        CHECK(!WaitUntilZeroWithDeadline(stuck, 80, 1));
        const std::uint64_t elapsed = ::GetTickCount64() - t1;
        CHECK(elapsed >= 60u && elapsed < 500u);
    }

    // 2. A-L16: inline main-thread SubmitAndWait sets and restores
    //    IsCurrentlyDrainingOnMainThread() on both normal return and exception.
    CHECK(Init());
    CHECK(IsInitialized());
    CHECK(GetMainThreadId() == ::GetCurrentThreadId());
    CHECK(!IsCurrentlyDrainingOnMainThread());
    {
        bool sawInside = false;
        DispatchResult dr = SubmitAndWait([&sawInside]() {
            sawInside = IsCurrentlyDrainingOnMainThread();
        }, 5000);
        CHECK(dr.ok);
        CHECK(dr.diagnostic_path == "inline_main_thread");
        CHECK(sawInside);
        CHECK(!IsCurrentlyDrainingOnMainThread());
    }
    {
        bool sawInside = false;
        DispatchResult dr = SubmitAndWait([&sawInside]() {
            sawInside = IsCurrentlyDrainingOnMainThread();
            throw std::runtime_error("boom");
        }, 5000);
        CHECK(!dr.ok);
        CHECK(dr.diagnostic_path == "inline_threw");
        CHECK(sawInside);
        CHECK(!IsCurrentlyDrainingOnMainThread());
    }

    // 3. A-M8: AbortPending() wakes a worker blocked in SubmitAndWait(30000)
    //    immediately without disabling the dispatcher.
    {
        std::atomic<bool> workRan{false};
        DispatchResult workerResult{};
        std::uint64_t workerElapsed = 0;
        std::thread worker([&]() {
            const std::uint64_t start = ::GetTickCount64();
            workerResult = SubmitAndWait([&workRan]() { workRan.store(true); }, 30000);
            workerElapsed = ::GetTickCount64() - start;
        });

        const std::uint64_t waitStart = ::GetTickCount64();
        while (GetStats().queue_depth == 0 && (::GetTickCount64() - waitStart) < 2000u) {
            ::Sleep(1);
        }
        CHECK(GetStats().queue_depth == 1u);
        AbortPending();
        worker.join();

        CHECK(!workerResult.ok);
        CHECK(workerResult.timed_out);
        CHECK(!workRan.load());
        CHECK(workerElapsed < 2000u);
        CHECK(IsInitialized());
        CHECK(GetStats().queue_depth == 0u);
    }

    // 4. A-M8: Shutdown() wakes a stalled worker immediately and rejects any
    //    subsequent SubmitAndWait with uninitialized; Init() can re-arm it.
    {
        std::atomic<bool> workRan{false};
        DispatchResult workerResult{};
        std::uint64_t workerElapsed = 0;
        std::thread worker([&]() {
            const std::uint64_t start = ::GetTickCount64();
            workerResult = SubmitAndWait([&workRan]() { workRan.store(true); }, 30000);
            workerElapsed = ::GetTickCount64() - start;
        });

        const std::uint64_t waitStart = ::GetTickCount64();
        while (GetStats().queue_depth == 0 && (::GetTickCount64() - waitStart) < 2000u) {
            ::Sleep(1);
        }
        CHECK(GetStats().queue_depth == 1u);
        Shutdown();
        worker.join();

        CHECK(!workerResult.ok);
        CHECK(workerResult.timed_out);
        CHECK(!workRan.load());
        CHECK(workerElapsed < 2000u);
        CHECK(!IsInitialized());

        DispatchResult afterShutdown = SubmitAndWait([]() {}, 5000);
        CHECK(!afterShutdown.ok);
        CHECK(afterShutdown.diagnostic_path == "uninitialized");
    }
}

// ============================================================================
// A-M3 + A-M4 — Named-pipe security (PIPE_REJECT_REMOTE_CLIENTS, protected
// user+SYSTEM DACL, FILE_FLAG_FIRST_PIPE_INSTANCE conflict) & module OwnsInit
// ============================================================================
void Test_AM3_AM4_PipeSecurityAndInitOwnership() {
    using namespace Discrete::PipeSecurity;

    // 1. A-M3: flags enforce remote rejection and first-instance ownership.
    CHECK((MakePipeMode() & PIPE_REJECT_REMOTE_CLIENTS) != 0u);
    CHECK((MakePipeOpenMode(true) & FILE_FLAG_FIRST_PIPE_INSTANCE) != 0u);
    CHECK((MakePipeOpenMode(false) & FILE_FLAG_FIRST_PIPE_INSTANCE) == 0u);

    // 2. A-M3: SDDL restricts access to SYSTEM + current user (protected DACL).
    ScopedPipeSecurityAttributes pipeSec;
    CHECK(pipeSec.IsValid());
    CHECK(pipeSec.Get() != nullptr);
    CHECK(pipeSec.Sddl().rfind(L"D:P(A;;GA;;;SY)(A;;GA;;;", 0) == 0);
    CHECK(pipeSec.Sddl().find(L";;;WD)") == std::wstring::npos);
    CHECK(pipeSec.Sddl().find(L";;;AU)") == std::wstring::npos);

    // Verify protected DACL bit is set on the parsed SECURITY_DESCRIPTOR.
    SECURITY_DESCRIPTOR_CONTROL sdCtrl = 0;
    DWORD sdRev = 0;
    CHECK(::GetSecurityDescriptorControl(pipeSec.Get()->lpSecurityDescriptor, &sdCtrl, &sdRev) != FALSE);
    CHECK((sdCtrl & SE_DACL_PROTECTED) != 0);

    // 3. A-M3: real kernel pipe creation — first instance succeeds, duplicate
    //    FIRST_PIPE_INSTANCE fails with ERROR_ACCESS_DENIED, second regular
    //    instance succeeds, and a local same-user client connects cleanly.
    wchar_t pipeName[128];
    ::_snwprintf_s(pipeName, _countof(pipeName), _TRUNCATE,
                   L"\\\\.\\pipe\\DiscreteSecurityTest_%lu_%llu",
                   ::GetCurrentProcessId(),
                   static_cast<unsigned long long>(::GetTickCount64()));

    HANDLE hFirst = ::CreateNamedPipeW(
        pipeName,
        MakePipeOpenMode(/*firstInstance=*/true),
        MakePipeMode(),
        2, 4096, 4096, 0, pipeSec.Get());
    CHECK(hFirst != INVALID_HANDLE_VALUE);

    if (hFirst != INVALID_HANDLE_VALUE) {
        // Attempting another FIRST_PIPE_INSTANCE on the same name MUST fail with ERROR_ACCESS_DENIED.
        HANDLE hConflict = ::CreateNamedPipeW(
            pipeName,
            MakePipeOpenMode(/*firstInstance=*/true),
            MakePipeMode(),
            2, 4096, 4096, 0, pipeSec.Get());
        const DWORD conflictErr = ::GetLastError();
        CHECK(hConflict == INVALID_HANDLE_VALUE);
        CHECK(conflictErr == ERROR_ACCESS_DENIED);
        if (hConflict != INVALID_HANDLE_VALUE) ::CloseHandle(hConflict);

        // Subsequent instance without FIRST_PIPE_INSTANCE succeeds.
        HANDLE hSecond = ::CreateNamedPipeW(
            pipeName,
            MakePipeOpenMode(/*firstInstance=*/false),
            MakePipeMode(),
            2, 4096, 4096, 0, pipeSec.Get());
        CHECK(hSecond != INVALID_HANDLE_VALUE);

        // Same-user local client can connect to the secured pipe.
        HANDLE hClient = ::CreateFileW(
            pipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        CHECK(hClient != INVALID_HANDLE_VALUE);
        if (hClient != INVALID_HANDLE_VALUE) ::CloseHandle(hClient);

        if (hSecond != INVALID_HANDLE_VALUE) ::CloseHandle(hSecond);
        ::CloseHandle(hFirst);
    }

    // 4. A-M4: module single-init ownership gate defaults false and toggles cleanly.
    CHECK(Discrete::ModuleLifecycle::kErrNotOwner != 0);
    CHECK(!Discrete::ModuleLifecycle::OwnsInit());
    Discrete::ModuleLifecycle::SetOwnsInit(true);
    CHECK(Discrete::ModuleLifecycle::OwnsInit());
    Discrete::ModuleLifecycle::SetOwnsInit(false);
    CHECK(!Discrete::ModuleLifecycle::OwnsInit());
}

// Helper for A-M6/A-L14 test: raw SEH read leaf using our MemoryAccessExceptionFilter
// directly (without IsReadable pre-check, simulating a TOCTOU / stale-cache hit
// on a PAGE_GUARD page or C++ exception inside a guarded call).
#pragma warning(push)
#pragma warning(disable: 4509)
bool TestRawReadWithFilter(const void* src, void* dst, std::size_t n) noexcept {
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (Discrete::Seh::MemoryAccessExceptionFilter(
                    GetExceptionCode(), GetExceptionInformation())) {
        return false;
    }
}

void TestThrowInsideSehFilter() {
    __try {
        throw std::bad_alloc();
    } __except (Discrete::Seh::MemoryAccessExceptionFilter(
                    GetExceptionCode(), GetExceptionInformation())) {
        // Must NEVER execute for C++ exceptions (0xE06D7363).
    }
}
#pragma warning(pop)

// ============================================================================
// A-M6 + A-L14 — Targeted SEH filter (never swallows C++ bad_alloc, preserves
// PAGE_GUARD on TOCTOU hit) & 64 KiB window-boundary straddle scan parity
// ============================================================================
void Test_AM6_AL14_SehFilterGuardPageAndWindowStraddle() {
    using namespace Discrete::Memory;

    // 1. A-M6: filter classification.
    CHECK(Discrete::Seh::MemoryAccessExceptionFilter(EXCEPTION_ACCESS_VIOLATION, nullptr)
          == EXCEPTION_EXECUTE_HANDLER);
    CHECK(Discrete::Seh::MemoryAccessExceptionFilter(EXCEPTION_IN_PAGE_ERROR, nullptr)
          == EXCEPTION_EXECUTE_HANDLER);
    CHECK(Discrete::Seh::MemoryAccessExceptionFilter(0xE06D7363u /*MSVC C++*/, nullptr)
          == EXCEPTION_CONTINUE_SEARCH);
    CHECK(Discrete::Seh::MemoryAccessExceptionFilter(EXCEPTION_BREAKPOINT, nullptr)
          == EXCEPTION_CONTINUE_SEARCH);

    // 2. A-M6: real C++ std::bad_alloc propagates cleanly through the SEH filter.
    bool caughtBadAlloc = false;
    try {
        TestThrowInsideSehFilter();
    } catch (const std::bad_alloc&) {
        caughtBadAlloc = true;
    }
    CHECK(caughtBadAlloc);

    // 3. A-L14: touching a PAGE_GUARD page via our SEH filter returns false AND
    //    restores PAGE_GUARD on that exact page so guard semantics survive.
    {
        void* guardPage = ::VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE,
                                         PAGE_READWRITE | PAGE_GUARD);
        CHECK(guardPage != nullptr);
        if (guardPage != nullptr) {
            std::uint32_t out = 0;
            CHECK(!TestRawReadWithFilter(guardPage, &out, sizeof(out)));

            MEMORY_BASIC_INFORMATION mbi{};
            CHECK(::VirtualQuery(guardPage, &mbi, sizeof(mbi)) != 0);
            CHECK((mbi.Protect & PAGE_GUARD) != 0);

            ::VirtualFree(guardPage, 0, MEM_RELEASE);
        }
    }

    // 4. A-M6: multi-window ScanForValue & ScanForString (>128 KiB region) catch
    //    values and strings straddling the 64 KiB window seam with exact addresses
    //    and zero duplicates.
    {
        constexpr std::size_t kBufSize = 200u * 1024u;
        void* region = ::VirtualAlloc(nullptr, kBufSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        CHECK(region != nullptr);
        if (region != nullptr) {
            auto* bytes = static_cast<std::uint8_t*>(region);
            std::memset(bytes, 0x11, kBufSize);

            // Place a unique Int64 straddling the 64 KiB boundary (offset 65533,
            // stride=1) and another straddling the 128 KiB boundary (offset 131070).
            const std::int64_t magicVal = 0x5A3C6E91B2D47F05ll;
            const std::size_t valOff1 = 65533u;
            const std::size_t valOff2 = 131070u;
            std::memcpy(bytes + valOff1, &magicVal, sizeof(magicVal));
            std::memcpy(bytes + valOff2, &magicVal, sizeof(magicVal));

            ValueScanRequest vreq{};
            vreq.dataType  = ValueDataType::Int64;
            vreq.scanType  = ValueScanType::Exact;
            vreq.alignment = 1;
            vreq.limit     = 64;
            std::memcpy(vreq.lo, &magicVal, sizeof(magicVal));

            auto vhits = ScanForValue(vreq, nullptr);
            int found1 = 0, found2 = 0;
            for (const auto& h : vhits) {
                if (h.addr == bytes + valOff1) ++found1;
                if (h.addr == bytes + valOff2) ++found2;
            }
            CHECK(found1 == 1);
            CHECK(found2 == 1);

            // Place a unique 12-byte string straddling the 64 KiB boundary with
            // non-dividing stride=3 (65532 % 3 == 0, spans [65532, 65544) across 65536!).
            const char needleStr[] = "STRADDLE_64K";
            const std::size_t strOff = 65532u;
            std::memcpy(bytes + strOff, needleStr, 12u);

            StringScanRequest sreq{};
            sreq.needle.assign(reinterpret_cast<const std::uint8_t*>(needleStr),
                               reinterpret_cast<const std::uint8_t*>(needleStr) + 12u);
            sreq.alignment = 3;
            sreq.limit     = 64;

            auto shits = ScanForString(sreq, nullptr);
            int foundStr = 0;
            for (const auto& h : shits) {
                if (h.addr == bytes + strOff) ++foundStr;
            }
            CHECK(foundStr == 1);

            ::VirtualFree(region, 0, MEM_RELEASE);
        }
    }
}

// ============================================================================
// B-M4 + B-M5 — Static field storage classification (const/literal &
// thread-static rejection, unified int32 offset) & strict UTF-8 class names
// ============================================================================
void Test_BM4_BM5_StaticFieldStorageAndUtf8Names() {
    using namespace Discrete::ManagedLayout;
    using namespace Discrete::AddressBounds;

    // 1. B-M4: NormalizeFieldOffset32 produces identical int32_t sentinels on
    //    both signed Mono int and unsigned IL2CPP size_t inputs.
    CHECK(NormalizeFieldOffset32<int>(0) == 0);
    CHECK(NormalizeFieldOffset32<int>(0x18) == 0x18);
    CHECK(NormalizeFieldOffset32<int>(-1) == -1);
    CHECK(NormalizeFieldOffset32<std::size_t>(0u) == 0);
    CHECK(NormalizeFieldOffset32<std::size_t>(0x7FFFFFFFu) == 0x7FFFFFFF);
    CHECK(NormalizeFieldOffset32<std::size_t>(0x80000000u) == -1);
    CHECK(NormalizeFieldOffset32<std::size_t>(static_cast<std::size_t>(-1)) == -1);

    // 2. B-M4: ClassifyStaticFieldStorage rejects non-static, literal (0x40),
    //    and thread-static (-1) while accepting normal static fields (incl. offset 0).
    CHECK(ClassifyStaticFieldStorage(0x00u, 0x10) == StaticFieldStorageKind::NotStatic);
    CHECK(std::strcmp(StaticFieldStorageReason(StaticFieldStorageKind::NotStatic), "not_static") == 0);

    CHECK(ClassifyStaticFieldStorage(kFieldAttrStatic | kFieldAttrLiteral, 0)
          == StaticFieldStorageKind::Literal);
    CHECK(ClassifyStaticFieldStorage(kFieldAttrStatic | kFieldAttrLiteral, -1)
          == StaticFieldStorageKind::Literal);
    CHECK(std::strcmp(StaticFieldStorageReason(StaticFieldStorageKind::Literal), "literal") == 0);

    CHECK(ClassifyStaticFieldStorage(kFieldAttrStatic, -1)
          == StaticFieldStorageKind::ThreadStatic);
    CHECK(ClassifyStaticFieldStorage(
              kFieldAttrStatic,
              NormalizeFieldOffset32(static_cast<std::size_t>(-1)))
          == StaticFieldStorageKind::ThreadStatic);
    CHECK(std::strcmp(StaticFieldStorageReason(StaticFieldStorageKind::ThreadStatic), "thread_static") == 0);

    CHECK(ClassifyStaticFieldStorage(kFieldAttrStatic, 0) == StaticFieldStorageKind::Regular);
    CHECK(ClassifyStaticFieldStorage(kFieldAttrStatic, 0x24) == StaticFieldStorageKind::Regular);
    CHECK(std::strcmp(StaticFieldStorageReason(StaticFieldStorageKind::Regular), "") == 0);

    // 3. B-M5: LooksLikePlausibleClassName accepts printable ASCII + valid
    //    multibyte UTF-8 (CJK, full-width, ConfuserEx Unicode, 4-byte emoji)
    //    and rejects control chars, malformed/overlong/surrogate UTF-8, and
    //    empty / unterminated buffers.
    CHECK(LooksLikePlausibleClassName("PlayerController", 64));
    CHECK(LooksLikePlausibleClassName("List`1<System.Int32>", 64));
    // UTF-8 CJK ("主角資料" = E4 B8 BB E8 A7 92 E8 B3 87 E6 96 99)
    const char cjkName[] = "\xE4\xB8\xBB\xE8\xA7\x92\xE8\xB3\x87\xE6\x96\x99";
    CHECK(LooksLikePlausibleClassName(cjkName, sizeof(cjkName)));
    // UTF-8 Japanese + ASCII ("プレイヤー_Mgr")
    const char jpName[] = "\xE3\x83\x97\xE3\x83\xAC\xE3\x82\xA4\xE3\x83\xA4\xE3\x83\xBC_Mgr";
    CHECK(LooksLikePlausibleClassName(jpName, sizeof(jpName)));
    // 2-byte UTF-8 ("Café")
    const char latin2[] = "Caf\xC3\xA9";
    CHECK(LooksLikePlausibleClassName(latin2, sizeof(latin2)));
    // 4-byte UTF-8 (U+1F600 = F0 9F 98 80)
    const char utf8Four[] = "Sym_\xF0\x9F\x98\x80";
    CHECK(LooksLikePlausibleClassName(utf8Four, sizeof(utf8Four)));

    // Rejections:
    CHECK(!LooksLikePlausibleClassName("", 1));
    const char noNul[4] = {'A', 'B', 'C', 'D'};
    CHECK(!LooksLikePlausibleClassName(noNul, 4));
    CHECK(!LooksLikePlausibleClassName("Bad\x01Name", 16));
    CHECK(!LooksLikePlausibleClassName("Bad\nName", 16));
    CHECK(!LooksLikePlausibleClassName("Bad\x7FName", 16));
    CHECK(!LooksLikePlausibleClassName("\x80" "bareCont", 16));        // stray continuation
    CHECK(!LooksLikePlausibleClassName("\xC0\xAF", 8));                // overlong 2-byte
    CHECK(!LooksLikePlausibleClassName("\xE0\x80\xAF", 8));            // overlong 3-byte
    CHECK(!LooksLikePlausibleClassName("\xED\xA0\x80", 8));            // UTF-16 surrogate U+D800
    CHECK(!LooksLikePlausibleClassName("\xF0\x80\x80\xAF", 8));        // overlong 4-byte
    CHECK(!LooksLikePlausibleClassName("\xF4\x90\x80\x80", 8));        // > U+10FFFF
    CHECK(!LooksLikePlausibleClassName("trunc\xE4\xB8", 16));          // truncated 3-byte
}

// ============================================================================
// B-M1 + B-M2 + B-M3 — MethodInvoker formal-type arg validation, RAII GC-root
// pinning for string args, and safe return classification (Bool / Enum /
// non-opt-in ValueType -> Unsupported)
// ============================================================================
void Test_BM1_BM2_BM3_MethodInvokeValidationAndGcRoots() {
    using namespace Discrete::Shared;
    constexpr std::uint32_t kMaxStruct = 128u;

    // --- 1. B-M1: Primitive exact-width matching ---
    {
        FormalParamDescriptor pInt32{"System.Int32", true, false, {}, 4, true};
        auto rOk = ValidateCallArg(InvokeArgKind::Int32, 0, kMaxStruct, pInt32);
        CHECK(rOk.ok && rOk.route == MarshalRoute::ValueSlot);

        CHECK(!ValidateCallArg(InvokeArgKind::Int64, 0, kMaxStruct, pInt32).ok);
        CHECK(!ValidateCallArg(InvokeArgKind::UInt32, 0, kMaxStruct, pInt32).ok);
        CHECK(!ValidateCallArg(InvokeArgKind::Float, 0, kMaxStruct, pInt32).ok);
        CHECK(!ValidateCallArg(InvokeArgKind::Address, 0, kMaxStruct, pInt32).ok);
        CHECK(!ValidateCallArg(InvokeArgKind::Null, 0, kMaxStruct, pInt32).ok);
        CHECK(!ValidateCallArg(InvokeArgKind::String, 0, kMaxStruct, pInt32).ok);

        FormalParamDescriptor pBool{"System.Boolean", true, false, {}, 1, true};
        auto rBool = ValidateCallArg(InvokeArgKind::Bool, 0, kMaxStruct, pBool);
        CHECK(rBool.ok && rBool.route == MarshalRoute::ValueSlot);
        CHECK(!ValidateCallArg(InvokeArgKind::Byte, 0, kMaxStruct, pBool).ok);

        // IntPtr / UIntPtr are CLR value types -> ValueSlot (never DirectPointer)
        FormalParamDescriptor pIntPtr{"System.IntPtr", true, false, {}, static_cast<std::uint32_t>(sizeof(void*)), true};
        auto rPtrAddr = ValidateCallArg(InvokeArgKind::Address, 0, kMaxStruct, pIntPtr);
        CHECK(rPtrAddr.ok && rPtrAddr.route == MarshalRoute::ValueSlot);
        auto rPtrNull = ValidateCallArg(InvokeArgKind::Null, 0, kMaxStruct, pIntPtr);
        CHECK(rPtrNull.ok && rPtrNull.route == MarshalRoute::ValueSlot);
        CHECK(!ValidateCallArg(InvokeArgKind::Int64, 0, kMaxStruct, pIntPtr).ok);

        // Unmanaged pointer (*) -> ValueSlot for Address/Null
        FormalParamDescriptor pRawPtr{"System.Byte*", false, false, {}, 0, false};
        auto rRaw = ValidateCallArg(InvokeArgKind::Address, 0, kMaxStruct, pRawPtr);
        CHECK(rRaw.ok && rRaw.route == MarshalRoute::ValueSlot);
        CHECK(!ValidateCallArg(InvokeArgKind::Int32, 0, kMaxStruct, pRawPtr).ok);

        // By-ref (&) -> always rejected
        FormalParamDescriptor pByRef{"System.Int32&", false, false, {}, 0, false};
        CHECK(!ValidateCallArg(InvokeArgKind::Int32, 0, kMaxStruct, pByRef).ok);
        CHECK(!ValidateCallArg(InvokeArgKind::Address, 0, kMaxStruct, pByRef).ok);
    }

    // --- 2. B-M1: System.String, System.Object, and reference types ---
    {
        FormalParamDescriptor pStr{"System.String", false, false, {}, 0, true};
        auto rStr = ValidateCallArg(InvokeArgKind::String, 0, kMaxStruct, pStr);
        CHECK(rStr.ok && rStr.route == MarshalRoute::ManagedString);
        auto rStrNull = ValidateCallArg(InvokeArgKind::Null, 0, kMaxStruct, pStr);
        CHECK(rStrNull.ok && rStrNull.route == MarshalRoute::DirectPointer);
        CHECK(!ValidateCallArg(InvokeArgKind::Address, 0, kMaxStruct, pStr).ok);
        CHECK(!ValidateCallArg(InvokeArgKind::Int32, 0, kMaxStruct, pStr).ok);

        FormalParamDescriptor pObj{"System.Object", false, false, {}, 0, true};
        CHECK(ValidateCallArg(InvokeArgKind::Address, 0, kMaxStruct, pObj).route == MarshalRoute::DirectPointer);
        CHECK(ValidateCallArg(InvokeArgKind::Null, 0, kMaxStruct, pObj).route == MarshalRoute::DirectPointer);
        CHECK(ValidateCallArg(InvokeArgKind::String, 0, kMaxStruct, pObj).route == MarshalRoute::ManagedString);
        // Crucial B-M1 guard: scalar to System.Object must be rejected (would pass &slot as MonoObject*)
        auto rBadScalar = ValidateCallArg(InvokeArgKind::Int32, 0, kMaxStruct, pObj);
        CHECK(!rBadScalar.ok && rBadScalar.error == "arg_type_mismatch");
        CHECK(!ValidateCallArg(InvokeArgKind::ValueStruct, 12, kMaxStruct, pObj).ok);

        FormalParamDescriptor pTransform{"UnityEngine.Transform", false, false, {}, 0, true};
        CHECK(ValidateCallArg(InvokeArgKind::Address, 0, kMaxStruct, pTransform).route == MarshalRoute::DirectPointer);
        CHECK(ValidateCallArg(InvokeArgKind::Null, 0, kMaxStruct, pTransform).route == MarshalRoute::DirectPointer);
        CHECK(!ValidateCallArg(InvokeArgKind::Int32, 0, kMaxStruct, pTransform).ok);
        CHECK(!ValidateCallArg(InvokeArgKind::String, 0, kMaxStruct, pTransform).ok);
        CHECK(!ValidateCallArg(InvokeArgKind::ValueStruct, 12, kMaxStruct, pTransform).ok);
    }

    // --- 3. B-M1: Enum & ValueStruct formal parameters ---
    {
        FormalParamDescriptor pEnum{"UnityEngine.FindObjectsInactive", true, true, "System.Int32", 4, true};
        auto rEnumOk = ValidateCallArg(InvokeArgKind::Int32, 0, kMaxStruct, pEnum);
        CHECK(rEnumOk.ok && rEnumOk.route == MarshalRoute::ValueSlot);
        auto rEnumStructOk = ValidateCallArg(InvokeArgKind::ValueStruct, 4, kMaxStruct, pEnum);
        CHECK(rEnumStructOk.ok && rEnumStructOk.route == MarshalRoute::ValueStructBuf);
        CHECK(!ValidateCallArg(InvokeArgKind::Byte, 0, kMaxStruct, pEnum).ok);
        CHECK(!ValidateCallArg(InvokeArgKind::Address, 0, kMaxStruct, pEnum).ok);
        CHECK(!ValidateCallArg(InvokeArgKind::Null, 0, kMaxStruct, pEnum).ok);

        FormalParamDescriptor pVec3{"UnityEngine.Vector3", true, false, {}, 12, true};
        auto rVecOk = ValidateCallArg(InvokeArgKind::ValueStruct, 12, kMaxStruct, pVec3);
        CHECK(rVecOk.ok && rVecOk.route == MarshalRoute::ValueStructBuf);
        auto rVecWrongSize = ValidateCallArg(InvokeArgKind::ValueStruct, 8, kMaxStruct, pVec3);
        CHECK(!rVecWrongSize.ok && rVecWrongSize.error == "arg_type_mismatch");
        auto rVecZero = ValidateCallArg(InvokeArgKind::ValueStruct, 0, kMaxStruct, pVec3);
        CHECK(!rVecZero.ok && rVecZero.error == "value_struct_arg_bad_size");
        auto rVecTooBig = ValidateCallArg(InvokeArgKind::ValueStruct, 256, kMaxStruct, pVec3);
        CHECK(!rVecTooBig.ok && rVecTooBig.error == "value_struct_arg_bad_size");
        // Crucial B-M1 guard: Address / scalar to struct must be rejected
        CHECK(!ValidateCallArg(InvokeArgKind::Address, 0, kMaxStruct, pVec3).ok);
        CHECK(!ValidateCallArg(InvokeArgKind::Float, 0, kMaxStruct, pVec3).ok);
        CHECK(!ValidateCallArg(InvokeArgKind::Null, 0, kMaxStruct, pVec3).ok);
    }

    // --- 4. B-M2: ScopedGcPinList pins & LIFO-releases handles + stack fallback ---
    {
        static std::vector<std::uint32_t> sFreed;
        static std::uint32_t sNextHandle = 100u;
        sFreed.clear();
        sNextHandle = 100u;

        auto mockPin = [](void*) -> std::uint32_t { return ++sNextHandle; };
        auto mockFree = [](std::uint32_t h) { sFreed.push_back(h); };

        int dummy1 = 1, dummy2 = 2, dummy3 = 3;
        {
            ScopedGcPinList<std::uint32_t, decltype(mockPin), decltype(mockFree)> pins(mockPin, mockFree);
            CHECK(pins.Pin(&dummy1));
            CHECK(pins.Pin(&dummy2));
            CHECK(pins.Pin(&dummy3));
            CHECK(pins.HandleCount() == 3u);
            CHECK(pins.StackRootCount() == 3u);
            pins.KeepAlive();
        }
        // Released automatically in LIFO order on scope exit
        CHECK(sFreed.size() == 3u);
        CHECK(sFreed[0] == 103u && sFreed[1] == 102u && sFreed[2] == 101u);

        // Fallback mode (gchandle exports unbound): stack roots hold up to kMaxStackRoots
        using NullPin  = std::uint32_t (*)(void*);
        using NullFree = void (*)(std::uint32_t);
        ScopedGcPinList<std::uint32_t, NullPin, NullFree> stackOnly(nullptr, nullptr);
        int dummies[40] = {};
        bool all32Ok = true;
        for (std::size_t i = 0; i < ScopedGcPinList<std::uint32_t, NullPin, NullFree>::kMaxStackRoots; ++i) {
            if (!stackOnly.Pin(&dummies[i])) all32Ok = false;
        }
        CHECK(all32Ok);
        CHECK(stackOnly.HandleCount() == 0u);
        CHECK(stackOnly.StackRootCount() == 32u);
        // 33rd object without gchandle API must fail cleanly
        CHECK(!stackOnly.Pin(&dummies[33]));
    }

    // --- 5. B-M3: Return classification (Void, Bool/Primitive, Enum, ValueType, Ref) ---
    {
        auto pVoid = ClassifyReturnObject("System.Void", true, false, {}, false, false);
        CHECK(pVoid.action == ReturnDecodeAction::Void);

        auto pBool = ClassifyReturnObject("System.Boolean", false, false, {}, true, false);
        CHECK(pBool.action == ReturnDecodeAction::Primitive && pBool.primitiveType == "System.Boolean");

        auto pStr = ClassifyReturnObject("System.String", false, false, {}, false, false);
        CHECK(pStr.action == ReturnDecodeAction::String);

        // Enum return decodes via underlying integer even when wantStructReturn == false!
        auto pEnum = ClassifyReturnObject("MyNs.MyFlags", false, true, "System.UInt16", true, false);
        CHECK(pEnum.action == ReturnDecodeAction::Primitive && pEnum.primitiveType == "System.UInt16");

        auto pEnumFallback = ClassifyReturnObject("MyNs.MyEnum", false, true, "", true, false);
        CHECK(pEnumFallback.action == ReturnDecodeAction::Primitive && pEnumFallback.primitiveType == "System.Int32");

        // Crucial B-M3 guard: non-opted-in struct/DateTime/Nullable MUST be Unsupported (never ReferenceAddr)
        auto pStructNoOptIn = ClassifyReturnObject("UnityEngine.Vector3", false, false, {}, true, false);
        CHECK(pStructNoOptIn.action == ReturnDecodeAction::Unsupported);

        auto pStructOptIn = ClassifyReturnObject("UnityEngine.Vector3", false, false, {}, true, true);
        CHECK(pStructOptIn.action == ReturnDecodeAction::ValueStruct);

        // Genuine reference type returns ReferenceAddr; null returns NullReference
        auto pRef = ClassifyReturnObject("UnityEngine.Transform", false, false, {}, false, false);
        CHECK(pRef.action == ReturnDecodeAction::ReferenceAddr);

        auto pNullRef = ClassifyReturnObject("UnityEngine.Transform", true, false, {}, false, false);
        CHECK(pNullRef.action == ReturnDecodeAction::NullReference);
    }
}

// ============================================================================
// B-M6 + B-M7 + B-M11 — RuntimeHealth poison latch, SEH-safe PE / stripped-page
// chunked scanning, and unified MonoVTable domain header validation
// ============================================================================
void Test_BM6_BM7_BM11_RuntimeHealthSafePeScanAndVtableValidation() {
    // 1. B-M6: RuntimeHealth poison latch preserves first root-cause op, counts
    //    subsequent faults, formats actionable restart message, and resets cleanly.
    {
        using namespace Discrete::RuntimeHealth;
        ResetForTests();
        CHECK(!IsPoisoned());
        CHECK(FormatReason().empty());

        MarkRuntimeFault("mono_class_vtable", EXCEPTION_ACCESS_VIOLATION);
        CHECK(IsPoisoned());
        auto s1 = GetSnapshot();
        CHECK(s1.poisoned);
        CHECK(std::strcmp(s1.operation, "mono_class_vtable") == 0);
        CHECK(s1.exceptionCode == EXCEPTION_ACCESS_VIOLATION);
        CHECK(s1.faultCount == 1ul);
        std::string reason = FormatReason();
        CHECK(reason.find("mono_class_vtable") != std::string::npos);
        CHECK(reason.find("restart the game") != std::string::npos);

        // Second fault increments count without clobbering original root cause.
        MarkRuntimeFault("mono_runtime_invoke", EXCEPTION_IN_PAGE_ERROR);
        auto s2 = GetSnapshot();
        CHECK(std::strcmp(s2.operation, "mono_class_vtable") == 0);
        CHECK(s2.exceptionCode == EXCEPTION_ACCESS_VIOLATION);
        CHECK(s2.faultCount == 2ul);

        ResetForTests();
        CHECK(!IsPoisoned());
    }

    // 2. B-M7: SafePeScan reads current process PE header safely, rejects
    //    corrupt/unmapped bases without AV, and scans across PAGE_NOACCESS
    //    anti-cheat stripped pages without losing readable neighbor pages.
    {
        using namespace Discrete::SafePeScan;
        // Null and invalid/unmapped bases return empty without crashing.
        CHECK(ListInitDataSectionsSafe(nullptr).empty());
        CHECK(ListInitDataSectionsSafe(reinterpret_cast<const std::uint8_t*>(0x1000u)).empty());

        // Current test exe's own module base has readable data sections.
        auto* selfBase = reinterpret_cast<const std::uint8_t*>(::GetModuleHandleW(nullptr));
        auto selfSecs = ListInitDataSectionsSafe(selfBase);
        CHECK(!selfSecs.empty());

        // Allocate 3 pages: [RW page 0][PAGE_NOACCESS page 1][RW page 2].
        // Place markers on page 0 and page 2; verify ScanReadableSpans skips
        // page 1 cleanly (zero AV) and delivers BOTH readable pages.
        constexpr std::size_t kPage = 4096u;
        void* region = ::VirtualAlloc(nullptr, 3 * kPage, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        CHECK(region != nullptr);
        if (region != nullptr) {
            auto* base = static_cast<std::uint8_t*>(region);
            std::memset(base, 0x11, kPage);
            std::memset(base + 2 * kPage, 0x22, kPage);
            const std::uint64_t marker0 = 0xCAFEBABE11223344ull;
            const std::uint64_t marker2 = 0xDEADBEEF55667788ull;
            std::memcpy(base + 0x200, &marker0, sizeof(marker0));
            std::memcpy(base + 2 * kPage + 0x300, &marker2, sizeof(marker2));

            DWORD oldProt = 0;
            BOOL stripped = ::VirtualProtect(base + kPage, kPage, PAGE_NOACCESS, &oldProt);
            CHECK(stripped != 0);

            std::vector<std::uint8_t> scratch;
            bool sawMarker0 = false;
            bool sawMarker2 = false;
            std::size_t spanCount = 0;
            std::size_t totalReadable = 0;
            ScanReadableSpans(
                base, 3 * kPage, 3 * kPage, 128u, scratch,
                [&](const std::uint8_t* local, const std::uint8_t* real, std::size_t len) -> bool {
                    ++spanCount;
                    totalReadable += len;
                    if (real == base && len == kPage) {
                        std::uint64_t v = 0;
                        std::memcpy(&v, local + 0x200, sizeof(v));
                        if (v == marker0) sawMarker0 = true;
                    }
                    if (real == base + 2 * kPage && len == kPage) {
                        std::uint64_t v = 0;
                        std::memcpy(&v, local + 0x300, sizeof(v));
                        if (v == marker2) sawMarker2 = true;
                    }
                    return true;
                });
            CHECK(spanCount == 2u);
            CHECK(totalReadable == 2 * kPage);
            CHECK(sawMarker0);
            CHECK(sawMarker2);

            ::VirtualProtect(base + kPage, kPage, PAGE_READWRITE, &oldProt);
            ::VirtualFree(region, 0, MEM_RELEASE);
        }
    }

    // 3. B-M11: ValidateMonoVtableHeader enforces identical domain + alignment
    //    validation across WhatIsAt, FindAllInstances, and ReverseLookupIndex.
    {
        using namespace Discrete::Mono;
        const std::uintptr_t kDomain = 0x20000000u;
        const std::uintptr_t kKlass  = 0x20001000u;  // 8-byte aligned
        const std::uintptr_t kVt8    = 0x20002000u;  // 8-byte aligned
        const std::uintptr_t kVt4    = 0x20002004u;  // 4 mod 8 (x86 IMT-prefixed)

        // Valid vtable with matching rootDomain passes
        CHECK(ValidateMonoVtableHeader(kVt8, kKlass, kDomain, kDomain));
        // Wrong domain (e.g. MonoType or RuntimeTypeHandle whose +0 is MonoClass*
        // but +2*ptr is NOT rootDomain) MUST be rejected!
        CHECK(!ValidateMonoVtableHeader(kVt8, kKlass, 0u, kDomain));
        CHECK(!ValidateMonoVtableHeader(kVt8, kKlass, 0x12345678u, kDomain));
        // Null rootDomain rejected
        CHECK(!ValidateMonoVtableHeader(kVt8, kKlass, 0u, 0u));
        // Unaligned / low klass rejected
        CHECK(!ValidateMonoVtableHeader(kVt8, kKlass + 4u, kDomain, kDomain));
        CHECK(!ValidateMonoVtableHeader(kVt8, 0x1000u, kDomain, kDomain));
        // Architecture-specific vtable alignment (4 mod 8 valid on x86, rejected on x64)
        if constexpr (sizeof(void*) == 4) {
            CHECK(ValidateMonoVtableHeader(kVt4, kKlass, kDomain, kDomain));
        } else {
            CHECK(!ValidateMonoVtableHeader(kVt4, kKlass, kDomain, kDomain));
        }
    }
}

// ============================================================================
// B-M8 + B-M9 + B-L7 (+ B-L11 switch overflow) — Mono MemberRef matching,
// IL2CPP .pdata-bounded enclosing method attribution, and value-type offset
// ============================================================================
void Test_BM8_BM9_BL7_FieldXrefMemberRefPdataAndStructOffset() {
    using namespace Discrete::FieldXref;

    // 1. B-M8: Token classification + generic name detection + MemberRef identity matching
    {
        CHECK(IsFieldDefToken(0x04000012u));
        CHECK(!IsFieldDefToken(0x0A000012u));
        CHECK(IsMemberRefToken(0x0A000042u));
        CHECK(!IsMemberRefToken(0x04000042u));
        CHECK(IsGenericTypeName("Singleton`1"));
        CHECK(IsGenericTypeName("Outer`1/Inner"));
        CHECK(!IsGenericTypeName("PlayerController"));

        int fDef = 1, fInflated = 2, fOther = 3, imgA = 10, imgB = 20;
        MonoFieldIdentity target{&fDef, &imgA, 0x04000007u, "Game", "Singleton`1", "instance"};

        // Direct pointer match
        CHECK(MatchesMonoFieldIdentity(target, target));

        // Inflated generic field sharing FieldDef token in same image -> MATCH
        MonoFieldIdentity inflatedByTok{&fInflated, &imgA, 0x04000007u, "Game", "Singleton`1", "instance"};
        CHECK(MatchesMonoFieldIdentity(inflatedByTok, target));

        // Inflated generic field with token==0 matched by (image, ns, defName, fieldName) -> MATCH
        MonoFieldIdentity inflatedByName{&fInflated, &imgA, 0u, "Game", "Singleton`1", "instance"};
        CHECK(MatchesMonoFieldIdentity(inflatedByName, target));

        // Same token / name in a DIFFERENT image -> REJECTED
        MonoFieldIdentity wrongImg{&fOther, &imgB, 0x04000007u, "Game", "Singleton`1", "instance"};
        CHECK(!MatchesMonoFieldIdentity(wrongImg, target));

        // Different field name in same generic class -> REJECTED
        MonoFieldIdentity wrongField{&fOther, &imgA, 0x04000008u, "Game", "Singleton`1", "other"};
        CHECK(!MatchesMonoFieldIdentity(wrongField, target));

        // Null field pointer -> REJECTED
        MonoFieldIdentity nullCand{nullptr, &imgA, 0x04000007u, "Game", "Singleton`1", "instance"};
        CHECK(!MatchesMonoFieldIdentity(nullCand, target));
    }

    // 2. B-L11: Overflow-safe IL switch advance (critical on 32-bit x86!)
    {
        CHECK(SafeIlSwitchAdvance(0u, 5u) == 5u);
        CHECK(SafeIlSwitchAdvance(3u, 17u) == 17u);
        CHECK(SafeIlSwitchAdvance(3u, 16u) == 0u);
        CHECK(SafeIlSwitchAdvance(0u, 4u) == 0u);
        // 0x40000001 * 4 wraps to 4 in 32-bit size_t — must NOT pass remaining==16!
        CHECK(SafeIlSwitchAdvance(0x40000001u, 16u) == 0u);
        CHECK(SafeIlSwitchAdvance(0xFFFFFFFFu, 1024u) == 0u);
    }

    // 3. B-M9: FindEnclosingMethodExact with synthetic .pdata function table
    {
        struct FakeMethod {
            std::uintptr_t address;
            const char*    name;
        };
        // Target image owns methods at 0x1000 and 0x3000; another assembly owns
        // an interleaved function at 0x2000..0x2200, and a tiny leaf at 0x1100.
        std::vector<FakeMethod> idx = {
            {0x1000u, "TargetA"},
            {0x3000u, "TargetB"},
            {0x4000u, "TargetLeaf"},
        };
        auto fakePdata = [](std::uintptr_t ip, std::uintptr_t& b, std::uintptr_t& e) -> bool {
            if (ip >= 0x1000u && ip < 0x1080u) { b = 0x1000u; e = 0x1080u; return true; }
            if (ip >= 0x2000u && ip < 0x2200u) { b = 0x2000u; e = 0x2200u; return true; }
            if (ip >= 0x3000u && ip < 0x3100u) { b = 0x3000u; e = 0x3100u; return true; }
            return false;  // no .pdata (e.g. leaf function or gap)
        };

        // Inside TargetA [0x1000, 0x1080) -> exact hit TargetA
        const FakeMethod* h1 = FindEnclosingMethodExact(idx, 0x1040u, fakePdata);
        CHECK(h1 != nullptr && std::strcmp(h1->name, "TargetA") == 0);

        // Inside OTHER assembly's interleaved function [0x2000, 0x2200) -> REJECTED (NOT TargetA!)
        CHECK(FindEnclosingMethodExact(idx, 0x2050u, fakePdata) == nullptr);

        // Inside un-indexed leaf at 0x1100 (after TargetA's .pdata end 0x1080) -> REJECTED!
        CHECK(FindEnclosingMethodExact(idx, 0x1100u, fakePdata) == nullptr);

        // Inside TargetB [0x3000, 0x3100) -> exact hit TargetB
        const FakeMethod* h2 = FindEnclosingMethodExact(idx, 0x30F0u, fakePdata);
        CHECK(h2 != nullptr && std::strcmp(h2->name, "TargetB") == 0);

        // Inside TargetLeaf (no .pdata, within kLeafFallbackMaxBytes) -> accepted
        const FakeMethod* hLeaf = FindEnclosingMethodExact(idx, 0x4020u, fakePdata);
        CHECK(hLeaf != nullptr && std::strcmp(hLeaf->name, "TargetLeaf") == 0);

        // Way past TargetLeaf (> kLeafFallbackMaxBytes) -> REJECTED
        CHECK(FindEnclosingMethodExact(idx, 0x4500u, fakePdata) == nullptr);

        // Before first indexed method -> REJECTED
        CHECK(FindEnclosingMethodExact(idx, 0x0500u, fakePdata) == nullptr);

        // Also sanity-check live RtlLookupFunctionEntry on x64 with our own test function!
#if defined(_M_AMD64)
        DWORD64 imgBase = 0;
        const std::uintptr_t selfFn = reinterpret_cast<std::uintptr_t>(&Test_BM8_BM9_BL7_FieldXrefMemberRefPdataAndStructOffset);
        PRUNTIME_FUNCTION rf = ::RtlLookupFunctionEntry(static_cast<DWORD64>(selfFn), &imgBase, nullptr);
        CHECK(rf != nullptr && imgBase != 0);
        if (rf != nullptr) {
            CHECK(static_cast<std::uintptr_t>(imgBase + rf->BeginAddress) <= selfFn);
            CHECK(static_cast<std::uintptr_t>(imgBase + rf->EndAddress) > selfFn);
        }
#endif
    }

    // 4. B-L7: Value-type struct unboxed instance offset adjustment
    {
        // Reference type: offset unchanged (includes 0x10 header on x64)
        CHECK(ComputeIl2CppInstanceScanOffset(0x18u, false, 0x10u) == 0x18u);
        // Value type (struct) on x64: boxed offset 0x10/0x14/0x18 -> unboxed 0x00/0x04/0x08
        CHECK(ComputeIl2CppInstanceScanOffset(0x10u, true, 0x10u) == 0x00u);
        CHECK(ComputeIl2CppInstanceScanOffset(0x14u, true, 0x10u) == 0x04u);
        CHECK(ComputeIl2CppInstanceScanOffset(0x18u, true, 0x10u) == 0x08u);
        // Value type on x86 (0x08 header): boxed 0x0C -> unboxed 0x04
        CHECK(ComputeIl2CppInstanceScanOffset(0x0Cu, true, 0x08u) == 0x04u);
        // Guard against underflow if a runtime already reports offset < header
        CHECK(ComputeIl2CppInstanceScanOffset(0x04u, true, 0x10u) == 0x04u);
    }
}

// ============================================================================
// C-M6 + C-M8 — searchSymbols single-assembly truncation & strict lossless
// 64-bit integer / non-truncating float JSON parsing
// ============================================================================
void Test_CM6_CM8_SearchSymbolsTruncAndStrictJsonNumerics() {
    using namespace Discrete::ExportHelpers;
    using nlohmann::json;

    // 1. C-M6: single-assembly cap sets truncated=true and leaves truncatedAssemblies empty;
    //    multi-assembly cap appends to truncatedAssemblies and leaves global truncated=false.
    {
        bool trunc = false;
        std::vector<std::string> truncAsms;
        RecordSearchSymbolsAsmCap(/*multiAsm=*/false, /*thisAsmCapped=*/true,
                                  "Assembly-CSharp", trunc, truncAsms);
        CHECK(trunc);
        CHECK(truncAsms.empty());

        bool multiTrunc = false;
        std::vector<std::string> multiTruncAsms;
        RecordSearchSymbolsAsmCap(/*multiAsm=*/true, /*thisAsmCapped=*/true,
                                  "Assembly-CSharp", multiTrunc, multiTruncAsms);
        CHECK(!multiTrunc);
        CHECK(multiTruncAsms.size() == 1u && multiTruncAsms[0] == "Assembly-CSharp");
    }

    // 2. C-M8: full 64-bit integer precision (values > 2^53) via JSON int and string
    {
        std::int64_t iv = 0;
        const std::int64_t ticks = 638500000000000123LL;  // > 2^53 (loses bits in double!)
        CHECK(ParseStrictJsonInt64(json(ticks), iv) && iv == ticks);
        CHECK(ParseStrictJsonInt64(json("638500000000000123"), iv) && iv == ticks);
        CHECK(ParseStrictJsonInt64(json("-9223372036854775808"), iv) && iv == std::numeric_limits<std::int64_t>::min());
        CHECK(ParseStrictJsonInt64(json("0x7FFFFFFFFFFFFFFF"), iv) && iv == std::numeric_limits<std::int64_t>::max());

        // Exact integral doubles succeed; fractional / non-finite / out-of-range doubles fail!
        CHECK(ParseStrictJsonInt64(json(42.0), iv) && iv == 42);
        CHECK(!ParseStrictJsonInt64(json(3.7), iv));
        CHECK(!ParseStrictJsonInt64(json(-3.1), iv));
        CHECK(!ParseStrictInt64FromDouble(std::numeric_limits<double>::quiet_NaN(), iv));
        CHECK(!ParseStrictInt64FromDouble(std::numeric_limits<double>::infinity(), iv));
        CHECK(!ParseStrictInt64FromDouble(1e30, iv));
        CHECK(!ParseStrictInt64FromDouble(9223372036854775808.0, iv));  // 2^63 overflows int64

        // Strings with fractional or trailing garbage fail; > INT64_MAX unsigned fails
        CHECK(!ParseStrictJsonInt64(json("3.7"), iv));
        CHECK(!ParseStrictJsonInt64(json("123abc"), iv));
        CHECK(!ParseStrictJsonInt64(json(UINT64_MAX), iv));

        // Unsigned 64-bit precision (Steam IDs / full uint64 range)
        std::uint64_t uv = 0;
        const std::uint64_t steamId = 76561198012345679ULL;  // > 2^53
        CHECK(ParseStrictJsonUInt64(json(steamId), uv) && uv == steamId);
        CHECK(ParseStrictJsonUInt64(json("18446744073709551615"), uv) && uv == UINT64_MAX);
        CHECK(ParseStrictJsonUInt64(json("0xFFFFFFFFFFFFFFFF"), uv) && uv == UINT64_MAX);
        CHECK(ParseStrictJsonUInt64(json(100.0), uv) && uv == 100u);
        CHECK(!ParseStrictJsonUInt64(json(3.7), uv));
        CHECK(!ParseStrictJsonUInt64(json(-1), uv));
        CHECK(!ParseStrictJsonUInt64(json("-1"), uv));
        CHECK(!ParseStrictUInt64FromDouble(18446744073709551616.0, uv));  // 2^64 overflows uint64
        CHECK(!ParseStrictUInt64FromDouble(std::numeric_limits<double>::quiet_NaN(), uv));

        // Strict double parser
        double dv = 0.0;
        CHECK(ParseStrictJsonDouble(json(3.75), dv) && dv == 3.75);
        CHECK(ParseStrictJsonDouble(json("3.75"), dv) && dv == 3.75);
        CHECK(!ParseStrictJsonDouble(json("3.75xyz"), dv));
        CHECK(!ParseStrictJsonDouble(json("NaN"), dv));
    }
}

// ============================================================================
// C-M4 + C-M5 — Snapshot array element layout resolution (no pointer stride
// guessing for struct/enum arrays), IL2CPP streaming instance header
// revalidation, idempotent cursor replay, and lock-free SessionManager decode
// ============================================================================
void Test_CM4_CM5_SnapshotArrayLayoutAndStreamingSafety() {
    using namespace Discrete::ExportHelpers;
    using namespace Discrete::Snapshot;

    // 1. C-M4: ResolveSnapshotArrayElementPlan across primitives, enums, named
    //    math structs, custom structs, reference types, jagged arrays, and unknown
    {
        const SnapshotArrayElemHint noHint{};

        // Primitives on x64 and x86 (including IntPtr pointer-width sensitivity)
        auto pInt32 = ResolveSnapshotArrayElementPlan("System.Int32[]", noHint, 8);
        CHECK(pInt32.kind == SnapshotArrayElemDecodeKind::Primitive);
        CHECK(pInt32.elemSize == 4u && !pInt32.isPointer);
        CHECK(pInt32.elementType == "System.Int32" && pInt32.decodeType == "System.Int32");

        auto pIntPtrX86 = ResolveSnapshotArrayElementPlan("System.IntPtr[]", noHint, 4);
        CHECK(pIntPtrX86.kind == SnapshotArrayElemDecodeKind::Primitive);
        CHECK(pIntPtrX86.elemSize == 4u && !pIntPtrX86.isPointer);

        // System.String / System.Object / jagged arrays always reference elements
        auto pStr = ResolveSnapshotArrayElementPlan("System.String[]", noHint, 8);
        CHECK(pStr.kind == SnapshotArrayElemDecodeKind::StringRef);
        CHECK(pStr.elemSize == 8u && pStr.isPointer);

        auto pJag = ResolveSnapshotArrayElementPlan("System.Int32[][]", noHint, 4);
        CHECK(pJag.kind == SnapshotArrayElemDecodeKind::ObjectRef);
        CHECK(pJag.elemSize == 4u && pJag.isPointer && pJag.elementType == "System.Int32[]");

        // Named Unity math structs never use pointer stride
        auto pVec3 = ResolveSnapshotArrayElementPlan("UnityEngine.Vector3[]", noHint, 8);
        CHECK(pVec3.kind == SnapshotArrayElemDecodeKind::NamedMathStruct);
        CHECK(pVec3.elemSize == 12u && !pVec3.isPointer);
        CHECK(pVec3.decodeType == "UnityEngine.Vector3");

        // Enum array with runtime hint -> Primitive using exact underlying width (NOT 8-byte ptr!)
        SnapshotArrayElemHint enumHint{};
        enumHint.layoutKnown = true;
        enumHint.isValueType = true;
        enumHint.isEnum = true;
        enumHint.valueSize = 4;
        enumHint.underlyingTypeName = "System.Int32";
        auto pEnum = ResolveSnapshotArrayElementPlan("Game.ElementKind[]", enumHint, 8);
        CHECK(pEnum.kind == SnapshotArrayElemDecodeKind::Primitive);
        CHECK(pEnum.elemSize == 4u && !pEnum.isPointer);
        CHECK(pEnum.elementType == "Game.ElementKind");
        CHECK(pEnum.decodeType == "System.Int32");

        SnapshotArrayElemHint byteEnumHint{};
        byteEnumHint.layoutKnown = true;
        byteEnumHint.isValueType = true;
        byteEnumHint.isEnum = true;
        byteEnumHint.underlyingTypeName = "System.Byte";
        auto pByteEnum = ResolveSnapshotArrayElementPlan("Game.TinyFlag[]", byteEnumHint, 8);
        CHECK(pByteEnum.kind == SnapshotArrayElemDecodeKind::Primitive);
        CHECK(pByteEnum.elemSize == 1u && !pByteEnum.isPointer && pByteEnum.decodeType == "System.Byte");

        // Custom value-type struct array -> RawValueStruct with exact stride, isPointer=false
        SnapshotArrayElemHint structHint{};
        structHint.layoutKnown = true;
        structHint.isValueType = true;
        structHint.isEnum = false;
        structHint.valueSize = 24;
        auto pStruct = ResolveSnapshotArrayElementPlan("Game.ItemSlot[]", structHint, 8);
        CHECK(pStruct.kind == SnapshotArrayElemDecodeKind::RawValueStruct);
        CHECK(pStruct.elemSize == 24u && !pStruct.isPointer);

        // Confirmed reference class -> ObjectRef with pointer stride & isPointer=true
        SnapshotArrayElemHint refHint{};
        refHint.layoutKnown = true;
        refHint.isValueType = false;
        refHint.valueSize = 0;
        auto pRef = ResolveSnapshotArrayElementPlan("Game.Enemy[]", refHint, 8);
        CHECK(pRef.kind == SnapshotArrayElemDecodeKind::ObjectRef);
        CHECK(pRef.elemSize == 8u && pRef.isPointer);

        // Unknown user type without runtime hint -> Unknown (NEVER guesses pointer stride!)
        auto pUnknown = ResolveSnapshotArrayElementPlan("Game.OpaqueStruct[]", noHint, 8);
        CHECK(pUnknown.kind == SnapshotArrayElemDecodeKind::Unknown);
        CHECK(pUnknown.elemSize == 0u && !pUnknown.isPointer);

        // Value type with invalid/oversized valueSize -> Unknown
        SnapshotArrayElemHint badStructHint{};
        badStructHint.layoutKnown = true;
        badStructHint.isValueType = true;
        badStructHint.valueSize = 0;
        CHECK(ResolveSnapshotArrayElementPlan("Game.ZeroStruct[]", badStructHint, 8).kind
              == SnapshotArrayElemDecodeKind::Unknown);

        badStructHint.valueSize = kMaxSnapshotArrayElemSize + 1u;
        CHECK(ResolveSnapshotArrayElementPlan("Game.GiantStruct[]", badStructHint, 8).kind
              == SnapshotArrayElemDecodeKind::Unknown);
    }

    // 2. C-M5(1): Live IL2CPP object header validation
    {
        const std::uintptr_t klassA = static_cast<std::uintptr_t>(0x12345678u);
        const std::uintptr_t klassB = static_cast<std::uintptr_t>(0x12349999u);
        CHECK(IsLiveIl2CppInstanceHeader(klassA, klassA));
        CHECK(!IsLiveIl2CppInstanceHeader(klassB, klassA));
        CHECK(!IsLiveIl2CppInstanceHeader(0, klassA));
        CHECK(!IsLiveIl2CppInstanceHeader(0, 0));
    }

    // 3. C-M5(2): Idempotent streaming cursor tracking
    {
        std::size_t hwm = 0;
        CHECK(ShouldChargeStreamingPlanRow(0, hwm) && hwm == 1u);
        CHECK(ShouldChargeStreamingPlanRow(1, hwm) && hwm == 2u);
        // Replay indices 0 and 1 -> must NOT charge stats or regress hwm
        CHECK(!ShouldChargeStreamingPlanRow(0, hwm) && hwm == 2u);
        CHECK(!ShouldChargeStreamingPlanRow(1, hwm) && hwm == 2u);
        // Fresh index 2 charges and advances hwm to 3
        CHECK(ShouldChargeStreamingPlanRow(2, hwm) && hwm == 3u);
    }

    // 4. C-M5(3): SessionManager::View releases global mutex before streaming
    //    decode / sleep; concurrent SessionManager::End completes immediately
    //    while the caller's local shared_ptr keeps the decoder alive cleanly.
    {
        struct BlockingMockDecoder final : public ISnapshotStreamDecoder {
            std::atomic<bool> entered{false};
            std::atomic<bool> release{false};
            std::atomic<int>* destroyedCounter = nullptr;

            explicit BlockingMockDecoder(std::atomic<int>* dc) : destroyedCounter(dc) {}
            ~BlockingMockDecoder() override {
                if (destroyedCounter != nullptr) ++(*destroyedCounter);
            }
            std::size_t DecodeRange(std::size_t cursor, std::size_t limit,
                                    nlohmann::json& out) override {
                entered.store(true, std::memory_order_release);
                while (!release.load(std::memory_order_acquire)) {
                    ::Sleep(5);
                }
                out.push_back(nlohmann::json{{"name", "MockClass"}});
                return cursor + limit;
            }
            std::size_t   PlanSize() const override          { return 1; }
            nlohmann::json DecodeStats() const override      { return nlohmann::json{{"staleInstances", 0}}; }
            bool          TruncatedByMemory() const override { return false; }
            bool          TruncatedByCount() const override  { return false; }
            std::uint64_t PeakPrivateUsage() const override  { return 0; }
        };

        std::atomic<int> destroyedCount{0};
        auto mock = std::make_shared<BlockingMockDecoder>(&destroyedCount);
        auto sess = std::make_unique<Session>();
        sess->streamDecoder = mock;
        const std::uint64_t sid = SessionManager::Instance().Begin(std::move(sess));

        // Simulate CmdSnapshotChunk: grab shared_ptr inside View, decode OUTSIDE View.
        std::shared_ptr<ISnapshotStreamDecoder> grabbed;
        const bool found = SessionManager::Instance().View(sid, [&](Session& s) {
            grabbed = s.streamDecoder;
        });
        CHECK(found && grabbed != nullptr);

        nlohmann::json chunkOut = nlohmann::json::array();
        std::thread decodeWorker([&]() {
            grabbed->DecodeRange(0, 1, chunkOut);
        });

        // Wait until decodeWorker is actively inside DecodeRange.
        while (!mock->entered.load(std::memory_order_acquire)) {
            ::Sleep(1);
        }

        // Now End() the session on this thread while decodeWorker is blocked:
        // must return immediately (not deadlocked on SessionManager::mu_) and
        // must NOT destroy the decoder while decodeWorker still holds `grabbed`!
        const bool ended = SessionManager::Instance().End(sid);
        CHECK(ended);
        CHECK(destroyedCount.load() == 0);

        mock->release.store(true, std::memory_order_release);
        decodeWorker.join();
        CHECK(chunkOut.size() == 1u);

        grabbed.reset();
        mock.reset();
        CHECK(destroyedCount.load() == 1);
    }
}

// ============================================================================
// D-M6 — IL2CPP CodeGenModule methodPointers[] token-RID indexing + mismatch count
// ============================================================================
void Test_DM6_MethodTokenPointerIndex() {
    using Discrete::Shared::ResolveMethodPointerIndex;

    // 1. Contiguous image where token RID - 1 matches (globalIdx - image.methodStart)
    {
        bool mismatch = true;
        auto idx = ResolveMethodPointerIndex(0x06000001u, 500, 500, 100u, mismatch);
        CHECK(idx.has_value() && *idx == 0u);
        CHECK(!mismatch);

        auto idx2 = ResolveMethodPointerIndex(0x0600002Au, 541, 500, 100u, mismatch);
        CHECK(idx2.has_value() && *idx2 == 41u);
        CHECK(!mismatch);
    }

    // 2. Non-contiguous / reordered metadata: token RID differs from globalIdx - methodStart
    //    Must return RID - 1 AND set outIndexMismatch = true.
    {
        bool mismatch = false;
        auto idx = ResolveMethodPointerIndex(0x0600000Au, 502, 500, 100u, mismatch);
        CHECK(idx.has_value() && *idx == 9u);
        CHECK(mismatch);
    }

    // 3. Zero RID (sentinel / invalid token): must return nullopt without underflow
    {
        bool mismatch = true;
        auto idx = ResolveMethodPointerIndex(0x06000000u, 500, 500, 100u, mismatch);
        CHECK(!idx.has_value());
        CHECK(!mismatch);

        auto idxZero = ResolveMethodPointerIndex(0u, 0, 0, 100u, mismatch);
        CHECK(!idxZero.has_value());
    }

    // 4. RID beyond module.methodCount: returns nullopt (and still flags mismatch if present)
    {
        bool mismatch = false;
        auto idx = ResolveMethodPointerIndex(0x06000065u, 500, 500, 100u, mismatch);
        CHECK(!idx.has_value());
        CHECK(mismatch);
    }
}

}  // namespace

int main() {
    Test_CH1_NormalizeFieldOffset();
    Test_CH2_ResolveArrayElementLayout();
    Test_CH3_Il2CppFieldDefaultDecoder();
    Test_AH2_ScannerExceptionSafety();
    Test_BH1_MonoVtableAlignment();
    Test_AH1_PinnedModuleSurvivesFreeLibrary();
    Test_BH2_AddressBoundsAndDynamicKlass();
    Test_AM1_PerClientScanCancelAndAtomicState();
    Test_AM2_PipeLineBufferPreservesPipelinedBytes();
    Test_AM11_StringScanAvx2TailAlignment();
    Test_CM3_ClampMainThreadTimeoutMs();
    Test_BM10_AL15_CM1_IndexedPoolThreadCapAndCancel();
    Test_AM7_AM8_AL16_TeardownAndMainThreadDispatcher();
    Test_AM3_AM4_PipeSecurityAndInitOwnership();
    Test_AM6_AL14_SehFilterGuardPageAndWindowStraddle();
    Test_BM4_BM5_StaticFieldStorageAndUtf8Names();
    Test_BM1_BM2_BM3_MethodInvokeValidationAndGcRoots();
    Test_BM6_BM7_BM11_RuntimeHealthSafePeScanAndVtableValidation();
    Test_BM8_BM9_BL7_FieldXrefMemberRefPdataAndStructOffset();
    Test_CM6_CM8_SearchSymbolsTruncAndStrictJsonNumerics();
    Test_CM4_CM5_SnapshotArrayLayoutAndStreamingSafety();
    Test_DM6_MethodTokenPointerIndex();

    const char* arch = (sizeof(void*) == 4) ? "x86" : "x64";
    if (g_failures == 0) {
        std::printf("discrete_dll_test (%s): %d checks passed, 0 failed\n", arch, g_checks);
        return 0;
    }
    std::fprintf(stderr, "discrete_dll_test (%s): %d/%d checks FAILED\n", arch, g_failures, g_checks);
    return 1;
}
