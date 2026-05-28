# UnityCEDumper — Development Plan

> 跨 PC 共用的開發進度文件。每完成一個小步驟就把 `[ ]` 改成 `[x]` 並 commit。
> 所有里程碑驗收條件 (Acceptance) 必須通過才能往下一階段。

**Last updated**: 2026-05-26 night — **Phase 29b (Snapshot UI + Diff) SHIPPED + LIVE-VERIFIED across all three Mono / IL2CPP targets, build #637**. Phase 29a + 27c also closed. Five iterations of Mono-freeze debugging — the root cause turned out to be `mono_class_vtable` called from our worker thread leaving BDWGC tracking entries that the game's main thread had to GC catch-up on. Fix: skip static field VALUE capture on Mono (metadata only). See lessons-learned.md §"Phase 29a-v4 — mono_class_vtable from worker thread wedges game main thread post-RPC".
**Current phase**: **✅ Phase 28b-v3.0 shipped + LIVE-VERIFIED, ⚠ v3.1 attempted but hit v29 layout dead-end** (build #593). v3.0 lazy-init slot scanner works for slots that have been populated by ANY init mechanism — `SteamManager.s_instance` returns 11 chain-exact matches with no regression. v3.1 attempted to map never-populated slots to classes statically via token decode but discovered three layout hypotheses all fail on v29: (1) `types[]` index → out of range (29456 < 50999); (2) typeDefIndex via per-image typeStart + il2cpp_image_get_class → out of range (10001 < 50999); (3) `Il2CppMetadataRegistration.metadataUsages[]` is **NULL** in v29 (entire indirection system removed). v29's per-method init protocol uses a different lookup mechanism (likely `Il2CppMetadataUsageList` in global-metadata.dat) whose decode needs proper reverse-engineering — deferred to v3.2 (2-4 days). v3.0 + diagnostic infra (`diagnoseClassReferences`, `dumpLazyInitSlots`, `IL2CPPLazyInitSlotScanner`, `MetadataUsagesArray()`, `Test-LazyInitSlots.ps1`) all in place. Banner advisory + lessons-learned.md document the dead-end + v3.2 cookbook for next attempt.

**Previous current-phase**: **✅ Phase 28b IL2CPP root-cause CONFIRMED + v1 advisory shipped** (build #555-#557). Build #546's diagnostic logging paid off: live re-run on SaGa F2R proved `il2cpp_class_get_static_data` returns a runtime-heap-allocated pointer outside GameAssembly.dll. **v1 fix C** ships a yellow advisory banner explaining the limitation; **Phase 28b-v2** designed + shipped same session (build #565). Also: Symbol Finder "Static only" toggle + per-asm limit in multi-asm scans, PipeServer disconnect-time GC drain.

**Previous current-phase**: **✅ Phase 20e LIVE-VERIFIED on 活俠傳** (2026-05-25) + **🚧 Phase 28b SaGa F2R 0-match diagnostic logging shipped** (build #546). Phase 20e fully closed via 活俠傳 live-smoke.

**Previous current-phase**: **✅ Phase 27 SHIPPED + LIVE-VERIFIED + 5 follow-up polish commits** (build #465). One known minor open issue: dual-monitor window-restore stays at maximize bounds instead of snapping back to pre-maximize Position+Size. Not blocking.

**Previous current-phase**: **✅ Phase 27 — Unity-aware Value Search SHIPPED + LIVE-VERIFIED** (all sub-phases 27a-27e). Build #452, 283/283 xUnit pass, AOT clean. Three production-bug follow-ups landed post-ship: (1) `SessionManager` heap-leaked to skip ~400K destructor calls at process exit; (2) `MonoAddressFinder` / `IL2CPPAddressFinder` caches also heap-leaked; (3) **Reentrant `ThreadAttachGuard`** — 4000+ per-WhatIsAt attach cycles were accumulating BDWGC finalizers that ran on game shutdown, hanging 活俠傳 for 44 s post-scan. Now: outer guard at scan-handler level + reentrancy counter make inner guards no-ops, batch goes from 4000 cycles → 1. Shutdown verified back to ~1 s on 活俠傳. New lesson recorded in [lessons-learned.md](lessons-learned.md). Live-smoked on SaGa F2R (IL2CPP) + 活俠傳 (Mono x86) for the DLL-level RPCs. UI Value Search tab landed; pending user click-through to verify the cross-tab handoff + DataGrid sort. Closes the long-standing "CE has value search, we don't" gap with a Unity-aware twist: every candidate row carries class/field/instance context (via whatIsAt), and the **`Mortal.Core.GameStat._value` vs `PLY_WORK_SAVEDATA.purse`** distinction (active state vs persisted state) emerges naturally from the refine workflow — CE alone cannot tell them apart. v2 candidates: 27f (libgc heap-section pre-filter) would close Phase 20-HEAP and accelerate global scans ~10x.

**Previous phase**: **🚧 Phase 27 — Unity-aware Value Search (27a shipped, 27b next)**. **27a live-smoked on three targets**: SaGa F2R (IL2CPP x64, global scan for Int32=100000 found `MainGame.PLY_WORK.purse` at +0x18 ✨), BongoCat (Mono x64, class-scoped scan), and 活俠傳 (Legend of Mortal, Mono x86, Unity 2020.3.49f1 — global scan reliably identified `Mortal.Core.GameStat._value` across UI state changes). One production bug discovered + fixed mid-smoke: `Mono::BuildClassRangeCache` was hammering `mono_class_vtable` for all ~17K classes in a single ThreadAttachGuard scope, wedging legacy 32-bit Mono when the game's main thread was focus-paused; rewrote as lazy vtable→klass resolution in `WhatIsAt` (cache build now ~500ms with zero runtime calls; per-candidate +1 SafeReadBytes deref, memoized after first hit). Lesson recorded in [lessons-learned.md](lessons-learned.md). Build #423.

**Original planning**: **🚧 Phase 27 — Unity-aware Value Search (planning)**. Sub-phases 27a-27f drafted (~6.5 day v1 estimate). No code yet. **Phase 28** (Field → Methods cross-reference) added as TODO stub — picks up after Phase 27 ships. Inspired by `D:\Github\AOBMaker`'s `WindowsValueScanner.cs` pattern but reuses existing Discrete primitives (`Memory::EnumerateCommittedRegions` + `whatIsAt` filter + `findInstances`) so no new scan engine needed. Two scan modes: **global** (CE flow — scan + whatIsAt filter) + **class-scoped** (Discrete flow — `listFieldValuesAcrossInstances` + value predicate, ~100x faster). Full CE filter mode set (exact / range / changed / unchanged / increased / decreased / new value). Includes Phase 17b carry-forward (Array / List<T> element resolution in `whatIsAt`). v2 sub-phase 27f closes Phase 20-HEAP simultaneously via libgc heap-section binding. Previous landings (2026-05-20 early/mid): **Phase 26 anti-tamper protection** — 3-slot SHA256 self-check spread across Core + UI assemblies (`BuildArtifactToken` / `ExportFormatToken` / `WindowsRuntimeMetadata`) + 2 poison surfaces (CT export offsets + Inspector grid values, 1/11 deterministic) + XOR-shielded footer credit + `inject-hash.ps1` 3-slot AOBMaker-style stamping + new `USAGE.md` end-user guide. Mid-session (same day): **Phase 20-MT main-thread marshaling** (MinHook detour on `runtime_invoke` + dispatcher queue) live-PASS on 5 targets; **Phase 20b/c/d Scene Explorer tab** + cross-tab handoff. Carry-forward from 2026-05-19: Phase 25 cross-class Symbol Finder, Phase 5 v32-v39 metadata parser (live-smoked on Songs of Silence v39 / 40k TypeDefs), Phase 12d 32-bit Mono (Legend of Mortal x86), Phase 21d Inspector write-back live PASS, 19d-3 smart-typed editors, 21a reverse-property lookup, DataGrid AOT sort trap fix, 22g DLL-free Mono register. **No critical-path open work.** Verified targets: SaGa F2R (IL2CPP v29 / x64), BongoCat (Mono Unity 6.0+ / x64), Farthest Frontier (both backends / x64), Songs of Silence (IL2CPP v39 / Unity 6.1 / x64), R-Type Dimensions EX (IL2CPP v24 / Unity 2018.1 / x64), FF VI PR (IL2CPP v24 / Unity 2019.4 / x64), Hollow Knight Silksong (Mono / Unity 6.0 / x64), **LEAVES (Mono / Unity 5.4 / x86 ✨)**, **Legend of Mortal (Mono / Unity 2020.3 / x86 ✨)**. Build #408 (DiscreteUI.exe AOT 39.4 MB after 3-slot guardian, DLL sizes unchanged from #376; 283/283 tests pass + AOT round-trip verified end-to-end). See `STATUS.md` for the detailed hand-off; this file is the canonical phase ledger.

**2026-05-07 renumbering**: the address-finder track was originally tagged Phase 18 with sub-phases 17a-17d, a holdover from when "find method instances" sat at Phase 17. After the address-finder shipped, the two phases were swapped so the sub-phase letters match the parent number — the track that just closed is now Phase 17 (matching its 17a-17d nodes), and find-method-instances moves to Phase 18. No runtime / wire-protocol changes; everything that previously said "Phase 18 sub-phase 17X" now reads "Phase 17 sub-phase 17X" (or simply "Phase 17X").


## Hand-off notes (historical — see archive)

Detailed sub-phase write-ups for shipped Phases 17 through 28 have
been carved into [plan-archive-phases-17-28.md](plan-archive-phases-17-28.md)
on 2026-05-29 to keep the active planning doc readable. The
**Phase Overview** table below still summarises every phase; the
archive holds the long-form plans / sub-phase ledgers / open
follow-ups. Phase 0–16 was archived earlier in
[plan-archive-phases-0-16.md](plan-archive-phases-0-16.md).

For the most recent session hand-offs (build #680+ with Phase 29c/d),
see [STATUS.md](STATUS.md); older hand-offs are in
[status-history.md](status-history.md).

---

## Phase Overview

| # | Phase | Goal | Status |
|---|-------|------|--------|
| 0 | Bootstrap | Repo skeleton, build script, logging | ✅ |
| 1 | Shared C++ Foundation | Memory / Logger / ModuleHelper / Pipe server | ✅ |
| 2 | Backend Detection + Handshake | Auto-detect Mono/IL2CPP, return engine info | ✅ |
| 3 | Mono Backend | Resolver → Domain walk → Class walk → Field/Method | ✅ |
| 4 | IL2CPP Backend (API) | Resolver → Domain walk → Class walk via exports | ✅ |
| 5 | IL2CPP Backend (Metadata) | global-metadata.dat parser (v24/27/29/31/35-39) | ✅ v24/v27/v29/v31 dispatch live (Phase 14c); **v32-v39 dispatch shipped + live-smoked 2026-05-19 (build #336)** — flat→section header transition + variable-width metadata indices + v35 struct-field removals; synthetic-fixture PASS on v29/v31/v35/v37/v38/v39; live PASS on Songs of Silence (v39, 40,501 TypeDefs + 44,047 string literals + Vector3 full field walk). IL2CPPMetadataRegistration AOB cross-link still optional ("2d follow-up", non-blocking) |
| 6 | Pipe Protocol Core Commands | getAssemblies / getClasses / getClassInfo / etc. | ✅ |
| 7 | UI Skeleton (Avalonia + AOT) | Single-instance app, pipe client, handshake | ✅ |
| 8 | UI Panels — Tree & Browse | AssemblyTree, ClassBrowser, FieldStruct, MethodList | ✅ |
| 9 | Live Inspection | InstanceFinder, LiveWalker, StaticField, read/write memory | ✅ |
| 9.5 | In-app picker + DLL injector | Replaces `Test-Handshake.ps1` for launch-and-attach | ✅ |
| 10 | Export Formats | CE XML, CSX (Structure Dissect), SDK header | ✅ UI-side; DLL-side `FieldExporter` + `exportCe*` pipe commands deliberately skipped (UI does it locally) |
| 11 | CE Lua Bridge | unity_dump.lua, unity_dissect.lua, unity_symbols.lua | ✅ Both bridge scripts (dissect + symbols) verified live; deploy tooling shipped |
| 11.5 | Inspector inline drilldown | Hierarchical pointer / array / value-struct expansion | ✅ |
| 12 | Proxy DLLs | version.dll / winmm.dll / dxgi.dll variants + bulk deploy panel | ✅ x64 (12a-12c) + ✅ **x86 + cross-bitness inject (12d, live-smoked 2026-05-19)** |
| 12d | 32-bit Mono support | x86 build of backend + proxy DLLs, cross-bitness inject, x86/x64 selection in installer | ✅ **All sub-phases (12d-1..d-5) shipped + live-smoked 2026-05-19** on Legend of Mortal (Unity 2020.3.49f1 / Mono bdwgc / x86). Build: parallel x86 CMake path via `build.ps1 -Arch x86` (outputs to `dist/x86/`). Struct-offset arch-awareness: new `dll/src/shared/ManagedLayout.h` exposes `kObjectHeader` / `kStringLengthOffset` / `kArrayDataOffset` / `kExceptionMessageOffset` as constexpr-of-`sizeof(void*)`; all hard-coded `0x10`/`0x14`/`0x18`/`0x20` byte offsets in `ExportAPI.cpp` + `MonoMethodInvoker.cpp` replaced. New `tools/inject-x86/` cross-bitness inject helper (15.5 KB standalone x86 exe; classic Win32 inject + clean exit codes). `WindowsDllInjector` shells out to `dist/x86/inject-x86.exe` when target is WOW64; `MainWindowViewModel` routes the DLL path to `dist/x86/UnityDumper-Mono.dll` for 32-bit Unity targets. Live PASS via proxy-DLL drop AND via the new inject helper: pipe handshake (backend=Mono, unity_version=2020.3.49f1, 114 assemblies), 19 Transform instances via `findInstances` (ScanForPointer with 4-byte ptr), `walkInstance` decoded `m_CachedPtr` at offset 0x08 = correct x86 MonoObject header, `monoCompileMethod` returned real JIT address, `inject-x86.exe` standalone reported `wow64=true LoadLibraryW=0x00D70000 OK` and pipe handshake confirmed the injection took. Cosmetic v2 polish still open: ProcessPickerWindow doesn't visually badge 32-bit rows (the `IsWow64` flag flows through but the grid just shows "Mono"); ProxyDeployWindow lacks a manual x86/x64 toggle (auto-detect works). Neither blocks anything. |
| 13 | Offline Metadata CLI | Standalone tool to parse global-metadata.dat | ✅ Ships as `dist/metadata-cli.exe` |
| 14 | Hardening | Error paths, AOT trim warnings, log rotation, edge cases | ✅ All sub-items closed (14a-14f) |
| 14c | IL2CPP metadata version dispatch | v24 / v27 / v29 / v31 TypeDefinition layouts | ✅ Cross-checked vs upstream Unity (`docs/il2cpp-metadata-crosscheck.md`) |
| 15 | Test Matrix | Mono / IL2CPP across multiple Unity versions | ✅ Verified across 6+ games covering v24/v27/v29/v31 + Mono bdwgc Unity 6 |
| 16 | Docs Finalization | dll-spec / pipe-protocol / ui-spec / internals docs + READMEs + CONTRIBUTING | ✅ Closed 2026-05-07 — all planned files now exist (lessons-learned, README.md + zh-TW, CONTRIBUTING.md, proxy-dlls, metadata-cli, export-formats, mono-internals, il2cpp-internals, dll-spec, ui-spec, technical-notes) |
| 17 | Address Finder (reverse lookup) | New wire command `whatIsAt(address)` returning instance + class + field; inverse of `findInstances` | ✅ All four sub-phases shipped (17a DLL handler / 17b field resolution / 17c UI Address Lookup tab / 17d CE Lua bridge). Spec in [address-finder.md](address-finder.md). |
| 18 | Find Method Instances | New wire commands `findMethodInstances` + `getMethodAobPattern` + UI overloads modal + AOB AA-script generator + AOBMaker `CreateAAScript` direct push | ✅ All four sub-phases shipped (18a DLL handler + signature decoding / 18a-3 hand-rolled x64 LDE for AOB wildcarding / 18c UI overloads picker + AobAaScriptGenerator + AOBMaker direct push / 18d `unity_symbols.lua` extension `registerMethodOverloads`). |
| 19 | Lua-callable Unity methods | New wire command `callMethod(asm, ns, class, method, [args], [instance])` invoking Mono / IL2CPP methods with marshaled primitive + string args; closes the long-standing CLAUDE.md goal | ✅ All five sub-phases shipped (19a IL2CPP DLL / 19b Mono port / 19c instance dispatch + string in/out / 19d UI MethodCallWindow / 19e `scripts/unity_invoke.lua` Lua bridge). Verified live across SaGa F2R + BongoCat + Farthest Frontier. Spec: [lua-invoke.md](lua-invoke.md). |
| 20 | Scene / GameObject explorer + Object.name search | `scripts/unity_scene.lua` Lua bridge + DiscreteUI Scene tab + filter-by-name + click-to-inspect handoff. Tree-walk primitives gated behind a NEW sub-phase **20-MT** (DLL-side main-thread marshaling) since iterating Unity icalls from the worker thread freezes the game. | 🚧 In progress — 20a landed + hardened 2026-05-08 (envelope unwrap fix + main-thread caveat after live test on SaGa F2R locked up the game). `getName` / `getAllGameObjects` / `findByName` are safe-and-working primitives; `getActiveSceneRoots` / `getChildren` / `getComponents` formally **EXPERIMENTAL until 20-MT ships**. 20b (UI tab) blocked behind 20-MT. Zero DLL changes so far; 20-MT will need DLL frame-hook work. |
| 21 | Inspector field write-back | New wire RPC `setInstanceField(addr, fieldOffset, type, value)` + SEH-protected `SafeWriteBytes` on both backends + UI Inspector field "Write value…" right-click + CE Lua `setField` wrapper | ✅ 21a (DLL handler + smoke), 21b (UI modal + 37 new tests + AOT clean), 21c (Lua bridge + docs) shipped 2026-05-19. **21d live click-through PASS 2026-05-19** on BongoCat Mono (right-click primitive Inspector row → Write value… modal → Apply → readMemory verifies + CE address-list refreshed). v1 covers primitives + raw address; reference-type writes deferred to v2 (GC-root marshaling). |
| 22 | Live CT Population via AOBMaker | One-click "Inject Discrete Bridge into CE Table" (Tools menu) + per-feature "Send to CE Table" buttons. Pivoted away from offline `.CT` build (Ascii85 + XML serializer scrapped) — uses AOBMaker CE plugin's pipe protocol (`InjectTableFile` + `CreateAAScript` + `CreateMemoryRecord` + `CreateAAScriptWithRecords`) to mutate the currently-open CT. User saves via CE File→Save. | ✅ **All sub-phases (22a–22f) shipped 2026-05-12 + live smoke fully closed 2026-05-18.** 22a `AobMakerBridge` extended (4 new commands, `IsAvailable` event); 22b 7 bridge lua files embedded in Core; 22c `BootstrapAaScriptGenerator` template; 22d Tools menu RelayCommand + `BridgeInjector` orchestrator; 22e `CallMethodAaScriptGenerator` + `AddressLookupAaScriptGenerator` per-feature buttons; 22f `docs/ce-bridge-integration.md` + `tools/Test-BridgeInject.ps1` smoke. **Live click-through smoke**: Steps 1-8 captured + fixed 6 P0/P1 bugs mid-session; Steps 9-11 (MethodCall→CE / AddressLookup→CE / save+reopen) PASS in follow-up session — user log `Mathf.Abs(-7) → 7` confirms the AA-script → bridge → `callUnityMethod` chain. 163/163 xUnit pass, AOT 0/0 warnings. **DLL-free Mono register path live-smoked 2026-05-19** with the Phase 22e `MonoRegisterSymbolAaScriptGenerator` on BongoCat — surfaced + fixed two production blockers (user-autorun `_G.mono_*` shadowing → `discrete_*` private namespace; CE Mono nil typename on Unity 2022+ → `mono_method_getSignature` substring fallback). |
| 25 | Cross-class Symbol Finder | New `searchSymbols` pipe RPC + Symbol Finder Tab in MainWindow. Walks every class in a named assembly for fields + methods whose name contains an ASCII case-insensitive substring. Row double-click navigates back to Browser Fields/Methods tab with the class + member filter pre-selected. Replaces the historical "click each class in turn" loop the user reported when hunting properties like `MaxHP` across a few-hundred-class assembly. | ✅ **Shipped + live-smoked 2026-05-19** (build #363) on BongoCat Mono — pattern `MaxHP` → `MainGame/CALC_WORK.maxHp` (`System.Int32` @ `0x2C`) in < 1s. 3 new fake-pipe `SearchSymbolsTests`. Properties surfaced via `get_*`/`set_*` methods (Mono/IL2CPP runtime convention; no separate property row kind). 25b (Interesting preset — curated Unity keyword scan + scoring, mirror of UE5CEDumper's Interesting Properties) deferred until basic finder shakes out — needs a Unity-tuned keyword list distinct from UE5CEDumper's FProperty-flavored one. |
| 27 | Unity-aware Value Search | CE-style First Scan / Next Scan but filtered to Unity managed instances only. New `scanFieldValue` + `refineValueScan` pipe RPCs; reuses `Memory::EnumerateCommittedRegions` + `whatIsAt` (Phase 17) to drop non-managed candidates. Two scan modes: **global** (CE flow — type + value → walks all committed RW pages, filters via `whatIsAt`) + **class-scoped** (Discrete flow — pick class first, equivalent to `listFieldValuesAcrossInstances` plus value filter; orders-of-magnitude faster). Supports int8-64 / uint8-64 / float / double / string (managed UTF-16). Full CE filter set: exact / range / changed / unchanged / increased / decreased / newValue. New Value Search tab (MainWindow position 6). Includes **Phase 17b carry-forward** — Array / List<T> element resolution in `whatIsAt`. | ✅ **All shipped sub-phases live-verified 2026-05-20** (builds #410→#452). 27a (global + class-scoped + whatIsAt filter) live-smoked on SaGa F2R + 活俠傳; 27b (Begin/Refine/End session + 7 CE filter modes) live-smoked on SaGa F2R (purse=100000 → spent 1 coin → newValue=99999 narrowed to PLY_WORK.purse, dropped SAVEDATA.purse correctly); 27a-bugfix (lazy vtable resolution) closed the 32-bit Mono hang; 27c (Array / List<T> element resolution); 27b-4 (UTF-16 / UTF-8 / ASCII string scan via Memory::ScanForString); 27d (UI Value Search tab — VM + DataGrid + cross-tab handoff to Inspector). 27e (CE Lua bridge `unity_valuescan.lua`) **dropped** 2026-05-20 — CE already has its own native value scan; nobody needs to call our scan from CE Lua, so the bridge has no realistic use case. 283/283 xUnit pass. |
| 28 | Field → Methods cross-reference (TODO stub) | Inverse of Symbol Finder's "find by name": given a field, list methods that read/write it. **Mono path**: walk method IL bytecode (`mono_method_header_get_code`) for `ldfld`/`stfld`/`ldsfld`/`stsfld` referencing the target field token — high-confidence, clean. **IL2CPP static fields**: reuse AOBMaker's `RipReferenceScanner` against the field's runtime address — scan executable pages for RIP-relative references, attribute hits to enclosing methods via Phase 18a range-check. **IL2CPP instance fields**: heuristic — AOB-scan declaring-class methods for `[reg+offset]` patterns matching field offset; mark "low confidence" in UI. | 📝 **TODO / not started** — picks up after Phase 27 ships. Approach drafted below. |
| 29 | Snapshot & Diff (跨 session 數值監視) | "全抓" snapshot walker (default scope = all user assemblies, opt-in narrow) + diff UI + SQLite cross-session persistence. Strict superset of CE's value-finder — instead of "find addresses with value X", surfaces "any field that changed" with class.field.instance context attached. **Bundles Phase 27c live smoke** — array element capture exercises the existing `whatIsAt` array resolver on SaGa F2R / BongoCat / 活俠傳; bugs fixed on both sides. Sub-phases 29a (DLL `snapshotAll` RPC + array smoke), 29b (in-memory diff UI), 29c (SQLite + cross-session). 29d (SPC-style multi-field constraint query) deferred until 29a-c stabilise. | ✅ **29a + 29b + 29c + 29d SHIPPED 2026-05-29 (29a/b LIVE-VERIFIED 2026-05-26; 29c/d green on 457 unit tests, live-smoke handed off; 29d adds SpcQueryEngine + SPC Query tab for N-snapshot constraint queries — kills CE's two-snap diff limitation)** across SaGa F2R / BongoCat / 活俠傳 (builds #596 → #637 — five iterations of Mono-freeze debugging, root cause was `mono_class_vtable` from worker thread leaving BDWGC tracking entries; fix: skip static-field VALUE capture on Mono). Phase 27c array-element live-smoke **CLOSED** as bundled side-effect. **29c (SQLite) + Phase 29 polish (noise-reduction filters on diff result) next**. |
| 26 | Anti-tamper protection | 3-slot SHA256 self-check spread across two assemblies + 2 poison surfaces (CT export offsets + Inspector grid values) + XOR-shielded credit string + AOT-stamped hash via `tools/inject-hash.ps1`. Inspired by AOBMaker's pattern but redesigned for cross-assembly slot distribution (Slot A in Core/Services / Slot B in Core/Services/Export / **Slot C cross-assembly in ui/UnityDumpUI/Platform**). Dev-mode bypass (0xCC placeholders) keeps all factors at 0 in Debug/Release single-file builds — exports + Inspector + tests stay byte-identical to pre-Phase-26. Class names renamed to non-suggestive forms so binary metadata no longer leaks "Integrity" / "Guardian" / "Shield". | ✅ **Shipped + AOT-verified 2026-05-20** (build #408). Two iterations: `ccaae6e` single-slot baseline + `348cbb6` 3-slot cross-assembly upgrade. `TokenAggregator.Combined() = A \| B \| C` ORs all three factors (UI registers `WindowsRuntimeMetadata.Read` into Core via `Program.Main`). Poison 1: `CloakOffset(off) => off ^ (factor & 0x7F)` in `CeXmlExporter` + `CsxExporter` shifts every emitted offset 0-127 bytes (CT loads cleanly in CE; reads wrong fields). Poison 2: `LiveInspectionViewModel.MaybePerturbDisplay` corrupts 1/11 numeric `DisplayValue` rows via per-row deterministic seed (string `GetHashCode` XOR offset XOR salt) — same row corrupts same way within session, different rows across sessions. Anti-trace: `Read()` returns 0 for first N calls (A:3 / B:2 / C:4) even when tampered. Anti-debug: `Debugger.IsAttached` on published binary returns poison code. AOT round-trip verified end-to-end: 3 sentinels at `0x02438F98` / `0x02438E80` / `0x0243ABF8` (C ~11 KB away from A/B). `inject-hash.ps1` 3-slot AOBMaker-style: zero all 3 slots → SHA256 once → write same hash into all 3. Footer credit `© bbfox @ https://opencheattables.com` decoded from XOR blob at VM construction. New [`USAGE.md`](../USAGE.md) end-user guide for CT makers. |

---

> **Phases 0 → 16** (bootstrap → both backends → live walking → UI
> skeleton → exports → Lua bridge → proxy DLLs → metadata CLI →
> hardening → test matrix → docs finalization) live in
> [plan-archive-phases-0-16.md](plan-archive-phases-0-16.md). Carved
> out 2026-05-19 — those phases are all shipped + closed long ago,
> never need editing. New work appends a new phase below.

---


## Phase 29 — Snapshot & Diff (跨 session 數值監視) ✅ 29a + 29b + 29c + 29d SHIPPED 2026-05-29

**Goal**: Capture a wide "everything" snapshot of the running game's
managed state, diff two snapshots, surface what changed with class /
field / instance context attached. Strict superset of CE's value-finder
flow — instead of "find addresses with value X" the user gets "any
field that changed" automatically labelled.

### Design pivot — scope inversion

Earlier drafts started from "user picks a class". Wrong. The user
typically **does not know where the data lives** — that's the whole
problem we're solving. Forcing per-class selection means the user
has to repeat the whole flow per class. Default scope is therefore
"全抓 — all user assemblies"; narrowing to a single class is opt-in.

| Mode | Default | Opt-in |
|---|---|---|
| **Scope** | All user-assembly classes (static + any with live instances) | Single class / set of classes |
| **Type filter** | All primitives + string + enum + inline struct + array / `List<T>` | Toggle per-type |
| **Instance cap per class** | 100 | 0–unlimited |
| **Array element cap** | 50 | 0–unlimited |
| **Assembly excludes** | `mscorlib`, `System.*`, `UnityEngine.CoreModule` base types | Editable |

### Why this is the real value-finder superset

CE first-scan / next-scan requires: knowing the *type* (int? float?),
knowing the *approximate value*, and running one scan per type. Our
snapshot diff requires: nothing. One click before action, one click
after, diff result auto-labelled with `class.field`. Information
density is an order of magnitude higher (CE gives N nameless
addresses, we give N rows tagged with `MainGame.PLY_WORK.hp:
500→350 (Δ-150)`).

### Sub-phases

- [x] **29a — `snapshotAll` DLL RPC** ✅ **Shipped + live-verified 2026-05-26**, builds #596→#598→#602.
  - New pipe RPC `snapshotAll(scopeFilter?, instanceCap?, arrayCap?)`
    on **both** backends.
  - Returns `{snapshotId, capturedAt, gameBuildId, gameSessionId,
    classes: [{fqn, assembly, statics: [...], instances: [{addr,
    fields: [...]}]}]}`.
  - Reuses existing `dumpClassInfo` bulk RPC (Phase C, build #496)
    for metadata + field layout. Reuses `findInstances` per class
    (capped at `instanceCap`). Reuses `getStaticFieldData` for
    statics. **No new metadata-walking code**.
  - **Bundles Phase 27c live smoke**: when the walker hits a `T[]`
    or `List<T>` field, it calls the existing `whatIsAt` array
    element resolver to capture length + per-element values. Bugs
    surfaced here get fixed on both sides (snapshot walker + the
    `whatIsAt` array path).
  - Default `scopeFilter` excludes `mscorlib`, `System.*`,
    `UnityEngine.CoreModule`. User can toggle.
  - `gameBuildId` derived from `SHA256(GameAssembly.dll)` prefix —
    enables 29c cross-session safety check.
  - Estimated data size: 50–200 MB JSON for SaGa F2R-scale (~3,700
    classes). Walk time: 5–15s. Acceptable for explicit user action,
    *not* for continuous polling.

- [x] **29b — In-memory snapshot store + diff UI** ✅ **SHIPPED + LIVE-VERIFIED 2026-05-26** (builds #604 → #637). Snapshot tab (MainWindow position 7) with capture controls, A/B selector, filter checkboxes (Hide synthetic types ON / Changed only ON / Show added / Show removed / Class filter substring), diff DataGrid with sort comparers (AOT-safe per §4.5 of aot-pitfalls.md), context menu (Inspect / Address Lookup / Copy address/class/field), cross-tab handoff. **Strategy C** filter (local UI synthetic-type hiding by `<` prefix) confirmed working. Cross-game stale-marking via `game_build_id` in handshake. **Five iterations of Mono-freeze debugging** to reach a stable state — culminated in the v4-round-4 fix: skip static field VALUE capture on Mono (mono_class_vtable from worker thread is the root cause of the post-RPC main-thread wedge). Metadata still captured. Live-verified on SaGa F2R (IL2CPP) full diff + BongoCat (Mono x64) responsive post-snapshot + 活俠傳 (Mono x86 / 1.1 GB heap walk) responsive post-snapshot.
  - New `Snapshot` tab in MainWindow (position 7).
  - "Take Snapshot" button → fires `snapshotAll` → stores in
    `ObservableCollection<SnapshotEnvelope>` (in-memory only at
    this stage).
  - Diff algorithm:
    - Static field key: `(class_fqn, field_offset)`.
    - Instance field key: `(class_fqn, instance_addr, field_offset)`
      — instance addr is stable *within* a session.
    - Array diff: element-by-element, output rows like
      `inventory[2]: 50 → 75`.
    - String diff: full string old vs new (capped display length).
  - Diff result DataGrid with columns
    `Class / Field / Instance / Old / New / Δ / %Δ`.
  - Filter checkboxes: changed-only (default on), added, removed,
    by class regex.
  - Right-click row → existing cross-tab handoff (Inspector /
    Address Lookup / Copy address).
  - No DB dependency at this stage.

- [ ] **29c — SQLite cross-session persistence** (~5 days, finalized 2026-05-29)
  - **Goal**: snapshots survive UI restart + game relaunch. Per-game
    DB. Cross-session diff is honest about address instability — static
    fields diff cleanly, instance fields suppressed by default
    cross-session (v1; identity matching pushed to 29d).
  - **5 decisions confirmed with user 2026-05-29**:
    1. Auto-save **OFF** by default + global toggle in Settings.
    2. **Per-game DB** keyed by `game_build_id` (FNV-1a of mono / IL2CPP
       module path+size+write_time, already produced by DLL — see
       [SnapshotSession.cpp:96](../dll/src/shared/SnapshotSession.cpp)).
       Filename `{exe_basename}_{build_id[0:8]}.db` for human readability.
    3. **v1 drops instance fields cross-session** — banner explains why.
    4. **Lazy load** — startup reads metadata only; class blobs loaded
       on first diff selection.
    5. **Auto-prune** older snapshots when DB > N (default 200 MB —
       gzip per-class compresses ~50× vs raw JSON so this comfortably
       holds 100+ wide snapshots; lowered from initial 1 GB on
       2026-05-29 after live observation),
       always keeping the latest 1. `VACUUM` when freed page ratio >30%.
       Threshold 0 = off.
  - **NuGet** (NativeAOT-safe): `Microsoft.Data.Sqlite.Core` 10.0.0 +
    `SQLitePCLRaw.bundle_e_sqlite3` 3.0.4. Adds ~1-1.5 MB to AOT
    publish. Needs `SQLitePCL.Batteries_V2.Init()` early in
    `Program.Main`.
  - **Default DB location**: `%LOCALAPPDATA%\Discrete_UEDumper\snapshots\`.
    Settable via Settings dialog → persisted to
    `%LOCALAPPDATA%\Discrete_UEDumper\settings.json` (new user-settings
    infrastructure, also AOT-safe via `[JsonSerializable]` source-gen).
  - **Schema** (hybrid: normalized metadata + per-class gzipped JSON blob —
    reuses existing `SnapshotClassEntry` DTO, no new mapping code):
    ```sql
    CREATE TABLE snapshots (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        label TEXT, captured_at_utc TEXT NOT NULL,
        game_build_id TEXT NOT NULL, game_session_id TEXT NOT NULL,
        target_process TEXT NOT NULL, backend TEXT NOT NULL,
        unity_version TEXT, ptr_size INTEGER,
        total_classes INTEGER NOT NULL, total_instances INTEGER NOT NULL,
        field_value_count INTEGER NOT NULL,
        instance_cap INTEGER, array_cap INTEGER,
        include_unchanged INTEGER NOT NULL DEFAULT 0,
        asm_filter TEXT,
        payload_bytes INTEGER NOT NULL DEFAULT 0  -- for prune accounting
    );
    CREATE INDEX ix_snapshots_captured ON snapshots(captured_at_utc DESC);

    CREATE TABLE snapshot_classes (
        snapshot_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
        assembly TEXT NOT NULL, class_fqn TEXT NOT NULL,
        instance_count INTEGER NOT NULL, static_count INTEGER NOT NULL,
        payload_gzip BLOB NOT NULL,
        PRIMARY KEY (snapshot_id, assembly, class_fqn)
    );
    CREATE INDEX ix_classes_fqn ON snapshot_classes(class_fqn);

    CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
    -- schema_version, target_process, game_build_id seeded on create
    ```
    Open with `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL`.
  - **Cross-session diff semantics** (unblocks current
    [SnapshotViewModel.cs:655](../ui/UnityDumpUI/ViewModels/SnapshotViewModel.cs) guard):
    | Scenario | Behavior |
    |---|---|
    | Same `session_id` (live) | Current 29b flow, no change |
    | Different `session_id`, same `build_id` | Auto statics-only + blue banner "Cross-session diff: instance fields suppressed (addresses change per launch). [Force include] [Learn more]" |
    | Different `build_id` | Statics-only + yellow banner "⚠ Cross-build diff — static offsets may have shifted" |
  - **Sub-phases**:
    - [ ] **29c-1** SnapshotStore + schema + migration runner + in-memory unit tests (1 day)
    - [ ] **29c-2** UserSettingsStore (atomic JSON, source-gen, AOT-safe) (0.5 day)
    - [ ] **29c-3** UI startup wiring — per-game DB resolution, lazy metadata load, switch on reconnect (0.5 day)
    - [ ] **29c-4** 💾 Save / Delete commands per envelope + AutoSave hook + unsaved-snapshot warning on app close / DB switch (0.5 day)
    - [ ] **29c-5** Cross-session / cross-build diff banner + force-include checkbox + remove existing block (0.5 day)
    - [ ] **29c-6** Auto-prune algorithm + VACUUM heuristic + status notification (0.5 day)
    - [ ] **29c-7** Snapshot tab status line ("DB: name · 7 snapshots · 124 MB / 200 MB · 📁 Open folder · ⚙ Settings") + folder picker via `IStorageProvider` + open folder via `Process.Start("explorer.exe", path)` + Settings modal (0.5 day)
    - [ ] **29c-8** Tests (round-trip / migration / prune boundary / cross-session detect) + live-smoke on 活俠傳 + SaGa F2R + BongoCat (relaunch between snaps → confirm stale flag + statics-only + per-game DB switch) + docs (1 day)
  - **Folder picker strategy**: `IStorageProvider.OpenFolderPickerAsync`
    for choosing dir (modern Avalonia 12 API), `Process.Start("explorer.exe", path)`
    for one-click "open this folder" button. Two APIs each suited to its
    job.
  - **Unsaved-snapshot UX**: app close or DB switch with any
    `IsDirty=true` (in-memory but not persisted) envelope → modal
    "You have N unsaved snapshots. Save them? [Save all] [Discard]
    [Cancel]". Prevents accidental loss when user toggled AutoSave OFF.
  - **Known risks + mitigations**: WAL handles crash-safety; cross-DB
    Mutex prevents race during DB switch; remote-drive (OneDrive/SMB)
    paths warned but not blocked; settings corruption → fallback to
    defaults + log warning (non-fatal at startup).

- [x] **29d — SPC-style multi-snapshot constraint query** ✅ **SHIPPED 2026-05-29**
  - **Killer use case** (verbatim from user): given 4 snapshots from
    the same game build, find every field whose value sequence walks a
    specific signature — e.g. `10160 → 9910 → 9410 → 9410` uniquely
    locates the 銀兩 GameStat by its real-world spending pattern
    (bought 250, bought 500, slept-no-change), filtering out all noise
    fields that happen to land in plausible ranges.
  - **6 sub-phases, ~4.5 days total**:
    - [x] **29d-1** `SpcQueryEngine` Core service + 22 unit tests.
      `SpcPredicate{Any|Exact|Range|NotEqual|Unchanged|Increased|Decreased|DeltaExact|DeltaRange}` —
      absolute predicates work on any index; relative predicates
      require index > 0 (compare vs previous snap's value). "Increased
      by N" / "Decreased by N" intentionally not exposed as separate
      kinds — `DeltaRange [-N,-N]` or `DeltaRange [-max,-min]` covers
      the same surface without bloat. `Run()` anchors on smallest
      FieldIndex, walks intersection, evaluates per-predicate per-key,
      first-failure short-circuits.
    - [x] **29d-2** `SpcQueryViewModel` + "SPC Query" tab (MainWindow
      position 8). Shares the Snapshot tab's envelope pool — saves /
      drops visible to both tabs. Per-snapshot row DataGrid (Include
      checkbox + ComboBox PredicateKind + two operand TextBoxes).
      Filters (StaticsOnly / ExcludeSynthetic / NumericOnly / Class /
      Type). Big Run button. Results DataGrid with per-snap "Values"
      column rendered via `SpcValueSequenceConverter` (`v0 → v1 → v2`).
    - [x] **29d-3** Cross-session / cross-build auto-mode mirrors
      Phase 29c-5 diff: sessions differ → auto-enable StaticsOnly +
      blue banner; builds differ → yellow warning banner.
    - [x] **29d-4** Non-additive RAM: post-Run, unload envelopes the
      VM itself hydrated for the query (envelopes that were already
      loaded — e.g. via Snapshot tab — stay loaded). N>5 advisory
      status line with estimated peak RAM. Real per-key streaming
      (constant RAM regardless of N) deferred to optional future
      optimization; current behavior is fine for typical N≤10.
    - [x] **29d-5** Result row context menu: Inspect this instance
      (cross-tab to Inspector) / Copy Class.Field / Copy instance
      address / Copy value sequence. Same `CopyToClipboardAsync`
      delegate pattern the other tabs use.
    - [x] **29d-6** 11 ViewModel integration tests + plan.md +
      STATUS.md updates. Total Phase 29c+d coverage: 457/457 tests.

- [x] **29e — Class-centric Key/Value pivot** — COMPLETE (29e-1 … 29e-4 funnel + 29e-5/6/7/8/9 polish: every flat DataGrid on the AOT-safe CustomSortComparer pattern [arrows work], Inspector tree-aware sort, DB-side class enumeration. 550/550 tests. Deferred: Layer B reference-array inner pivot [needs DLL], field-name pre-hydrate, anomaly auto-surface)
  - **User insight (2026-05-29)**: SPC's `{asm}|{fqn}|{instAddr}|{offset}|{field}`
    key collapses across game sessions (address drift) and even within
    a session when instance order drifts (活俠傳 銀兩 GameStat lands at
    #123 or #124 depending on walk). Real game data has stable
    business keys *inside* each instance (`_propertyID`, `_itemGroup`),
    so pivoting on key field values instead of addresses sidesteps
    both drift problems at once.
  - **Design doc**: see [class-pivot.md](class-pivot.md) for the
    full spec — 3-stage funnel (Stage 0 class shortlist by CV /
    null ratio / field-type ratios → Stage 1 field-level key
    auto-discovery → Stage 2 PivotEngine), array element handling
    (Layer A primitive index expansion now, Layer B reference inner
    pivot deferred), cross-session banner repurposing, schema
    impact (none — existing `.db` files keep working).
  - **DB compatibility**: zero migration. `BuildFieldIndex` is
    in-memory only; payload_gzip stores raw `SnapshotClassEntry[]`
    with full `SnapshotArrayElements` intact, so 29e ships on every
    existing saved snapshot without re-capture.
  - **Sub-phases** (each ships green on the full test suite, with a
    STATUS.md TL;DR refresh):
    - [x] **29e-1**  `PivotEngine` + unit tests (no UI). 16 tests
      shipped at build #684, 478/478 green. `SnapshotEnvelope.FormatFieldValue`
      promoted from `private` to `internal` so the engine reuses the
      shared formatter. Engine handles single + compound keys, collisions
      (HasCollision flag + `<N: v1, v2>` cell rendering), absent cells,
      cross-session (engine is address-agnostic by design), KeyFilter,
      assembly/FQN match, chunked SnapshotClassEntry merge,
      missing-key sentinel, duplicate-field-name first-wins.
    - [x] **29e-1b** Layer A: primitive array index expansion in
      `BuildFieldIndex` (`{addr}|{offset}|inventory[3]` pseudo-keys).
      SPC tab inherits per-element tracking for free. 10 tests at
      build #685, 488/488 green. New `ExpandArrayElements` +
      `FormatArrayElement` helpers in `SnapshotEnvelope` — mirrors
      FormatFieldValue's ladder for element-level rendering. Statics
      + instance fields handled identically. Reference-array slots
      render as `ptr=…` rows (Layer A doesn't dive into element
      fields). Edge cases tested: empty array, ArrayError, cap-hit
      (Length > Count), element-level error, non-array fields
      unaffected.
    - [x] **29e-2**  "Class Pivot" tab — Stage 2 only, manual class +
      K + V picker, repurposed cross-session banner. New tab at
      MainWindow position 9 ([MainWindow.axaml](../ui/UnityDumpUI/Views/MainWindow.axaml),
      [PivotViewModel.cs](../ui/UnityDumpUI/ViewModels/PivotViewModel.cs)).
      18 ViewModel tests at build #687, 506/506 green. Shares the
      snapshot envelope pool with Snapshot + SPC tabs.
      AutoCompleteBox suggestions for Class FQN + field names sourced
      from hydrated snapshots. Cross-session banner reads as a
      feature ("Cross-session pivot — keying by (k1, k2) sidesteps
      address drift") rather than the SPC warning. Cross-build still
      a yellow warning. Results render via a single AOT-safe
      "Trajectory" column (CompiledBinding compatible) — same
      mental model as SPC's "v0 → v1 → v2" cell, with multi-line
      "fieldName: …" prefix for multi-value-field queries.
      DoubleTapped + 4-entry ContextMenu (Inspect / Copy key tuple /
      Copy value sequence / Copy instance address latest).
    - [x] **29e-3**  Stage 0 class shortlisting DataGrid (CV / null %
      / field-type ratios) — sortable shortlist of stable,
      instance-bearing classes. [ClassShortlistEngine.cs](../ui/UnityDumpUI/ViewModels/ClassShortlistEngine.cs)
      (pure engine, 12 tests) + Stage-0 Expander in the Class Pivot
      tab + 4 ViewModel tests. 524/524 green. Per class: Presence,
      InstancesPerSnap, Avg/StdDev/CV (headline stability), FieldCount,
      Primitive/String/Array/Null field ratios, IsSynthetic. Default
      view filters to full-presence + non-synthetic + FieldCount≥2,
      sorted Presence↓ / CV↑ / FieldCount↓. Assembly filter +
      show-synthetic / show-partial toggles. Double-click a row →
      fills Class FQN + scopes assembly → flows into Stage 2.
      `SnapshotEnvelope.IsSyntheticClassName` promoted to internal
      for reuse. **29e-7**: class + assembly autocomplete now seed from
      DB metadata on connect (`SnapshotStore.ListDistinctClassesAsync`,
      no blob hydrate), so the Class FQN box works before any Analyze.
      The shortlist metrics still hydrate on Analyze (they need the
      actual instance data).
    - [x] **29e-4**  Stage 1 field-level key auto-discovery
      (S_stable + S_cardinality + TypePrior + NamePrior) + greedy
      compound-key suggestion. [KeyDiscoveryEngine.cs](../ui/UnityDumpUI/ViewModels/KeyDiscoveryEngine.cs)
      (pure engine, 10 tests) + Stage-1 Expander in the Class Pivot
      tab (+5 ViewModel tests). 539/539 green. Per field: S_stable
      (avg pairwise Jaccard of per-snap value sets), S_cardinality
      (distinct/instance avg), TypePrior (int=1.0 / string·enum=0.85 /
      float=0.2 / bool=0.1 / ref=0.05 / array=0), NamePrior
      (id/name/key/hash/type/group/code substrings), composite
      Score=0.4/0.3/0.2/0.1. Greedy compound-key: starts from the
      top field, adds fields until the key is unique in every snap or
      a 3-field cap — directly answers the "_property=99 collides"
      problem by proposing (_type, _property) etc. UI: Score fields
      button + filter + suggested-key label + Apply, scored DataGrid
      (sortable by every sub-score), double-click → add as key,
      right-click → add as value. The filterable field picker the
      user asked for during 29e-2 smoke.
  - **Deferred to follow-up Phase**: Layer B (reference-type array
    elements pivot by inner key — needs DLL-side capture of element
    `Fields[]`) — **now planned as Phase 29f below**; anomaly
    auto-surface (stretch) still deferred.

### Bundled Phase 27c live smoke ✅ **CLOSED 2026-05-26**

Phase 27c (array / `List<T>` element resolution in `whatIsAt`) shipped
DLL-side at build #433 but never got a live-game smoke. Phase 29a's
snapshot walker hits the same code path on every `T[]` / `List<T>`
field it encounters, so live targets become smoke targets for both
features simultaneously:

| Target | What it exercised | Result |
|---|---|---|
| SaGa F2R (IL2CPP v29 / x64) | Multiple `T[]` fields — `MainGame/DR_ENV.code: UInt32[2451]`, `MainGame/HalfReader.buff: Byte[0]`, `MainGame/DIRENTRY.system: SByte[0]` | ✅ 7,804 successful captures, 1,228 open-generic `T[]` placeholders correctly filtered, 53K false-positive arrays flagged as `arrayError` (synthetic-type "instances") |
| BongoCat (Mono / x64) | `SteamInventoryTest.m_SerializedBuffer: Byte[1900545]`, `BattleRoyale._roundDuration: RoundDuration[95]`, `SelectionMenu._options: Option[]` | ✅ 44 successful captures + 1,109 `arrayError` (mostly false-positive `SelectionMenu._options` instances) |
| 活俠傳 (Mono x86 / Unity 2020.3) | `MultiKeyDictionary\`3.buckets: Int32[786438]`, `.entries: Dictionary.Entry<T1,Dictionary<T2,T3>>[2]` | ✅ 14 successful captures with 32-bit pointer width working correctly |

**Rule**: any bug surfaced by 29a's array path gets fixed on **both**
sides (snapshot walker + the original `whatIsAt` array resolver). One
issue surfaced + fixed: open-generic placeholder type names like `T[]`
were being treated as real arrays and reading garbage at random
pointer offsets (reported length values in the 10^9–10^12 range).
Fix: only attempt array decode when `typeName` contains `.` or `/`
(real types are namespaced; open generics aren't).

### Out of scope (deferred — not v1)

- `Dictionary<K,V>` / `HashSet<T>` internal bucket walker (would
  need hash table decode).
- `Task<T>` / `Lazy<T>` async wrapper unwrap.
- Reference-type **recursive** content expansion — v1 records only
  the pointer address; user manually opens Inspector for content.
- Cross-build offset reconciliation — different game versions get
  a warning banner, no auto-translation.
- Phase 29d (constraint query / SPC) — deferred until 29a–c
  validated by real use.

### Phase 29 polish — TODO (deferred from 2026-05-26 live-smoke)

Filed after 29a + 29b shipped + user reported the diff result has
too much noise to spot the signal. None blocks v1; all are quality-
of-life improvements over the existing "Hide synthetic types +
Changed only + Class filter" stack.

- [x] **Noise-reduction filters on diff DataGrid** — SHIPPED 2026-05-26.
  Filter bar in [MainWindow.axaml](../ui/UnityDumpUI/Views/MainWindow.axaml)
  Snapshot tab expanded to two rows; collapsible right-side stats
  panel hangs off the diff DataGrid. Backing logic in
  [SnapshotViewModel.cs](../ui/UnityDumpUI/ViewModels/SnapshotViewModel.cs).
  - Value range filter (numeric Min/Max, "either side in range"
    semantics) — bound via `NumericUpDown` to `decimal?` so null =
    unbounded.
  - Pointer-mask toggle — keys off the new `DiffRowKind.Pointer`
    classifier (both Old and New start with `ptr=`). Re-allocation
    noise drops out in one click.
  - Field-name substring filter symmetric to the existing Class
    filter (`FieldFilterText`).
  - Distinct-count stats panel (top-30 field names by occurrence,
    sorted desc). Double-click a row or right-click "Exclude this
    field name" on the diff grid → name lands in `ExcludedFieldNames`
    and disappears from results. "Clear exclusions" button + an
    "Apply field exclusions" master toggle.
  - Large-string mask (default ON, threshold 256 chars) — drops
    `System.Exception._stackTrace`-class blobs without needing a
    name pattern. Threshold is user-editable in the same filter bar.

  **Design fixes folded in same edit**:
  - `SnapshotDiffRow` gained `Kind` / `NumericOld` / `NumericNew` /
    `MaxValueLength` populated once at construction, so every noise
    filter is an O(1) predicate.
  - Replaced `ChangedOnly` with three independent
    `ShowChanged` / `ShowAdded` / `ShowRemoved` toggles (defaults
    `ON, OFF, OFF`); dropped dead-code branches.
  - `FilteredDiffRows` materialized into `ObservableCollection<>` —
    single O(N) rebuild on any filter-property change, was lazy
    `IEnumerable` re-evaluated per render.
- [x] **Capture cap defaults bumped + live memory tracking** —
  SHIPPED 2026-05-27 (`d18443f`). User's 活俠傳 GameStat money
  instance at `0x29B0ACE8` sat past position 100 in the heap-walk
  order; prior 100-instance default silently missed it.
  - `InstanceCap` 100 → 512, `ArrayCap` 50 → 256 in
    [SnapshotViewModel.cs](../ui/UnityDumpUI/ViewModels/SnapshotViewModel.cs).
  - Sample `Process.WorkingSet64` at `TakeSnapshotAsync` entry; live
    ticker (200ms cadence) shows current value + Δ from start:
    `Capturing… 12.3 s — process 1.42 GB (+340 MB)`. Same
    annotation lands in the streaming-chunk status + final
    snapshot status + log line.

- [x] **DLL hard-cap raise** — SHIPPED 2026-05-27 (`06984bf`, DLL
  build #643). Defaults stay at 512/256; the raise just gives
  users room to push higher.
  - `ExportAPI.cpp` snapshotBegin Mono + IL2CPP: instanceCap
    `> 1024` → `> 8192`, arrayCap `> 256` → `> 4096`.
  - `MonoAddressFinder.cpp` + `IL2CPPAddressFinder.cpp`
    `FindAllInstances` `perClassCap` ceiling 4096 → 8192 so
    the internal cap doesn't silently clip the new ExportAPI
    ceiling.
  - `MainWindow.axaml` `NumericUpDown.Maximum` 1024→8192 and
    256→4096; tooltips updated.
  - `MagicStrings.h` snapshotBegin doc-comment updated.

- [x] **Cross-tab Address Lookup bug** — SHIPPED 2026-05-27
  (`db8bb9b`). `TabIndexAddressLookup` / `TabIndexSymbolFinder`
  constants in [MainWindowViewModel.cs:324](../ui/UnityDumpUI/ViewModels/MainWindowViewModel.cs)
  were inverted relative to XAML TabItem order; every Address
  Lookup cross-tab handoff has been silently routing to Symbol
  Finder since Phase 25. Surfaced when the new snapshot-diff
  right-click "Find what's at this address" menu item exposed it.
  Constants swapped + comment added so future tab insertions
  notice the sync requirement.

- [x] **32-bit snapshot OOM mitigation — options #1 + #2 +
  pre-snapshot abort gate SHIPPED 2026-05-27** (DLL build #645).
  Options #1 (auto-clamp on x86) + #2 (surface game memory) +
  the user's bonus ask ("can we abort if approaching 2 G/4 G
  boundary?") landed in one bundle.
  - **DLL**: new `QueryProcessMemory` helper using
    `GetProcessMemoryInfo` returns `workingSet` / `privateUsage`
    / `pointerSize` (= `sizeof(void*)` in the game's address
    space). Wired into `BuildHandshakeJson` (so UI sees bitness
    + current memory on every connect) and both `snapshotBegin`
    handlers (Mono + IL2CPP) as a fresh post-decode sample in
    the response.
  - **DLL pre-check abort gate** at the top of each
    `snapshotBegin`: if pointerSize == 4 AND privateUsage ≥
    3 GB, return a structured error refusing to run the
    snapshot. Threshold chosen as 4 GB ceiling − ~1 GB
    safety margin for our own JSON cache. The 活俠傳 build
    #646 CTD becomes a clean "Game process at 3.2 GB private
    memory — refusing snapshot to avoid OOM crash" instead.
  - **UI Core**: `HandshakeResult` + `SnapshotBeginResult`
    gained `GamePointerSize` / `GameWorkingSet` /
    `GamePrivateUsage` JSON-bound properties.
  - **UI VM**: `SnapshotViewModel.OnGameConnected(ps, ws, pu)`
    called from `MainWindowViewModel.ConnectAsync` after every
    handshake. On 32-bit targets, clamps InstanceCap down to
    256 + ArrayCap down to 64 if currently above (never
    clamps upward — manual user picks stay respected).
  - **UI banner**: `GameMemoryBanner` property surfaces
    "Game: 32-bit Mono — WorkingSet 1.42 GB, committed 1.78 GB
     — snapshot caps auto-clamped for safety". Suffix
    escalates to "⚠ approaching 4 GB ceiling" at 2 GB and
    "⛔ over 3 GB — DLL will refuse next snapshot" at 3 GB.
    New Border bound to `IsNotNullOrEmpty` so the banner
    only appears post-handshake.
  - **Status enrichment**: streaming-chunk + final-snapshot
    status lines now show game-process memory alongside the
    UI process memory.

  **Option #3 (streaming-chunk architecture)** stays deferred —
  the pre-check refusal closes the hard-CTD path, and the
  auto-clamp keeps snap #2 inside safe range on the common
  32-bit cases. Streaming only matters if a user wants to
  push past 256/64 caps on x86 (and the DLL refusal banner
  will make that obvious).

- [x] **Snapshot `FindAllInstances` false-positive root-cause fix**
  — SHIPPED 2026-05-28 (DLL #650, commit `b4bd935`).
  See [lessons-learned.md §"Snapshot FindAllInstances 2-level scan accepts non-object memory"](lessons-learned.md#snapshot-findallinstances-2-level-scan-accepts-non-object-memory-as-fake-instances--eats-per-class-cap-before-reaching-real-instances--proper-fix-2026-05-28-dll-650)
  for the full diagnostic + design walk.

  **Root cause**: bulk heap walk did a 2-level scan — for each
  qword, dereference and check whether the deref is in the
  known class set. That accepts not just real `MonoVTable*` but
  also non-vtable Mono structures whose first qword happens to
  be a class pointer (RGCTX cache slots, internal lookup
  tables that store vtable pointer values as their payload,
  etc.). Per-class `findInstances` doesn't have this problem
  because it does a 1-level scan against a specific vtable
  address.

  **Fix**: validate the candidate vtable structurally. Real
  `MonoVTable` has the root `MonoDomain*` at offset
  `2 * sizeof(uintptr_t)` (per
  `mono/metadata/class-internals.h`). One extra SEH-safe
  pointer read per candidate, no Mono runtime calls, zero
  BDWGC interaction (so zero risk of the build #637 wedge
  recurring).

  **Live-verify on Mortal (活俠傳, Mono x86)**:
  - GameStat capture: 256 (cap hit, mostly garbage) → 134-135
    (matches live `findInstances` ~152)
  - UI memory per snapshot: 1.5+ GB → ~99-139 MB (15× reduction)
  - Total UI memory for 2 snapshots: 3+ GB → ~238 MB
  - Money diff at known-stable address 0x29BFACC0 finally
    surfaces as proper `changed` row: `_value 10160 → 9910
    (Δ=-250)`
  - Diff totals dropped to reasonable order
    (1,688 changed vs prior 7,110+ noise floor)

  **Residual (Phase 29f polish — deferred)**: ~1 stray entry
  per snapshot in 0xB2Dxxxxxx range — probably a per-method or
  generic-instantiation `MonoVTable` alias family that
  coincidentally also has a domain pointer at the right offset.
  ~99% precision, not blocking. Could refine further by also
  validating `*(qw + 3*ptr_size)` looks like a `MonoType*`, but
  diminishing returns unless a user complains.

  **Companion impact**: yesterday's 32-bit OOM mitigation
  bundle (auto-clamp + DLL refusal gate + memory banner) stays
  as safety net but is much less essential now. With the noise
  floor gone the user can push `InstanceCap` back to 512+ on
  32-bit Mortal without hitting OOM.

- [ ] **Phase 29e — Full / Differential snapshot architecture**
  (proposed by user 2026-05-27, **DEFERRED**). UI-side feature
  that adds a second snapshot kind so multi-snapshot workflows
  don't bloat UI memory linearly.

  **Design (user-supplied)**:
  - Two snapshot kinds: `Full` (today's behaviour) and
    `Differential`. Each Diff is anchored to exactly one base
    Full and only stores fields whose value differs from the
    base. Typical size: 1–5% of base.
  - Rules: Diff cannot be based on another Diff (no chains).
    Dropping a Full cascade-drops every Diff anchored to it.
  - Cross-pair compare logic:
    | A kind | B kind | Behaviour |
    |---|---|---|
    | Full | Full | direct compare (current) |
    | Full | Diff (base = A) | direct — Diff already IS the delta |
    | Full | Diff (base ≠ A) | reconstruct virtual Full from Diff+base, then compare |
    | Diff | Diff (same base) | direct delta-vs-delta, missing key on either = unchanged |
    | Diff | Diff (different base) | reconstruct both virtual Fulls, then compare |
  - The `Unchanged` row toggle is only available on `Full vs Full`
    pairs (Diff has already discarded its unchanged data).

  **What it solves**: UI-side multi-snapshot memory bloat. After
  10 snapshots on 活俠傳-scale the UI would hold ~16 GB; with
  differential it's ~2 GB (one Full + 9 small deltas).

  **What it doesn't solve**: capture-time game OOM on x86 (DLL
  still does a full heap walk + decode + JSON build each time;
  the OOM gate from `35ab309` handles that). Pipe bandwidth
  also unchanged.

  **Cost estimate**: ~2–3 days of code + ≥1 live-smoke round.
  Touches `SnapshotEnvelope` (kind + base ref), `Diff()` (five
  cases instead of one), `DropSnapshot*` (cascade), filter
  panel (Unchanged availability gating), DisplayName (kind +
  base linkage rendered in dropdown), persistence path (29c
  has to know how to store the relationship).

  **Inspiration**: Oracle DB Materialized View incremental
  refresh — same shape, just no mlog (we have the base full
  snapshot directly instead of a change journal).

  **Recommendation**: defer until a real workflow needs 5+
  snapshots in one session. The current 2-snapshot
  Full+Full diff is well-served and the OOM mitigation
  bundle from `35ab309` removes the immediate pressure that
  prompted this discussion.

- [ ] **Mono static field VALUE capture** (optional, user-opt-in
  checkbox in Snapshot tab): currently statics on Mono only
  capture metadata (per build #637 fix). A "Capture static values
  (may freeze game briefly)" toggle would let users get static
  diff when they accept the freeze trade-off.
- [ ] **Phase 29c — SQLite cross-session persistence** — original
  29 sub-phase. With 29b's stale-marking via `game_build_id` in
  handshake, the persistence layer just needs to key snapshots
  by `(game_build_id, captured_at, label)` and load the matching
  ones on reconnect.
- [ ] **Phase 29d — SPC-style multi-field constraint query** —
  long-deferred design.

### Acceptance criteria

- **29a** ✅ — `snapshotAll` against SaGa F2R Assembly-CSharp scope
  completes in 28-29s on build #602 (full decode + heap walk; chunks
  are pure JSON slicing after that). 73K instances captured, 7,804
  arrays decoded successfully. **Closes Phase 27c live-smoke** —
  array element walker verified on three games.
- **29b** — Two in-memory BongoCat snapshots taken before/after an
  in-game action surface ≥ 1 field change with correct class.field
  label in the diff DataGrid.
- **29c** — SQLite-persisted snapshot survives game restart;
  static-field cross-session diff works on the same `game_build_id`.
- **27c live-smoke** ✅ **CLOSED 2026-05-26**.

### v1 effort estimate

| Sub-phase | Days |
|---|---|
| 29a (DLL walker + array smoke) | 1.5 |
| 29b (in-memory diff UI) | 1 |
| 29c (SQLite + cross-session) | 1.5 |
| **Total v1** | **~4** |
| 29d (deferred) | TBD |

---

## Phase 29f — Class Pivot Layer B (reference-array inner-pivot) 📝 PLANNED 2026-05-28

**Goal**: pivot the elements of a reference-type array/`List<T>` field
keyed by an *inner* field of each element, tracked across snapshots —
e.g. `Mortal.Core.GameStat._statAdditionData : StatConvertCollectionData[]`
keyed by element `_propertyID`, value = element inner value. Sidesteps
both Layer A's index-shuffle weakness and address drift. Full design in
[class-pivot.md §7 "Layer B"](class-pivot.md).

### Design decisions (confirmed with user 2026-05-28)

1. **DLL-side capture required** — element inner fields must be frozen
   into `payload_gzip` at capture time (element addresses are dead in
   older snapshots; cross-session is the primary use case).
   `SnapshotArrayElement` gains `Fields: List<SnapshotFieldEntry>?`,
   **one level deep only** (no recursion into element arrays/refs).
2. **Capture model is bitness-adaptive**:
   - **x64** → blanket by default (inner fields for every reference
     array; true 全抓). User may narrow.
   - **x86** → OFF by default + banner; if the user enables it, it is
     **targeted only** (must nominate `class.arrayField` target(s)).
     Blanket-on-x86 is not offered — 活俠傳 (the motivating game) is
     x86 and OOM-prone; the Phase-29-polish refusal gate stays as the
     backstop.
3. **Pivot key = compound** `(owner-instance Stage-1 key, element
   inner key)`. Outer key identifies the owning instance, inner key
   identifies the element within the array — both drift-proof.
4. **No schema change** — `Fields` rides inside the existing gzipped
   blob; old `.db` files stay compatible (their elements have no inner
   fields → Layer B honestly shows nothing).

### Sub-phases (each ships green on the full suite + STATUS.md TL;DR)

- [x] **29f-1** Protocol: `SnapshotArrayElement.Fields` +
  source-gen JsonContext coverage + round-trip tests. (0.5 day)
  ✅ **Shipped 2026-05-28**, 555/555 green. Added
  `SnapshotArrayElement.Fields` (`List<SnapshotFieldEntry>?`, JSON
  `fields`) + `SnapshotElementClassError` (JSON `element_class_error`)
  to [ProtocolModels.cs](../ui/UnityDumpUI.Core/Services/ProtocolModels.cs).
  No new `[JsonSerializable]` needed — `List<SnapshotFieldEntry>` +
  `SnapshotArrayElement` were already registered, so the recursive
  element→fields cycle is covered. +3 tests in
  [SnapshotStoreTests.cs](../ui/UnityDumpUI.Tests/SnapshotStoreTests.cs):
  gzip+source-gen DB round-trip (inner fields + `element_class_error` +
  primitive-array element stays null), wire-contract (`"fields"` key +
  recursive round-trip via `ProtocolJsonContext`), and pre-Layer-B
  backward-compat (legacy JSON with no `fields` → `Fields == null`).
- [x] **29f-2** DLL element-field capture on **both** backends —
  ✅ **Code-complete + builds green (x64 + x86), 2026-05-28**; live-smoke
  folded into 29f-5. `walkArrayElementsInline` gained a
  `std::function<json(uintptr_t)>` element capturer; its reference branch
  now calls it (keeps `ptr`, adds `fields` / `element_class_error`).
  New public SEH-safe `Mono::ClassOfObject` / `IL2CPP::ClassOfObject`
  (wrap `object_get_class`) resolve each element's runtime class; the
  per-backend capturer walks the element's parent chain via the cached
  `getClassInfo` and decodes each field with the shared `decodeField`
  (skips statics, one level deep — nested arrays stay `ptr=`, capped at
  `session->elementFieldCap`). New `arrayElementFields` (bool) +
  `elementFieldCap` (default 16 / hard-cap 64) + `arrayElementTargets`
  (`"FQN"` / `"FQN.fieldName"`) RPC params on both snapshotBegin
  handlers + `Snapshot::Session`. **x86 blanket gate**: 32-bit + empty
  targets → capture disabled (OOM safety); effective state echoed back
  as `arrayElementFields` in the Begin response (+ mirrored on
  `SnapshotBeginResult`). MagicStrings snapshotBegin doc updated.
  DLL build #714, 555/555 UI tests still green.
- [x] **29f-3** `ArrayPivotEngine` (mirror `PivotEngine`) + unit
  tests — ✅ **Shipped 2026-05-28, 569/569 green** (+14).
  [ArrayPivotEngine.cs](../ui/UnityDumpUI/ViewModels/ArrayPivotEngine.cs)
  pivots reference-array elements by a compound `(outer instance key,
  inner element key)` tuple, value = element inner field(s). Reuses
  `PivotRow` + `PivotEngine.FormatCollisionCell` (made internal) so the
  UI consumes array + instance pivots through the same row shape.
  `AddressPerSnap` = element object address (falls back to owning
  instance). Tests in
  [ArrayPivotEngineTests.cs](../ui/UnityDumpUI.Tests/ArrayPivotEngineTests.cs):
  slot-shuffle resilience, compound key, collision, absent cells,
  cross-session, elements-without-fields skipped + honest-empty,
  KeyFilter, missing-inner-key sentinel (= elementFieldCap clip),
  element-address handoff, multi-value, validation.
- [x] **29f-4** Class Pivot tab inner-pivot UI — ✅ **Shipped
  2026-05-28, 575/575 green** (+6). Two UI surfaces:
  - **Snapshot tab (produce)**: "Capture array element fields (Layer B)"
    checkbox + "Elem field cap" NumericUpDown + "Targets" TextBox; wired
    through `SnapshotViewModel` → extended `BackendAdapter.SnapshotBeginAsync`
    (`arrayElementFields` / `elementFieldCap` / `arrayElementTargets`). On
    x86 the DLL's echoed effective state drives a "capture disabled — name
    targets" status note.
  - **Pivot tab (consume)**: `ArrayPivotMode` toggle reveals Array-field +
    Inner-key + Inner-value `AutoCompleteBox`es; "Key fields" doubles as the
    outer owner-instance key. `RunAsync` branches to `ArrayPivotEngine`.
    `KnownArrayFields` (reference arrays on the class) + `KnownElementFieldNames`
    (captured inner fields) auto-discover from hydrated snapshots. 0-row
    inner-pivot adds a "re-take with Layer B enabled" hint.
  ViewModel tests in [PivotViewModelTests.cs](../ui/UnityDumpUI.Tests/PivotViewModelTests.cs):
  slot-shuffle tracking, compound outer key, missing array-field /
  inner-key / inner-value errors, no-captured-elements hint, autocomplete
  population. UI (DiscreteUI.dll) compiles; bindings match the existing
  AOT-shipping Class Pivot pattern.
- [ ] **29f-5** Live-smoke on 活俠傳 `GameStat._statAdditionData`
  (x86 targeted path) + a x64 blanket smoke + docs. (0.5 day + ≥1
  live round)

### v1 effort estimate

| Sub-phase | Days |
|---|---|
| 29f-1 (protocol) | 0.5 |
| 29f-2 (DLL capture, both backends) | 1.5 |
| 29f-3 (ArrayPivotEngine + tests) | 1 |
| 29f-4 (UI inner-pivot) | 1 |
| 29f-5 (live-smoke + docs) | 0.5 |
| **Total** | **~4.5** |

### Risks

- **Capture-size blow-up** = instances × arrayCap × elementFieldCap.
  Bounded by the bitness-adaptive model + hard caps; x64 blanket still
  needs a projected-size advisory in the status line.
- **Per-element `object_get_class`** adds a deref + class-cache lookup
  per reference element. Mitigated by the existing global class-info
  cache; measure on a wide x64 snap before shipping blanket default.
- Element class can differ per slot (polymorphic arrays) — must read
  runtime class per element, not assume `element_type`.

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
