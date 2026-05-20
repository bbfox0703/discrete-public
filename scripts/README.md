# Discrete — Unity Dumper

> Inspect, scan, and modify Unity managed game state from Cheat Engine
> or the standalone `DiscreteUI.exe` GUI.

---

## Disclaimer

- **Educational / personal use only.** Use against online / multiplayer
  games likely violates their Terms of Service. Single-player offline
  titles only.
- **No warranty.** Use at your own risk. Memory modification can corrupt
  save data or crash the host process.
- **Kernel-mode anti-cheat unsupported.** EAC, BattlEye, Vanguard,
  Denuvo, etc. will detect or block this tool. Don't try.
- **Anti-virus may flag the proxy DLLs** (`version.dll` / `winmm.dll`).
  This is a generic Windows technique that's also commonly used by
  malware — false positives are normal. Whitelist the install folder
  if your AV objects.

---

## Who this is for

You'll get value out of this if you already know:

- **Unity managed code** — what an Assembly is, the Class / Field /
  Method hierarchy, the difference between value-types and
  reference-types in C#.
- **Cheat Engine table authoring** — you've written CT activate
  scripts, used Auto Assembler, created memory records.
- **Basic reverse engineering** — comfortable with hex addresses,
  pointer offsets, type marshaling.

You don't need C++ or .NET expertise — the tools handle that.

---

## System requirements

