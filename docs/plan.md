# UnityCEDumper — Development Plan

> 跨 PC 共用的開發進度文件。每完成一個小步驟就把 `[ ]` 改成 `[x]` 並 commit。
> 所有里程碑驗收條件 (Acceptance) 必須通過才能往下一階段。

**Last updated**: 2026-04-28
**Current phase**: Phase 5 — IL2CPP Metadata Deep Parse (segment 2c ✅: string defaults decoded)

## Hand-off notes (latest)

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
| 5 | IL2CPP Backend (Metadata) | global-metadata.dat parser (v24/27/29/31) | 🟡 Next |
| 6 | Pipe Protocol Core Commands | getAssemblies / getClasses / getClassInfo / etc. | ⬜ |
| 7 | UI Skeleton (Avalonia + AOT) | Single-instance app, pipe client, handshake | ✅ |
| 8 | UI Panels — Tree & Browse | AssemblyTree, ClassBrowser, FieldStruct, MethodList | ⬜ |
| 9 | Live Inspection | InstanceFinder, LiveWalker, StaticField, read/write memory | ⬜ |
| 10 | Export Formats | CE XML, CSX (Structure Dissect), SDK header | ⬜ |
| 11 | CE Lua Bridge | unity_dump.lua, unity_dissect.lua, unity_symbols.lua | ⬜ |
| 12 | Proxy DLLs | version.dll / winmm.dll / dxgi.dll variants | ⬜ |
| 13 | Offline Metadata CLI | Standalone tool to parse global-metadata.dat | ⬜ |
| 14 | Hardening | Error paths, AOT trim warnings, log rotation, edge cases | ⬜ |
| 15 | Test Matrix | Mono Unity 2018/2020 + IL2CPP 2020/2021/2022/Unity 6 + CE 7.5/7.6 | ⬜ |
| 16 | Docs Finalization | dll-spec / pipe-protocol / ui-spec / internals docs | ⬜ |

---

## Phase 0 — Bootstrap

**Goal**: Empty-but-buildable skeleton everyone can clone and `build.ps1` cleanly.

- [x] `CMakeLists.txt` root + per-target (DLL Mono / DLL IL2CPP / ProxyDLL)
- [x] `build.ps1` with `-Target {DLL|UI|ProxyDLL|Test|MetadataCli|All}`, `-Backend {Mono|IL2CPP|Both}`, `-Mode {Debug|Release|Publish}`, `-Clean`
- [x] `build.cmd` thin wrapper
- [x] `build_number.txt` auto-increment hook (in `build.ps1`)
- [x] Directory layout per Architecture §Directory Structure (empty `.keep` placeholders OK)
- [x] `.gitignore`, `.gitmodules` (vendor submodules: nlohmann/json — placeholder, real submodule add deferred)
- [x] `dll/src/main.cpp` — minimal `DllMain` that opens log file
- [x] `ui/UnityDumpUI/` — Avalonia 12 template, AOT publish profile, `Mutex` single-instance
- [x] `ui/UnityDumpUI.Tests/` — xUnit v3 project with smoke tests
- [x] `scripts/unity_dump.lua` — stub with version banner
- [x] **First-build verification**: run `build.ps1 -Target All -Mode Release` on dev PC
- [x] Add `nlohmann/json` submodule (`git submodule add https://github.com/nlohmann/json.git dll/vendor/nlohmann_json`)
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
- [x] `vendor/nlohmann/json.hpp` (single header)
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
- [partial] Version-specific header structs — only v29 prefix is exposed today; v24 / v27 / v31 fall through with a warning but still parse the prefix
- [x] String heap reader (`nameIndex` → UTF-8 string)
- [partial] Tables: TypeDefinitions ✅, FieldDefinitions ✅, **StringLiterals ✅**, FieldDefaults ✅; MethodDefinitions still TBD
- [ ] Cross-link metadata index ↔ runtime `Il2CppClass*` via `Il2CppMetadataRegistration` AOB (deferred — see "2d follow-up")
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

