# Dev Log

Append-only activity history, newest first. Each entry references a build number
from `build_number.txt` so commits can be cross-referenced. Existing entries are
never modified or rewritten — archive older entries into `docs/archive/` only if
this file grows too large.

**Reading tip:** grep `^## ` for the index, then read from the top (newest-first).

> **Looking for current state or open backlog?** See [plan.md](plan.md) for the
> current phase, open TODO list, and phase ledger.

-----

## 2026-11-26 (build 1536) — Add offline C++ DLL self-test suite (`discrete_dll_test`, x64 + x86) for C-H1 / C-H2 / C-H3 / A-H2 / B-H1

- **What changes:** Added the repository's first offline C++ DLL unit test executable (`dll/tests/dll_core_test.cpp`), compiled with `/W4 /WX` and executed on **both x64 and x86 (WOW64)** by `.\build.cmd test` and `.\build.cmd publish` (never copied into `dist\`).
  - Extracted pure, runtime-independent helpers into shared headers included by both the production DLLs and the test suite (zero code duplication):
    - `dll/src/shared/ExportHelpers.h` — `NormalizeFieldOffset` (`C-H1`), `ResolveArrayElementLayout` (`C-H2`), and `DecodeCompressedSignedInt` / `ClrTypeNameToIl2CppTag` / `DecodeIl2CppFieldDefaultByTag` (`C-H3`), replacing inline copies in `ExportAPI.cpp`.
    - `dll/src/shared/ParallelScanDriver.h` — `RunWorkerPool` (`A-H2`), used by `Memory.cpp::RunParallelScan`.
    - `dll/src/mono/MonoVtableFilter.h` — `IsAlignedMonoVtablePtr` / `IsPlausibleMonoVtableCandidate` (`B-H1`), used by `MonoAddressFinder.cpp`.
  - **Covered (`329` checks on x64, `332` checks on x86):**
    - `C-H1`: normal offsets, x64/x86 unsigned `SIZE_MAX` (`0xFFFFFFFFFFFFFFFF` / `0xFFFFFFFF`), `> INT32_MAX`, and negative signed offsets → `-1`.
    - `C-H2`: resolved reference types ignoring `class_value_size` and caller hints (returning pointer width 8 on x64 / 4 on x86), inline value structs (`Vector3`), enums, primitives, and unresolved fallbacks.
    - `C-H3`: v24/v27 raw little-endian (`01 00 00 00` → `1`, `FF FF FF 7F` → `Int32.MaxValue`, signed `-1` vs unsigned max, truncated blobs), v29/v31/v39 `0xFE`/`0xFF` sentinels + 1-/2-byte compressed signed ints, `I8`, `R4` `+Infinity`, and `ClrTypeNameToIl2CppTag`.
    - `A-H2`: compile-time `static_assert(!noexcept(...))` on `ScanForValue` and `ScanForString`; `RunWorkerPool` normal parallel run, mid-pool `std::system_error` spawn failure with join + inline drain, first-thread `std::bad_alloc` fallback, worker-body `std::bad_alloc` containment; and live `ScanForValue` / `ScanForString` over a `VirtualAlloc` RW page.
    - `B-H1`: `0 mod 8` and `19 * sizeof(void*)` IMT-prefixed vtable pointers (`4 mod 8` accepted on x86, rejected on x64), unaligned/null/`< 0x10000` rejection.
  - Wired `-DBUILD_TESTS=ON` in `CMakeLists.txt` and `Run-CppTests` (`-Target CppTest` + integrated into `-Target Test` / `All`) in `build.ps1`.
- **Tests & build:** `.\build.cmd publish` (build #1536) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686); C++ `discrete_dll_test` 329 passed (x64) / 332 passed (x86).

-----

## 2026-11-26 (build 1534) — Fix B-H1 (x86 Mono vtable `& 7` filter dropping interface-implementing classes)

- **Bug fix (`B-H1`):** In `dll/src/mono/MonoAddressFinder.cpp`, both `WhatIsAt` (`:706`) and `SehSweepRegionLoad` (`:839`, shared by `FindAllInstances` and `ReverseLookupIndex::Build`) filtered candidate `MonoVTable*` pointers with `& 7u`. On 32-bit Mono, classes implementing any interface prepend a 19-slot IMT (`19 * 4 = 76` bytes, `4 mod 8`) ahead of the `MonoVTable` in an 8-byte-aligned mempool, so every array (`T[]`), `List<T>`, `Dictionary<K,V>`, and interface-implementing game class had a `4 mod 8` vtable address and was silently dropped.
  - Changed both vtable alignment checks to `alignof(void*) - 1` (`& 3u` on x86, `& 7u` on x64), while preserving the 8-byte alignment checks for `instAddr` (BDWGC 8-byte granule) and `MonoClass*` (mempool-allocated).
  - Marked `B-H1` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1534) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686).

