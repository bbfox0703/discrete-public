# UnityCEDumper — Session Hand-off

**Branch**: `dev` (clean — pushed up to commit `9919b56`; will be merged to `main` in this hand-off commit)
**Last commit**: `9919b56 fix(bridge): Phase 22c — bootstrap smoke via pipe handshake, not executeCodeEx`
**Build**: 285 (Publish / AOT; DiscreteUI.exe 38.6 MB; 0/0 trim+AOT warnings)
**Tests**: 163/163 pass (was 57; +106 across Phase 22 — `AobMakerBridge` 11, `BridgeLuaResources` 14, `BootstrapAaScriptGenerator` 12, `BridgeInjector` 7, `CallMethodAaScriptGenerator` 22, `AddressLookupAaScriptGenerator` 26, `AobAaScriptGenerator` 5, plus +3 regression bars across the 3 AA generators)
**Current state**: **Phase 22 shipped 22a–22f + live-smoke session 2026-05-18 caught + fixed 6 P0/P1 bugs mid-flight.** Code surface (Tools menu, Send-to-CE buttons, bridge inject pipeline) all green; bridge files land in CE cleanly; bootstrap loads; Mono JIT method resolution works end-to-end. Live smoke completed Steps 1-8 against BongoCat (Mono, PID rotated 23484/40700/68344 as user restarted between iterations); **Steps 9-11 deferred to a fresh session** (MethodCall→CE, AddressLookup→CE, save/reopen persistence). The 6 bugs were exactly the kind of "would-fire-on-first-user-run" silent-failure modes that the unit test suite couldn't reach — each is now permanently locked out by a new regression test. Carry-forward from previous session: All earlier shipped phases verified live + 4 backlog cosmetics ✅ live-verified 2026-05-08 against build #251 (Silksong / SaGa F2R / FF VI / R-Type) + Inspector classifier & Browser filter-highlight polish landed in build #257. **Phase 20a (Scene/GameObject explorer Lua bridge)** is parked at "FindObjectsOfType(Type, bool) recipe untested".