- [ ] `handshake` (already in Phase 2 — verify final shape)
- [ ] `scan` — re-resolve exports, refresh domain pointer
- [ ] `getAssemblies`
- [ ] `getClasses` (with namespace grouping, paging if > N)
- [ ] `getClassInfo` (parent, fields, methods, properties, interfaces)
- [ ] `getFieldInfo`
- [ ] `getMethodInfo`
- [ ] `getStaticFieldData`
- [ ] `findInstances` (heap walk strategy doc'd in `lessons-learned.md`)
- [ ] `readMemory` / `writeMemory`
- [ ] `registerSymbol`
- [ ] `exportCeXml` / `exportCsx` / `exportSdkHeader` (delegate to FieldExporter — Phase 10)
- [ ] `getEnumValues`
- [ ] `resolvePointer`
- [ ] `walkInstance`
- [ ] Mono-only: `monoCompileMethod`, `monoGetVTable`
- [ ] IL2CPP-only: `il2cppGetMetadataStrings`, `il2cppGetGenericInstances`
- [ ] Error envelope (`{"id":N,"error":{"code":..,"message":..}}`)

**Acceptance**:
- `pipe-protocol.md` written with request/response examples; Python smoke-test script exercises every command

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

- [ ] `AssemblyTreePanel` — virtualized tree of assemblies
- [ ] `ClassBrowserPanel` — namespace/class hierarchy under selected assembly
- [ ] `FieldStructPanel` — DataGrid of fields (Name / Offset / Type / Attributes)
- [ ] `MethodListPanel` — methods with signature + native address
- [ ] Search/filter box (substring + case-insensitive) over all panels
- [ ] Inheritance toggle: "show inherited fields"

**Acceptance**:
- Drill from assembly → class → field/method on a real game without UI freeze (tree virtualization works on 5000+ classes)

---

## Phase 9 — Live Inspection

- [ ] `InstanceFinderPanel` — scan heap / pointer scan for instances of a class
- [ ] `LiveWalkerPanel` — read instance, render fields as live values, click-to-expand pointers
- [ ] `StaticFieldPanel` — viewer for static field block, type-aware decoding (string, array, list)
- [ ] Refresh / auto-refresh interval

**Acceptance**:
- Pick `PlayerController.health` in a known test game, see live value update as gameplay changes it

---

## Phase 10 — Export Formats

- [ ] `shared/FieldExporter.{h,cpp}` — type mapping per Architecture §CSX table
- [ ] CE XML generator (pointer chain `<CheatEntries>` rooted at static instance pointer)
- [ ] CSX generator (CE Structure Dissect import format)
- [ ] SDK header generator (`.h` with offsets + struct layout, parent fields included)
- [ ] UI buttons in `FieldStructPanel` to export selected class

**Acceptance**:
- Generated CE XML imports cleanly into Cheat Engine and resolves the live value
- Generated CSX imports into Structure Dissect, types decode correctly
- Generated `.h` compiles under MSVC

---

## Phase 11 — CE Lua Bridge

- [ ] `scripts/unity_dump.lua` — find game process, `injectDLL`, `callFunction("StartPipe")`, status banner in CE
- [ ] `scripts/unity_dissect.lua` — query DLL via Lua bridge → build CE Structure Dissect entries
- [ ] `scripts/unity_symbols.lua` — batch register symbols (Architecture §Symbol Registration Pattern)
- [ ] Verify only existing CE Lua APIs are used (no invented calls)

**Acceptance**:
- Open CE → load `unity_dump.lua` → DLL injected → UI's pipe handshakes successfully

---

## Phase 12 — Proxy DLLs (version / winmm / dxgi)

- [ ] `dll/src/proxy/ProxyCommon.{h,cpp}` — shared loader: `LoadLibrary("UnityDumper-{Backend}.dll")` + `DllMain` boilerplate
- [ ] `dll/src/proxy/version/ProxyMain.cpp` + `version.def` — forward every `version.dll` export to system copy
- [ ] `dll/src/proxy/winmm/ProxyMain.cpp` + `winmm.def` — forward every `winmm.dll` export
- [ ] `dll/src/proxy/dxgi/ProxyMain.cpp` + `dxgi.def` — forward every `dxgi.dll` export
- [ ] `build.ps1 -Target ProxyDLL` builds all three variants
- [ ] `ProxyDeployPanel` (UI) — pick variant (version / winmm / dxgi) + backend (Mono / IL2CPP), copy proxy + backend DLL into game folder, prompt to launch
- [ ] Detect which proxy is safest for the target (e.g., DX12 game → prefer dxgi, audio-heavy game → winmm)

**Acceptance**:
- Drop each of the three variants into a game folder → game launches normally → DLL loads → UI connects without CE
- All three forwarders pass dependency walker check (no missing exports)

---

## Phase 13 — Offline Metadata CLI

**Goal**: Standalone command-line tool that parses `global-metadata.dat` without injecting into a game. Useful for static analysis, CI dumping, or working from a game backup.

- [ ] `tools/MetadataCli/` — new C++ project (or C# AOT — TBD; pick whichever shares more code with `il2cpp/MetadataParser`)
- [ ] Reuses `il2cpp/MetadataParser` as a static lib (factor out from DLL build)
- [ ] CLI args: `metadata-cli <path-to-global-metadata.dat> [--out json|csv|sdk] [--filter <namespace>]`
- [ ] Emits same shape as DLL pipe responses (so UI could in theory load offline dumps later)
- [ ] Version auto-detect (v24 / v27 / v29 / v31)
- [ ] Distributed alongside main build via `build.ps1 -Target MetadataCli`

**Acceptance**:
- Run `metadata-cli global-metadata.dat --out json > dump.json` on a known game; assembly/class/field counts match the DLL-injected dump

---

## Phase 14 — Hardening

- [ ] All API calls wrapped in SEH; failures logged not crashed
- [ ] Mono `mono_thread_attach` audited everywhere
- [ ] IL2CPP version dispatch covers v24/27/29/31; emit clear log on unknown
- [ ] Pipe reconnect on disconnect (UI side)
- [ ] AOT trim warnings = 0; full publish run
- [ ] Log rotation tested (write 100MB, confirm only 4 files × 8MB remain)
- [ ] Stripped / obfuscated metadata yields graceful degraded mode, not crash

---

## Phase 15 — Test Matrix

### Game / Engine combinations

| # | Game | Unity Ver | Backend | Status |
|---|------|-----------|---------|--------|
| 1 |   | 2018.x | Mono | ⬜ |
| 2 |   | 2020.x | Mono | ⬜ |
| 3 |   | 2020.x | IL2CPP (v24) | ⬜ |
| 4 |   | 2021.x | IL2CPP (v27/29) | ⬜ |
| 5 |   | 2022.x | IL2CPP (v29) | ⬜ |
| 6 |   | Unity 6 | IL2CPP (v31) | ⬜ |

### Cheat Engine compat

| CE Version | Lua bridge OK | Symbol register OK | CSX import OK |
|------------|---------------|--------------------|---------------|
| 7.5        | ⬜ | ⬜ | ⬜ |
| 7.6        | ⬜ | ⬜ | ⬜ |
| latest     | ⬜ | ⬜ | ⬜ |

### Proxy variants

| Proxy | Test game type | Status |
|-------|----------------|--------|
| version.dll | generic | ⬜ |
| winmm.dll   | audio-using game | ⬜ |
| dxgi.dll    | DX11/12 game | ⬜ |

Record results in `docs/test-games.md` (game name, version, backend, pass/fail, notes).

---

## Phase 16 — Docs Finalization

- [ ] `docs/architecture.md` — finalized, replaces `Archiutecture.md` (note: typo in current filename)
- [ ] `docs/dll-spec.md`
- [ ] `docs/pipe-protocol.md`
- [ ] `docs/ui-spec.md`
- [ ] `docs/export-formats.md`
- [ ] `docs/mono-internals.md`
- [ ] `docs/il2cpp-internals.md`
- [ ] `docs/technical-notes.md`
- [ ] `docs/lessons-learned.md`
- [ ] `docs/test-games.md`
- [ ] `docs/metadata-cli.md` — offline CLI usage
- [ ] `docs/proxy-dlls.md` — variant selection guide (version vs winmm vs dxgi)
- [ ] `Readme.MD` + `Readme_zh-TW.MD` install/usage
- [ ] `CONTRIBUTING.md`

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
