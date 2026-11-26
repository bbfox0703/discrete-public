# Dev Log

Append-only activity history, newest first. Each entry references a build number
from `build_number.txt` so commits can be cross-referenced. Existing entries are
never modified or rewritten — archive older entries into `docs/archive/` only if
this file grows too large.

**Reading tip:** grep `^## ` for the index, then read from the top (newest-first).

> **Looking for current state or open backlog?** See [plan.md](plan.md) for the
> current phase, open TODO list, and phase ledger.

-----

## 2026-11-26 (build 1545) — Fix E-M1 + E-M6 (exporter cancellation propagation, CTS ownership, and Browser stale `_currentChain` + tests)

- **Bug fixes (`E-M1`, `E-M6`):**
  - **`E-M1` (`CeXmlExporter.cs`, `CeXmlExporter.Live.cs`, `CsxExporter.cs`, `AssemblyBrowserViewModel.cs`, `LiveInspectionViewModel.cs`):**
    - Replaced all 9 remaining bare `catch { ... }` blocks across `CeXmlExporter.cs` and `CeXmlExporter.Live.cs` with `catch (Exception ex) when (ex is not OperationCanceledException)` and added `ct.ThrowIfCancellationRequested()` checks at recursive entry points and element loops in `CeXmlExporter` and `CsxExporter`, so cancelling or superseding an export aborts cleanly instead of returning truncated XML/CSX.
    - Hardened export CTS ownership in `AssemblyBrowserViewModel` (`CopyCeXmlAsync`, `CopyCeFieldAsync`, `ExportAsync`) and `LiveInspectionViewModel` (`CopyCeXmlInstanceAsync`, `CopyCeFieldNodeCoreAsync`): each invocation owns and disposes its own `CancellationTokenSource` in `finally`, and checks `ReferenceEquals(_exportCts, myCts)` before writing to the clipboard/file, updating progress/errors, or clearing `IsExporting` — preventing a superseded export from clobbering the newer export's clipboard or busy indicator.
  - **`E-M6` (`AssemblyBrowserViewModel.cs`):**
    - Cleared `_currentChain` and cancelled `_detailCts` immediately in `OnSelectedClassChanged`, and notified `CanExecuteChanged` on all export commands.
    - Updated `CanExport()` to require `_currentChain.Count > 0` with `_currentChain[^1]` matching `SelectedClass.Name` and `SelectedClass.Namespace`, and guarded `LoadClassDetailAsync` against stale CTS completion so switching classes (or a failed `getClassInfo`) can never export the previously selected class.
  - Added 8 regression tests in `ui/UnityDumpUI.Tests/ExportCancellationAndBrowserChainTests.cs` covering live/template CE XML & CSX cancellation propagation, non-cancellation fault tolerance, and `AssemblyBrowserViewModel` selection/chain synchronization.
  - Marked `E-M1` and `E-M6` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1545) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1175 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 451 checks passed (x64) / 454 checks passed (x86).

-----

## 2026-11-26 (build 1544) — Fix A-M11 + A-M12 (AVX2 string-scan scalar tail alignment & identity-guard regex ReDoS + tests)

