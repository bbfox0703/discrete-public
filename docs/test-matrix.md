# Test matrix

Phase 15. Compatibility ledger for the games + tooling versions UnityCEDumper has been exercised against. Add a row whenever you verify a new target end-to-end (handshake → walkInstance → at least one decoded value matches a manual CE read).

> Maintenance note: when you add a new row, also drop the raw `metadata-cli info` and `metadata-cli stats` JSON into the **Notes** column or its own subsection so future bisects can cross-check against the same blob without re-running the game.

---

## Unity targets (verified)

| Game | Unity | Backend | Metadata ver | Bitness | DLL build verified | Workflow | Notes |
|---|---|---|---|---|---|---|---|
| **SaGa Frontier 2 Remastered** | `2021.3.39f1` | IL2CPP | v29 | x64 | 101 (Phase 11), 108 (Phase 14), 110+ (Phase 12) | Pick & inject AND proxy install | **Primary verification target.** All phases 6–14 were validated against this game. `MainGame.PLY_WORK` decode round-trips against a manual CE Structure Dissect, including the `Vector3`/`reserv2[]` substruct expansion (Phase 11.5 follow-up). |
| **SaGa Frontier Remastered** | `2019.4.16f1` | IL2CPP | v24 | x64 | metadata-cli only (Phase 13) | Static metadata only | Type-tables NOT parsed (Phase 14c gate — only v29 wired up today). Prefix tables work: 15182 string literals readable via `metadata-cli info`. Live walkInstance has not been exercised; backend resolver may need offset adjustments for the older IL2CPP runtime. |
| **Hollow Knight Silksong** | `6000.0.50f1` | Mono | n/a | x64 | not yet exercised | n/a | Unity 6 era. MonoBleedingEdge present. The `unity_symbols.lua` Mono-method registration path (using `monoCompileMethod`) hasn't been validated against a Unity 6 bdwgc binary — would be a worthwhile next test. |

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

---

## Phase coverage gaps

These are the failure modes the test matrix doesn't cover *yet* — useful when scoping Phase 14c / 12d / future sub-phases.

| Gap | Need a target with… | Owning phase |
|---|---|---|
| IL2CPP metadata v27 (Unity 2018-19) | A Unity 2018 / 2019.x IL2CPP game | 14c |
| IL2CPP metadata v31 (Unity 2023+) | A Unity 2023.x IL2CPP game | 14c |
| 32-bit Mono (legacy Unity ≤ 2017) | A Unity 5.x / 2017.x x86 Mono game | 12d |
| Heavy obfuscator output | A Beebyte / Eazfuscator-built target | 14b banner — already wired, just needs a real test |
| `getMetadataStats` `partial_strip` verdict | Same as above (or a partial-strip game) | 14b |
| Stripped metadata via mismatched magic | An encrypted / packed `global-metadata.dat` | error-banner verification only |

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
