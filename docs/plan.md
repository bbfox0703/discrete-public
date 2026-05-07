# UnityCEDumper — Development Plan

> 跨 PC 共用的開發進度文件。每完成一個小步驟就把 `[ ]` 改成 `[x]` 並 commit。
> 所有里程碑驗收條件 (Acceptance) 必須通過才能往下一階段。

**Last updated**: 2026-05-07
**Current phase**: Phases **0–15 + 9.5 + 11.5 + 14c + 18 (17a/b/c/d) all closed**. **Phase 18** (address-finder reverse lookup) closed on 2026-05-07 with the `unity_addrfind.lua` CE Lua bridge — `whatIsAt` ships end-to-end across DLL → UI → CE. **Phase 12d** (32-bit Mono) is the only formally-deferred phase. Open feature work: **Phase 16** (docs finalization, parallelizable), **Phase 17** (find method instances, ~one phase). See `STATUS.md` for the detailed hand-off summary; this file is the canonical phase ledger.

## Hand-off notes (historical — for live state see STATUS.md)

- Phase 18 sub-phase 17d green (build #198, 2026-05-07):
  - New [scripts/unity_addrfind.lua](../scripts/unity_addrfind.lua) (13.6 KB) — closes out Phase 18's address-finder track on the CE side. Public API: `whatIsAt(addr, maxBackwardScan?)` (low-level, returns parsed table); `lookupAddress(addr)` (pretty-prints to console, mirrors `Test-WhatIsAt.ps1`'s output format); `getClassRangeStats(rebuild?)` (diagnostic / cache pre-warm); `lookupSelectedAddress()` (memview-or-prompt entry point); `bindAddrLookupHotkey(keys)` (optional global hotkey that fires `lookupSelectedAddress`).
  - Memory-view selection: tries `getMemoryViewForm().HexadecimalView.SelectionStart` first, falls back to `.TopAddress`, then `inputQuery` when neither is available — every accessor is `pcall`-wrapped so older CE builds (and weird forks) degrade gracefully.
  - JSON parser + RPC helper deliberately duplicated from `unity_dissect.lua` (instead of `require`-ing utils) so the file remains drop-in standalone — users who already pasted the dissect / symbols scripts only need to add this one. ~3 s pipe-open retry budget + per-RPC `processMessages()` / `sleep(1)` yield are identical to the sibling scripts.
  - `scripts/README.md` gains a Phase 17d step in the workflow + entry in the install layout block. Existing build script's `*.lua` glob in `Invoke-CompanionCopy` (`build.ps1:412`) means no build-script changes were needed.
  - **Phase 18 closed** — addresses 17a (DLL handler), 17b (field resolution), 17c (UI tab), 17d (CE Lua bridge) all green.
  - **NuGet bumps landed in this session**: Avalonia 12.0.1 → 12.0.2 (UnityDumpUI.csproj inlined the version literal, removing the `$(AvaloniaVersion)` MSBuild property), `Tmds.DBus.Protocol` 0.92.0 → 0.93.0, `Microsoft.NET.Test.Sdk` 18.4.0 → 18.5.1. AOT publish stays clean (0 warnings under all four trim/AOT analyzers); `DiscreteUI.exe` actually shrank from 45.2 MB → 39.0 MB after the bumps, suggesting Avalonia 12.0.2 brought trim improvements.

