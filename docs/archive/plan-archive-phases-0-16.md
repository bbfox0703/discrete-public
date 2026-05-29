# UnityCEDumper — Phase Archive (0 → 16)

> Phases 0 through 16 — bootstrap, both backends, live walking, UI
> skeleton, export formats, Lua bridge, proxy DLLs, offline CLI,
> hardening, test matrix, docs finalization. All shipped + closed +
> live-verified long before this archive was carved.
>
> Carved out of [plan.md](../plan.md) on 2026-05-19 to keep the live
> phase ledger focused on current work (Phase 17 onward). Phase
> Overview table in [plan.md](../plan.md) still references each phase
> by number; this file holds the detailed acceptance checklists and
> hand-off notes for each one.
>
> **Editing convention**: don't. These phases are done. New work
> appends a new phase to [plan.md](../plan.md), not this archive.

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
- [x] Version-specific header structs — v24 / v27 / v29 / v31 all dispatched (Phase 14c). Cross-checked against upstream Unity source in `vendor/libil2cpp/`; full audit in [docs/il2cpp-metadata-crosscheck.md](../il2cpp-metadata-crosscheck.md)
- [x] String heap reader (`nameIndex` → UTF-8 string)
- [partial] Tables: TypeDefinitions ✅, FieldDefinitions ✅, **StringLiterals ✅**, FieldDefaults ✅; MethodDefinitions deferred (no current consumer)
- [x] Cross-link metadata index ↔ runtime `Il2CppClass*` via `Il2CppMetadataRegistration` AOB — landed commit `5c59077` (2026-04-29). See "2d follow-up" below for the full description.
- [x] Enum default values (segment 2d v1: type-aware decode using runtime `value__` underlying type), **string literal extraction ✅**
- [x] Detection of stripped / obfuscated metadata (segment 3): `getMetadataStats` pipe command. Counts single-char / two-char user-type names (excluding `<...>` compiler-generated) and emits a verdict — `clean` / `partial_strip` / `heavy_strip`. IL2CPP path reads from `global-metadata.dat` directly; Mono path walks runtime classes.

**2d follow-up** (✅ shipped 2026-04-29, commit `5c59077`): full
`Il2CppMetadataRegistration` discovery in `GameAssembly.dll` so non-enum
const literals (e.g. `const int kFoo = 42`) in
`il2cppGetMetadataTypeFields` can also be type-aware-decoded.

**Approach used** — *structure-shape* scan, not a literal-byte AOB. See
[IL2CPPMetadataRegistration.cpp](../../dll/src/il2cpp/IL2CPPMetadataRegistration.cpp):

1. Enumerate every readable / non-executable / data PE section in
   `GameAssembly.dll`.
2. Step 8 bytes at a time through each section, testing whether the
   next 0x80 bytes match the `Il2CppMetadataRegistration` shape (v29 /
   x64 layout): seven `(int32 count, int32 zero-pad, ptr64-aligned)`
   slot pairs, then one `(uint64 count, ptr)` pair.
3. Reject candidates where any padding word is non-zero, any pointer
   is mis-aligned, count exceeds 134 M, or `typesCount < 100`.
4. Final verification: sample 5 spread indices into `types[]` and
   require ≥ 3 to point at valid `Il2CppType*` (8-byte-aligned, tag
   byte at +10 in `[0x01..0x20]`).
5. Cache the hit; subsequent `GetTypeTag(typeIndex)` reads
   `*(uint8_t*)(types[idx] + 10)` via SEH-protected read.

**Wired into** `il2cppGetMetadataTypeFields` (ExportAPI.cpp:1945).
Per-field FieldDefault decode now produces typed `value` + `enc_bytes`
+ `type_tag` for tags 0x02 (Boolean), 0x03 (Char), 0x04–0x0B (signed /
unsigned int8…int64), 0x0C–0x0D (R4 / R8 float / double), 0x0E
(String — handled separately, doesn't need the runtime types[] table).

**Smoke test**: [Test-MetadataFields.ps1](../../tools/Test-MetadataFields.ps1)
prints `Il2CppMetadataRegistration: YES/no` banner + per-field
`tag=0x.. value=.. enc=NB` for every field with a default.

**Cross-version validation status**:
- v29 (SaGa F2R / IL2CPP) — ✅ verified live; banner shows YES, type
  tags decoded correctly.
- v24 / v27 / v31 — ⏳ not formally validated. Structural-shape
  heuristic theoretically version-independent, but unverified.
  Quick win — recipe in
  [STATUS.md §"Mono-side smoke procedure" pattern](../STATUS.md).

**Tag coverage extension** (out of scope, drip-feed): tags 0x11
(ValueType), 0x12 (Class), 0x1C (Object) etc. for non-primitive const
literals are not decoded today. Practically rare in shipping game
code; defer until a user asks.

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
- [x] **14c** IL2CPP metadata v24 / v27 / v29 / v31 dispatch — empirically-verified TypeDefinition layouts, six games tested, all four versions covered. Cross-checked against upstream Unity source in `vendor/libil2cpp/`; full audit captured in [docs/il2cpp-metadata-crosscheck.md](../il2cpp-metadata-crosscheck.md).
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

Detailed ledger in [docs/test-matrix.md](../test-matrix.md). Summary:

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

All six leading types in each IL2CPP game match canonical `<Module>` / anonymous-types / `Mono.Runtime` etc. with correct IL token (`0x02000003` at row 2). v24/v27/v29/v31 TypeDefinition layouts cross-checked against Unity's official sources in `vendor/libil2cpp/` — see [docs/il2cpp-metadata-crosscheck.md](../il2cpp-metadata-crosscheck.md).

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
- [x] `docs/address-finder.md` (Phase 17 design spec)
- [x] `scripts/README.md` (CE bridge install + workflow)

Closed 2026-05-07 — every planned doc now exists:
- [x] `docs/dll-spec.md` — C ABI surface + module map + lifecycle + SEH leaf list
- [x] `docs/ui-spec.md` — Avalonia 12 AOT app breakdown, ViewModel organization, services + bridges
- [x] `docs/export-formats.md` — CE XML / CSX / SDK header schemas + live-mode rules
- [x] `docs/mono-internals.md` — Mono backend deep dive (resolver / domain / walker / invoker / runtime ops)
- [x] `docs/il2cpp-internals.md` — IL2CPP backend deep dive (runtime path + metadata-parser version dispatch)
- [x] `docs/technical-notes.md` — drip-feed catch-all (build numbering, log channels, cmake cache, CJK console, AV)
- [x] `docs/lessons-learned.md` — consolidates the memory entries + STATUS caveats (runtime_invoke marshaling, signatureMatch trap, Avalonia 12 trio, GC root, PowerShell ConvertTo-Json, etc.)
- [x] `docs/metadata-cli.md` — offline CLI usage + comparison vs live runtime
- [x] `docs/proxy-dlls.md` — version/winmm selection guide + ProxyDeployWindow walkthrough
- [x] `README.md` (root, English) + `README.zh-TW.md` (root, Traditional Chinese)
- [x] `CONTRIBUTING.md` (root) — build / test / code conventions / PR style

Phase 16 was parallelizable with feature work; landing it closed the
last "soft" item before declaring the repo presentable for outside
contributors.

---