- **Bug fixes (`A-M11`, `A-M12`):**
  - **`A-M11` (`dll/src/shared/Memory.{h,cpp}`):** After the 32-byte AVX2 loop in `SehMemcmpProbe` exited, the scalar tail resumed directly at `i` (a multiple of 32). When `stride` did not divide 32 (e.g. `3, 5, 6, 7, 12, 64`), the tail stepped on an off-grid sequence (`32, 32+s, ...`), missing aligned hits and reporting unaligned false positives. Fixed by rounding `i` up to the next multiple of `stride` (with overflow guard) before entering the scalar tail, and exposed `Discrete::Memory::ScanStringSpan` for direct unit testing.
  - **`A-M12` (`tools/check_no_local_paths.py`):** `SESSION_DIR_PATTERN`'s intermediate segment class `[^\s:*?"<>|]{0,120}` did not exclude path separators (`/` and `\`), causing exponential backtracking ($O(2^N)$) on long non-matching paths with many separators. Excluded `/` and `\` (`_NO_SEP`) so every path decomposes uniquely in linear time.
  - Added `Test_AM11_StringScanAvx2TailAlignment` to `dll/tests/dll_core_test.cpp` (targeted stride=6 off-grid decoy test + exhaustive scalar-oracle parity across 13 strides `1..64` and varied tail lengths on both x64 and x86) and `tools/verify/test_check_no_local_paths.py` (positive/negative cases + 50-separator ReDoS timing assertions `< 0.1s`).
  - Marked `A-M11` and `A-M12` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1544) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; `python tools/verify/test_check_no_local_paths.py` passed (4 positive, 3 negative, 3 ReDoS checks); C# xUnit v3 tests 1167 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 451 checks passed (x64) / 454 checks passed (x86).

-----

## 2026-11-26 (build 1542) — Fix A-M1 + A-M2 (+ A-L5, A-L7) (per-client disconnect monitor, pipelined ReadLine buffer, atomic ScanCancel + tests)

- **Bug fixes (`A-M1`, `A-M2`, plus `A-L5` and `A-L7` in `PipeServer` / `ScanCancel`):**
  - **`A-M1` + `A-L7` (`dll/src/shared/ScanCancel.h`, `dll/src/shared/PipeServer.cpp`):**
    - Packed global `activeCount` and `cancelBit` into a single `std::atomic<std::uint64_t>` (`(activeCount << 1) | cancelBit`), eliminating the check-then-store TOCTOU between `RequestCancel()` and `Scope::~Scope()` (`A-L7`).
    - Added per-worker `ClientSlotGuard` binding a stack-local `std::atomic<int> clientScanDepth` in `ServeClient`, incremented/decremented by `ScanCancel::Scope` on that thread. Each client's disconnect monitor now checks ONLY its own `clientScanDepth > 0` instead of the global scan count, so a second client (e.g. a CE Lua bridge burst) disconnecting during a non-scan RPC never probes or cancels another client's active scan (`A-M1`) and never races its own overlapped `ReadFile`.
  - **`A-M2` + `A-L5` (`dll/src/shared/PipeLineBuffer.h`, `dll/src/shared/PipeServer.cpp`):**
    - Added persistent per-connection `Discrete::PipeFraming::LineBuffer` so bytes arriving after `'\n'` in the same `ReadFile()` chunk are preserved for subsequent pipelined requests instead of being silently dropped (`A-M2`).
    - Returned explicit `ReadLineResult::TooLarge` when a request exceeds the 1 MiB line cap and sent a `-32600 "request too large"` JSON-RPC error + log entry before closing (`A-L5`); removed duplicate `#include <thread>`.
  - Added `Test_AM1_PerClientScanCancelAndAtomicState` (including a 20,000-iteration concurrent `Scope` vs `RequestCancel` race test) and `Test_AM2_PipeLineBufferPreservesPipelinedBytes` (+48 checks on both x64 and x86) to `dll/tests/dll_core_test.cpp`.
  - Marked `A-M1`, `A-M2`, `A-L5`, and `A-L7` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1542) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1167 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 445 checks passed (x64) / 448 checks passed (x86).

-----

## 2026-11-26 (build 1541) — Fix B-H2 (object-extent bounds check & pure-memory generic/array klass recognition on Mono + IL2CPP + tests)

