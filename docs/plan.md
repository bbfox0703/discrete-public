# UnityCEDumper — Development Plan

> 跨 PC 共用的開發進度文件。每完成一個小步驟就把 `[ ]` 改成 `[x]` 並 commit。
> 所有里程碑驗收條件 (Acceptance) 必須通過才能往下一階段。

**Last updated**: 2026-05-21 (session close — build #465; ready for next-session handoff)
**Current phase**: **✅ Phase 27 SHIPPED + LIVE-VERIFIED + 5 follow-up polish commits**. See [STATUS.md](STATUS.md) "TL;DR — what's left to pick up" for the carry-forward list. One known minor open issue: dual-monitor window-restore stays at maximize bounds instead of snapping back to pre-maximize Position+Size. Not blocking. Otherwise Phase 27 is done, README rewritten for end-user audience, lessons documented.

**Previous current-phase**: **✅ Phase 27 — Unity-aware Value Search SHIPPED + LIVE-VERIFIED** (all sub-phases 27a-27e). Build #452, 283/283 xUnit pass, AOT clean. Three production-bug follow-ups landed post-ship: (1) `SessionManager` heap-leaked to skip ~400K destructor calls at process exit; (2) `MonoAddressFinder` / `IL2CPPAddressFinder` caches also heap-leaked; (3) **Reentrant `ThreadAttachGuard`** — 4000+ per-WhatIsAt attach cycles were accumulating BDWGC finalizers that ran on game shutdown, hanging 活俠傳 for 44 s post-scan. Now: outer guard at scan-handler level + reentrancy counter make inner guards no-ops, batch goes from 4000 cycles → 1. Shutdown verified back to ~1 s on 活俠傳. New lesson recorded in [lessons-learned.md](lessons-learned.md). Live-smoked on SaGa F2R (IL2CPP) + 活俠傳 (Mono x86) for the DLL-level RPCs. UI Value Search tab landed; pending user click-through to verify the cross-tab handoff + DataGrid sort. Closes the long-standing "CE has value search, we don't" gap with a Unity-aware twist: every candidate row carries class/field/instance context (via whatIsAt), and the **`Mortal.Core.GameStat._value` vs `PLY_WORK_SAVEDATA.purse`** distinction (active state vs persisted state) emerges naturally from the refine workflow — CE alone cannot tell them apart. v2 candidates: 27f (libgc heap-section pre-filter) would close Phase 20-HEAP and accelerate global scans ~10x.

**Previous phase**: **🚧 Phase 27 — Unity-aware Value Search (27a shipped, 27b next)**. **27a live-smoked on three targets**: SaGa F2R (IL2CPP x64, global scan for Int32=100000 found `MainGame.PLY_WORK.purse` at +0x18 ✨), BongoCat (Mono x64, class-scoped scan), and 活俠傳 (Legend of Mortal, Mono x86, Unity 2020.3.49f1 — global scan reliably identified `Mortal.Core.GameStat._value` across UI state changes). One production bug discovered + fixed mid-smoke: `Mono::BuildClassRangeCache` was hammering `mono_class_vtable` for all ~17K classes in a single ThreadAttachGuard scope, wedging legacy 32-bit Mono when the game's main thread was focus-paused; rewrote as lazy vtable→klass resolution in `WhatIsAt` (cache build now ~500ms with zero runtime calls; per-candidate +1 SafeReadBytes deref, memoized after first hit). Lesson recorded in [lessons-learned.md](lessons-learned.md). Build #423.

**Original planning**: **🚧 Phase 27 — Unity-aware Value Search (planning)**. Sub-phases 27a-27f drafted (~6.5 day v1 estimate). No code yet. **Phase 28** (Field → Methods cross-reference) added as TODO stub — picks up after Phase 27 ships. Inspired by `D:\Github\AOBMaker`'s `WindowsValueScanner.cs` pattern but reuses existing Discrete primitives (`Memory::EnumerateCommittedRegions` + `whatIsAt` filter + `findInstances`) so no new scan engine needed. Two scan modes: **global** (CE flow — scan + whatIsAt filter) + **class-scoped** (Discrete flow — `listFieldValuesAcrossInstances` + value predicate, ~100x faster). Full CE filter mode set (exact / range / changed / unchanged / increased / decreased / new value). Includes Phase 17b carry-forward (Array / List<T> element resolution in `whatIsAt`). v2 sub-phase 27f closes Phase 20-HEAP simultaneously via libgc heap-section binding. Previous landings (2026-05-20 early/mid): **Phase 26 anti-tamper protection** — 3-slot SHA256 self-check spread across Core + UI assemblies (`BuildArtifactToken` / `ExportFormatToken` / `WindowsRuntimeMetadata`) + 2 poison surfaces (CT export offsets + Inspector grid values, 1/11 deterministic) + XOR-shielded footer credit + `inject-hash.ps1` 3-slot AOBMaker-style stamping + new `USAGE.md` end-user guide. Mid-session (same day): **Phase 20-MT main-thread marshaling** (MinHook detour on `runtime_invoke` + dispatcher queue) live-PASS on 5 targets; **Phase 20b/c/d Scene Explorer tab** + cross-tab handoff. Carry-forward from 2026-05-19: Phase 25 cross-class Symbol Finder, Phase 5 v32-v39 metadata parser (live-smoked on Songs of Silence v39 / 40k TypeDefs), Phase 12d 32-bit Mono (Legend of Mortal x86), Phase 21d Inspector write-back live PASS, 19d-3 smart-typed editors, 21a reverse-property lookup, DataGrid AOT sort trap fix, 22g DLL-free Mono register. **No critical-path open work.** Verified targets: SaGa F2R (IL2CPP v29 / x64), BongoCat (Mono Unity 6.0+ / x64), Farthest Frontier (both backends / x64), Songs of Silence (IL2CPP v39 / Unity 6.1 / x64), R-Type Dimensions EX (IL2CPP v24 / Unity 2018.1 / x64), FF VI PR (IL2CPP v24 / Unity 2019.4 / x64), Hollow Knight Silksong (Mono / Unity 6.0 / x64), **LEAVES (Mono / Unity 5.4 / x86 ✨)**, **Legend of Mortal (Mono / Unity 2020.3 / x86 ✨)**. Build #408 (DiscreteUI.exe AOT 39.4 MB after 3-slot guardian, DLL sizes unchanged from #376; 283/283 tests pass + AOT round-trip verified end-to-end). See `STATUS.md` for the detailed hand-off; this file is the canonical phase ledger.

**2026-05-07 renumbering**: the address-finder track was originally tagged Phase 18 with sub-phases 17a-17d, a holdover from when "find method instances" sat at Phase 17. After the address-finder shipped, the two phases were swapped so the sub-phase letters match the parent number — the track that just closed is now Phase 17 (matching its 17a-17d nodes), and find-method-instances moves to Phase 18. No runtime / wire-protocol changes; everything that previously said "Phase 18 sub-phase 17X" now reads "Phase 17 sub-phase 17X" (or simply "Phase 17X").

## Hand-off notes (historical — for live state see STATUS.md)

- Phase 17 sub-phase 17d green (build #198, 2026-05-07):
  - New [scripts/unity_addrfind.lua](../scripts/unity_addrfind.lua) (13.6 KB) — closes out Phase 17's address-finder track on the CE side. Public API: `whatIsAt(addr, maxBackwardScan?)` (low-level, returns parsed table); `lookupAddress(addr)` (pretty-prints to console, mirrors `Test-WhatIsAt.ps1`'s output format); `getClassRangeStats(rebuild?)` (diagnostic / cache pre-warm); `lookupSelectedAddress()` (memview-or-prompt entry point); `bindAddrLookupHotkey(keys)` (optional global hotkey that fires `lookupSelectedAddress`).
  - Memory-view selection: tries `getMemoryViewForm().HexadecimalView.SelectionStart` first, falls back to `.TopAddress`, then `inputQuery` when neither is available — every accessor is `pcall`-wrapped so older CE builds (and weird forks) degrade gracefully.
  - JSON parser + RPC helper deliberately duplicated from `unity_dissect.lua` (instead of `require`-ing utils) so the file remains drop-in standalone — users who already pasted the dissect / symbols scripts only need to add this one. ~3 s pipe-open retry budget + per-RPC `processMessages()` / `sleep(1)` yield are identical to the sibling scripts.
  - `scripts/README.md` gains a Phase 17d step in the workflow + entry in the install layout block. Existing build script's `*.lua` glob in `Invoke-CompanionCopy` (`build.ps1:412`) means no build-script changes were needed.
  - **Phase 17 closed** — addresses 17a (DLL handler), 17b (field resolution), 17c (UI tab), 17d (CE Lua bridge) all green.
  - **NuGet bumps landed in this session**: Avalonia 12.0.1 → 12.0.2 (UnityDumpUI.csproj inlined the version literal, removing the `$(AvaloniaVersion)` MSBuild property), `Tmds.DBus.Protocol` 0.92.0 → 0.93.0, `Microsoft.NET.Test.Sdk` 18.4.0 → 18.5.1. AOT publish stays clean (0 warnings under all four trim/AOT analyzers); `DiscreteUI.exe` actually shrank from 45.2 MB → 39.0 MB after the bumps, suggesting Avalonia 12.0.2 brought trim improvements.

- Phase 17 sub-phase 17c green (build #197, 2026-05-06):
  - New `AddressLookupViewModel` ([ui/UnityDumpUI/ViewModels/AddressLookupViewModel.cs](../ui/UnityDumpUI/ViewModels/AddressLookupViewModel.cs)) wraps the `whatIsAt` RPC with CommunityToolkit.Mvvm source-gen (no reflection) and exposes presentation-friendly labels (`InstanceLabel`, `ClassLabel`, `FieldOffsetLabel`, `FieldLabel`, `NearestFieldLabel`, `KindLabel`, `ScannedLabel`). Cancellation-safe — supersedes prior in-flight lookups so a fast-typing user doesn't get out-of-order results.
  - New DTOs in `ProtocolModels.cs`: `WhatIsAtResult` / `WhatIsAtField` / `WhatIsAtScanRange` / `ClassRangeStatsResult`. Registered with the source-gen `ProtocolJsonContext` alongside the existing types so AOT publish stays at zero IL2026 / IL3050 warnings.
  - `BackendAdapter` gains `WhatIsAtAsync(address, maxBackwardScan)` and `GetClassRangeStatsAsync(rebuild)`. The `Boolean` JSON serializer is now registered too (used by `getClassRangeStats`'s optional `rebuild` arg).
  - New "Address Lookup" tab on `MainWindow` next to Fields / Inspector / Methods. Address textbox + Lookup / Clear buttons + a Backward-scan NumericUpDown (4096 default, capped at 16384 to match the DLL). Result block shows Instance / Class / Field offset / Field (or NearestField with delta) / Kind / Scanned-range, plus *Inspect this instance* and *Copy instance address* action buttons. The "no result" panel surfaces the raw `reason` from the wire (`no_klass_in_range`, `class_range_cache_unavailable`, `no_il2cpp_domain`, …) so users can tell apart "scan landed in unmanaged memory" from "DLL isn't loaded properly".
  - `MainWindowViewModel` gains `SelectedDetailTabIndex` (TwoWay-bound on the right-pane `TabControl`) plus `TabIndexFields` / `TabIndexInspector` / `TabIndexMethods` / `TabIndexAddressLookup` constants. Two cross-tab paths: (a) Address Lookup → Inspector via `OnInspectInstanceRequested` event handler that calls `Inspector.SetTarget` + `Inspector.FocusOnInstance`, then sets `SelectedDetailTabIndex = TabIndexInspector`; (b) Inspector → Address Lookup via `LookupInspectorAddressAsync(address)` that pre-fills + runs the lookup and flips back. Disconnect tears down the lookup state via `AddressLookup.ClearCommand` so a stale class-pointer block doesn't survive a game relaunch.
  - `LiveInspectionViewModel.FocusOnInstance(address)` is the new public seam used by the cross-tab handoff. Inserts the address as `Instances[0]` if not already present, then sets `SelectedInstance` so the existing `OnSelectedInstanceChanged` cascade fires `walkInstance` — same flow auto-refresh and `FindInstances` use, no duplicate walk path to maintain.
  - `MainWindow` code-behind hook `OnLookupInspectorAddressClicked` reads `Inspector.SelectedNode.EffectiveAddress` (deref pointer for pointer-typed rows; field's storage address otherwise) and hands it to `LookupInspectorAddressAsync`. Sits alongside the existing `Copy CE XML rooted here` / `Copy CE field` context-menu items on the Inspector grid, separated by a `<Separator />` for visual grouping.
  - Strings live in `Resources/Strings/en.axaml` under the new `AddrLookup.*` keys, plus the `Inspector.LookupAddressMenu` entry. Mirrors the existing `Inspector.*` / `Browser.*` namespacing so locale-swappable for free when a translation lands.
  - Native AOT publish clean: `dotnet publish -c Release -r win-x64 -p:PublishAot=true` finishes 0 warnings under all four trim/AOT analyzers; `DiscreteUI.exe` lands in `dist/` at 45.2 MB (vs ~45 MB before, no measurable size impact from the new tab + VM).
- Phase 17 sub-phase 17b green (build #194, 2026-05-06):
  - Field-offset resolution layered on top of 17a's instance + class identification. New `FieldHit` struct in both backends carries `name` / `type` / `offset` / `size` / `kind` / `declaredIn` plus enum / value-type flags; `whatIsAt` populates either `field` (exact `[offset, offset+size)` hit) or `nearestField` (closest declared field below the queried offset, with `nearestFieldDelta`).
  - `EnumerateInstanceFields(klass)` — new internal helper in `IL2CPPAddressFinder.cpp` and `MonoAddressFinder.cpp`. Walks the parent chain (depth cap 16 like `walkInstance`), iterates each class's instance fields directly via the resolver (skipping statics), and computes per-field size via `class_from_*_type` → `class_value_size` for value types, an inline primitive-name table as fallback, 8 bytes for reference types. Mono uses `class_is_enum` + `class_enum_basetype`; IL2CPP uses the parent-class-name `Enum` trick + `value__` field walk (same approach as `ReadClassInfo`).
  - Kind classifier: `leaf` (primitive at exact offset, includes enums), `value-struct` (multi-field value type at exact offset — Vector3, Color, Quaternion, user struct), `pointer` (reference type at exact offset — System.String, T[], class), `interior` (queried offset falls inside the field but not at its boundary — exposes `interiorOffset` for the byte-offset within the field).
  - All ResolveFieldAt SEH-leaf primitives sit alongside the existing 17a probes. Single `ThreadAttachGuard` scope covers both the backward scan AND the field walk so the field-resolution pass doesn't pay an extra attach round-trip. Static fields are filtered at enumeration time so their offsets (which are relative to the static block, not the instance base) can't pollute the offset match.
  - `tools/Test-WhatIsAt.ps1` extended with a follow-up line per probe showing the resolved field — name, type, offset, size, kind, declaredIn.
  - Out of scope for 17b (deferred): pointer-to-class recursion (drill into a pointer field's target instance), `T[]` / `List<T>` array-slot resolution, `System.String` UTF-16 buffer interior detection. The wire format leaves room for these in `field.kind` (could add `array-slot` / `array-element`) without breaking existing callers.
- Phase 17 sub-phase 17a green (build #188, 2026-05-06):
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
  - `Tmds.DBus.Protocol` was a transitive Linux-only dep pulled in by `Avalonia.Desktop` → `Avalonia.X11` → `Avalonia.FreeDesktop`. After 19a we dropped `Avalonia.Desktop` in favor of `Avalonia.Win32` directly (Windows-only build per project scope) — both the Tmds.DBus override pins AND the ILC "will always throw" dead-code diagnostics for X11/DBus entry points are gone.
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
| 26 | Anti-tamper protection | 3-slot SHA256 self-check spread across two assemblies + 2 poison surfaces (CT export offsets + Inspector grid values) + XOR-shielded credit string + AOT-stamped hash via `tools/inject-hash.ps1`. Inspired by AOBMaker's pattern but redesigned for cross-assembly slot distribution (Slot A in Core/Services / Slot B in Core/Services/Export / **Slot C cross-assembly in ui/UnityDumpUI/Platform**). Dev-mode bypass (0xCC placeholders) keeps all factors at 0 in Debug/Release single-file builds — exports + Inspector + tests stay byte-identical to pre-Phase-26. Class names renamed to non-suggestive forms so binary metadata no longer leaks "Integrity" / "Guardian" / "Shield". | ✅ **Shipped + AOT-verified 2026-05-20** (build #408). Two iterations: `ccaae6e` single-slot baseline + `348cbb6` 3-slot cross-assembly upgrade. `TokenAggregator.Combined() = A \| B \| C` ORs all three factors (UI registers `WindowsRuntimeMetadata.Read` into Core via `Program.Main`). Poison 1: `CloakOffset(off) => off ^ (factor & 0x7F)` in `CeXmlExporter` + `CsxExporter` shifts every emitted offset 0-127 bytes (CT loads cleanly in CE; reads wrong fields). Poison 2: `LiveInspectionViewModel.MaybePerturbDisplay` corrupts 1/11 numeric `DisplayValue` rows via per-row deterministic seed (string `GetHashCode` XOR offset XOR salt) — same row corrupts same way within session, different rows across sessions. Anti-trace: `Read()` returns 0 for first N calls (A:3 / B:2 / C:4) even when tampered. Anti-debug: `Debugger.IsAttached` on published binary returns poison code. AOT round-trip verified end-to-end: 3 sentinels at `0x02438F98` / `0x02438E80` / `0x0243ABF8` (C ~11 KB away from A/B). `inject-hash.ps1` 3-slot AOBMaker-style: zero all 3 slots → SHA256 once → write same hash into all 3. Footer credit `© bbfox @ https://opencheattables.com` decoded from XOR blob at VM construction. New [`USAGE.md`](../USAGE.md) end-user guide for CT makers. |

---

> **Phases 0 → 16** (bootstrap → both backends → live walking → UI
> skeleton → exports → Lua bridge → proxy DLLs → metadata CLI →
> hardening → test matrix → docs finalization) live in
> [plan-archive-phases-0-16.md](plan-archive-phases-0-16.md). Carved
> out 2026-05-19 — those phases are all shipped + closed long ago,
> never need editing. New work appends a new phase below.

---

## Phase 17 — Address Finder (reverse lookup)

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

## Phase 18 — Find Method Instances

**Goal**: Mirror Cheat Engine's "find method instances" with overload
disambiguation. Lets users target a specific overload by signature instead
of registering all methods of a class wholesale (which `unity_symbols.lua`
does today).

Broken into four sub-phases that mirror the Phase 17 shape (DLL → server
parsing → UI → CE Lua). Sub-phase letters use the parent number per the
2026-05-07 renumbering convention.

- [x] **18a** DLL handler: new pipe RPC `findMethodInstances(assembly, ns, class, methodName, [signatureMatch])` returning `[{ token, address, signature, paramTypes, paramNames, returnType, humanSuffix, declaringClass, ... }]` for every matching overload. Server-side enumerates matching `MethodInfo*` via the existing `class_get_methods` iterator. Native address comes from `ReadMethodPointer` for IL2CPP and `monoCompileMethod` for Mono. SEH-leaf primitives reuse the AddressFinder pattern. Shared `MethodSignatureFormat.h` produces the symbol-safe `humanSuffix` (`3float` / `Vector3` / `noargs` / `string_Card_Point`) used by both UI and Lua bridge. Smoke test: `tools/Test-FindMethodInstances.ps1`.
- [x] **18b** Signature decoding: each `MethodInfo*`'s parameter list emits a human-readable signature string (e.g. `(System.Single, System.Single, System.Single)`). Optional `signatureMatch` arg narrows results. IL2CPP uses `il2cpp_method_get_param_count` + `il2cpp_method_get_param`; Mono uses `mono_method_signature` + `mono_signature_get_params`. All five new resolver bindings are optional — old runtimes degrade gracefully (empty paramTypes / returnType, address still ships). **Bonus 18a-3** added a hand-rolled minimal x64 length disassembler (`shared/AobPattern.{h,cpp}`) + `getMethodAobPattern` RPC that reads N bytes (default 32, capped 128) from any address, walks them with conservative wildcarding (rel8/rel32 immediates + RIP-relative ModRM disp32 → `??`), returns CE-style pattern + module name/offset.
- [x] **18c** UI overloads modal + AOB AA-script generator. Right-click any row in the Methods grid → "Find overloads…" opens a multi-select `MethodOverloadsWindow`. Three clipboard outputs share the symbol-naming convention `{Class}_{Method}_{Suffix}`: **Copy AOB AA script** (per-overload `getMethodAobPattern` → CE Auto-Assembler script with `aobscanmodule` + `registersymbol` lines, idempotent `discrete_logScan` Lua helper guarded with `if _G.discrete_logScan == nil then`), **Copy registerSymbol script** (hardcoded-address path), **Copy addresses** (newline-separated). **Plus**: "Create AA in CE" button uses AOBMaker's `CreateAAScript` verb (`\\.\pipe\AOBMakerCEBridge`) to push the generated AA script directly into CE's address list as a vtAutoAssembler memory record, with optional auto-activate. AOT publish at build #207 stays 0/0 warnings.
- [x] **18d** CE Lua bridge: extended `scripts/unity_symbols.lua` with `registerMethodOverloads(assembly, ns, class, methodName, [sigFilter])`. Symbol shape `Unity:{Asm}:{Ns}.{Cls}.m_{Method}__{HumanSuffix}` (e.g. `__3float`, `__Vector3`). Reuses the existing inline `rpc()` / `parseValue()` JSON parser + 15-attempt `io.open` retry + per-RPC `processMessages()`+`sleep(1)` yield pattern.

**Acceptance**: ✅
- IL2CPP — verified live against SaGa F2R `UnityEngine.Vector3.Set` (single 3-float overload, address resolves, suffix `3float`, params `newX/newY/newZ` ship through). AA-script path tested against `MainGame/PLY_WORK.AddGrowHpMax(int)` — single-click checkbox toggle works, "Copy AOB AA script" writes a 32-byte pattern script to clipboard.
- Mono — JIT-compile path runs server-side via `mono_compile_method`; abstract / generic-without-args overloads come back with `compiled=false` and are skipped.
- AOBMaker direct push — generates AA script and pushes via `CreateAAScript` ({type, description, script, autoActivate}) over `\\.\pipe\AOBMakerCEBridge`, falls back to "AOBMaker pipe unavailable" status when CE plugin isn't loaded.
- Symbol-name uniqueness — `MakeSymbolName` carries a `taken` set so within-batch collisions auto-suffix `_2` / `_3`.

---

## Phase 19 — Lua-callable Unity methods (Lua → managed invocation)

**Goal**: Invoke managed methods (Mono JIT or IL2CPP AOT entry points) from
CE Lua with marshaled args + return values. Closes the long-standing
"Lua-callable Unity methods" goal in [CLAUDE.md](#) §"Headline capabilities"
and lets cheat-table authors call e.g. `Player.AddItem(99)` or
`Vector3.ToString()` from a CE hotkey without hand-writing native-ABI
Auto-Assembler.

Spec: [docs/lua-invoke.md](lua-invoke.md) (TBD — write alongside 19a).

### Why now

Phase 18 just landed — users have addresses + AOB scripts. The next missing
capability is *calling* methods cleanly. Today CE Lua can dispatch to a
registered symbol via `executeCodeEx`, but only with raw native ABI
(RCX/RDX/R8/R9 for x64, no struct-by-value rules, no string marshaling).
That's fine for methods taking a single int; painful for anything that
takes `string`, returns a managed type, or might throw.

### Why a custom RPC when CE already has Mono support?

CE 7.5+ ships its own Mono client (`mono.dll` Lua module, enabled via
*Settings → Mono → Enable Mono features*) that **does work for both Mono
and IL2CPP** Unity games — `LaunchMonoDataCollector()` →
`mono_image_findClass` → `mono_class_findMethodWithSignature` →
`mono_invoke_method` is the canonical CE path for managed-method
invocation.

In practice it's awkward enough to be worth replacing for any non-trivial
workflow: overload disambiguation requires hand-built CLR signature
strings, string args / returns need manual `mono_string_new` /
`mono_string_toCSharpString` plumbing, exceptions surface as Lua errors
without typed message extraction, and CE must stay attached for the
mono APIs to remain valid. Our `callMethod` RPC handles all of those
server-side.

**This makes the CE-only path an OPTIONAL alternative, not a competing
solution** — useful for one-off prototyping when the user happens to
have CE attached and doesn't need our pipe surface elsewhere. The
recommended workflow stays our DLL + `callMethod`. Full
side-by-side comparison: [docs/lua-invoke.md §"Why a custom RPC when CE
has built-in support?"](lua-invoke.md#why-a-custom-rpc-when-ce-has-built-in-support).

### Scope (v1)

In:
- Static and instance methods
- Primitive args: `bool` / `byte` / `sbyte` / `int16` / `uint16` /
  `int32` / `uint32` / `int64` / `uint64` / `float` / `double` / `char`
- `string` arg (UTF-8 over the wire → `MonoString*` /
  `Il2CppString*` server-side via `mono_string_new_utf8` /
  `il2cpp_string_new_utf8`)
- Raw `address` arg for `IntPtr` / `void*` / pre-resolved object pointers
- `null` arg
- Primitive return value (boxed `MonoObject*` → unbox → JSON number/bool)
- `string` return value (managed string → UTF-8 over the wire, mirrors
  the existing `walkInstance` String decoder)
- Reference-type return value as raw address — caller chains
  `whatIsAt` / `walkInstance` for further inspection
- Exception capture: `result.exception = "Type: message"` instead of
  crashing the host; methods that `try`/`catch` internally still succeed

Out (v2 follow-ups, deliberately not started):
- `ref` / `out` parameters
- Open-generic methods (need explicit instantiation from CE side)
- Boxed value-type args (`Vector3`, `Color`, user struct) — pass by
  pre-allocated address only in v1; full marshaling later
- `async` / `Task<T>` methods — call the sync entry point directly
- Delegates / events
- Marshaling back to the game's main thread (Unity scene-graph APIs
  assume main-thread execution; v1 invokes on the pipe worker thread)

### Sub-phases (mirror Phase 17/18 shape)

- [x] **19a** DLL handler — IL2CPP first (static methods only). Landed
  2026-05-07. New module pair `dll/src/il2cpp/IL2CPPMethodInvoker.{h,cpp}`
  + new pipe RPC `callMethod(assembly, ns, class, method, [signatureMatch],
  [instance], [args])`. Resolver gains six optional bindings
  (`il2cpp_runtime_invoke`, `il2cpp_object_unbox`,
  `il2cpp_object_get_class`, `il2cpp_string_new`, `il2cpp_string_chars`,
  `il2cpp_string_length`); the three string-related ones are pre-bound for
  19c but unused in 19a. Single-pass overload resolution mirrors Phase
  18a's iterator (depth-cap method walk + `signatureMatch` substring
  filter) and returns the `MethodInfo*` directly. Marshaling uses one
  8-byte slot per arg (low-N-byte writes, upper bits zero — matches
  IL2CPP's `libil2cpp/vm/Runtime.cpp::Invoke` convention). Whole invoke
  wrapped in a single SEH leaf so a hard crash in the JIT/AOT entry
  becomes `error: native_crash_in_runtime_invoke` instead of tearing down
  the host. Exception path: `il2cpp_object_get_class` →
  `il2cpp_class_get_name` for the type, `Il2CppException + 0x18` for the
  `_message` System.String pointer (decoded via the same UTF-16 → UTF-8
  layout the Inspector's String field already uses). Return decode
  switches on the resolved formal `returnType`: void/bool/int*/uint*/
  float/double unboxed via `object_unbox`; reference types surface as the
  raw managed pointer (`returnValue: "0x..."`); `System.String` returns
  surface as Address in 19a (19c will decode to UTF-8). Spec doc:
  [docs/lua-invoke.md](lua-invoke.md). Pipe-protocol entry:
  [pipe-protocol.md#callmethod-il2cpp](pipe-protocol.md#callmethod-il2cpp).
  Smoke: `tools/Test-CallMethod.ps1` (positive / exception / overload-disambig
  scenarios). 19a explicitly rejects `instance != 0`
  (`instance_methods_not_supported_in_19a`), non-static methods
  (`instance_required_for_non_static`), and string args
  (`string_args_not_supported_in_19a`) — all three lift in 19c.

- [x] **19b** Mono port — landed 2026-05-07, build #213. New module pair
  `dll/src/mono/MonoMethodInvoker.{h,cpp}` mirrors the IL2CPP shape
  exactly (single-pass overload resolver, 8-byte-slot marshaling, SEH
  wrap, `MonoException + 0x18` for `_message` decode). Resolver gains six
  new optional bindings (`mono_runtime_invoke`, `mono_object_unbox`,
  `mono_object_get_class`, `mono_string_new`, `mono_string_chars`,
  `mono_string_length`); the three string-related ones are pre-bound for
  19c but unused in 19b. Address field of the response is populated via
  the existing `mono_compile_method` path (Mono is JIT — no
  AOT-`methodPointer` shortcut). Wire shape is byte-identical to IL2CPP;
  per-backend error codes are `instance_methods_not_supported_in_19b` /
  `string_args_not_supported_in_19b` / `mono_runtime_invoke unbound`.
  Acceptance: same canonical positive (`Math.Abs(int):-7 → 7`) +
  exception (`Convert.ToByte(int):-1 → OverflowException`) +
  overload-disambig gates that 19a verified, against a Mono game
  (BongoCat) instead of SaGa F2R. **All three gates verified live
  2026-05-07** — `Math.Abs(int):-7 → 7`, `Math.Abs(double):-3.14159
  → 3.14159` via `signatureMatch=(System.Double)`, `Convert.ToByte(int):-1
  → System.OverflowException` (host process stayed alive, message
  decoded cleanly from `MonoException + 0x18` confirming the shared
  System.Exception layout assumption).

- [x] **19c** Instance methods + string marshaling — landed 2026-05-07,
  build #215. Lifts the static-only / no-string restrictions on both
  backends with byte-identical wire-shape changes:
  * **Instance dispatch** — `instance` arg parsed as hex; routing rule:
    static method → instance silently ignored (callers may always send
    one); non-static method + `instance == 0` → reject with
    `instance_required_for_non_static`; otherwise forward as `obj` to
    `*_runtime_invoke`. The blanket `instance_methods_not_supported_in_19{a,b}`
    rejects are gone.
  * **String args** — wire `type: string` allocates a managed string via
    `il2cpp_string_new(utf8)` / `mono_string_new(domain, utf8)` and slots
    the resulting `Il2CppString*` / `MonoString*` pointer for runtime
    invoke. The `string_*` resolver bindings that were pre-bound in 19a/b
    become load-bearing here. Errors:
    `il2cpp_string_new unbound` / `mono_string_new unbound` (export
    missing), `il2cpp_string_new returned null` /
    `mono_string_new returned null` (alloc faulted).
  * **String returns** — `returnType == "System.String"` decodes the
    managed `+0x10 length / +0x14 UTF-16 chars` layout into UTF-8 using
    the existing `DecodeManagedString` helper each invoker carries (no
    factor-out into shared header — the helper has no SEH and the
    duplication keeps each backend self-contained, matching the per-.cpp
    SEH-leaf pattern this codebase already follows).
  * **GC-root caveat** documented in `lua-invoke.md` — managed pointers
    returned by `callMethod` are valid for the next call you make right
    now. Best practice: `findInstances` → `callMethod` in one Lua block.
  * Reference return values continue to surface as
    `returnValue: "0x..."` hex string; only `System.String` got promoted
    from address-encoded to UTF-8-decoded.
  * Smoke test `tools/Test-CallMethod.ps1` gains four new scenarios:
    `StringConcat` (string args + string return), `StringParse` (string
    arg + non-string return), `StringThrow` (string arg + managed
    exception), `Instance` (caller supplies `-InstanceAddress 0x...` +
    class triple + method). **Three string scenarios verified live on
    both backends 2026-05-07** (build #220): IL2CPP/SaGa F2R + Mono/BongoCat
    both pass `Parse("42") → 42`, `Concat("Hello, ", "World") →
    "Hello, World"`, `Parse("not a number") → FormatException`. Instance
    dispatch verified live on Mono (Farthest Frontier) —
    `ItemBundle.get_numberOfItems()` returned `1` against a live
    instance address.
  * **Marshaling fix mid-19c** — initial implementation used the
    "uniform" convention from Mono docs (params[i] = &slot for both
    value and reference types). Parse("42") returned FormatException
    because the trampoline read &slot as if it were a managed string.
    Switched to the non-uniform convention used by BepInEx /
    MelonLoader / Il2CppInterop: value types still pass &slot, but
    reference types pass the object pointer **directly** in params[i].
    Fixed in build #220.

- [x] **19d** UI surface — landed 2026-05-07, build #222. Right-click any
  row in the `MethodOverloadsWindow` grid → "Call method…" opens the new
  `MethodCallWindow` modal seeded from that overload's signature
  (`signatureMatch` pinned to the row's exact signature string, paramTypes
  + paramNames threaded into one editor row each). Result panel renders
  the typed return value (UTF-8 string for `System.String`, signed/unsigned
  int / float for primitives, hex address for reference types), captured
  managed exception ("Type: message"), or protocol error per the
  switch-on-`returnType` rule from `lua-invoke.md`.
  * **Architecture**: new `CallArgFactory` in `UnityDumpUI.Core` wraps the
    AOT-safe per-primitive `JsonSerializer.SerializeToElement` calls so
    `ProtocolJsonContext` stays internal — VMs in `UnityDumpUI` build wire
    payloads via a public seam (`Null()` / `Bool(bool)` / `Int(tag, long)`
    / `UInt(tag, ulong)` / `Float(tag, double)` / `String(string)` /
    `Address(hex)`). One method per wire `type` tag from `lua-invoke.md`.
  * **Editors**: v1 uses a single TextBox per param row plus a "null"
    checkbox enabled for reference types. Parsing happens client-side
    against the formal type (range-checked for int/uint widths, culture-
    invariant for floats, `bool.TryParse` for booleans, single-character
    or numeric code-unit for `System.Char`). Invalid input surfaces as
    `arg '<name>' (<type>): <reason>` in the result panel before the
    request leaves the UI. Smart-typed editors (NumericUpDown for ints,
    ComboBox for enums via `getEnumValues`) deferred to 19d-2.
  * **Instance address** field is pre-bound TwoWay; empty = static call
    (DLL ignores on a static overload, requires on a non-static one).
  * **AOT publish stays clean** — 0/0 trim/AOT analyzer warnings under
    all four analyzers; `DiscreteUI.exe` 38.1 MB.
  * **Inspector cross-tab** ("Call instance method…" right-click on a
    walked instance → pre-fills the `this` address and opens dialog
    scoped to the instance's class) deferred to 19d-2 — the dialog
    accepts an instance address typed manually for now, which covers the
    direct workflow.
  * Files: new [`MethodCallWindow.axaml{,.cs}`](../ui/UnityDumpUI/Views/MethodCallWindow.axaml),
    new [`MethodCallViewModel.cs`](../ui/UnityDumpUI/ViewModels/MethodCallViewModel.cs),
    new [`CallArgFactory.cs`](../ui/UnityDumpUI.Core/Services/CallArgFactory.cs), DTOs
    in `ProtocolModels.cs` (`CallArgRequest` / `CallMethodResult` /
    `CallMethodMethodInfo`) + matching `ProtocolJsonContext` registrations.

- [x] **19d-2** Polish — landed 2026-05-07, build #234. Two UX
  improvements on top of 19d that don't change the wire layer:
  * **Auto-skip overloads modal on count==1** — Methods grid right-click
    "Call method… (pick overload)" now probes `findMethodInstances`
    first; when the result has exactly one entry, jumps straight to
    `MethodCallWindow` seeded from that overload, bypassing the picker.
    Multi-overload methods (`Mathf.Abs`, `String.Concat`) still drop
    into the picker. "Find overloads…" keeps today's always-show-modal
    behaviour because the user explicitly wants the AOB / Copy panel.
    Implemented as a split handler pair in `MainWindow.axaml.cs`
    (`OnMethodCallMethodClicked` for the fast path,
    `OnMethodFindOverloadsClicked` unchanged) plus shared row-resolution
    and overloads-VM-construction helpers (`ResolveMethodRow`,
    `BuildOverloadsVm`).
  * **Inspector cross-tab "Call instance method…"** — right-click any
    instance in the Inspector's Instances ListBox → "Call method on
    this instance… (switch to Methods)" snaps `SelectedInstance` to the
    right-clicked row and switches the right-pane TabControl to
    Methods. From there, the existing Methods-grid right-click
    (above) opens MethodCallWindow with the `Instance address` textbox
    pre-filled. The cross-tab handoff is scope-checked
    (`GetDefaultMethodCallInstance` returns the inspector's selected
    instance ONLY when `Browser.SelectedClass` matches `Inspector`'s
    `TargetAssembly + TargetNamespace + TargetName` — strict equality
    on all three) so a stale inspector instance can't bleed into a
    method call on a different class.
  * Threading: `MethodOverloadsViewModel` constructor gains an optional
    `defaultInstanceAddress` param that's forwarded to
    `MethodCallViewModel` when a row's "Call method…" is invoked.
    `MethodCallViewModel` already had a `defaultInstanceAddress`
    constructor param (added in 19d for future use); 19d-2 finally
    populates it from a real source.
  * AOT publish stays clean — 0/0 trim/AOT analyzer warnings;
    `DiscreteUI.exe` 38.3 MB.
  * Files: [`MainWindow.axaml.cs`](../ui/UnityDumpUI/Views/MainWindow.axaml.cs)
    (split handlers + helpers + Inspector instance handler),
    [`MainWindow.axaml`](../ui/UnityDumpUI/Views/MainWindow.axaml)
    (rewire menu Click + add Instances ListBox ContextMenu),
    [`MethodOverloadsViewModel.cs`](../ui/UnityDumpUI/ViewModels/MethodOverloadsViewModel.cs)
    (new `DefaultInstanceAddress` property + ctor param),
    [`MethodOverloadsWindow.axaml.cs`](../ui/UnityDumpUI/Views/MethodOverloadsWindow.axaml.cs)
    (forward `DefaultInstanceAddress` into `MethodCallViewModel`),
    [`Strings/en.axaml`](../ui/UnityDumpUI/Resources/Strings/en.axaml)
    (new `Inspector.CallMethodOnInstanceMenu` resource).
  * Deferred to 19d-3: smart-typed editors (NumericUpDown for sized ints,
    ComboBox for enums via `getEnumValues`).

- [x] **19d-3** Smart-typed editors — landed 2026-05-19. Replaces the
  single TextBox in `MethodCallWindow`'s Value column with a per-row
  editor chosen by a new `MethodCallEditorKind` enum:
  * **NumericUpDown** for `System.{S?Byte, Int16, UInt16, Int32, UInt32,
    Int64, UInt64}` — bounded to each type's `[MinValue, MaxValue]` so
    out-of-range input is clamped at the editor instead of bouncing back
    as a `"arg '...' (System.Int32): out of range [...]"` after Call.
    `decimal`-backed (the entire sized-int range fits in `decimal`). The
    setter forwards the value as invariant-culture text into
    `ValueText` so the existing wire serializer
    (`MethodCallViewModel.TryBuildArg`) keeps working unchanged.
    `System.Single` / `System.Double` stay on TextBox — Avalonia's
    `NumericUpDown` is `decimal`-backed, which would silently truncate
    `double`s outside the decimal range (and most game inputs don't need
    a stepper anyway).
  * **ComboBox** for enum-typed params — populated from
    `getEnumValues(assembly, namespace, name)`. Each row's
    `MethodCallEnumChoice` carries the member name, display label
    (`"Game = 1"`), and the invariant-culture numeric `WireValue` written
    back to `ValueText` on selection. The probe also captures
    `underlying_type` so the wire `type` tag becomes the underlying
    integer type (`int32` / `uint32` / etc.) instead of the CLR enum
    class name — fixes a latent issue where enum params were being
    encoded as `type:"address"` via TryBuildArg's unknown-type
    fallthrough.
  * **Async enum probe** lives in `MethodCallViewModel.InitializeAsync`
    and is kicked from `MethodCallWindow.Opened`. Per param: skip
    primitives + nested types (`+`) + generics (`<`) + empty names; try
    the method's own assembly first, then `UnityEngine.CoreModule` when
    the namespace starts with `UnityEngine`. Soft failures (class not
    found, not an enum, Mono missing `mono_field_get_data`) silently
    leave the row on TextBox.
  * Files: new DTOs in
    [`ProtocolModels.cs`](../ui/UnityDumpUI.Core/Services/ProtocolModels.cs)
    (`GetEnumValuesResult` / `EnumValueEntry`), registered in
    [`ProtocolJson.cs`](../ui/UnityDumpUI.Core/Services/ProtocolJson.cs)
    so AOT publish stays clean; new `GetEnumValuesAsync` in
    [`BackendAdapter.cs`](../ui/UnityDumpUI.Core/Services/BackendAdapter.cs);
    new `MethodCallEditorKind` enum + `MethodCallEnumChoice` plus
    extended `MethodCallParamRow` (numeric range / NumericValue setter /
    EnumChoices / SelectedEnumChoice / EnumUnderlyingType) and
    `InitializeAsync` + `ProbeEnumAsync` in
    [`MethodCallViewModel.cs`](../ui/UnityDumpUI/ViewModels/MethodCallViewModel.cs);
    multi-editor `DataTemplate` in
    [`MethodCallWindow.axaml`](../ui/UnityDumpUI/Views/MethodCallWindow.axaml);
    `Opened` handler in
    [`MethodCallWindow.axaml.cs`](../ui/UnityDumpUI/Views/MethodCallWindow.axaml.cs).
  * Build clean (0 warnings); 163/163 tests pass; AOT publish 0/0 trim/AOT
    analyzer warnings; `DiscreteUI.exe` 38.7 MB.
  * **Live click-through smoke 2026-05-19 (user-confirmed, multi-target)**:
    * IL2CPP — `MainGame.PARTY_WORK.SetValue(System.UInt32 ofs,
      System.Byte value)`. NumericUpDown rendered for both rows with
      spinners; manual `6` into `ofs` accepted. Null checkbox correctly
      disabled on both value-type rows (existing `IsReference` gating).
    * Mono (BongoCat / Unity 2022+) — `UnityEngine.Mathf.Abs(System.Int32
      value)`. NumericUpDown rendered for the Int32 row; manual `99`
      accepted. Confirms parity across backends.
    * ComboBox / enum path **not yet exercised live** — pending a method
      with an enum param.
    * **19d-3 polish (same-day follow-up, 2026-05-19)** —
      `System.Boolean` now renders `AutoCompleteBox` with `true`/`false`
      suggestions (FilterMode=StartsWith, MinimumPrefixLength=0 so the
      dropdown surfaces on focus); free text still accepted (the
      existing `bool.TryParse` path consumes case-insensitive variants).
      New 5th `MethodCallEditorKind.Bool`; new `IsEditorBool` derived
      prop + `BoolSuggestions` static list on `MethodCallParamRow`.
      Build clean, 163/163 tests pass, AOT publish 0/0 warnings.

- [x] **19e** Lua bridge — landed 2026-05-07, build #224. New
  `scripts/unity_invoke.lua` (18 KB; standalone — does NOT `require` the
  sibling scripts). Closes the original CLAUDE.md goal "invoke managed
  methods from CE Lua with marshaled args / return values." Public API:
  ```lua
  -- Static call:
  local r = callUnityMethod("UnityEngine.CoreModule", "UnityEngine",
                            "Mathf", "Abs",
                            { {type="int32", value=-7} })
  -- → r.ok=true, r.returnValue=7, r.returnType="System.Int32"

  -- Instance call:
  local r = callUnityMethod("Assembly-CSharp", "MainGame", "PLY_WORK",
                            "AddGrowHpMax",
                            { {type="int32", value=10} },
                            { instance = "0x16F8CEA0BE0" })
  ```
  Same `\\.\pipe\UnityDumpBfx` retry scaffolding + per-RPC
  `processMessages()` + `sleep(1)` yield as the sibling scripts.

  Implementation details:
  * Ships its own JSON serializer (~50 LOC, request-shape-specific) on
    top of the response-shape-specific parser the sibling scripts also
    carry. Needed because `callMethod`'s `args` is a nested JSON array
    of `{type, value}` objects — flat-only `{"k":v}` builders the other
    scripts use can't express it.
  * Arg builder helpers — `argNull` / `argBool` / `argByte` / `argSByte`
    / `argInt16` / `argUInt16` / `argInt32` / `argUInt32` / `argInt64`
    / `argUInt64` / `argFloat` / `argDouble` / `argChar` / `argString`
    / `argAddress` — one per wire type tag from `lua-invoke.md`. Keeps
    callers from hand-typing `{type="int32", value=...}` for every arg.
    `argChar("X")` accepts a 1-char string and converts to the code
    unit; `argAddress` accepts hex string OR Lua number.
  * Result helpers: `asAddress(v)` decodes a `"0x..."` returnValue back
    to a Lua number; `prettyPrint(r)` logs a one-line human summary;
    `pcallUnityMethod(...)` is the non-throwing variant.
  * `findOverloads(asm, ns, class, method, sigMatch?)` wraps
    `findMethodInstances` so users can list every overload of a method
    and pick the right `signatureMatch` from CE Lua without leaving the
    console (e.g. `Mathf.Abs` has 6).
  * Header comment carries quick-start + full API reference + the same
    overload-disambig + GC-root caveats from `lua-invoke.md` so the
    file is self-documenting (the dofile target is itself the docs).
  * `scripts/README.md` gains a Phase 19e workflow step (item 6) +
    install-layout entry.

### Wire shape

```jsonc
// Request
{
  "id": 1,
  "cmd": "callMethod",
  "args": {
    "assembly":       "Assembly-CSharp",
    "namespace":      "MainGame",
    "name":           "Player",
    "method":         "TakeDamage",
    "signatureMatch": "Int32, String",   // optional; same semantics as Phase 18
    "instance":       "0x16F8CEA0BE0",   // null/omit for static methods
    "args": [
      { "type": "int32",  "value": 10 },
      { "type": "string", "value": "fire" }
    ]
  }
}

// Response (success)
{
  "id": 1,
  "result": {
    "ok":          true,
    "returnType":  "System.Void",
    "returnValue": null,
    "exception":   null,
    "elapsedMs":   3
  }
}

// Response (managed exception caught)
{
  "id": 1,
  "result": {
    "ok":          false,
    "returnType":  "System.Int32",
    "returnValue": null,
    "exception":   "System.NullReferenceException: Object reference not set to an instance of an object."
  }
}
```

Wire `type` tags for v1: `bool`, `byte`, `sbyte`, `int16`, `uint16`,
`int32`, `uint32`, `int64`, `uint64`, `float`, `double`, `char`,
`string`, `address`, `null`.

### Acceptance

- IL2CPP: `callMethod("UnityEngine.CoreModule", "UnityEngine", "Mathf",
  "Abs", [{int32:-7}])` returns `{ok:true, returnValue:7,
  returnType:"System.Int32"}` on SaGa F2R.
- Mono: same shape against a Mono game's `System.Math.Abs(int)`.
- Instance call: `Vector3.ToString()` against a live instance returns a
  formatted string (e.g. `"(1.0, 2.0, 3.0)"`).
- String round-trip: any method that accepts `string` and returns
  `string` echoes the input correctly across the wire.
- Exception path: calling `Convert.ToInt32` on a non-numeric string
  returns `ok:false` + the exception's `Type: message` shape — host
  process stays alive, no log scrubbing required.
- `signatureMatch` disambiguates between overloads exactly as Phase 18
  does (e.g. picks `Vector3.Set(float, float, float)` over
  `Vector3.Set(Vector3)`).
- `tools/Test-CallMethod.ps1` covers the canonical positive (return
  value), negative (managed exception), and overload-disambiguation
  cases.
- AOT publish stays 0/0 warnings under all four trim/AOT analyzers.

### Caveats / non-goals

- **GC roots**: an instance address obtained from `findInstances` /
  `walkInstance` is valid for the call you make right now; a previous
  GC that ran between the address read and the invoke can have moved
  the object (Mono moving GC). `lua-invoke.md` will document the
  recommended chain `findInstances` → `callMethod` in a single Lua
  block when correctness matters.
- **Thread**: invoke runs on the pipe worker thread inside a single
  `ThreadAttachGuard` scope. Methods that assume the Unity main thread
  (most engine APIs touching the scene graph) will misbehave. v1
  doesn't marshal back to the game's main thread; users should target
  methods that don't touch Unity engine state. v2 follow-up could add a
  "queue on next Update" path via a hook — out of scope.
- **`ref` / `out` parameters**: deferred. Unity gameplay code rarely
  uses these; we'll add when a user asks.
- **Generic method instantiations**: deferred. The IL2CPP runtime
  registers concrete instantiations; v2 could expose a way to pick one,
  but v1 only invokes non-generic methods (or already-instantiated
  generics whose `MethodInfo*` is in the class's method table).

Estimated effort: ~3 days for v1 (19a + 19b in one day, 19c in one day,
19d + 19e in one day).

---

## Phase 20 — Scene / GameObject explorer + Object.name search

**Goal**: Reproduce the most useful concept from
[`vendor/UnityExplorer/`](../vendor/UnityExplorer/) — its Scene Explorer
tab — in our out-of-process architecture. UnityExplorer's full Scene
Explorer needs in-process Unity engine API access (it's a BepInEx /
MelonLoader plugin); we get an equivalent surface for free by composing
already-shipped Phase 19 (`callMethod`) and Phase 9.5 (`walkInstance`)
primitives — no DLL changes needed.

### Why now

Phase 19 just landed `callMethod`. The single largest user-visible gap vs
UnityExplorer / RuntimeUnityEditor is **"how do I find which GameObject a
managed value belongs to"** — today users have `findInstances` (by class)
and `whatIsAt` (reverse lookup by address), but no way to walk the actual
scene-graph tree the way Unity programmers think. Adding this is cheap
because all the pieces exist.

### Scope (v1)

In:
- `scripts/unity_scene.lua` — public entry points
  - `getActiveSceneRoots()` → returns array of `{ address, name, childCount }`
    by calling `SceneManager.GetActiveScene().GetRootGameObjects()` then
    walking each `GameObject.transform` for `childCount`
  - `getChildren(gameObjectAddr)` → traverse `Transform.GetChild(i)` for
    `i = 0..childCount-1`, return `{ address, name, childCount }` for each
  - `getComponents(gameObjectAddr)` → traverse `GameObject.GetComponents()`
    array (return as `findMethodInstances`-style class+addr pairs so the
    caller can pass into our existing inspector)
  - `findByName(pattern)` — substring match across the active scene tree.
    Recursive walk via `getActiveSceneRoots` + `getChildren`. Cap at
    configurable max-depth (default 10).
- DiscreteUI: new "Scene" tab in MainWindow
  - TreeView control. Lazy-expand via `getChildren`. Each row shows
    `name + (componentCount?)`. Cells colored by `activeInHierarchy`
    (gray = inactive).
  - Filter input box at top → calls `findByName` and flattens result to
    a list view.
  - Right-click any row → "View instance" (flips to existing Inspector
    tab with `SelectedInstance` set) and "Show components" (drill into
    `getComponents` result, each becomes a child).

Out (v2 candidates):
- `DontDestroyOnLoad` scene + `HideAndDontSave` scene enumeration —
  needs different API path (`Object.FindObjectsOfTypeAll` filtered by
  scene). Defer.
- Mouse-pick (`Physics.Raycast` / `GraphicRaycaster`) — requires
  in-process. Out of scope.
- Live update / scene-change listener — would need a managed callback,
  also out of scope. v1 is "press refresh".

### Sub-phases

- [x] **20a** `unity_scene.lua` Lua bridge (file landed 2026-05-08, hardened
  same day across THREE iterations of live testing on SaGa F2R) —
  implements `getName(addr)`, `getAllGameObjects(opts)`,
  `findByName(pattern, opts)`, `getActiveSceneRoots(opts)`,
  `getChildren(addr)`, `getComponents(addr)`. Standalone; pure
  composition over `callMethod` + `walkArray` (no longer uses
  `findInstances` for the GameObject enumeration entry point — see
  issue 3 below). **Three issues surfaced + fixed**:
    1. *Wire envelope unwrap missed* — `rpc()` returned `{id, result}` raw
       envelope instead of unwrapping `result`; every public function
       saw nil fields and returned 0/empty. Fixed.
    2. *Main-thread marshaling not respected* — `getActiveSceneRoots`
       on a 1024-GameObject scene issued ~2048 sustained icalls
       (`Transform.get_parent` etc.) and locked up the game. This is
       the v1 limitation called out in `lua-invoke.md` ("v1 invokes on
       the pipe worker thread"). Fixed by lowering defaults and adding
       a 1 ms `sleep()` per-RPC throttle. **`getActiveSceneRoots` /
       `getChildren` / `getComponents` are now formally EXPERIMENTAL
       until 20-MT lands.**
    3. *`findInstances UnityEngine.GameObject` returns metadata
       records, not instances* — heap-scan heuristic ("first qword ==
       classPtr") matches IL2CPP RGCTX cache entries that legitimately
       store the GameObject classPtr at +0x00 AND +0x18 with garbage at
       +0x10. Even a two-layer Lua-side validator (klass match +
       m_CachedPtr range check) couldn't distinguish them — 622/622
       survivors returned `native_crash_in_runtime_invoke` on get_name
       in SaGa F2R's equipment screen. Forensic confirmed identical
       layout signature across all samples (+0x18 = classPtr again,
       +0x10 = constant 0x41BC0600). `whatIsAt` shares the heuristic
       so reverse lookup couldn't disambiguate either. **Pivot**:
       `getAllGameObjects` now uses `Object.FindObjectsOfTypeAll(Type)`
       — Unity's authoritative engine enumeration, zero false
       positives. Cost shape: 2 callMethod + 1 walkArray, constant
       regardless of N. Pattern matches what UnityExplorer / RUE use
       in-process. The `findInstances`-based path is preserved as
       deprecated `_isValidInstance` for users who explicitly want the
       old strategy on user classes (where it's still fine). All
       three issues logged in `lessons-learned.md`.
    4. *IL2CPP code stripping removes most enumeration APIs* —
       `Resources.FindObjectsOfTypeAll`, `SceneManager.get_sceneCount`,
       `GameObject.FindWithTag`, the 1-arg `Object.FindObjectsOfType(Type)`
       overload — ALL stripped on SaGa F2R's shipped IL2CPP build.
       Survivors: `Object.FindObjectsOfType(Type, bool)` (2-arg),
       `GameObject.Find(string)`, `Camera.get_main()`,
       `SceneManager.GetActiveScene()` + `Scene.GetRootGameObjects()`
       (last one needs Scene value-type marshaling — Phase 19 v2).
       Code path updated to call the surviving 2-arg overload with
       `includeInactive=true` and a precise `(System.Type, System.Boolean)`
       signatureMatch (the previous `(System.Type)` didn't substring-match
       the 2-arg signature because Type is followed by a comma, not a
       close paren). **Untested in this session** — left for the next
       run after stripping was confirmed mid-test. Logged in
       `lessons-learned.md` (two new entries: IL2CPP stripping +
       signatureMatch close-paren trap).
- [x] **20a-2** Verify the enumeration recipe end-to-end. **Closed
  2026-05-18** with cross-version + cross-backend PASS via the new
  `tools/Test-FindObjectsOfType.ps1` smoke (4-step recipe replay,
  no CE / Lua required):
  - **SaGa F2R** (IL2CPP, Unity 2021): picked the 2-arg
    `FindObjectsOfType(Type, bool)` overload — survived stripping as
    expected. 14,942 GameObjects in scene; sampled names came back as
    real "SoundObject_SeCh11..15" instances, 0 native_crashes.
  - **BongoCat** (Mono, Unity 2022+): `FindObjectsOfType` has 0
    overloads (entirely removed in Unity 2022+, not just deprecated).
    Picked the 3-arg `FindObjectsByType(Type, FindObjectsInactive,
    FindObjectsSortMode)` replacement. 40,752 GameObjects; sampled
    names "Background" ×5, 0 native_crashes.
  - **Enum marshaling fact-check** (bonus): the two enum params on the
    Unity 2022+ API marshal as plain `int32` over the wire (ABI is
    identical to underlying type — no DLL changes needed, no new
    `enum` wire tag). `Include=1`, `None=0`.
  - `scripts/unity_scene.lua` `getAllGameObjects` `tries[]` updated
    to a three-entry priority list: 2-arg OfType → 1-arg OfType →
    3-arg ByType. The first match wins, so production calls land on
    the historically-preferred path on each Unity version.
- [x] **20-MT** ✅ **Shipped + live-smoked 2026-05-20.** DLL-side
  main-thread marshaling. MinHook detour on `mono_runtime_invoke` /
  `il2cpp_runtime_invoke` piggybacks on Unity's per-frame engine tick
  to drain a work queue in main-thread context. `callMethod` now
  accepts `main_thread: true` (default false for Phase 19 back-compat)
  + optional `main_thread_timeout_ms` (default 5000); `unity_scene.lua`
  bridges flip it on automatically. New `getDispatcherStats` RPC +
  `main_thread` sub-object on handshake/scan surface `hook_installed`
  / `is_healthy` / `tick_count` / `drain_count` etc. for diagnostics.
  Live-PASS matrix (5 targets across Mono + IL2CPP × x64 + x86 ×
  Unity 5.4 → 6000.3): BongoCat (Mono x64 / Unity 6000.2), SaGa F2R
  (IL2CPP v29 x64 / Unity 2021.3), Songs of Silence (IL2CPP v39 x64
  / Unity 6000.3), **LEAVES (Mono x86 / Unity 5.4.3 — surfaced the
  x86 cdecl calling-convention bug)**, **Legend of Mortal / 活俠傳
  (Mono x86 / Unity 2020.3)** — 1000-iteration `Mathf.Abs` burst on
  each, 0 errors, 0 timeouts, drain_count == submit_count == 1000,
  no game crashes. **Mid-implementation discovery**:
  nested `mono_thread_attach` on Unity main thread destroys Unity's
  own thread handle (reproducing the build #54 BongoCat crash);
  `ThreadAttachGuard` now consults
  `MainThreadDispatcher::IsCurrentlyDrainingOnMainThread()` and
  becomes a no-op on main thread. Two lessons recorded in
  [lessons-learned.md](lessons-learned.md): nested-attach trap +
  Unity 2018 focus-pause. Files: `dll/src/shared/MainThreadDispatcher.{h,cpp}`,
  `dll/src/mono/MonoMainThreadHook.{h,cpp}`,
  `dll/src/il2cpp/IL2CPPMainThreadHook.{h,cpp}`, `dll/vendor/minihook/`
  (existing vendor, now wired into CMakeLists). 279/279 xUnit pass
  (+3 new wire-shape tests).
- [ ] **20-HEAP** *(new sub-phase, fallback if Unity APIs aren't
  reachable)* DLL-side managed-heap walker that doesn't depend on Unity
  managed APIs at all — walks libgc heap sections via
  `GC_get_heap_section_count` / `GC_get_heap_section_at`, scans for
  IL2CPP object headers (klass match + valid m_CachedPtr), returns
  authoritative instance lists. Bypasses both `findInstances`'s
  false-positive problem AND IL2CPP code stripping. New wire RPC like
  `walkManagedHeap(classFilter)`. Multi-day; only needed if 20a-2
  doesn't work on the user's target games.
- [x] **20b** ✅ **Shipped 2026-05-20.** DiscreteUI Scene tab — new
  `SceneExplorerViewModel.cs` (+ inline `SceneNode` model) +
  `SceneEnumerator.cs` in Core. Filter-driven TreeView (no separate
  axaml file — Scene TabItem added inline to MainWindow.axaml). Top bar
  has pattern TextBox + Limit NumericUpDown (1–500, default 200) +
  Search button. Initial state empty; Search calls
  `FindObjectsOfType*(Type)` with the same priority-list as
  `unity_scene.lua` (Unity 2022+ 3-arg `FindObjectsByType` → 2-arg
  `FindObjectsOfType(Type, bool)` → 1-arg `FindObjectsOfType(Type)`),
  then `walkArray` for the address list, then per-element `Object.get_name`
  for the display label. Each TreeView item lazy-expands via
  `GameObject.transform` → `Transform.childCount` → loop
  `Transform.GetChild(i)` → child `gameObject` → `get_name`. Every
  Unity engine icall is routed through Phase 20-MT
  (`mainThread: true`) so even big scenes don't freeze. Right-click
  context menu: Inspect (Phase 20c) / Copy address / Copy name.
- [x] **20c** ✅ **Shipped 2026-05-20.** Cross-tab handoff — Scene
  right-click "Inspect this GameObject" raises
  `SceneExplorerViewModel.NavigateToInspectorRequested(address)`;
  `MainWindowViewModel.OnSceneNavigateToInspector` calls
  `Inspector.SetTarget(UnityEngine.CoreModule, UnityEngine, GameObject)`
  + `Inspector.FocusOnInstance(addr)` + `SelectedDetailTabIndex = TabIndexInspector`.
  Same plumbing pattern as the Phase 19d-2 Methods → Inspector hop.
- [x] **20d** ✅ **Verified live 2026-05-20** on two targets:
  * **BongoCat** (Mono / Unity 6000.2 / x64): Scene tree expanded,
    lazy children loaded, empty-expansion handled correctly (leaf
    GameObjects show empty Children — Avalonia auto-hides chevron
    on empty collection). Status bar surfaced "X match(es) for Player
    of Y scanned (Z in scene, …ms; via Unity 2022+ 3-arg ByType)".
  * **SaGa Frontier 2 Remastered** (IL2CPP v29 / Unity 2021.3 / x64):
    Scene enumeration + Inspect this GameObject cross-tab handoff PASS;
    Inspector populates correctly + the existing walkInstance cascade
    fires automatically.
  Older-Unity quirk noted (and Symbol Finder's earlier `UnityEngine.CoreModule`
  vs `UnityEngine` monolithic-fallback applies symmetrically — but
  SceneEnumerator uses 3-arg ByType which is Unity 2022+ only, so
  Unity 5.x / 2017.1 GameObject enumeration via SceneEnumerator
  falls back to the 1-arg `FindObjectsOfType(Type)` survival path).

Estimated effort: ~½–1 day total. No DLL changes. No new wire
commands.

---

## Phase 21 — Inspector field write-back

**Goal**: Close the read/write asymmetry in the Inspector. Today
`walkInstance` reads field values; there is no path to write them back
beyond manually opening Cheat Engine's memory viewer. UnityExplorer
exposes per-field "Apply" buttons; we should too, since the underlying
write is just "address + offset + type + value" — easier than the read
path which had to handle pointer chains and value-type inlining.

### Scope (v1)

In:
- New wire RPC `setInstanceField(instanceAddress, fieldOffset, type, value)`.
  - `type` matches the existing wire-type taxonomy from Phase 19's
    `callMethod` arg builders: `int8/16/32/64`, `uint8/16/32/64`, `float`,
    `double`, `bool`, `address` (writes a raw pointer).
  - `value` is JSON-typed (number / bool / `"0x..."` for address).
  - Backend writes `sizeof(type)` bytes via SEH-protected `SafeWriteBytes`
    on both Mono and IL2CPP. No runtime reflection needed — the field
    offset is already known from the prior `walkInstance` response.
- UI: existing read-only Inspector field cells become editable on
  double-click. Apply button per row commits via the new RPC. Edit
  state shown via row highlight; failed writes show inline error.
- CE Lua bridge: extend `unity_invoke.lua` (or pair `unity_dump.lua`)
  with `setField(addr, off, type, value)` wrapper.

Out (v2):
- Reference-type field write (writing a managed `string` or object
  pointer) — needs `il2cpp_string_new` / `mono_string_new` and proper
  GC root handling, identical to Phase 19c's marshaling. Defer one
  sub-phase.
- Value-type-by-value (Vector3, Color) — need full struct marshaler.
  Defer to Phase 19's "boxed value-type args" v2 follow-up.
- Atomic batch write (multi-field commit) — out of scope.

### Sub-phases

- [x] **21a** DLL handler — `setInstanceField` in
  `dll/src/shared/ExportAPI.cpp` (build #326). One backend-agnostic
  handler (~150 lines) sitting right before the Mono/IL2CPP `#if`
  split — no Mono/IL2CPP API needed since the write is just process
  memory. Accepts both wire-short types (`int32`) and CLR-style names
  (`System.Int32`); marshals each type into an 8-byte stack buffer +
  per-type width then calls `Memory::SafeWriteBytes`. Returns `ok`
  + `written` + echoed `address` / `fieldOffset` / `fieldAddress` /
  `type` on success; soft `error` + `hint` on unwritable / out-of-range /
  unsupported-type. PowerShell smoke
  [`tools/Test-SetInstanceField.ps1`](../tools/Test-SetInstanceField.ps1)
  saves bytes via `readMemory`, calls `setInstanceField`, reads back
  via `readMemory` to verify the new value, then restores the
  original bytes unless `-NoRestore`. Spec recorded in
  [pipe-protocol.md §"setInstanceField"](pipe-protocol.md).
- [x] **21b** UI Inspector field write modal (build #331, AOT clean).
  Right-click any Inspector row → "Write value…" context-menu item →
  opens new `FieldWriteWindow` modal. Per-row editor flavor picked by
  new `Core.Services.InspectorWriteTypeMap` helper (`Resolve(clrType)`
  → `{ Supported, WireType, EditorKind, [Min,Max], ByteSize,
  IsUnsigned }`): NumericUpDown for sized ints (clamped to type
  range), TextBox for float/double (decimal can't represent full
  double range — same call as Phase 19d-3), AutoCompleteBox with
  `true`/`false` suggestions for bool, TextBox for char + address.
  Reference / value-struct / collection rows open the modal but
  surface an unsupported banner instead of letting Write fire (gated
  by `TypeInfo.Supported`). On success the VM fires `Written` event
  → handler runs `Inspector.RefreshWalkCommand` so the row updates.
  New `SetFieldValueFactory` paralleling `CallArgFactory` keeps the
  source-gen JSON context internal to Core. New `BackendAdapter
  .SetInstanceFieldAsync` + `SetInstanceFieldResult` DTO + source-gen
  registration in `ProtocolJson.cs`. Tests: +6 fake-pipe RPC smoke
  (happy path + bool + float + address + soft-error + range-error) +
  31 `InspectorWriteTypeMap` classifier (theory rows) +
  unsupported-type guard. 238/238 pass.
- [x] **21c** CE Lua bridge wrapper — extended
  [`scripts/unity_invoke.lua`](../scripts/unity_invoke.lua) with
  `setField(fieldAddr, wireType, value)` + `setInstanceField(
  instanceAddr, fieldOffset, wireType, value)` (1 KB code growth).
  Bridge-side type marshaling mirrors the DLL — accepts both
  shortform tags and CLR names; bool tolerates `0/1/true/false/
  "true"/"false"`; address tolerates Lua number or `"0x..."`/bare
  hex. Pipe-level errors throw (matching the rest of the script);
  soft errors land in `result.error` so consumers can branch on
  `result.ok`. Documented in
  [lua-invoke.md §"Phase 21b — field write-back"](lua-invoke.md)
  with worked examples.
- [ ] **21d** Live verification — pending user click-through on a
  Mono + an IL2CPP target. Suggested recipe:
  * **IL2CPP** — SaGa F2R → DiscreteUI Pick & Inject → Browser
    selects `MainGame.PLY_WORK` → Inspect → right-click any Int32
    field (e.g. `HP`) → "Write value…" → enter `9999` → Write.
    Verify CE address-list value updates in real time and the
    inspector row shows the new value after the auto-refresh.
  * **Mono** — BongoCat or Farthest Frontier Mono → same flow,
    but verify against a primitive field on a discoverable class.
  * **CE Lua** — open CE Lua console, `dofile([[..\\unity_invoke.lua]])`,
    call `setField("0x...", "int32", 999)`, watch the value
    change. Compare the round-trip output against the
    `readMemory` snapshot the Lua call returns.

### GC-root caveat

A managed pointer field that the user wants to overwrite may shift
between the read (which gave us the address + offset to display) and
the write (the user's Apply click). For value-type fields, the offset
is stable — the embedded value is rewritten in place. For reference-type
fields (deferred to v2), this is the same caveat as Phase 19c's return
addresses — chain operations within one request when correctness matters.

Estimated effort: ~½ day + tests.

---

## Phase 22 — Live CT Population via AOBMaker ✅ SHIPPED 2026-05-12

**Goal**: Let end-users consume Discrete via the only CE interaction
they care about — enable/disable AA-script checkboxes. Pivoted away
from "build a standalone `.CT` from scratch" (Ascii85 + XML serializer
scrapped) after auditing `D:\Github\AOBMaker\plugins\CEPlugin\src\protocol.h`
+ `D:\Github\UE5CEDumper\ui\UE5DumpUI\Services\AobMakerBridgeService.cs`.

**Model shipped**: user opens CE 7.6+ with AOBMaker plugin loaded, opens
(or starts) any CT, then DiscreteUI fires AOBMaker pipe commands that
inject our 7 bridge lua modules into the table's `<Files>` and create
matching AA-script memory records. User saves via CE File→Save. No
offline CT builder, no Ascii85, no XML serializer. Final cost: ~1.5
days (originally estimated 2; under because the AOBMaker plugin
already had every command we needed).

**Status**: all 6 sub-phases (22a–22f) landed across commits
`296f76f` / `99b25f8` / `421c3c8` / `6cdfdc3` / `bcd378d` / `dcb9695`.
**Live click-through smoke FULLY CLOSED 2026-05-18**: Steps 1-8
verified in the initial smoke session (with 6 P0/P1 bug fixes
mid-flight); Steps 9-11 verified in a follow-up session — user log
captured `Mathf.Abs(-7) → 7` end-to-end through the
DiscreteUI → AOBMaker → CT → bridge → `callUnityMethod` → `prettyPrint`
chain on Mono build #304. 11-step checklist
([`docs/ce-bridge-integration.md` § "Manual smoke checklist"](ce-bridge-integration.md))
is now reference material, not pending work.

### Why the pivot

Original Phase 22 design assumed we'd serialize a fresh `.CT` XML
ourselves (Ascii85-encode every embedded file + match CE's
whitespace + golden-file testing). That entire layer is dead weight
because the AOBMaker CE plugin already exposes 5 pipe commands that
mutate the currently-open CT in-process:

| Command | Maps to |
|---|---|
| `InjectTableFile { fileName, content }` | Insert one `<File>` entry — content is raw UTF-8, plugin internally calls `findTableFile`+`createTableFile`+`Stream.copyFrom`. Idempotent (existing same-name entry is deleted first). |
| `CreateAAScript { description, script, autoActivate }` | Append a `vtAutoAssembler` (type=11) memory record. Already used by Phase 18c. |
| `CreateMemoryRecord { description, address, valueType, isSigned }` | Append a primitive memory record (byte/word/dword/qword/single/double/string/byteArray). Phase 21 write-back will use this. |
| `CreateAAScriptWithRecords { description, script, autoActivate, hideChildren, records:[{ description, address, valueType, isSigned, offsets:[…], color }] }` | Parent AA script + N pointer-chain child records under it. Used for "register one symbol + expose its fields" pattern (Phase 17 Address Lookup hand-off). |
| `CreateSymbolScript { name, aob, pos, aoblen, symbol, module }` | Plugin assembles the AA template internally. We already have our own `AobAaScriptGenerator`, so this is informational only. |

UE5CEDumper has the canonical implementation of this pattern —
[`Services/HelperLuaResource.cs`](../../UE5CEDumper/ui/UE5DumpUI/Services/HelperLuaResource.cs)
embeds one lua module via `<EmbeddedResource>` in csproj,
[`Services/AobMakerBridgeService.cs`](../../UE5CEDumper/ui/UE5DumpUI/Services/AobMakerBridgeService.cs)
exposes `InjectTableFileAsync` + `CheckAvailabilityAsync` + `IsAvailable`,
and `MainWindowViewModel.InjectCeHelperLuaAsync()` is a single Tools-menu
RelayCommand. Phase 22 = scale that pattern from 1 module to 7 + add
a bootstrap AA-script generator.

### What AOBMaker plugin cannot do today (out of scope for v1)

1. **Build a brand-new `.CT` without CE open** — plugin operates on
   the currently-loaded table. User saves via CE File→Save.
2. **True nested memory-record trees** — `CreateAAScriptWithRecords`
   supports a single parent + offset-children. Multi-level grouping
   like CrimsonDesert.CT's "Toggle Scripts > Battle > …" would need
   a `parentId` parameter on AOBMaker side. Defer; ship flat with
   description prefixes (`[Discrete] callMethod: Mathf.Abs`) for
   visual grouping.
3. **Duplicate detection** — re-clicking a "Send to CE Table" entry
   creates a second copy. v1 documents this in the menu tooltip
   ("Re-click creates duplicates; remove old entry in CE first").
4. **List / query existing records** — no `ListRecords` /
   `FindByDescription` command.

These can come back as "Phase 22.1 — AOBMaker plugin extensions" if
demand surfaces, but each is a small AOBMaker-side patch, not a
Discrete-side architecture change.

### Reference

- Plugin protocol: `D:\Github\AOBMaker\plugins\CEPlugin\src\protocol.h`
  + handler implementations in `pipe_server.cpp` (lines 582 onwards
  cover `HandleCreateMemoryRecord` / `HandleCreateAAScript` /
  `HandleCreateAAScriptWithRecords` / `HandleInjectTableFile`).
- Discrete's existing AOBMaker client (Phase 18c): [`AobMakerBridge.cs`](../ui/UnityDumpUI.Core/Services/AobMakerBridge.cs)
  — 3 commands wired (`NavigateHexView` / `NavigateDisassembler` /
  `CreateAAScript`). Phase 22a extends to all 5 we need.
- UE5CEDumper reference implementation:
  - [`ui/UE5DumpUI/Services/HelperLuaResource.cs`](../../UE5CEDumper/ui/UE5DumpUI/Services/HelperLuaResource.cs)
    (embedded-resource pattern).
  - [`ui/UE5DumpUI/Services/AobMakerBridgeService.cs:275-331`](../../UE5CEDumper/ui/UE5DumpUI/Services/AobMakerBridgeService.cs)
    (`InjectTableFileAsync` + `CheckAvailabilityAsync` + `IsAvailable` observable).
  - [`ui/UE5DumpUI/ViewModels/MainWindowViewModel.cs:828-876`](../../UE5CEDumper/ui/UE5DumpUI/ViewModels/MainWindowViewModel.cs)
    (`InjectCeHelperLuaAsync` Tools-menu RelayCommand).
- Original deferred design (reason it was scrapped): see this section's
  git history at commit `2143a77`.

### Sub-phase breakdown

| # | Task | Effort | Key content |
|---|---|---|---|
| **22a** | Extend `AobMakerBridge` | 3h | Add `InjectTableFileAsync(fileName, content)`, `CreateMemoryRecordAsync(description, address, valueType, isSigned)`, `CreateAAScriptWithRecordsAsync(description, script, records, autoActivate, hideChildren)`, `CheckAvailabilityAsync()` + `IsAvailable` observable. Wire model gains `FileName` / `Content` / `ValueType` / `IsSigned` / `Records[]` fields — all `[JsonIgnore(WhenWritingNull)]` so the same wire shape covers all 5 commands (AOT-safe via existing source-gen context). Direct port of UE5CEDumper `AobMakerBridgeService.cs`. |
| **22b** | Embed 7 bridge lua files as `<EmbeddedResource>` | 1h | Add 7 `<EmbeddedResource Include="..\..\scripts\unity_*.lua">` entries to `UnityDumpUI.csproj` with stable `<LogicalName>` keys. New `UnityDumpUI/Services/BridgeLuaResources.cs` exposes `ReadAll() → IReadOnlyList<(string fileName, string content)>` plus `Read(name)`. Mirrors UE5CEDumper `HelperLuaResource.cs`. |
| **22c** | "Load Discrete Bridge" AA-script template | 3h | C# `const string` template + minimal substitution (no dynamic AA generation). `[ENABLE]` block iterates the 7 files via `findTableFile` + `Stream.copyFrom` + `load()`, guarded by `_G.discrete_bridgeLoaded`; runs `unity_handshake()` to assert `protocol_version` (warns but does not abort on mismatch); pings the DLL pipe (`\\.\pipe\UnityDumper-{Backend}`) and prints "Inject DLL via DiscreteUI first" if absent. `[DISABLE]` clears the globals. |
| **22d** | Tools menu "Inject Discrete Bridge into CE Table" | 2h | New `MainWindowViewModel.InjectDiscreteBridgeAsync()` RelayCommand: `CheckAvailability` → 7× `InjectTableFile` with progress status ("Injecting 1/8…") → 1× `CreateAAScript` for bootstrap. Stops on first failure with the AOBMaker error message. Final status: "Bridge ready in CE — enable Load Discrete Bridge to activate". Menu item bound to `IsAvailable`; tooltip when disabled: "Open CE with AOBMaker plugin loaded". |
| **22e** | Per-feature "Send to CE Table" entries | 4h | • **Methods grid context menu** → "Send AA script to CE Table" — calls existing `AobAaScriptGenerator.Build()` on the selected overload(s), pushes result via `CreateAAScript`. • **MethodCallWindow** "Send to CE Table" button — wraps `callUnityMethod(asm, ns, cls, method, args, opts)` in a `{$lua}` AA block + `CreateAAScript`. • **AddressLookup** result row → uses `CreateAAScriptWithRecords` to push instance pointer + each field as offset-children. • **Inspector** field row (gated on Phase 21) — "Send write-back to CE Table" → `CreateAAScript` wrapping `setInstanceField(...)`. Every generated `[ENABLE]` prepends `if not _G.discrete_bridgeLoaded then print('Enable Load Discrete Bridge first'); return end`. |
| **22f** | E2E smoke + integration doc | 2h | New [`docs/ce-bridge-integration.md`](ce-bridge-integration.md) describing the user journey (open CE → load CT → DiscreteUI Inject Bridge → use feature buttons → CE File→Save). Smoke: clean CE 7.6 → empty CT → bridge injection → Phase 18 AOB push + Phase 19 callMethod push + Phase 17 AddressLookup push → File→Save → re-open in fresh CE process → verify all 3 features still enable cleanly. |

### Dependencies + parallelism

```
22a (bridge methods) ──┐
22b (embed resources) ─┼──→ 22d (Tools menu) ──┐
22c (bootstrap AA)   ──┘                       │
                                                ├──→ 22f (E2E)
22e (per-feature buttons) ─────────────────────┘
```

22a / 22b / 22c are independent and can be done in parallel once 22a's
wire-model design is locked. 22d is the convergence point. 22e can
start as soon as 22a lands (it only needs `CreateAAScriptAsync` + the
new `CreateAAScriptWithRecordsAsync`). 22f closes the phase.

### Acceptance

- Clean CE 7.6 + empty CT → DiscreteUI Tools → "Inject Discrete Bridge into CE Table" → status text shows "8/8 complete" → CE Table → Show table files lists 7 `unity_*.lua` files → memory record list shows "Load Discrete Bridge" entry.
- Enable "Load Discrete Bridge" → CE Lua console prints `[discrete] bridge loaded — IL2CPP / build 257`.
- In DiscreteUI Methods grid, right-click `Mathf.Abs` → "Send AA script to CE Table" → CE gains a new AA-script entry → enable it → AOB scan hits, `registersymbol` succeeds.
- Same workflow from MethodCallWindow for `Mathf.Abs(-7)` → CE gains a callMethod wrapper entry → enable → CE Lua console prints `7`.
- `File → Save` the CT → open the saved `.CT` in a fresh CE process → re-enable Bridge + any feature script → behaviour identical.
- When `IsAvailable=false` (CE closed / plugin not loaded), Tools menu item is disabled with an informative tooltip; errors mid-flight surface the AOBMaker `Message` field (not a generic "failed").
- AOT publish: 0/0 trim+AOT warnings; new tests (`AobMakerBridge` mock-driven inject flow + bootstrap AA template string assertions) ≥ 6/6 pass.

### Out of scope (deferred, not blocking ship)

- ❌ Offline `.CT` builder (Ascii85 + XML serializer + golden-file fixture). Re-evaluate as Phase 22.5 only if a user needs to generate a CT without CE open.
- ❌ Multi-level nested memory-record trees (needs AOBMaker `parentId` extension).
- ❌ Encrypted CT output.
- ❌ Bidirectional CT import / edit-and-export.

### 22g — DLL-free Mono symbol register (shipped 2026-05-19)

User-requested addition for cheat-table authors who don't want to
inject the UnityCEDumper DLL. Lets the Methods overload picker emit
AAs that register Mono symbols entirely via CE's built-in Mono Lua
API (`mono_findClass` / `mono_class_enumMethods` /
`mono_compile_method`), through the user's hand-rolled
`mono_registerSymbol*` helper pattern. Single CT preparation step
(Tools → Inject Discrete Bridge) ships the helpers; generated AAs
lazy-load them on enable.

| Piece | Where | Note |
|---|---|---|
| `scripts/mono_registersymbol_helpers.lua` | new file | 3 register helpers + `mono_ensureAttached()` + `SafeMonoDestroy`. All `if _G.xxx == nil` guarded. No top-level side effects — Mono attach happens lazily on first register call. |
| `BridgeLuaResources.LoadOrder` | +1 entry | 7 → 8 files. The bootstrap auto-loads the helpers harmlessly at bridge boot (only function defs land in _G). |
| `UnityDumpUI.Core.csproj` | +1 EmbeddedResource | Same LogicalName convention as the other 7 bridge files. |
| `MonoRegisterSymbolAaScriptGenerator` | new file | Emits `[ENABLE]` that lazy-loads helpers via `findTableFile` + chunk-load (idempotent), then `mono_registerSymbolEx(symName, ns, cls, method, { paramTypes })` per overload. Uses full-name paramTypes (e.g. `'System.Int32'`) to avoid the `feedback_signaturematch_substring` trap. `[DISABLE]` pcalls each `unregisterSymbol`. Mono-only — throws on populated `ModuleName` (IL2CPP signal). |
| `AaScriptOverload.ParamTypes` | new init-only field | `IReadOnlyList<string>?` — populated by `MethodOverloadsViewModel.BuildScriptBagAsync` from `MethodOverloadRow.ParamTypes`. Optional / null = no per-param info (generator falls back to empty Lua table). |
| `MethodOverloadsViewModel` | +2 commands + `IsMonoBackend` gate | `CopyMonoSymbolScriptCommand` (clipboard) + `CreateMonoSymbolInCeCommand` (AOBMaker push). Both gated on `IsMonoBackend` + at-least-one-selected; CreateInCe additionally requires `HasAobMakerBridge`. |
| `MethodOverloadsWindow.axaml` | +2 buttons | `IsVisible="{Binding IsMonoBackend}"` / `"{Binding ShowMonoCreateInCe}"`. Tooltips explain the prereq (run Tools → Inject Discrete Bridge first). |
| `MainWindow.axaml.cs` `BuildOverloadsVm` | +1 ctor arg | Threads `vm.Backend2 == "Mono"` into `MethodOverloadsViewModel.IsMonoBackend`. |
| Tests | +11 generator / +3 BridgeLuaResources / −1 BridgeInjector refactor | 180 → 193 pass. `BridgeInjector` test now reads `LoadOrder[last]` so future LoadOrder additions don't need test edits. |

**Live smoke**: pending. Manual recipe: clean CE 7.6 + AOBMaker
plugin loaded + empty CT + Mono target (e.g. BongoCat or
Farthest Frontier Mono build) → DiscreteUI Pick & Inject → Tools
→ Inject Discrete Bridge (8 files + 1 AA, ~120 ms) → Methods tab
→ right-click a method → Method overloads → tick an overload →
"Copy mono_register AA" → paste into a new CT entry → enable →
expect CE Lua console banner
`mono_registerSymbolEx: registered <name> = <addr>`. Or use
"Create mono_register in CE" to skip the clipboard round-trip.
- ❌ DLL bundling inside the CT (Bootstrap script just pings the DLL pipe; user keeps DiscreteUI + CE both running).

### Pre-work

✅ None needed — `handshake.protocol` (integer, currently 1) is already
emitted by `ExportAPI.cpp` (line 104) and documented in
`pipe-protocol.md` (line 146). 22c's Bootstrap AA script asserts on this
existing field (note: actual key is `protocol`, not `protocol_version`).

---

## Phase 25 — Cross-class Symbol Finder ✅ SHIPPED 2026-05-19

**Goal**: Replace the "click every class in turn through the Browser
pane" loop the user reported when hunting a property like `MaxHP` that
could live in any of the few-hundred classes in `Assembly-CSharp`. One
keystroke ought to find it.

**Design**:
* **New pipe RPC `searchSymbols`** — parallel Mono + IL2CPP handlers
  in `ExportAPI.cpp` walk the existing `EnumerateClasses(image)` × per-
  class `ReadClassInfo` path, apply ASCII case-insensitive substring
  match on field/method names, accumulate matches up to `limit`.
  Defaults: limit 200, hard-cap 5000, both kinds (`field` + `method`)
  enabled. Returns `{matches: [...], scanned_classes: N, truncated: bool}`.
  Wire shape is unified across backends — IL2CPP method rows carry the
  AOT `methodPointer`; Mono method rows omit address (calling
  `mono_compile_method` per method during a bulk scan is too heavy;
  defer JIT to the user's per-overload Methods picker click).
* **New file-local `ICaseContainsAscii` helper** at the top of
  `ExportAPI.cpp` — ASCII fold (`(c >= 'A' && c <= 'Z') ? c | 0x20 : c`),
  no `<locale>` drag-in. C# field/method names are always ASCII.
* **Properties handled as `get_*`/`set_*` methods** — the Mono/IL2CPP
  runtimes already lower properties to that pair of accessor methods,
  and the Browser Methods grid surfaces them, so the finder doesn't
  need a separate `property` row kind. User search `MaxHP` automatically
  matches the field `MaxHP`, the getter `get_MaxHP`, and the setter
  `set_MaxHP` in one pass.
* **Core layer**: new `BackendAdapter.SearchSymbolsAsync(asm, pattern,
  kinds?, limit?)` with a JsonDocument-based inline `kinds` array (no
  AOT-unfriendly serializer path for a string[]). New
  `SearchSymbolsResult` + `SearchSymbolMatch` DTOs + `ProtocolJsonContext`
  envelope.
* **UI tab**: new TabItem at position 4 (after Address Lookup) in
  MainWindow. Assembly dropdown shares `Browser.Assemblies` so the
  list auto-populates after handshake. Pattern textbox triggers
  Search on Enter (the bound `RelayCommand`'s `CanExecute` gates on
  selected assembly + non-empty pattern + ≥1 kind toggle on).
  Limit NumericUpDown (Width 140 + MinWidth 120 — the original 90px
  column starved the number display behind the up/down spinner).
  Sortable results grid (Kind / Class / Member / Type / Offset /
  Address). Row double-click or right-click "Navigate" jumps back
  to the Browser: selects assembly + class, pre-fills Fields/Methods
  filter with the member name, switches to the matching tab.
  Right-click also surfaces Copy class / Copy member.
* **Tests**: 3 new fake-pipe `SearchSymbolsTests` — wire-shape pin
  (cmd + args), omit-null-kinds/-limit guard, truncated flag round-
  trip. SymbolFinderRow's display projection (ClassDisplay /
  OffsetLabel / KindLabel) lives in the UI project (not Core) so
  xUnit can't reach it without pulling Avalonia into test deps;
  covered by manual click-through smoke instead. Lift to Core if the
  projection grows enough to need its own tests.

**Live smoke 2026-05-19**:
* **BongoCat** (Mono / Unity 2022+ / Assembly-CSharp): pattern
  `MaxHP` returned `MainGame/CALC_WORK.maxHp` (`System.Int32` @
  `0x2C`) as the first hit in < 1s. Double-click jumped to Browser
  Fields tab with the row filtered + selected.
* Limit-column width regression caught + fixed in the same session
  (Width 90 → 140 + MinWidth 120).

**Files**:
* `dll/src/shared/MagicStrings.h` — new `CmdSearchSymbols`
* `dll/src/shared/ExportAPI.cpp` — `ICaseContainsAscii` helper +
  both backends' handlers
* `ui/UnityDumpUI.Core/Services/ProtocolModels.cs` —
  `SearchSymbolsResult` + `SearchSymbolMatch`
* `ui/UnityDumpUI.Core/Services/ProtocolJson.cs` — JSON contexts
* `ui/UnityDumpUI.Core/Services/BackendAdapter.cs` —
  `SearchSymbolsAsync` + `BuildKindsJson` helper
* `ui/UnityDumpUI/ViewModels/SymbolFinderViewModel.cs` (new)
* `ui/UnityDumpUI/ViewModels/MainWindowViewModel.cs` —
  `SymbolFinder` property + `OnSymbolFinderNavigateRequested`
  handler + `TabIndexSymbolFinder = 4`
* `ui/UnityDumpUI/Views/MainWindow.axaml` — new TabItem
* `ui/UnityDumpUI/Views/MainWindow.axaml.cs` — 5 handlers
* `ui/UnityDumpUI/Resources/Strings/en.axaml` — `SymbolFinder.*` keys
* `ui/UnityDumpUI.Tests/SearchSymbolsTests.cs` (new)

### Phase 25b — Interesting preset (deferred)

UE5CEDumper has a sibling "Interesting Properties" panel that runs a
batched multi-keyword search against a curated seed list (HP /
Damage / Gold / XP / Speed / Health / Mana / Stamina / Crit / ...)
and scores each match by category (Stats / Combat / Resources /
Movement / Utility) with a "class-location" bonus for unusual
containers (e.g. fields living on a HUD class get a +4 bump). It
surfaces fields no user would type into the basic finder.

Phase 25b would add the same surface for Unity but using a Unity-
tuned keyword list (UE5CEDumper's list is FProperty-flavored —
`HitResult`, `Pawn`, `Ability` — which doesn't translate). Open
design questions before implementing:

* Keyword list curation — start from a Unity-specific dump (BongoCat
  + SaGa F2R + Songs of Silence pooled) and bias toward names that
  appear across ≥ 3 games.
* Single batched RPC vs. client-side iterates the existing
  `searchSymbols` once per keyword. Latter is simpler (no new DLL
  RPC) but slower; UE5 chose the batched path because their cap
  was 1.5s vs. 42s on 4400-class games. Unity assemblies are
  generally smaller per-assembly, so the simpler path may be
  acceptable.
* Score model — UE5 keyword-hit ladder (Stats=5, Combat=4, Resources=4,
  Movement=4, Utility=3) is a reasonable starting point; we'd add
  Unity-specific categories (UI / Audio / VFX / SaveData) and skip
  UE-specific ones (Networking / Replication).

Defer until either a user explicitly asks for it, or the basic Phase
25 finder gets enough use to surface the "I don't know what to
search for" gap.

---

## Phase 26 — Anti-tamper protection ✅ SHIPPED 2026-05-20

**Goal**: Make a hex-edited `DiscreteUI.exe` silently produce wrong
output, not crash, not show a "tamper detected" banner. Cost cap:
"simple protection" — defensive enough to deter casual modification,
not enterprise-grade obfuscation. Inspired by `D:\Github\AOBMaker`'s
pattern (StringShield + IntegrityGuardian{,B,C} + Logic Poisoning)
but redesigned for cross-assembly slot distribution so the three
sentinel + 32-byte-hash blocks don't sit adjacent in `.rdata`.

### Sub-phases

- [x] **26a** Single-slot baseline (`ccaae6e`):
  XOR-shielded credit string + one `BuildArtifactToken` (then named
  `IntegrityGuardian`) self-check + `inject-hash.ps1` + CT export
  `CloakOffset(off) ^= factor & 0x7F` in `CeXmlExporter` + `CsxExporter`.
  Footer credit added to MainWindow.axaml (centered, opacity 0.55).
  AOT round-trip verified on first attempt.
- [x] **26b** 3-slot cross-assembly upgrade (`348cbb6`):
  Renamed all 3 originals to non-suggestive forms
  (`IntegrityGuardian` → `BuildArtifactToken` / `IntegrityCore` →
  `SchemaConstants` / `StringShield` → `LocalizedText` /
  `GetIntegrityFactor` → `Read`); added `ExportFormatToken` in
  Core/Services/Export and `WindowsRuntimeMetadata` in
  ui/UnityDumpUI/Platform (cross-assembly). Each guardian self-checks
  the running binary independently — zeros all 3 slots before SHA256
  so any-of-three tampering trips all-of-three checks.
  `TokenAggregator.Combined() = A | B | C` bridges Core ↔ UI via
  delegate registered in `Program.Main`.
- [x] **26c** Inspector grid poison (`348cbb6`):
  `LiveInspectionViewModel.WalkedFieldNode.MaybePerturbDisplay`
  perturbs 1/11 numeric `DisplayValue` rows on tampered builds.
  Per-row deterministic seed (string `GetHashCode` XOR offset XOR
  salt) so same row corrupts same way within a session; across
  sessions GetHashCode randomization shifts WHICH rows look buggy.
  Skips non-numeric strings ("(struct — expand)", quoted strings,
  bool literals, errors, pointers).
- [x] **26d** 3-slot `inject-hash.ps1` (`348cbb6`):
  Locates all 3 dev slots (sentinel + 32×0xCC) → zeros all 3 hash
  regions → computes SHA256 once → writes same hash into all 3.
  `build.ps1 -Mode Publish` invokes it as a post-publish step;
  non-AOT builds skip injection (slot stays 0xCC → `IsDevMode` → no-op).

### Design tradeoffs (documented for future-me)

- **Single-file Release vs AOT**: deflate-compressed bundle in
  `dist/DiscreteUI.exe` (Debug/Release single-file) collapses the
  32×0xCC tail to a few bytes → `inject-hash.ps1` can't find slots
  there. Only `-Mode Publish` (AOT) produces a binary where the
  static byte literals stay searchable in `.rdata`. Build script
  gates injection accordingly.
- **DelayThreshold side-effect**: each guardian returns 0 for the
  first N calls (A:3 / B:2 / C:4) even when tampered. Defeats
  trivial breakpoint trace at the cost of the first ~2 exported
  offsets being unpoisoned. Acceptable — exports usually have 50+
  fields, attacker who comparatively reads CT vs ground truth
  would notice "everything's slightly off" not "first two are
  right then everything else is wrong".
- **Asymmetric poison coverage**: CT export goes through Core's
  `TokenAggregator.Combined()` which only sees slot C via the
  Program.Main-registered delegate. If an attacker patches A + B
  but not C, CT export still sees factor=C (non-zero) → still
  poisoned. **All three slots must be patched correctly to escape
  either surface.**
- **`InternalsVisibleTo` quirk**: Core's `InternalsVisibleTo` must
  reference the UI's **assembly name** (`DiscreteUI`), not the
  project name (`UnityDumpUI`). Hit this during build; documented
  in lessons-learned.md.

### Out of scope (v2 candidates if user wants more breadth)

- Methods grid Address column poison (only Inspector + CT export
  covered today)
- Symbol Finder address column poison (same reason)
- Multi-poison-target obfuscation (each guardian poisons its own
  unique consumer rather than the OR-combined "any tampering
  trips everything" model)
- Anti-VM / anti-sandbox detection (out of scope for "simple")

---

## Phase 27 — Unity-aware Value Search 🚧 PLANNING 2026-05-20

**Goal**: Close the read-by-value gap. Today every Discrete query starts from
a class name (Browser) or address (Address Lookup). CE users approach the
problem from the opposite end — "I have HP=100, where does the game store
it?". This phase brings that workflow into Discrete while keeping the
Unity-class-aware filtering that CE itself cannot do.

**Inspiration**: `D:\Github\AOBMaker\src\AOBMaker.Platform.Windows\Services\WindowsValueScanner.cs`
(parallel chunked VirtualQueryEx walker with memory budget + cancellation).
We do NOT reimplement that engine in C# — we reuse the existing DLL-side
`Memory::EnumerateCommittedRegions` + `Memory::ScanForPointer` plumbing
([dll/src/shared/Memory.h](../dll/src/shared/Memory.h)) which already has
Phase 23's MEM_IMAGE / stack-residue filtering hardened.

### Why two scan modes

| Mode | When to use | Speed |
|---|---|---|
| **Global** | CE flow — "I see HP=100, find it" | Slower First Scan (walks all committed RW), but Next Scan only re-reads the filtered Unity-managed candidates (typically < 1000 addrs after `whatIsAt`) |
| **Class-scoped** | Discrete flow — "I know it's somewhere on `PLY_WORK`" | Order-of-magnitude faster — only walks live instances of the named class via existing `findInstances` path |

Same wire-shape result for both — UI surfaces them as a mode toggle on
the search tab. Class-scoped is essentially `listFieldValuesAcrossInstances`
(Phase 21a) with an added value-filter predicate, so the implementation
cost there is tiny.

### Reusable existing infrastructure

| Existing | Reused for | Lives in |
|---|---|---|
| `Memory::EnumerateCommittedRegions()` | Region enumeration with VirtualQueryEx + protect filter | [dll/src/shared/Memory.h:55](../dll/src/shared/Memory.h) |
| `Memory::SafeReadBytes` + SEH-leaf primitives | Reading anti-cheat-stripped pages without crashing the scan | same |
| `Memory::ScanForPointer` (Phase 23 hardened) | Template / pattern for the new `ScanForValue<T>` parallel scanner | same |
| `whatIsAt(addr)` (Phase 17) — class-range cache + backward scan | Per-candidate Unity-managed filter | [MonoAddressFinder.cpp](../dll/src/mono/MonoAddressFinder.cpp) / [IL2CPPAddressFinder.cpp](../dll/src/il2cpp/IL2CPPAddressFinder.cpp) |
| `listFieldValuesAcrossInstances` (Phase 21a) | Class-scoped mode core — already iterates instances + reads field values | ExportAPI handlers |
| `findInstances` (Phase 6c) | Class-scoped mode instance enumeration | same |
| AOBMaker `WindowsValueScanner.cs` `ValueScanRequest` / `ValueScanState` / `ValueScanType` shapes | UI-side reference for DataType / ScanType enums + Parallel.ForEach result-merge pattern | `D:\Github\AOBMaker\src\AOBMaker.Core\Models\` |

**Crucially**: anti-cheat is already a non-issue because the scan runs from
inside the target process via the existing injected DLL. User-mode AC that
hooks `OpenProcess` / `ReadProcessMemory` is invisible to our path. AC
that hooks `VirtualQueryEx` from inside the process is the only remaining
concern and is rare; same `SafeReadBytes` SEH-protection keeps the scan
from crashing if a hooked region throws.

### Sub-phases

- [ ] **27a** DLL `scanFieldValue` RPC (First Scan).
  * New `Memory::ScanForValue<T>` helper paralleling `Memory::ScanForPointer`:
    chunked parallel walk over `EnumerateCommittedRegions()` output, skip
    MEM_IMAGE + thread-stack regions (already filtered today), exact /
    range comparison, alignment defaulting to `sizeof(T)` with a "fast
    scan" toggle for misaligned hits.
  * **Type taxonomy** (8 numeric + 1 string + 1 byte-array) — matches
    AOBMaker's `ScanDataType.cs`: Int8 / Int16 / Int32 / Int64 / UInt8 /
    UInt16 / UInt32 / UInt64 / Float / Double + **string** (managed
    UTF-16, walks `MonoString*` / `Il2CppString*` layouts — length-prefix
    aware, see Phase 27b-2 below).
  * **Per-candidate `whatIsAt` resolution** — every value-match address
    is fed to the existing class-range cache + backward-scan logic. Only
    candidates that resolve to a managed instance + field are kept.
    Non-managed hits (game C++ heap, engine static buffers, etc.) are
    silently dropped — counted in `droppedNonManaged` so the user can
    see how aggressively the filter is firing.
  * **Result envelope**: `{ candidates: [{addr, instAddr, asm, ns, class,
    field, fieldOffset, fieldKind, currentValue}], scannedBytes,
    droppedNonManaged, truncated, sessionId }`. `sessionId` is what Next
    Scan (27b) uses to refine.
  * **Result limit** — mirror AOBMaker's adaptive budget (`int.Clamp(
    available * 0.25 / per-result-bytes, 100k, 50M)`), but enforce a
    hard cap of 50k POST-`whatIsAt`-filter on the wire (a 50k JSON
    payload is already ~6 MB).
  * Smoke test: `tools/Test-ScanFieldValue.ps1` — known-value scan against
    SaGa F2R (`HP = 100` int32) + BongoCat (`Mathf.PI` float).
  * Class-scoped mode: when `request.targetClass` is set, skip the full
    region walk — call existing `findInstances` to get instance addrs,
    then for each instance + each declared field of matching wire-type,
    SafeRead the field value, compare against the search value, emit the
    candidate with full `whatIsAt`-shape envelope baked in (no actual
    whatIsAt call needed since we already know instance + field).

- [ ] **27b** DLL session state + Next Scan filter modes.
  * **Session lifecycle**: `beginValueScan(request) → sessionId`,
    `refineValueScan(sessionId, scanType, [value], [valueTo]) →
    updated candidates`, `endValueScan(sessionId)`. Server holds
    `unordered_map<uint64_t, ScanSession>` with one session per UI tab
    (single session per pipe connection is enough today; multi-session
    is just changing the cap from 1 to N).
  * **Full CE filter mode set**: `exact` / `range [from, to]` /
    `changed` / `unchanged` / `increased` / `decreased` / `new value`.
    Comparison semantics match CE exactly (Compare-to-previous-scan for
    changed/increased/decreased; Compare-to-given-value for exact/range/
    new value).
  * **Session memory cost**: per-session = `candidates.size() *
    sizeof(Candidate)` where `Candidate = {addr, lastValue}` for numeric
    + `{addr, lastValueStr}` for string. 50k × 32 bytes = 1.6 MB
    worst-case — acceptable.
  * **27b-2 — String scan path**:
    * Mono: `MonoString` layout is `[ObjectHeader (8/16 bytes) | length:int32 | chars[length+1]:UTF-16]`. Walking strings means iterating GC heap for objects whose first qword == `mono_get_string_class()` vtable, then reading the length+UTF-16 buffer.
    * IL2CPP: same shape but `Il2CppString*` class pointer comes from `il2cpp_defaults.string_class`. Some IL2CPP builds inline UTF-16 strings into static data sections — those are reachable via the same `whatIsAt` filter (string klass match in backward scan).
    * UI-side: search box accepts plain UTF-8 / UTF-16-auto-detected, encoding option dropdown (UTF-16 / UTF-8 / ASCII) like CE.
    * Substring matching: optional toggle (exact equal vs. contains).
      Substring search is O(N × M) on candidates — capped at 5000 string
      candidates pre-substring-filter to keep it sane.

- [ ] **27c** Array / List<T> element resolution in `whatIsAt`
  (Phase 17b carry-forward). Detect Array klass two ways: (a) class name
  ends in `[]` from `*_class_get_name`, (b) `*_class_get_element_class`
  returns non-null. Compute element index from
  `(queriedAddr - elementsBase) / elementSize` where:
  * `elementsBase` = instance base + array-header size (Mono: 32 bytes
    on x64, 16 on x86; IL2CPP: 32 bytes — see [ManagedLayout.h](../dll/src/shared/ManagedLayout.h)
    `kArrayDataOffset`).
  * `elementSize` from `*_class_value_size(element_class)` for value-typed
    arrays; pointer-size for reference arrays.
  * Returned field shape: `{kind: "array-element", arrayIndex, arrayLength, elementClass, elementOffset}`.
  * `List<T>` falls into the same path naturally — its backing `_items`
    field IS a `T[]`, so user drilling into a `List<T>._items` row already
    gets `whatIsAt`-resolved as an array element.

- [ ] **27d** UI Value Search Tab (MainWindow position 5, after Symbol Finder).
  * **Top bar**: Mode toggle (Global / Class-scoped) + Assembly +
    optional Class dropdown (enabled when class-scoped) + DataType
    dropdown + Value textbox + Value-To textbox (for range) + ScanType
    dropdown (exact / range / changed / unchanged / increased /
    decreased / new value) + Alignment toggle (Default / Misaligned) +
    First Scan / Next Scan buttons.
  * **Encoding sub-bar** (visible when DataType == String): UTF-16 /
    UTF-8 / ASCII radio + Match-mode dropdown (Exact / Contains).
  * **Result grid** (sortable, same `DataGridSortComparers` pattern as
    the rest of the app — Phase 21a's lesson):
    * Class | Field | Type | Offset | Current Value | Previous Value
      (after Next Scan) | Instance Address | Field Address
    * Right-click context menu: Inspect this instance (Phase 17c
      handoff — cross-tab to Inspector), Write value… (Phase 21d handoff —
      opens FieldWriteWindow), Find what writes (existing AOBMaker
      bridge `NavigateHexView`), Copy field addr / instance addr / class.
  * **Status bar**: `XX,XXX candidates of YY,YYY scanned (ZZ,ZZZ dropped
    non-managed) in NNNN ms`.
  * **Cancellation**: Long First Scan blocks the worker thread for
    seconds — must surface a Cancel button + propagate a token through
    the wire (new `cancelValueScan(sessionId)` RPC, server checks
    cancellation flag between regions inside the parallel scan).

- [x] ~~**27e** CE Lua bridge `scripts/unity_valuescan.lua`.~~ **Dropped 2026-05-20**.
  Originally planned as a sibling to `unity_addrfind.lua` / `unity_invoke.lua`,
  but the symmetry is misleading: those bridges expose Discrete-only
  capabilities that CE doesn't have (reverse address lookup, managed
  method invocation). Value scan is the OPPOSITE — CE already has a
  fast, well-known native value scanner. A CT author who wants Unity-
  aware value scan opens DiscreteUI's Value Search tab; a CT author
  who wants raw value scan uses CE's built-in. Calling Discrete's
  scan FROM CE Lua doesn't fit any realistic workflow. The implemented
  script was deleted; SessionManager + Begin/Refine/End RPCs remain
  for UI consumption.

- [ ] **27f** *(deferred, v2 acceleration)* libgc heap-section binding.
  Bind `GC_get_heap_section_count` + `GC_get_heap_section_at` (BDWGC
  exports) per backend, restrict global-mode scan to only those regions
  for ~10x speedup. **Doubles as Phase 20-HEAP** — same code path that
  walks the managed heap for value scan also satisfies the 20-HEAP
  "walk libgc heap sections for IL2CPP instances" goal that's been open
  since the Phase 20 series. Caveat: not all Unity builds link
  bdwgc — some IL2CPP / non-bdwgc Mono builds don't export these. Fall
  back to global EnumerateCommittedRegions transparently.

### Acceptance criteria

* **Global mode, IL2CPP**: SaGa F2R First Scan for `Int32 = 100` returns
  the live `PLY_WORK.HP` candidate among results; non-managed memory hits
  (e.g. C++ engine ints that happen to == 100) are dropped via
  `whatIsAt`. Completes < 5s on a typical dev box.
* **Next Scan**: After in-game damage, "decreased" scan narrows to <= 5
  candidates (player HP + maybe enemy HP if visible).
* **Class-scoped mode, Mono**: BongoCat First Scan with class=
  `MainGame/CALC_WORK` + `Int32 = 100` skips full-process walk, returns
  results in < 100ms (just `findInstances` + per-field SafeRead).
* **Array resolution**: Scanning for an item count value finds matches
  inside an inventory array and reports `{kind: "array-element",
  arrayIndex: N}` instead of `{kind: "interior"}`.
* **String scan**: Searching for `"GameOver"` (UTF-16) finds managed
  strings; substring mode finds prefix-match candidates.
* **AC-stripped pages**: scan never crashes the game even when a region
  is hook-blocked; failure count surfaced in status.
* **Cancellation**: Long First Scan can be cancelled mid-flight from UI;
  worker exits cleanly within ~200ms.
* **No regression** in existing tests (283/283 pass) + AOT publish clean
  (0 IL2026/IL3050 warnings).

### Risks / open questions

* **Phase 17 backward-scan distance** — current default 4 KB. Some Unity
  MonoBehaviour subclasses with hundreds of fields exceed that. The
  `maxBackwardScan` arg on `whatIsAt` is configurable (cap 16 KB); the
  scan RPC will use the 16 KB cap for filter calls to avoid missed
  instances. Trade-off: each backward scan costs ~2k load+compare cycles
  vs cache; bumping to 16 KB is fine.
* **whatIsAt throughput** — 50k filter calls × 16 KB backward = 800 MB of
  probes. Each probe is one binary-search class-cache lookup + one SEH
  name-probe. Profiled scale: ~20 ns / probe → 16 ms total. Comfortably
  inside the scan budget.
* **Float / Double precision** — CE matches floats by epsilon (0.0001
  default). We mirror that with a tunable `tolerance` field; matches
  AOBMaker's `WindowsValueScanner.cs` `Math.Abs(value - target) <= tolerance`
  pattern.
* **Concurrent scan + walkInstance** — if user is also walking instances
  on another tab while a scan runs, both touch `ThreadAttachGuard`. The
  Phase 20-MT lesson about nested-attach already taught us to make the
  guard reentrant via thread-local counter — re-verify under the scan
  worker thread context before shipping.
* **Session lifecycle on disconnect** — server must drop sessions when
  the pipe closes (no orphan candidate buffers). Reuse Phase 14a's
  auto-disconnect cleanup hook.

### Estimated effort

| Sub-phase | Effort |
|---|---|
| 27a (DLL First Scan + class-scoped) | ~2 days |
| 27b (Next Scan + filter modes + string path) | ~1.5 days |
| 27c (array resolution in whatIsAt) | ~0.5 day |
| 27d (UI tab + Cancel + cross-tab handoffs) | ~2 days |
| ~~27e (CE Lua bridge)~~ — dropped (CE has native scan) | ~~~0.5 day~~ |
| **v1 total** | **~6.5 days** |
| 27f (libgc, deferred — also closes 20-HEAP) | ~1-2 days |

---

## Phase 28 — Field → Methods cross-reference 📝 TODO STUB

**Goal**: Inverse of Symbol Finder (Phase 25). Given a Unity field, list
every method that reads or writes it. Closes the "I know what's interesting
but I want to find the code that touches it" workflow that has no
equivalent in CE for managed code.

**Status**: not started. Picks up after Phase 27 ships. This stub captures
the approach decided in the 2026-05-20 late planning conversation; no
code, no commitment to scope yet.

### Approach by backend

| Backend / field-kind | Approach | Confidence |
|---|---|---|
| Mono instance + static fields | Walk method IL bytecode via `mono_method_get_header` + `mono_method_header_get_code`. Iterate IL ops looking for `ldfld` (0x7B) / `stfld` (0x7D) / `ldsfld` (0x7E) / `stsfld` (0x80). Each carries a 4-byte field-token immediate — compare against the target field's token. | **High** — IL is exact; no false positives. |
| IL2CPP static fields | Reuse [AOBMaker's `RipReferenceScanner.cs`](D:\github\AOBMaker\src\AOBMaker.Core\Services\RipReferenceScanner.cs) against the field's runtime static-storage address. Scan executable pages for `mov reg, [rip + disp32]` or `lea reg, [rip + disp32]` where `disp32` resolves to the field address. Attribute each hit to its enclosing method via Phase 18a's method-address enumeration (binary-search the address against `findMethodInstances`'s sorted method-pointer list). | **High** — RIP-relative is unambiguous. |
| IL2CPP instance fields | Heuristic. IL is discarded post-AOT; the field offset shows up as a literal in `mov rax, [rcx + 0xN]` style instructions. AOB-scan limited to: (a) methods in the declaring class + descendants, (b) methods that take `this` of compatible type. Filter for opcodes that actually access an instance pointer at the right offset. Surface results marked "low confidence". | **Low** — collisions with other struct offsets at the same byte distance are frequent. v1 may just decline to support this and surface a "declaring class's methods may reference this — enumerate them via Symbol Finder" hint. |

### Reusable infrastructure

* **AOBMaker `RipReferenceScanner.cs`** — already does the static-field scan
  pattern for general native code; just needs Unity-managed-method
  range attribution wired up.
* **Phase 18a `findMethodInstances`** — gives us the `{methodAddr, methodName}`
  table per class. Building a sorted lookup over all methods of all
  classes in an assembly is one new RPC + one cache.
* **Phase 25 `searchSymbols`** — surfaces the result table format we'd
  reuse (Class / Member / Type / Offset / Address).

### Tentative wire shape

```jsonc
// Request
{
  "command": "findFieldReferences",
  "assembly": "Assembly-CSharp",
  "ns": "MainGame",
  "class": "PLY_WORK",
  "fieldName": "HP",
  "kind": "any",  // "read" / "write" / "any"
  "limit": 200
}
// Response
{
  "ok": true,
  "field": { "name": "HP", "offset": 0x1F4, "type": "System.Int32", "isStatic": false },
  "references": [
    {
      "asm": "Assembly-CSharp", "ns": "MainGame", "class": "PLY_WORK",
      "method": "TakeDamage", "signature": "(System.Int32)",
      "address": "0x7FF712345678", "ilOffset": 0x0023,  // Mono only
      "kind": "write", "confidence": "exact" /* or "heuristic" */
    }
  ],
  "scanned_methods": 4523,
  "truncated": false
}
```

### Tentative UI integration

* Inspector grid right-click → "Find methods that touch this field…" → opens
  new modal `FieldReferencesWindow` (or a new Tab if usage justifies it).
* Symbol Finder result row right-click → same entry point for fields.
* Each reference row's "Navigate to disassembly" pushes the method addr
  to AOBMaker's `NavigateDisassembler` (existing Phase 18c bridge).

### Acceptance (sketch — refine when phase opens)

* Mono — Given `PLY_WORK.HP` (offset 0x1F4), `findFieldReferences` returns
  every method in `Assembly-CSharp` that contains an `stfld`/`ldfld` on
  that field token. Cross-check against IL view in dnSpy.
* IL2CPP static — Given `Math.PI` (or similar), the RIP scan returns the
  methods that load that double constant.
* IL2CPP instance — best-effort; user-visible "low confidence" badge in
  UI; v1 may decline this branch.

### Estimated effort (rough)

| Sub-phase | Effort |
|---|---|
| 28a — Mono IL walker + token match | ~1 day |
| 28b — IL2CPP static via RipReferenceScanner adapter | ~1 day |
| 28c — IL2CPP instance heuristic (or v1 skip) | ~1-2 days OR 0 if skipped |
| 28d — UI modal + Symbol Finder / Inspector entry points | ~1 day |
| 28e — CE Lua bridge | ~0.5 day |
| **v1 total** | **~3.5-5.5 days depending on 28c scope** |

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
