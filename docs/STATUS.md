# UnityCEDumper — Session Hand-off

**Branch**: `dev` (Phase 19d-2 + Mono-side smoke verified live, changes uncommitted)
**Last committed**: `fix(deploy): glob *.lua in Install-CEScripts so new bridge scripts ship`
**Build**: 234 (after the 19d-2 AOT publish)
**Current state**: **Phase 19 fully shipped (19a–19e) AND verified live on both backends across DLL + UI + Lua bridge, plus 19d-2 polish landed and click-through verified.** Mono-side `unity_invoke.lua` smoke ran 2026-05-07 against BongoCat — all 4 scenarios green, including managed-exception capture (host stayed alive). The original CLAUDE.md goal "invoke managed Unity methods from CE Lua with marshaled args / return values" is closed end-to-end on both Mono and IL2CPP. **Phase 12d** (32-bit Mono) is now the only formally-deferred phase.

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
| 15 | ✅ | [test-matrix.md](test-matrix.md) — 7 verified targets across two backends + four metadata versions. |
| 17 (a/b/c/d) | ✅ | **Address Finder (reverse lookup).** New wire command `whatIsAt(addr)` returning instance + class + declared field. DLL backward-scan + class-pointer cache, UI Address Lookup tab, CE Lua bridge `unity_addrfind.lua`. Spec: [address-finder.md](address-finder.md). |
| 18 (a/b/c/d) | ✅ | **Find Method Instances + AOB AA-script generator + AOBMaker direct push.** New wire commands `findMethodInstances` + `getMethodAobPattern`, hand-rolled x64 LDE, UI overloads modal, `unity_symbols.lua` overload-aware extension, `CreateAAScript` integration over `\\.\pipe\AOBMakerCEBridge`. |
| **19 (a/b/c/d/e)** | ✅ | **Lua-callable Unity methods.** New wire RPC `callMethod(asm, ns, class, method, [signatureMatch], [instance], [args])` invoking Mono / IL2CPP methods with marshaled primitive + null + address + string args + boxed-primitive / address / UTF-8-decoded-string returns + managed exception capture. Static AND instance dispatch on both backends. UI MethodCallWindow (right-click any overload row → Call method…) + `scripts/unity_invoke.lua` Lua bridge with full arg-builder set. Spec: [lua-invoke.md](lua-invoke.md). |
| **19d-2** | ✅ | **UI polish.** Methods grid "Call method… (pick overload)" auto-skips the picker modal when `findMethodInstances` returns exactly one overload — single-overload user methods (`AddItems`, `get_numberOfItems`, `Save`) become one click. Inspector right-click on any instance → "Call method on this instance… (switch to Methods)" snaps `SelectedInstance` to that row + flips to the Methods tab; the subsequent Methods-grid right-click then pre-fills the `Instance address` textbox in `MethodCallWindow` (scope-checked: only when Browser.SelectedClass matches Inspector.TargetClass on assembly + namespace + name). |
| 16 (docs) | 🟡 Partial | `plan` / `STATUS` / `pipe-protocol` / `Archiutecture` / `test-matrix` / `address-finder` / `lua-invoke` / `il2cpp-metadata-crosscheck` exist; `dll-spec`, `ui-spec`, `mono-internals`, `il2cpp-internals`, `export-formats`, `technical-notes`, `lessons-learned`, `metadata-cli`, `proxy-dlls`, `Readme.MD` (en+zh-TW), `CONTRIBUTING.md` still TBD. |

---

## What landed in this session — Phase 19d-2 polish (auto-skip picker + Inspector cross-tab)

Two UX improvements on top of the freshly-shipped Phase 19 vertical. No wire-layer changes; no DLL rebuild required (UI-only).

- **Auto-skip overloads modal on count==1**. Methods grid right-click "Call method… (pick overload)" probes `findMethodInstances` first; when the result has exactly one entry, jumps straight to `MethodCallWindow` seeded from that overload. Multi-overload methods (`Mathf.Abs`, `String.Concat`) still drop into the picker. "Find overloads…" keeps today's always-show-modal behaviour because the user explicitly wants the AOB / Copy panel. Implemented as a split handler pair in `MainWindow.axaml.cs`: `OnMethodCallMethodClicked` (new fast path) + `OnMethodFindOverloadsClicked` (unchanged) + shared `ResolveMethodRow` / `BuildOverloadsVm` helpers so the two paths can't drift.

- **Inspector cross-tab "Call instance method…"**. Right-click any instance in the Inspector's Instances ListBox → "Call method on this instance… (switch to Methods)" snaps `SelectedInstance` to the right-clicked row and switches the right-pane TabControl to the Methods tab. From there, the Methods-grid right-click "Call method… (pick overload)" opens `MethodCallWindow` with the `Instance address` textbox pre-filled. The cross-tab handoff is **strictly scope-checked**: `GetDefaultMethodCallInstance` returns the inspector's selected instance ONLY when `Browser.SelectedAssembly.Name == Inspector.TargetAssembly` AND `Browser.SelectedClass.Namespace == Inspector.TargetNamespace` AND `Browser.SelectedClass.Name == Inspector.TargetName` (ordinal equality on all three). This prevents a stale inspector instance from bleeding into a method call on a different class, which would silently feed a wrong-type pointer to the trampoline.

- **Threading**: `MethodOverloadsViewModel`'s constructor gains an optional `defaultInstanceAddress` param (forwarded to `MethodCallViewModel`). `MethodCallViewModel`'s `defaultInstanceAddress` ctor arg already existed in 19d for future use; 19d-2 finally populates it from a real source. AOT publish stays clean — 0/0 trim/AOT analyzer warnings; `DiscreteUI.exe` 38.3 MB.

