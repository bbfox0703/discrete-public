# UnityCEDumper — Session Hand-off Archive

> Historical "What landed" entries for sessions older than two
> hand-offs ago. Carved out of [STATUS.md](STATUS.md) on 2026-05-19
> to keep the main hand-off doc readable.
>
> The current + most-recent-prior session stays in
> [STATUS.md](STATUS.md). Older sessions land here in
> reverse-chronological order — newest section is at the top.

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

**Live click-through smoke closed** — Steps 1-8 verified 2026-05-18 (with 6 bugs caught + fixed); Steps 9-11 verified in a follow-up session (user log: `Mathf.Abs(-7) → 7`). Full 11-step checklist green end-to-end against BongoCat (Mono).

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

