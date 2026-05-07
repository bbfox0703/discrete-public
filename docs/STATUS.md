# UnityCEDumper — Session Hand-off

**Branch**: `dev` (one commit ahead of origin — push pending)
**Last commit**: `06195cc` (`docs(plan): draft Phase 19 — Lua-callable Unity methods + close out 18`)
**Build**: 208
**Current state**: **Phases 0–15 + 9.5 + 11.5 + 14c + 17 (17a/b/c/d) + 18 (18a/b/c/d) all closed**. **Phase 19** (Lua-callable Unity methods) drafted, not started — five sub-phases ready in [plan.md](plan.md#phase-19--lua-callable-unity-methods-lua--managed-invocation). **Phase 12d** (32-bit Mono) stays the only formally-deferred phase.

This session closed **Phase 18 (Find Method Instances)** end-to-end. Headline outputs:

- New wire commands `findMethodInstances(asm, ns, class, method, [signatureMatch])` + `getMethodAobPattern(address, length)` on both Mono and IL2CPP backends. Returns every overload's address, decoded signature, and a symbol-safe `humanSuffix` (`3float` / `Vector3` / `noargs` / `string_Card_Point`) used downstream by both UI and Lua bridge.
- New module pair `dll/src/{mono,il2cpp}/{Mono,IL2CPP}MethodFinder.{h,cpp}`. Resolver bindings extended for sig/param exports (all optional — old runtimes degrade gracefully).
- Hand-rolled minimal x64 length disassembler (`shared/AobPattern.{h,cpp}`, ~250 LOC) for AOB pattern generation. Conservative wildcarding: rel8/rel32 immediates (CALL/JMP/Jcc/LOOPxx) and RIP-relative ModRM disp32 → `??`; unknown opcode → fall through with literal bytes (the AOB stays valid, just less version-stable). Avoids a Zydis vendor dep.
- New UI modal: right-click any row in the Methods grid → **Find overloads…** opens `MethodOverloadsWindow` with multi-select checkboxes. Four output paths: **Copy AOB AA script** (CE Auto-Assembler with `aobscanmodule` + `registersymbol` + idempotent `discrete_logScan` Lua helper guarded by `if _G.discrete_logScan == nil`), **Copy registerSymbol script** (hardcoded-address path), **Copy addresses**, and **Create AA in CE** (pushes the script straight into CE's address list as a vtAutoAssembler memory record via AOBMaker's `CreateAAScript` verb on `\\.\pipe\AOBMakerCEBridge` — UE5CEDumper-style, optional auto-activate).
- `scripts/unity_symbols.lua` extended with `registerMethodOverloads(asm, ns, cls, method, [opts])`. Symbol shape `Unity:{Asm}:{Ns}.{Cls}.m_{Method}__{HumanSuffix}` so cheat tables can target a specific overload by `getAddress("...m_Set__3float")`.

**Phase 19 drafted today** — closes the long-standing "Lua-callable Unity methods" goal that's been sitting in [CLAUDE.md](../CLAUDE.md) and [Archiutecture.md](Archiutecture.md) but never made it into the phase ledger. Marshaled invocation of Mono `mono_runtime_invoke` / IL2CPP `il2cpp_runtime_invoke` with primitive + string args + boxed primitive / string / address return values + managed exception capture. v1 covers static + instance methods, defers `ref/out` params + open generics + value-type-by-value args. Five sub-phases (19a IL2CPP / 19b Mono / 19c instance + string marshaling / 19d UI MethodCallDialog / 19e new `scripts/unity_invoke.lua`). Spec doc to write alongside 19a: `docs/lua-invoke.md`. Estimated ~3 days for v1.

> Companion files: `plan.md` (canonical phase ledger), `pipe-protocol.md` (DLL ↔ UI contract), `Archiutecture.md` (rules / invariants), `test-matrix.md` (verified games + tooling versions), `address-finder.md` (Phase 17 design spec).

---

## What's done

| Phase | Status | Highlights |
|---|---|---|
| 0–11.5 | ✅ | Bootstrap → Mono/IL2CPP backends → live walking → UI panels → CE Lua bridge → inspector inline drilldown. |
| 12 (x64) | ✅ | `version.dll` / `winmm.dll` proxy DLLs + `ProxyDeployWindow` bulk-install panel. |
| 12d (32-bit Mono) | ⏳ Deferred | x86 build config, x86 backend DLL, cross-bitness inject — engineering effort, no external blockers. |
| 13 | ✅ | `dist/metadata-cli.exe` — offline `global-metadata.dat` parser. |
| 14a–14f | ✅ | Hardening: auto-disconnect, stripped-metadata banner, thread-attach audit, log rotation, SEH-leaf audit. |
| 14c | ✅ | IL2CPP metadata v24/v27/v29/v31 dispatch — six games verified across all four versions. Cross-checked vs `vendor/libil2cpp/`. |
| 15 | ✅ | [test-matrix.md](test-matrix.md) — 7 verified targets across two backends + four metadata versions. |
| **17 (17a/b/c/d)** | ✅ | **Address Finder (reverse lookup).** New wire command `whatIsAt(addr)` returning instance + class + declared field. DLL backward-scan + class-pointer cache, UI Address Lookup tab, CE Lua bridge `unity_addrfind.lua`. Spec: [address-finder.md](address-finder.md). |
| **18 (18a/b/c/d)** | ✅ | **Find Method Instances + AOB AA-script generator + AOBMaker direct push.** New wire commands `findMethodInstances` + `getMethodAobPattern`, hand-rolled x64 LDE, UI overloads modal, `unity_symbols.lua` overload-aware extension, `CreateAAScript` integration over `\\.\pipe\AOBMakerCEBridge`. |
| **19** | ⬜ Drafted | **Lua-callable Unity methods.** Marshaled invocation of managed methods from CE Lua. Closes the original CLAUDE.md goal. Plan in [plan.md](plan.md#phase-19--lua-callable-unity-methods-lua--managed-invocation). |
| 16 (docs) | 🟡 Partial | Architecture / pipe-protocol / STATUS / address-finder / il2cpp-metadata-crosscheck exist; `dll-spec`, `ui-spec`, `mono-internals`, `il2cpp-internals`, `export-formats`, `technical-notes`, `lessons-learned`, `metadata-cli`, `proxy-dlls`, `Readme.MD` (en+zh-TW), `CONTRIBUTING.md` still TBD. |

---

## What landed in this session — Phase 17, Phase 18, AOBMaker

### Phase 17 — Address Finder (reverse lookup) — closed 2026-05-07

- **17a DLL handler** (`{Mono,IL2CPP}AddressFinder.{h,cpp}`) — Strategy A backward scan from queried address (8-byte stride, default 4 KB / capped 16 KB). Validates candidate `klass*` pointers via SEH-protected `*_class_get_name` calls + a class-pointer range cache built lazily on first use (`scan` invalidates). Mono cache also tracks the `MonoVTable*` set + `vtable→klass` map since the object header points at a vtable, not the class. Companion `getClassRangeStats` for diagnostics.
- **17b Field-offset resolution** — parent-chain walk + per-field size lookup (via `class_value_size` for value types, primitive-name fallback table, 8 bytes for references). Reports either `field` (exact range hit, kind classifier `leaf` / `value-struct` / `pointer` / `interior`) or `nearestField` (closest declared field below the offset, with `nearestFieldDelta`).
- **17c UI tab** — dedicated *Address Lookup* tab next to Fields / Inspector / Methods + Inspector right-click "Find what's at this address". Cross-tab handoff "Inspect this instance" hands the result to `LiveInspectionViewModel.FocusOnInstance(address)` and flips back to Inspector.
- **17d CE Lua bridge** — `scripts/unity_addrfind.lua` ships `whatIsAt(addr)` / `lookupAddress(addr)` / `getClassRangeStats(rebuild)` / `lookupSelectedAddress()` + optional `bindAddrLookupHotkey({VK_CONTROL, VK_F2})` reading CE memory view's `HexadecimalView.SelectionStart` / `TopAddress` (graceful fallback to `inputQuery`).
- **NuGet bumps**: Avalonia 12.0.1 → 12.0.2, `Tmds.DBus.Protocol` 0.92.0 → 0.93.0, `Microsoft.NET.Test.Sdk` 18.4.0 → 18.5.1. AOT publish stays 0/0 warnings; `DiscreteUI.exe` shrank from 45.2 MB → 39.0 MB after the Avalonia bump.

### Phase 18 — Find Method Instances + AOB AA-script generator — closed 2026-05-07

- **18a-1 Resolver bindings** — Mono: `mono_method_signature`, `mono_signature_get_param_count`, `mono_signature_get_params`, `mono_signature_get_return_type`, `mono_method_get_param_names` (all optional). IL2CPP: `il2cpp_method_get_param_count`, `il2cpp_method_get_param`, `il2cpp_method_get_param_name`, `il2cpp_method_get_return_type` (all optional). Old runtimes degrade gracefully — empty paramTypes / returnType, address still ships.
- **18a-2 MethodFinder backends** — `dll/src/{mono,il2cpp}/{Mono,IL2CPP}MethodFinder.{h,cpp}`. Iterates `class_get_methods`, filters by name, decodes signature, JIT-compiles each match (Mono) / reads `MethodInfo->methodPointer` (IL2CPP). Shared `dll/src/shared/MethodSignatureFormat.h` (header-only) produces the symbol-safe `humanSuffix` so Mono and IL2CPP responses are byte-identical.
- **18a-3 Hand-rolled x64 LDE + AOB pattern handler** — `dll/src/shared/AobPattern.{h,cpp}` walks N bytes (default 32, capped 128) via a minimal x64 length disassembler with conservative wildcarding (rel8/rel32 immediates + RIP-relative ModRM disp32 → `??`; unknown opcode → fall through with literal bytes). Skipped Zydis vendor dep — for 32-byte prologues the relevant instruction subset is small. Wire response carries pattern + raw bytes + module name/base/offset + decoded stats.
- **18c UI overloads modal** — right-click Methods row → "Find overloads…" opens `MethodOverloadsWindow`. Multi-select via per-row checkbox (uses `DataGridTemplateColumn` with inline `<CheckBox Focusable="False">` so a single click toggles, mirrors the `ProxyDeployWindow` fix). New `AobAaScriptGenerator` in `Core/Services/Export/` produces a CE Auto-Assembler script with `aobscanmodule` + `registersymbol` lines + an idempotent `discrete_logScan` Lua helper. Multiple AA scripts on the same cheat table cooperate — the `if _G.discrete_logScan == nil then` guard means the helper only loads once. Symbol naming: `{Class}_{Method}_{HumanSuffix}` with auto-disambiguation (`_2` / `_3` suffix) on collision.
- **AOBMaker `CreateAAScript` direct push** — extended `AobMakerBridge` with the third verb (after `NavigateHexView` / `NavigateDisassembler`). New "Create AA in CE" button on the modal pushes the generated script straight into CE's address list as a vtAutoAssembler memory record (UE5CEDumper-style, no clipboard round-trip). Optional auto-activate flips the entry's checkbox on after creation. Bridge stays fail-soft when AOBMaker / CE aren't running (status line reads "AOBMaker pipe unavailable").
- **18d Lua bridge** — `scripts/unity_symbols.lua` gains `registerMethodOverloads(asm, ns, cls, method, [opts])`. Symbol shape `Unity:{Asm}:{Ns}.{Cls}.m_{Method}__{HumanSuffix}` (e.g. `__3float`, `__Vector3`, `__noargs`). Reuses the existing inline JSON parser + 15-attempt `io.open` retry + per-RPC `processMessages()` + `sleep(1)` yield.
- **CanExecute propagation fix** — `CommunityToolkit.Mvvm` doesn't auto-re-evaluate a command's CanExecute when a child observable item's property changes; only the parent VM's own properties propagate via `[NotifyCanExecuteChangedFor]`. `MethodOverloadRow.IsSelected` is a child-collection property, so the four selection-dependent commands (`CopyAobScript`, `CopyRegisterSymbol`, `CopyAddresses`, `CreateAaScriptInCe`) never fired `NotifyCanExecuteChanged` on row tick. Fix: subscribe to each row's `PropertyChanged` in `RefreshAsync`, flip the four commands' CanExecute manually when `IsSelected` toggles.

---

## Recent commit history (most recent first)

```
06195cc docs(plan): draft Phase 19 — Lua-callable Unity methods + close out 18
67faa2e fix(overloads): single-click checkbox in the overloads grid
1920901 fix(overloads): single-overload buttons + Create AA in CE via AOBMaker
75b1392 feat: Phase 18d — registerMethodOverloads in unity_symbols.lua
87f6eb1 feat: Phase 18c — UI overload picker + AOB AA script generator
d258564 feat: Phase 18a-3 — getMethodAobPattern handler with hand-rolled x64 LDE
10af5b5 feat: Phase 18a — findMethodInstances DLL handler (Mono + IL2CPP)
e0fb46b docs: outline Phase 18 sub-phases (18a/b/c/d)
12e76ff docs: renumber address finder 18→17, find-method-instances 17→18
68bb4f4 feat: Phase 17d — unity_addrfind.lua CE Lua bridge (closes Phase 18)
503818e Update NuGet packages version
1c1d90c feat: Phase 17c — Address Lookup tab + Inspector right-click handoff
8db32fb feat: Phase 17b — whatIsAt field-offset resolution
d04d36e fix(metadata): reject unsupported v32+ + document v35-v39 future work
5919069 feat: Phase 17a — whatIsAt + class-pointer cache (DLL)
a934513 docs(plan): refresh plan.md as canonical phase ledger
428962b docs: pin Address Finder feature spec + cleaner project goal
aecc0e6 docs: il2cpp metadata layout cross-check vs upstream Unity sources
```

---

## What's open

### Formally deferred

| Item | Why | Required to unblock |
|---|---|---|
| **Phase 12d — 32-bit Mono** | x86 support is unstarted. CMake currently enforces x64-only (`CMAKE_SIZEOF_VOID_P EQUAL 8` is a fatal); UI's `WindowsDllInjector` runs `CreateRemoteThread` from a 64-bit process and won't cross to 32-bit targets; we ship x64 proxy DLLs only. | Engineering only — no external blockers. Parallel x86 build config in cmake, x86 builds of `UnityDumper-Mono.dll` + both proxy variants with Mono 32-bit struct offsets verified, cross-bitness inject (or document "proxy install only" for x86), x86/x64 selection in the install flyout. |

### Drafted, not started

- **Phase 19 — Lua-callable Unity methods**. Closes the long-standing CLAUDE.md goal. Marshaled invocation of `mono_runtime_invoke` / `il2cpp_runtime_invoke` with primitive + string args + boxed primitive / string / address return values + managed exception capture. Five sub-phases (19a IL2CPP / 19b Mono / 19c instance + string marshaling / 19d UI MethodCallDialog / 19e `scripts/unity_invoke.lua`). v2 follow-ups deliberately not started: ref/out params, open generics, value-type-by-value args, async, main-thread marshaling. Plan in [plan.md](plan.md#phase-19--lua-callable-unity-methods-lua--managed-invocation). Estimated ~3 days for v1.

### Open follow-ups (not blocking)

- **Phase 16 — remaining docs** — `dll-spec.md`, `ui-spec.md`, `export-formats.md`, `mono-internals.md`, `il2cpp-internals.md`, `technical-notes.md`, `lessons-learned.md`, `metadata-cli.md`, `proxy-dlls.md`, `Readme.MD` (en + zh-TW), `CONTRIBUTING.md`. Parallelizable with feature work.
- **Phase 5 segment 2d** (long-deferred, non-blocking) — full `Il2CppMetadataRegistration` AOB scan in `GameAssembly.dll` so non-enum const literals (e.g. `const int kFoo = 42`) in `il2cppGetMetadataTypeFields` can be type-aware-decoded. Half-day effort.
- **Phase 17e — Strategy B address finder** — only-if-asked. Cached heap walk + interval tree for batch CE pointer-scan lookups. Skip until requested.
- **CSX live mode** — design dead-end as scoped (CSX is an offset-relative *structure* format; "live" only makes sense as snapshot-derived layout). CE XML already provides true absolute-address live mode. Defer until a user explicitly asks.
- **More automated test coverage** — current test suite covers protocol, exporters, pipe lifecycle, a few inspector helpers. Big gaps: classifier (enum vs value-type vs pointer routing), end-to-end `MetadataParser` round-trips per version, CE-XML live-mode rendering against a deterministic fixture, AobMakerBridge happy + sad-path with a fake pipe server. Drip-feed.
- **Highlight matching substring** in Fields / Methods / Inspector filter results — pure UI polish on top of the existing substring filter.
- **Inspector classifier unit tests** — easiest concrete test addition: feed `WalkedField` records with `IsEnum=true` / various pointer values into `WalkedFieldNode.FromInstanceField`, assert `Kind`. Catches regressions before the user has to launch a game.

---

## Suggested first steps for the new session

| Option | Effort | Why pick this |
|---|---|---|
| **Phase 19a — IL2CPP `callMethod` DLL handler** | One day | Biggest user-visible win remaining. Static methods + primitives + null + address args. AOT pointers ready, no JIT step — simpler design than Mono, lets us nail the marshaling shape before doubling it. Write `docs/lua-invoke.md` alongside. |
| **Phase 19b — Mono port of `callMethod`** | One day, straight after 19a | `mono_runtime_invoke` with the same arg-marshaling semantics. Reuses the existing `monoCompileMethod` path internally. Both backends reach feature parity for static + primitive args. |
| **Phase 19c — Instance methods + string marshaling** | One day | Adds `instance` arg, string in/out marshaling (alloc `MonoString*` / `Il2CppString*` for in-args, decode UTF-16 for return values), reference-type return as raw address. The piece that turns Phase 19 from demo to useful. |
| **Inspector classifier unit tests** | <½ day | Cheapest test win. Offline regression coverage for the classifier kind-routing logic. |
| **Highlight matching substring** | <½ day | Pure UI polish — small but visible improvement for users who filter heavily. |
| **Phase 16 docs** | Variable, parallelizable | No user-facing feature gain but important for adoption / contributor onboarding. |
| **Phase 12d (32-bit Mono)** | Multi-day | Required for any pre-Unity-2018 Mono game. No user has asked yet (no 32-bit Unity game in test matrix). |

### Live verifications (carried forward — still green)

These remain confirmed clicked-through on the dev box:

- ✅ Phase 9 — `MainGame/PLY_WORK` decode matches CE reference table value-for-value (SaGa F2R, IL2CPP).
- ✅ Phase 11 — `unity_dissect.lua` builds a 56-field Structure Dissect entry for `MainGame/PLY_WORK`; `unity_symbols.lua` registers 10 statics + 323 method JITs on Silksong's `GameManager`.
- ✅ Phase 14c — IL2CPP metadata v24/v27/v29/v31 dispatch verified against six games covering all four versions.
- ✅ Phase 17 — `whatIsAt` round-trips against SaGa F2R `PLY_WORK.HP` field address; lookup latency sub-ms after first cache build (~tens of ms).
- ✅ **Phase 18 — single-overload AA-script generation** verified against SaGa F2R `MainGame/PLY_WORK.AddGrowHpMax(int)` — 32-byte AOB pattern with 0 wildcards (simple `MOVSS [rcx],xmm0` prologue), suffix `int`, address resolves, "Copy AOB AA script" writes a complete script to clipboard.

### Pending verification (not yet exercised on a live game this session)

- **Phase 18 multi-overload path** — needs a real overloaded method. Canonical case: `Vector3.Set(float, float, float)` exists but Unity inlines the Vector3-arg variant on Unity 2021.3 so SaGa F2R only shows one. Try `string.Equals` or any user-written overloaded method.
- **AOBMaker `CreateAAScript` direct push** — code is in but not yet user-tested with CE + AOBMaker plugin running. Expected: clicking "Create AA in CE" creates a vtAutoAssembler entry titled `{Class}.{Method} {Suffix}` in CE's address list, optionally already enabled.
- **`unity_symbols.lua` `registerMethodOverloads`** — code is in but not yet exercised against a Lua console session.

---

## Repo layout (relevant bits)

```
discrete/
├── build.cmd / build.ps1       <- UE5-style wrapper; dist-copy ships scripts/ + README
├── build_number.txt
├── dist/                       <- gitignored; built artifacts land here
├── docs/
│   ├── plan.md                 <- canonical phase ledger
│   ├── pipe-protocol.md        <- DLL ↔ UI contract (current: protocol v1)
│   ├── Archiutecture.md        <- rules / invariants (note: filename typo preserved)
│   ├── address-finder.md       <- Phase 17 design spec
│   ├── il2cpp-metadata-crosscheck.md <- Phase 14c upstream-Unity audit
│   ├── test-matrix.md          <- verified games + tooling versions
│   └── STATUS.md               <- this file
├── dll/
│   ├── src/main.cpp            <- DllMain auto-runs Init + StartPipe
│   ├── src/shared/
│   │   ├── ExportAPI.h/.cpp    <- C ABI + every pipe handler
│   │   ├── AobPattern.h/.cpp   <- Phase 18a-3 hand-rolled x64 LDE
│   │   ├── MethodSignatureFormat.h <- Phase 18 humanSuffix builder (header-only)
│   │   ├── MagicStrings.h      <- pipe + command name constants
│   │   ├── Memory.h            <- SEH-protected SafeReadBytes
│   │   ├── PipeServer.h/.cpp   <- threaded named-pipe server
│   │   └── Logger.h            <- per-PID rotated log files
│   ├── src/mono/               <- MonoResolver/Domain/ClassWalker/AddressFinder/MethodFinder/...
│   └── src/il2cpp/             <- IL2CPPResolver/Domain/ClassWalker/AddressFinder/MethodFinder/...
├── ui/
│   ├── UnityDumpUI/            <- Avalonia 12 app (AOT). AssemblyName=DiscreteUI
│   │   ├── ViewModels/
│   │   │   ├── MainWindowViewModel.cs
│   │   │   ├── AssemblyBrowserViewModel.cs
│   │   │   ├── LiveInspectionViewModel.cs
│   │   │   ├── AddressLookupViewModel.cs       <- Phase 17c
│   │   │   ├── MethodOverloadsViewModel.cs     <- Phase 18c
│   │   │   ├── ProcessPickerViewModel.cs
│   │   │   └── ProxyDeployViewModel.cs
│   │   ├── Views/
│   │   │   ├── MainWindow.axaml + .cs
│   │   │   ├── ProcessPickerWindow.axaml + .cs
│   │   │   ├── ProxyDeployWindow.axaml + .cs
│   │   │   └── MethodOverloadsWindow.axaml + .cs <- Phase 18c
│   │   └── Resources/Strings/en.axaml
│   ├── UnityDumpUI.Core/       <- Protocol + services (AOT-safe)
│   │   └── Services/
│   │       ├── PipeClient.cs / BackendAdapter.cs / ProtocolModels.cs / ProtocolJson.cs
│   │       ├── AobMakerBridge.cs               <- 3-verb pipe client (Nav + CreateAAScript)
│   │       └── Export/
│   │           ├── UnityTypeMap.cs
│   │           ├── SdkHeaderExporter.cs / CsxExporter.cs / CeXmlExporter.cs
│   │           └── AobAaScriptGenerator.cs     <- Phase 18c
│   └── UnityDumpUI.Tests/       <- xUnit v3
├── tools/                       <- PowerShell pipe helpers + Phase 13 metadata-cli
│   ├── metadata-cli/            <- Phase 13 offline metadata parser
│   ├── Test-FindMethodInstances.ps1            <- Phase 18 smoke
│   ├── Test-AobPattern.ps1                     <- Phase 18a-3 smoke
│   ├── Test-WhatIsAt.ps1                       <- Phase 17 smoke
│   └── Test-*.ps1
└── scripts/                     <- CE Lua bridge
    ├── README.md
    ├── unity_dump.lua / unity_dissect.lua / unity_symbols.lua
    ├── unity_addrfind.lua                      <- Phase 17d
    └── utils.lua
```

---

## Known caveats / things to watch (carried forward)

- **AOT analyzers are strict.** Any new JSON serialization path must go through the source-gen `ProtocolJsonContext`. Reflection-based `JsonSerializer.SerializeToElement<T>(value)` trips IL2026 + IL3050. Pattern to copy: the `Str(string)` / `Int(int)` helpers in `BackendAdapter.cs`.
- **`LibraryImport` does NOT auto-resolve A/W**. Unlike classic `DllImport` with `CharSet`, every entry point name is taken literally. Use explicit `EntryPoint = "FooW"` for any function that has A/W variants.
- **Avalonia 12 clipboard API churn**: `DataObject` / `DataFormats` deprecated, `IClipboard.SetTextAsync` / `SetDataObjectAsync` removed. The only supported writer is `IClipboard.SetDataAsync(IAsyncDataTransfer)` — wrap a `DataFormat.Text` item in a `DataTransfer`.
- **Avalonia 12 `avares://` keys off AssemblyName, not RootNamespace.** Renamed the exe? Update `App.axaml`'s `ResourceInclude Source="avares://{AssemblyName}/..."` too.
- **Avalonia 12 `DataGridCheckBoxColumn`** requires entering edit mode (double-click) before the checkbox responds. Use `DataGridTemplateColumn` with an inline `<CheckBox Focusable="False">` for single-click toggles. (Bit `MethodOverloadsWindow` and `ProxyDeployWindow` — same fix.)
- **`CommunityToolkit.Mvvm` source-gen name collision**: a property named `Backend` collides with the generated field name. Use `Backend2`.
- **`CommunityToolkit.Mvvm` and child-collection CanExecute**: a command's CanExecute is NOT auto-re-evaluated when an item inside an `ObservableCollection<T>` raises PropertyChanged. Subscribe to each row's `PropertyChanged` and call `Notify*Changed()` manually. Pattern: `MethodOverloadsViewModel.OnRowPropertyChanged`.
- **IL2CPP `method_get_pointer`** is not exported in Unity 2021.3+. We read `MethodInfo.methodPointer` directly via SEH-protected struct read. Don't reintroduce the export-only path.
- **`-DBACKEND=` cmake cache poisoning**: cmake hates being switched between `Mono` / `IL2CPP` / `Both` in the same build dir. `build.ps1` wipes the per-backend build dir on every configure for this reason.
- **CJK Windows console** munges em-dashes printed by the script unless we route through `Start-Transcript`. `build.cmd` already does.
- **Game must be closed** before rebuilding the DLL — LNK1104 if the DLL is still mapped from a previous injection.
- **Antivirus** flags freshly-built `DiscreteUI.exe` (lots of `LibraryImport` native calls + remote thread injection P/Invokes). User has `dist/` excluded; mention this if a fresh dev hits a quarantine error after `build.cmd publish`.
- **`PipeClient` write+read runs uncancellable past commit.** Don't "fix" this back to passing `ct` everywhere — the race it solves (id-mismatch when caller cancels mid-request) is not theoretical, it broke rapid Find Instances clicks.
- **Phase 18 AOB decoder bails on unknown opcode** — falls through with literal bytes (no wildcards). The AOB stays valid, just less version-stable. By design — false positives (over-wildcarding) cause symbol ambiguity, which is the failure mode we must avoid. Bailout is reported via `decoderBailed: true, bailedAt: N` in the wire response.

---

## Quick reference

```powershell
# Full clean build (DLL + UI AOT + tests) -> dist/
.\build.cmd publish clean

# UI only, fast iteration (self-contained single-file in dist\)
.\build.cmd ui

# IL2CPP DLL only
.\build.cmd il2cpp

# Tests
.\build.cmd test

# Phase 18 smoke (after injecting via Test-Handshake.ps1):
.\tools\Test-FindMethodInstances.ps1 -ClassAssembly UnityEngine.CoreModule -Namespace UnityEngine -Name Vector3 -Method Set
.\tools\Test-AobPattern.ps1 -Address <hex-from-above>

# Phase 17 smoke:
.\tools\Test-WhatIsAt.ps1 -ProcessId <pid>
```

Pipe protocol contract: [pipe-protocol.md](pipe-protocol.md) (current: protocol v1, includes `walkArray` / `whatIsAt` / `findMethodInstances` / `getMethodAobPattern`).
PowerShell smoke tests: `tools/Test-*.ps1` — still useful for one-shot pipe RPCs.
CE-side workflow: [scripts/README.md](../scripts/README.md) — install layout + `dofile` invocations.
Canonical phase ledger: [plan.md](plan.md). All phase numbers, status flags, and acceptance criteria live there.
