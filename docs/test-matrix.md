# Test matrix

Phase 15. Compatibility ledger for the games + tooling versions UnityCEDumper has been exercised against. Add a row whenever you verify a new target end-to-end (handshake → walkInstance → at least one decoded value matches a manual CE read).

> Maintenance note: when you add a new row, also drop the raw `metadata-cli info` and `metadata-cli stats` JSON into the **Notes** column or its own subsection so future bisects can cross-check against the same blob without re-running the game.

---

## Unity targets (verified)

| Game | Unity | Backend | Metadata ver | Bitness | DLL build verified | Workflow | Notes |
|---|---|---|---|---|---|---|---|
| **SaGa Frontier 2 Remastered** | `2021.3.39f1` | IL2CPP | v29 | x64 | 101 (Phase 11), 108 (Phase 14), 110+ (Phase 12), 150 (Phase 14c), 188+ (Phase 17), 201+ (Phase 18), 209+ (Phase 19a/c/e) | Pick & inject AND proxy install | **Primary verification target.** All phases 6–14 + 17 + 18 + 19 validated here. `MainGame.PLY_WORK` decode round-trips against a manual CE Structure Dissect (Phase 11.5 incl. `Vector3`/`reserv2[]` substruct expansion). Phase 17 `whatIsAt` round-trips against `PLY_WORK.HP` field address. **Phase 18** AOB pattern generation verified against `MainGame/PLY_WORK.AddGrowHpMax(int)` — single-overload, 32-byte AOB with 0 wildcards (simple `MOVSS [rcx],xmm0` prologue), suffix `int`, "Copy AOB AA script" writes a complete CE Auto-Assembler script to clipboard. **Phase 19a/c/e** verified end-to-end: `Mathf.Abs(int:-7) → 7`, `Mathf.Abs(float:-3.5) → 3.5`, `Convert.ToByte(int:-1) → System.OverflowException`, `String.Concat("Hello, ", "World") → "Hello, World"`, `Int32.Parse("42") → 42` — across PowerShell smoke (`Test-CallMethod.ps1`) AND the CE Lua bridge (`unity_invoke.lua` from CE 7.5.x console). `metadata-cli info`: 10001 type defs, 11392 string literals. |
| **SaGa Frontier Remastered** | `2019.4.16f1` | IL2CPP | **v24** | x64 | 150 (Phase 14c) | Static metadata only (`metadata-cli`) | Phase 14c verification target: 7,514 type defs parse cleanly, canonical leading types (`<Module>`, `<>f__AnonymousType0\`1`, `Locale`, `SR`, `Mono.Runtime`) with correct IL tokens. Live walkInstance not yet exercised against this older IL2CPP runtime. |
| **FINAL FANTASY VI PR** | `2019.4.26f1` | IL2CPP | **v24** | x64 | 150 (Phase 14c) | Static metadata only | Second v24 fixture — confirms layout works across Unity 2019.4 sub-versions. 11,438 type defs, 11,537 string literals. |
| **Townscaper** | `2020.3.4f1` | IL2CPP | **v27** ✨ | **x86** | 150 (Phase 14c) | Static metadata only | Phase 14c v27 target — only 32-bit IL2CPP build in the corpus. Its `global-metadata.dat` parses identically to v29 (88-byte stride, IL token at +84). 8,132 type defs match canonical leading types — confirming the metadata format is bitness-neutral even when the game runtime is x86. Live runtime support deferred to Phase 12d. |
| **Farthest Frontier** | `2022.3.62f1` | **IL2CPP + Mono** ✨ | **v31** (IL2CPP path) | x64 | 150 (Phase 14c), 213+ (Phase 19c) | Static metadata + live `callMethod` (both backends) | Phase 14c v31 target #1: 15,848 type defs, 20,788 string literals. Layout empirically verified to match v29 exactly (same 88-byte stride). **Phase 19c instance dispatch verified live on the Mono path**: `ItemBundle.get_numberOfItems()` (no-arg instance method, returns UInt32) called against a live instance address returned `1`. Same game's IL2CPP build also surfaced 2 overloads of `ItemBundle.AddItems` via `findMethodInstances` — `(System.UInt32, System.UInt32)` + `(ItemBundle)` — confirming wire-shape parity across the two backends. Game has both Mono and IL2CPP launch options, useful for cross-backend regression. |
| **Emberward** | `2022.3.62f2` | IL2CPP | **v31** | x64 | 150 (Phase 14c) | Static metadata only | Phase 14c v31 target #2: 23,982 type defs, 23,828 string literals. Largest type table in the corpus. |
| **Hollow Knight Silksong** | `6000.0.50f1` | Mono | n/a | x64 | 161+ (Phase 16-prep session) | Pick & inject | **Mono bdwgc verification target.** `PlayerData` (1350 fields, 3-level inheritance chain) walks end-to-end. Validates: (a) Mono walkInstance against Unity 6 bdwgc; (b) server-side enum detection (`mapZone : GlobalEnums.MapZone` decodes via `class_is_enum` + `class_enum_basetype` to its underlying `System.Int32`, returns `value: 6` instead of the catch-all 8-byte ptr); (c) sort-by-offset, instance-switch selection preservation, refilter perf at scale. **Still un-exercised**: `unity_symbols.lua` Mono `monoCompileMethod` registration path — last piece needed to fully close out "Mono bdwgc end-to-end" coverage. |

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
| 32-bit Mono (legacy Unity ≤ 2017) | A Unity 5.x / 2017.x x86 Mono game | 12d |
| Heavy obfuscator output | A Beebyte / Eazfuscator-built target | 14b banner — already wired, just needs a real test |
| `getMetadataStats` `partial_strip` verdict | Same as above (or a partial-strip game) | 14b |
| Stripped metadata via mismatched magic | An encrypted / packed `global-metadata.dat` | error-banner verification only |
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