- **Bug fix (`B-H2` — final High-severity audit finding, 10/10 High complete):** `BuildClassRangeCache` only enumerated TypeDef rows (`0x02xxxxxx` on Mono, `il2cpp_image_get_class` on IL2CPP), omitting runtime-synthesized generic instantiations (`List<T>`, `Dictionary<K,V>`) and arrays (`T[]`), and neither `WhatIsAt` nor `ReverseLookupIndex::Lookup` verified that `addr - instance < objectSize`. As a result, addresses inside generic instances, arrays, or gaps were falsely attributed to the nearest preceding TypeDef neighbor object with huge `nearestFieldDelta`, and Mono lacked IL2CPP's array fallback parity.
  - Added shared pure header `dll/src/shared/AddressBounds.h`:
    - `ComputeObjectExtent` & `IsOffsetWithinExtent` for regular objects (`class_instance_size` / `object_get_size`, clamped to `>= 2*ptr`), SZARRAYs (`4*ptr + max_length * elemSize`, capped at 64M with overflow protection), and `System.String` (`2*ptr + 4 + (charLen + 1) * 2`).
    - `IntersectCandidateOffsets`, `LooksLikePlausibleClassName` (ASCII + valid UTF-8), and `ValidateDynamicKlassMemory` — pure-memory structural validation of dynamic generic/array klasses using self-calibrated `(image, element_class, name)` offsets with zero runtime API calls on unvalidated memory.
    - `SelectContainingEntry` — bounded `ReverseLookupIndex` interval lookup that rejects out-of-bounds neighbor hits and backtracks up to 16 entries past embedded false-positive header pointers inside larger enclosing objects.
  - Added optional runtime bindings: `mono_class_instance_size` (`MonoResolver.{h,cpp}`) and `il2cpp_class_instance_size` + `il2cpp_object_get_size` (`IL2CPPResolver.{h,cpp}`).
  - Updated `MonoAddressFinder.{h,cpp}` and `IL2CPPAddressFinder.{h,cpp}` symmetrically:
    - Cached sorted loaded `imageSet`, per-class static `extentInfo`, and `dynamicClassSet` + capped negative memo `notDynamicClass`; calibrated `(image, element_class, name)` offsets once across up to 16 TypeDef samples.
    - Enforced `IsOffsetWithinExtent` in `WhatIsAt`, `WhatIsAtAcceptingArrays`, and `ReverseLookupIndex::Lookup` (including `lastExtent_` on the locality fast-path).
    - Accepted validated dynamic generic & array klasses in Mono `WhatIsAt`, `FindAllInstances`, and `ReverseLookupIndex::Build` (gated by `vtable->domain == rootDomain` + pure-memory validation) and in IL2CPP `WhatIsAt` / `WhatIsAtAcceptingArrays` (feeding the shared `dynamicClassSet` merged into `FindAllInstances` / `ReverseLookupIndex::Build`).
  - Documented object-extent bounds and generic/array coverage in [address-finder.md](address-finder.md).
  - Added `Test_BH2_AddressBoundsAndDynamicKlass` (+43 checks on both x64 and x86) to `dll/tests/dll_core_test.cpp`.
  - Marked `B-H2` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1541) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1167 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 397 checks passed (x64) / 400 checks passed (x86).

-----

## 2026-11-26 (build 1539) — Fix A-H1 (+ A-L1, A-L12) (pin backend module, make DllMain detach a no-op, add x64/x86 OS-loader probe tests)

