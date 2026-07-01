# Test matrix

Phase 15. Compatibility ledger for the games + tooling versions Discrete has been exercised against. Add a row whenever you verify a new target end-to-end (handshake → walkInstance → at least one decoded value matches a manual CE read).

> Maintenance note: when you add a new row, also drop the raw `metadata-cli info` and `metadata-cli stats` JSON into the **Notes** column or its own subsection so future bisects can cross-check against the same blob without re-running the game.

---

## Unity targets (verified)

| Game | Unity | Backend | Metadata ver | Bitness | DLL build verified | Workflow | Notes |
|---|---|---|---|---|---|---|---|
| **SaGa Frontier 2 Remastered** | `2021.3.39f1` | IL2CPP | v29 | x64 | 101 (Phase 11), 108 (Phase 14), 110+ (Phase 12), 150 (Phase 14c), 188+ (Phase 17), 201+ (Phase 18), 209+ (Phase 19a/c/e) | Pick & inject AND proxy install | **Primary verification target.** All phases 6–14 + 17 + 18 + 19 validated here. `MainGame.PLY_WORK` decode round-trips against a manual CE Structure Dissect (Phase 11.5 incl. `Vector3`/`reserv2[]` substruct expansion). Phase 17 `whatIsAt` round-trips against `PLY_WORK.HP` field address. **Phase 18** AOB pattern generation verified against `MainGame/PLY_WORK.AddGrowHpMax(int)` — single-overload, 32-byte AOB with 0 wildcards (simple `MOVSS [rcx],xmm0` prologue), suffix `int`, "Copy AOB AA script" writes a complete CE Auto-Assembler script to clipboard. **Phase 19a/c/e** verified end-to-end: `Mathf.Abs(int:-7) → 7`, `Mathf.Abs(float:-3.5) → 3.5`, `Convert.ToByte(int:-1) → System.OverflowException`, `String.Concat("Hello, ", "World") → "Hello, World"`, `Int32.Parse("42") → 42` — across PowerShell smoke (`Test-CallMethod.ps1`) AND the CE Lua bridge (`unity_invoke.lua` from CE 7.5.x console). `metadata-cli info`: 10001 type defs, 11392 string literals. |
| **SaGa Frontier Remastered** | `2019.4.16f1` | IL2CPP | **v24** | x64 | 150 (Phase 14c) | Static metadata only (`metadata-cli`) | Phase 14c verification target: 7,514 type defs parse cleanly, canonical leading types (`<Module>`, `<>f__AnonymousType0\`1`, `Locale`, `SR`, `Mono.Runtime`) with correct IL tokens. Live walkInstance not yet exercised against this older IL2CPP runtime. |
| **FINAL FANTASY VI PR** | `2019.4.26f1` | IL2CPP | **v24** | x64 | 150 (Phase 14c), 235 (Phase 5 segment 2d cross-version sweep, 2026-05-07) | Static metadata + live-runtime sweep | Second v24 fixture — confirms layout works across Unity 2019.4 sub-versions. 11,438 type defs, 11,537 string literals. **Phase 5 segment 2d sweep 2026-05-07**: registration scan + R4 / R8 const decode all pass; surfaced a real I*/U* integer-decode bug — v24 metadata stores raw little-endian int bytes, not the CLI compressed-int + sentinel encoding our decoder assumed (which is v29+ behaviour). `System.Int32.MaxValue` returns -2147483648 instead of 2147483647 because byte 0 = 0xFF is read as the type_min sentinel. Fix sketched in archive notes (`v24 integer-decode bug`, resolved in commit `cb3d100`). |
| **R-Type Dimensions EX** | `2018.1.9f2` | IL2CPP | **v24.0** ✨ | x64 | 235 (Phase 5 segment 2d cross-version sweep, 2026-05-07) + 479 (offline `metadata-cli` ValidateTypeTable() sanity check, 2026-05-24) + **480 (full v24.0 / v24.1 layout characterization, 2026-05-24)** | Static metadata (`metadata-cli`, v24.0) + Inject + isolated registration probe | **Oldest IL2CPP target in the matrix and the only confirmed v24.0 sample.** Two independent issues stacked on this game; both now characterized: <br><br>**(a) Runtime export gap (still open)**: the `il2cpp_image_get_class_count` + `il2cpp_image_get_class` exports were added between Unity 2018.1 and 2019.x. `IL2CPPResolver` reports `required 16/18, optional 23/25 (resolved=no)` and bails — every IL2CPP-exports-based RPC returns `"il2cpp runtime not resolved"`. The structural-shape `Il2CppMetadataRegistration` AOB scan still works (`found=YES, types_count=19874`), confirming Phase 5 segment 2d's discovery heuristic is version-portable from Unity 2018.1 through Unity 6.1. Workaround documented in archive notes (`Unity 2018.x IL2CPP missing-exports`): demote the two missing exports to OPTIONAL + use the now-correct `MetadataParser`-table-driven fallback through `il2cpp_class_from_name`. ~2 hr, deferred until a user asks. <br><br>**(b) Metadata-parser v24 sub-version gap (CLOSED build #480)**: upstream Il2CppDumper splits v24 into v24.0 / v24.1 / v24.2-v24.5 sub-versions with different `Il2CppTypeDefinition` strides (104 / 100 / 92 bytes — older Unity 2018 builds carry `customAttributeIndex` + `rgctxStartIndex` + `rgctxCount` fields later dropped). Build #479 caught the silent corruption via `Parser::ValidateTypeTable()` ('<Module>' / token=0x02000001 invariant) and demoted to strings-only. Build #480 ([dll/src/il2cpp/MetadataLayout.h](../dll/src/il2cpp/MetadataLayout.h) + [MetadataParser.cpp](../dll/src/il2cpp/MetadataParser.cpp)) added `TypeDefinition_v24_0` (104 bytes) / `TypeDefinition_v24_1` (100 bytes) + `FieldDefinition_v24_0` (16 bytes with the extra `customAttributeIndex`) plus `DetectV24Variant()` which probes the right stride at load time by checking which candidate lands TypeDef[0].token at `0x02000001`. Live result on R-Type Dimensions: `{"version":24,"v24_sub_version":"v24.0","loaded":true,"type_tables_parsed":true,"type_definition_count":3841,"string_literal_count":3828}`; strip stats `verdict:"clean"`, `strip_ratio:0.0005` (only 2 two-char names — pristine debug build). Field decode verified: `System.Int32` returns the canonical 3 fields (`MaxValue`/`MinValue`/`m_value`) with `has_default:true` on the const pair + tokens `0x04000001` / `0x04000002` / `0x04000003`. Same `MetadataParser` code path serves both CLI and live DLL, so any future MetadataParser-driven RPC against a Unity 2018.x v24.0/v24.1 target will now read correctly. |
| **Townscaper** | `2020.3.4f1` | IL2CPP | **v27** ✨ | **x86** | 150 (Phase 14c) | Static metadata only | Phase 14c v27 target — only 32-bit IL2CPP build in the corpus. Its `global-metadata.dat` parses identically to v29 (88-byte stride, IL token at +84). 8,132 type defs match canonical leading types — confirming the metadata format is bitness-neutral even when the game runtime is x86. Live runtime support deferred to Phase 12d. |
| **The Legend of Tianding / 廖添丁** | `2020.3.48f1` | IL2CPP | **v27** ✨ | x64 | **404 (Phase 20-MT v27 verification, 2026-05-20)** | Pick & inject + Phase 20-MT stress smoke + `Test-MetadataFields` | **First x64 v27 IL2CPP target in the matrix — closes the long-open "v27 verification" hole.** End-to-end PASS: handshake (81 assemblies, hook armed), Phase 20-MT 1000-iteration `Mathf.Abs` burst (8.33 ms/call main-thread, drain_count=1000, 0 timeouts, ticks=303870 observed during burst — confirms the runtime_invoke hook fires + the dispatcher drains correctly on v27 IL2CPP same as on v29/v31/v39). `Test-MetadataFields System.Int32`: `MaxValue` decodes to 2147483647 + `MinValue` to -2147483648 (raw-LE 4-byte path, `enc=4B`) — **proves the v24/v27 → raw-LE / v29+ → sentinel-compressed-int dispatch (build #251 commit `cb3d100`) works on v27 the same as v24**. `Test-MetadataFields System.Single`: `MinValue` / `Epsilon` / `MaxValue` decode correctly; `PositiveInfinity` / `NegativeInfinity` / `NaN` show "(no decode)" in the smoke script but the wire correctly carries `value_kind` (JSON Inf/NaN coercion fix, build #251 commit `fd428d2`). No remaining v27 decoder bugs surfaced. |
| **Farthest Frontier** | `2022.3.62f1` | **IL2CPP + Mono** ✨ | **v31** (IL2CPP path) | x64 | 150 (Phase 14c), 213+ (Phase 19c) | Static metadata + live `callMethod` (both backends) | Phase 14c v31 target #1: 15,848 type defs, 20,788 string literals. Layout empirically verified to match v29 exactly (same 88-byte stride). **Phase 19c instance dispatch verified live on the Mono path**: `ItemBundle.get_numberOfItems()` (no-arg instance method, returns UInt32) called against a live instance address returned `1`. Same game's IL2CPP build also surfaced 2 overloads of `ItemBundle.AddItems` via `findMethodInstances` — `(System.UInt32, System.UInt32)` + `(ItemBundle)` — confirming wire-shape parity across the two backends. Game has both Mono and IL2CPP launch options, useful for cross-backend regression. |
| **Emberward** | `2022.3.62f2` | IL2CPP | **v31** | x64 | 150 (Phase 14c) | Static metadata only | Phase 14c v31 target #2: 23,982 type defs, 23,828 string literals. Largest type table in the corpus. |
| **Heroes of Might and Magic: Olden Era** | `6000.0.66f1` ✨ (Unity **6.0 LTS**, build hash `e7adf66625be`) | IL2CPP | **v31** | x64 | 477 (`metadata-cli` static check, 2026-05-24) | Static metadata only (`metadata-cli`) | **First Unity 6.0 LTS sample in the corpus and a corpus-relevant data point: Unity 6.0 LTS still ships metadata v31, while Unity 6.1 (Songs of Silence `6000.3.8f1`) jumped to v39 — confirming the v31 → v39 format switch lives at the 6.0 → 6.1 boundary, not at the Unity 5/6/2022.3 boundary.** UnityPlayer.dll FileVersion `6000.0.66.44534` (per Windows file properties, 2026-05-24). Installed under `D:\SteamLibrary\steamapps\common\Heroes of Might and Magic Olden Era\`. On-disk `global-metadata.dat` is 22,350,592 bytes; magic `AF 1B B1 FA` + version word `1F 00 00 00` (= 31) verified by hex inspection. `metadata-cli info`: `{"version":31,"loaded":true,"type_tables_parsed":true,"type_definition_count":24682,"string_literal_count":15889}`. `metadata-cli stats`: `{"total_classes":24682,"with_namespace":14208,"single_char_names":24,"short_names_2":668,"compiler_generated":1953,"numeric_lead":0,"min_name_length":1,"max_name_length":83,"avg_name_length":16.31,"strip_ratio":0.0304,"verdict":"clean"}`. First 10 types sample shows canonical BCL leaders (`<Module>`, `Microsoft.CodeAnalysis.EmbeddedAttribute`, `System.Runtime.CompilerServices.NullableAttribute` / `NullableContextAttribute`) plus game classes with mixed naming — readable names (`CameraShake`, `ShakeSettings`, `EShakeType`) alongside a handful of two-char obfuscated leaf classes (`bc`, `bd`, `be`) — consistent with the 0.0304 strip-ratio "clean" verdict. Live `walkInstance` / `findInstances` / `callMethod` not yet exercised. |
| **Hollow Knight Silksong** | `6000.0.50f1` | Mono | n/a | x64 | 161+ (Phase 16-prep session) | Pick & inject | **Mono bdwgc verification target.** `PlayerData` (1350 fields, 3-level inheritance chain) walks end-to-end. Validates: (a) Mono walkInstance against Unity 6 bdwgc; (b) server-side enum detection (`mapZone : GlobalEnums.MapZone` decodes via `class_is_enum` + `class_enum_basetype` to its underlying `System.Int32`, returns `value: 6` instead of the catch-all 8-byte ptr); (c) sort-by-offset, instance-switch selection preservation, refilter perf at scale. **Still un-exercised**: `unity_symbols.lua` Mono `monoCompileMethod` registration path — last piece needed to fully close out "Mono bdwgc end-to-end" coverage. |
| **Songs of Silence** | `6000.3.8f1` (Unity 6.1) | IL2CPP | **v39** ✨ | x64 | 235 (Phase 5 segment 2d cross-version sweep + V39 runtime sweep, 2026-05-07) + **336 (Phase 5 v32-v39 metadata.dat parser live-smoke, 2026-05-19)** | Pick & inject + `Test-V39RuntimeSweep.ps1` + offline `metadata-cli` | **First v32+ target in the matrix and fully supported as of 2026-05-19.** Three-stage verification: (1) Phase 5 segment 2d structural-shape AOB scan in `IL2CPPMetadataRegistration::Find()` works (`il2cppGetMetadataRegistration` returns `found=true`, `types_count=144837`). (2) Full runtime sweep: `getAssemblies` (253 assemblies including `quantum.code`), `getClasses UnityEngine.CoreModule` (1093 classes), `getClassInfo UnityEngine.GameObject` (143 methods, parent=Object correctly walked), `findInstances UnityEngine.GameObject` (8/8 matches), `walkInstance` (decoded `m_CachedPtr` at +0x10), `findMethodInstances UnityEngine.Mathf.Abs` (2 overloads with AOT addresses + decoded signatures), `callMethod Mathf.Abs(int:-7) → 7`. (3) **Offline `metadata-cli` on real v39 `global-metadata.dat` (build #336, 2026-05-19)**: `info` reports `version=39, loaded=true, type_tables_parsed=true, type_definition_count=40501, string_literal_count=44047`; `stats` reports `verdict="clean"` across all 40k types; `types` resolves canonical Unity types (`UnityEngine.Vector3` index 15063, `UnityEngine.Mathf` 15065, `UnityEngine.GameObject` 15151); `type UnityEngine Vector3` decodes all 15 expected fields with correct tokens 0x04000A3B+ — validates the section-based HeaderV38 reader + variable-width metadata index plumbing end-to-end on real-world data. |
| **Endless Legend** | `5.3.6p1` | Mono (**legacy `mono.dll`**) ✨ | n/a | x64 | **345 (legacy-mono resolver gap fix, 2026-05-19)** | Pick & Inject (x64 in-process injector) | **First Unity 5.x / pre-bdwgc legacy mono.dll target in the matrix.** Surfaced a resolver-gate gap: legacy Unity 5.3-era `mono.dll` doesn't export `mono_assembly_get_name` / `mono_assembly_name_get_name` / `mono_free` (bound 29/32, `resolved=false`, every RPC after handshake returned `"mono runtime not resolved"`). All three are already null-guarded at call sites (MonoDomain.OnAssembly falls back via `image_get_name`; MonoTypeParser skips `r.free` when null — leaks a small string per type-name lookup but non-fatal). Fix: demote to `optBind`. Post-fix: 32/32 bound, `assemblies_count=26` (single bundled `UnityEngine` assembly + 25 others including `Assembly-UnityScript-firstpass` — UnityScript era!), `Assembly-CSharp` enumerates 6683 classes, `findInstances UnityEngine.Transform` returns 100+ matches in 5 sec across 381 regions (840 MB scanned), `walkInstance` on a real Transform decodes 2 fields: `m_InstanceID` (System.Int32) at +0x10 = 309595880, `m_CachedPtr` (System.IntPtr) at +0x18 — the Unity-5-only `m_InstanceID` field that modern Unity removed; correct x64 offsets via `ManagedLayout::kObjectHeader=0x10` + natural alignment padding. **Cross-validates the arch-aware layout abstraction across both bdwgc and legacy-mono backends.** Also fixed `Inject-Dll.ps1` to prefer `dist/` first so the stale pre-12d `build/dll-both-release/bin/` doesn't shadow new builds. |
| **Kingdom** | `5.3.4f1` | Mono (**legacy `mono.dll`**) | n/a | **x86** ✨ | **349 (Phase 12d + TEB-fix live-smoke, 2026-05-19)** | `inject-x86.exe` cross-bitness inject | **First x86 + legacy-mono target — the 4th corner of the bitness × runtime matrix.** Auto-routed through `inject-x86.exe`; handshake clean (backend=Mono, unity_version=5.3.4f1, 18 assemblies). `findInstances UnityEngine.Transform` returned 100+ matches in 539 ms across 233/395 scanned regions (211 thread stacks correctly filtered = 1 per thread, no bogus duplicates). `walkInstance` decoded **x86-specific layout** with no padding hole: `m_InstanceID` (Int32) at offset **0x08** = 491781376 (real instance ID); `m_CachedPtr` (IntPtr) at offset **0x0C** — packs directly adjacent because IntPtr is 4-byte aligned on x86, unlike x64 where the int32 → IntPtr transition leaves a 4-byte padding hole. **Confirms `ManagedLayout::kObjectHeader = 2 * sizeof(void*)` + natural-alignment-aware field offsets work across all 4 mono variants (bdwgc × legacy × x64 × x86).** |
| **LEAVES - The Return** | `5.4.3f1` | Mono (**legacy `mono.dll`**) | n/a | **x86** ✨ | **349 (Phase 12d + TEB-fix live-smoke, 2026-05-19)** | `inject-x86.exe` cross-bitness inject | **Surfaced the TEB-offset bug** that motivated the build #349 fix. Memory.cpp's `CollectThreadStackRanges` hardcoded x64 TEB offsets (StackBase +0x08, StackLimit +0x10); on x86 those offsets pointed at FiberData / SubSystemTib, producing bogus "stack" ranges that overlapped legitimate heap regions → ScanForPointer over-filtered → `findInstances Transform = 0` despite `class_ptr` resolving cleanly. Fix: scale offsets with `sizeof(void*)` (same pattern as ManagedLayout). Before/after on LEAVES: `skipStack` dropped 572 → 217 (1 per thread), `regions scanned` 23 → 380, `bytes scanned` 54 MB → 305 MB, `count` **0 → 7 Transforms**. The bug was silently degrading every x86 target's scan; Legend of Mortal happened to find 19 instances despite bogus filtering because its bdwgc heap allocated in regions that didn't overlap the random stack ranges. Post-fix Legend of Mortal scans should also improve (re-test pending). |
| **Subnautica** | `2019.4.36f1` | Mono (bdwgc) | n/a | x64 | **466 (passive 2026-05-20 build seen alive in a 3rd-party's environment 2026-05-25; user did not click-through, but DLL-side resolver + main-thread hook armed cleanly)** | Proxy DLL (pre-installed by user) | **Used 2026-05-25 as a control sample to disprove the AnvirHook8_64.dll "anti-hook" hypothesis from the Leaf it Alone diagnosis** — Subnautica is a completely unprotected Unity Mono game; if `AnvirHook8_64.dll` were game-side protection, this game would NOT load it. It did → AnvirHook is user-machine-wide injected (likely Anvir Task Manager Pro or a similar Windows sysadmin tool), not game-side protection. Bonus coverage: confirms `MonoResolver: mono-2.0-bdwgc.dll — bound 29/29 exports` + main-thread hook armed on `mono_runtime_invoke` on Unity 2019.4 (filling a gap between 2017.x and 2020.3 in the Mono-bdwgc matrix). Also empirically validates the **same-PID double-inject singleton guard**: a newer build #549 attempted attach 55 s after #466 had already taken the PID, was correctly turned away with `DLL skipping init: another backend already claimed PID`, and the pipe/walk activity continued cleanly via #466 — no crash, no resource leak. UI-side observation worth follow-up: 211 `EnumerateAssemblies` calls in 16 seconds (~76 ms cadence) suggests a polling loop in some VM is over-refreshing — flagged as a separate spawned task. Click-through walk / instance / call-method smoke remains untested — but every DLL-level pre-requisite is green. |
| **Legend of Mortal** | `2020.3.49f1` | Mono (bdwgc) | n/a | **x86** ✨ | **339 (Phase 12d-1..d-4 live-smoke, 2026-05-19) + 342 (inject-x86.exe, Phase 12d-5) + 349 (TEB fix retroactively improves scan, re-test pending)** | Proxy DLL drop AND `dist/x86/inject-x86.exe` cross-bitness inject helper | **First 32-bit Mono target in the matrix.** Closes Phase 12d end-to-end. Live PASS chain: `Test-Handshake.ps1 -SkipInject -Backend Mono` returned `backend=Mono, unity_version=2020.3.49f1, assemblies_count=114, build=339`; `Test-FindInstances` returned 19 Transform instances + `scan_stats: regions=1077 scanned=20 skipImage=339 skipStack=718` (ScanForPointer with 4-byte pointer width); `Test-WalkInstance` on a returned Transform decoded `m_CachedPtr` at offset **0x08** — the critical x86 MonoObject-header validation (was 0x10 on x64), confirming `ManagedLayout::kObjectHeader = 2 * sizeof(void*)` resolves correctly; `Test-MonoWalk` enumerated 21 Assembly-CSharp classes + UnityEngine.GameObject (77 methods, parent=Object); `Test-MonoOps` returned a real JIT address for `UnityEngine.Object.GetInstanceID` (0x3E6F6328, 32-bit). **Cross-bitness inject helper (Phase 12d-5) live PASS**: `inject-x86.exe 55456 Discrete-Mono.dll` reported `wow64=true remote_alloc=0x00C20000 LoadLibraryW=0x00D70000 OK` (exit 0); subsequent handshake confirmed DLL loaded and pipe operational. Note: `kInstanceID_None` static-field probe failed — Unity 2020+ inlines `const int = 0`, drops it from the reflection table; not an x86-specific issue. Cross-bitness pipe: x64 PowerShell client → x86 DLL server worked transparently (NamedPipe is bitness-agnostic kernel object). |

## Unsupported targets (confirmed failures)

Targets where the v1 backend fails cleanly with a diagnosable reason —
recorded so we don't re-investigate the same game twice and so future
fallback work (Phase 5-2d AOB cross-link, export-less IL2CPP fallback)
has concrete targets to validate against.

| Game | Unity | Backend | Failure mode | DLL build observed | Notes |
|---|---|---|---|---|---|
| **Leaf it Alone** ([Steam #3981100](https://store.steampowered.com/app/3981100/Leaf_it_Alone/)) | `6000.2.6f2` (Unity **6.1**) | IL2CPP | **Triple-symptom obfuscation** — see [lessons-learned §"Obfuscated / packed IL2CPP target diagnostic fingerprint"](lessons-learned.md#obfuscated--packed-il2cpp-target-diagnostic-fingerprint) | 549 (2026-05-25, other user's build of `ba17b88`) | **Two definitive obfuscation symptoms** present: (1) **`il2cpp_*` exports all stripped / hidden** — `IL2CPPResolver: GameAssembly.dll — required 0/16, optional 0/27 (resolved=no)`; not a single one of the 43 standard exports is reachable via `GetProcAddress`. (2) **`global-metadata.dat` encrypted** — `global-metadata.dat magic mismatch (corrupt or encrypted)` (the canonical `0xFAB11BAF` is missing from offset 0; runtime presumably decrypts in-memory on first access). **Originally also flagged `AnvirHook8_64.dll` co-loading as a third symptom but corrected 2026-05-25** — see [Subnautica](#unity-targets-verified) row: the same DLL appears in Subnautica's module list and that game is completely unprotected Mono, so AnvirHook is **user-machine-wide injected** (likely Anvir Task Manager Pro or similar sysadmin tool) and NOT game-side protection. The two real symptoms (export stripping + metadata encryption) are sufficient on their own. No `walk` log category was even created because every RPC bails at the `if (!IsResolved()) return` gate. **Clean failure mode** — pipe handshakes cleanly, no crash, no hang. Requires Phase 5-2d (`IL2CPPMetadataRegistration` AOB cross-link, currently a non-blocking follow-up) **plus** a new "export-less IL2CPP fallback" phase (AOB-scan `il2cpp_*` function prologues + self-bind, no `GetProcAddress`) to make any progress. ~3-5 days of work; not currently scheduled. Workaround for now: not supported. |

---

## Steam scan recall

Game picker (`Scan Steam…` button on the Proxy DLL flyout) walks `libraryfolders.vdf` from these libraries on this dev box:

- `C:\Program Files (x86)\Steam` — primary install
- `D:\SteamLibrary` — secondary

All three Unity games above show up in the picker output with backend, bitness, and currently-installed proxy correctly identified. `Show unsupported` checkbox toggles 32-bit / Unknown rows; none observed yet in this corpus.

---

## Tooling versions

| Component | Version pinned | Notes |
|---|---|---|
| Avalonia | `12.0.1` (`ui/Directory.Build.props`) | Native AOT publish stays at 0/0 warnings/errors. |
| .NET SDK | `10.0.203` (per `dotnet --version` on this box) | UI is `net10.0-windows`. Tests use xUnit v3. |
| MSVC | `14.50` (Visual Studio 18 / Build Tools) | DLL targets x64 only. SEH-leaf rule documented in [Archiutecture.md](Archiutecture.md). |
| CMake | bundled with VS 18 (`3.30+`) | Phase 12 proxies + Phase 13 CLI go through the same Ninja generator. |
| Cheat Engine | `7.5.1` confirmed | Lua-side `enumModules()` + `injectDLL()` paths used by `unity_dump.lua` (Phase 11). Older CE versions may not export `injectDLL` — script falls back to `loadLibrary` per the CE-portability fix in `b8ce152`. |
| AOBMaker (CE plugin) | optional | Listens on `\\.\pipe\AOBMakerCEBridge`. Used by the Inspector's per-row `HEX` button to navigate CE's Memory View. When not running, the button no-ops (logs to `ui/view`); rest of the inspector keeps working. |
| Vendor sources | tracked by `sync_vendor.ps1` | `dll/vendor/nlohmann/` (json single-header), `dll/vendor/minihook/` are build deps. `vendor/{libil2cpp,Il2CppDumper,BepInEx,mono,UnityExplorer,Il2CppAssemblyUnhollower}/` are reference repos for cross-checking IL2CPP layouts. Both subtrees gitignored — re-run the script when you want to refresh upstream. |

---

## Phase coverage gaps

These are the failure modes the test matrix doesn't cover *yet* — useful when scoping Phase 14c / 12d / future sub-phases.

| Gap | Need a target with… | Owning phase |
|---|---|---|
| ~~32-bit Mono (legacy Unity ≤ 2017)~~ | ✅ **Fully closed 2026-05-19** — 3 different x86 Mono targets live-smoked: Legend of Mortal (Unity 2020.3 / bdwgc), Kingdom (Unity 5.3.4 / legacy), LEAVES (Unity 5.4.3 / legacy). All work via inject-x86.exe + auto-routing in DiscreteUI / Inject-Dll.ps1. Surfaced TEB-offset bug in CollectThreadStackRanges (silently degraded every x86 target's scan); fixed in build #349. | 12d (1..5 closed) + Memory.cpp TEB fix |
| Heavy obfuscator output | A Beebyte / Eazfuscator-built target | 14b banner — already wired, just needs a real test |
| `getMetadataStats` `partial_strip` verdict | Same as above (or a partial-strip game) | 14b |
| ~~Stripped metadata via mismatched magic~~ | ✅ **Real target confirmed 2026-05-25** — [Leaf it Alone](#unsupported-targets-confirmed-failures) (Unity 6.1 / IL2CPP) reproduces the encrypted-metadata path AND export-stripping AND third-party anti-hook DLL simultaneously. Resolver bails cleanly with `required 0/16, optional 0/27 (resolved=no)`; metadata parser logs `magic mismatch (corrupt or encrypted)`; no crash, no hang, no `walk` log category created. v1 cannot handle this class of target — needs Phase 5-2d AOB cross-link + new export-less IL2CPP fallback. | error-banner verified live; v1 fallback not yet built |
| ~~**Unity 2018.1 v24 TypeDefinition layout**~~ | ✅ **Closed 2026-05-24 (build #480)** — added `TypeDefinition_v24_0` (104 bytes) / `TypeDefinition_v24_1` (100 bytes) + `FieldDefinition_v24_0` (16 bytes) layouts to `MetadataLayout.h` and `Parser::DetectV24Variant()` which probes the right stride by validating TypeDef[0].token=0x02000001. R-Type Dimensions EX now parses cleanly as v24.0 (3841 types, all canonical BCL names + sequential tokens). Same `MetadataParser` code path serves CLI and live DLL — fix covers both. The only remaining Unity-2018.x gap is the IL2CPP missing-exports issue (separate, still deferred). | metadata layout — closed |
| Unity 6 Mono `monoCompileMethod` symbol registration | Silksong (already in the matrix) — just need to run `unity_symbols.lua` against it | Mono coverage closure |
| **Phase 18 multi-overload AA-script generation** | Any class with genuinely overloaded methods (Farthest Frontier `ItemBundle.AddItems` exposes 2 overloads — `(System.UInt32, System.UInt32)` + `(ItemBundle)`. Confirmed via `findMethodInstances` smoke; AOB pattern generation against the multi-overload result not yet user-clicked through.) | Phase 18 — single-overload verified; multi-overload AA-script not yet user-clicked |
| **Phase 18 `unity_symbols.lua` `registerMethodOverloads`** | Any overloaded method, exercise the Lua bridge from CE's console. | Phase 18 — code in, exercised once at minimum (CE Lua dofile); no comprehensive smoke run |
| ✅ **AOBMaker `CreateAAScript` direct push** | CE running with the AOBMaker plugin loaded — verified live 2026-05-07. | Phase 18 closed |
| ✅ **Phase 19 — Lua-callable methods** | Static + instance + primitive + string args + string return + managed exception capture. Verified live across SaGa F2R + BongoCat + Farthest Frontier; PowerShell smoke (`Test-CallMethod.ps1`) AND CE Lua bridge (`unity_invoke.lua`) both green. | Phase 19 closed |
| **Phase 19 CE-Mono unity_invoke.lua side** | Run unity_invoke.lua against BongoCat's Mono backend from CE's Lua console. Implementation is byte-identical at the wire layer (verified via PowerShell), but the Lua wrapper hasn't been exercised on the Mono path live. | Phase 19 — IL2CPP-side Lua-bridge smoke green; Mono-side Lua-bridge smoke pending |
| AOBMaker bridge integration (HEX navigate) | CE running with the AOBMaker plugin loaded | Manual smoke only — Inspector per-row `HEX` button calls `\\.\pipe\AOBMakerCEBridge` |

---

## Reproduction commands

For copy-paste into a fresh shell when re-verifying any of the above:

```pwsh
# Static-only check (no game launch needed)
.\dist\metadata-cli.exe info  "<...>\<game>_Data\il2cpp_data\Metadata\global-metadata.dat"
.\dist\metadata-cli.exe stats "<...>\<game>_Data\il2cpp_data\Metadata\global-metadata.dat"

# Live verification — assumes the game is running
.\tools\Test-Handshake.ps1 -ProcessId <pid>
.\tools\Get-LogStats.ps1            # post-session log size sanity

# Steam-scan recall — produces the picker's input data
$libs = Get-Content "$env:ProgramFiles (x86)\Steam\steamapps\libraryfolders.vdf" -Raw
[regex]::Matches($libs, '"path"\s+"([^"]+)"') | %% { $_.Groups[1].Value -replace '\\\\','\\' }
```

---

## Regression live-smoke procedures

Step-by-step verification recipes for specific bug fixes, so a future session can
re-confirm the fix without re-deriving the repro. Convention (per
[group-scan-plan.md §7](group-scan-plan.md)): C++ DLL logic has **no unit-test
harness** → verify by `build.cmd dll` + a live-smoke recipe here; C# logic gets an
**xUnit** test *and* (where it has a UI surface) a live-smoke pass. Each recipe lists
the **failing symptom** (what the bug looked like) so the tester knows what a
regression would look like.

### 2026-07-01 leak/bug audit fixes

Baseline: last dedicated leak/bug review before this batch was `f621837`
(2026-06-02, build #990). The four fixes below landed in build #1235. See
[plan.md → Recent batches (2026-07-01)](plan.md) for the full write-up.

#### #1 (HIGH) — `refineGroupScan` stale scan-cancel flag wipes the session

- **What broke**: `ScanCancel::Scope`'s destructor cleared `g_scanActive` but **not**
  `g_scanCancel`. `refineGroupScan` ([ExportAPI.cpp](../dll/src/shared/ExportAPI.cpp))
  was the only handler that polled `IsCancelRequested()` without owning a `Scope`, so a
  cancel/disconnect on any *earlier* scan left the flag armed; the next **Next Scan**
  (refine) broke on the first iteration and `cands.resize(0)` destroyed the whole group
  session without testing anything. Fix removed the poll (mirrors the already-immune
  `refineValueScan`, which never polls).
- **Coverage**: DLL C++ → live-smoke only (no C++ unit harness). Not reproducible from
  xUnit.
- **Target**: any injectable Unity game (Mono **or** IL2CPP) with two known numeric
  values living at distinct fields of the same object (e.g. `Hp` + `MaxHp`).
- **Steps**:
  1. Inject the DLL; **Value Search** tab → flip **Group** toggle ON.
  2. Enter ≥2 slots with the known values → **First Scan**. Confirm the candidate grid
     is **non-empty** (note the count, e.g. `12`).
  3. Arm a stale cancel: start any other scan (a single-value "numeric all widths" scan,
     or a fresh Group First Scan) and click **⛔ Cancel** before it finishes — this
     leaves `g_scanCancel` set. (Equivalently: force-quit + relaunch a second UI client
     whose disconnect monitor arms the flag mid-scan.)
  4. Return to the still-open group session and click **Next Scan** (refine) with any
     per-slot predicate (e.g. Unchanged / Unchanged).
- **PASS (fixed)**: the refine re-tests every candidate and prunes only those failing
  the predicate; the count stays sane (e.g. `12 → 12` for Unchanged). DLL log line
  `refineGroupScan sess=<id> total=<N> removed=<M>` shows a real `total`, not `total=0
  removed=all`.
- **FAIL (regressed)**: the count collapses to **0** on the first Next Scan after a
  cancel, and the log shows `total=0 removed=<all>` even though nothing was re-tested.

#### #3 (MEDIUM) — Snapshot Group **Mode B** shows the stale snapshot-A value

- **What broke**: `SnapshotGroupEngine.BuildCandidate`
  ([SnapshotGroupEngine.cs](../ui/UnityDumpUI/ViewModels/SnapshotGroupEngine.cs)) sourced
  each slot's displayed `CurrentValue` from `fv.ValueDisplay`, and `fv` always comes from
  `Snapshots[0]` (snapshot A). In Mode B (2 snapshots) the grid therefore showed the
  **old** value even though matching (via `leaf.Num`) was correct. Fix reads
  `leaf.Display[last]` / `leaf.Num[last]` (`last = snapCount-1` → snapshot B).
- **Coverage**: C# → **xUnit** `SnapshotGroupEngineTests.ModeB_Representative_ShowsNewestSnapshotValue`
  (asserts the Decreased slot renders `80`, not `100`) + the UI live-smoke below.
  Run the unit test with `build.cmd test`.
- **Target**: any snapshot-capable game with a numeric field that changes between two
  captures (HP is ideal).
- **Steps**:
  1. **Snapshot** tab → **Capture** snapshot A.
  2. Change the value in-game (take damage so `Hp` drops, e.g. 100 → 80); **Capture**
     snapshot B.
  3. Flip **Group** toggle ON → pick **A + B** (Mode B) → add a **Decreased** slot for
     `Hp` (and optionally an **Unchanged** slot for `MaxHp`) → **Run**.
  4. Expand the matched object in the results grid and read the Decreased slot's
     **Current** value.
- **PASS (fixed)**: shows the snapshot-**B** (newest) value — `80`.
- **FAIL (regressed)**: shows the snapshot-**A** value — `100`.

> #2 (MEDIUM, `FieldReferencesViewModel` BulkGate subscription leak) and #4 (LOW,
> aggregate group-scan signed/unsigned width dedup) verify the same way: #2 by
> repeatedly opening/closing the "Find methods that touch this field…" modal and
> watching that private/WS memory returns to baseline (the VM + its Rows must be
> collectable after Close); #4 by a Value Search group scan in aggregate mode on an
> **unsigned** field (e.g. `UInt16`) whose value later crosses the sign boundary
> (e.g. 100 → 40000), then an `increased`/`decreased` Next Scan — the candidate must
> survive (pre-fix it was misread as negative and wrongly dropped).
