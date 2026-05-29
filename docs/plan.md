# UnityCEDumper — Development Plan

> 跨 PC 共用的開發進度文件。這是專案的**正典進度帳 (canonical phase ledger)**
> 與**集中待辦清單 (the single source of TODO)**。完成一步就把 `[ ]` 改成
> `[x]` 並 commit。已完成 phase 的長篇寫作搬到 [archive/](archive/);
> 本檔只保留 Phase Overview 摘要 + 仍 open 的 backlog。

**Last updated**: 2026-05-29
**Current state**: ✅ **Phase 30 (snapshot memory & speed) + scan P1/P2 全部 SHIPPED 並 merged to `main`**(build **#820**)。
30a/30b/30c headline 完成且 live-verified;scan **P1**(平行化)+ **P2**(IL2CPP value-scan reverse-lookup interval-tree)完成;外加 low-RAM advisory banner、per-PID log count-based 保留、DataGrid 橫向捲動修正。
`dev == main == origin`,**603 UI tests green**,兩個 DLL 都 build clean。
**Nothing mid-flight** — 下方 Open Backlog 全是 deferred-by-design 的選配後續,none blocking。
完成 phase 的細節見 [archive/plan-archive-phases-29-30.md](archive/plan-archive-phases-29-30.md)。

---

## Open Backlog / TODO

> 這是**唯一**的待辦清單。所有 open 項目集中於此;Phase Overview 表只記 ship 狀態,不重複放 todo。

### 🔍 Scan line — 詳見 [Memory-Scanning-Internals.md](Memory-Scanning-Internals.md) §16.3 / §16.5b
- [ ] **P2 Mono port** — 把 §16.5b interval-tree 移植到 `Mono::WhatIsAt`(抽出 Mono `ResolveInstanceLookup` + `ReverseLookupIndex`,對應 IL2CPP 已做的 [IL2CPPAddressFinder.cpp](../dll/src/il2cpp/IL2CPPAddressFinder.cpp))。IL2CPP shipped #814;活俠傳 value scans 受益。
- [ ] **P2 SIMD inner loop** — `Vector256` anchor for `ScanForString`,4-qword compare for `ScanForPointer`。
- [ ] **Mono `FindAllInstances` 平行化** — P1 follow-up;Mono 端仍單執行緒(IL2CPP 已平行)。
- [ ] **P3 VirtualQuery cache** — 低優先,P1 已大幅降低需求。

### 📸 Snapshot line — 詳見 [snapshot-streaming-dedup.md](snapshot-streaming-dedup.md) + [archive/plan-archive-phases-29-30.md](archive/plan-archive-phases-29-30.md)
- [ ] **30c transient ~10 GB UI peak** — streaming chunk size 100→25(per-chunk re-expansion 是尖峰);需 re-smoke。
- [ ] **30b Mono game-side stream-decode** — session-held-attach 生命週期;**HIGH-RISK**(活俠傳 BDWGC freeze)。Mono 維持 decode-in-Begin 直到驗證安全。
- [ ] **schema-retention dedup** — 把 per-field metadata 折成 shared ref;**LOW-ROI**(per design doc)。
- [ ] **Mono static field VALUE capture**(optional / user-opt-in)— 目前 Mono 端跳過 static 值擷取(`mono_class_vtable` 從 worker thread → BDWGC freeze 根因)。

### 🧪 待補的 live-smoke(需 inject 實機)
- [ ] **29h live-smoke** — re-inject build #739,實機 re-run find instances 驗證 metadata-arena false-positive 修正。
- [ ] **29f-5 live-smoke**(部分已驗)— 活俠傳 `GameStat._statAdditionData`(x86 targeted)+ x64 blanket smoke。

### 💤 未啟動 / deferred 設計
- [ ] **Phase 28 — Field → Methods cross-reference**(TODO stub,從未開始)— 設計見下方 Phase Overview #28。
- [ ] **Phase 28b-v3.2** — IL2CPP never-populated static slot → class 的 token decode(v29 layout dead-end;v3.2 cookbook 見 [lessons-learned.md](lessons-learned.md))。
- [ ] **Phase 29e/29f deferred** — Layer B reference-array inner-pivot anomaly auto-surface、field-name autocomplete pre-hydrate(見 [class-pivot.md](class-pivot.md))。

---

## Documentation & archive map

| 文件 | 內容 |
|---|---|
| **plan.md**(本檔) | 正典 phase ledger(Phase Overview)+ **集中 TODO** |
| [STATUS.md](STATUS.md) | 目前 build/phase 一行式指標檔(→ 本檔取得 ledger + TODO) |
| [archive/plan-archive-phases-0-16.md](archive/plan-archive-phases-0-16.md) | Phase 0–16 長篇寫作 |
| [archive/plan-archive-phases-17-28.md](archive/plan-archive-phases-17-28.md) | Phase 17–28 長篇寫作 |
| [archive/plan-archive-phases-29-30.md](archive/plan-archive-phases-29-30.md) | Phase 29 / 29e / 29f / 29g / 29h / 30 長篇寫作 + 早期 Cross-PC/決議前的細節 |
| [archive/status-history.md](archive/status-history.md) | 舊 session hand-offs |
| [archive/STATUS-archive-2026-05-29.md](archive/STATUS-archive-2026-05-29.md) | 2026-05-29 瘦身前的完整 STATUS hand-off 快照 |

設計 / 參考文件:[Archiutecture.md](Archiutecture.md) · [pipe-protocol.md](pipe-protocol.md) · [Memory-Scanning-Internals.md](Memory-Scanning-Internals.md) · [snapshot-streaming-dedup.md](snapshot-streaming-dedup.md) · [class-pivot.md](class-pivot.md) · [lessons-learned.md](lessons-learned.md) · [aot-pitfalls.md](aot-pitfalls.md);完整文件對照表見 [README.md](../README.md)。

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
| 30 | Snapshot memory & speed | Streaming decode + value compaction + UI stream-to-disk (bound game + UI RAM on huge snapshots) + scan P1/P2 accel | ✅ 30a / 30b (IL2CPP) / 30c SHIPPED 2026-05-29 (build #820); 30b-Mono + smaller chunk size deferred — see [Open Backlog](#open-backlog--todo) + [archive](archive/plan-archive-phases-29-30.md) |

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