- **Files touched**: [MainWindow.axaml.cs](../ui/UnityDumpUI/Views/MainWindow.axaml.cs), [MainWindow.axaml](../ui/UnityDumpUI/Views/MainWindow.axaml), [MethodOverloadsViewModel.cs](../ui/UnityDumpUI/ViewModels/MethodOverloadsViewModel.cs), [MethodOverloadsWindow.axaml.cs](../ui/UnityDumpUI/Views/MethodOverloadsWindow.axaml.cs), [Resources/Strings/en.axaml](../ui/UnityDumpUI/Resources/Strings/en.axaml).

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
fix(deploy): glob *.lua in Install-CEScripts so new bridge scripts ship
feat: Phase 19e — scripts/unity_invoke.lua (CE Lua bridge for callMethod)
fix(ui): Phase 19d Call method… discoverability + status hint
docs: list CE-only mono_invoke_method as optional alternative to callMethod
feat: Phase 19d — UI MethodCallWindow for callMethod
docs: mark Phase 19c instance dispatch verified live (Mono / Farthest Frontier)
feat: Phase 19c — instance dispatch + string in/out marshaling
fix(ui): wire Skia + HarfBuzz explicitly after dropping Avalonia.Desktop
feat: Phase 19a/b — callMethod RPC (IL2CPP + Mono, static-only)
chore(ui): drop Avalonia.Desktop metapackage in favor of Avalonia.Win32
docs: refresh STATUS / Archiutecture / pipe-protocol / test-matrix for Phase 17+18 closure
docs(plan): draft Phase 19 — Lua-callable Unity methods + close out 18
fix(overloads): single-click checkbox in the overloads grid
fix(overloads): single-overload buttons + Create AA in CE via AOBMaker
feat: Phase 18d — registerMethodOverloads in unity_symbols.lua
```

---

## What's open

### Formally deferred

| Item | Why | Required to unblock |
|---|---|---|
| **Phase 12d — 32-bit Mono** | x86 support is unstarted. CMake currently enforces x64-only (`CMAKE_SIZEOF_VOID_P EQUAL 8` is a fatal); UI's `WindowsDllInjector` runs `CreateRemoteThread` from a 64-bit process and won't cross to 32-bit targets; we ship x64 proxy DLLs only. | Engineering only — no external blockers. Parallel x86 build config in cmake, x86 builds of `UnityDumper-Mono.dll` + both proxy variants with Mono 32-bit struct offsets verified, cross-bitness inject (or document "proxy install only" for x86), x86/x64 selection in the install flyout. |

### Phase 19 polish (drafted but deferred — would land as 19d-3)

- **Smart-typed editors** — NumericUpDown for sized ints (range-checked at the editor level instead of post-Call), ComboBox for enum params via `getEnumValues`. Current TextBox + culture-invariant parsing + post-Call error message works correctly but is plain.

### Open follow-ups (not blocking, drip-feed)

- **Phase 16 — remaining docs** — `dll-spec.md`, `ui-spec.md`, `mono-internals.md`, `il2cpp-internals.md`, `export-formats.md`, `technical-notes.md`, `lessons-learned.md`, `metadata-cli.md`, `proxy-dlls.md`, `Readme.MD` (en + zh-TW), `CONTRIBUTING.md`. Parallelizable with feature work.
- **Phase 5 segment 2d** (long-deferred, non-blocking) — full `Il2CppMetadataRegistration` AOB scan in `GameAssembly.dll` so non-enum const literals in `il2cppGetMetadataTypeFields` can be type-aware-decoded. Half-day.
- **Phase 17e — Strategy B address finder** — only-if-asked. Cached heap walk + interval tree for batch CE pointer-scan lookups. Skip until requested.
- **Phase 18 multi-overload AA-script user-click smoke** — Farthest Frontier `ItemBundle.AddItems` exposes 2 overloads (verified via `findMethodInstances`); end-to-end "Copy AOB AA script" against the multi-overload result not yet user-clicked through.
- **More automated test coverage** — current test suite covers protocol, exporters, pipe lifecycle, a few inspector helpers. Big gaps: classifier (enum vs value-type vs pointer routing), end-to-end `MetadataParser` round-trips per version, CE-XML live-mode rendering against a deterministic fixture, AobMakerBridge happy + sad-path with a fake pipe server. Drip-feed.
- **Highlight matching substring** in Fields / Methods / Inspector filter results — pure UI polish.
- **CSX live mode** — design dead-end as scoped (CSX is an offset-relative *structure* format; "live" only makes sense as snapshot-derived layout). CE XML already provides true absolute-address live mode. Defer until a user explicitly asks.

---

## Suggested first steps for the new session

| Option | Effort | Why pick this |
|---|---|---|
| **Phase 16 docs** | Variable, parallelizable | No user-facing feature gain but important for adoption / contributor onboarding. Pick one of: `lessons-learned.md` (consolidates the memory entries we've accumulated — runtime_invoke marshaling, signatureMatch substring trap, PowerShell ConvertTo-Json unwrap, Avalonia 12 Win-only trio), `Readme.MD` (en+zh-TW), or `CONTRIBUTING.md`. |
| **Inspector classifier unit tests** | <½ day | Cheapest test win. Offline regression coverage for the classifier kind-routing logic. |
| **Highlight matching substring** | <½ day | Pure UI polish — small but visible improvement for users who filter heavily. |
| **Phase 12d (32-bit Mono)** | Multi-day | Required for any pre-Unity-2018 Mono game. No user has asked yet (no 32-bit Unity game in test matrix). |

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

### Pending verification (small)

_(none — every shipped phase has a live click-through on the dev box.)_

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