- **Bug fix (`A-H1`, plus `A-L1` and `A-L12` in `main.cpp`):** Previously `DllMain(DLL_PROCESS_DETACH)` with `lpReserved == NULL` (external `FreeLibrary` from CE "unload module", Process Hacker, or a third-party injector) ran `OnDetach() -> Discrete_Shutdown() -> PipeServer::Stop()` + `MainThreadHook::Uninstall()` under the OS loader lock. Because exiting worker/monitor threads block on `LdrShutdownThread` waiting for the same loader lock, the wait inevitably timed out and detached threads returned into unmapped code pages (access violation / game crash).
  - Added shared header `dll/src/shared/ModuleLifecycle.h` with `PinModuleContaining()` (`GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS)`) and `TryClaimProcessMutex()` (`CreateMutexW(..., bInitialOwner = FALSE, ...)`).
  - Updated `dll/src/main.cpp`:
    - `OnAttach` pins the active backend DLL via `PinModuleContaining(&OnAttach)` immediately after claiming the per-PID single-init mutex, so external `FreeLibrary` calls become safe no-ops and cannot unmap code under live pipe workers or MinHook detours.
    - Removed `OnDetach()` and made `DllMain(DLL_PROCESS_DETACH)` an unconditional no-op returning `TRUE` on both `lpReserved != nullptr` and `lpReserved == nullptr`; explicit teardown remains exclusively the off-DllMain `Discrete_Shutdown()` export (`scripts/unity_dump.lua`).
    - Fixed `A-L1` (`bInitialOwner = FALSE`, no cross-thread `ReleaseMutex`) and `A-L12` (`MagicStrings::LogCategoryInit` instead of raw `L"init"`).
  - Updated stale `OnDetach()` comment in `dll/src/shared/PipeServer.cpp` while keeping the bounded `WaitForSingleObject` wait as defense-in-depth.
  - Added test-only `dll/tests/pin_probe_dll.cpp` (built alongside `discrete_dll_test` in `build/cpp-tests-release-{x64,x86}/bin`, never copied to `dist\`) and `Test_AH1_PinnedModuleSurvivesFreeLibrary` in `dll/tests/dll_core_test.cpp`: verifies `TryClaimProcessMutex` duplicate-rejection & cross-thread acquire (no `WAIT_ABANDONED`), then loads `discrete_pin_probe.dll` with a live worker thread, invokes `FreeLibrary` 3×, asserts <1 s elapsed / module stays mapped / `DLL_PROCESS_DETACH` never fires / worker stays alive, and verifies explicit off-DllMain `Probe_Shutdown()` joins cleanly — on both x64 and x86 (WOW64).
  - Marked `A-H1`, `A-L1`, and `A-L12` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1539) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1167 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 354 checks passed (x64) / 357 checks passed (x86) (+25 A-H1 checks on each arch).

-----

## 2026-11-26 (build 1538) — Fix E-H2 (template CE XML & CSX enum/value-struct/collection layout + tests)

- **Bug fix (`E-H2`):** Template-mode CE XML (`CeXmlExporter.cs`, the Assembly Browser default when `CeXmlBaseAddress` is `"INSTANCE_ADDRESS"`) and CE Structure Dissect (`CsxExporter.cs`) previously treated all unknown game types as dereferenced pointers (`Offsets=[0]` / `Vartype="Pointer"`) — causing enum and custom `System.ValueType` struct fields to dereference inline integers/bytes as addresses — and emitted broken collection layouts (missing the SZARRAY header `+0x20`/`+0x10`, omitting `List<T>._items` dereference `+0x10`/`+0x08`, striding reference-type arrays by `ComputeStructSize(child)` without per-slot deref, and expanding `HashSet`/`IList`/`IEnumerable` as flat arrays).
  - Added shared internal `ui/UnityDumpUI.Core/Services/Export/TemplateExportHelpers.cs`: `IsEnumClass`, `IsValueTypeClass`, `ResolveEnumUnderlyingType` (reads `value__` underlying integer type with `System.Int32` fallback), `ComputeValueTypeHeaderAdjust` (strips Mono/IL2CPP boxed `2*ptr` header offsets when structs are emitted inline), `ComputeInlineStructSizeAsync` (natural-aligned inline struct stride), `IsNonArrayCollectionType`, and pointer-size helpers.
  - Updated `CeXmlExporter.cs` template mode: resolves game field types via `IClassInfoLookup` even at depth cap so `System.Enum` emits an inline scalar leaf (using its underlying type) and `System.ValueType` emits an inline group with `offsets: null` (or inline leaf at depth cap); contiguous `T[]` and `List<T>` use `4*ptr` SZARRAY data start, `List._items` (`2*ptr`) deref, pointer-strided reference slots with `Offsets=[0]`, and inline contiguous slots for primitive/enum/value-struct elements; non-array collections (`HashSet`, `IList`, `IEnumerable`, `Dictionary`, …) emit watchable pointer leaves; root template header annotates `(x64 layout)` / `(x86 layout)` and uses `PointerVartype(options)`.
  - Updated `CsxExporter.cs` in sync: enums emit flat scalar `<Element>`s; `System.ValueType` structs flatten inline at `parentOffset + (memberOffset - headerAdjust)` (or `Array of byte` at depth cap, never `Pointer`); `T[]` and `List<T>` emit `List` (`_items` + `_size`) and SZARRAY structures at exact `ptr`-dependent offsets with accurate `AutoCreateStructsize`.
  - Updated `CeXmlExporter.Live.cs` (`EmitMetadataFieldAsync`) to reuse `ResolveEnumUnderlyingType` so non-`Int32` enums in metadata-filled null slots also emit their true width/signedness.
  - Wired `Browser.GamePointerSize = hs.GamePointerSize ?? 0` in `MainWindowViewModel.cs` and passed `PointerSize` through `AssemblyBrowserViewModel.CurrentExportOptions`.
  - Added 13 regression test cases in `ui/UnityDumpUI.Tests/CeXmlAndCsxTemplateExporterTests.cs` covering enums (Int32, Byte, depth 0), custom boxed-offset ValueType structs, x64 + x86 `T[]` & `List<T>` reference arrays, inline struct/enum/primitive arrays, non-array collections, and layout tags across both `CeXmlExporter` and `CsxExporter`.
  - Marked `E-H2` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1538) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1167 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 329 passed (x64) / 332 passed (x86).

-----

## 2026-11-26 (build 1537) — Fix D-H1 (32-bit x86 ISA mode for Rust AOB pattern builder + tests)

- **Bug fix (`D-H1`):** `dll/rust/src/aob.rs` previously decoded every method byte window strictly in x86-64 mode regardless of target architecture. On 32-bit Mono JIT code, `0x40..0x4F` (`INC`/`DEC r32`) were consumed as REX prefixes (desynchronizing all subsequent instructions), `0xA0..0xA3` (`MOV AL/EAX, moffs32`) consumed 8 bytes instead of 4 and left absolute static addresses un-wildcarded, and `mod=00 SIB base=5` absolute jump-table displacements stayed literal.
  - Added 32-bit protected-mode decoding (`build_pattern_for_mode(..., bitness)` and C ABI `dc_aob_build_pattern_mode` in `dll/rust/src/aob.rs` + `dll/rust/include/discrete_core.h`), while keeping `dc_aob_build_pattern` / `build_pattern` 100% byte-identical in 64-bit mode.
  - In 32-bit mode: `0x40..0x4F` decode as 1-byte `INC`/`DEC r32`; `0xA0..0xA3` decode as 4-byte wildcarded `moffs32` (preserving width under `0x66` operand-size prefix); `mod=00 rm=5` and `mod=00 SIB base=5` wildcard 32-bit absolute `disp32`; 32-bit-only opcodes (`PUSHAD`/`POPAD`, `0x82`, segment push/pop, BCD ops) decode cleanly.
  - Updated `AobPattern.h` and `AobPatternRs.cpp` to pass `bitness` (defaulting to `sizeof(void*) * 8` — 32 on x86 Mono DLL, 64 on x64 Mono/IL2CPP DLLs).
  - Added 6 Rust unit / FFI / small-window invariant tests in `dll/rust/src/aob.rs` (running on both x86_64 and i686).
  - Marked `D-H1` as fixed in [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md).
- **Tests & build:** `.\build.cmd publish` (build #1537) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1154 passed (0 failed, 0 skipped); Rust core tests 110 passed (x64) / 111 passed (i686); C++ `discrete_dll_test` 329 passed (x64) / 332 passed (x86).

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