-----

## 2026-11-26 (build 1533) — Fix A-H2 (`ScanForValue` / `ScanForString` `noexcept` & `RunParallelScan` thread-pool `std::terminate`)

- **Bug fix (`A-H2`):** `Memory::ScanForValue` and `Memory::ScanForString` were declared `noexcept` despite allocating region/chunk/result vectors and spawning worker threads via `RunParallelScan`; on 32-bit hosts under address-space pressure, any `std::bad_alloc` or `std::system_error` invoked `std::terminate()` and killed the host game. Additionally, if `pool.emplace_back(worker)` threw after earlier threads had started, `pool`'s destructor destroyed joinable `std::thread` objects (triggering `std::terminate` even for non-`noexcept` `ScanForPointer`), and uncaught exceptions inside a worker thread would also terminate the host.
  - Removed `noexcept` from `ScanForValue` and `ScanForString` in `dll/src/shared/Memory.h` and `dll/src/shared/Memory.cpp` (so allocations failing under memory pressure propagate cleanly to `PipeServer::Dispatch` as JSON-RPC errors), and updated the stale single-threaded doc comment in `Memory.h`.
  - Wrapped `RunParallelScan`'s worker lambda in `try / catch (...)` (`noexcept`) and guarded `pool.emplace_back(worker)` so thread-creation failure joins any already-started workers before finishing remaining chunks inline on the calling thread.
  - Marked `A-H2` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1533) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe` (including `r2-dump` / `xref-dump` harnesses); identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686).

-----

## 2026-11-26 (build 1532) — Fix C-H3 (IL2CPP `getEnumValues` FieldDefault decoder unified with `il2cppGetMetadataTypeFields`)

- **Bug fix (`C-H3`):** In `dll/src/shared/ExportAPI.cpp`, IL2CPP `getEnumValues` previously carried its own older `decodeCompressedSigned` / `decodeTyped` lambdas that decoded every `<=32-bit` enum FieldDefault as an ECMA-335 compressed signed integer without checking metadata version or v29+ `0xFE`/`0xFF` min/max sentinels. On v24/v27 (Unity 2018–2020) games where FieldDefaults are raw little-endian, every `<=32-bit` enum value decoded wrong (e.g. `01 00 00 00` → `-64`), and on v29+ games `int.MaxValue`/`int.MinValue` members failed with `decode_error`.
  - Extracted `DecodeCompressedSignedInt`, `ClrTypeNameToIl2CppTag`, and `DecodeIl2CppFieldDefaultByTag` into file-scope `#if defined(DISCRETE_BACKEND_IL2CPP)` helpers and routed both `il2cppGetMetadataTypeFields` and `getEnumValues` through them (net -62 lines in `ExportAPI.cpp`).
  - Marked `C-H3` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1532) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686).

-----

## 2026-11-26 (build 1531) — Fix C-H2 (`walkArray` reference-type element stride overwritten by `class_value_size`)

- **Bug fix (`C-H2`):** In `dll/src/shared/ExportAPI.cpp` (`CmdWalkArray`), when the caller passed an element class (`elementAssembly` / `elementNamespace` / `elementName`), both the IL2CPP and Mono branches unconditionally called `class_value_size(k)` and overwrote `elemSize`. For reference-type elements (e.g. `UserClass[]`), `class_value_size` returns `instance_size - sizeof(ObjectHeader)` rather than pointer size, causing every array element after `[0]` to step by the wrong stride and breaking both Inspector array expansion and live CE XML export.
  - Guarded `class_value_size` behind `elemIsValueType` (using `SehValueSize`) in both IL2CPP and Mono paths, and explicitly set `elemSize = ManagedLayout::kPointerSize` whenever `elemClassResolved && isPointer` (`!elemIsValueType`).
  - Marked `C-H2` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1531) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686).

-----

## 2026-11-26 (build 1530) — Fix C-H1 (`[ThreadStatic]` `SIZE_MAX` field offsets breaking JSON deserialization)

