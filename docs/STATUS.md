# UnityCEDumper — Session Hand-off

**Branch**: `dev` (clean — pushed up to commit `587e794`)
**Last commit**: `587e794 fix(il2cpp): Unity 2018.x — demote two missing exports + MetadataParser fallback`
**Build**: 253 (post-verification warning cleanup; live verification used #251)
**Current state**: **All shipped phases verified live + 4 backlog cosmetics ✅ live-verified 2026-05-08 against build #251 across four targets (Silksong / SaGa F2R / FF VI / R-Type). ExportAPI.cpp:1863 C4244 narrowing warnings cleaned up in #253 (no functional change — `metadataVersion` typed as `int32_t` to match `MetadataParser::version()`'s return).** Phase 19 + 19d-2 closed across DLL → UI → CE Lua bridge on both backends. Phase 16 docs landed. Phase 5 segment 2d cross-version sweep against Unity 2018.1 → 6.1 (metadata v24 / v29 / v31 / v39) confirms `Il2CppMetadataRegistration` structural-shape AOB scan is fully version-portable; v39 runtime sweep 9/9 green. **Phase 20a (Scene/GameObject explorer Lua bridge)** is parked at "FindObjectsOfType(Type, bool) recipe untested" after surfacing four cliffs (envelope unwrap, main-thread marshaling, findInstances false positives, IL2CPP code stripping) — see open follow-ups. **Backlog sweep 2026-05-08**: ✅ v24 integer-decode (commit `cb3d100`, **live-verified FF VI**), ✅ Unity 2018.x missing IL2CPP exports (commit `587e794`, **live-verified R-Type Dimensions EX**), ✅ findInstances Mono `class_ptr` consistency (commit `3e519a8`, **live-verified Silksong**), ✅ JSON Inf/NaN R4/R8 `value_kind` (commit `fd428d2`, **live-verified SaGa F2R**). Phase 12d (32-bit Mono) re-estimated as 3-5 days (user has test target). **Phase 22** (.CT embedding + AA-script generator UI tab) **promoted to next active phase** per user direction. Session ending — fresh session will pick up from Phase 22 design + live verification of the 4 fixes.

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

---

## What landed in this session — Phase 16 docs + Phase 5 segment 2d cross-version sweep

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

## What landed in the previous session — Phase 19d-2 polish

Two UX improvements on top of the freshly-shipped Phase 19 vertical. UI-only.

- **Auto-skip overloads modal on count==1**. Methods grid right-click "Call method… (pick overload)" probes `findMethodInstances` first; on count==1, jumps straight to `MethodCallWindow`. Multi-overload methods still drop into the picker. Split handler pair in `MainWindow.axaml.cs` (`OnMethodCallMethodClicked` + unchanged `OnMethodFindOverloadsClicked`) + shared `ResolveMethodRow` / `BuildOverloadsVm` helpers.

- **Inspector cross-tab "Call instance method…"**. Right-click any instance in the Inspector → "Call method on this instance… (switch to Methods)" snaps `SelectedInstance` + flips to Methods tab. The Methods-grid right-click then pre-fills `Instance address` in `MethodCallWindow` (scope-checked: only when Browser.SelectedClass matches Inspector.TargetClass on assembly + namespace + name).

Files: [`MainWindow.axaml.cs`](../ui/UnityDumpUI/Views/MainWindow.axaml.cs), [`MainWindow.axaml`](../ui/UnityDumpUI/Views/MainWindow.axaml), [`MethodOverloadsViewModel.cs`](../ui/UnityDumpUI/ViewModels/MethodOverloadsViewModel.cs), [`MethodOverloadsWindow.axaml.cs`](../ui/UnityDumpUI/Views/MethodOverloadsWindow.axaml.cs), [`Resources/Strings/en.axaml`](../ui/UnityDumpUI/Resources/Strings/en.axaml). AOT publish 0/0 warnings; `DiscreteUI.exe` 38.3 MB.

---

## What landed previously — Phase 19 (full vertical) + UI Windows-only cleanup

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

- **Phase 22 — `.CT` embedding + AA-script generator UI tab** (PROMOTED to next active phase, ~2-3 days) — user confirmed this is the most practical of the three new phases. End-users only enable/disable AA scripts in `.CT`s; raw `dofile` is not their workflow. Phase 22 packages whatever IS shipping (Phase 17 / 18 / 19 + Phase 20a partial) into a self-contained `.CT` with embedded `unity_*.lua` + auto-built AA scripts. Reference pattern: `D:\Github\Mydev-Cheat-Engine-Tables\Crimson Desert\CrimsonDesert.CT`. Full design in [plan.md §Phase 22](plan.md#phase-22--ct-embedding--aa-script-generator-ui-tab-deferred).
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
| **Phase 22 — `.CT` embedding + AA-script generator UI tab** | 2-3 days | **User-confirmed top priority** (2026-05-08). End-users only enable/disable AA scripts; this phase makes Discrete production-shippable. Packages already-working Phase 17 / 18 / 19 + Phase 20a partial. Full design in [plan.md §Phase 22](plan.md#phase-22--ct-embedding--aa-script-generator-ui-tab-deferred). |
| **Phase 20a-2 — verify FindObjectsOfType recipe** | ~30 min | Quick verification of the 2-arg recipe pivot at session end 2026-05-08. If it works, Phase 20a closes; if not, opens Phase 20-HEAP (DLL-side libgc walker). |
| **Phase 21 — field write-back** | ~½ day | Real capability gap: today the Inspector only reads. New wire RPC + SEH-protected write + editable UI cells. Full design in [plan.md §Phase 21](plan.md#phase-21--inspector-field-write-back). |
| **Inspector classifier unit tests** | <½ day | Cheapest test-coverage win. Offline regression coverage for the classifier kind-routing logic. |
| **Highlight matching substring** | <½ day | Pure UI polish — visible improvement for users who filter heavily. |
| **Phase 12d (32-bit Mono)** | 3-5 days | User has 32-bit Mono test target (confirmed 2026-05-08) — engineering effort only. Parallel x86 build config in CMake, x86 backend DLL with Mono 32-bit struct offsets, x86 proxy DLLs, cross-bitness inject, x86/x64 selector in installer. |
| **v32+ metadata.dat parser** | 2–3 days | Only blocks const-decode + offline `metadata-cli` on Unity 6.1+. v39 runtime already works. Worth it if anyone asks for offline v32+ analysis. Full design ready in [il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md). |

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
