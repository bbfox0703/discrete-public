# discrete — Unity Cheat Engine Dumper

[![Phase 30](https://img.shields.io/badge/phase-30_(memory_%26_speed)_shipped-success)](docs/plan.md)
[![Backends](https://img.shields.io/badge/backends-Mono_x64_%2B_IL2CPP_x64-blue)](docs/test-matrix.md)
[![AOT](https://img.shields.io/badge/UI-Avalonia_12_AOT_(0%2F0_warnings)-purple)]()
[![License](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

> Live Unity dumper + cheat-table generator + Lua-callable managed methods.
> C++ DLL backend + Avalonia 12 AOT UI + Cheat Engine Lua bridge.
>
> **Languages**: English (this file) · [繁體中文](README.zh-TW.md)

---

## What it does

Inject a single backend DLL into a running Unity game and get:

- **Live class browser** — every assembly → class → field/method,
  decoded from Mono's runtime tables or IL2CPP's `global-metadata.dat`
  (versions v24/v27/v29/v31). No game-source needed.
- **Live instance walker** — find every live instance of a class on
  the heap, expand pointer / array fields inline, watch values
  refresh at 250 ms / 1 s.
- **Reverse address lookup** (`whatIsAt`) — paste any address from
  CE's pointer-scan and identify the managed instance + class +
  declared field it belongs to. The inverse of `findInstances`.
- **Method overload picker** (`findMethodInstances`) — locate every
  overload of a named method, get its native address (Mono JIT or
  IL2CPP AOT pointer) + signature.
- **AOB AA-script generator** — given an overload, produce a
  Cheat Engine Auto-Assembler script with version-stable AOB pattern
  + `registersymbol` block. Multi-overload methods get one symbol per
  signature so the same script handles all of them.
- **AOBMaker direct-push** — when the AOBMaker CE plugin is loaded,
  the UI can push the generated AA script straight into CE's address
  list as a memory record (skipping the clipboard round-trip).
- **`callMethod` RPC** (Phase 19) — invoke any managed method (Mono
  JIT or IL2CPP AOT entry) with marshaled primitive / string / null /
  address args + typed return values + managed-exception capture.
  Available three ways:
  1. **DLL pipe RPC** — `\\.\pipe\DiscreteBfx`, JSON over named pipe.
  2. **UI Method Call dialog** — right-click any overload row in the
     Method Overloads window → "Call method…".
  3. **CE Lua bridge** — `dofile [...]\discrete\unity_invoke.lua`
     then `callUnityMethod(asm, ns, class, method, args, opts)`.
- **CE XML / CSX / SDK header export** — current class as a Cheat
  Table structure, a Structure Dissect entry, or a C++ header.
  Live mode embeds real instance addresses so the .CT file works
  out-of-the-box.
- **Proxy DLL deploy** — `version.dll` / `winmm.dll` / `dxgi.dll`
  side-by-side installer for games where direct injection is
  awkward. Bulk-install panel scans your Steam library and deploys
  to selected games.
- **Offline metadata CLI** — `metadata-cli.exe` parses
  `global-metadata.dat` standalone (no game required) for
  reverse-engineering scratch work.

See [docs/plan.md](docs/plan.md) for the canonical phase ledger plus
the centralized **Open Backlog / TODO** — start there.
[docs/STATUS.md](docs/STATUS.md) is a one-line status pointer, and
[docs/archive/](docs/archive/) holds the long-form write-ups for
shipped phases.

## Architecture

```
                  ┌─────────────────────────┐
                  │ Cheat Engine + AOBMaker │
                  └──────────┬──────────────┘
                             │  \\.\pipe\AOBMakerCEBridge
                             ▼
                  ┌─────────────────────────┐
                  │  DiscreteUI.exe (AOT)   │  Avalonia 12, single-file
                  │  • Assembly browser     │  Win-only (Skia + HarfBuzz)
                  │  • Live inspector       │
                  │  • Address Lookup tab   │
                  │  • Method Overloads     │
                  │  • Method Call dialog   │
                  │  • Proxy Deploy panel   │
                  └──────────┬──────────────┘
                             │  \\.\pipe\DiscreteBfx (JSON over pipe)
                             ▼
                  ┌─────────────────────────┐
                  │  Discrete-Mono.dll   │  ← injected into Unity x64 game
                  │  / -IL2CPP.dll          │
                  │                         │
                  │  • backend detector     │
                  │  • domain / class walk  │
                  │  • method invoker       │  ← Phase 19 (callMethod)
                  │  • AOB-pattern decoder  │  ← Phase 18 (hand-rolled x64 LDE)
                  │  • address finder       │  ← Phase 17 (whatIsAt)
                  └─────────────────────────┘
                             ▲
                             │ runtime / metadata APIs
                             │ (mono_class_get_methods,
                             │  il2cpp_class_get_field_at_index, ...)
                  ┌─────────────────────────┐
                  │   Unity game (target)   │
                  │  Mono.dll / GameAsm.dll │
                  └─────────────────────────┘
```

CE Lua bridge scripts (`unity_dump.lua`, `unity_dissect.lua`,
`unity_symbols.lua`, `unity_addrfind.lua`, `unity_invoke.lua`) talk
to the same `\\.\pipe\DiscreteBfx` from CE's Lua console — no UI
required for headless workflows.

## Quick start

### Requirements

- **Windows 10 / 11** (x64). Linux / macOS not supported.
- **.NET SDK 10** for building the UI / tests.
- **Visual Studio 2022 Build Tools** (or the C++ workload of full VS)
  for the DLL.
- **CMake 3.20+**, **Ninja** (auto-resolved by the build script if
  you have VS).
- **Cheat Engine 7.5+** for the Lua bridge.

### Build

```powershell
# Full release publish (DLL + UI AOT + tests + metadata-cli) → dist/
.\build.cmd publish clean

# UI only (fast iteration; self-contained single-file)
.\build.cmd ui

# IL2CPP DLL only / Mono DLL only
.\build.cmd il2cpp
.\build.cmd mono

# xUnit v3 tests
.\build.cmd test
```

Outputs land in `dist/`:

```
DiscreteUI.exe                    38 MB (Native AOT, single-file)
Discrete-Mono.dll              ~545 KB
Discrete-IL2CPP.dll            ~570 KB
metadata-cli.exe                  ~70 KB
version.dll / winmm.dll           ~130 KB each (proxy DLLs)
unity_*.lua / utils.lua           CE Lua bridge scripts
libSkiaSharp.dll / ...            Avalonia native dependencies
```

### Use

**With the UI**:

```
1. Launch the target Unity game.
2. Run dist\DiscreteUI.exe.
3. Click "Pick & inject…", filter by name or PID, click Inject & Connect.
4. Browse assemblies → classes; right-click a class → "Inspect (find instances)…".
5. Right-click a method → "Call method… (pick overload)" to invoke it.
```

**With CE only** (no UI):

```
1. Launch the target Unity game.
2. Open Cheat Engine → File → Open Process → pick the game.
3. Open Lua console (Ctrl+Alt+L), run:
   dofile([[C:\Program Files\Cheat Engine\discrete\unity_dump.lua]])
4. The DLL gets injected and starts the pipe server. Then:
   dofile([[...\discrete\unity_invoke.lua]])
   local r = callUnityMethod("Assembly-CSharp", "MainGame", "PLY_WORK",
                             "AddGrowHpMax", { argInt32(10) })
   print(r.ok, r.returnValue)
```

Bridge script install: `.\build.cmd deploy` after a publish, or
manually copy `dist\unity_*.lua` + `dist\Discrete-*.dll` to
`<CE-install>\discrete\`. See [scripts/README.md](scripts/README.md)
for layout details.

## Verified targets

Seven games across both backends + every metadata version we ship —
see [docs/test-matrix.md](docs/test-matrix.md) for the full list.
SaGa F2R (IL2CPP v29), BongoCat (Mono bdwgc), Farthest Frontier
(IL2CPP v31), Hollow Knight: Silksong (IL2CPP v31), and more.

## Documentation map

| File | What it covers |
|---|---|
| [plan.md](docs/plan.md) | **Canonical phase ledger (0 → 30) + centralized Open Backlog / TODO** — start here |
| [STATUS.md](docs/STATUS.md) | One-line status pointer (current build / phase → plan.md) |
| [archive/](docs/archive/) | Long-form write-ups for shipped phases + old session hand-offs |
| [Archiutecture.md](docs/Archiutecture.md) | Rules and invariants (rule of one backend, AOT mandate, single-instance, async everywhere…) |
| [pipe-protocol.md](docs/pipe-protocol.md) | DLL ↔ UI wire contract (every command, every shape) |
| [test-matrix.md](docs/test-matrix.md) | Verified games + tooling versions |
| [address-finder.md](docs/address-finder.md) | Phase 17 design — `whatIsAt` reverse lookup spec |
| [lua-invoke.md](docs/lua-invoke.md) | Phase 19 design — `callMethod` spec, marshaling rules, error codes |
| [il2cpp-metadata-crosscheck.md](docs/il2cpp-metadata-crosscheck.md) | Audit of our IL2CPP layouts vs upstream Unity sources |
| [lessons-learned.md](docs/lessons-learned.md) | Engineering gotchas that took a session to figure out |
| [proxy-dlls.md](docs/proxy-dlls.md) | `version.dll` / `winmm.dll` / `dxgi.dll` proxy + ProxyDeployWindow |
| [metadata-cli.md](docs/metadata-cli.md) | `metadata-cli.exe` standalone tool |
| [export-formats.md](docs/export-formats.md) | CE XML / CSX / SDK header schemas |
| [mono-internals.md](docs/mono-internals.md) | Mono backend deep dive |
| [il2cpp-internals.md](docs/il2cpp-internals.md) | IL2CPP backend deep dive |
| [dll-spec.md](docs/dll-spec.md) | DLL ABI + module map + lifecycle |
| [ui-spec.md](docs/ui-spec.md) | UI architecture (ViewModels, windows, bridges) |
| [technical-notes.md](docs/technical-notes.md) | Catch-all for engineering notes |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Build / test / code conventions / PR style |

## License

Licensed under the **GNU General Public License v3.0** (GPL-3.0).
See [LICENSE](LICENSE) for the full text.

## Acknowledgements

- **Cheat Engine** — Eric "Dark Byte" Heijnen + the wider CE community.
- **Avalonia 12** — UI framework (Native AOT story).
- **CommunityToolkit.Mvvm** — source-generator MVVM (no reflection).
- **nlohmann/json** — DLL-side JSON.
- **xunit.v3** — UI test runner.
- **BepInEx / MelonLoader / Il2CppInterop** projects — non-uniform
  `runtime_invoke` marshaling convention reference (see
  [lessons-learned.md](docs/lessons-learned.md)).
- The four upstream Unity metadata-version source files in
  `vendor/libil2cpp/` — kept side-by-side for offset cross-checking
  (see [il2cpp-metadata-crosscheck.md](docs/il2cpp-metadata-crosscheck.md)).