- **Bug fix (`C-H1`):** IL2CPP encodes `[ThreadStatic]` fields with `THREAD_STATIC_FIELD_OFFSET = (size_t)-1` (`18446744073709551615` on x64, `4294967295` on x86). Emitting raw unsigned offsets broke `System.Text.Json` deserialization into `int` / `int?` / `long?` DTO fields on `getClassInfo`, `dumpClassInfo`, `searchSymbols`, `whatIsAt`, `getStaticFieldData`, and `findFieldReferences`, aborting browsing/batch exports for any class or assembly with `[ThreadStatic]` fields (e.g. `System.Threading.Thread`, `UniTask`, `Newtonsoft`).
  - **DLL (`dll/src/shared/ExportAPI.cpp`):** Added `NormalizeFieldOffset<T>` (mapping negative values and unsigned values `> INT32_MAX` to `-1`) and applied it across all Mono and IL2CPP field-offset JSON serialization sites (`getClassInfo`, `dumpClassInfo`, `searchSymbols`, `findFieldReferences`, `getFieldInfo`, `getStaticFieldData`, `whatIsAt`).
  - **UI (`ui/UnityDumpUI.Core/Services/`):** Added AOT-safe [FieldOffsetJsonConverter.cs](../ui/UnityDumpUI.Core/Services/FieldOffsetJsonConverter.cs) (`FieldOffsetJsonConverter`, `NullableFieldOffsetJsonConverter`, `NullableLongFieldOffsetJsonConverter`) on `FieldEntry.Offset`, `WalkedField.Offset`, `GetStaticFieldDataResult.Offset`, `WhatIsAtField.Offset`, `SearchSymbolMatch.Offset`, and `FieldReferenceFieldInfo.Offset` in `ProtocolModels.cs` as defense in depth against older DLLs or corrupt offsets.
  - **PipeClient (`ui/UnityDumpUI.Core/Services/PipeClient.cs`):** Wrapped `JsonSerializer.Deserialize` `JsonException` into `PipeProtocolException` so callers receive a consistent protocol exception.
  - **Tests:** Added 5 regression tests in `ui/UnityDumpUI.Tests/FieldOffsetProtocolTests.cs` covering x64/x86 unsigned `SIZE_MAX` sentinels across all affected DTOs and malformed-JSON `PipeProtocolException` wrapping.
  - **Docs:** Marked `C-H1` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1530) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686).

-----

## 2026-11-26 (build 1528) — Fix E-H1 (x86 `List<T>._items` pointer read in live CE XML export), add `GEMINI.md` & badge, rename audit docs

- **Bug fix (`E-H1`):** `CeXmlExporter.Live.cs` (`EmitCollectionLiveAsync`) previously read a hardcoded 8 bytes for `List<T>._items` and parsed them with `ParseLeQword` (which required 16 hex chars). On 32-bit Mono targets (`PointerSize == 4`), that swept the adjacent `_size` field at `+0xC` into the upper 32 bits (`_items | (_size << 32)`), causing `WalkArrayAsync` to fail on every non-empty `List<T>`.
  - Changed the `ReadMemoryAsync` size to `ptr` (4 on x86, 8 on x64) and replaced `ParseLeQword` in `CeXmlExporter.cs` with `ParseLePointer(string leHex, int byteCount)`.
  - Added two x86 regression tests in `ui/UnityDumpUI.Tests/CeXmlExporterLiveTests.cs` (`RenderLive_X86_NonEmptyList_ReadsFourByteItemsPointer` and `RenderLive_X86_NonEmptyList_ParsesExactFourByteHexResponse`), and updated `FakeLive.ReadMemoryAsync` to slice returned hex to `size * 2` chars and record `LastReadMemorySize`.
- **Build warning cleanup:** Fixed a pre-existing x86 MSVC `warning C4244` (`uint64_t` → `uintptr_t`) in `dll/src/shared/ExportAPI.cpp:4622` with `static_cast<std::uintptr_t>`.
- **Docs & metadata:**
  - Added [GEMINI.md](../GEMINI.md) with `CLAUDE.md` as its first rule, plus workflow rules (append-only `docs/dev-log.md`, zero-error/zero-warning `.\build.cmd publish`, Python-first tooling with UTF-8 care, `vendor/` via `.\sync_vendor.ps1`, read-only sibling repos).
  - Added the Gemini badge to [README.md](../README.md) and [README.zh-TW.md](../README.zh-TW.md).
  - Renamed `docs/AUDIT_PLAN.md` → [audit-2026-11-23-plan.md](audit-2026-11-23-plan.md) and `docs/BUG_REPORT.md` → [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md), updated internal cross-references, and marked `E-H1` fixed.
  - Created this append-only [dev-log.md](dev-log.md).
- **Tests & build:** `.\build.cmd publish` (build #1528) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1149 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686).
