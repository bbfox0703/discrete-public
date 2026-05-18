# UnityCEDumper — Development Plan

> 跨 PC 共用的開發進度文件。每完成一個小步驟就把 `[ ]` 改成 `[x]` 並 commit。
> 所有里程碑驗收條件 (Acceptance) 必須通過才能往下一階段。

**Last updated**: 2026-05-08
**Current phase**: **Every shipped phase is closed and verified live.** Phases 0–15 + 9.5 + 11.5 + 14c + 16 + 17 + 18 + 19 (all five sub-phases + 19d-2 polish) all ✅ across both backends. Phase 16 docs landed (lessons-learned + READMEs en/zh-TW + CONTRIBUTING + 7 internal/spec docs). Phase 5 segment 2d cross-version sweep complete (5 metadata versions × 5 Unity major versions covered): `Il2CppMetadataRegistration` structural-shape AOB scan is fully version-portable through Unity 2018.1 → 6.1; v39 runtime sweep shows 9/9 paths green so v32+ targets are first-class supported for runtime-API workflows. **Phase 12d** (32-bit Mono) stays the only formally-deferred phase. **No critical-path open work.** **Backlog sweep 2026-05-08 (✅ all 4 fixes live-verified)**: v24 integer-decode (FF VI), Unity 2018.x missing IL2CPP exports (R-Type Dimensions EX), Mono `class_ptr` (Silksong), R4/R8 `value_kind` (SaGa F2R). **Polish sweep 2026-05-08**: Inspector classifier extracted to `UnityDumpUI.Core.Services.InstanceFieldClassifier` + 38 unit tests; `HighlightedTextBlock` Browser-filter substring highlight + 14 unit tests. Test count 5 → 57. Build #257 (Publish/AOT clean, `DiscreteUI.exe` 38.3 MB). Verified targets across the matrix: SaGa F2R (IL2CPP v29), BongoCat (Mono Unity 6.0), Farthest Frontier (both backends, IL2CPP v31), Songs of Silence (IL2CPP v39 / Unity 6.1), R-Type Dimensions EX (IL2CPP v24 / Unity 2018.1), FF VI PR (IL2CPP v24 / Unity 2019.4), Hollow Knight Silksong (Mono / Unity 6.0), plus the original Phase 14c metadata-only fixtures (SaGa Frontier Remastered, Townscaper, Emberward). See `STATUS.md` for the detailed hand-off summary; this file is the canonical phase ledger.

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
| 12d | 32-bit Mono support | x86 build of backend + proxy DLLs, cross-bitness inject, x86/x64 selection in installer | ⏳ Deferred — engineering effort only (user confirmed 2026-05-08 they HAVE a 32-bit Mono test target available; not blocked on demand). Re-estimated: ~3-5 days focused work (parallel x86 build config in CMake, x86 backend DLL with Mono 32-bit struct offsets verified, x86 builds of `version.dll` / `winmm.dll` / `dxgi.dll` proxies, cross-bitness inject from x64 UI to x86 game (or document "proxy install only" for x86), x86/x64 selection flyout in installer, test matrix expansion with the 32-bit target) |
| 13 | Offline Metadata CLI | Standalone tool to parse global-metadata.dat | ✅ Ships as `dist/metadata-cli.exe` |
| 14 | Hardening | Error paths, AOT trim warnings, log rotation, edge cases | ✅ All sub-items closed (14a-14f) |
| 14c | IL2CPP metadata version dispatch | v24 / v27 / v29 / v31 TypeDefinition layouts | ✅ Cross-checked vs upstream Unity (`docs/il2cpp-metadata-crosscheck.md`) |
| 15 | Test Matrix | Mono / IL2CPP across multiple Unity versions | ✅ Verified across 6+ games covering v24/v27/v29/v31 + Mono bdwgc Unity 6 |
| 16 | Docs Finalization | dll-spec / pipe-protocol / ui-spec / internals docs + READMEs + CONTRIBUTING | ✅ Closed 2026-05-07 — all planned files now exist (lessons-learned, README.md + zh-TW, CONTRIBUTING.md, proxy-dlls, metadata-cli, export-formats, mono-internals, il2cpp-internals, dll-spec, ui-spec, technical-notes) |
| 17 | Address Finder (reverse lookup) | New wire command `whatIsAt(address)` returning instance + class + field; inverse of `findInstances` | ✅ All four sub-phases shipped (17a DLL handler / 17b field resolution / 17c UI Address Lookup tab / 17d CE Lua bridge). Spec in [address-finder.md](address-finder.md). |
| 18 | Find Method Instances | New wire commands `findMethodInstances` + `getMethodAobPattern` + UI overloads modal + AOB AA-script generator + AOBMaker `CreateAAScript` direct push | ✅ All four sub-phases shipped (18a DLL handler + signature decoding / 18a-3 hand-rolled x64 LDE for AOB wildcarding / 18c UI overloads picker + AobAaScriptGenerator + AOBMaker direct push / 18d `unity_symbols.lua` extension `registerMethodOverloads`). |
| 19 | Lua-callable Unity methods | New wire command `callMethod(asm, ns, class, method, [args], [instance])` invoking Mono / IL2CPP methods with marshaled primitive + string args; closes the long-standing CLAUDE.md goal | ✅ All five sub-phases shipped (19a IL2CPP DLL / 19b Mono port / 19c instance dispatch + string in/out / 19d UI MethodCallWindow / 19e `scripts/unity_invoke.lua` Lua bridge). Verified live across SaGa F2R + BongoCat + Farthest Frontier. Spec: [lua-invoke.md](lua-invoke.md). |
| 20 | Scene / GameObject explorer + Object.name search | `scripts/unity_scene.lua` Lua bridge + DiscreteUI Scene tab + filter-by-name + click-to-inspect handoff. Tree-walk primitives gated behind a NEW sub-phase **20-MT** (DLL-side main-thread marshaling) since iterating Unity icalls from the worker thread freezes the game. | 🚧 In progress — 20a landed + hardened 2026-05-08 (envelope unwrap fix + main-thread caveat after live test on SaGa F2R locked up the game). `getName` / `getAllGameObjects` / `findByName` are safe-and-working primitives; `getActiveSceneRoots` / `getChildren` / `getComponents` formally **EXPERIMENTAL until 20-MT ships**. 20b (UI tab) blocked behind 20-MT. Zero DLL changes so far; 20-MT will need DLL frame-hook work. |
| 21 | Inspector field write-back | New wire RPC `setInstanceField(addr, fieldOffset, type, value)` + SEH-protected `SafeWriteBytes` on both backends + UI Inspector cells go editable with Apply button | ⏳ Planned — after Phase 20. ~½ day + tests. Closes the read/write asymmetry: today the Inspector only reads. GC-root caveat applies (same pattern as Phase 19c). |
| 22 | Live CT Population via AOBMaker | One-click "Inject Discrete Bridge into CE Table" (Tools menu) + per-feature "Send to CE Table" buttons. Pivoted away from offline `.CT` build (Ascii85 + XML serializer scrapped) — uses AOBMaker CE plugin's pipe protocol (`InjectTableFile` + `CreateAAScript` + `CreateMemoryRecord` + `CreateAAScriptWithRecords`) to mutate the currently-open CT. User saves via CE File→Save. | ✅ **All sub-phases (22a–22f) shipped 2026-05-12 + live smoke fully closed 2026-05-18.** 22a `AobMakerBridge` extended (4 new commands, `IsAvailable` event); 22b 7 bridge lua files embedded in Core; 22c `BootstrapAaScriptGenerator` template; 22d Tools menu RelayCommand + `BridgeInjector` orchestrator; 22e `CallMethodAaScriptGenerator` + `AddressLookupAaScriptGenerator` per-feature buttons; 22f `docs/ce-bridge-integration.md` + `tools/Test-BridgeInject.ps1` smoke. **Live click-through smoke**: Steps 1-8 captured + fixed 6 P0/P1 bugs mid-session; Steps 9-11 (MethodCall→CE / AddressLookup→CE / save+reopen) PASS in follow-up session — user log `Mathf.Abs(-7) → 7` confirms the AA-script → bridge → `callUnityMethod` chain. 163/163 xUnit pass, AOT 0/0 warnings. |

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
- [x] Cross-link metadata index ↔ runtime `Il2CppClass*` via `Il2CppMetadataRegistration` AOB — landed commit `5c59077` (2026-04-29). See "2d follow-up" below for the full description.
- [x] Enum default values (segment 2d v1: type-aware decode using runtime `value__` underlying type), **string literal extraction ✅**
- [x] Detection of stripped / obfuscated metadata (segment 3): `getMetadataStats` pipe command. Counts single-char / two-char user-type names (excluding `<...>` compiler-generated) and emits a verdict — `clean` / `partial_strip` / `heavy_strip`. IL2CPP path reads from `global-metadata.dat` directly; Mono path walks runtime classes.