> Companion files: [plan.md](plan.md) (canonical phase ledger), [pipe-protocol.md](pipe-protocol.md) (DLL ↔ UI contract), [Archiutecture.md](Archiutecture.md) (rules / invariants), [test-matrix.md](test-matrix.md) (verified games + tooling versions), [lua-invoke.md](lua-invoke.md) (Phase 19 spec), [address-finder.md](address-finder.md) (Phase 17 spec).

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
| 15 | ✅ | [test-matrix.md](test-matrix.md) — **9 verified targets** across two backends + 4 metadata versions × 5 Unity major versions (2018.1 / 2019.4 / 2021.3 / 2022.3 / 6.1). Latest additions (2026-05-07): R-Type Dimensions EX (oldest IL2CPP — Unity 2018.1, surfaced missing-exports finding) + Songs of Silence (newest IL2CPP — Unity 6.1 / v39, first-class supported). |
| 17 (a/b/c/d) | ✅ | **Address Finder (reverse lookup).** New wire command `whatIsAt(addr)` returning instance + class + declared field. DLL backward-scan + class-pointer cache, UI Address Lookup tab, CE Lua bridge `unity_addrfind.lua`. Spec: [address-finder.md](address-finder.md). |
| 18 (a/b/c/d) | ✅ | **Find Method Instances + AOB AA-script generator + AOBMaker direct push.** New wire commands `findMethodInstances` + `getMethodAobPattern`, hand-rolled x64 LDE, UI overloads modal, `unity_symbols.lua` overload-aware extension, `CreateAAScript` integration over `\\.\pipe\AOBMakerCEBridge`. |
| **19 (a/b/c/d/e)** | ✅ | **Lua-callable Unity methods.** New wire RPC `callMethod(asm, ns, class, method, [signatureMatch], [instance], [args])` invoking Mono / IL2CPP methods with marshaled primitive + null + address + string args + boxed-primitive / address / UTF-8-decoded-string returns + managed exception capture. Static AND instance dispatch on both backends. UI MethodCallWindow (right-click any overload row → Call method…) + `scripts/unity_invoke.lua` Lua bridge with full arg-builder set. Spec: [lua-invoke.md](lua-invoke.md). |
| **19d-2** | ✅ | **UI polish.** Methods grid "Call method… (pick overload)" auto-skips the picker modal when `findMethodInstances` returns exactly one overload — single-overload user methods (`AddItems`, `get_numberOfItems`, `Save`) become one click. Inspector right-click on any instance → "Call method on this instance… (switch to Methods)" snaps `SelectedInstance` to that row + flips to the Methods tab; the subsequent Methods-grid right-click then pre-fills the `Instance address` textbox in `MethodCallWindow` (scope-checked: only when Browser.SelectedClass matches Inspector.TargetClass on assembly + namespace + name). |
| 16 (docs) | ✅ | All planned files exist. New in this batch: `lessons-learned.md`, `README.md` + `README.zh-TW.md` (root), `CONTRIBUTING.md` (root), `proxy-dlls.md`, `metadata-cli.md`, `export-formats.md`, `mono-internals.md`, `il2cpp-internals.md`, `dll-spec.md`, `ui-spec.md`, `technical-notes.md`. |
| **22 (Live CT Population via AOBMaker)** | ✅ | All sub-phases (22a–22f) shipped 2026-05-12 across 6 commits. `AobMakerBridge` extended with InjectTableFile + CreateMemoryRecord + CreateAAScriptWithRecords + CheckAvailability; 7 lua bridge files embedded as `<EmbeddedResource>` in Core; bootstrap AA-script generator (idempotent guard + preload trick for `unity_dump.lua`'s `require 'utils'`); `BridgeInjector` orchestrator (8-step pipeline w/ progress + structured failure result); MainWindowViewModel hoisted AobMaker to top-level + Tools menu RelayCommand; per-feature `CallMethodAaScriptGenerator` + `AddressLookupAaScriptGenerator` for MethodCallWindow + AddressLookup tab; new [`docs/ce-bridge-integration.md`](ce-bridge-integration.md) + [`tools/Test-BridgeInject.ps1`](../tools/Test-BridgeInject.ps1) wire smoke. **Live click-through smoke pending.** |

---

## What landed in this session — Phase 22f live smoke (2026-05-18) caught + fixed 6 P0/P1 bugs

**Setup**: clean Cheat Engine 7.6 + AOBMaker plugin loaded + empty CT + DiscreteUI build #279 + BongoCat (Mono). Walked through [`docs/ce-bridge-integration.md` § "Manual smoke checklist"](ce-bridge-integration.md).

**Outcome**: Steps 1-8 of the 11-step checklist completed green after 6 fix iterations. Each iteration surfaced a real production-blocker that the pre-existing unit test suite couldn't catch (live-runtime / multi-process / Lua-parse / Windows-pipe-kernel concerns). Final session ended at the boundary of Steps 9-11 (MethodCall→CE, AddressLookup→CE, save/reopen).

### Bugs caught + fixed (in surfacing order)

| # | Bug | Severity | Commit | Fix |
|---|---|---|---|---|
| 1 | `unity_scene.lua` shipped with CRLF in working tree → AOBMaker plugin's `Stream.Size` verify mismatched the pre-LF-normalisation byte count → bridge inject **aborts at file 7/8** | P1 silent-fail | [`571ddf0`](https://github.com/) | `.gitattributes` enforces `*.lua text eol=lf` repo-wide; working copy normalised; `Read_EmbeddedContent_IsLfOnly_NoCrlf` [Theory] × 7 + tightened byte-for-byte round-trip test (was 5% tolerance, now exact). |
| 2 | `AobAaScriptGenerator` emitted `aobscanmodule(name, , pattern)` for Mono (empty module from `getMethodAobPattern`). CE accepts the syntax; scan **never matches**; symbol stays unresolved. | P1 silent-fail | [`bcc2ff6`](https://github.com/) | Per-overload: empty `ModuleName` → fall back to `aobscan(name, pattern)` whole-process scan + 5 regression tests in new `AobAaScriptGeneratorTests`. (This fix worked for unique patterns but unblocked bug #4 below.) |
| 3 | Bootstrap AA had `print('… CE''s symbolhandler …')` (C#-style apostrophe doubling). Lua reads `''` as close-then-open-string → `')' expected near "s symbolhandler…"` → **entire chunk fails to parse** → bridge never loads. | P0 silent-fail (the worst — "OK" status from CE, no bridge state, one cryptic error) | [`a169c1f`](https://github.com/) | Lua `\'` escape + `\w''\w` regex regression bar in all 3 generators (catches the bug shape — `letter''letter` — without false-positiving on legitimate empty-string `''` between commas). |
| 4 | Mono JIT routinely emits **multiple instantiations** of the same source method (different generic params, optimization tiers); their prologues are byte-identical. `aobscan(pattern)` returned 3 hits for `Mathf.Abs(int)` — CE picked the first which was wrong. | P1 wrong-answer | [`253402a`](https://github.com/) | Architectural fix: Mono JIT path no longer AOB-scans at all. New `registerMethodSymbol(symName, asm, ns, cls, method, sigFilter)` in `unity_symbols.lua` calls `findMethodInstances` RPC (DLL-side `mono_compile_method` — overload-precise). AA `[ENABLE]` emits `{$lua}` block with the bridge call instead of `{$asm} aobscan(...)`. IL2CPP path (AOT addresses in `GameAssembly.dll`) unchanged — keeps fast `aobscanmodule` form. |
| 5 | DLL pipe was `nMaxInstances=1` (Phase 11 design). Phase 22's "DiscreteUI persistent + bridge bursts" workflow contends for the single slot → bridge `io.open(PIPE)` returns ERROR_PIPE_BUSY (`"Invalid argument"` via Lua CRT) → **AA scripts can never reach the DLL** while DiscreteUI is connected. | P0 silent-fail | [`8075675`](https://github.com/) | `nMaxInstances=4` + detached worker thread per accepted client. Main loop returns to `CreateNamedPipeW` immediately; concurrent DiscreteUI + bridge work cleanly. Handlers already read-mostly (Mono/IL2CPP runtime is re-entrant); per-handler mutex deferred until a real race surfaces under load. |
| 6 | Bootstrap smoke called `U.callDLL('UnityDumper_GetBuildNumber','uint32')` via CE `executeCodeEx` (remote thread in target). Returns nil even when DLL + pipe + every other pipe-RPC works. Printed misleading "DLL not yet reachable" on every successful bridge load. | P2 false-negative | [`9919b56`](https://github.com/) | Replaced with inline pipe-`handshake` RPC (same path the bridge's `registerMethodSymbol` / `callUnityMethod` use). If those work, smoke passes. New `Build_SmokeChecksDllViaPipeHandshake` test pins the new shape + explicitly forbids the old `executeCodeEx` / `U.callDLL` / `UnityDumper_GetBuildNumber` substrings. |

### What's verified live (Steps 1-8 of the manual smoke checklist)

- ✅ **Step 1**: Clean Publish build (build #285, AOT 0/0 warnings, `DiscreteUI.exe` 38.6 MB)
- ✅ **Step 2**: CE 7.6 + AOBMaker plugin + empty CT — `AOBMaker: connected` green chip in DiscreteUI immediately
- ✅ **Step 3**: DiscreteUI launches, connectivity dot green
- ✅ **Step 4**: Tools → "Inject Discrete Bridge into CE Table" — 8/8 wire calls (after CRLF fix), ~120 ms total
- ✅ **Step 5**: CE `Table → Show table files` lists all 7 `unity_*.lua` files with expected byte sizes
- ✅ **Step 6**: Enable "Load Discrete Bridge" → CE Lua console prints all 7 module banners (after apostrophe-escape fix) → ready message via pipe handshake (after smoke replacement)
- ✅ **Step 7**: DiscreteUI Pick & Inject BongoCat → connected → build matches DLL build number
- ✅ **Step 8**: Methods grid right-click `UnityEngine.Mathf.Abs(System.Int32)` → "Create AA in CE" → new entry with `registerMethodSymbol(...)` form → enable → `[discrete] Mathf_Abs_int = 236D471F080` — single resolved address, no AOB ambiguity (after Mono-bridge-resolve + concurrent-pipe fixes)

### What's deferred to the next session

- ⏳ **Step 9** — MethodCallWindow → "Send to CE Table…" → wraps `callUnityMethod(asm, ns, cls, method, args, opts)` into AA → enable → expect `prettyPrint` output. Target: `Mathf.Abs(-7) → 7`.
- ⏳ **Step 10** — Address Lookup → right-click an Inspector field → "Find what's at this address" → AddressLookup tab → "Send to CE Table…" → expect parent AA + 1 child memory record showing the live field value in CE's grid.
- ⏳ **Step 11** — `File → Save As` → close CE → reopen → re-enable bridge + per-feature entries → verify all entries survive the save/reopen cycle.

Recovery state for the next session:
- All 6 fix commits on `dev` + merged to `main` (this hand-off commit)
- BongoCat may have been closed by user after the smoke; if so: relaunch → DiscreteUI Pick & Inject before any CE work
- CE may have been closed; if so: relaunch → reopen the in-progress smoke CT (or start fresh and re-run Tools→Inject Discrete Bridge)
- `dist/` is at build #285 — both DLLs + DiscreteUI.exe + 7 `*.lua` files all current

### Cumulative across Phase 22 (a→f + live-smoke fixes)

- **13 production commits** on `dev` (22a-22f + 6 live-smoke bug fixes)
- **+106 xUnit cases** (57 → 163, all pass)
- **6 production-blocker bugs** caught by the live smoke (would have hit on first user run on every machine — esp. bugs #1, #3, #4, #5)
- New Core types: `BridgeInjector`, `BridgeLuaResources`, `BootstrapAaScriptGenerator`, `CallMethodAaScriptGenerator`, `AddressLookupAaScriptGenerator`, `AaScriptChildRecord`, `CeValueType`, plus `Assembly` field added to `AaScriptOverload`
- Bridge methods added to `AobMakerBridge`: +4 (`InjectTableFile`, `CreateMemoryRecord`, `CreateAAScriptWithRecords`, `CheckAvailability`) + `AvailabilityChanged` event
- Bridge Lua additions: new `registerMethodSymbol(symName, asm, ns, cls, method, sigFilter)` in `unity_symbols.lua` — single-overload bridge-resolve (Phase 22f follow-up to `registerMethodOverloads`)
- DLL changes: `nMaxInstances=1 → 4` + thread-per-client in `PipeServer.cpp`
- UI surface added: Tools menu + 2 Send-to-CE buttons + AOBMaker connectivity dot + bridge-inject status banner
- New docs: `ce-bridge-integration.md`, lessons-learned additions for AOBMaker + executeCodeEx + concurrent-pipe + CRLF
- New smoke: `tools/Test-BridgeInject.ps1` (3 modes: PingOnly / default minimal / Full production replay)
- Build: #261 → #285 (24 build increments across the phase)

**Phase 22 outcome**: the AOBMaker-based bridge install + per-feature push surface is production-ready against Mono. IL2CPP path is structurally unchanged from Phase 18c (AOB scan against `GameAssembly.dll` — known good). Steps 9-11 close the remaining smoke; the architectural correctness has been demonstrated end-to-end.

---

## What landed in the prior session — Phase 22 (Live CT Population via AOBMaker), end-to-end

Six commits across one session. The original Phase 22 plan ("offline `.CT` builder with Ascii85 + XML serializer + cart UI") got scrapped on day 1 after auditing AOBMaker's CE plugin (which already exposes `InjectTableFile` + 4 sibling commands that mutate a live, currently-open CT) and UE5CEDumper's `<EmbeddedResource>` pattern. New approach: DiscreteUI fires AOBMaker pipe commands; user saves via CE File→Save. Saved ~1.5 days from the original 3.5-day estimate.

### 22a — `AobMakerBridge` extension (commit `296f76f`)

`InjectTableFileAsync(fileName, content) → (bool Ok, string? ErrorMessage)`, `CreateMemoryRecordAsync(...)`, `CreateAAScriptWithRecordsAsync(description, script, records, autoActivate, hideChildren)`, `CheckAvailabilityAsync()`. New `IsAvailable` property + `AvailabilityChanged` event (transition-only fire — matches `PipeClient.Disconnected` pattern). New `CeValueType` enum (CE TVariableType ordering 0–9) + `AaScriptChildRecord` record type. Wire model extended with 6 new optional fields + nested `records[]` array via source-gen JSON context (AOT-clean). 15s timeout for InjectTableFile (matches CE-side `synchronize()` + `Stream.Size` verification cost). Tests: 11 new xUnit cases via fake `NamedPipeServerStream` (matches `PipeClientLifecycleTests` pattern). `InternalsVisibleTo("UnityDumpUI.Tests")` added so the test-only `(FileLogger?, string pipeNameOverride)` ctor is reachable.

### 22b — Embed 7 bridge lua files (commit `99b25f8`)

7× `<EmbeddedResource Include="..\..\scripts\unity_*.lua">` entries in `UnityDumpUI.Core.csproj` with stable `UnityDumpUI.Core.Resources.Bridge.<file>` LogicalName. New `Services/BridgeLuaResources.cs` exposes `LoadOrder` (utils first — `unity_dump.lua` does `require 'utils'`), `Read(name)`, `ReadAll() → IReadOnlyList<(FileName, Content)>`, `ListEmbeddedNames()` diagnostic. Hosted in Core (not UI as the original plan sketched) so the test project's existing Core-only `ProjectReference` reaches the accessor cleanly. AOT-safe (`GetManifestResourceStream` is on the AOT happy path). Tests: 14 new (incl. [Theory] across all 7 files + manifest-matches-LoadOrder check + byte-for-byte round-trip vs disk for `unity_scene.lua` — the largest file, most likely to surface stream truncation).

### 22c — Bootstrap AA-script generator (commit `421c3c8`)

`BootstrapAaScriptGenerator.Build()` emits the "Load Discrete Bridge" AA whose `[ENABLE]`:
1. Idempotency guard via `_G.discrete_bridgeLoaded`.
2. `findTableFile` + `Stream.copyFrom` + `DataString` per file (CE's portable read pattern).
3. **Pre-populates `package.preload['utils']`** before running `unity_dump.lua` — solves the `require 'utils'` problem in the no-filesystem CT context without modifying any existing script.
4. Iterates `BRIDGE_FILES`, skips utils, `load() + chunk()` each file.
5. Smoke: `pcall U.callDLL('UnityDumper_GetBuildNumber','uint32')` — soft-fails with a clear message if DLL not yet injected.
6. Sets `_G.discrete_bridgeLoaded = true`.

`[DISABLE]` clears the flag (CE has no chunk-unload primitive — clearing the flag is enough to allow re-`[ENABLE]`). Const-string template + minimal substitution (`__BRIDGE_FILES_LIST__` + `__ENTRY_DESCRIPTION__`); no dynamic AA generation. Tests: 11 new cases pinning preload-before-loop ordering, file-list match with `BridgeLuaResources.LoadOrder` via regex token count, no leftover `__PLACEHOLDERS__`, `EntryDescription = "Load Discrete Bridge"` stability for AOBMaker duplicate-detect when it ships.

### 22d — Tools menu + `BridgeInjector` orchestrator (commit `6cdfdc3`)

New `BridgeInjector.InjectAsync(IAobMakerBridge, IProgress<BridgeInjectProgress>?, CancellationToken) → BridgeInjectResult` runs the 8-step pipeline (1× CheckAvailability + 7× InjectTableFile + 1× CreateAAScript). Stops on first failure with structured `BridgeInjectResult { Ok, FilesInjected, AaScriptCreated, FailedStepDescription, ErrorMessage }` so the VM can build a precise status line ("Failed at 'Injecting unity_dump.lua': …") without re-deriving labels. Stateless / static — testable in isolation with a mock `IAobMakerBridge` (no PipeClient/FileLogger entanglement).

VM hoist: `AobMaker` is now a top-level `MainWindowViewModel` property (was leaking through `Inspector.AobMaker`). Inspector + MethodOverloads + Tools menu share one instance. New `[ObservableProperty] IsAobMakerAvailable` mirrors the bridge's `IsAvailable` via `AvailabilityChanged`; opportunistic startup probe sets the connection-bar dot. New `[ObservableProperty] BridgeInjectStatus` — per-step status line (persists after run for read-at-your-pace UX).

UI: Tools ▾ Button + MenuFlyout in the connection bar (room for Phase 22e per-feature entries to join later). "AOBMaker: connected/not connected" colored status dot. Bridge inject status banner (green strip, monospace) appears only after the first inject this session.

**Documented deviation from plan**: menu item NOT pre-gated on `IsAobMakerAvailable`. Pre-gating creates a chicken-and-egg loop (user opens CE *after* DiscreteUI → menu stays disabled forever without a polling timer). Instead the command runs CheckAvailability as step 1 and surfaces "open CE with the AOBMaker plugin loaded" via `BridgeInjectStatus`. Same UX pattern as the existing InjectAndConnect button.

Tests: 7 new orchestrator cases pinning call order, file sequence, abort-on-each-failure-mode, plugin-error message forwarding.

### 22e — Per-feature Send-to-CE-Table entries (commit `bcd378d`)

Two new generators in Core/Services/Export:
- `CallMethodAaScriptGenerator.Build(asm, ns, cls, method, sigMatch, instance, args)` — wraps `callUnityMethod(...)` from `unity_invoke.lua` into a `{$lua}` AA block. Maps every primitive type to its `argTYPE(v)` builder. Single-quote/backslash/newline escape for string args. Invariant-culture float (catches CJK locale comma trap). Out-of-range integers clamp instead of fail.
- `AddressLookupAaScriptGenerator.Build(req)` — wraps a `WhatIsAtResult` into `CreateAAScriptWithRecords` payload: parent AA registers `DiscreteAt_<Cls>_<hex>` symbol, child records expose the resolved field at its offset with proper CLR→CE TVariableType mapping (incl. enum unwrapping to underlying type).

UI wiring: MethodCallWindow + AddressLookup grew "Send to CE Table…" buttons (bound to `HasAobMaker` → button hidden when no bridge). Bridge threaded through via the existing `CopyToClipboardAsync`-style post-construction setter pattern (no ctor signature churn). MethodOverloadsViewModel exposed its bridge as a public property so MethodOverloadsWindow can thread it through to child MethodCallWindow.

Bridge-loaded guard prefix on every generated `[ENABLE]`:
```lua
if not _G.discrete_bridgeLoaded then
  print('[Discrete] Enable "Load Discrete Bridge" first')
  return
end
```
Phase 18c's pure-AOB scripts deliberately don't carry the guard (they don't depend on the bridge — `aobscanmodule` + `registersymbol` are standalone).

Drive-by fix to `BridgeInjectorTests`: `Progress<T>.Report` posts to the captured `SyncContext`, which xUnit doesn't have. Replaced with a synchronous `SyncProgress<T>` helper. Test now passes deterministically.

Tests: 48 new ([Theory] across 10 primitive arg builders + 14 CLR→CE type mappings + escape/clamp/sanitize edges).

### 22f — Integration doc + wire smoke (this commit)

New [`docs/ce-bridge-integration.md`](ce-bridge-integration.md): user journey (5-click happy path), what lands in the CT (file list + AA shape), generated entry shape examples for each of the 3 generators, troubleshooting (10+ scenarios), manual smoke checklist (11 steps to run after any Phase 22 code change), architecture references.

New [`tools/Test-BridgeInject.ps1`](../tools/Test-BridgeInject.ps1) PowerShell wire smoke. Three modes:
- `-PingOnly` — just probes pipe availability.
- (default) — minimal smoke: 1 stub `_discrete_smoke.lua` inject + 1 stub AA script. Useful for "is the pipe wired right?" probes without needing the repo's bridge files.
- `-Full` — replays the production pipeline reading `scripts/*.lua` from the repo (bypasses DiscreteUI, useful for AOBMaker plugin update validation without launching DiscreteUI / a Unity game).

Per-step result printed with timing + plugin-side error message on failure. Production timeouts respected (15s for InjectTableFile, 5s for everything else).

### Cumulative across 22a–22f

- 6 production commits + 1 doc-only commit
- 91 new xUnit cases (57 → 148, all pass)
- 1 new orchestrator (`BridgeInjector`)
- 3 new AA-script generators (`Bootstrap`, `CallMethod`, `AddressLookup`)
- 1 extended bridge (`AobMakerBridge` +4 commands +1 event)
- 1 new resource accessor (`BridgeLuaResources` over 7 embedded `.lua` files)
- 1 hoist (`AobMaker` from `Inspector` → top-level `MainWindowViewModel`)
- 1 new menu entry + 2 new buttons + 1 status indicator + 1 status banner in MainWindow.axaml
- 1 new doc (`ce-bridge-integration.md`)
- 1 new smoke (`Test-BridgeInject.ps1`)
- AOT 0/0 trim+AOT warnings throughout, `DiscreteUI.exe` 38.3 MB → 38.6 MB (+0.3 MB for 7 embedded scripts + Core code)

**Live click-through smoke pending** — see [`docs/ce-bridge-integration.md` § "Manual smoke checklist"](ce-bridge-integration.md). Recipe: clean CE 7.6 + AOBMaker plugin + empty CT + DiscreteUI + a known-good Unity target → 11 steps → expect 7 lua files in the table + 4 AA-script entries (1 bootstrap + 1 AOB + 1 callMethod + 1 AddressLookup), each enabling cleanly.

---

## What landed in the session before — 4-fix live verification + Inspector classifier extract + Browser filter highlight

Three deliverables across one session. Heavy on test coverage + UX polish, light on new capability.

### Cross-PC sync trap surfaced + recovered

Session opened with a four-target verification of the 2026-05-08 backlog fixes — and **all four failed at first run**. Root cause: `Inject-Dll.ps1` reads from `build/dll-both-release/bin/`, not `dist/`. The 18:56 build that landed in `build/` predated the 21:11–21:18 fix commits. `dist/UnityDumper-IL2CPP.dll` had been updated to 21:18:03 (21 sec before the last fix commit) so it was *also* still missing one fix. Forcing a `.\build.ps1 -Target DLL -Backend Both` (build #251) made every fix live-pass on the next try. Logged in [lessons-learned.md](lessons-learned.md) — DO NOT skip a rebuild before live verification when the most recent thing you did was a commit, even if `dist/` artifacts have a "recent" timestamp.

### Backlog-sweep fixes — all 4 live-verified ✅

Targets / observations (full breakdown in §"Open follow-ups" entries below):

| Fix | Commit | Target | Observation |
|---|---|---|---|
| Mono `findInstances class_ptr` | `3e519a8` | Hollow Knight Silksong / Mono / Unity 6000.0 | `class_ptr : 0x000002201761E3D0` (no fallback to `vtable`), 100 Transform instances. |
| R4/R8 `value_kind` for Inf/NaN | `fd428d2` | SaGa F2R / IL2CPP / v29 | `PositiveInfinity` → `value:null, value_kind:"infinity"`; `NegativeInfinity` → `"-infinity"`; `NaN` → `"nan"`; finite floats all `"normal"`. Raw JSON sanity-checked end-to-end. |
| v24 integer-decode | `cb3d100` | FF VI / IL2CPP / v24 / Unity 2019.4 | `Int32.MaxValue` → 2147483647 (was -2147483648); `MinValue` → -2147483648 (was 0); `enc=4B` proves the raw-LE branch fired. |
| Unity 2018.x missing IL2CPP exports | `587e794` | R-Type Dimensions EX / IL2CPP / v24 / Unity 2018.1 | `assemblies_count=36` (was 0); `findInstances UnityEngine.GameObject` returned 100 matches with `class_ptr` populated; scan only 529 ms — fallback enumeration is fast enough to be the production path, not a degraded slow lane. |

Drive-by cleanup in commit `0315071`: `ExportAPI.cpp:1863` C4244 narrowing warnings cleaned up (`metadataVersion` retyped from `uint16_t` to `int32_t` to match `MetadataParser::version()`'s return). 0 warnings post-build #253.

### Inspector classifier extracted + 38 unit tests (commit `1e0d861`)

`WalkedFieldKind` enum + `ClassifyInstanceField` + `TryParseCollectionType` + `TryParseClassName` moved out of `LiveInspectionViewModel.cs` (net -150 lines from the VM) and into a new pure-logic class `UnityDumpUI.Core.Services.InstanceFieldClassifier`. The VM now delegates; behaviour unchanged.

New `InstanceFieldClassifierTests.cs` covers all five precedence rules:
- **Rule 1**: `IsEnum` trumps everything (including the gotcha case where `IsValueType=true` AND `IsEnum=true` — without the short-circuit, the field would route to `ValueStruct` and crash on expand).
- **Rule 2**: Collection (`T[]` / `List<T>`) with empty-pointer degradation to `Leaf`.
- **Rule 3**: `ValueStruct` via server-side `IsValueType` flag (modern resolver) vs the legacy `UnityTypeMap`-driven path (which only catches Unity-known structs like Vector3 / Quaternion / Color — documented limitation; custom user structs need the modern resolver's `IsValueType=true`).
- **Rule 4**: `PointerToClass` including the dotted-namespace-in-generic-arg case (`MainGame.ClassAry<MainGame.CARRYITEM>` resolves to outer `MainGame.ClassAry`).
- **Rule 5**: `Leaf` fallback for `System.*`, unknown types, null type, etc.

### Browser filter-highlight UX (commit `950c0d8`)

New `HighlightedTextBlock` Avalonia control + `UnityDumpUI.Core.Services.HighlightSegmenter` pure-logic helper. Wired into the four filtered Browser lists:

| List | Filter binding | Highlighted columns |
|---|---|---|
| Assemblies (ListBox) | `Browser.AssemblyFilter` → `#AssemblyFilterBox.Text` | Name |
| Classes (ListBox) | `Browser.ClassFilter` → `#ClassFilterBox.Text` | Name + Namespace |
| Fields (DataGrid) | `Browser.FieldFilter` → `#FieldFilterBox.Text` | Name + Type |
| Methods (DataGrid) | `Browser.MethodFilter` → `#MethodFilterBox.Text` | Name |

Inspector Fields grid is deliberately untouched — it has no filter (drives off `walkInstance` results); nothing to highlight.

Two implementation notes worth remembering (now logged in [lessons-learned.md](lessons-learned.md)):
1. **Avalonia 12 `TextBlock.Text` and `Inlines` are mutually-exclusive sources.** Setting both leads to undefined rendering. The control uses a `SourceText` property and flips between plain-text and inline-runs internally, clearing the other source first.
2. **DataGrid highlight requires `DataGridTemplateColumn` + `CellTemplate`** — `DataGridTextColumn` doesn't accept custom controls. Switching preserves sort behaviour via `SortMemberPath="Name"` etc.

Element-name binding (`{Binding #FilterBox.Text}`) means the live filter value flows into every row template without the VM having to push it onto each row record.

`HighlightSegmenter` has 14 [Fact]/[Theory] cases covering all the edge cases — empty filter, null source, filter-not-found, at start / end / middle, multiple occurrences, adjacent matches, case-insensitive preservation, filter-equals-source, overlapping patterns (resolved left-to-right, no greedy backtrack).

Total test count went from **5 → 57** across the session.

### Tooling state for the new PC

- `dist/` is up to date as of build #257 (Publish / AOT — `DiscreteUI.exe` 38.3 MB, both DLLs at 576 / 545 KB).
- `build/dll-both-release/bin/` matches dist (verified — `Inject-Dll.ps1` will pick up the fresh DLLs).
- All 4 verification targets are documented in the §"Open follow-ups" block — Silksong / SaGa F2R / FF VI / R-Type. If you want a fresh smoke run on the new PC, the recipes are in §"Suggested first steps" → "Re-verify on the new PC" (added below).

---

## What landed previously — Phase 16 docs + Phase 5 segment 2d cross-version sweep

Two big chunks across one long session. No code changes to the DLL or UI binaries; pure documentation refresh + smoke-test infrastructure + verification against five Unity-version targets.

### Phase 16 docs — closed end-to-end

12 new files (≈ 3300 lines):

- **Repo root**: [`README.md`](../README.md), [`README.zh-TW.md`](../README.zh-TW.md), [`CONTRIBUTING.md`](../CONTRIBUTING.md).
- **`docs/`**: [`lessons-learned.md`](lessons-learned.md), [`proxy-dlls.md`](proxy-dlls.md), [`metadata-cli.md`](metadata-cli.md), [`export-formats.md`](export-formats.md), [`mono-internals.md`](mono-internals.md), [`il2cpp-internals.md`](il2cpp-internals.md), [`dll-spec.md`](dll-spec.md), [`ui-spec.md`](ui-spec.md), [`technical-notes.md`](technical-notes.md).

`lessons-learned.md` consolidates every memory entry (runtime_invoke marshaling, signatureMatch substring trap, Avalonia 12 trio, GC root, PowerShell ConvertTo-Json, AOT analyzer rules, etc.) into one searchable place. The two READMEs + `CONTRIBUTING.md` are the new user-facing entry points; the seven `docs/*-spec.md` / `*-internals.md` files are the contributor-facing deep dives. Phase Overview row 16 flipped to ✅.

### Phase 5 segment 2d — cross-version sweep + bug surface

Started by correcting a stale framing — `Phase 5 segment 2d` follow-up actually shipped commit `5c59077` on 2026-04-29; STATUS / plan kept describing it as deferred. Then ran the cross-version smoke against four IL2CPP targets covering metadata v24 / v29 / v31 / v39 across Unity 2018.1 → 6.1.

**Headline finding**: the `Il2CppMetadataRegistration` structural-shape AOB scan is **fully version-portable** — found cleanly on every target tested (Unity 2018.1 / 2019.4 / 2021.3 / 2022.3 / 6.1). Heuristic correctness confirmed across 5 Unity major versions and 4 metadata versions.

**Three new findings, all deferred but documented with fix sketches**:

1. **v24 integer-decode bug** (FF VI / Unity 2019.4) — `il2cppGetMetadataTypeFields`'s I*/U* family decoder assumes CLI compressed-int + sentinel encoding (correct for v29+) but v24 metadata stores raw little-endian bytes. `Int32.MaxValue` returns `-2147483648` instead of `2147483647`. Fix: per-version dispatch hooked off `MetadataParser::version()`. ~1 hr.

2. **Unity 2018.x missing IL2CPP exports** (R-Type Dimensions EX / Unity 2018.1) — runtime is missing `il2cpp_image_get_class_count` + `il2cpp_image_get_class` (added between Unity 2018.x and 2019.x). `IL2CPPResolver` bails (`required 16/18, resolved=no`); every runtime RPC returns `"il2cpp runtime not resolved"`. Registration scan still works (proves it's runtime-ABI-sensitive, not metadata-version-sensitive). Fix: demote both exports to OPTIONAL + add `MetadataParser`-table-driven fallback through `il2cpp_class_from_name`. ~2 hr.

3. **v32+ metadata.dat parsing not implemented** (Songs of Silence / Unity 6.1) — `MetadataParser` rejects v39 by design (`version > 31` guard). However the v39 runtime sweep ([`Test-V39RuntimeSweep.ps1`](../tools/Test-V39RuntimeSweep.ps1)) shows **9 / 9 runtime paths green** on v39 — Inspector / class browser / Phase 17/18/19 all work. Only `il2cppGetMetadataTypeFields` const decode + offline `metadata-cli` are gated. So **v39 is effectively first-class supported** for ~95 % of usage; v32+ parser work (2-3 day project per [il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md)) only unlocks the const-decode niche.

Bonus minor findings:
- `findInstances` IL2CPP path returns empty `classPtr` field (matches still come back). Cosmetic backlog item.
- JSON spec doesn't allow `Infinity` / `NaN`; `nlohmann/json` coerces to `null` for special-case floats. R4 decoder runs but value field is null on the 3 special values. Cosmetic.

### Phase 18 multi-overload AA-script user-click verified

Closed the last open Phase 18 verification: Farthest Frontier `ItemBundle.AddItems` (2 overloads — `(System.UInt32, System.UInt32)` + `(ItemBundle)`) — both AOB patterns scan-clean from CE, both `registersymbol` succeed, addresses resolve in `GameAssembly.dll+0xB20900` / `+0xB20980`. Two-symbol `[ENABLE]` block is idempotent.

### New tooling

- **[`tools/Test-MetadataRegistration.ps1`](../tools/Test-MetadataRegistration.ps1)** — diagnostic probe for `il2cppGetMetadataRegistration` in isolation. Useful when `MetadataParser` rejects metadata.dat (e.g. v32+) but you want to verify the registration scan independently.
- **[`tools/Test-V39RuntimeSweep.ps1`](../tools/Test-V39RuntimeSweep.ps1)** — composite "what works on a v32+ target" sweep (9 independent runtime paths, one client connection). Generic enough to use on any IL2CPP target as a smoke.

### Files touched (whole session)

13 docs files (`STATUS.md`, `plan.md`, `test-matrix.md`, `il2cpp-metadata-crosscheck.md`, plus the 9 new docs), 3 root files (`README.md` + `README.zh-TW.md` + `CONTRIBUTING.md`), 2 new smoke scripts. **No code changes to the DLL, UI, or Lua bridges.** The docs / plan / matrix are now ground truth for handing off to a fresh checkout on another machine.

---

## What landed two sessions ago — Phase 19d-2 polish

Two UX improvements on top of the freshly-shipped Phase 19 vertical. UI-only.

- **Auto-skip overloads modal on count==1**. Methods grid right-click "Call method… (pick overload)" probes `findMethodInstances` first; on count==1, jumps straight to `MethodCallWindow`. Multi-overload methods still drop into the picker. Split handler pair in `MainWindow.axaml.cs` (`OnMethodCallMethodClicked` + unchanged `OnMethodFindOverloadsClicked`) + shared `ResolveMethodRow` / `BuildOverloadsVm` helpers.

- **Inspector cross-tab "Call instance method…"**. Right-click any instance in the Inspector → "Call method on this instance… (switch to Methods)" snaps `SelectedInstance` + flips to Methods tab. The Methods-grid right-click then pre-fills `Instance address` in `MethodCallWindow` (scope-checked: only when Browser.SelectedClass matches Inspector.TargetClass on assembly + namespace + name).

Files: [`MainWindow.axaml.cs`](../ui/UnityDumpUI/Views/MainWindow.axaml.cs), [`MainWindow.axaml`](../ui/UnityDumpUI/Views/MainWindow.axaml), [`MethodOverloadsViewModel.cs`](../ui/UnityDumpUI/ViewModels/MethodOverloadsViewModel.cs), [`MethodOverloadsWindow.axaml.cs`](../ui/UnityDumpUI/Views/MethodOverloadsWindow.axaml.cs), [`Resources/Strings/en.axaml`](../ui/UnityDumpUI/Resources/Strings/en.axaml). AOT publish 0/0 warnings; `DiscreteUI.exe` 38.3 MB.

---

## What landed three sessions ago — Phase 19 (full vertical) + UI Windows-only cleanup

### Phase 19 — Lua-callable Unity methods (closed end-to-end)

- **19a — IL2CPP DLL handler (static-only)**. New module pair `dll/src/il2cpp/IL2CPPMethodInvoker.{h,cpp}`. Single-pass overload resolver mirrors Phase 18a's iterator (returns `MethodInfo*` directly, no double-walk). Six new optional resolver bindings: `il2cpp_runtime_invoke`, `il2cpp_object_unbox`, `il2cpp_object_get_class` (load-bearing in 19a), plus `il2cpp_string_new` / `il2cpp_string_chars` / `il2cpp_string_length` (pre-bound for 19c). Whole `runtime_invoke` call wrapped in one SEH leaf so a hard crash in the JIT/AOT entry becomes `error: native_crash_in_runtime_invoke`. Exception decode reads `Il2CppException + 0x18` for `_message` (Mono-derived `System.Exception` layout, stable across Unity 2018+). Verified live on SaGa F2R 2026-05-07.

- **19b — Mono port (static-only)**. New module pair `dll/src/mono/MonoMethodInvoker.{h,cpp}` mirrors the IL2CPP shape exactly — only the dispatcher changes (`mono_runtime_invoke` instead of `il2cpp_runtime_invoke`). Six new optional Mono bindings (note: `mono_string_new` takes a `MonoDomain*` first arg, unlike IL2CPP). Address field of the response populated via `mono_compile_method` (Mono is JIT — no AOT-`methodPointer` shortcut). Wire shape byte-identical to 19a; per-backend error-code suffix only. Verified live on BongoCat 2026-05-07.

- **19c — Instance dispatch + string in/out marshaling**. Lifts the static-only / no-string restrictions on both backends with byte-identical wire-shape changes. **Marshaling fix mid-19c**: initial implementation used the "uniform" convention from Mono docs (params[i] = &slot for both value AND reference types). Parse("42") returned FormatException because the trampoline read &slot as if it were a managed string. Switched to the **non-uniform convention** used by BepInEx / MelonLoader / Il2CppInterop: value types still pass `&slot`, but reference types (Null / String / Address) pass the object pointer **directly** in `params[i]`. The Mono-docs "uniform" convention turns out to be wrong (or at least mismatched with what the actual x64 trampoline expects). String args use `il2cpp_string_new(utf8)` / `mono_string_new(domain, utf8)`; string returns decode UTF-16 → UTF-8 via the existing `DecodeManagedString` helper each invoker carries. Verified live on SaGa F2R + BongoCat + Farthest Frontier (the last via `ItemBundle.get_numberOfItems()` instance call).

- **19d — UI MethodCallWindow**. Right-click any row in the `MethodOverloadsWindow` grid → "Call method…" opens the new `MethodCallWindow` modal seeded from that overload's signature (paramTypes + paramNames threaded into one editor row each). New `CallArgFactory` in `UnityDumpUI.Core` wraps the AOT-safe `JsonSerializer.SerializeToElement` calls per wire `type` tag (Null / Bool / Int / UInt / Float / String / Address) so the source-gen `ProtocolJsonContext` stays internal — VMs in the outer `UnityDumpUI` project build wire payloads through this public seam. Editors v1: single TextBox per param plus a "null" checkbox enabled for reference types (smart-typed editors deferred to 19d-2 polish). Result panel renders typed return value, captured managed exception, or protocol error per the switch-on-`returnType` rule from `lua-invoke.md`. Also added a sibling "Call method… (pick overload)" entry to the main Methods grid context menu so the workflow is discoverable from one click in. AOT publish stays clean (0/0 trim/AOT analyzer warnings).

- **19e — `scripts/unity_invoke.lua` CE Lua bridge**. New standalone script (18 KB, ships to `dist/` via the existing `*.lua` glob). Public API matches the plan's example one-for-one: `callUnityMethod(asm, ns, class, method, args, opts)` where `opts = { instance, signatureMatch }`. Arg builders for every wire type tag (`argNull` / `argBool` / `argByte` / `argSByte` / `argInt16..64` / `argUInt16..64` / `argFloat` / `argDouble` / `argChar` / `argString` / `argAddress`) — keeps callers from hand-typing `{type="int32", value=...}` for every arg. Result helpers: `asAddress(v)` decodes "0x..." returnValue back to a Lua number; `prettyPrint(r)` logs a one-line human summary; `pcallUnityMethod` is the non-throwing variant; `findOverloads(asm, ns, class, method)` wraps `findMethodInstances` so users can list overloads from CE Lua before picking a `signatureMatch`. Standalone (does NOT `require` sibling scripts) — ships its own JSON parser AND its own JSON serializer (`callMethod`'s `args` is a nested array of `{type, value}` objects, which the flat-only `{"k":v}` builders the other scripts use can't express). Header comment carries quick-start + full API + the substring-trap and GC-root caveats so the dofile target is self-documenting. **Verified live on IL2CPP (SaGa F2R) — all 4 scenarios green** (Mathf.Abs(int) → 7, Concat → "Hello, World", Convert.ToByte(-1) → OverflowException, prettyPrint round-trip).

### Windows-only UI cleanup (commits `128e1cb` + `62e8468`)

Dropped `Avalonia.Desktop` metapackage in favor of bare `Avalonia.Win32` + `Avalonia.Skia` + `Avalonia.HarfBuzz` (all three needed; `UsePlatformDetect` used to wire all three behind one call). Linux is not in scope; Avalonia.X11 + Avalonia.FreeDesktop + Tmds.DBus.Protocol all dropped from the AOT graph. ILC dead-code diagnostics gone, `DiscreteUI.exe` 39.0 → 38.0 → 38.1 MB. **Two stumbles** along the way (now both saved to memory): missing `.UseSkia()` (silent black window), then missing `.UseHarfBuzz()` + `Avalonia.HarfBuzz` package (silent process exit with `0xC0000409`, no log file). Both fixed in `62e8468`. Program.cs now reads `.UseWin32().UseSkia().UseHarfBuzz().WithInterFont()`.

### Deploy script fix (commit at HEAD)

`tools/Install-CEScripts.ps1` had a hardcoded `$luaNames` list (4 files) — was missing `unity_addrfind.lua` (Phase 17d!) AND `unity_invoke.lua` (Phase 19e). Switched to `Get-ChildItem -Filter '*.lua'` glob so future scripts ship automatically. Glob now picks up all 6 files in `dist/`.

---

## Recent commit history (most recent first)

```
950c0d8 feat(ui): highlight matching substring in Browser filter results
1e0d861 refactor(inspector): extract field classifier to Core + 38 unit tests
0315071 docs+fix: live-verify 4 backlog fixes (build #251) + clean ExportAPI C4244
2143a77 docs: refresh STATUS / plan after backlog sweep — 4 fixes shipped, Phase 22 active
587e794 fix(il2cpp): Unity 2018.x — demote two missing exports + MetadataParser fallback
cb3d100 fix(dll): v24 integer-decode — per-version dispatch on FieldDefault decoder
fd428d2 fix(dll): emit value_kind for R4/R8 const literals (Infinity / NaN preserved)
3e519a8 fix(dll): Mono findInstances now also returns class_ptr (cross-backend uniform)
e2bf776 feat: Phase 20a Lua scene-explorer bridge + 4 cliffs from live debug
1b86335 Update sync_vendor.ps1
8a8223e docs: refresh STATUS / plan for cross-PC handoff
f69cf2b docs: Phase 5 segment 2d — R-Type Dimensions EX (Unity 2018.1 / v24) surfaces missing IL2CPP exports
a32e6dc docs/tools: Phase 5 segment 2d — v39 runtime sweep 9/9 paths green on Songs of Silence
52c5879 docs(crosscheck): refresh v35-v39 section — production reality, not future work
938605c docs/tools: Phase 5 segment 2d — v24 + v39 cross-version verification
85e3f43 docs: correct stale Phase 5 segment 2d entries — already shipped, not deferred
7994d95 docs: complete Phase 16 — add 9 new docs/ files + close out the phase
ffbade7 docs: add README (en + zh-TW) and CONTRIBUTING at repo root
```

---

## What's open

### Formally deferred

| Item | Why | Required to unblock |
|---|---|---|
| **Phase 12d — 32-bit Mono** | x86 support is unstarted. CMake currently enforces x64-only (`CMAKE_SIZEOF_VOID_P EQUAL 8` is a fatal); UI's `WindowsDllInjector` runs `CreateRemoteThread` from a 64-bit process and won't cross to 32-bit targets; we ship x64 proxy DLLs only. **User has a 32-bit Mono test target available (confirmed 2026-05-08) — not blocked on demand, only on engineering effort.** Re-estimated as ~3-5 days focused work. | Engineering only — no external blockers. Parallel x86 build config in cmake, x86 builds of `UnityDumper-Mono.dll` + both proxy variants with Mono 32-bit struct offsets verified, cross-bitness inject (or document "proxy install only" for x86), x86/x64 selection in the install flyout, plus expand test matrix with the 32-bit target. |

### Phase 19 polish (drafted but deferred — would land as 19d-3)

- **Smart-typed editors** — NumericUpDown for sized ints (range-checked at the editor level instead of post-Call), ComboBox for enum params via `getEnumValues`. Current TextBox + culture-invariant parsing + post-Call error message works correctly but is plain.

### Open follow-ups (not blocking, drip-feed)

Closed in this session, kept here as crossed-out reference until next prune:

- ~~**Phase 16 docs**~~ — ✅ closed 2026-05-07. All planned doc files now exist (see Phase Overview table).
- ~~**Phase 5 segment 2d cross-version smoke**~~ — ✅ partially closed 2026-05-07. v29 / v31 / v39 verified; v24 partial (registration + R4/R8 OK, I*/U* decoder bug); v27 still uncovered. See full table in [§Phase 5 segment 2d cross-version smoke](#phase-5-segment-2d-cross-version-smoke) below.
- ~~**Phase 18 multi-overload AA-script user-click smoke**~~ — ✅ closed 2026-05-07 against Farthest Frontier `ItemBundle.AddItems` (2 overloads).

Genuine open items:

- ~~**Phase 22 — `.CT` embedding + AA-script generator UI tab**~~ ✅ **Shipped 2026-05-12 across 22a–22f, 6 commits.** Pivoted from "offline `.CT` builder" to "live CT population via AOBMaker plugin" after auditing the plugin's existing pipe protocol. Tools menu "Inject Discrete Bridge" + per-feature Send-to-CE buttons live; 148/148 tests pass; AOT clean. Live click-through smoke against CE 7.6 + AOBMaker + a Unity game still pending (see [`docs/ce-bridge-integration.md` § "Manual smoke checklist"](ce-bridge-integration.md)).
- **Phase 20 — Scene / GameObject explorer** (parked at 20a, see follow-ups) — **20a journey across one session 2026-05-08 on SaGa F2R**: shipped Lua bridge, then iteratively learnt FOUR cliffs the hard way: (1) wire envelope `{id, result}` unwrap missed; (2) sustained Unity-icall bursts freeze the game (Phase 19 v1 worker-thread limitation); (3) `findInstances UnityEngine.GameObject` returns IL2CPP RGCTX metadata records that fool both Lua filters AND `whatIsAt`; (4) IL2CPP code stripping removes most enumeration APIs — `Resources.FindObjectsOfTypeAll`, `Object.FindObjectsOfTypeAll`, single-arg `Object.FindObjectsOfType`, `SceneManager.get_sceneCount`, etc. all stripped on SaGa F2R's shipped build. Survivors: `Object.FindObjectsOfType(Type, bool)`, `GameObject.Find(string)`, `Camera.get_main()`, `SceneManager.GetActiveScene()` + `Scene.GetRootGameObjects()`. Recipe pivoted to the 2-arg `FindObjectsOfType` survivor — **untested at session end**. Three new sub-phases opened: **20a-2** (verify the new recipe end-to-end), **20-MT** (DLL-side main-thread marshaling for callMethod-heavy recipes), **20-HEAP** (DLL-side libgc heap walker as the strip-resistant fallback). Three new lessons in [lessons-learned.md](lessons-learned.md): IL2CPP stripping detection, signatureMatch close-paren trap, plus updates to the existing findInstances-false-positives entry. Full design in [plan.md §Phase 20](plan.md#phase-20--scene--gameobject-explorer--objectname-search).
- **Phase 21 — Inspector field write-back** (planned, ~½ day + tests) — closes the read/write asymmetry. New wire RPC `setInstanceField(addr, fieldOffset, type, value)`, SEH-protected `SafeWriteBytes` on both backends, UI Inspector cells go editable with Apply button. v1 covers primitives + raw address; reference-type writes (string / object pointer) deferred to v2 with its own GC-root marshaling. Full design in [plan.md §Phase 21](plan.md#phase-21--inspector-field-write-back).
- ~~**v24 integer-decode bug**~~ ✅ **Fixed + live-verified 2026-05-08** (commit `cb3d100`, build #251 against FF VI / Unity 2019.4 / v24) — added per-version dispatch hooked off `mp.version()`. v24/v27 → raw LE branch; v29+ → existing sentinel + compressed-int branch. Both branches emit `encoding: "raw_le"` / `"sentinel_compressed"` in the response. R4/R8/Bool/Char unaffected (always raw LE). Verified: `Int32.MaxValue` → 2147483647 (was -2147483648), `Int32.MinValue` → -2147483648 (was 0), both with `enc=4B` confirming the raw LE branch fired.
- ~~**Unity 2018.x missing IL2CPP exports**~~ ✅ **Fixed + live-verified 2026-05-08** (commit `587e794`, build #251 against R-Type Dimensions EX / Unity 2018.1 / v24) — `il2cpp_image_get_class_count` + `il2cpp_image_get_class` demoted from REQUIRED to OPTIONAL in `IL2CPPResolver`. `EnumerateClasses` now has a fallback path that walks `MetadataParser::GetTypeDefinition()` and calls `il2cpp_class_from_name(image, ns, name)` per type — null returns implicitly filter by image. Cost: O(N_global_typedefs) per call, slower than the native iterator but functional. **Verified**: `assemblies_count=36` (was 0), `findInstances UnityEngine.GameObject` returned 100 matches with `class_ptr` populated, scan took only 529 ms — the fallback enumeration is fast enough for production use, not a slow-path warning.
- ~~**`findInstances` IL2CPP `classPtr` empty**~~ ✅ **Fixed + live-verified 2026-05-08** (commit `3e519a8`, build #251 against Hollow Knight Silksong / Mono / Unity 6000.0) — was actually a stale STATUS note; IL2CPP did populate `class_ptr`. Real issue was Mono returning only `vtable` while consumers expected `class_ptr`. Fix: Mono findInstances now also returns `class_ptr` (additive, keeps `vtable` for compat). Verified: Silksong run printed `class_ptr : 0x000002201761E3D0` (no fallback to `vtable`), 100 Transform instances. Both backends now expose the canonical field; pipe-protocol.md updated to spell out "scan target = `vtable` on Mono, `class_ptr` on IL2CPP".
- ~~**JSON Inf/NaN value coercion**~~ ✅ **Fixed + live-verified 2026-05-08** (commit `fd428d2`, build #251 against SaGa F2R / IL2CPP / v29) — R4/R8 `il2cppGetMetadataTypeFields` decode now emits `value_kind`: `"normal"` / `"infinity"` / `"-infinity"` / `"nan"`. `value` stays `null` for non-finite (JSON spec compliance), `value_kind` recovers the semantic. Older clients ignoring `value_kind` see the same behaviour as before — purely additive. Verified: `System.Single.PositiveInfinity` → `value:null, value_kind:"infinity"`; `NegativeInfinity` → `"-infinity"`; `NaN` → `"nan"`; finite floats (MinValue/MaxValue/Epsilon/NegativeZero) all stamp `"normal"`.
- **`findInstances` heap scan includes thread-stack + IL2CPP RGCTX-metadata false positives** (~½ day fix in DLL) — surfaced 2026-05-08 via Phase 20a. Heuristic is "first qword == class_ptr" across all RW memory regions; stale stack-frame local variables of type `GameObject*` satisfy this AND IL2CPP RGCTX cache entries legitimately store class pointers at +0x00 + +0x18. Lua-side workaround in `unity_scene.lua:_isValidInstance` (two-layer + DEPRECATED). Proper fix: restrict `findInstances` to the IL2CPP managed heap range (queryable from libgc / `il2cpp_gc_get_heap_section_count`) — would also unblock 20-HEAP. Logged in [lessons-learned.md](lessons-learned.md).
- **v27 verification target** — no Unity 2020 IL2CPP game in matrix to confirm the v24 bug fix also works on v27 (very likely shares the raw-LE encoding). 5–10 min if a target appears.
- **v32+ metadata.dat parser** (2-3 day project) — only blocks `il2cppGetMetadataTypeFields` const-decode + offline `metadata-cli` on v32+ targets. Runtime path on v39 already works (9/9 sweep). Defer until a user needs offline analysis or const decoding on Unity 6.1+. Full design in [il2cpp-metadata-crosscheck.md §"v35-v39 work — production reality"](il2cpp-metadata-crosscheck.md).
- **Phase 17e — Strategy B address finder** — only-if-asked. Cached heap walk + interval tree for batch CE pointer-scan lookups. Skip until requested.
- **More automated test coverage** — ✅ Inspector classifier + HighlightSegmenter shipped 2026-05-08 (build #257, 5 → 57 tests). Big gaps still open: end-to-end `MetadataParser` round-trips per version, CE-XML live-mode rendering against a deterministic fixture, AobMakerBridge happy + sad-path with a fake pipe server. Drip-feed.
- ~~**Highlight matching substring**~~ ✅ **Shipped 2026-05-08** (build #257) — new `HighlightedTextBlock` control + `HighlightSegmenter` pure-logic helper. Wired into Assemblies / Classes / Fields (Name + Type) / Methods (Name) lists. Element-name binding from each filter `TextBox` so the live filter value flows into every row without VM plumbing. Inspector field grid (which has no filter — drives off `walkInstance` results) deliberately untouched.
- **CSX live mode** — design dead-end as scoped (CSX is offset-relative; "live" only makes sense as snapshot-derived layout). CE XML already provides true absolute-address live mode. Defer until a user explicitly asks.

---

## Suggested first steps for the new session

Pick whichever fits the slot of time you have. Nothing on this list is blocking.

| Option | Effort | Why pick this |
|---|---|---|
| **Phase 22 live-smoke — finish Steps 9-11** | ~20 min | Steps 1-8 verified live 2026-05-18 against BongoCat (Mono); 6 P0/P1 bugs were caught + fixed mid-session. Steps 9-11 (MethodCall→CE / AddressLookup→CE / save+reopen) remain. Recipe: launch BongoCat → DiscreteUI Pick & Inject → CE+AOBMaker → Tools→Inject Discrete Bridge → enable Load Discrete Bridge → continue from §"Phase E" in [`docs/ce-bridge-integration.md`](ce-bridge-integration.md) — specifically the 3 remaining table rows. |
| **`findInstances` heap-scan false positives — restrict to managed heap range** | ~½ day | Highest CP-value of the open ½-day items. Heap scan today walks every RW page → matches stale stack-frame `GameObject*` locals AND IL2CPP RGCTX cache entries (legit class-pointer storage). Lua-side workaround (`_isValidInstance` two-layer filter in `unity_scene.lua`) is DEPRECATED. Proper fix: restrict scan to the IL2CPP managed heap via `il2cpp_gc_get_heap_section_count` / Mono `mono_gc_*` equivalents. Also unblocks Phase 20-HEAP. Affects Phase 20a / `whatIsAt` / every Inspector instance pick. |
| **Phase 20a-2 — verify FindObjectsOfType recipe** | ~30 min | Quick verification of the 2-arg recipe pivot at session end 2026-05-08. If it works, Phase 20a closes; if not, opens Phase 20-HEAP (DLL-side libgc walker). |
| **Phase 21 — field write-back** | ~½ day | Real capability gap: today the Inspector only reads. New wire RPC + SEH-protected write + editable UI cells. Full design in [plan.md §Phase 21](plan.md#phase-21--inspector-field-write-back). |
| **Phase 19d-3 — smart-typed editors** | <½ day | `NumericUpDown` for sized ints (range-checked at edit time, not post-Call), `ComboBox` for enum params via `getEnumValues`. Most natural to ship together with Phase 22's AA-script generator. |
| **Phase 12d (32-bit Mono)** | 3-5 days | User has 32-bit Mono test target (confirmed 2026-05-08) — engineering effort only. Parallel x86 build config in CMake, x86 backend DLL with Mono 32-bit struct offsets, x86 proxy DLLs, cross-bitness inject, x86/x64 selector in installer. |
| **v32+ metadata.dat parser** | 2–3 days | Only blocks const-decode + offline `metadata-cli` on Unity 6.1+. v39 runtime already works. Worth it if anyone asks for offline v32+ analysis. Full design ready in [il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md). |

### Re-verify on the new PC (sanity check before continuing)

Once the repo is checked out + the dev VS DevShell is wired, before starting any new feature work it's worth confirming the new PC's environment doesn't drift from the dev box's:

```powershell
# 1. Build everything (Publish + AOT + tests, ~2 min on a warm cache)
.\build.ps1 -Mode Publish

# 2. Quick handshake against any one target (BongoCat / Silksong easiest because no per-target metadata caveats):
.\tools\List-UnityProcs.ps1
.\tools\Test-Handshake.ps1 -ProcessId <PID>

# 3. (Optional) Re-run the 4-fix verification recipes — see the entries in §"Open follow-ups"
#    against the 4 verified targets (Silksong / SaGa F2R / FF VI / R-Type) if you have any
#    of them installed on the new PC.
```

Acceptance: build #258+ rolls clean (0/0 trim+AOT warnings), `57/57 tests pass`, `Test-Handshake` shows `backend=…, build=258+, unity_version=…` against a live target.

### Live verifications (carried forward — still green)

These remain confirmed clicked-through on the dev box:

- ✅ Phase 9 — `MainGame/PLY_WORK` decode matches CE reference table value-for-value (SaGa F2R, IL2CPP).
- ✅ Phase 11 — `unity_dissect.lua` builds a 56-field Structure Dissect entry for `MainGame/PLY_WORK`; `unity_symbols.lua` registers 10 statics + 323 method JITs on Silksong's `GameManager`.
- ✅ Phase 14c — IL2CPP metadata v24/v27/v29/v31 dispatch verified against six games covering all four versions.
- ✅ Phase 17 — `whatIsAt` round-trips against SaGa F2R `PLY_WORK.HP` field address; lookup latency sub-ms after first cache build (~tens of ms).
- ✅ Phase 18 — single-overload AA-script generation against SaGa F2R `MainGame/PLY_WORK.AddGrowHpMax(int)`. AOBMaker `CreateAAScript` direct push.
- ✅ Phase 18 multi-overload — Farthest Frontier `ItemBundle.AddItems` (2 overloads: `(System.UInt32, System.UInt32)` + `(ItemBundle)`) — both AOB patterns scan-clean from CE, both `registersymbol` succeed, addresses resolve in `GameAssembly.dll+0xB20900` / `+0xB20980`. The two-symbol `[ENABLE]` block enables idempotent re-runs. Verified 2026-05-07.
- ✅ **Phase 19a/b/c** — both backends, three games. SaGa F2R (IL2CPP) + BongoCat (Mono) + Farthest Frontier (both — instance dispatch via `ItemBundle.get_numberOfItems()`). PowerShell smoke (`Test-CallMethod.ps1`) covers Mathf.Abs(int/float) + Convert.ToByte(int) + Int32.Parse(string) + String.Concat(string,string) + Instance + Custom scenarios.
- ✅ **Phase 19d** — UI MethodCallWindow user-tested via the Methods grid context menu.
- ✅ **Phase 19e** — `unity_invoke.lua` dofile + 4 scenarios green from CE Lua console on **both backends**: IL2CPP / SaGa F2R + Mono / BongoCat (2026-05-07). Mono-side run of the BongoCat recipe: `Math.Abs(-7) → 7` (0 ms), `String.Concat("Hello, ", "World") → "Hello, World"` (0 ms), `Convert.ToByte(-1) → System.OverflowException` (host stayed alive, message decoded from `MonoException + 0x18`), `prettyPrint(r)` round-trip clean. Wire layer was already byte-identical via the 19b PowerShell smoke; this confirms the Lua wrapper side too.
- ✅ **Phase 19d-2** — User click-through green on both UX paths: auto-skip on single-overload methods jumps straight to `MethodCallWindow` (no picker shown); Inspector cross-tab right-click → switches to Methods tab → subsequent Methods-grid right-click pre-fills the `Instance address` textbox from the inspector's selected instance. Build: 38.3 MB AOT, 0/0 trim/AOT warnings, 5/5 xUnit pass (build #234).
- ✅ **Phase 5 segment 2d cross-version sweep** (2026-05-07, build #235-236) — Registration scan: ✅ on Unity 2018.1 (R-Type / v24, types=19874), Unity 2019.4 (FF VI / v24), Unity 2021.3 (SaGa F2R / v29, baseline), Unity 2022.3 (Farthest Frontier / v31), Unity 6.1 (Songs of Silence / v39, types=144837). FieldDefault decode: ✅ R4/R8 across all versions, ✅ I*/U* on v29 / v31, ⚠️ I*/U* broken on v24 (v24 metadata uses raw LE; decoder assumes CLI compressed-int + sentinels). v32+ blocked at `MetadataParser` `version > 31` reject (by design).
- ✅ **Phase 5 segment 2d v39 runtime sweep** (2026-05-07, build #235) — `Test-V39RuntimeSweep.ps1` 9/9 paths green against Songs of Silence: handshake, `il2cppGetMetadataRegistration`, `getAssemblies` (253 assemblies), `getClasses UnityEngine.CoreModule` (1093 classes), `getClassInfo UnityEngine.GameObject` (143 methods, parent walk OK), `findInstances` (8/8 matches), `walkInstance` (decoded `m_CachedPtr +0x10`), `findMethodInstances UnityEngine.Mathf.Abs` (2 overloads with addresses + signatures), `callMethod Mathf.Abs(int:-7) → 7`. **v39 is first-class supported** for Inspector / browser / Phase 17/18/19 / Lua bridges; only `il2cppGetMetadataTypeFields` const decode + offline `metadata-cli` remain gated.

### Pending verification (small)

_(none — every shipped phase has a live click-through on the dev box. New work surfaced by the cross-version sweep is captured in "Open follow-ups" with fix sketches.)_

### Phase 5 segment 2d cross-version smoke

The `Il2CppMetadataRegistration` discovery uses a structural-shape scan (not a literal-byte AOB) — see [plan.md §Phase 5 "2d follow-up"](plan.md) for the full algorithm. Should work version-agnostically, but only v29 is formally validated. Quick recipe to cover the remaining versions:

**Pre-flight** (per target):

1. Inject `UnityDumper-IL2CPP.dll` into the game (DiscreteUI Pick & Inject or `Test-Handshake.ps1`). Confirm `handshake` reports `metadata_version`.
2. Identify a class with at least one **non-enum** field that has a `const` literal initializer — pretty much any user-defined class with `private const int kFoo = 42;` will do. Generic candidates: `UnityEngine.Mathf` (PI / Deg2Rad / Epsilon), `System.Math` (PI / E), or any game-specific class with tunable constants.

**Run the smoke**:

```powershell
.\tools\Test-MetadataFields.ps1 -Namespace UnityEngine -Name Mathf
```

**Pass criteria**:

- Banner near the top reads `Il2CppMetadataRegistration: YES` (green).
- For each field with a `default`, the line reads `tag=0x.. = <value>  enc=NB` in cyan — NOT just `hex16 = ... raw_size=N data#X` alone.
- `value` is sane for the type (e.g. `Mathf.PI` → `3.141593`, `Mathf.Deg2Rad` → `0.0174533`).
- No process crash; no near-miss spam in `%LOCALAPPDATA%\UnityCEDumper\Logs\<pid>\init\`.

**Targets to cover**:

| Metadata version | Target | Status |
|---|---|---|
| v24 | FINAL FANTASY VI (Unity 2019.4.26f1) | ⚠️ Partial 2026-05-07 — registration scan works, R4/R8 decode works, but I*/U* decoder is wrong (see "v24 integer-decode bug" below) |
| v24 | R-Type Dimensions EX (Unity 2018.1.9f2) | ⚠️ Runtime resolver bails (Unity 2018.1 IL2CPP missing two required exports — see "Unity 2018.x IL2CPP missing-exports" below); registration scan still works (`types_count=19874`) |
| v27 | (any Unity 2020 game in the test matrix) | ⏳ |
| v29 | SaGa F2R | ✅ verified |
| v31 | Farthest Frontier (Unity 2022.3.62f3) | ✅ verified 2026-05-07 |
| v39 | Songs of Silence (Unity 6000.3.8f1) | ✅ Runtime path 9/9 green (handshake / domain walk / class enum / getClassInfo / findInstances / walkInstance / Phase 18 findMethodInstances / Phase 19 callMethod); only `il2cppGetMetadataTypeFields` const-literal path + offline `metadata-cli` blocked behind `version > 31`. See "v39 — first-class supported" subsection below. |

**v31 verification details** (2026-05-07, build #235):

- Banner: `Il2CppMetadataRegistration: YES` ✅
- `System.Int32.MaxValue` → tag 0x08 (I4), value 2147483647, enc 1B (sentinel `0xFE` = type_max, decoder correctly bounded to int max)
- `System.Int32.MinValue` → tag 0x08 (I4), value -2147483648, enc 1B (sentinel `0xFF` = type_min)
- `System.Single.MinValue` / `Epsilon` / `MaxValue` / `NegativeZero` → tag 0x0C (R4), correct float values, enc 4B
- `System.Single.PositiveInfinity` / `NegativeInfinity` / `NaN` → tag 0x0C identified, decoder ran, but wire `value` came back null (see "JSON Inf/NaN limitation" below).

**Known non-blocking issue surfaced during v31 verification**: JSON spec doesn't allow `Infinity` / `-Infinity` / `NaN` as numeric literals. The R4 decoder code path runs and sets `enc_bytes=4`, but `nlohmann/json` serializes the float value as `null` for the three special cases. The smoke script reports "(no decode)" when value is null. Tag identification still works. Fix would be either (a) emit special-case strings (`"infinity"` / `"nan"`) for these values, or (b) add a flag like `"value_kind": "infinity"` alongside a null `value`. Defer until a user actually needs to read these specific constants.

### v24 integer-decode bug (surfaced 2026-05-07 against FF VI)

The v24 cross-version smoke uncovered a real bug in the integer FieldDefault decoder. Banner / structural-shape scan / R4 / R8 paths all work on v24 — but the I*/U* family (Bool / Char / I1-I8 / U1-U8) decode wrong values.

**Root cause**: the I*/U* decoder in `il2cppGetMetadataTypeFields` (ExportAPI.cpp:1860-1875 area) assumes **CLI compressed-int with sentinel encoding** (`0xFE` = type_max, `0xFF` = type_min, else compressed-signed). That's correct for v29 / v31. v24 IL2CPP code-gen instead emits **raw little-endian bytes** for integer constants — same convention floats already use unconditionally.

**Concrete reproduction**:

| Field (v24, FF VI) | raw_hex first 4 bytes | LE-int32 reading | Should be | Decoder returns |
|---|---|---|---|---|
| `System.Int32.MaxValue` | `FFFFFF7F` | `0x7FFFFFFF` = 2147483647 ✅ | 2147483647 | **-2147483648** ❌ (saw `0xFF` first byte → type_min sentinel) |
| `System.Int32.MinValue` | `00000080` | `0x80000000` = -2147483648 ✅ | -2147483648 | **0** ❌ (saw `0x00` → 1-byte CLI compressed-int = 0) |

R4 / R8 are unaffected because their decoder is unconditional `memcpy(p, 4 or 8)`.

**Fix options** (none implemented yet):

1. **Per-version dispatch** — version-aware decoder, raw LE for v24 / v27, sentinel + compressed-int for v29+.
2. **Auto-detect** — if the data slot is exactly N bytes for a fixed-N type (1 for I1, 4 for I4, 8 for I8) AND the value would otherwise be a sentinel result, prefer raw LE. Brittle when the actual value really IS the sentinel (`int.MaxValue` is the legitimate max).
3. **Feature flag at the registration-find level** — `IL2CPPMetadataRegistration::IsLikelyV24Encoding()` heuristic, reported as `encoding_format` in the wire response, smoke / consumers branch on it.

Option 1 is cleanest. Estimated ½-day work: hook the encoding choice off `MetadataParser::version()` (already known at the site), branch the I*/U* decoder, add v24/v27 unit tests. Defer until a user explicitly needs `getMetadataTypeFields` const-int values on v24 / v27.

**Workaround in the meantime**: read `raw_hex_first16` and decode manually for v24 / v27. The Inspector and class browser don't depend on this path — only `il2cppGetMetadataTypeFields`'s typed-value field does.

### v39 — first-class supported, except for two metadata.dat-only paths (verified 2026-05-07 against Songs of Silence)

Songs of Silence (Unity 6000.3.8f1) is the first **v32+ metadata target** to land in the test matrix. Initial sweep found the structural-shape `Il2CppMetadataRegistration` AOB scan working cleanly:

```json
{ "found": true,
  "address": "0x00007FFE9C13D830",
  "types_array": "0x00007FFE9C22A1C0",
  "types_count": 144837 }
```

Follow-up sweep ([Test-V39RuntimeSweep.ps1](../tools/Test-V39RuntimeSweep.ps1)) extended that to the full runtime API surface — **9 / 9 paths green**:

| Path | Result |
|---|---|
| `handshake` / `il2cppGetMetadataRegistration` / `getAssemblies` / `getClasses` | ✅ |
| `getClassInfo UnityEngine.GameObject` (143 methods, parent=Object, inheritance walk) | ✅ |
| `findInstances UnityEngine.GameObject` (8 / 8 matches, class-pointer match works) | ✅ |
| `walkInstance` first GameObject (decoded `m_CachedPtr` at +0x10) | ✅ |
| `findMethodInstances UnityEngine.Mathf.Abs` (2 overloads, both with AOT addresses + decoded signatures) | ✅ Phase 18 |
| `callMethod Mathf.Abs(int:-7) → 7` | ✅ Phase 19 (marshal + invoke + return decode) |

**Effective conclusion: v39 is first-class supported for ~95 % of typical usage** — Inspector, class browser, Phase 17 (`whatIsAt`), Phase 18 (overload + AOB), Phase 19 (`callMethod`), and the CE Lua bridges all work without any v32+ specific code. The runtime APIs (`il2cpp_image_get_class`, `il2cpp_class_get_fields`, `il2cpp_method_get_pointer` fallback, `il2cpp_runtime_invoke`) didn't shift between v31 and v39 in any way that affects our consumers.

**Two paths still gated behind unimplemented v32+ metadata.dat parsing**:
1. `il2cppGetMetadataTypeFields` — const literal decoding via `MetadataParser`. Affects only the typed-FieldDefault values in the field list, NOT field iteration / offset / type strings (those go through the runtime).
2. Offline [`metadata-cli`](../tools/metadata-cli/) — pure on-disk analysis. Live runtime path is an unblocked alternative for everything except diff-checking metadata across versions.

Both are bounded-scope features the average user doesn't need on a v39 game.

`MetadataParser::LoadHeader` continues to reject `version > 31` by design — see the defensive guard in [il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md). Implementing v32+ metadata-table parsing is a 2-3 day project documented there.

**Minor cosmetic finding from the sweep**: `findInstances` IL2CPP path returns empty `classPtr` field in the response (matches still come back correctly — heap scan finds them, just the response builder doesn't populate the per-class pointer field for IL2CPP). Not v32+ specific; low-priority backlog item.

### Unity 2018.x IL2CPP missing-exports (surfaced 2026-05-07 against R-Type Dimensions EX)

A second v24 target — R-Type Dimensions EX (Unity 2018.1.9f2) — exposed a different cliff: **Unity 2018.x IL2CPP runtime is missing two exports we currently mark REQUIRED**:

- `il2cpp_image_get_class_count(image) → size_t`
- `il2cpp_image_get_class(image, idx) → Il2CppClass*`

`IL2CPPResolver` log: `required 16/18, optional 23/25 (resolved=no)`. The resolver's `resolved=no` flag gates every runtime RPC — `getAssemblies` / `getClasses` / `getClassInfo` / `findInstances` / `walkInstance` / `findMethodInstances` / `callMethod` all return `il2cpp runtime not resolved`.

**Important nuance**: this is a **Unity-version**-sensitive issue, not a metadata-version one. FF VI is also v24 metadata but Unity 2019.4 — its runtime exports both APIs. So:

| | metadata version | Runtime exports OK? | Outcome |
|---|---|---|---|
| Unity 2018.1 (R-Type) | v24 | ❌ missing 2 required | Resolver bails, runtime path unusable |
| Unity 2019.4 (FF VI) | v24 | ✅ | Runtime path works; only the v24-metadata-format integer decoder bug remains |

The two missing exports were added between Unity 2018.x and 2019.x. Most Unity 2018.x games used Mono rather than IL2CPP for Windows (IL2CPP for Windows didn't become common until 2019+) — so impact is limited to the small subset of "early IL2CPP Windows ports" like R-Type Dimensions EX (2014-era port).

**Important confirmation**: the structural-shape `Il2CppMetadataRegistration` AOB scan **still works** on Unity 2018.1 (`il2cppGetMetadataRegistration` returns `found=YES, types_count=19874`) — the discovery heuristic is genuinely **version-portable from Unity 2018.1 through Unity 6.1** (the full span of our test matrix).

**Fix sketch** (~2 hr, deferred):

1. Demote the two missing exports from REQUIRED → OPTIONAL in `IL2CPPResolver`.
2. Add a fallback in `IL2CPPClassWalker::EnumerateClasses` that, when those two exports are unbound, iterates `MetadataParser`'s `TypeDefinitions` table (filtered by image) and calls `il2cpp_class_from_name(image, ns, name)` for each — this older API has been exported since Unity 2017. Slower but functional.
3. Same pattern for any other "image-based class enumeration" call site.

Defer until a user reports needing Unity 2018 IL2CPP support. Today the workaround is "use a Unity 2019.4+ test target".

If `Il2CppMetadataRegistration: no` shows up on any version, the registration's PE-section layout or struct shape changed between versions — would need a per-version layout dispatch in `IL2CPPMetadataRegistration.cpp` (currently hardcoded `kRegBytes = 0x80` for v29). The init log will have `MetadataRegistration: near-miss @0x.. typesCount=N: <reason>` lines pointing at what failed the heuristic.

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
│   ├── lua-invoke.md           <- Phase 19 design spec
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
│   ├── src/mono/               <- MonoResolver/Domain/ClassWalker/AddressFinder/MethodFinder/MethodInvoker/...
│   └── src/il2cpp/             <- IL2CPPResolver/Domain/ClassWalker/AddressFinder/MethodFinder/MethodInvoker/...
├── ui/
│   ├── UnityDumpUI/            <- Avalonia 12 app (AOT). AssemblyName=DiscreteUI. Win-only stack: Avalonia.Win32 + Avalonia.Skia + Avalonia.HarfBuzz.
│   │   ├── ViewModels/
│   │   │   ├── MainWindowViewModel.cs
│   │   │   ├── AssemblyBrowserViewModel.cs
│   │   │   ├── LiveInspectionViewModel.cs
│   │   │   ├── AddressLookupViewModel.cs       <- Phase 17c
│   │   │   ├── MethodOverloadsViewModel.cs     <- Phase 18c
│   │   │   ├── MethodCallViewModel.cs          <- Phase 19d
│   │   │   ├── ProcessPickerViewModel.cs
│   │   │   └── ProxyDeployViewModel.cs
│   │   ├── Views/
│   │   │   ├── MainWindow.axaml + .cs
│   │   │   ├── ProcessPickerWindow.axaml + .cs
│   │   │   ├── ProxyDeployWindow.axaml + .cs
│   │   │   ├── MethodOverloadsWindow.axaml + .cs <- Phase 18c
│   │   │   └── MethodCallWindow.axaml + .cs    <- Phase 19d
│   │   └── Resources/Strings/en.axaml
│   ├── UnityDumpUI.Core/       <- Protocol + services (AOT-safe)
│   │   └── Services/
│   │       ├── PipeClient.cs / BackendAdapter.cs / ProtocolModels.cs / ProtocolJson.cs
│   │       ├── AobMakerBridge.cs               <- 3-verb pipe client (Nav + CreateAAScript)
│   │       ├── CallArgFactory.cs               <- Phase 19d (public seam over internal JsonContext)
│   │       └── Export/
│   │           ├── UnityTypeMap.cs
│   │           ├── SdkHeaderExporter.cs / CsxExporter.cs / CeXmlExporter.cs
│   │           └── AobAaScriptGenerator.cs     <- Phase 18c
│   └── UnityDumpUI.Tests/       <- xUnit v3
├── tools/                       <- PowerShell pipe helpers + Phase 13 metadata-cli
│   ├── metadata-cli/            <- Phase 13 offline metadata parser
│   ├── Install-CEScripts.ps1    <- glob-driven deploy to <CE>/discrete/
│   ├── Test-CallMethod.ps1      <- Phase 19 smoke
│   ├── Test-FindMethodInstances.ps1 <- Phase 18 smoke
│   ├── Test-AobPattern.ps1      <- Phase 18a-3 smoke
│   ├── Test-WhatIsAt.ps1        <- Phase 17 smoke
│   └── Test-*.ps1
└── scripts/                     <- CE Lua bridge (deployed to <CE>/discrete/ via build.cmd deploy)
    ├── README.md
    ├── unity_dump.lua / unity_dissect.lua / unity_symbols.lua
    ├── unity_addrfind.lua                      <- Phase 17d
    ├── unity_invoke.lua                        <- Phase 19e
    └── utils.lua
```

---

## Known caveats / things to watch (carried forward + Phase 19 additions)

### Build / DLL
- **`-DBACKEND=` cmake cache poisoning**: cmake hates being switched between `Mono` / `IL2CPP` / `Both` in the same build dir. `build.ps1` wipes the per-backend build dir on every configure for this reason.
- **Game must be closed** before rebuilding the DLL — LNK1104 if the DLL is still mapped from a previous injection.
- **Phase 18 AOB decoder bails on unknown opcode** — falls through with literal bytes (no wildcards). The AOB stays valid, just less version-stable. By design — false positives (over-wildcarding) cause symbol ambiguity, which is the failure mode we must avoid. Bailout is reported via `decoderBailed: true, bailedAt: N` in the wire response.
- **IL2CPP `method_get_pointer`** is not exported in Unity 2021.3+. We read `MethodInfo.methodPointer` directly via SEH-protected struct read. Don't reintroduce the export-only path.

### Phase 19 specific
- **`runtime_invoke` non-uniform marshaling** — value types pass `&value`, reference types pass the pointer **directly** in `params[i]`. Matches BepInEx / MelonLoader / Il2CppInterop. Mono's API docs describe a "uniform" `&pointer` convention for ref types — that's wrong for the actual x64 trampoline. Both backends use the non-uniform form. See `MarshalArg` in `IL2CPPMethodInvoker.cpp` / `MonoMethodInvoker.cpp` for the pattern.
- **`signatureMatch` substring trap** — `"Int32"` is a substring of `"UInt32"`. Default to parens-bounded `"(System.Int32)"` for guaranteed uniqueness when calling `callMethod` / `findMethodInstances`. The smoke scripts and `unity_invoke.lua` examples all use the parens form for this reason.
- **GC-root caveat for `callMethod` returns** — a managed pointer returned by `callMethod` is valid for the next call you make right now. A GC that ran between the address read and the next invoke can have moved the object (Mono moving GC; IL2CPP largely non-moving but not guaranteed). Best practice: chain `findInstances` → `callMethod` in one Lua block when correctness matters.
- **PowerShell `ConvertTo-Json` single-element-array unwrap** — when writing a `Test-*.ps1` smoke script that nests an array as a property value, `[pscustomobject]@{ args = @($oneObj) }` flattens the array to a bare object. Splice the args JSON in as raw text (see `tools/Test-CallMethod.ps1` for the pattern).

### Avalonia / UI
- **Avalonia 12 Windows-only build needs all three pieces wired** — `Avalonia.Win32` + `Avalonia.Skia` + `Avalonia.HarfBuzz` packages, `.UseWin32().UseSkia().UseHarfBuzz()` in `Program.cs`. Missing `.UseHarfBuzz()` throws `InvalidOperationException: No text shaping system configured` from `AppBuilder.Setup()` BEFORE any logging starts — process exits with `0xC0000409`, no log file, no window. Missing `.UseSkia()` produces a silent black window.
- **AOT analyzers are strict.** Any new JSON serialization path must go through the source-gen `ProtocolJsonContext`. Reflection-based `JsonSerializer.SerializeToElement<T>(value)` trips IL2026 + IL3050. Pattern to copy for in-Core code: the `Str(string)` / `Int(int)` helpers in `BackendAdapter.cs`. For UI ViewModels (which can't reach the internal context), go through `CallArgFactory` (Phase 19d pattern).
- **`LibraryImport` does NOT auto-resolve A/W**. Unlike classic `DllImport` with `CharSet`, every entry point name is taken literally. Use explicit `EntryPoint = "FooW"` for any function that has A/W variants.
- **Avalonia 12 clipboard API churn**: only supported writer is `IClipboard.SetDataAsync(IAsyncDataTransfer)` — wrap a `DataFormat.Text` item in a `DataTransfer`.
- **Avalonia 12 `avares://` keys off AssemblyName, not RootNamespace.** Renamed the exe? Update `App.axaml`'s `ResourceInclude Source="avares://{AssemblyName}/..."` too.
- **Avalonia 12 `DataGridCheckBoxColumn`** requires entering edit mode (double-click) before the checkbox responds. Use `DataGridTemplateColumn` with an inline `<CheckBox Focusable="False">` for single-click toggles.
- **`CommunityToolkit.Mvvm` source-gen name collision**: a property named `Backend` collides with the generated field name. Use `Backend2`.
- **`CommunityToolkit.Mvvm` and child-collection CanExecute**: a command's CanExecute is NOT auto-re-evaluated when an item inside an `ObservableCollection<T>` raises PropertyChanged. Subscribe to each row's `PropertyChanged` and call `Notify*Changed()` manually. Pattern: `MethodOverloadsViewModel.OnRowPropertyChanged`.

### Misc
- **CJK Windows console** munges em-dashes printed by the script unless we route through `Start-Transcript`. `build.cmd` already does.
- **Antivirus** flags freshly-built `DiscreteUI.exe` (lots of `LibraryImport` native calls + remote thread injection P/Invokes). User has `dist/` excluded; mention this if a fresh dev hits a quarantine error after `build.cmd publish`.
- **`PipeClient` write+read runs uncancellable past commit.** Don't "fix" this back to passing `ct` everywhere — the race it solves (id-mismatch when caller cancels mid-request) is not theoretical, it broke rapid Find Instances clicks.

---

## Quick reference

```powershell
# Full clean build (DLL + UI AOT + tests) -> dist/
.\build.cmd publish clean

# UI only, fast iteration (self-contained single-file in dist\)
.\build.cmd ui

# IL2CPP / Mono DLL only
.\build.cmd il2cpp
.\build.cmd mono

# Tests
.\build.cmd test

# Deploy bridge scripts + DLLs to <CE>\discrete\
.\build.cmd deploy            # post-build, copies from dist/
.\build.cmd deploy-scripts    # live iteration on Lua, copies from scripts/

# Phase 19 smoke (after injecting via Test-Handshake.ps1):
.\tools\Test-CallMethod.ps1                    # Mathf.Abs(-7) -> 7
.\tools\Test-CallMethod.ps1 -Scenario Throw    # Convert.ToByte(-1) -> OverflowException
.\tools\Test-CallMethod.ps1 -Scenario Float    # Mathf.Abs(-3.5) -> 3.5
.\tools\Test-CallMethod.ps1 -Scenario StringConcat
.\tools\Test-CallMethod.ps1 -Scenario StringParse
.\tools\Test-CallMethod.ps1 -Scenario StringThrow
.\tools\Test-CallMethod.ps1 -Scenario Instance -ClassAssembly <asm> -Name <Cls> -Method ToString -InstanceAddress 0x...

# Phase 18 smoke:
.\tools\Test-FindMethodInstances.ps1 -ClassAssembly <asm> -Namespace <ns> -Name <Cls> -Method <m>
.\tools\Test-AobPattern.ps1 -Address <hex>

# Phase 17 smoke:
.\tools\Test-WhatIsAt.ps1 -ProcessId <pid>
```

Pipe protocol contract: [pipe-protocol.md](pipe-protocol.md) (current: protocol v1 — includes `walkArray` / `whatIsAt` / `findMethodInstances` / `getMethodAobPattern` / `callMethod`).
PowerShell smoke tests: `tools/Test-*.ps1` — still useful for one-shot pipe RPCs without launching the UI.
CE-side workflow: [scripts/README.md](../scripts/README.md) — install layout + `dofile` invocations.
Canonical phase ledger: [plan.md](plan.md). All phase numbers, status flags, and acceptance criteria live there.
