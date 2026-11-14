# discrete — Unity Cheat Engine Dumper

[![Backends](https://img.shields.io/badge/backends-Mono_x64%2Fx86_%2B_IL2CPP_x64-blue)](docs/test-matrix.md)
[![License](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)
[![Built with Claude Code](https://img.shields.io/badge/Built%20with-Claude%20Code-CC785C?logo=claude)](https://claude.ai/code)

Live Unity dumper and cheat-table generator: a C++ DLL injected into the game,
an Avalonia UI, and a Cheat Engine Lua bridge.

**Languages**: English (this file) · [繁體中文](README.zh-TW.md)

## Features

- **Class browser** — assembly → class → field / method, read live from the
  Mono runtime or from IL2CPP's `global-metadata.dat` (v24–v39, through
  Unity 6.1).
- **Instance inspector** — find live instances of a class, expand pointers and
  arrays inline, watch values refresh, and write fields back.
- **Address lookup** — paste an address (e.g. from a CE pointer scan) and get
  the managed object, class and field it belongs to.
- **Value search, snapshots and class pivot** — CE-style scans narrowed by
  class, snapshot diffs, and cross-instance field tables.
- **Methods** — list overloads with native addresses, generate AOB-based
  Auto-Assembler scripts, or call a managed method directly.
- **Export** — CE cheat-table XML, Structure Dissect files and C++ headers;
  live mode embeds real addresses.
- **CE Lua bridge** — the same pipe API from CE's Lua console, no UI needed.
- **Proxy DLLs** — `version.dll` / `winmm.dll` loaders for games where
  injection is awkward, with a bulk installer for your Steam library.
- **Offline metadata CLI** — `metadata-cli.exe` parses `global-metadata.dat`
  without running the game.

## Download

Prebuilt Windows builds are on the
[Releases](https://github.com/bbfox0703/discrete-public/releases) page:
self-contained, no .NET runtime needed. The binaries are unsigned, so
Windows SmartScreen or antivirus may warn on first run.

## Usage

**With the UI**

1. Start the Unity game.
2. Run `DiscreteUI.exe` → **Pick & inject…** → choose the game → **Inject & Connect**.
3. Browse assemblies and classes; right-click a class → **Inspect (find instances)…**.

**With Cheat Engine only**

1. Copy the `.lua` scripts and `Discrete-*.dll` into `<CE install>\discrete\`
   (`.\build.cmd deploy` does this after a build).
2. Attach CE to the game, open the Lua console (Ctrl+Alt+L) and run:

```lua
dofile([[C:\Program Files\Cheat Engine\discrete\unity_dump.lua]])     -- injects the DLL
dofile([[C:\Program Files\Cheat Engine\discrete\unity_invoke.lua]])
local r = callUnityMethod("Assembly-CSharp", "MainGame", "PLY_WORK",
                          "AddGrowHpMax", { argInt32(10) })
print(r.ok, r.returnValue)
```

See [USAGE.md](USAGE.md) for the CT-maker workflow and
[scripts/README.md](scripts/README.md) for the Lua API.

## Building from source

Requirements: Windows 10/11 x64, Visual Studio (C++ workload; CMake and Ninja
come with it), .NET SDK 10, Rust via [rustup](https://rustup.rs) (the pinned
toolchain and both targets install from `rust-toolchain.toml`), Python 3
(build-time checks), and Cheat Engine 7.5+ for the Lua bridge.

```powershell
.\sync_vendor.ps1            # fetch pinned C++ deps (nlohmann/json, MinHook)
.\build.cmd publish clean    # DLLs + Native AOT UI + tests + metadata-cli -> dist\
.\build.cmd /?               # all targets (ui, dll, mono, il2cpp, test, deploy, ...)
```

Always build through `build.cmd`; bare `cmake` / `dotnet` calls lack the VS
developer environment. x86 outputs go to `dist\x86\`.

## How it works

```
Cheat Engine (+ Lua bridge) ─┐
                             ├─ \\.\pipe\DiscreteBfx (JSON) ─► Discrete-Mono.dll / Discrete-IL2CPP.dll
DiscreteUI.exe ──────────────┘                                  injected into the Unity game,
                                                                reads Mono / IL2CPP runtime data
```

The UI never talks to the game directly; every Mono / IL2CPP difference lives
behind the pipe protocol. The backend DLLs are C++ with a Rust core linked in
(IL2CPP metadata parser, scan algorithms, x64 code decoding), which reads game
memory only through the C++ side's crash-safe reads.

## Verified games

SaGa F2R, BongoCat, Farthest Frontier, Hollow Knight: Silksong, Songs of
Silence and more, across both backends and every supported metadata version.
Full list: [docs/test-matrix.md](docs/test-matrix.md).

## Documentation

| | |
|---|---|
| [USAGE.md](USAGE.md) | End-user guide for cheat-table makers |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Build, tests, conventions, release procedure |
| [docs/plan.md](docs/plan.md) | Development state, backlog and phase history |
| [docs/Archiutecture.md](docs/Archiutecture.md) | Architecture rules and invariants |
| [docs/pipe-protocol.md](docs/pipe-protocol.md) | DLL ↔ UI wire contract |
| [docs/rust-core.md](docs/rust-core.md) | The Rust core inside the DLLs: FFI rules, build, regression gates |
| [docs/](docs/) | Everything else: backend internals, export formats, proxy DLLs, lessons learned |

## License

[GNU General Public License v3.0](LICENSE).

## Acknowledgements

Cheat Engine (Eric "Dark Byte" Heijnen and community) · Avalonia ·
CommunityToolkit.Mvvm · nlohmann/json (MIT) · MinHook (BSD-2-Clause) ·
iced-x86 (MIT) · Zydis (MIT; decoder reference for the oracle) · xunit.v3 · BepInEx, MelonLoader and Il2CppInterop for
`runtime_invoke` marshaling reference · upstream libil2cpp sources for layout
cross-checks.