| Requirement | Notes |
|---|---|
| **Windows 10 or 11**, x64 | DiscreteUI.exe is 64-bit only (32-bit games are still supported via cross-bitness injection). |
| **Cheat Engine 7.5+** (7.6+ recommended) | Needed for `loadLibrary` / table-side scripts. |
| **Latest Visual C++ Redistributable (x64)** | Required by both `DiscreteUI.exe` and the 64-bit backend DLLs. The "Visual C++ Redistributable for Visual Studio 2015-2022" covers everything we ship — Microsoft maintains forward-compat in this family across MSVC 14.x. [x64 download](https://aka.ms/vs/17/release/vc_redist.x64.exe). |
| **Latest Visual C++ Redistributable (x86)** | Only required if you target 32-bit Unity Mono games. Same "2015-2022" redist covers x86 builds. [x86 download](https://aka.ms/vs/17/release/vc_redist.x86.exe). |
| **.NET runtime** | **Not needed separately.** `DiscreteUI.exe` is published as Native AOT and is fully self-contained. |
| **AOBMaker CE plugin** *(optional)* | Required only if you want one-click "Inject Discrete Bridge into CE Table" from the UI's Tools menu. |

Supported game runtimes:

| Backend | Architectures | Unity versions tested |
|---|---|---|
| Mono (bdwgc) | x64, x86 | 5.4 → 6.0+ |
| IL2CPP | x64 only (Unity 2019+ dropped x86 IL2CPP) | 2018.1 → 2022.3 → 6.1 |

---

## Features at a glance

| File | Provides |
|---|---|
| **`DiscreteUI.exe`** | GUI for everything. Seven tabs: Fields, Inspector, Methods, Symbol Finder, Address Lookup, Scene, Value Search. |
| **`UnityDumper-Mono.dll` / `UnityDumper-IL2CPP.dll`** | Injected backend. Speaks named-pipe JSON-RPC on `\\.\pipe\UnityDumpBfx`. |
| **`version.dll` / `winmm.dll`** *(proxy DLLs)* | Drop next to the game's `.exe` for pre-Unity-init injection. Auto-selects Mono vs IL2CPP backend. |
| **`unity_dump.lua`** | One-shot injector from CE Lua. |
| **`unity_dissect.lua`** | Populate CE's *Structure Dissect* directly from a managed class layout. |
| **`unity_symbols.lua`** | Register static fields + method addresses as CE symbols so CT memory records can reference them by name. |
| **`unity_addrfind.lua`** | Reverse lookup: any address → managed instance + class + field. |
| **`unity_invoke.lua`** | Call any Mono / IL2CPP method from CE Lua with typed args + return value + managed exception capture. |
| **`unity_scene.lua`** | Walk Unity's scene graph (GameObject hierarchy, components, transforms). |
| **`mono_registersymbol_helpers.lua`** | DLL-free Mono symbol register helpers (for CT authors who prefer pure-Lua). |
| **`inject-x86.exe`** + **`x86/`** subfolder | Cross-bitness inject helper. `DiscreteUI.exe` is x64-only, so when the target is a 32-bit Unity Mono game the UI shells out to `inject-x86.exe` (a tiny standalone 32-bit injector) to push the matching `x86/UnityDumper-Mono.dll` into the target. Ships next to `DiscreteUI.exe`; not run by the end user directly. |
| **`metadata-cli.exe`** | Offline `global-metadata.dat` parser (works on a game folder without launching the game). |

### What the UI's tabs do

- **Fields** — Browse Assemblies → Classes → declared fields with offsets, types, and static-field addresses.
- **Inspector** — Live walk of instance fields. Pointer drilldown into nested structures, value-write back, list-the-same-field-across-all-instances.
- **Methods** — Find every overload of a method by signature, invoke any of them with typed args (NumericUpDown / ComboBox-for-enums / AutoCompleteBox-for-bool editors), capture exceptions.
- **Symbol Finder** — Substring search across every class in one (or all) assemblies. Type `MaxHP`, get every field/method whose name contains it.
- **Address Lookup** — Reverse: paste an address, get instance + class + field.
- **Scene** — GameObject hierarchy tree with substring filter + cross-tab "inspect this GameObject".
- **Value Search** — CE-style First Scan + Next Scan, filtered to Unity managed instances. Every result row carries class + field context that CE alone cannot provide. Supports numeric (Int8–UInt64 / Float / Double) and string (UTF-16 / UTF-8 / ASCII, exact + contains).

---

## Deployment patterns

You have three choices for getting Discrete into the target game.
Pick one based on how your CT users will install your table.

### Mode A — Manual install (CE folder)

End user extracts the release zip into `<CE-install>/discrete/`.

```
<CE-install>/
├── cheatengine-x86_64.exe
└── discrete/
    ├── UnityDumper-Mono.dll       ← required for Mono targets
    ├── UnityDumper-IL2CPP.dll     ← required for IL2CPP targets
    ├── unity_dump.lua             ← required (injector)
    ├── utils.lua                  ← required
    ├── unity_dissect.lua          ← optional
    ├── unity_symbols.lua          ← optional
    ├── unity_invoke.lua           ← optional
    ├── unity_scene.lua            ← optional
    ├── unity_addrfind.lua         ← optional
    ├── mono_registersymbol_helpers.lua  ← optional
    └── x86/
        ├── UnityDumper-Mono.dll   ← required for 32-bit Mono games
        └── version.dll / winmm.dll ← required if shipping Mode C for 32-bit games
```

The CT's `[ENABLE]` script does:
```lua
dofile(getCheatEngineDir() .. 'discrete\\unity_dump.lua')
inject(getOpenedProcessID())
```

**Pro**: one install serves every Discrete-using CT.
**Con**: end user has to copy files once.

### Mode B — Embed everything in the `.CT` (zero install)

End user opens the `.CT` and it Just Works. The table contains all the
required files as embedded *Table Files* (Table menu → Add file…). The
`[ENABLE]` script extracts them to a temp directory and injects from
there.

Reference implementation: **`ce_table_bootstrap.lua`** in this folder.
Copy its `bootstrap()` function into your CT's autorun script, edit
`FILES_TO_EXTRACT` to list whichever files you embedded, then call
`bootstrap()` from your `[ENABLE]` block.

Tip: embed only the files you actually use. A minimum-functional CT
only needs `UnityDumper-{Mono,IL2CPP}.dll` + `utils.lua` +
`unity_dump.lua`. Adding the optional scripts is a few extra KB each.

**Pro**: end user does nothing — opens `.CT`, plays. No "download
these N files first" instructions.
**Con**: each `.CT` is ~1 MB larger (the backend DLL). Updating the
runtime requires re-publishing every CT that embeds it.

### Mode C — Proxy DLL (pre-Unity-init injection)

Drop `version.dll` or `winmm.dll` next to the game's `.exe`. Windows
loads it before Unity, and the proxy auto-loads the matching backend.

| Game target | Proxy from |
|---|---|
| 64-bit Unity (most modern games) | `version.dll` / `winmm.dll` in the package root |
| 32-bit Unity (older Mono games) | `x86/version.dll` / `x86/winmm.dll` |

The matching backend (`UnityDumper-Mono.dll` or `UnityDumper-IL2CPP.dll`
in the correct arch) must sit next to the proxy.

**Don't mix arches**: a 64-bit proxy with a 32-bit backend DLL silently
fails to load.

**Pro**: works on games that block runtime `LoadLibrary`. Loads early
enough to catch Unity's very-first-frame init events. Survives game
restart without re-running CT activate.
**Con**: anti-virus often flags generic proxy DLLs. End user must drop
the file into the game folder manually (some games are in `Program
Files` and require admin to write there).

### Files NOT to deploy

These ship in the package for developers but shouldn't be installed
on end-user machines:

| File | Why not |
|---|---|
| `DiscreteUI.exe` | Author / developer tool. Don't bundle with shipped CTs — CT users only need the DLL + scripts. |
| `metadata-cli.exe` | Offline IL2CPP metadata dump tool. Dev-only. |
| `inject-x86.exe` | Cross-bitness inject helper for DiscreteUI. Not needed if you use Mode A/B/C for distribution. |
| `*.pdb` | Debug symbols. |

---

## Quick start

After deploying (any mode above) and opening a Unity game in CE:

```lua
-- Open CE's Lua console (View → Lua Engine, or Ctrl+Alt+L), then:
dofile([[<CE>\discrete\unity_dump.lua]])
-- Output:
--   [UnityCEDumper] Backend: IL2CPP — DLL injected (build N)
--   [UnityCEDumper] Pipe ready: \\.\pipe\UnityDumpBfx

-- Reverse address lookup — paste any CE-discovered address:
dofile([[<CE>\discrete\unity_addrfind.lua]])
lookupAddress(0x000001E55B2518F0)
--   → instance=0x...  class=MainGame.PLY_WORK  field=chrNo
--     (System.Byte @ +0x10)

-- Or launch DiscreteUI.exe for the GUI.
```

For end users of a published `.CT`, the activate-script handles all of
this automatically (Mode A/B/C above).

---

## Debugging & bug reports

### Where logs live

All logs go under `%LOCALAPPDATA%\Discrete_UEDumper\Logs\`.

| Path | What's logged |
|---|---|
| `<pid>\init\current.log` | DLL load + module enumeration + version banner |
| `<pid>\pipe\current.log` | Pipe-server connection lifecycle |
| `<pid>\scan\current.log` | Memory scans (value scans, instance heap scans) |
| `<pid>\walk\current.log` | Class enumeration + address-finder cache builds |
| `<pid>\metadata\current.log` | IL2CPP metadata parser |
| `ui\*\current.log` | DiscreteUI app log (separate from per-game logs) |

Open the folder quickly:
```powershell
explorer "$env:LOCALAPPDATA\Discrete_UEDumper\Logs"
```

Each log auto-rotates at 8 MB; older entries land under
`current.log.1` / `current.log.2` etc.

### Running diagnostics from CE Lua

Once the DLL is injected, every `unity_*.lua` script can be re-run
from CE's Lua console to probe state. The scripts print to CE's
console — usually visible at the bottom of the Lua Engine window.

```lua
-- Verify pipe + backend detection:
dofile([[<CE>\discrete\unity_dump.lua]])
-- (the inject() call also prints the handshake)

-- Walk every class in an assembly (large output — pipe it):
dofile([[<CE>\discrete\unity_symbols.lua]])
registerClassSymbols("Assembly-CSharp", "MainGame", "PLY_WORK",
                     {verbose=true})
```

If a script hangs, CE's main thread may be busy on a backend RPC.
Wait — the script inserts `processMessages() + sleep(1)` between
RPCs so CE stays responsive even on a 300-method class.

### Running diagnostics from PowerShell

The release package ships PowerShell smoke harnesses under `tools/`.
They open `\\.\pipe\UnityDumpBfx` directly and send JSON-RPC frames.
Useful for reproducing bugs without launching DiscreteUI or CE.

```powershell
# Run from the dist/ folder (or anywhere — they use absolute paths).
.\tools\Test-Handshake.ps1                       # verify pipe + backend
.\tools\Test-WhatIsAt.ps1                        # reverse address lookup
.\tools\Test-ScanFieldValue.ps1 -Value 100       # value scan
.\tools\Test-ValueScanSession.ps1 -Value 100     # First / Next Scan workflow
.\tools\Test-CallMethod.ps1                      # invoke a managed method
```

Each script logs to stdout; pipe to a file if you want to attach the
output to a bug report:
```powershell
.\tools\Test-ScanFieldValue.ps1 -Value 100 > scan-bug.log
```

### What to include in a bug report

1. **Game** — name, Steam ID if applicable, version.
2. **Unity version** + **backend** (Mono / IL2CPP). The DLL prints
   both in `init\current.log` at the top:
   ```
   UnityDumper DLL attached build=NNN backend=Mono pid=...
   Detect: backend=Mono unity=2020.3.49f1 ...
   ```
3. **DLL build number** (`NNN` above).
4. **Reproduce steps** — exact Lua commands or DiscreteUI clicks.
5. **Relevant log entries** — at minimum the last ~50 lines from
   `init\current.log` plus the category log for the failing feature
   (e.g. `scan/current.log` for a Value Search issue, `walk/current.log`
   for an Address Lookup issue).
6. **Screenshot** of the UI if a visual / DataGrid problem.

> **Privacy note**: log files contain class / field names from your
> game. Trim to the relevant entries before sharing publicly if the
> game's class layout is sensitive.

---

## Known limitations

- **Anti-cheat**: kernel-mode AC (EAC, BattlEye, Vanguard, etc.) is
  unsupported and won't be. User-mode AC that hooks
  `OpenProcess`/`ReadProcessMemory` is bypassed (our DLL runs from
  inside the game process), but AC that scans for our DLL itself
  will still flag us.
- **Method invocation GC caveat**: a managed pointer returned by
  `callUnityMethod` (and friends) is valid only until the next GC
  cycle. Don't stash and reuse across long delays — chain follow-up
  RPCs immediately when correctness matters.
- **String search**: UTF-16 only for Unity-managed strings (System.String
  is always UTF-16). UTF-8 / ASCII modes are available for unmanaged
  engine strings or natively-stored text.
- **IL2CPP code stripping**: Unity's IL2CPP build strips "unused"
  managed code aggressively. Methods you can see in dnSpy may not
  exist in the runtime. Use Symbol Finder / `findMethodInstances` to
  verify before referencing a method by name.
- **32-bit IL2CPP**: not a thing in Unity 2019+ (Unity dropped x86
  IL2CPP targets). Only 32-bit Mono is supported on the x86 side.