**2d follow-up** (✅ shipped 2026-04-29, commit `5c59077`): full
`Il2CppMetadataRegistration` discovery in `GameAssembly.dll` so non-enum
const literals (e.g. `const int kFoo = 42`) in
`il2cppGetMetadataTypeFields` can also be type-aware-decoded.

**Approach used** — *structure-shape* scan, not a literal-byte AOB. See
[IL2CPPMetadataRegistration.cpp](../dll/src/il2cpp/IL2CPPMetadataRegistration.cpp):

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

**Smoke test**: [Test-MetadataFields.ps1](../tools/Test-MetadataFields.ps1)
prints `Il2CppMetadataRegistration: YES/no` banner + per-field
`tag=0x.. value=.. enc=NB` for every field with a default.

**Cross-version validation status**:
- v29 (SaGa F2R / IL2CPP) — ✅ verified live; banner shows YES, type
  tags decoded correctly.
- v24 / v27 / v31 — ⏳ not formally validated. Structural-shape
  heuristic theoretically version-independent, but unverified.
  Quick win — recipe in
  [STATUS.md §"Mono-side smoke procedure" pattern](STATUS.md).

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
  * Deferred to a hypothetical 19d-3 (no user ask yet): smart-typed
    editors (NumericUpDown for sized ints, ComboBox for enums via
    `getEnumValues`).

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
- [ ] **20-MT** *(new sub-phase)* DLL-side main-thread marshaling —
  frame-hooked queue drain so `callMethod` on Unity icalls no longer
  fights the game thread. Probably patches `PlayerLoop` or a similar
  per-frame Unity hook to invoke pending requests on the main thread.
  **Required to make `getActiveSceneRoots` / `getChildren` /
  `getComponents` (and similar future Unity-engine API recipes) safe at
  scale.** Multi-day; design TBD; sequenced before Phase 20b.