- Phase 18 sub-phase 17c green (build #197, 2026-05-06):
  - New `AddressLookupViewModel` ([ui/UnityDumpUI/ViewModels/AddressLookupViewModel.cs](../ui/UnityDumpUI/ViewModels/AddressLookupViewModel.cs)) wraps the `whatIsAt` RPC with CommunityToolkit.Mvvm source-gen (no reflection) and exposes presentation-friendly labels (`InstanceLabel`, `ClassLabel`, `FieldOffsetLabel`, `FieldLabel`, `NearestFieldLabel`, `KindLabel`, `ScannedLabel`). Cancellation-safe — supersedes prior in-flight lookups so a fast-typing user doesn't get out-of-order results.
  - New DTOs in `ProtocolModels.cs`: `WhatIsAtResult` / `WhatIsAtField` / `WhatIsAtScanRange` / `ClassRangeStatsResult`. Registered with the source-gen `ProtocolJsonContext` alongside the existing types so AOT publish stays at zero IL2026 / IL3050 warnings.
  - `BackendAdapter` gains `WhatIsAtAsync(address, maxBackwardScan)` and `GetClassRangeStatsAsync(rebuild)`. The `Boolean` JSON serializer is now registered too (used by `getClassRangeStats`'s optional `rebuild` arg).
  - New "Address Lookup" tab on `MainWindow` next to Fields / Inspector / Methods. Address textbox + Lookup / Clear buttons + a Backward-scan NumericUpDown (4096 default, capped at 16384 to match the DLL). Result block shows Instance / Class / Field offset / Field (or NearestField with delta) / Kind / Scanned-range, plus *Inspect this instance* and *Copy instance address* action buttons. The "no result" panel surfaces the raw `reason` from the wire (`no_klass_in_range`, `class_range_cache_unavailable`, `no_il2cpp_domain`, …) so users can tell apart "scan landed in unmanaged memory" from "DLL isn't loaded properly".
  - `MainWindowViewModel` gains `SelectedDetailTabIndex` (TwoWay-bound on the right-pane `TabControl`) plus `TabIndexFields` / `TabIndexInspector` / `TabIndexMethods` / `TabIndexAddressLookup` constants. Two cross-tab paths: (a) Address Lookup → Inspector via `OnInspectInstanceRequested` event handler that calls `Inspector.SetTarget` + `Inspector.FocusOnInstance`, then sets `SelectedDetailTabIndex = TabIndexInspector`; (b) Inspector → Address Lookup via `LookupInspectorAddressAsync(address)` that pre-fills + runs the lookup and flips back. Disconnect tears down the lookup state via `AddressLookup.ClearCommand` so a stale class-pointer block doesn't survive a game relaunch.
  - `LiveInspectionViewModel.FocusOnInstance(address)` is the new public seam used by the cross-tab handoff. Inserts the address as `Instances[0]` if not already present, then sets `SelectedInstance` so the existing `OnSelectedInstanceChanged` cascade fires `walkInstance` — same flow auto-refresh and `FindInstances` use, no duplicate walk path to maintain.
  - `MainWindow` code-behind hook `OnLookupInspectorAddressClicked` reads `Inspector.SelectedNode.EffectiveAddress` (deref pointer for pointer-typed rows; field's storage address otherwise) and hands it to `LookupInspectorAddressAsync`. Sits alongside the existing `Copy CE XML rooted here` / `Copy CE field` context-menu items on the Inspector grid, separated by a `<Separator />` for visual grouping.
  - Strings live in `Resources/Strings/en.axaml` under the new `AddrLookup.*` keys, plus the `Inspector.LookupAddressMenu` entry. Mirrors the existing `Inspector.*` / `Browser.*` namespacing so locale-swappable for free when a translation lands.
  - Native AOT publish clean: `dotnet publish -c Release -r win-x64 -p:PublishAot=true` finishes 0 warnings under all four trim/AOT analyzers; `DiscreteUI.exe` lands in `dist/` at 45.2 MB (vs ~45 MB before, no measurable size impact from the new tab + VM).
- Phase 18 sub-phase 17b green (build #194, 2026-05-06):
  - Field-offset resolution layered on top of 17a's instance + class identification. New `FieldHit` struct in both backends carries `name` / `type` / `offset` / `size` / `kind` / `declaredIn` plus enum / value-type flags; `whatIsAt` populates either `field` (exact `[offset, offset+size)` hit) or `nearestField` (closest declared field below the queried offset, with `nearestFieldDelta`).
  - `EnumerateInstanceFields(klass)` — new internal helper in `IL2CPPAddressFinder.cpp` and `MonoAddressFinder.cpp`. Walks the parent chain (depth cap 16 like `walkInstance`), iterates each class's instance fields directly via the resolver (skipping statics), and computes per-field size via `class_from_*_type` → `class_value_size` for value types, an inline primitive-name table as fallback, 8 bytes for reference types. Mono uses `class_is_enum` + `class_enum_basetype`; IL2CPP uses the parent-class-name `Enum` trick + `value__` field walk (same approach as `ReadClassInfo`).
  - Kind classifier: `leaf` (primitive at exact offset, includes enums), `value-struct` (multi-field value type at exact offset — Vector3, Color, Quaternion, user struct), `pointer` (reference type at exact offset — System.String, T[], class), `interior` (queried offset falls inside the field but not at its boundary — exposes `interiorOffset` for the byte-offset within the field).
  - All ResolveFieldAt SEH-leaf primitives sit alongside the existing 17a probes. Single `ThreadAttachGuard` scope covers both the backward scan AND the field walk so the field-resolution pass doesn't pay an extra attach round-trip. Static fields are filtered at enumeration time so their offsets (which are relative to the static block, not the instance base) can't pollute the offset match.
  - `tools/Test-WhatIsAt.ps1` extended with a follow-up line per probe showing the resolved field — name, type, offset, size, kind, declaredIn.
  - Out of scope for 17b (deferred): pointer-to-class recursion (drill into a pointer field's target instance), `T[]` / `List<T>` array-slot resolution, `System.String` UTF-16 buffer interior detection. The wire format leaves room for these in `field.kind` (could add `array-slot` / `array-element`) without breaking existing callers.
- Phase 18 sub-phase 17a green (build #188, 2026-05-06):
  - New module pair `dll/src/{mono,il2cpp}/{Mono,IL2CPP}AddressFinder.{h,cpp}` implementing the Strategy A backward scan from `address-finder.md`. Each backend exposes `BuildClassRangeCache()` / `InvalidateClassRangeCache()` / `GetClassRangeStats()` / `WhatIsAt(addr, maxBackwardScan)`.
  - Two new pipe commands: `whatIsAt` (the lookup itself) and `getClassRangeStats` (diagnostic / pre-warm). Wired in both backend blocks of `dll/src/shared/ExportAPI.cpp`. The `scan` command was extended to drop the cache so newly-loaded assemblies get picked up by the next lookup.
  - IL2CPP cache is a sorted vector of `Il2CppClass*` (one entry per enumerated class) with min/max bounds for fast range rejection. Mono cache is two parallel sorted vectors — `MonoClass*` and `MonoVTable*` — plus a `vtable → klass` `unordered_map`, since the Mono object header points at a vtable, not the class. `mono_class_vtable` is called for every class during cache build inside one `ThreadAttachGuard` scope (avoids the nested-attach footgun from build #54).
  - Validation pipeline: `(addr & 7) == 0` → range bounds → set membership → SEH-protected `*_class_get_name` probe returns non-empty. Anything that fails any of those steps is skipped silently and the scan walks back another 8 bytes.
  - Build cost on first call is one full `EnumerateAssemblies → EnumerateClasses` pass (~tens of ms even for SaGa F2R's 10k IL2CPP classes / Silksong's many Mono classes); subsequent `whatIsAt` calls are sub-ms because they only do the binary-search membership test + one name-probe per candidate.
  - Smoke test: `tools/Test-WhatIsAt.ps1`. Pulls a live instance address via `findInstances`, then probes `whatIsAt` at base + a few offsets to verify round-trip; also probes `0xDEADBEEF` to confirm the negative path returns `found:false` cleanly.
  - Field-offset resolution lands in 17b — for now `whatIsAt` reports the raw `fieldOffset` (instance base → queried address) without resolving it to a field name.
- Phase 5 segment 2 green (build #25, SaGa F2R):
  - `MetadataLayout.h`: full v29 prefix `HeaderV29` (168 bytes), `TypeDefinition_v29` (88), `FieldDefinition_v29` (12), `FieldDefaultValue_v29` (12)
  - `MetadataParser` exposes `TypeDefinitionCount` / `GetTypeDefinition(idx)` / `FindTypeByName(ns, name)` / `EnumerateTypeFields(td)` / `FindFieldDefault(globalFieldIdx)`
  - `il2cppGetMetadataTypeFields` pipe command: returns every metadata field of a type plus first 16 bytes hex of each FieldDefault blob
  - Verified results on SaGa F2R:
    - `UnityEngine.Object` recovers `objectIsNullMessage`, `cloneDestroyedMessage` as CONST (with raw string blobs) — `kInstanceID_None` is GONE in this build because IL2CPP fully inlines `const int = 0` and removes it from FieldDefinition table too
    - `UnityEngine.Vector3` recovers `kEpsilon` / `kEpsilonNormalSqrt` (float consts, 4 bytes LE direct: `AC C5 27 37` → `~1e-5`)
    - `System.Int32` recovers `MaxValue` / `MinValue` (int consts use IL2CPP compressed encoding, NOT direct 4-byte LE — typed decode needed for these)
  - Encoding observations:
    - `float` const → 4 raw bytes LE
    - `string` const → `[ECMA-335 compressed *signed* int length][utf8 content]`. The *signed* part is critical: low bit of unsigned-decoded value is the sign flag, so actual length = `unsigned >> 1`. Got bitten by this assuming unsigned in build #27 — walker over-read into the next field's prefix and rejected on a stray `0x80` byte (false "bad lead byte")
    - `int32` const → IL2CPP-specific compact encoding (build #28 hex showed leading `0xFE` for `Int32.MaxValue`). Still needs runtime types[] table for proper typed decode (segment 2d)
- Phase 5 segment 2c green (build #31): `TryDecodeStringDefault` correctly handles signed-compressed length, sanitizes invalid UTF-8 to U+FFFD instead of rejecting / throwing. PipeServer wraps `response.dump()` with `error_handler_t::replace` + try/catch so handler-side exceptions can never tear down the host process. Verified `UnityEngine.Object.objectIsNullMessage` → `"The Object you want to instantiate is null."` (43 chars, matches Unity source); `cloneDestroyedMessage` → `"Instantiate failed because the clone was destroyed during creation. ..."` (137 chars).
- Phase 5 segment 1 green (build #23, SaGa F2R / Unity 2021.3.39f1 / metadata v29):
  - `MetadataParser` reads the entire `global-metadata.dat` into memory (capped at 256 MB), validates magic `0xFAB11BAF`, caches the prefix offsets (sanity → version → stringLiteral{Offset,Size} → stringLiteralData{Offset,Size} → string{Offset,Size})
  - `il2cppGetMetadataStrings` pipe command: `{ "limit": N, "offset": M }` (limit 0 = all). Returns `{ "version", "count", "offset", "returned", "strings" }`
  - 11,392 string literals enumerated end-to-end on SaGa F2R. First ~500 are mscorlib Unicode category tables (looks like garbage but is valid). Game-readable strings start ~5000 (`Megalith/CombatAreaGrid` shader names), `__content`-style UIToolkit USS classes around 8000
  - Bitness note: `global-metadata.dat` is purely 32-bit-offset blob, layout is bitness-neutral. Pointer-bitness concern surfaces only when cross-linking to runtime `Il2CppClass*` (segment 2)
  - Currently only v29 layout has been exercised. v24 / v27 / v31 still parse the shared prefix and emit a warning so we know we're guessing
  - Pipe handler script: `tools/Test-MetadataStrings.ps1`
- Phase 4 green (build #19, target: SaGa Frontier 2 Remastered / Unity 2021.3.39f1 IL2CPP):
  - `IL2CPPResolver` splits exports into required (18) vs optional (9); `resolved=yes` requires only the 18 critical ones — newer Unity drops a handful of nice-to-haves
  - `il2cpp_method_get_pointer` is **not exported** in Unity 2021.3+ → IL2CPP `getClassInfo.methods[].address` and `getMethodInfo.matches[].address` come back as `0x0`. Phase 14 will read it from `MethodInfo` struct offset instead
  - `il2cpp_image_get_name` returns `"Assembly-CSharp.dll"` (with `.dll` suffix); `EnumerateAssemblies` strips it so pipe args stay backend-agnostic
  - 57 assemblies / 2451 classes in `Assembly-CSharp` for SaGa F2R; `UnityEngine.GameObject` parent/methods correct; `UnityEngine.Object.objectIsNullMessage` static field address resolves to a valid string pointer
  - IL2CPP **doesn't** keep `const` literal fields in the runtime field table (Mono does, for reflection) — `kInstanceID_None` style probes will say "field not found" on IL2CPP. Use `static readonly` fields for spot checks
- Same 7 pipe commands (`getAssemblies` … `getStaticFieldData`) work on the IL2CPP variant via the `#elif defined(UNITYCEDUMPER_BACKEND_IL2CPP)` branch in `ExportAPI.cpp`. `monoCompileMethod` is Mono-only by design (IL2CPP is AOT — address belongs in `getClassInfo`).
- **build.ps1 caveat**: `-Backend IL2CPP` outputs to `build/dll-il2cpp-release/bin/`, but `Inject-Dll.ps1` reads from `build/dll-both-release/bin/`. Use `-Backend Both` for any test you plan to inject. Or pass `-DllPath` to the injector explicitly.
- Phase 3 green (build #14, target: BongoCat / Unity Mono 2022.x):
  - `MonoResolver` binds 23/23 exports from `mono-2.0-bdwgc.dll`, all categories logged
  - `MonoThreadAttach` RAII wraps every mono API call from the pipe worker
  - `MonoDomain` enumerates 56 assemblies in BongoCat (`Assembly-CSharp`, `mscorlib`, etc.)
  - `MonoClassWalker` enumerates 448 classes in `Assembly-CSharp`; reads parent / fields (with type+offset+flags) / methods (token+flags)
  - `MonoTypeParser` returns clean type strings via `mono_type_get_name` + `mono_free`
  - **Static fields**: `mono_class_vtable` + `mono_vtable_get_static_field_data` resolves to a real address (verified `UnityEngine.Object.kInstanceID_None` at non-zero process address)
  - **Method JIT**: `mono_compile_method` returns native code pointer (verified `UnityEngine.Object.GetInstanceID`)
  - **Field offset spot-check**: `UnityEngine.Object.m_CachedPtr` at 0x10 (matches MonoObject header on x64); `BongoCat.AdventsDrawer.date` at 0x38 (consistent MonoBehaviour layout)
  - Class name `GetClassInfo` → `ReadClassInfo` to dodge `winuser.h` macro collision (`UNICODE` makes `GetClassInfo` → `GetClassInfoW`)
- New pipe commands wired: `getAssemblies`, `getClasses`, `getClassInfo`, `getFieldInfo`, `getMethodInfo`, `getStaticFieldData`, `monoCompileMethod`. Handshake `assemblies_count` is now live for Mono builds.
- New helper scripts:
  - `tools/Test-MonoWalk.ps1` — exercises segment 2 (getClasses / getClassInfo / getFieldInfo / getMethodInfo)
  - `tools/Test-MonoOps.ps1` — exercises segment 3 (getStaticFieldData / monoCompileMethod)
- CMake split: Mono-only sources live in `dll/src/mono/` and only compile into `UnityDumper-Mono.dll`. IL2CPP DLL stays minimal until Phase 4.
- Watch out: SEH `__try`/`__except` cannot share a frame with destructors — every Mono runtime call goes through a leaf `Seh*` helper (`#pragma warning(disable: 4509)`)
- Watch out: Bitdefender flags injected DLLs as virus → add `D:\Github\discrete\build\` and `%LOCALAPPDATA%\UnityCEDumper\` to AV exclusions on each dev PC
- Watch out: linker LNK1104 means a previous host still has the DLL mapped — close it before rebuilding
- Watch out: PowerShell `try {} catch {}` is a statement, not an expression — cannot inline inside `[pscustomobject]@{}`
- Lessons learned (still applicable):
  - VS DevShell loading must use `Import-Module Microsoft.VisualStudio.DevShell.dll` + `Enter-VsDevShell` cmdlet (NOT `Launch-VsDevShell.ps1` — recurses on VS 18)
  - `Avalonia.ReactiveUI` has no v12 release; use `CommunityToolkit.Mvvm` 8.x for AOT-friendly MVVM
  - `Tmds.DBus.Protocol` 0.9x triggers NU1903; pin to `0.92.0` in every csproj
  - `BuiltInComInteropSupport=false` is required for Avalonia 12 + Native AOT

---

## Phase Overview

| # | Phase | Goal | Status |
|---|-------|------|--------|
| 0 | Bootstrap | Repo skeleton, build script, logging | ✅ |
| 1 | Shared C++ Foundation | Memory / Logger / ModuleHelper / Pipe server | ✅ |
| 2 | Backend Detection + Handshake | Auto-detect Mono/IL2CPP, return engine info | ✅ |
| 3 | Mono Backend | Resolver → Domain walk → Class walk → Field/Method | ✅ |
| 4 | IL2CPP Backend (API) | Resolver → Domain walk → Class walk via exports | ✅ |
| 5 | IL2CPP Backend (Metadata) | global-metadata.dat parser (v24/27/29/31) | ✅ v24/v27/v29/v31 dispatch live (Phase 14c); IL2CPPMetadataRegistration AOB cross-link still optional ("2d follow-up", non-blocking) |
| 6 | Pipe Protocol Core Commands | getAssemblies / getClasses / getClassInfo / etc. | ✅ |
| 7 | UI Skeleton (Avalonia + AOT) | Single-instance app, pipe client, handshake | ✅ |
| 8 | UI Panels — Tree & Browse | AssemblyTree, ClassBrowser, FieldStruct, MethodList | ✅ |
| 9 | Live Inspection | InstanceFinder, LiveWalker, StaticField, read/write memory | ✅ |
| 9.5 | In-app picker + DLL injector | Replaces `Test-Handshake.ps1` for launch-and-attach | ✅ |
| 10 | Export Formats | CE XML, CSX (Structure Dissect), SDK header | ✅ UI-side; DLL-side `FieldExporter` + `exportCe*` pipe commands deliberately skipped (UI does it locally) |
| 11 | CE Lua Bridge | unity_dump.lua, unity_dissect.lua, unity_symbols.lua | ✅ Both bridge scripts (dissect + symbols) verified live; deploy tooling shipped |
| 11.5 | Inspector inline drilldown | Hierarchical pointer / array / value-struct expansion | ✅ |
| 12 | Proxy DLLs | version.dll / winmm.dll / dxgi.dll variants + bulk deploy panel | ✅ x64 (12a-12c). 12d (32-bit Mono) deferred — see below |
| 12d | 32-bit Mono support | x86 build of backend + proxy DLLs, cross-bitness inject, x86/x64 selection in installer | ⏳ Deferred — engineering only, no external blockers |
| 13 | Offline Metadata CLI | Standalone tool to parse global-metadata.dat | ✅ Ships as `dist/metadata-cli.exe` |
| 14 | Hardening | Error paths, AOT trim warnings, log rotation, edge cases | ✅ All sub-items closed (14a-14f) |
| 14c | IL2CPP metadata version dispatch | v24 / v27 / v29 / v31 TypeDefinition layouts | ✅ Cross-checked vs upstream Unity (`docs/il2cpp-metadata-crosscheck.md`) |
| 15 | Test Matrix | Mono / IL2CPP across multiple Unity versions | ✅ Verified across 6+ games covering v24/v27/v29/v31 + Mono bdwgc Unity 6 |
| 16 | Docs Finalization | dll-spec / pipe-protocol / ui-spec / internals docs | 🟡 Architecture / pipe-protocol / STATUS / address-finder / il2cpp-metadata-crosscheck exist; remaining items still TBD |
| 17 | Find Method Instances | New wire command `findMethodInstances(asm, ns, class, method, [signature])` + UI tab | ⬜ Not started |
| 18 | Address Finder (reverse lookup) | New wire command `whatIsAt(address)` returning instance + class + field; inverse of `findInstances` | ✅ All four sub-phases shipped (17a DLL handler / 17b field resolution / 17c UI Address Lookup tab / 17d CE Lua bridge). Spec in [address-finder.md](address-finder.md). |

---

## Phase 0 — Bootstrap

**Goal**: Empty-but-buildable skeleton everyone can clone and `build.ps1` cleanly.

- [x] `CMakeLists.txt` root + per-target (DLL Mono / DLL IL2CPP / ProxyDLL)
- [x] `build.ps1` with `-Target {DLL|UI|ProxyDLL|Test|MetadataCli|All}`, `-Backend {Mono|IL2CPP|Both}`, `-Mode {Debug|Release|Publish}`, `-Clean`
- [x] `build.cmd` thin wrapper
- [x] `build_number.txt` auto-increment hook (in `build.ps1`)
- [x] Directory layout per Architecture §Directory Structure (empty `.keep` placeholders OK)
- [x] `.gitignore`, `.gitmodules` (vendor submodules: nlohmann/json — submodule retired in favour of `sync_vendor.ps1` cloning into `dll/vendor/nlohmann/`, see Phase 16 follow-ups)
- [x] `dll/src/main.cpp` — minimal `DllMain` that opens log file
- [x] `ui/UnityDumpUI/` — Avalonia 12 template, AOT publish profile, `Mutex` single-instance
- [x] `ui/UnityDumpUI.Tests/` — xUnit v3 project with smoke tests
- [x] `scripts/unity_dump.lua` — stub with version banner
- [x] **First-build verification**: run `build.ps1 -Target All -Mode Release` on dev PC
- [x] Vendor `nlohmann/json` (now via `sync_vendor.ps1` → `dll/vendor/nlohmann/`; the original git-submodule registration was removed when the script-based approach landed)
- [x] Inject built DLL into `notepad.exe` and confirm log file appears

**Acceptance**:
- `build.ps1 -Target All -Mode Release` produces `UnityDumper-Mono.dll`, `UnityDumper-IL2CPP.dll`, `UnityDumpUI.exe`, `version.dll`
- DLL injected into a notepad.exe writes "init" log to `%LOCALAPPDATA%\UnityCEDumper\Logs\<pid>\init\`
- `dotnet publish -r win-x64 -c Release` of UI produces single-file AOT exe with **zero trim/AOT warnings**

---

## Phase 1 — Shared C++ Foundation

**Goal**: Reusable modules used by both Mono and IL2CPP backends.

- [x] `shared/Logger.{h,cpp}` — 5 categories (init/scan/metadata/pipe/walk), per-PID subfolder, 4-file rotation, 8MB cap
- [x] `shared/Memory.{h,cpp}` — `AOBScan(pattern, mask)`, `SafeRead<T>(addr)` with SEH, `SafeWrite`, `IsReadable`
- [x] `shared/ModuleHelper.{h,cpp}` — `GetModuleBase(name)`, `ResolveExport(module, name)`, `EnumerateModules()`
- [x] `PipeServer.{h,cpp}` — async overlapped IO, newline-delimited JSON, per-connection thread, dispatch table
- [x] `ExportAPI.{h,cpp}` — C ABI: `Init()`, `Shutdown()`, `StartPipe()`, `StopPipe()`, `GetBackendType()`
- [x] `dll/vendor/nlohmann/single_include/nlohmann/json.hpp` (synced by `sync_vendor.ps1`)
- [x] Magic-words header `shared/MagicStrings.h` (pipe name, log folder name, all command strings)

**Acceptance**:
- Pipe server accepts handshake `{"cmd":"ping"}` → `{"pong":true}` from a Python smoke test
- Logger files rotate at 8MB; init log captures DLL load time + module list

---

## Phase 2 — Backend Detection + Handshake

- [x] `BackendDetector.{h,cpp}` — module sniff (`mono-2.0-bdwgc.dll` / `mono.dll` / `GameAssembly.dll`)
- [x] Unity version extraction from `globalgamemanagers` or `data.unity3d`
- [x] IL2CPP metadata version read (first 8 bytes of `global-metadata.dat`)
- [x] `handshake` command returns spec-shaped JSON (engine/backend/unity_version/metadata_version/game_module/assemblies_count)

**Acceptance**:
- Test against ≥1 Mono game and ≥1 IL2CPP game; both return correct `backend` and `unity_version`

---

## Phase 3 — Mono Backend

- [x] `mono/MonoResolver.{h,cpp}` — resolve every export in Architecture §Mono Entry Point Resolution table; lazy bind, log missing
- [x] `mono/MonoDomain.{h,cpp}` — `mono_get_root_domain` → `mono_assembly_foreach` → image list with name/path
- [x] `mono_thread_attach` wrapper (RAII guard for any Mono call from injected thread)
- [x] `mono/MonoClassWalker.{h,cpp}` — iterate types via `MONO_TABLE_TYPEDEF`, expose Field / Method iterators
- [x] `mono/MonoTypeParser.{h,cpp}` — `MonoType*` → readable string (`int`, `System.Single`, `MyNs.Foo[]`, `List<T>` etc.)
- [x] Inheritance chain walk (`mono_class_get_parent`)
- [x] Static field address: `mono_class_vtable` + `mono_vtable_get_static_field_data`
- [x] `mono_compile_method` → native address (with try/except)

**Acceptance**:
- Dump `Assembly-CSharp` of a known Mono game (e.g., a 2018 Unity build) — produce JSON list with assembly count, total class count, and sampled field offsets matching dnSpy / Cheat Engine manual probes

---

## Phase 4 — IL2CPP Backend (API path)

- [x] `il2cpp/IL2CPPResolver.{h,cpp}` — resolve every export in Architecture §IL2CPP table from `GameAssembly.dll`
- [x] `il2cpp/IL2CPPDomain.{h,cpp}` — `il2cpp_domain_get` → assemblies → images
- [x] `il2cpp/IL2CPPClassWalker.{h,cpp}` — `il2cpp_image_get_class_count` + `il2cpp_image_get_class(idx)`; field / method iterators
- [x] `il2cpp/IL2CPPTypeParser.{h,cpp}` — `Il2CppType*` → string, handle generic instantiations
- [x] Static field block via `il2cpp_class_get_static_field_data`
- [x] Method native address (AOT pointer in `Il2CppMethodInfo`) — `ReadMethodPointer` falls back to reading `*(void**)methodInfo` (the `methodPointer` field at offset 0) when `il2cpp_method_get_pointer` is missing, which it is on Unity 2021.3+. Wired into both `getClassInfo.methods[].address` and `getMethodInfo.matches[].address`.
- [x] Inheritance via `il2cpp_class_get_parent`

**Acceptance**:
- Dump a known IL2CPP game; verify a Vector3 field offset against in-memory inspection

---

## Phase 5 — IL2CPP Backend (Metadata Deep Parse)

- [x] `il2cpp/MetadataParser.{h,cpp}` — locate `global-metadata.dat`, validate magic `0xFAB11BAF`, version dispatch
- [x] Version-specific header structs — v24 / v27 / v29 / v31 all dispatched (Phase 14c). Cross-checked against upstream Unity source in `vendor/libil2cpp/`; full audit in [docs/il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md)
- [x] String heap reader (`nameIndex` → UTF-8 string)
- [partial] Tables: TypeDefinitions ✅, FieldDefinitions ✅, **StringLiterals ✅**, FieldDefaults ✅; MethodDefinitions deferred (no current consumer)
- [ ] Cross-link metadata index ↔ runtime `Il2CppClass*` via `Il2CppMetadataRegistration` AOB (deferred — see "2d follow-up", non-blocking)
- [x] Enum default values (segment 2d v1: type-aware decode using runtime `value__` underlying type), **string literal extraction ✅**
- [x] Detection of stripped / obfuscated metadata (segment 3): `getMetadataStats` pipe command. Counts single-char / two-char user-type names (excluding `<...>` compiler-generated) and emits a verdict — `clean` / `partial_strip` / `heavy_strip`. IL2CPP path reads from `global-metadata.dat` directly; Mono path walks runtime classes.

**2d follow-up** (not yet started): full `Il2CppMetadataRegistration` discovery
in `GameAssembly.dll` so non-enum const literals (e.g. `const int kFoo = 42`)
in `il2cppGetMetadataTypeFields` can also be type-aware-decoded. Likely
approach: AOB scan the `.data` section for the registration struct shape
(positive `typesCount`, all sub-pointers fall inside the module range). The
runtime types[] table dereference lets `FieldDefault.typeIndex` map to a
primitive type tag without going through `value__`.

**Acceptance**:
- Same game as Phase 4 — extra info (string literals, enum names) appears in `getClassInfo` result

---

## Phase 6 — Pipe Protocol Core Commands

Implement every command in Architecture §Core Commands. One sub-task per command:

- [x] `handshake` (verified final shape; carries engine / backend / unity_version / metadata_version / build / protocol)
- [x] `scan` — re-resolves exports, refreshes domain pointer (re-runs `BackendDetector` + resolver init)
- [x] `getAssemblies`
- [x] `getClasses` (namespace + name; nested-type names surface as `Outer/Inner` so `FindClass` round-trips)
- [x] `getClassInfo` (parent, fields, methods)
- [x] `getFieldInfo`
- [x] `getMethodInfo`
- [x] `getStaticFieldData`
- [x] `findInstances` (heap walk: aligned-qword scan of every committed RW page for the class header pointer; capped + plausibility-filtered)
- [x] `readMemory` / `writeMemory`
- [x] `registerSymbol` (in-DLL named-pointer map; survives until DLL unload)
- [partial] `exportCeXml` / `exportCsx` / `exportSdkHeader` — **deliberately not implemented as DLL pipe commands**. The UI layer (`ui/UnityDumpUI.Core/Services/Export/`) emits all three formats locally from `getClassInfo` data, which is good enough for both the standalone UI and the CE-side `unity_dissect.lua` flow. DLL-side `FieldExporter` would only be needed if a third client (CLI / proxy panel / external tool) wanted the same outputs without re-implementing them.
- [x] `getEnumValues` (IL2CPP: full ECMA-335 §II.23.2 decode; Mono: type-aware width via `value__` underlying type)
- [x] `resolvePointer` (CE-style chain: every offset except last dereferenced; soft mid-chain failure carries `step`/`address`/`trace`)
- [x] `walkInstance` (parent-chain walk depth-capped at 16; per-field decoder shared with `walkArray`)
- [x] Mono-only: `monoCompileMethod`, `monoGetVTable`
- [x] IL2CPP-only: `il2cppGetMetadataStrings`, `il2cppGetMetadataTypeFields` (genericInstances supplanted by metadata-table walks)
- [x] Error envelope (`{"id":N,"error":{"code":..,"message":..}}`) — dispatcher errors codes documented in `pipe-protocol.md`; handler-level soft errors land inside `result.error` per command convention
- [x] **Bonus** (Phase 10 follow-up): `walkArray` — read managed `T[]` header + per-element decode, with optional element-class metadata via `class_value_size` / `class_is_valuetype`

**Acceptance**: ✅
- `pipe-protocol.md` documents every command with request/response examples
- PowerShell smoke tests under `tools/Test-*.ps1` exercise the major paths (handshake, walk, find, memory, metadata)
- `BackendAdapter` typed C# wrappers consumed by the UI cover every command above

---

## Phase 7 — UI Skeleton (Avalonia + AOT)

- [x] `App.axaml` / `MainWindow.axaml` — base layout (left: assemblies tree placeholder, center: handshake detail, right: inspector placeholder); 3-pane DockPanel + connection bar
- [x] `Mutex` single-instance guard on app start (already in place from Phase 0)
- [x] `Resources/Strings/en.axaml` with `StaticResource` bindings (no hardcoded UI strings)
- [x] `Services/PipeClient.cs` — async `NamedPipeClientStream`, JSON line framing, request/response correlation by `id`, single-flight via `SemaphoreSlim`
- [x] `Services/ProtocolModels.cs` + `Services/ProtocolJson.cs` — typed DTOs with `[JsonSerializable]` source-gen context (handshake, ping wired; more added per command in later phases)
- [x] `Services/BackendAdapter.cs` — typed façade (`PingAsync`, `HandshakeAsync`); ViewModels never see raw JSON
- [x] `Services/FileLogger.cs` — `%LOCALAPPDATA%\UnityCEDumper\Logs\ui\<category>` (init / pipe / view), 4-file rotate, 8MB cap
- [x] Platform interface in `Core` project (`IProcessEnumerator`); `WindowsProcessEnumerator` concrete impl in UnityDumpUI
- [x] `MainWindowViewModel` (`CommunityToolkit.Mvvm` source-gen, no reflection) drives Connect / Disconnect commands and surfaces handshake fields + error banner
- [x] AOT publish profile produces single trimmed `UnityDumpUI.exe` — `dotnet publish -r win-x64 -p:PublishAot=true` completes with **0 warnings** under `TreatWarningsAsErrors=true` and the trim/AOT/single-file analyzers all enabled

**Acceptance**: ✅
- UI starts (Avalonia + AOT publish succeeded), Connect attempts pipe handshake and renders all 9 fields plus error banner on failure
- Single-instance Mutex (`Global\UnityCEDumper.UI.SingleInstance`) refuses second launch
- `dotnet publish -c Release -r win-x64 -p:PublishAot=true` finishes with zero trim/AOT warnings

---

## Phase 8 — UI Panels: Tree & Browse

- [x] `AssemblyTreePanel` — virtualized list of assemblies (left pane, ListBox with built-in virtualization handles 1000+ entries)
- [x] `ClassBrowserPanel` — class list under the selected assembly (middle pane). Namespace shown as a sub-line on each entry; same virtualized ListBox handles 5000+ classes.
- [x] `FieldStructPanel` — DataGrid of fields (Name / Offset / Type / Flags / static / Declared in)
- [x] `MethodListPanel` — methods (Name / Address / Token / Flags / static / Declared in) — Address populated for IL2CPP via the methodPointer struct read added in the Phase 7 backend follow-up
- [x] Search/filter box (substring + case-insensitive) on the assembly list and the class list
- [x] Inheritance toggle: "show inherited members" — walks `parent` chain via repeated `getClassInfo` calls (bounded at depth 32) and merges ancestor fields/methods oldest-first; each row carries the `Declared in` column so the origin is clear
- [x] Protocol DTOs added: `GetAssembliesResult`, `GetClassesResult`, `GetClassInfoResult` (+ `AssemblyEntry`, `ClassEntry`, `FieldEntry`, `MethodEntry`, `ClassRef`) wired through `ProtocolJsonContext` for source-gen serialization
- [x] `BackendAdapter.GetAssembliesAsync` / `GetClassesAsync` / `GetClassInfoAsync` typed façade
- [x] `AssemblyBrowserViewModel` (`CommunityToolkit.Mvvm` source-gen, no reflection) coordinates the selection cascade + filtering + inheritance walk; cancellation tokens drop superseded fetches
- [x] Auto-loads after handshake (Connect kicks off `RefreshAssembliesCommand`); Disconnect wipes the browser to avoid stale data
- [x] AOT publish: `dotnet publish -c Release -r win-x64 -p:PublishAot=true` finishes with 0 warnings / 0 errors

**Acceptance**: ✅
- Drill from assembly → class → field/method works without UI freeze (ListBox virtualization holds, async backend fetch keeps the UI thread free)
- AOT publish clean under all four trim/AOT analyzers

---

## Phase 9 — Live Inspection

- [x] `InstanceFinderPanel` — heap-scans every committed RW page for the class header pointer (`Il2CppClass*` for IL2CPP, `MonoVTable*` for Mono); 8-byte aligned; capped at 4096 matches; surfaces in the Inspector tab as a virtualized list of instance addresses.
- [x] `LiveWalkerPanel` — `walkInstance` reads the instance + parent chain (depth cap 16), per-field decoder produces typed values (bool/int/float/string with UTF-16 → UTF-8 conversion / pointer); rendered in a DataGrid with click-to-walk pointer support
- [x] `StaticFieldPanel` — exposed via `LiveInspectionViewModel.ReadStaticFieldAsync(field, size)` which chains `getStaticFieldData` → `readMemory`. Programmatic API in place; full panel UX deferred (current Inspector tab focuses on instance walks).
- [x] Refresh / auto-refresh interval — `PeriodicTimer` driven; off / 250 ms / 1 s buttons in the toolbar
- [x] **Bonus**: `walkArray` for live array element walk (right-click a `T[]` / `List<T>` field → "Walk array" → per-slot data)
- [x] **Bonus**: nested-type support both backends (classes surface as `Outer/Inner`; `FindClass` walks `class_get_nested_types`)

**Acceptance**: ✅
- `MainGame/PLY_WORK` instance walk on SaGa F2R reproduces a Cheat Engine "Get stat" reference table value-for-value (chrNo=9, hpNow=309, lpNow/lpMax=18, wpNow=56, …)

---

## Phase 10 — Export Formats

**Scope deviation**: implemented in C# under `ui/UnityDumpUI.Core/Services/Export/` instead of the originally-planned DLL-side `shared/FieldExporter.{h,cpp}`. UI consumes `getClassInfo` data directly + recursive `walkArray` calls in live mode. The DLL-side path was skipped because (a) the standalone UI already had the data, (b) the CE-side `unity_dissect.lua` opens the pipe directly and builds CE structures itself, (c) no third client (CLI / proxy panel / external tool) currently needs DLL-side exports. Revisit if Phase 13 (offline CLI) wants to share format generators.

- [ ] ~~`shared/FieldExporter.{h,cpp}`~~ — replaced by C# `UnityTypeMap` + per-format exporters in `ui/UnityDumpUI.Core/Services/Export/`
- [x] CE XML generator (`CeXmlExporter`): hierarchical `<CheatTable><CheatEntries>` with `GroupHeader=1` + `Offsets=[0]` deref + `<Options moHideChildren="1" moDeactivateChildrenAsWell="1"/>` collapse. **Two render modes**: template (relative `+offset` chains, no live calls) and live-data (absolute addresses everywhere, recursive `readMemory` + `walkArray` per node).
- [x] CSX generator (`CsxExporter`): CE-canonical `<Structure>` round-trip shape (`Offset` + `OffsetHex` + `DisplayMethod` all required by CE's loader; `EConvertError` happens otherwise — caught + fixed during initial bring-up). Nested `<Structure>` for pointer drilldown via recursive `getClassInfo`.
- [x] SDK header generator (`SdkHeaderExporter`): `#pragma pack(push,1)` struct + explicit `// +0x...` offset comments; statics skipped; optional inherited-field block.
- [x] UI buttons (Phase 8 detail toolbar): Export `.h` / Export `.csx` / Export `.CT` to file; Copy CE XML to clipboard; Copy CE field via right-click on a Fields row. All five share one `ExportOptions` knob set (depth / arrayLimit / collapse / baseAddress / liveData) plumbed through the `Export options ▾` flyout.
- [x] **Bonus**: `IClassInfoLookup` + `ILiveExportSource` decoupling (BackendAdapter implements both); `Inspector.SelectedInstance.Address` auto-mirrors into `Browser.CeXmlBaseAddress` so the live render roots at the picked instance.

**Acceptance**: ✅
- Generated CE XML imports cleanly into Cheat Engine, resolves real values when CE XML base is set to a live instance address (verified on SaGa F2R `MainGame/PLY_WORK`)
- Generated CSX imports into Structure Dissect, all 49 fields populate at the right offsets with the right types
- Generated `.h` follows the standard layout; not formally compiled under MSVC but the syntax is the same `#pragma pack` + struct shape we test against C++ codegen routinely

**Open follow-up (out-of-scope from the original Phase 10 plan, tracked in `STATUS.md`)**: CSX live mode. CE XML got the live-render treatment; CSX still emits structural placeholders for collection drilldown. CSX's `Element` addressing is relative to slot start (no absolute-address mode), so it's a different design problem. Worth doing once a user asks.

---

## Phase 11 — CE Lua Bridge

- [x] `scripts/utils.lua` — `callDLL(funcName, retType, ...)` wraps `executeCodeEx`; `detectBackend()` mirrors `WindowsProcessEnumerator.DetectBackend`; `dllPath()` resolves `<CE>/discrete/UnityDumper-{Backend}.dll` with a script-dir fallback for portable installs.
- [x] `scripts/unity_dump.lua` — sanity-check process attached → detect backend → `loadLibrary(dllPath)` → idempotent `UnityDumper_StartPipe` → banner with build #. `discoverScriptDir()` uses `debug.getinfo(1,"S").source` so `dofile([[…]])` invocations work (commit `2d0641d`).
- [x] `scripts/unity_dissect.lua` — opens `\\.\pipe\UnityDumpBfx` directly via `io.open(..., "r+b")`, parses `getClassInfo` JSON via a tiny purpose-built parser, builds a CE `Structure Dissect` entry via `createStructure` / `addElement` with managed-type → CE Vartype map matching `CsxExporter`.
- [x] `scripts/unity_symbols.lua` — same pipe + parser pattern; `registerClassSymbols(asm, ns, name)` walks `getClassInfo`, calls `getStaticFieldData` per static field, registers CE symbols under the Architecture.md naming convention. IL2CPP method addresses come straight from `getClassInfo.methods[].address`; Mono needs `monoCompileMethod` first which we intentionally skip (mass JIT would be a footgun).
- [x] Only existing CE Lua APIs used: `getOpenedProcessID`, `getModule`, `loadLibrary`, `getCheatEngineDir`, `executeCodeEx`, `getAddress`, `createStructure` / `addElement`, `registerSymbol`, `sleep`, `pcall`, `print`. No invented calls. `io.open` on a named pipe is plain Lua, not CE-specific.
- [x] Scripts ship in `dist/` alongside the DLLs + `README.md` so users only need that folder, not a checked-out repo (commit `3b287c5`, build.ps1 dist-copy step).

**Acceptance**: ✅
- `unity_dump.lua` injects + brings up pipe end-to-end on both backends (verified SaGa F2R IL2CPP + Hollow Knight Silksong Unity 6 Mono).
- `unity_dissect.lua` → `dissectClass("Assembly-CSharp", "", "MainGame/PLY_WORK")` builds a 56-field `Structure Dissect` entry that opens cleanly in CE's Memory View → Ctrl+D → Structures menu. Surfaced + fixed a silent `createStructure` registration bug (CE 7.5.x doesn't auto-add to global list — script now calls `addToGlobalStructureList()`).
- `unity_symbols.lua` → `registerClassSymbols("Assembly-CSharp", "", "GameManager")` against Silksong: 10 static fields + 323/328 method JITs registered, sub-3-minute walltime, all symbols (`.s_<field>` and `.m_<method>`) resolve via `getAddress`. Surfaced + fixed three latent bugs along the way: (a) pipe-open race on first RPC after DllMain returns (added 15-attempt retry loop); (b) main-thread starvation under hundreds of back-to-back RPCs (added `processMessages()` + `sleep(1)` per RPC); (c) `::` symbol-name collision (renamed convention to `.m_<method>`, mirrors `.s_<field>`).
- Deploy tooling (`tools/Install-CEScripts.ps1` + `build.cmd deploy[-scripts]`) — self-elevating PowerShell installer auto-detects CE install path, copies scripts/DLLs into `<CE>/discrete/`, replaces the manual elevated-copy workflow.

---

## Phase 12 — Proxy DLLs (version / winmm)

- [x] `dll/src/proxy/loader.cpp` — shared loader: pre-load real system DLL, `DisableThreadLibraryCalls`, spawn worker thread that polls for backend modules up to ~5s, detects via `BackendDetector::Detect()`, and `LoadLibraryW`s `UnityDumper-{Backend}.dll` from the same directory
- [x] `dll/src/proxy/version/version.cpp` + hand-written export thunks — forwards every `version.dll` export
- [x] `dll/src/proxy/winmm/winmm.cpp` + hand-written export thunks — forwards every `winmm.dll` export
- [ ] ~~`dll/src/proxy/dxgi/`~~ — deferred; the two shipped variants cover the use cases that have come up. Add when a target requires it.
- [x] `build.ps1 -Target ProxyDLL` builds both variants
- [x] **Proxy auto-load** (12a): both proxies build to ~130 KB each, DllMain pre-loads the real system DLL so first thunk call is cheap, then spawns the backend-loader worker thread.
- [x] **Per-process init mutex** (12b): `Global\UnityCEDumper-Init-PID-{pid}` claimed in `OnAttach`; if `ERROR_ALREADY_EXISTS`, log + skip Init. Covers the "proxy already loaded us, then user clicked Pick & inject" double-load scenario.
- [x] **Bulk deploy panel** (12c): `ProxyDeployWindow` (UE5CEDumper-style multi-select panel). Toolbar: ↻ Rescan / Select all / Unselect all / Invert / "When installing: ◉ version.dll ○ winmm.dll" / "Show 32-bit / unsupported" filter. DataGrid columns: checkbox, Game, Backend, Bits, Proxy, Status, Last operation, Path. Bottom bar: **Install / Uninstall / Update all deployed**. Steam-library scan via `libraryfolders.vdf`; Unity backend + bitness detection via PE `IMAGE_FILE_MACHINE` + module markers.
- [x] **Version stamping + outdated detection**: `WindowsProxyInstaller.DetectSync` reads `FileVersionInfo` off both the installed proxy DLL and `dist/`'s copy; outdated rows flagged in the panel; `Update all deployed` re-installs every stale row in one click.
- [x] **PE VERSIONINFO** baked into every shipped binary; `OnAttach` log records `UnityDumper DLL attached 0.1.0.139 261b0f2 (Release, 2026-05-05T14:00:28Z) backend=IL2CPP pid=…`.

**Phase 12d (32-bit Mono) — DEFERRED**

Required pieces for x86 support:
- [ ] x86 build configuration in CMake (current root `CMakeLists.txt` enforces x64 via `CMAKE_SIZEOF_VOID_P EQUAL 8`)
- [ ] x86 build of `UnityDumper-Mono.dll` + both proxy variants with Mono 32-bit struct offsets verified
- [ ] Cross-bitness injection in the UI (current `WindowsDllInjector` runs `CreateRemoteThread` from a 64-bit process — won't work into a 32-bit target)
- [ ] x86 / x64 selection in the install flyout based on detected target architecture

For 32-bit Mono targets today: the proxy install path *should* still work because the proxy is loaded by the game itself, BUT we currently only ship x64 proxy DLLs; the UI installer will copy x64 binaries that the 32-bit game can't load.

**Acceptance**: ✅ for 12a-12c
- Both proxy variants build to ~130 KB each, pass dependency-walker check (all forwarded exports present)
- Drop `version.dll` into a Steam game folder → game launches normally → backend DLL auto-loaded → UI connects without CE injection
- Bulk install/uninstall/update-all flow exercised end-to-end via the deploy panel

---

## Phase 13 — Offline Metadata CLI

**Goal**: Standalone command-line tool that parses `global-metadata.dat` without injecting into a game. Useful for static analysis, CI dumping, or working from a game backup.

- [x] `tools/metadata-cli/main.cpp` — new C++ project (chose C++ because `MetadataParser.cpp` is the canonical implementation and rewriting it in C# AOT would have meant maintaining two parsers)
- [x] **Refactor**: `MetadataParser.cpp` made backend-agnostic core; `BackendDetector` dependency moved to `MetadataParserHostPath.cpp` (DLL-only). The CLI links the core + Logger only.
- [x] CLI args: subcommands rather than `--out` flag — `info` (version + counts), `stats` (replicates `getMetadataStats` strip-heuristic), `types` (one JSON record per type def to stdout), `type <ns> <name>` (fields + decoded string defaults)
- [x] Output is line-oriented JSON to stdout; errors → stderr; exit 1 on failure
- [x] Version auto-detect (v24 / v27 / v29 / v31) via header version field; falls back gracefully on untested versions with a stderr note
- [x] Build wiring: `build.cmd cli` / `build.ps1 -Target MetadataCli` produces `dist/metadata-cli.exe` (~67 KB Release)

**Acceptance**: ✅
- `metadata-cli info <path>` against SaGa F2R global-metadata.dat returns `version=29, type_tables_parsed=true, type_definition_count=10001, string_literal_count=11392`
- `metadata-cli stats <path>` reports `verdict=clean, strip_ratio=0.0011`
- Smoke-tested against six games covering all four metadata versions (see Phase 15 / 14c)

---

## Phase 14 — Hardening

- [x] **14a** auto-disconnect on pipe teardown: `PipeClient` raises a `Disconnected` event when next read fails; `MainWindowViewModel` runs the same teardown the explicit Disconnect does, tagged `(game exited)`. Reconnect retry sized to the proxy's startup window — 12 attempts × 600 ms = ~7 s.
- [x] **14b** stripped-metadata banner: opportunistic post-handshake `getMetadataStats` call surfaces `partial_strip` / `heavy_strip` verdicts as a yellow banner.
- [x] **14c** IL2CPP metadata v24 / v27 / v29 / v31 dispatch — empirically-verified TypeDefinition layouts, six games tested, all four versions covered. Cross-checked against upstream Unity source in `vendor/libil2cpp/`; full audit captured in [docs/il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md).
- [x] **14d** Mono thread-attach audit: every direct `r.<runtime_fn>` call in pipe handlers reviewed; the only gap (walkArray's Mono element-class lookup running un-attached) fixed. Nested attach/detach deliberately avoided after build #54.
- [x] **14e** log rotation: code review confirmed `RotateIfNeeded` is correct (4 files × 8 MB caps); `tools/Get-LogStats.ps1` ships for post-session spot checks.
- [x] **14f** SEH-leaf audit: every `__try` body in `dll/src/` verified leaf-shape (one fn call or one memcpy). Both rules (SEH-leaf + Mono attach policy) codified in `Archiutecture.md`'s "Rules (MUST follow)".
- [x] AOT trim warnings = 0 (every commit since Phase 7 verifies `dotnet publish -c Release -r win-x64 -p:PublishAot=true` finishes 0/0; project enforces `TreatWarningsAsErrors=true` for trim/AOT analyzers).
- [x] **Pipe-client cancellation race fixed** (commit `7a2e424`): cancelling between write and read no longer leaves a stale response in the pipe buffer for the next caller. Honours `ct` around gate wait + before commit + after read; the actual write+read runs uncancellable.
- [x] **Pipe-client lifecycle fixed** (commit `694efa3`): split `DisconnectAsync` (re-usable) from `DisposeAsync` (terminal). Old code disposed on every Disconnect, bricking the gate so next Connect threw `ObjectDisposedException`. Regression covered by `PipeClientLifecycleTests`.
- [x] **DLL pipe is single-instance** (commit `3ef8a0b`): switched `CreateNamedPipeW` from `PIPE_UNLIMITED_INSTANCES` to `1` to kill the ghost-accumulation bug where dying games left lingering instances that the next client routed into.
- [x] **Lua-side pipe-open retry**: `unity_dissect.lua` + `unity_symbols.lua` retry `io.open` up to 15× at 200 ms before giving up — closes the race where the DLL's pipe-server worker thread hasn't called `CreateNamedPipeW` yet by the time the inject script returns "Pipe ready".
- [x] **Lua-side main-thread yield**: per-RPC `processMessages()` + `sleep(1)` so CE's window-message pump can run between back-to-back pipe round-trips. Prevents the apparent hang during a 300+ method JIT registration on Mono.

**Acceptance**: ✅ — all 14a-14f green and verified live.

---

## Phase 15 — Test Matrix

Detailed ledger in [docs/test-matrix.md](test-matrix.md). Summary:

### Game / Engine combinations (verified)

| # | Game | Unity Ver | Backend | Metadata | Status |
|---|------|-----------|---------|----------|--------|
| 1 | SaGa Frontier 1 Remastered | 2019.4 | IL2CPP | v24 | ✅ 7,514 types parsed |
| 2 | FINAL FANTASY VI Pixel Remaster | 2019.4 | IL2CPP | v24 | ✅ 11,438 types parsed |
| 3 | Townscaper | 2020.x | IL2CPP (x86) | v27 | ✅ 8,132 types parsed (proves bitness-neutral metadata) |
| 4 | SaGa Frontier 2 Remastered | 2021.3.39f1 | IL2CPP | v29 | ✅ 10,001 types; live walks + dissect verified |
| 5 | Farthest Frontier | 2022.x | IL2CPP | v31 | ✅ 15,848 types parsed |
| 6 | Emberward | 2022.x | IL2CPP | v31 | ✅ 23,982 types parsed |
| 7 | Hollow Knight Silksong | Unity 6 | Mono (bdwgc) | n/a | ✅ Live `PlayerData` walk + symbol registration end-to-end |
| 8 | BongoCat | 2022.x | Mono | n/a | ✅ Reference target during Phase 3 bringup |

All six leading types in each IL2CPP game match canonical `<Module>` / anonymous-types / `Mono.Runtime` etc. with correct IL token (`0x02000003` at row 2). v24/v27/v29/v31 TypeDefinition layouts cross-checked against Unity's official sources in `vendor/libil2cpp/` — see [docs/il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md).

### Cheat Engine compat

| CE Version | Lua bridge OK | Symbol register OK | CSX import OK |
|------------|---------------|--------------------|---------------|
| 7.5.1 | ✅ | ✅ | ✅ |
| 7.6 | ⬜ untested | ⬜ untested | ⬜ untested |

### Proxy variants

| Proxy | Test game type | Status |
|-------|----------------|--------|
| version.dll | generic | ✅ |
| winmm.dll   | audio-using game | ✅ |
| dxgi.dll    | DX11/12 game | deferred — not built |

**Acceptance**: ✅ — initial matrix populated, deferred items called out in `test-matrix.md`. Future games added by reusing the same `metadata-cli info` + live-walk verification flow.

---

## Phase 16 — Docs Finalization

Already in repo:
- [x] `docs/Archiutecture.md` (typo in filename preserved for git history; rename TBD)
- [x] `docs/pipe-protocol.md`
- [x] `docs/STATUS.md`
- [x] `docs/plan.md` (this file)
- [x] `docs/test-matrix.md`
- [x] `docs/il2cpp-metadata-crosscheck.md` (Phase 14c follow-up)
- [x] `docs/address-finder.md` (Phase 18 design spec)
- [x] `scripts/README.md` (CE bridge install + workflow)

Still TBD:
- [ ] `docs/dll-spec.md` — formal C ABI surface (`UnityDumper_*` exports)
- [ ] `docs/ui-spec.md` — Avalonia component breakdown, AOT constraints, MVVM patterns
- [ ] `docs/export-formats.md` — CE XML / CSX / SDK header detailed reference
- [ ] `docs/mono-internals.md` — Mono struct layouts, version diffs
- [ ] `docs/il2cpp-internals.md` — IL2CPP runtime struct layouts (companion to metadata-crosscheck)
- [ ] `docs/technical-notes.md` — collected gotchas
- [ ] `docs/lessons-learned.md` — cross-game debugging notes
- [ ] `docs/metadata-cli.md` — offline CLI usage
- [ ] `docs/proxy-dlls.md` — variant selection guide (version vs winmm)
- [ ] `Readme.MD` + `Readme_zh-TW.MD` — install / usage / screenshots
- [ ] `CONTRIBUTING.md`

Phase 16 is parallelizable with feature work; the existing docs cover the
runtime-critical contracts (pipe protocol, architecture, status). The
remaining items are user-facing reference / onboarding material.

---

## Phase 17 — Find Method Instances

**Goal**: Mirror Cheat Engine's "find method instances" with overload
disambiguation. Lets users target a specific overload by signature instead
of registering all methods of a class wholesale (which `unity_symbols.lua`
does today).

- [ ] Pipe RPC: `findMethodInstances(assembly, ns, class, methodName, [signatureMatch])` returning `[{ token, address, signature, declaringClass }]` for every matching overload
- [ ] Server-side: enumerate matching `MethodInfo*` (Mono / IL2CPP), compile + return native pointers
- [ ] Optional signature-string match for narrowing among overloads
- [ ] UI: new tab on the class browser surfacing all overloads with their addresses + a "register selected as CE symbol" action
- [ ] Lua bridge: extend `unity_symbols.lua` with a `registerMethodOverloads(asm, ns, class, name, [sigFilter])` helper that uses the new RPC

**Acceptance**:
- Given a class with overloaded methods (e.g. `Vector3.Set(float, float, float)` vs `Vector3.Set(Vector3)`), `findMethodInstances` returns both with distinct addresses and distinguishing signatures
- Selecting one overload in the UI registers a CE symbol pointing at the right native address; the other overload's symbol is not affected
- Useful follow-up to the per-row HEX → AOBMaker pipeline (Phase 11.5)

---

## Phase 18 — Address Finder (reverse lookup)

**Goal**: Inverse of `findInstances`. Given any address from CE's scanner /
pointer-scan, identify which Unity managed instance, class, and field that
address belongs to. Closes a structural gap between CE's address-centric
workflow and our class-centric inspector.

Spec: [docs/address-finder.md](address-finder.md).

- [x] **17a** DLL handler: `whatIsAt(address)` with Strategy A backward-scan from queried address, 8-byte stride, up to 4 KB (capped at 16 KB). Validates candidate `klass*` pointers via SEH-protected `*_class_get_name` calls + a class-pointer range cache built lazily on first use (and invalidated on `scan`). Mono-side cache also tracks the `MonoVTable*` set + a `vtable→klass` map since the object header points at a vtable, not the class. `getClassRangeStats` companion command exposes cache size for diagnostics. Smoke test: `tools/Test-WhatIsAt.ps1`.
- [x] **17b** Field-offset resolution: parent-chain walk + per-field size lookup (via `class_value_size` for value types, primitive-name fallback table, 8 bytes for references). Resolves to either `field` (exact range hit, with kind classifier `leaf`/`value-struct`/`pointer`/`interior`) or `nearestField` (closest declared field below the offset, with `nearestFieldDelta`). Pointer-deref recursion (chase pointer-to-class targets, T[]/List<T> element resolution, string-buffer detection) deferred — Strategy A returns enough data for the UI / Lua bridge to chain follow-up RPCs.
- [x] **17c** UI: dedicated *Address Lookup* tab next to Fields / Inspector / Methods + Inspector right-click "Find what's at this address" menu item. Tab carries an address input, backward-scan slider (default 4 KB, capped at 16 KB), and a result panel showing instance / class / fieldOffset + the resolved declared field with kind classifier. Cross-tab handoff: "Inspect this instance" hands the result to `LiveInspectionViewModel` via a new `FocusOnInstance(address)` helper and flips the right-pane TabControl back to Inspector. Inspector right-click pre-fills the row's effective address (deref pointer for pointer rows, storage address otherwise) and switches to the lookup tab.
- [x] **17d** CE Lua bridge: `scripts/unity_addrfind.lua` ships alongside the existing `unity_dissect` / `unity_symbols` scripts with the same `\\.\pipe\UnityDumpBfx` RPC + retry scaffolding. Public API: `whatIsAt(addr)` returns the parsed table, `lookupAddress(addr)` pretty-prints to CE's console, `getClassRangeStats(rebuild)` for diagnostics, plus optional `bindAddrLookupHotkey({VK_CONTROL, VK_F2})` that registers a CE hotkey firing `lookupSelectedAddress()` (reads CE memory view's `HexadecimalView.SelectionStart` / `TopAddress`, falls back to `inputQuery` when no memview is open). Build script's `*.lua` glob auto-includes the new script so `dist/` carries it.
- [ ] **17e** Strategy B (cached heap walk + interval tree) — only if a user reports needing batch lookups against a CE pointer-scan result. v1 ships Strategy A only.

**Acceptance** (full criteria in [address-finder.md](address-finder.md)):
- Given the address of a known live `PLY_WORK.HP` field on SaGa F2R, `whatIsAt` returns instance + class + field name + offset matching DiscreteUI's `findInstances` + `walkInstance` output
- Address `+8` bytes into a `Vector3` field's storage reports the field as `position` with the right `kind`
- Parent-class field (e.g. `MonoBehaviour.useGUILayout`) reports the declaring class, not the most-derived one
- Known-bad address (e.g. `0xDEADBEEF`) returns `found: false, reason: "address_unreadable"` instead of crashing the host
- CE main thread stays responsive during a backward scan

Estimated effort: ~3.5 days for v1.

---

## Cross-PC Workflow

每次切換 PC：

1. `git pull`
2. 開啟 `docs/plan.md`，看 **Last updated** 與 **Current phase**
3. 找到第一個 `[ ]` 項目繼續
4. 完成後：勾 `[x]`，更新 **Last updated**，commit & push
5. 大階段切換時更新 **Current phase** 與 Phase Overview 表的狀態欄

---

## Resolved Decisions (2026-04-27)

| Decision | Choice |
|----------|--------|
| Architecture support | **x64-only for v1**; x86 reserved for v2. Phase 5 (IL2CPP metadata deep parse) must be bitness-parameterized from day one (`MetadataView<arch::x64/x86>`, `using ptr_t = std::uintptr_t`) so a future x86 port is mechanical. v2 work: 32-bit `inject-x86.exe` helper, CMake x86 toolchain, build matrix doubles to 4 variants, proxy DLLs duplicated. |
| Cheat Engine compat | **CE 7.5+** (lower bound, was 7.6+) |
| UI theme | **Follow system** only — no in-app dark/light toggle |
| Proxy DLL variants | **`version.dll` + `winmm.dll` + `dxgi.dll`** (all three) |
| Offline metadata tool | **Yes** — separate CLI tool for offline `global-metadata.dat` analysis |
| Avalonia version | **v12** (`Avalonia` 12.x package) |
| xUnit version | **v3** (`xunit.v3` package, not classic xUnit) |
| Native AOT | **Mandatory** — UI must compile under Native AOT, zero reflection. Use source generators (`[JsonSerializable]`, `[ObservableProperty]`, compiled bindings only). All `PublishAot=true` warnings must be resolved, not suppressed. |
