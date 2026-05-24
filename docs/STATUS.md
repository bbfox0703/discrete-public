# UnityCEDumper — Session Hand-off

**Branch**: `dev` (2026-05-24 **session close** — build #527).

## TL;DR — what's left to pick up

**Phase 20e shipped** (build #527, 2026-05-24). `SceneEnumerator` now
probes both `(CoreModule, monolithic)` FQNs in `ResolveGameObjectTypeAsync`
and routes every downstream `CallMethodAsync` through the resolved
assembly name (Object.FindObjects, get_name, GameObject.get_transform,
Transform.get_childCount, Transform.GetChild, Component.get_gameObject).
Existing fake-pipe tests still pass under the CoreModule-first probe
order; +1 new test pins the monolithic-fallback path end-to-end. Suite
357 → **358 pass**. **Live-verify TODO**: re-test on 活俠傳 — Scene
tab should now enumerate GameObjects instead of erroring with the old
`Type.GetType … returned null` message. Log line "resolved GameObject
Type via … (downstream assembly = …)" confirms which probe survived.

**Phase 28 v1 shipped** (build #515 UI / #518 with tests). 28a Mono IL
walker + 28b IL2CPP static-field RIP scanner + 28d UI modal with 3
right-click entry points (Inspector / Symbol Finder / Browser Fields
tab) all landed clean. 28c (IL2CPP instance fields, AOB heuristic) and
28e (CE Lua bridge) deferred per the
[[project-phase-28-scope]] memory.

**Phase 28 live-smoke COMPLETE** (build #523, 2026-05-24):
* 活俠傳 (Mono x86 / Unity 2020.3) — `Mortal.Core.GameStat._value`
  → 15 exact matches: `get_Value` / `get_FinalValue` (reads) +
  `Reset` / `SetValue` (5 calls, 3 reads + 2 writes) /
  `AddValue` (3 calls, 2 reads + 1 write). Cross-checked semantically
  — matches dnSpy IL view shape.
* SaGa F2R (IL2CPP v29 / Unity 2021.3) — instance fields cleanly
  surface "not supported in v1" error (28c deferred per scope
  decision). Static-field path live-tried via `SteamManager` static
  → **0 matches** returned. Could be (a) cross-assembly attribution
  drop (v1 same-assembly limit), (b) thunk / SIMD load opcode pattern
  not yet covered by the v1 matcher, or (c) the field was simply
  unreferenced in the running build. Recorded as a Phase 28 v2
  follow-up to investigate when more static-field targets show up.

(Earlier session content below — Phase 27 + READMEs + minor open
issues kept for context.)

Phase 27 (Unity-aware Value Search) shipped + live-verified end-to-end.
Five production-bug + UX follow-up commits landed on top. README rewrite
for end-user (CT maker) audience landed. Lessons documented. Repo
ready for handoff.

**Known minor open issue** (deferred — not blocking):
* Dual-monitor maximize → restore: the `OnWindowPropertyChanged` hook
  in MainWindow.axaml.cs captures pre-maximize bounds correctly, but on
  restore the window stays at maximize-bounds rather than snapping back
  to the saved pre-maximize Position+Size. Likely Avalonia 12's own
  restore-bounds path is running AFTER our hook and overriding our
  Position/Width/Height assignments. Next session: probably need
  Dispatcher.UIThread.Post(...) to defer our restore until after
  Avalonia's pass, or override OnPropertyChangedCore instead of
  subscribing to the event. Code at MainWindow.axaml.cs `OnWindowPropertyChanged`.

**Other carry-forward** (from earlier session notes — still open):
* **27c live click-through smoke** — array element resolution in
  `whatIsAt` shipped + DLL-level unit-tested; no live game smoke yet
  on a `T[]` / `List<T>` instance.
* **27b-4 live smoke** — UTF-16 / UTF-8 / ASCII string scan shipped;
  no live game smoke.
* **27d live UI click-through** — Value Search tab shipped + live-
  smoked on 活俠傳 (Mono x86) and SaGa F2R (IL2CPP); SaGa F2R confirmed
  the session refine workflow narrows correctly.
* **Phase 27f** *(deferred, v2 acceleration)* — libgc heap-section
  binding. Doubles as Phase 20-HEAP closure. Not blocking.
* **Phase 28** (Field → Methods cross-reference) — TODO stub, picks
  up after Phase 27 stabilises.

---

(Previous session-handoff narrative starts below.)

**Branch**: `dev` (2026-05-20 **late night** session — **Phase 27 fully shipped + live-verified + 3 production-bug follow-ups**. Build #452. Beyond the headline value-search stack, the live testing on 活俠傳 surfaced three perf/shutdown bugs that all landed same-night: SessionManager heap-leak + AddressFinder cache heap-leaks + reentrant ThreadAttachGuard. The reentrant-guard fix is the standout: 4000+ per-WhatIsAt thread_attach/detach cycles were leaving BDWGC finalizers queued, which ran on game shutdown and hung 活俠傳 for 44 s post-scan. Outer guard at scan-handler level + new per-thread reentrancy counter cuts 4000 cycles → 1. Verified: post-scan game-close time 44 s → ~1 s. New lesson recorded in [lessons-learned.md](lessons-learned.md): "any batch loop calling Mono / IL2CPP runtime APIs should hold ONE outer ThreadAttachGuard". Earlier same session — **Phase 27 fully shipped (27a + 27b + 27c + 27b-4 + 27d + 27e)**: complete Unity-aware value search stack. Build #433. **283/283 xUnit pass**. New artifacts:

* DLL: `scanFieldValue` (global + class-scoped + string scan), `beginValueScan` / `refineValueScan` / `endValueScan` (session lifecycle). `whatIsAt` now resolves array element indices for `T[]` / `List<T>._items`. `Mono::BuildClassRangeCache` rewritten to lazy vtable resolution (closes 32-bit Mono hang).
* UI: new "Value Search" tab (MainWindow position 6) — DataType + ScanType + Value/ValueTo + Tolerance + Encoding/MatchMode (string mode) + First Scan / Next Scan / End Session buttons + DataGrid with class/field/type/offset/current value/instance/address columns + right-click → Inspect this instance (cross-tab to Inspector via FocusOnInstance). AOT-clean.
* ~~CE Lua bridge `scripts/unity_valuescan.lua`~~ — **dropped 2026-05-20**. Originally implemented; user observation: CE already has a native value scanner, so a CE-side bridge that calls our scan back into CE has no realistic workflow. The DLL-side Begin/Refine/End RPCs remain for UI consumption; the Lua wrapper was deleted as unused surface.
* Tooling: `tools/Test-ScanFieldValue.ps1` + `tools/Test-ValueScanSession.ps1` PowerShell smoke harnesses.

**Live-smoked**: SaGa F2R (IL2CPP) one-shot + session (purse=100000 → spent 1 coin → `newValue=99999` narrowed to PLY_WORK.purse correctly, while PLY_WORK_SAVEDATA.purse stayed 100000 and got correctly dropped); 活俠傳 (Mono x86, Unity 2020.3.49f1) found `Mortal.Core.GameStat._value` reliably across UI state changes. **UI tab live click-through still pending user verification.**

The savefile-vs-active-state distinction is something CE alone can't surface — it doesn't know which class owns which address. This is the headline value of Unity-aware scan.

Earlier today same session: **Phase 27a shipped + live-smoked + production bug fixed**: `scanFieldValue` RPC works end-to-end on IL2CPP x64 (SaGa F2R), Mono x64 (BongoCat), and Mono x86 (活俠傳 / Legend of Mortal, Unity 2020.3.49f1). Mid-smoke caught one P0 hang on legacy 32-bit Mono — eager `mono_class_vtable` batch for 17K classes wedged BDWGC when game's main thread was concurrently focus-paused (alt-tab to console). Rewrote `Mono::BuildClassRangeCache` + `WhatIsAt` to lazy vtable→klass resolution: cache build does pure pointer enumeration (~500ms, zero runtime calls), backward scan derefs each candidate qword via SafeReadBytes to get klass, memoizes for subsequent hits. Build #423 verified hang-free across 2 runs at different in-game UIs on 活俠傳. Lesson recorded in [lessons-learned.md](lessons-learned.md). Also fixed build.ps1 default `-Arch x64` → `Both` (no-args `build.cmd` now rebuilds x86 + x64 together — was leaving `dist/x86/` stale) + `WindowsProcessEnumerator` no longer force-sets Backend=None for WOW64 processes (32-bit Unity targets now appear in Pick & Inject picker). Earlier today (2026-05-20 even later): **Phase 27 planning landed**: Unity-aware Value Search drafted as sub-phases 27a-27f in [plan.md](plan.md). Inspired by `D:\Github\AOBMaker`'s `WindowsValueScanner.cs` but reuses Discrete's existing `Memory::EnumerateCommittedRegions` + `whatIsAt` (Phase 17) filter + `findInstances` (Phase 6c) — no new scan engine needed. Two scan modes: **global** (CE flow) + **class-scoped** (Discrete flow, ~100x faster). Full CE filter mode set. Includes Phase 17b carry-forward — Array / List<T> element resolution in `whatIsAt`. v1 estimated ~6.5 days; v2 sub-phase 27f doubles as Phase 20-HEAP closure via libgc heap-section binding. **Phase 28** (Field → Methods cross-reference) added as TODO stub — Mono IL walk + IL2CPP RIP scan for static fields + heuristic for IL2CPP instance fields; picks up after Phase 27. **No code yet for either.** Earlier today (2026-05-20 mid/late): **Phase 26 anti-tamper protection** — 3-slot SHA256 self-check spread across Core + UI assemblies (`BuildArtifactToken` / `ExportFormatToken` / `WindowsRuntimeMetadata`), XOR-shielded footer credit, CT-export offset poisoning, and 1/11 Inspector grid value poisoning — all gated by 0xCC dev-mode bypass; on top of mid-session Phase 20-MT main-thread marshaling and Phase 25 multi-assembly Symbol Finder.)
**Last commit**: feat(security): 3-slot token guardian + Inspector grid poison _(planning-only update — no commit yet for Phase 27/28)_
**Build**: 408 (DLL sizes unchanged from build #376; `DiscreteUI.exe` AOT 39.4 MB after 3-slot guardian + TokenAggregator + InspectorPoison helper; Release single-file unchanged)
**Tests**: 283/283 pass (no new tests this session — protection layer is dev-mode-bypassed so existing snapshot tests stay byte-identical) + AOT round-trip verified end-to-end: 3 distinct sentinels in `.rdata`, inject-hash.ps1 patches all 3 with SHA256 `2A9C9F…E13B`, slot C ~11 KB away from A/B (cross-assembly blob distance).
**Current state**: **Phase 26 shipped + AOT-verified.** Three independent token guardians (slots A in Core/Services, B in Core/Services/Export, C in ui/UnityDumpUI/Platform) each self-check the running binary by zeroing ALL three slots and re-computing SHA256. `TokenAggregator.Combined()` ORs all three factors so tampering ANY slot trips both poison surfaces. Surface 1 — CT export offsets: `CloakOffset(off) => off ^ (factor & 0x7F)` in CeXml/CsxExporter. Surface 2 — Inspector `DisplayValue`: `MaybePerturbDisplay` XORs low byte of 1/11 numeric rows (per-row deterministic seed) with `factor & 0xFF`. Dev mode (Debug/Release single-file with 0xCC slots) short-circuits to factor=0 — exports + Inspector identical to pre-Phase-26. `tools/inject-hash.ps1` upgraded to 3-slot AOBMaker-style patching; build.ps1 calls it on `-Mode Publish` only. Class names renamed to non-suggestive forms (`IntegrityGuardian` → `BuildArtifactToken` / `IntegrityCore` → `SchemaConstants` / `StringShield` → `LocalizedText`) so binary metadata no longer leaks the words "Integrity" / "Guardian" / "Shield". UI footer (centered, opacity 0.55) shows `© bbfox @ https://opencheattables.com` decoded from XOR blob at VM construction. New end-user doc at `USAGE.md` (root) covers Mode A/B deployment for CT makers. Pre-Phase-26 work still in scope: **Phase 20-MT live-PASS across 5 targets** (BongoCat / SaGa F2R / Songs of Silence / LEAVES x86 / Legend of Mortal x86) MinHook detour on `mono_runtime_invoke` / `il2cpp_runtime_invoke` piggybacks on Unity's per-frame engine tick to drain a work queue on the Unity main thread. `callMethod` got two new optional args (`main_thread: bool` default false, `main_thread_timeout_ms: int` default 5000); `unity_scene.lua` defaults the engine-icall wrappers to `main_thread = true`; UI `MethodCallWindow` got a "Run on Unity main thread" checkbox. New `getDispatcherStats` RPC + `main_thread` sub-object on handshake/scan expose `hook_installed`/`is_healthy`/`tick_count`/`drain_count`/`queue_depth` for diagnostics. Discovered mid-implementation: nested `mono_thread_attach` on Unity main thread reproduces the build #54 BongoCat-killer; `ThreadAttachGuard` now consults a thread-local `IsCurrentlyDrainingOnMainThread()` flag and becomes a no-op on main thread. Stress 1000-iteration `Mathf.Abs` burst on main-thread path: BongoCat 15.66 ms/call, SaGa F2R 7.13 ms/call, Songs of Silence 3.95 ms/call — zero errors, zero timeouts, drain_count == 1000 on all. Two new lessons in [lessons-learned.md](lessons-learned.md): nested-attach trap + Unity 2018 focus-pause (`Application.runInBackground = false` default — R-Type Dimensions EX reports `is_healthy: false` when window loses focus, expected behavior). Phase 20b / 20c / 20d (Scene tab UI + cross-tab handoff + 2-target verify) still open; Phase 20-HEAP (libgc heap-walker fallback) also still open. Live-target work converged + **basic Symbol Finder shipped**. **Phase 25** new tab in MainWindow (position 4) wraps a new `searchSymbols` RPC walking every class in an assembly for fields + methods whose name contains an ASCII case-insensitive substring; replaces the "click each class in turn" loop the user reported. Both backends emit unified wire shape (IL2CPP rows carry AOT address; Mono skips mono_compile_method for bulk-scan performance — JIT deferred to the user-driven Methods picker). Row activation jumps back to Browser Fields/Methods tab with the class + member filter pre-selected. Phase 25b "interesting preset" curated keyword scan deliberately deferred until basic Finder shakes out. **DLL-free Mono path** now usable end-to-end on Unity 2022+ Mono — surfaced + fixed two production-blocker bugs during live smoke: (1) user-autorun `_G.mono_registerSymbolEx` shadows our idempotent helper, fixed by namespacing the working code under `_G.discrete_mono_registerSymbolEx`; (2) CE's `mono_method_get_parameters` returns nil typename for every parameter on at least UnityEngine.Object's methods on Unity 2022+ Mono — `mono_method_getSignature` is fine, so the helper now falls back to signature substring match when typename match fails (success log shows `(matched via signature)`). Both lessons recorded in [lessons-learned.md](lessons-learned.md). Carry-forward: Phase 22 closed in prior session. Phase 23 (heap-scan convergence) and Phase 20a-2 (FindObjectsByType cross-version recipe) both closed 2026-05-18.

> Companion files: [plan.md](plan.md) (canonical phase ledger), [pipe-protocol.md](pipe-protocol.md) (DLL ↔ UI contract), [Archiutecture.md](Archiutecture.md) (rules / invariants), [test-matrix.md](test-matrix.md) (verified games + tooling versions), [lua-invoke.md](lua-invoke.md) (Phase 19 spec), [address-finder.md](address-finder.md) (Phase 17 spec).

---

## What's done

| Phase | Status | Highlights |
|---|---|---|
| 0–11.5 | ✅ | Bootstrap → Mono/IL2CPP backends → live walking → UI panels → CE Lua bridge → inspector inline drilldown. |
| 12 (x64) | ✅ | `version.dll` / `winmm.dll` proxy DLLs + `ProxyDeployWindow` bulk-install panel. |
| 12d (32-bit Mono) | ✅ ALL d-1..d-5 | **Fully closed + live-smoked 2026-05-19** on Legend of Mortal (Unity 2020.3.49f1 / Mono bdwgc / x86). Parallel x86 CMake path via `build.ps1 -Arch x86` (output to `dist/x86/`). New `dll/src/shared/ManagedLayout.h` makes MonoObject / MonoString / MonoArray / Exception offsets constexpr-of-`sizeof(void*)`. New `tools/inject-x86/` cross-bitness helper exe (15.5 KB) for DiscreteUI x64 → 32-bit target inject; `WindowsDllInjector` shells out to it; `MainWindowViewModel` routes DLL path through `dist/x86/`. Live PASS: handshake `backend=Mono, unity_version=2020.3.49f1, 114 assemblies`; 19 Transform instances via `findInstances`; `walkInstance` decoded `m_CachedPtr` at offset 0x08 (= correct x86 MonoObject header); `monoCompileMethod GetInstanceID` returned real JIT address; **standalone `inject-x86.exe 55456 ...` reported `wow64=true LoadLibraryW=0x00D70000 OK` followed by clean pipe handshake** — proves the cross-bitness inject mechanism. Cosmetic v2 polish open: ProcessPickerWindow doesn't visually badge 32-bit rows; ProxyDeployWindow lacks manual x86/x64 toggle. Neither blocks anything. |
| 13 | ✅ | `dist/metadata-cli.exe` — offline `global-metadata.dat` parser. |
| 14a–14f | ✅ | Hardening: auto-disconnect, stripped-metadata banner, thread-attach audit, log rotation, SEH-leaf audit. |
| 14c | ✅ | IL2CPP metadata v24/v27/v29/v31 dispatch — six games verified across all four versions. Cross-checked vs `vendor/libil2cpp/`. |
| 15 | ✅ | [test-matrix.md](test-matrix.md) — **9 verified targets** across two backends + 4 metadata versions × 5 Unity major versions (2018.1 / 2019.4 / 2021.3 / 2022.3 / 6.1). Latest additions (2026-05-07): R-Type Dimensions EX (oldest IL2CPP — Unity 2018.1, surfaced missing-exports finding) + Songs of Silence (newest IL2CPP — Unity 6.1 / v39, first-class supported). |
| 17 (a/b/c/d) | ✅ | **Address Finder (reverse lookup).** New wire command `whatIsAt(addr)` returning instance + class + declared field. DLL backward-scan + class-pointer cache, UI Address Lookup tab, CE Lua bridge `unity_addrfind.lua`. Spec: [address-finder.md](address-finder.md). |
| 18 (a/b/c/d) | ✅ | **Find Method Instances + AOB AA-script generator + AOBMaker direct push.** New wire commands `findMethodInstances` + `getMethodAobPattern`, hand-rolled x64 LDE, UI overloads modal, `unity_symbols.lua` overload-aware extension, `CreateAAScript` integration over `\\.\pipe\AOBMakerCEBridge`. |
| **19 (a/b/c/d/e)** | ✅ | **Lua-callable Unity methods.** New wire RPC `callMethod(asm, ns, class, method, [signatureMatch], [instance], [args])` invoking Mono / IL2CPP methods with marshaled primitive + null + address + string args + boxed-primitive / address / UTF-8-decoded-string returns + managed exception capture. Static AND instance dispatch on both backends. UI MethodCallWindow (right-click any overload row → Call method…) + `scripts/unity_invoke.lua` Lua bridge with full arg-builder set. Spec: [lua-invoke.md](lua-invoke.md). |
| **19d-2** | ✅ | **UI polish.** Methods grid "Call method… (pick overload)" auto-skips the picker modal when `findMethodInstances` returns exactly one overload. Inspector right-click on any instance → "Call method on this instance… (switch to Methods)" snaps `SelectedInstance` + flips to the Methods tab (scope-checked: only when Browser.SelectedClass matches Inspector.TargetClass). |
| **19d-3** | ✅ | **Smart-typed editors in MethodCallWindow.** Replaces the single TextBox in the Value column with a per-row editor picked via `MethodCallEditorKind`: NumericUpDown for sized ints (range-clamped at the editor, no more post-Call out-of-range errors); ComboBox for enum params populated from `getEnumValues` (wire-tag re-routed through the enum's underlying integer type — fixes a latent type:"address" fallthrough bug); AutoCompleteBox for `System.Boolean` (true/false suggestions, free text still accepted). Float/Double stay on TextBox (decimal can't represent the full double range). Probe order for enums: method's own assembly → UnityEngine.CoreModule. Live-smoked on IL2CPP (PARTY_WORK.SetValue) and Mono (BongoCat / Mathf.Abs). |
| 16 (docs) | ✅ | All planned files exist. New in this batch: `lessons-learned.md`, `README.md` + `README.zh-TW.md` (root), `CONTRIBUTING.md` (root), `proxy-dlls.md`, `metadata-cli.md`, `export-formats.md`, `mono-internals.md`, `il2cpp-internals.md`, `dll-spec.md`, `ui-spec.md`, `technical-notes.md`. |
| **21a (Reverse-property lookup)** | ✅ | New wire RPC `listFieldValuesAcrossInstances(class, fieldOffset, fieldType[, limit])` — row-wise inverse of `walkInstance`. Right-click any value-typed Inspector row → "List this field across instances…" → modal grid showing `[instance_address, field_address, value]` for every live instance of the target class. Scope: value-typed fields whose offset is statically fixed from the instance base. New `InspectorFieldOffsetResolver` (Core) walks the parent chain summing offsets across value-struct ancestors and bails on pointer / collection / array-slot ancestors with a soft reason. **Live-smoked 2026-05-19** on IL2CPP (`PARTY_WORK +0x10 System.Byte`, 256 instances). Polish landed same day: column sort (after the three-layer AOT trap fix, see below) + case-insensitive substring filter on the Value column. 9 resolver unit tests + 3 fake-pipe RPC smoke tests. |
| **Follow-up tasks (2026-05-19)** | ✅ | Five small UX additions spawned + merged on the same day as Phase 21a. **Classes Copy class name** (right-click any Classes-list entry); **Inspector Class ptr Copy buttons** (small `name`/`addr` buttons next to the Class ptr row); **Fields/Methods per-cell context-menu copy** (Copy field/method name / Copy type / Copy address / Copy "Declared in"); **Copy CE Field multi-select fix** (was only exporting the last-focused row); plus Phase 21a itself. |
| **DataGrid sort AOT three-layer trap fix** | ✅ | Long-running mystery: every DataGrid in the app silently no-op'd on header click under AOT. Layer 1: `AvaloniaUseCompiledBindingsByDefault=true` + Window-level `x:DataType` makes column Bindings compile to `CompiledBinding` (no readable Path). Layer 2: empty Path → DataGrid auto-disables `CanUserSort` per-column (grid-level `CanUserSortColumns=True` is overridden — click doesn't even fire `Sorting`). Layer 3: even with sort re-enabled, default sort uses reflection on `SortMemberPath` which AOT trim drops — glyph animates but rows don't reorder. Fix needs all three layers: explicit `SortMemberPath` + `CanUserSort="True"` per column in XAML, plus `CustomSortComparer` wired in code-behind. New shared `DataGridSortComparers` + `WireSortComparers` extension applied to all 6 grids (FieldAcross / Fields tab / Inspector / Methods tab / ProcessPicker / ProxyDeploy). Diagnosed via FileLogger Sorting-event trace; cross-checked against `D:\Github\CrimsonAtomtic` for reference shape. Full mechanism + diagnostic recipe in [lessons-learned.md](lessons-learned.md). Recipe for new DataGrids in [technical-notes.md](technical-notes.md). |
| **22 (Live CT Population via AOBMaker)** | ✅ | All sub-phases (22a–22f) shipped 2026-05-12 across 6 commits. `AobMakerBridge` extended with InjectTableFile + CreateMemoryRecord + CreateAAScriptWithRecords + CheckAvailability; 7 lua bridge files embedded as `<EmbeddedResource>` in Core; bootstrap AA-script generator (idempotent guard + preload trick for `unity_dump.lua`'s `require 'utils'`); `BridgeInjector` orchestrator (8-step pipeline w/ progress + structured failure result); MainWindowViewModel hoisted AobMaker to top-level + Tools menu RelayCommand; per-feature `CallMethodAaScriptGenerator` + `AddressLookupAaScriptGenerator` for MethodCallWindow + AddressLookup tab; new [`docs/ce-bridge-integration.md`](ce-bridge-integration.md) + [`tools/Test-BridgeInject.ps1`](../tools/Test-BridgeInject.ps1) wire smoke. **Live click-through smoke pending.** |
| **21 (Inspector field write-back)** | ✅ a/b/c | New wire RPC `setInstanceField(addr, fieldOffset, type, value)` (build #326) — one backend-agnostic handler dispatching per-type to `Memory::SafeWriteBytes`. Accepts both wire-short tags (`int32`) and CLR names (`System.Int32`); marshals into an 8-byte stack buffer with per-type clamping + width validation. New `FieldWriteWindow` modal (build #331) opened from Inspector right-click "Write value…" — editor flavor picked by new `InspectorWriteTypeMap.Resolve` helper (NumericUpDown for sized ints / TextBox for float / AutoCompleteBox for bool / TextBox for char + address). Reference / value-struct / collection rows open the modal but show an unsupported banner. New `SetFieldValueFactory` paralleling `CallArgFactory`; `BackendAdapter.SetInstanceFieldAsync` + `SetInstanceFieldResult` DTO. CE Lua: `setField` / `setInstanceField` in `unity_invoke.lua` mirror the wire RPC. `tools/Test-SetInstanceField.ps1` smoke (`readMemory` snapshot → `setInstanceField` → `readMemory` verify → restore). 238/238 tests pass (+45: 6 fake-pipe RPC + 31 classifier theory + misc). **21d live click-through pending** — recipe in plan.md. |
| **5 (v32-v39 metadata.dat parser)** | ✅ shipped + LIVE-SMOKED | `MetadataParser` widened to accept `[16, 39]` (was `[16, 31]`). New layouts in `MetadataLayout.h`: `TypeDefinition_v35` (84 bytes — v29 minus `elementTypeIndex`), `StringLiteralEntry_v35` (4 bytes — drops length, computed from delta), `Il2CppSectionMetadata` (12-byte `{offset, size, count}` triplet), `HeaderV38` (380 bytes — 31 section triplets), `IndexSizes` runtime-variable struct + `GetIndexSize(count)` helper. `LoadHeader` dispatches flat (v16-v37) vs section (v38+). `LoadTables` computes `IndexSizes` from `parameters.size/count` + table counts. `TypeDefSizeof` / `FieldDefSizeof` / `FieldDefaultValueSizeof` return per-version sizes (84 for v32-v37; `68+3*TIS+GCIS` for v38+ TypeDef; `8+TIS` for v38+ FieldDef / FieldDefault). `GetTypeDefinition` / `EnumerateTypeFields` / `FindFieldDefault` got hand-walked v38+ paths that consult `indexSizes_`. `GetStringLiteral` recovers v35's missing length from delta-to-next-entry. Smoke ladder: synthetic-fixture `tools/Test-MetadataVersions.ps1` PASS on v29/v31/v35/v37/v38/v39 + correctly rejects v40; **live smoke on Songs of Silence (Unity 6.1.x / v39) 2026-05-19** — `metadata-cli info` decoded 40,501 TypeDefinitions + 44,047 string literals; `metadata-cli stats` ran clean across all 40k types (strip_ratio=0.0008, verdict `clean`); `metadata-cli type UnityEngine Vector3` decoded all 15 expected fields (`kEpsilon` / `kEpsilonNormalSqrt` / `x/y/z` / `zeroVector...negativeInfinityVector`) with correct tokens (0x04000A3B+) + variable-width type indices. **v32-v39 dispatch verified end-to-end on real-world data.** |
| **25 (cross-class Symbol Finder)** | ✅ shipped + LIVE-SMOKED | New `searchSymbols` pipe RPC (parallel Mono + IL2CPP handlers) walks every class in a named assembly, iterates fields + methods via the existing `EnumerateClasses` + `ReadClassInfo` path, applies ASCII case-insensitive substring match on name, accumulates up to `limit` (default 200, hard-cap 5000), returns `scanned_classes` + `truncated`. IL2CPP method rows carry AOT pointer; Mono skips JIT for bulk scan (drill into Methods picker for the per-overload `mono_compile_method`). New UI Tab "Symbol Finder" (position 4, after Address Lookup) — assembly dropdown shares `Browser.Assemblies`, pattern textbox (Enter triggers Search), Fields/Methods toggles, Limit NumericUpDown (Width 140 + MinWidth 120 so the number stays visible after the spinner widget claims its share), sortable results grid (Kind / Class / Member / Type / Offset / Address). Row double-click or right-click → Navigate jumps back to Browser Fields/Methods tab with the class + member filter pre-selected; right-click also surfaces Copy class / Copy member. 3 new fake-pipe `SearchSymbolsTests`. **Live smoke 2026-05-19** on BongoCat (Mono / Unity 2022+): pattern `MaxHP` against `Assembly-CSharp` returned `MainGame/CALC_WORK.maxHp` (`System.Int32` @ `0x2C`) as first hit in < 1s; double-click jumped to Browser Fields tab with the row filtered + selected. Phase 25b "interesting preset" (curated Unity keyword scan + scoring, mirror of UE5CEDumper's Interesting Properties panel) deferred until basic Finder shakes out — Unity-specific keyword list (HP/Damage/Gold/XP/Speed/...) still needs design. |
| **26 (Anti-tamper protection)** | ✅ shipped + AOT-verified | 3-slot SHA256 self-check spread across two assemblies + 2 poison surfaces + XOR-shielded credit string. Slots: A `BuildArtifactToken` (Core/Services) / B `ExportFormatToken` (Core/Services/Export) / C `WindowsRuntimeMetadata` (ui/UnityDumpUI/Platform — **cross-assembly**). `TokenAggregator.Combined() = A \| B \| C` bridges Core consumers to slot C via UI-registered delegate from `Program.Main`. Poison surface 1: `CeXmlExporter` / `CsxExporter` `CloakOffset(off) => off ^ (factor & 0x7F)` shifts every exported field by 0-127 bytes (CT loads cleanly in CE, reads wrong fields). Poison surface 2: `LiveInspectionViewModel.WalkedFieldNode.MaybePerturbDisplay` perturbs 1/11 numeric `DisplayValue` rows (per-row deterministic seed) via low-byte XOR — same row corrupts same way within a session, different rows across sessions (string `GetHashCode` randomization). Anti-trace: each guardian's `Read()` returns 0 for first N calls (A:3 / B:2 / C:4) even when tampered. Anti-debug: `Debugger.IsAttached` on a published binary returns its own poison code. Dev-mode no-op: 0xCC placeholder `IsDevMode` short-circuits all factors to 0 (Debug/Release single-file paths). `tools/inject-hash.ps1` upgraded to 3-slot AOBMaker-style patching (zero all 3 slots → one SHA256 → write same hash to all 3); `build.ps1 -Mode Publish` invokes it. AOT round-trip verified: sentinels at `0x02438F98` / `0x02438E80` / `0x0243ABF8` (C ~11 KB away from A/B = different .rdata blob). Footer credit `© bbfox @ https://opencheattables.com` decoded from XOR blob at VM construction. Class names renamed (`IntegrityGuardian` → `BuildArtifactToken` / `IntegrityCore` → `SchemaConstants` / `StringShield` → `LocalizedText` / `GetIntegrityFactor` → `Read`) so binary metadata no longer leaks "Integrity" / "Guardian" / "Shield" strings. New end-user guide [`USAGE.md`](../USAGE.md) at repo root for CT makers. 283/283 tests pass (no new tests — protection layer dev-mode-bypassed). Commits `ccaae6e` (single-slot) + `348cbb6` (3-slot upgrade). |

---

## What landed in this session — 2026-05-20 even later (Phase 27 planning + Phase 28 stub)

**Planning only — no code changes.** User asked to evaluate two new feature
ideas; after discussion, agreed to fully plan feature 1 and add feature 2
as a TODO stub.

### Phase 27 — Unity-aware Value Search

Full sub-phase plan (27a–27f) drafted in [plan.md](plan.md). Key design
decisions captured in the AskUserQuestion exchange:

* **v1 includes string scan** — managed UTF-16 path walking `MonoString*`
  / `Il2CppString*` layouts; encoding option + Exact / Contains match
  mode in UI.
* **v1 includes full CE filter-mode set** — exact / range / changed /
  unchanged / increased / decreased / new value (parity with CE Next
  Scan).
* **Two scan modes shipped together** — global (CE flow) for the
  "I have HP=100, find it" case; class-scoped (Discrete flow) for
  "I know it's somewhere on PLY_WORK" — ~100x faster, essentially
  `listFieldValuesAcrossInstances` + value predicate.

### Architecture choice

Reuses existing primitives instead of rebuilding AOBMaker-style:

| Existing | What it gives us |
|---|---|
| `Memory::EnumerateCommittedRegions` (Phase 23 hardened) | MEM_IMAGE + thread-stack filtering for free |
| `Memory::ScanForPointer` template | Parallel chunked scan pattern |
| `whatIsAt` (Phase 17) | Per-candidate Unity-managed filter |
| `findInstances` (Phase 6c) | Class-scoped mode core |
| `listFieldValuesAcrossInstances` (Phase 21a) | Pattern for class-scoped value walk |
| AOBMaker `WindowsValueScanner.cs` shapes | UI-side `DataType` / `ScanType` enum reference (not the engine itself — we run from inside the target process) |

Anti-cheat is largely a non-issue because the scan runs from inside the
target process via the existing injected DLL — user-mode AC hooking
`OpenProcess`/`ReadProcessMemory` doesn't see our scan. `SafeReadBytes`
SEH-protection covers the rare in-process VirtualQuery hook case.

### Phase 17b carry-forward bundled into Phase 27

Array / List<T> element resolution was deferred in Phase 17b ("nearest
field" returns when query lands in an array buffer). Phase 27c finally
closes this — detect Array klass via class-name `[]` suffix or
non-null element_class, compute index from offset, return
`{kind: "array-element", arrayIndex, arrayLength}`.

### Sub-phase v1 estimate: ~6.5 days

| Sub-phase | Effort |
|---|---|
| 27a (DLL First Scan + class-scoped + whatIsAt filter) | ~2 days |
| 27b (Next Scan session + filter modes + string path) | ~1.5 days |
| 27c (Array element resolution in whatIsAt) | ~0.5 day |
| 27d (UI Value Search tab + Cancel + cross-tab handoffs) | ~2 days |
| 27e (CE Lua bridge `scripts/unity_valuescan.lua`) | ~0.5 day |
| 27f (libgc heap-section, deferred — closes 20-HEAP simultaneously) | ~1-2 days |

### Phase 28 — Field → Methods cross-reference (TODO stub)

Brief approach captured in plan.md. Mono path uses IL bytecode walk
(`ldfld`/`stfld`/`ldsfld`/`stsfld` token match) — high confidence, clean.
IL2CPP static fields reuse [AOBMaker's `RipReferenceScanner.cs`](D:\github\AOBMaker\src\AOBMaker.Core\Services\RipReferenceScanner.cs)
against the field's static-storage address. IL2CPP instance fields stay
heuristic (AOB scan for `[reg+offset]` patterns within declaring class's
methods) and may be skipped in v1. No sub-phases / no commitment to
scope yet.

### Next session pickup

* Start coding Phase 27a — new `Memory::ScanForValue<T>` helper +
  `scanFieldValue` RPC handler in both backends. Same SEH-leaf pattern
  as the existing scan primitives.

---

## What landed in this session — 2026-05-20 late (Phase 26 anti-tamper + USAGE.md)

User-driven hardening pass inspired by `D:\Github\AOBMaker`'s pattern:
add cheap, hard-to-bypass binary protection so a hex-edited DiscreteUI
silently produces wrong output instead of either crashing or letting
the modification go unnoticed. Two iterations on the same day.

### Iteration 1 — single Guardian (`ccaae6e`)

* Added [LocalizedText](../ui/UnityDumpUI.Core/Services/LocalizedText.cs)
  (was `StringShield`) — rolling-XOR encoded blobs for `bbfox`, the
  URL, and the full credit string. PowerShell-precomputed bytes match
  on-host decode bit-for-bit (35-char attribution round-trip
  hand-verified during build).
* Added single-slot `IntegrityGuardian` + `IntegrityCore` (renamed in
  Iteration 2 — see below) + [tools/inject-hash.ps1](../tools/inject-hash.ps1)
  + `build.ps1 -Mode Publish` hook. AOT-verified slot at `0x024382B8`.
* Added centered footer to MainWindow.axaml bound to
  `MainWindowViewModel.CopyrightText`.
* CT export `CloakOffset(off) => off ^ (factor & 0x7F)` in
  `CeXmlExporter` + `CsxExporter`. 0-127 byte shift means CE still
  parses the CT cleanly; addresses land in the same object, just
  reference wrong fields.

### Iteration 2 — 3 slots cross-assembly + Inspector poison (`348cbb6`)

User feedback: "AOBMaker 太近了，放遠一點" + "poison 點能不能偶爾在
grid 給錯數值/offset?" → upgraded design.

* **Renamed all 3 originals** to non-suggestive forms so binary
  metadata stops shouting "integrity protection":
  * `StringShield` → `LocalizedText`
  * `IntegrityCore` → `SchemaConstants`
  * `IntegrityGuardian` → `BuildArtifactToken`
  * `GetIntegrityFactor()` → `Read()`
* **3 token guardians**, cross-assembly:
  * Slot A — [`BuildArtifactToken`](../ui/UnityDumpUI.Core/Services/BuildArtifactToken.cs) in Core/Services
  * Slot B — [`ExportFormatToken`](../ui/UnityDumpUI.Core/Services/Export/ExportFormatToken.cs) in Core/Services/Export
  * Slot C — [`WindowsRuntimeMetadata`](../ui/UnityDumpUI/Platform/WindowsRuntimeMetadata.cs) **in ui/UnityDumpUI/Platform (cross-assembly)**
  * AOT layout verified: A `0x02438F98` / B `0x02438E80` / C `0x0243ABF8` — C ~11 KB away from A/B (different .rdata blob).
* **TokenAggregator** ([`Core/Services/TokenAggregator.cs`](../ui/UnityDumpUI.Core/Services/TokenAggregator.cs))
  bridges Core ↔ UI: `Program.cs` calls
  `TokenAggregator.RegisterExternal(WindowsRuntimeMetadata.Read)` at
  app startup so Core consumers see slot C's factor via delegate.
  `Combined() = A | B | C` — tampering ANY slot trips both surfaces.
* **`InternalsVisibleTo`** added for `DiscreteUI` (not "UnityDumpUI" —
  the assembly name differs from project name, hit + fixed during build).
* **Second poison surface** — Inspector grid:
  `LiveInspectionViewModel.WalkedFieldNode.MaybePerturbDisplay` runs
  on every primitive display value. Per-row deterministic seed (string
  GetHashCode XOR offset XOR salt) — 1-in-11 rows get their numeric
  low byte XOR'd with `factor & 0xFF`. Skips structs / pointers /
  errors / quoted strings. Same row corrupts same way within a session
  (looks like a flaky reader, not a tamper response); across sessions
  string GetHashCode randomization shifts WHICH rows look buggy.
* **3-slot inject-hash.ps1**: locate all 3 dev slots → zero all 3
  hash regions → compute SHA256 once → write same hash into all 3.
  Each runtime guardian does the same zero-everything-then-hash dance
  so its own slot's stored hash matches the computed hash. Same
  pattern as AOBMaker's A/B/C.

### Defensive-design notes recorded (per code-review pass)

* **DelayThreshold side-effect**: each guardian's `Read()` returns 0
  for the first N calls (A:3, B:2, C:4) even when tampered, defeating
  trivial breakpoint trace. Consequence: the first ~2 emitted offsets
  on first export are unpoisoned even on a tampered binary — but the
  remaining 100+ are, and the inspector poison's per-row seed is
  independent of that counter.
* **Single-file Release vs AOT**: the deflate-compressed bundle in
  `dist/DiscreteUI.exe` (Debug/Release single-file mode) has the
  sentinel byte sequence but NOT the trailing 32×0xCC (compression
  collapses runs). Only `-Mode Publish` (AOT) produces a binary where
  inject-hash.ps1 can find + patch slots. Build script gates the
  injection accordingly — dev iteration is unaffected.
* **Dev-mode no-op**: `IsDevMode(HashSlot)` checks for the 32×0xCC
  tail; while uninjected, every `Read()` short-circuits to 0 and
  factors stay 0 across `TokenAggregator.Combined()`. All 283 tests
  + every Inspector smoke + every CT export remain byte-identical.

### New end-user doc — [USAGE.md](../USAGE.md) at repo root

Brief, technical-detail-free guide aimed at CT makers who already
know Unity + CE table authoring. Phpbb-markdown-compatible (per
`D:\Github\CrimsonAtomtic\docs\oct\phpBB-markdown-syntax.md`) so it
pastes into an opencheattables.com forum post without rework. Key
section is "CE Table deployment modes" — Mode A keeps DiscreteUI for
the author, Mode B bakes the bridge into the `.CT` via
**Tools → Inject Discrete Bridge**.

### Tests / build

* 283/283 xUnit pass (unchanged — no new tests, protection
  short-circuits in dev mode)
* `dotnet build` 0 errors / 0 warnings (Core + UI + Tests)
* AOT publish round-trip verified: 3 sentinels in published exe + 3
  injectable slots + inject-hash.ps1 stamps all 3 with same SHA256

### Carry-forward / next session

* Phase 26b candidates if user wants more breadth: add poison points
  to Methods grid address column + Symbol Finder address column
  (currently only Inspector + CT export). Not blocking.
* Two minor open items pre-Phase-26 from `plan.md` remain deferred:
  17e (Strategy B cached heap walk) + 20-HEAP (libgc fallback) +
  25b (interesting-preset Unity keyword scan).
* `_invocationCount` 2^31 wrap edge case (4 billion calls per
  session) — acknowledged, not patched (irrelevant in practice).

---

## What landed in this session — 2026-05-19 late additions (Phase 25 + DLL-free Mono live smoke + MethodOverloads polish + Phase 21d / enum smoke)

On top of the mega-batch below, the session kept going through three more
live-smoke cycles + one new feature. All shipped on `dev`, fast-forwarded
to `main`, pushed to origin.

### Phase 25 — cross-class Symbol Finder (~½ day)

The user reported clicking each class one-by-one through `Assembly-CSharp`'s
several-hundred classes whenever they hunt a property like `MaxHP`. New
Tab on the right pane (position 4) wraps a new `searchSymbols` RPC with:
ASCII case-insensitive substring match (asymmetric tail-match preserves
the `Int32 → UInt32` substring trap), fields + methods toggles, Limit
(default 200, DLL-side hard cap 5000), sortable result grid, double-click
navigate-to-Browser handoff (selects assembly + class, pre-fills the
Fields/Methods filter, switches tab). DLL Mono + IL2CPP each got a
parallel handler walking `EnumerateClasses` × `ReadClassInfo`. New file-
local `ICaseContainsAscii` helper (no `<locale>` drag-in — names are
always ASCII in C#). Live smoke on BongoCat / Mono / Unity 2022+:
`MaxHP` → `MainGame/CALC_WORK.maxHp` (`System.Int32` @ `0x2C`), < 1s
end-to-end. Properties handled as `get_*`/`set_*` methods (Mono/IL2CPP
runtime convention; UI confirmed acceptable). Phase 25b interesting-
preset deferred — needs a Unity-tuned keyword list distinct from
UE5CEDumper's FProperty-flavored one.

### DLL-free Mono register path — live smoke + 2 production fixes

Live smoke against BongoCat (Mono / Unity 2022+) drove the path through
`Object.FindObjectsByType(System.Type, FindObjectsInactive, FindObjectsSortMode)`
(the 3-arg enum overload). Surfaced two issues that both needed a fix
before the path was usable on any Unity 2022+ Mono target:

* **User-autorun shadows the helper's idempotent block.** The user had
  hand-rolled `_G.mono_registerSymbolEx` in a CE autorun script (forums
  pattern, doesn't call `LaunchMonoDataCollector`); our helper's
  `if _G.mono_registerSymbolEx == nil` guard skipped its own load,
  leaving the AA to invoke the unattached autorun version → instant
  `mono_findClass` returns nil → `💔 Class not found - UnityEngine.Object`.
  Fix: `scripts/mono_registersymbol_helpers.lua` now installs
  `_G.discrete_mono_registerSymbol*` (no guard, always overwrite — our
  namespace, our canonical version) alongside the legacy `mono_*`
  names (still guarded for user copy-paste). Generator
  (`MonoRegisterSymbolAaScriptGenerator`) keys idempotency off the
  discrete-namespaced symbol and emits `_G.discrete_mono_registerSymbolEx`
  calls.
* **CE 7.6 + Unity 2022+ Mono returns nil typename for every parameter
  on `UnityEngine.Object`'s methods**, even though `mono_method_getSignature`
  renders the full type info cleanly. Helper now falls back to signature-
  string substring match when typename match fails for any param; success
  log shows `(matched via typename)` vs `(matched via signature)`. Plus
  `pcall` shielding around `mono_method_get_parameters` (generic /
  uncompiled overloads throw), `mono_enumImages` warm-up for split-module
  classes, and an asymmetric `_discrete_typename_match` so the generator
  can emit full namespaced names while the helper accepts CE's stripped
  forms (still rejects the `Int32` substring of `UInt32`). Diagnostic
  output now lists every name-matched overload + its rendered signature
  when no match lands, so the next CE/Unity-version surprise surfaces
  the relevant data immediately.

### MethodOverloadsWindow polish (4 small fixes triggered by smoke)

* **WrapPanel + Width 1100** (was StackPanel + 900) — bottom button bar
  now reflows under any window size instead of clipping the last entries.
* **Mechanism-grouped button labels** replacing the wordy old set: now
  `Copy: addresses / registersymbol / aobscan / mono_register` plus
  `Push aobscan to CE / Push mono_register to CE`. The action prefix
  (`Copy:` vs `Push`) groups visually, the underlying CE primitive name
  shows what each button emits — no more "what's the difference between
  three copy buttons".
* **Single-overload auto-tick** — Refresh ticks the only row's
  `IsSelected` so the bottom-bar commands enable on open without manual
  click; mirrors the Phase 19d-2 auto-skip on the Call method… context
  menu. Multi-row keeps the legacy "user must tick" gating; status text
  updated to "Tick rows to enable the buttons below".
* **NumericUpDown Width 140 + MinWidth 120** (was Width 90) — the
  number display was hidden behind the up/down spinner widget; the
  Phase 25 Symbol Finder Limit field hit the same trap on first smoke
  and got the same fix.

### Phase 21d Inspector field write-back + enum ComboBox path — live click-through PASS

Both stayed pending after their initial Phase 21 / 19d-3 ship; this
session walked through them end-to-end:

* **Phase 21d**: BongoCat Mono right-click any primitive Inspector row →
  Write value… modal → Apply → readMemory verifies the write landed,
  CE address-list refreshed via the existing AOBMaker bridge.
* **enum ComboBox path**: same target, `UnityEngine.Object.FindObjectsByType(Type, FindObjectsInactive, FindObjectsSortMode)`
  drove both enum params through the Phase 19d-3 `getEnumValues` → ComboBox
  cascade. Closes the Phase 19d-3 follow-up that previously had no enum-
  parameter target to exercise.

### Session totals (post mega-batch)

* **Tests**: 238 → 241 (+3 SearchSymbolsTests).
* **Build**: #340-something → **#363**. DLL Mono 571.5→580.5 KB, IL2CPP
  606→615 KB, `DiscreteUI.exe` 39.1→39.2 MB, helper lua 10.6→23.1 KB.
* **Commits on dev (since the mega-batch hand-off)**: 2 —
  `a41c7d3 feat(ui): MethodOverloadsWindow polish + DLL-free Mono live smoke PASS`,
  `b1d8ef2 feat: Phase 25 — cross-class Symbol Finder tab`.
* **Docs**: STATUS.md / plan.md / pipe-protocol.md / lessons-learned.md
  all updated in the post-Phase-25 docs sweep.

---

## What landed in this session — 2026-05-19 mega-batch (19d-3 + 5 follow-ups + 21a + DataGrid AOT trap)

A single-day session that started as "ship Phase 19d-3 smart-typed
editors" and accumulated four more independent landings as user
testing surfaced gaps.

### Phase 19d-3 — smart-typed editors

Replaces the single TextBox in `MethodCallWindow`'s Value column with
a per-row editor picked by a new `MethodCallEditorKind` enum:

* **NumericUpDown** for `System.{S?Byte, Int16, UInt16, Int32, UInt32,
  Int64, UInt64}` — bounded to the type's `[MinValue, MaxValue]` so
  out-of-range input clamps at the editor instead of bouncing back as
  a post-Call error. `decimal`-backed (the full sized-int range fits).
* **ComboBox** for enum params — populated from a new
  `GetEnumValuesAsync` BackendAdapter call kicked from
  `MethodCallWindow.Opened`. Wire `type` tag is re-routed through the
  enum's `underlying_type` (fixes a latent bug where enums fell
  through `TryBuildArg`'s unknown-type branch to `type:"address"`).
* **AutoCompleteBox** for `System.Boolean` — suggests `true`/`false`
  on focus but accepts any text the existing `bool.TryParse` consumes
  (case-insensitive, surrounding whitespace tolerated).

Probe order for enums: method's own assembly → `UnityEngine.CoreModule`
for `UnityEngine.*` namespaces. Soft failures (class not found / not
an enum / Mono missing `mono_field_get_data`) silently leave the row
on TextBox. Float/Double stay on TextBox — decimal-backed
NumericUpDown would silently truncate doubles outside decimal range.

**Live smoke (user-confirmed)**: IL2CPP `MainGame.PARTY_WORK.SetValue(
UInt32 ofs, Byte value)` — both rows rendered NumericUpDown with
spinners, manual `6` into `ofs` accepted. Mono / Unity 2022+ BongoCat
`UnityEngine.Mathf.Abs(Int32 value)` — NumericUpDown rendered, manual
`99` accepted. Confirms parity across backends.

### 5 follow-up tasks (spawned + merged same day)

Each opened as a separate spawn-task chip and merged sequentially:

1. **Classes Copy class name** — Classes-list right-click menu adds
   "Copy class name" sibling to "Find instances". Single-select v1.
2. **Inspector Class ptr Copy buttons** — small `name` / `addr`
   buttons next to the Class ptr row in the Inspector header.
3. **Fields/Methods per-cell copy** — Fields grid right-click adds
   Copy field name / Copy type / Copy "Declared in"; Methods grid
   adds Copy method name / Copy address / Copy "Declared in".
4. **Copy CE Field multi-select fix** — was only exporting the last
   focused row when user multi-selected; now iterates SelectedItems
   on both Fields tab and Inspector grid. New
   `CeXmlExporterMultiFieldTests` × 4.
5. **Phase 21a Reverse-property lookup** (below).

### Phase 21a — reverse-property lookup

New "List this field across instances" right-click flow in the
Inspector. Inverse of the existing Inspector cascade (one instance →
all fields); this is one field → all live instances. New wire RPC
`listFieldValuesAcrossInstances`, new `FieldAcrossInstancesWindow`
modal with 3-column DataGrid (Instance address / Field address /
Value). Scope: value-typed fields whose offset is statically fixed.
Pointer-traversal / collection-element ancestors bail with a soft
reason. **Polish landed same day**: column sort works (after the
DataGrid AOT fix below) + case-insensitive substring filter on the
Value column. **Live-smoked**: `PARTY_WORK +0x10 System.Byte`, 256
matches end-to-end.

### DataGrid AOT three-layer trap (long-running mystery — solved)

Every DataGrid in the app silently no-op'd on header click under
AOT. Diagnosed via `FileLogger`-traced Sorting event + per-column
state dump. Three layers all have to be open:

| Layer | What happens | Symptom |
|---|---|---|
| 1 | Window-level `x:DataType` makes column Bindings compile to `CompiledBinding` whose Path is empty at runtime | (invisible) |
| 2 | Empty Path → DataGrid auto-disables `CanUserSort` per-column; `CanUserSortColumns=True` on the grid is overridden | Header click does nothing, no glyph, no Sorting event |
| 3 | Default sort uses reflection on `SortMemberPath` to read row property values; AOT trim drops that path | Glyph (▲▼) animates, but rows don't reorder |

**Fix** (needs all three layers open):

* Per column XAML: `SortMemberPath="<PropertyName>"` +
  `CanUserSort="True"` — opens layer 2.
* Code-behind: wire `CustomSortComparer` per column via a typed
  `Func<TRow, value>` selector — opens layer 3, AOT-safe (no
  reflection).

Shared infrastructure:

* [`UnityDumpUI.Core.Services.DataGridSortComparers`](../ui/UnityDumpUI.Core/Services/DataGridSortComparers.cs)
  — pure IComparer factories: `Hex<TRow>` / `Ordinal<TRow>` /
  `Number<TRow>` / `Bool<TRow>`.
* [`UnityDumpUI.Views.DataGridSortExtensions.WireSortComparers`](../ui/UnityDumpUI/Views/DataGridSortExtensions.cs)
  — `DataGrid.WireSortComparers(dictionary)` extension keyed by
  `SortMemberPath`.

Applied to **all 6 sortable DataGrids** in the app: FieldAcross,
Browser Fields tab, Inspector walked-fields, Methods tab,
ProcessPicker, ProxyDeploy — 33 columns total, working pattern in
`MainWindow.axaml.cs:WireSortComparersForAllGrids`.

**Cross-checked against** sibling project `D:\Github\CrimsonAtomtic`
(same Avalonia 12 + Native AOT setup, all DataGrids sort correctly).
Difference: their XAML scope shape produces plain `Binding` (not
`CompiledBinding`) for column Bindings, so layer 1 never triggers
and the default sort works. Our explicit-comparer route is the
strict-superset fix that works in both scope shapes.

Full mechanism + diagnostic recipe in
[lessons-learned.md §"Avalonia 12 DataGrid sort is a three-layer
trap"](lessons-learned.md). Recipe for adding new DataGrids in
[technical-notes.md §"Adding a new sortable DataGrid"](technical-notes.md).

### Session totals

* **Tests**: 163 → 180 pass (+17 new: 4 multi-row Copy CE field +
  9 offset resolver + 3 fake-pipe RPC smoke + 1 namespace threading
  regression).
* **Build**: AOT publish clean throughout. `DiscreteUI.exe` grew
  38.3 MB → 38.9 MB (Phase 21a new VMs/Window + DataGrid sort
  helpers).
* **Commits**: 19 on `dev` since `06f7880` (was main).
* **Docs**: STATUS.md + plan.md + technical-notes.md +
  lessons-learned.md + pipe-protocol.md all updated.

### Late-day addition — DLL-free Mono symbol register path

User-requested: for cheat-table authors who don't want to inject the
UnityCEDumper DLL, generate AA scripts that call CE's built-in
Mono Lua API (`mono_findClass` / `mono_class_enumMethods` /
`mono_compile_method`) via the user's hand-rolled
`mono_registerSymbol` / `mono_registerSymbolEx` /
`mono_registerSymbolBySignatureMatch` helpers. Lives in the Methods
overload picker (right-click any method → Method overloads).

* **New** `scripts/mono_registersymbol_helpers.lua` — the three
  register helpers + `mono_ensureAttached()` + `SafeMonoDestroy`,
  all `if _G.xxx == nil` guarded. Side-effect-free at load time
  (Mono attach happens lazily on first register call) so the
  bridge bootstrap can ship it harmlessly even to users who never
  hit the DLL-free path.
* **Wired into** `BridgeLuaResources.LoadOrder` (now 8 entries) +
  `UnityDumpUI.Core.csproj` `<EmbeddedResource>` so Tools → Inject
  Discrete Bridge ships it into the CT alongside the existing 7
  `unity_*.lua` files.
* **New generator** `MonoRegisterSymbolAaScriptGenerator` —
  emits an AA whose `[ENABLE]` lazy-loads
  `mono_registersymbol_helpers.lua` out of the CT via
  `findTableFile` + `createStringStream` + `load` (same trick
  `BootstrapAaScriptGenerator` uses for `unity_dump.lua`), with
  idempotency guard `if _G.mono_registerSymbolEx == nil`. One
  `mono_registerSymbolEx(symName, ns, cls, method, { paramTypes })`
  call per selected overload, using **full-name** paramTypes
  (e.g. `'System.Int32'` not `'Int32'`) to avoid the substring
  trap (`feedback_signaturematch_substring`). `[DISABLE]`
  pcall-wraps `unregisterSymbol` per — partial enable still tears
  down cleanly.
* **UI surface** in `MethodOverloadsWindow`: two new buttons
  ("Copy mono_register AA", "Create mono_register in CE") gated
  on `IsMonoBackend` (new VM property threaded through from
  `MainWindowViewModel.Backend2 == "Mono"`). The CreateInCe
  button additionally requires `HasAobMakerBridge`.
* **Mono-only by design** — IL2CPP overloads route to the
  existing AOB-scan path. The new generator's `Build()` throws
  `ArgumentException` if any overload has `ModuleName` populated
  (IL2CPP signal), so a UI-gating bug would surface loudly
  rather than emit a broken AA.
* **Tests**: 180 → 193 pass (+11 new
  `MonoRegisterSymbolAaScriptGeneratorTests` covering helper
  lazy-load shape, single + multi overload, zero-param, null
  paramTypes, Lua apostrophe escaping, IL2CPP-input guard, empty
  + null input; +3 new `BridgeLuaResources` Inline cases for the
  8th file; −1 refactored `BridgeInjector` last-file assertion
  now reads `LoadOrder[last]` instead of hard-coded
  `unity_scene.lua`).
* **AOT publish**: 0 warnings, `DiscreteUI.exe` size delta
  negligible (new generator + 2 commands + Lua file are all small).
* **Live smoke**: pending — needs a Mono target + CE + AOBMaker.
  Two-step manual recipe: (1) Tools → Inject Discrete Bridge
  (ships the new helper file into the CT), (2) Methods tab →
  right-click method → Method overloads → tick overload → "Copy
  mono_register AA" → paste into CT entry → enable. Verify
  console banner `mono_registerSymbolEx: registered <name> = <addr>`
  appears.

---

## What landed in this session — Phase 22f live smoke (2026-05-18) caught + fixed 6 P0/P1 bugs

**Setup**: clean Cheat Engine 7.6 + AOBMaker plugin loaded + empty CT + DiscreteUI build #279 + BongoCat (Mono). Walked through [`docs/ce-bridge-integration.md` § "Manual smoke checklist"](ce-bridge-integration.md).

**Outcome**: Steps 1-8 of the 11-step checklist completed green after 6 fix iterations during the 2026-05-18 session. Each iteration surfaced a real production-blocker that the pre-existing unit test suite couldn't catch (live-runtime / multi-process / Lua-parse / Windows-pipe-kernel concerns). **Steps 9-11 confirmed PASS in a follow-up session** — user log captured `[UnityCEDumper.invoke] int(System.Int32) -> System.Int32 = 7 (0ms)` against `Mathf.Abs(-7)` (Step 9 target) on Mono build #304. Phase 22 fully closed.

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

### Steps 9-11 — all PASS (follow-up session)

- ✅ **Step 9** — MethodCallWindow → *Send to CE Table…* → AA entry → enable → CE Lua console printed
  `[UnityCEDumper.invoke] int(System.Int32) -> System.Int32 = 7 (0ms)` against `Mathf.Abs(-7)`. End-to-end
  pipeline (DiscreteUI → AOBMaker → CT entry → `{$lua}` block → bridge → `callUnityMethod` → `prettyPrint`)
  verified clean.
- ✅ **Step 10** — Address Lookup → *Send to CE Table…* → parent AA + child memory record landed in CE
  grid with live field value.
- ✅ **Step 11** — Save → close CE → reopen → re-enable bridge + per-feature entries survived the
  save/reopen cycle (CE serialised the 7 `unity_*.lua` files into the `.CT`; no need to re-run
  Tools→Inject Discrete Bridge).

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

**Phase 22 outcome**: the AOBMaker-based bridge install + per-feature push surface is production-ready against Mono. IL2CPP path is structurally unchanged from Phase 18c (AOB scan against `GameAssembly.dll` — known good). Steps 9-11 closed the remaining smoke; the architectural correctness has been demonstrated end-to-end.

---

> **Older sessions** (Phase 22 a–e build-up, 4-fix live verification,
> Phase 16 docs, Phase 19d-2 polish, Phase 19 vertical + AOT win-only
> build, …) archived in [status-history.md](status-history.md).
> Carved out 2026-05-19 to keep this file readable.

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
| ~~**Phase 12d — 32-bit Mono**~~ | ✅ **All 5 sub-phases closed 2026-05-19.** Build path (CMake + build.ps1 -Arch x86), x86 backend DLL with arch-aware Mono struct offsets (`ManagedLayout.h`), x86 proxy DLLs (`dist/x86/version.dll` + `winmm.dll`), new `inject-x86.exe` cross-bitness helper + `WindowsDllInjector` shell-out, and live-smoke on Legend of Mortal (Unity 2020.3.49f1) all shipped. Standalone helper smoked clean (LoadLibraryW=0x00D70000, OK). | Cosmetic v2 polish open: visually badge 32-bit picker rows; manual bitness toggle in ProxyDeployWindow (auto-detect works). Neither blocks anything. |

### Phase 19 polish (drafted but deferred — would land as 19d-3)

- ~~**Smart-typed editors**~~ ✅ **Shipped 2026-05-19 (Phase 19d-3)** — `MethodCallWindow`'s Value column now picks NumericUpDown / ComboBox / TextBox per row via the new `MethodCallEditorKind` enum. Sized-int params get a `decimal`-backed NumericUpDown bounded to the type's `[MinValue, MaxValue]` so out-of-range input clamps at the editor instead of bouncing back after Call. Enum params kick an async `getEnumValues` probe on window-open (method's own assembly first, then `UnityEngine.CoreModule` for `UnityEngine.*` namespaces); on success the row switches to a ComboBox of `name = decoded` choices and the wire `type` tag is re-routed through the enum's underlying integer type — fixes a latent bug where enums used to fall through to `type:"address"`. Float / double stay on TextBox (decimal can't represent the full double range). Build clean, 163/163 tests pass, AOT publish 0/0 warnings, `DiscreteUI.exe` 38.7 MB. See [plan.md §19d-3](plan.md#phase-19--lua-callable-unity-methods) for the implementation breakdown.

  **Live click-through smoke 2026-05-19 (user-confirmed, multi-target)**:
  * **IL2CPP target** — `MainGame.PARTY_WORK.SetValue(System.UInt32 ofs, System.Byte value)`. NumericUpDown rendered for both rows (up/down spinners visible), seeded `0`, manual entry `6` into `ofs` accepted. Null checkbox correctly disabled (greyed) on both value-type rows — `IsReference == false` so the CheckBox `IsEnabled` is false. **Intended** (value types can't be null in CLR; existing `IsReference` gating from 19d).
  * **Mono target (BongoCat / Unity 2022+)** — `UnityEngine.Mathf.Abs(System.Int32 value)`. NumericUpDown rendered for the Int32 row, manual entry `99` accepted. Null checkbox correctly disabled (value type). Confirms the smart-typed editors work on Mono as well as IL2CPP.
  * **No enum param** was present in either signature, so the ComboBox path is not yet live-exercised. Still pending a smoke against a method with an enum parameter (e.g. `Object.FindObjectsOfType(Type, FindObjectsInactive)` on a Unity 2022+ build).
  * **User feedback addressed same day** — `System.Boolean` params now render an `AutoCompleteBox` with `true`/`false` suggestions (FilterMode=StartsWith, MinimumPrefixLength=0 so the dropdown surfaces on focus) while still accepting any text the existing `bool.TryParse` consumes (case-insensitive, surrounding whitespace tolerated). Picks up `Bool` as a 5th `MethodCallEditorKind`; `IsEditorBool` derived prop drives `IsVisible`. Build + tests + AOT all clean.

### Open follow-ups (not blocking, drip-feed)

Closed in this session, kept here as crossed-out reference until next prune:

- ~~**Multi-row "Copy CE field" only exported the last-focused row**~~ ✅ **Fixed 2026-05-19**. Both the Fields tab DataGrid (`AssemblyBrowserViewModel.CopyCeFieldAsync`) and the Inspector tab DataGrid (`LiveInspectionViewModel.CopyCeFieldNodeAsync`) were reading the singular `SelectedItem` binding when the user had Ctrl/Shift-selected multiple rows. Avalonia DataGrid exposes the full selection via `SelectedItems` (`IList`), but it isn't a bindable property — the VMs now consume a `Func<IReadOnlyList<TRow>>` accessor wired by `MainWindow.xaml.cs` (`GetBrowserSelectedFieldRows` / `GetInspectorSelectedNodes`) that snapshots `DataGrid.SelectedItems`. Both grids gained `SelectionMode="Extended"` (matches the default but makes the contract explicit) + `x:Name="BrowserFieldsDataGrid"` so the code-behind can reach them. Exporter: new `CeXmlExporter.RenderFieldsAsync(IReadOnlyList<FieldEntry>, …)` emits one `<CheatEntry>` per supplied row under a single declaring-class wrapper; the single-row path stays byte-identical to the legacy `RenderFieldAsync` output (now a thin delegate). Inspector path: new private `WrapLeavesXml(IReadOnlyList<WalkedFieldNode>)` replaces the singular `WrapLeafXml` and emits one absolute-address leaf per node — works for heterogeneous selections (top-level fields mixed with expanded sub-tree rows). 4 new xUnit cases in `CeXmlExporterMultiFieldTests`: single-row shape parity, multi-row count + ordering + header label, empty-input ArgumentException. 167/167 tests pass; AOT publish 0/0 warnings.
- ~~**Phase 16 docs**~~ — ✅ closed 2026-05-07. All planned doc files now exist (see Phase Overview table).
- ~~**Phase 5 segment 2d cross-version smoke**~~ — ✅ partially closed 2026-05-07. v29 / v31 / v39 verified; v24 partial (registration + R4/R8 OK, I*/U* decoder bug); v27 still uncovered. See full table in [§Phase 5 segment 2d cross-version smoke](#phase-5-segment-2d-cross-version-smoke) below.
- ~~**Phase 18 multi-overload AA-script user-click smoke**~~ — ✅ closed 2026-05-07 against Farthest Frontier `ItemBundle.AddItems` (2 overloads).
- ~~**Classes list — "Copy class name" right-click menu**~~ — ✅ shipped 2026-05-19 (build #307). Sibling to "Find instances" / "Inspect (find instances)…" on the Classes ListBox. Pushes `ClassItem.DisplayName` (`"Namespace.Name"`, or just `Name` when namespace is empty) to the clipboard via the existing `Browser.CopyToClipboardAsync` `DataTransfer` pattern. Single-select (matches the ListBox's selection mode); multi-select deferred. Release + Tests + AOT Publish all clean (163/163 tests pass).

Genuine open items:

- ~~**Inspector "Class ptr" copy buttons**~~ ✅ **Shipped 2026-05-19** — two small `name` / `addr` buttons next to the Class ptr row in the Inspector header. `name` copies the fully-qualified `<namespace>.<name>` (or bare `<name>` when the namespace is empty); `addr` copies the displayed `0x…` pointer verbatim. Reuses the existing `LiveInspectionViewModel.CopyToClipboardAsync` hook (same `IClipboard.SetDataAsync(IAsyncDataTransfer)` path as Copy CE XML / Copy instance address). Mirrors the tiny-button style from the walked-field row actions (addr/ptr/name/HEX) so the Inspector header stays visually consistent. Localized via `Inspector.CopyClassNameTooltip` / `Inspector.CopyClassAddressTooltip` in `en.axaml`. Tests 163/163 PASS, AOT publish clean.
- ~~**Phase 22 — `.CT` embedding + AA-script generator UI tab**~~ ✅ **Shipped 2026-05-12 across 22a–22f, 6 commits.** Pivoted from "offline `.CT` builder" to "live CT population via AOBMaker plugin" after auditing the plugin's existing pipe protocol. Tools menu "Inject Discrete Bridge" + per-feature Send-to-CE buttons live; 148/148 tests pass; AOT clean. Live click-through smoke against CE 7.6 + AOBMaker + a Unity game still pending (see [`docs/ce-bridge-integration.md` § "Manual smoke checklist"](ce-bridge-integration.md)).
- ~~**Phase 25 — cross-class Symbol Finder**~~ ✅ **Shipped + live-smoked 2026-05-19** (build #363). New `searchSymbols` RPC + MainWindow Tab — see Phase Overview row above for details. **Phase 25b (interesting preset)** still open — needs a Unity-tuned curated keyword list (HP / Damage / Gold / XP / Speed / Health / Mana / Stamina / ...) distinct from UE5CEDumper's FProperty-typed one, plus a per-row score for the existing results grid. ~½ day when prioritised.
- ~~**Phase 21d Inspector field write-back live click-through**~~ ✅ **PASS 2026-05-19** on BongoCat Mono — right-click primitive row → Write value… modal → Apply → readMemory verifies + CE address-list refreshes via the existing AOBMaker bridge.
- ~~**enum ComboBox path live smoke**~~ ✅ **PASS 2026-05-19** via `UnityEngine.Object.FindObjectsByType(Type, FindObjectsInactive, FindObjectsSortMode)` on BongoCat — both enum params surfaced as ComboBox via the Phase 19d-3 `getEnumValues` cascade. Closes the Phase 19d-3 follow-up that had no enum-parameter smoke target.
- ~~**DLL-free Mono register path live smoke**~~ ✅ **PASS 2026-05-19** with `Object.FindObjectsByType` against BongoCat. Surfaced + fixed two production blockers in the same session: user-autorun shadows helper (→ `discrete_*` private namespace); CE Mono returns nil typename on Unity 2022+ (→ `mono_method_getSignature` fallback path). See "What landed" block above + lessons-learned for the forensic.
- **Phase 20 — Scene / GameObject explorer** (parked at 20a, see follow-ups) — **20a journey across one session 2026-05-08 on SaGa F2R**: shipped Lua bridge, then iteratively learnt FOUR cliffs the hard way: (1) wire envelope `{id, result}` unwrap missed; (2) sustained Unity-icall bursts freeze the game (Phase 19 v1 worker-thread limitation); (3) `findInstances UnityEngine.GameObject` returns IL2CPP RGCTX metadata records that fool both Lua filters AND `whatIsAt`; (4) IL2CPP code stripping removes most enumeration APIs — `Resources.FindObjectsOfTypeAll`, `Object.FindObjectsOfTypeAll`, single-arg `Object.FindObjectsOfType`, `SceneManager.get_sceneCount`, etc. all stripped on SaGa F2R's shipped build. Survivors: `Object.FindObjectsOfType(Type, bool)`, `GameObject.Find(string)`, `Camera.get_main()`, `SceneManager.GetActiveScene()` + `Scene.GetRootGameObjects()`. Recipe pivoted to the 2-arg `FindObjectsOfType` survivor. Three new sub-phases opened: **20a-2** (verify the new recipe end-to-end), **20-MT** (DLL-side main-thread marshaling for callMethod-heavy recipes), **20-HEAP** (DLL-side libgc heap walker as the strip-resistant fallback). Three new lessons in [lessons-learned.md](lessons-learned.md): IL2CPP stripping detection, signatureMatch close-paren trap, plus updates to the existing findInstances-false-positives entry. Full design in [plan.md §Phase 20](plan.md#phase-20--scene--gameobject-explorer--objectname-search). **20a-2 closed 2026-05-18** (see next bullet); 20-MT + 20-HEAP remain open.
- ~~**Phase 20a-2 — verify `getAllGameObjects` recipe end-to-end**~~ ✅ **Closed 2026-05-18** via new `tools/Test-FindObjectsOfType.ps1` smoke (4-step recipe replay over raw RPCs — `Type.GetType` → `FindObjectsOfType/ByType` → `walkArray` → `Object.get_name` × N). Cross-backend cross-version PASS: **SaGa F2R** (IL2CPP / Unity 2021) picked the 2-arg `FindObjectsOfType(Type, bool)` overload, 14,942 GameObjects in scene, sampled names came back as real `SoundObject_SeCh11..15` instances, 0 native_crashes. **BongoCat** (Mono / Unity 2022+) had **0** `FindObjectsOfType` overloads (the entire API was removed in Unity 2022+, not just deprecated), automatically fell through to `FindObjectsByType(Type, FindObjectsInactive, FindObjectsSortMode)` — the 3-arg compiled overload. 40,752 GameObjects, sampled names "Background" ×5, 0 native_crashes. **Bonus finding**: the two enum params on the Unity 2022+ API marshal as plain `int32` over the wire (ABI is identical to underlying type — no DLL `enum` tag needed). Lua bridge `getAllGameObjects` `tries[]` grew a third entry (3-arg ByType for Unity 2022+); first-match-wins keeps production calls on the historically-preferred path per Unity version. Phase 23's heap-scan filter is also indirectly validated by this run (0 native_crashes on real instance pointers, no stack/RGCTX residue surfaced).
- ~~**Phase 21 — Inspector field write-back**~~ ✅ **Shipped a/b/c + 21d live click-through PASS 2026-05-19**. v1 (primitives + raw address) is the entire shipped scope; v2 (reference-type writes — string + object pointer with proper GC-root marshaling) deferred until a user actually needs it. Full design in [plan.md §Phase 21](plan.md#phase-21--inspector-field-write-back).
- ~~**v24 integer-decode bug**~~ ✅ **Fixed + live-verified 2026-05-08** (commit `cb3d100`, build #251 against FF VI / Unity 2019.4 / v24) — added per-version dispatch hooked off `mp.version()`. v24/v27 → raw LE branch; v29+ → existing sentinel + compressed-int branch. Both branches emit `encoding: "raw_le"` / `"sentinel_compressed"` in the response. R4/R8/Bool/Char unaffected (always raw LE). Verified: `Int32.MaxValue` → 2147483647 (was -2147483648), `Int32.MinValue` → -2147483648 (was 0), both with `enc=4B` confirming the raw LE branch fired.
- ~~**Unity 2018.x missing IL2CPP exports**~~ ✅ **Fixed + live-verified 2026-05-08** (commit `587e794`, build #251 against R-Type Dimensions EX / Unity 2018.1 / v24) — `il2cpp_image_get_class_count` + `il2cpp_image_get_class` demoted from REQUIRED to OPTIONAL in `IL2CPPResolver`. `EnumerateClasses` now has a fallback path that walks `MetadataParser::GetTypeDefinition()` and calls `il2cpp_class_from_name(image, ns, name)` per type — null returns implicitly filter by image. Cost: O(N_global_typedefs) per call, slower than the native iterator but functional. **Verified**: `assemblies_count=36` (was 0), `findInstances UnityEngine.GameObject` returned 100 matches with `class_ptr` populated, scan took only 529 ms — the fallback enumeration is fast enough for production use, not a slow-path warning.
- ~~**`findInstances` IL2CPP `classPtr` empty**~~ ✅ **Fixed + live-verified 2026-05-08** (commit `3e519a8`, build #251 against Hollow Knight Silksong / Mono / Unity 6000.0) — was actually a stale STATUS note; IL2CPP did populate `class_ptr`. Real issue was Mono returning only `vtable` while consumers expected `class_ptr`. Fix: Mono findInstances now also returns `class_ptr` (additive, keeps `vtable` for compat). Verified: Silksong run printed `class_ptr : 0x000002201761E3D0` (no fallback to `vtable`), 100 Transform instances. Both backends now expose the canonical field; pipe-protocol.md updated to spell out "scan target = `vtable` on Mono, `class_ptr` on IL2CPP".
- ~~**JSON Inf/NaN value coercion**~~ ✅ **Fixed + live-verified 2026-05-08** (commit `fd428d2`, build #251 against SaGa F2R / IL2CPP / v29) — R4/R8 `il2cppGetMetadataTypeFields` decode now emits `value_kind`: `"normal"` / `"infinity"` / `"-infinity"` / `"nan"`. `value` stays `null` for non-finite (JSON spec compliance), `value_kind` recovers the semantic. Older clients ignoring `value_kind` see the same behaviour as before — purely additive. Verified: `System.Single.PositiveInfinity` → `value:null, value_kind:"infinity"`; `NegativeInfinity` → `"-infinity"`; `NaN` → `"nan"`; finite floats (MinValue/MaxValue/Epsilon/NegativeZero) all stamp `"normal"`.
- ~~**`findInstances` heap scan includes thread-stack + IL2CPP RGCTX-metadata false positives**~~ ✅ **Fixed + live-verified 2026-05-18 (Phase 23, build #301)** against BongoCat (Mono). `ScanForPointer` now filters `MEM_IMAGE` regions (DLL `.data` / `.bss` — eliminates RGCTX + Mono class tables) and thread stacks (`NtQueryInformationThread` + TEB walk). The "managed heap range" framing was wrong: `il2cpp_gc_get_heap_section_count` doesn't exist in any vendor `il2cpp-api-functions.h` (2017.1 → 6000.0). The negative-filter approach (exclude module + stack) is runtime-agnostic, works on Mono + all IL2CPP versions, and kills both documented false-positive sources. Live smoke on BongoCat: 1194 regions → 571 scanned (366 MEM_IMAGE + 257 thread stacks excluded). New `scan_stats` field in wire response + `scan` log category. Deprecated `_isValidInstance` removed from `scripts/unity_scene.lua`. Full forensic in [lessons-learned.md](lessons-learned.md).
- ~~**v27 verification target**~~ ✅ **Verified 2026-05-20** against **The Legend of Tianding / 廖添丁** (IL2CPP / Unity 2020.3.48f1 / x64) — the long-missing v27 target finally appeared in the Steam library. End-to-end PASS: handshake (81 assemblies, hook armed), Phase 20-MT stress smoke (1000 calls × Mathf.Abs, 8.33 ms/call main-thread, drain_count=1000, 0 timeouts), `Test-MetadataFields System.Int32` decodes `MaxValue` → 2147483647 + `MinValue` → -2147483648 (raw-LE 4-byte path, `enc=4B`), `Test-MetadataFields System.Single` decodes `MinValue` / `Epsilon` / `MaxValue` correctly; `PositiveInfinity` / `NegativeInfinity` / `NaN` show "(no decode)" in the smoke script but that's the JSON Inf/NaN value-coercion fix (build #251 commit `fd428d2`) — the wire carries `value_kind` correctly, the test script just doesn't surface it. **Confirms the v24/v27 → raw-LE / v29+ → sentinel-compressed-int dispatch (build #251 commit `cb3d100`) works on v27 the same as on v24.** No remaining decoder bugs on v27.
- ~~**v32+ metadata.dat parser**~~ ✅ **Shipped + live-smoked 2026-05-19** (build #336) — v32-v39 dispatch via new `TypeDefinition_v35`, `Il2CppSectionMetadata` + `HeaderV38` section reader, `IndexSizes` runtime-variable-width plumbing. `tools/Test-MetadataVersions.ps1` synthetic-fixture smoke PASS on v29/v31/v35/v37/v38/v39 + rejects v40. **Live smoke on Songs of Silence (Unity 6.1.x / v39)**: 40,501 TypeDefinitions + 44,047 string literals decoded; `Vector3` full field walk recovered all 15 expected fields with correct tokens + type indices. Full design + landed-vs-deferred breakdown in [il2cpp-metadata-crosscheck.md §"v35-v39 status — SHIPPED"](il2cpp-metadata-crosscheck.md).
- **Phase 17e — Strategy B address finder** — only-if-asked. Cached heap walk + interval tree for batch CE pointer-scan lookups. Skip until requested.
- **More automated test coverage** — ✅ Inspector classifier + HighlightSegmenter shipped 2026-05-08 (build #257, 5 → 57 tests). Big gaps still open: end-to-end `MetadataParser` round-trips per version, CE-XML live-mode rendering against a deterministic fixture, AobMakerBridge happy + sad-path with a fake pipe server. Drip-feed.
- ~~**Highlight matching substring**~~ ✅ **Shipped 2026-05-08** (build #257) — new `HighlightedTextBlock` control + `HighlightSegmenter` pure-logic helper. Wired into Assemblies / Classes / Fields (Name + Type) / Methods (Name) lists. Element-name binding from each filter `TextBox` so the live filter value flows into every row without VM plumbing. Inspector field grid (which has no filter — drives off `walkInstance` results) deliberately untouched.
- ~~**Fields / Methods per-cell copy-to-clipboard menu**~~ ✅ **Shipped 2026-05-19** — right-click any Browser Fields-tab row → 3 new items (Copy field name / Copy type / Copy "Declared in"); same on the Methods-tab row (Copy method name / Copy address / Copy "Declared in"). Address substitutes for return-type since `MethodRow` doesn't carry one — return type only comes from the heavier `FindMethodInstances` RPC. Handlers in [`MainWindow.axaml.cs`](../ui/UnityDumpUI/Views/MainWindow.axaml.cs) reuse the existing `CopyToClipboardAsync` (`DataTransferItem` + Avalonia 12 clipboard pattern). Single-select only; multi-select join-with-newline is a v2 follow-up (noted inline in XAML comment). 163/163 tests pass; AOT publish clean.
- **CSX live mode** — design dead-end as scoped (CSX is offset-relative; "live" only makes sense as snapshot-derived layout). CE XML already provides true absolute-address live mode. Defer until a user explicitly asks.

---

## Suggested first steps for the new session

Pick whichever fits the slot of time you have. Nothing on this list is blocking.

| Option | Effort | Why pick this |
|---|---|---|
| ~~**Phase 19d-3 + 5 follow-ups + 21a**~~ | — | ✅ All shipped 2026-05-19. See "What landed in this session" above. |
| ~~**DataGrid AOT sort trap**~~ | — | ✅ Diagnosed + fixed 2026-05-19. Three-layer trap, lessons-learned + technical-notes carry the full mechanism + recipe for the next DataGrid. |
| ~~**Phase 21 — Inspector field write-back**~~ | — | ✅ a/b/c shipped 2026-05-19 (build #333). **21d live click-through still pending** — fastest next step: launch BongoCat (Mono) or SaGa F2R (IL2CPP), inject DiscreteUI, walk an instance, right-click a primitive field, "Write value…", verify CE address-list updates in real time. Recipe in [plan.md §Phase 21](plan.md#phase-21--inspector-field-write-back). |
| **Phase 20-MT — main-thread marshaling for `callMethod`** | ~1 day | Sustained Unity-icall bursts can freeze the game (Phase 19 v1 worker-thread limitation surfaced during Phase 20a recipe testing). Design in [plan.md §Phase 20](plan.md#phase-20--scene--gameobject-explorer--objectname-search). Needed before heavy-fanout recipes like "name every GameObject in the scene" can run reliably. |
| ~~**Phase 12d (32-bit Mono)**~~ | — | ✅ **Fully closed 2026-05-19** — all 5 sub-phases shipped + live-smoked on Legend of Mortal. Cosmetic polish remains (ProcessPickerWindow 32-bit badge, ProxyDeployWindow manual bitness toggle) but underlying functionality complete. |
| ~~**v32+ metadata.dat parser**~~ | — | ✅ Shipped + live-smoked 2026-05-19 (build #336) against Songs of Silence (Unity 6.1.x / v39). 40k TypeDefs + 44k string literals + Vector3 full field walk all PASS. |

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