- [ ] **20-HEAP** *(new sub-phase, fallback if Unity APIs aren't
  reachable)* DLL-side managed-heap walker that doesn't depend on Unity
  managed APIs at all — walks libgc heap sections via
  `GC_get_heap_section_count` / `GC_get_heap_section_at`, scans for
  IL2CPP object headers (klass match + valid m_CachedPtr), returns
  authoritative instance lists. Bypasses both `findInstances`'s
  false-positive problem AND IL2CPP code stripping. New wire RPC like
  `walkManagedHeap(classFilter)`. Multi-day; only needed if 20a-2
  doesn't work on the user's target games.
- [ ] **20b** DiscreteUI Scene tab — new `SceneExplorerViewModel.cs` +
  `SceneExplorerView.axaml`. Lazy TreeView. Right-click context menu
  matching the existing patterns from Inspector + Methods tabs.
- [ ] **20c** Cross-tab handoff — "View instance" flips to Inspector
  tab with `SelectedInstance.Address = node.Address` populated. Reuse
  the Phase 19d-2 cross-tab plumbing already added between Inspector and
  Methods.
- [ ] **20d** Live verification on at least 2 targets (one IL2CPP, one
  Mono). Document any per-Unity-version quirks (older Unity may not have
  `Scene` struct as a managed type — fall back to
  `Object.FindObjectsOfType<GameObject>()`).

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

- [ ] **21a** DLL handler — `setInstanceField` in
  `dll/src/shared/ExportAPI.cpp`, dispatches per-type to a small
  `SafeWriteBytes` helper. Both backends share the writer (it's just
  process memory write). PowerShell smoke `Test-SetInstanceField.ps1`.
- [ ] **21b** UI editable cells — `LiveInspectionView.axaml` field grid
  goes editable. New `FieldEditViewModel` row state. Apply button
  bound to a command that calls the wire RPC.
- [ ] **21c** CE Lua bridge wrapper — short addition to
  `unity_invoke.lua` or a new sibling. Documented example in
  [lua-invoke.md](lua-invoke.md).
- [ ] **21d** Live verification — write `Player.HP` on SaGa F2R, watch
  CE address-list value update in real time. Do same for one Mono
  target.

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
- ❌ DLL bundling inside the CT (Bootstrap script just pings the DLL pipe; user keeps DiscreteUI + CE both running).

### Pre-work

✅ None needed — `handshake.protocol` (integer, currently 1) is already
emitted by `ExportAPI.cpp` (line 104) and documented in
`pipe-protocol.md` (line 146). 22c's Bootstrap AA script asserts on this
existing field (note: actual key is `protocol`, not `protocol_version`).

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
