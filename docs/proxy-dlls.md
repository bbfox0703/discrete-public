# Proxy DLLs (`version.dll` / `winmm.dll`) — Phase 12

> Side-by-side proxy DLLs that load the dumper backend automatically
> when the game starts, without an external injector. Drop the proxy
> next to the game's `.exe` and `Discrete-{Mono,IL2CPP}.dll` rides
> along.

---

## Why a proxy

Direct injection (`CreateRemoteThread` from `DiscreteUI.exe` or
`Install-CEScripts.ps1`) works fine but:

- The game must already be running.
- Some games crash when injected after their anti-cheat / engine
  initialization completes — the dumper has to be loaded *before*
  the engine spins up.
- Injection requires admin (or `SeDebugPrivilege`); a proxy DLL
  doesn't.
- A proxy can be set up once and forgotten — every game launch
  picks up the dumper automatically.

Proxy DLLs solve all four. The Unity .exe statically imports
`version.dll` (used for `GetFileVersionInfo*`) and many also import
`winmm.dll` (used for `timeGetTime`). The OS loader resolves these
imports at process start; if a same-named DLL exists next to the
game's .exe, that one wins over `C:\Windows\System32\version.dll`.

So: ship a same-named DLL that **forwards every export to the real
system DLL** + **also loads the dumper backend on a worker thread**.
Game runs as if nothing changed; backend gets loaded for free.

## Status (2026-05-07)

| Variant | Phase | Status |
|---|---|---|
| `version.dll` | 12a | ✅ Shipped |
| `winmm.dll`   | 12b | ✅ Shipped |
| `dxgi.dll`    | 12  | ❌ Not yet shipped (planned but not started — `version`/`winmm` cover the test matrix) |
| `ProxyDeployWindow` (UI) | 12c | ✅ Shipped — bulk install panel, scans Steam library |
| 32-bit support | 12d | ⏳ Deferred — engineering only, no external blocker |

The 32-bit gate covers everything: x86 build of `loader.cpp` + each
proxy variant + each backend DLL, x86 path in `WindowsProxyInstaller`,
x86/x64 selection in the install flyout. No 32-bit Unity game in the
test matrix has surfaced yet, so the work is on the back burner.

## Architecture

```
Unity-game.exe
   └─ implicit import: version.dll
            └─ OS loader: load <game-dir>\version.dll
                                       │
                            ┌──────────┴───────────────┐
                            │  PROXY version.dll       │
                            │  (our shipping DLL)      │
                            │                          │
                            │  DllMain (DLL_PROCESS_   │
                            │   ATTACH):               │
                            │  1. EnsureReal() — open  │
                            │     System32\version.dll │
                            │     once, cache HMODULE  │
                            │  2. Spawn worker thread: │
                            │     • Detect Mono/IL2CPP │
                            │     • LoadLibrary the    │
                            │       sibling backend    │
                            │       DLL                │
                            │  3. Return TRUE          │
                            └──────────┬───────────────┘
                                       │
                       game's calls to GetFileVersionInfo* etc.
                                       │
                                       ▼
                            ┌──────────────────────────┐
                            │  Per-export thunks:      │
                            │  static fn cache +       │
                            │  GetProcAddress on       │
                            │  first call.             │
                            └──────────┬───────────────┘
                                       │
                                       ▼
                            ┌──────────────────────────┐
                            │  Real System32\          │
                            │  version.dll             │
                            └──────────────────────────┘
```

### Three pieces of the proxy

1. **Real-DLL forwarder**. Every export the system DLL ships is
   declared in our `.def` file (`version.def` / `winmm.def`) so the
   linker emits a same-named export. The implementation (in
   `version.cpp` / `winmm.cpp`) is a `PROXY_THUNK(ret, conv, name, ...)`
   macro that on first call resolves the real DLL's same-named export
   via `GetProcAddress` and caches it in a static. Hot path is one
   indirect branch.
   - **C2375 sidestep**: Windows headers declare every `version.dll`
     export with `__declspec(dllimport)`, which clashes with our
     `__declspec(dllexport)`. We name the C functions `Proxy_<symbol>`
     and rename them to the public `<symbol>` via the `.def` file's
     `name = Proxy_name` aliasing. Same approach BepInEx-style proxies
     use. See [version.def](../dll/src/proxy/version/version.def).

2. **Backend loader**. `Proxy_StartBackendLoader(HMODULE proxySelf)`
   spawns a worker thread that:
   - Polls `BackendDetector::Detect()` for up to 5 seconds (Unity
     engine DLLs may not be loaded yet at our `DllMain` time).
   - Once Mono or IL2CPP is detected, `LoadLibraryW`s the matching
     `Discrete-{Mono,IL2CPP}.dll` from the proxy's own directory.
   - The backend DLL's `DllMain` runs `Init()` + `StartPipe()` as
     usual (same as direct injection).
   - On failure (Unknown backend after 5 s, sibling DLL missing,
     `LoadLibrary` fails), the proxy stays silent — the game keeps
     running normally without the dumper.
   - **Worker thread** is mandatory: `DllMain` holds the loader lock,
     so calling `LoadLibrary` directly there for a DLL that has its
     own `DllMain` would deadlock.

3. **Real-DLL HMODULE cache**. `Proxy_LoadRealDll(name)` caches up to
   4 system-DLL handles in a `SRWLOCK`-guarded array. The cache
   handles never get `FreeLibrary`'d — the proxy holds the references
   for the lifetime of the host process so per-export thunks stay
   valid.

See [loader.h](../dll/src/proxy/loader.h) /
[loader.cpp](../dll/src/proxy/loader.cpp) for the contract.

## Pick which proxy

| Variant | When to use |
|---|---|
| **`version.dll`** | Default — every Unity .exe imports it. First choice unless something else has already claimed `<game-dir>\version.dll`. |
| **`winmm.dll`** | Fallback — when another tool (RTSS, ReShade injector, MelonLoader's loader) has already deployed `version.dll`. Most Unity .exes also import `winmm.dll`. |
| **`dxgi.dll`** | Not shipped yet. Would cover D3D11/12 games where neither `version` nor `winmm` is imported. No game in the test matrix needs it yet. |

When both `version` and `winmm` are unavailable, the user falls back
to direct injection via `DiscreteUI.exe` Pick & Inject or
`tools/Install-CEScripts.ps1`.

## ProxyDeployWindow (UI)

Phase 12c shipped a dedicated **Proxy Deploy** window — open via
"Proxy Deploy…" in the connection bar.

### Layout

```
┌─ Proxy DLL Deploy ──────────────────────────────────────────────┐
│ [filter…] [Show 32-bit]  [↻ Rescan]                             │
│                                                                 │
│ When installing: ⦿ version  ○ winmm                             │
│ [Select all] [Unselect all] [Invert]                            │
│                                                                 │
│ ┌─────────────────────────────────────────────────────────────┐ │
│ │ ☐ Game            Backend  Bits  Proxy  Status   ...        │ │
│ │ ☑ SaGa F2R        IL2CPP   x64   —      Available           │ │
│ │ ☐ BongoCat        Mono     x64   ver    Deployed (build #5) │ │
│ │ ☑ Farthest Front. IL2CPP   x64   —      Available           │ │
│ └─────────────────────────────────────────────────────────────┘ │
│                                                                 │
│  3 selected · 12 deployed · 18 total                            │
│  Last: Installed in 3 of 3 games                                │
│                                                                 │
│ [Install selected] [Uninstall selected] [Update all deployed]   │
└─────────────────────────────────────────────────────────────────┘
```

### Bulk semantics

| Action | Behavior |
|---|---|
| **Install selected** | Install the **default kind** (radio choice) into every selected & supported row. If a row already has the OTHER kind, the installer's mutex install swaps it (handled by `WindowsProxyInstaller`). |
| **Uninstall selected** | Remove every proxy + backend file from each selected row, regardless of which kind is currently in place. |
| **Update all deployed** | Re-install with the row's CURRENT kind (preserving the user's per-game choice) for every row that already has a proxy. Selection ignored. Useful after rebuilding `DiscreteUI.exe` to push fresh DLLs in one click. |

### Auto-detection

The UI scans the user's Steam library (parsed from
`<Steam>\config\libraryfolders.vdf`) plus any registered Unity games
the OS knows about. Each game row reports:

- **Backend** — Mono / IL2CPP / Unknown (heuristic — looks for
  `mono-2.0-bdwgc.dll` / `mono.dll` / `GameAssembly.dll` next to the
  .exe).
- **Bits** — x64 / x86 / Unknown (PE header check).
- **Proxy** — `—` (none), `ver` (version.dll deployed), or `winmm`.
- **Status** — `Available`, `Deployed (build #N)`, `Mismatch (file
  missing)`, `Unsupported (32-bit)`, etc.

32-bit / Unsupported rows are hidden by default; toggle "Show 32-bit"
to surface them. Bulk operations skip them.

## Manual deploy (no UI)

```powershell
# Copy proxy + backend into the game directory:
$game = 'C:\Path\To\Game-Folder'
Copy-Item .\dist\version.dll                $game\
Copy-Item .\dist\Discrete-IL2CPP.dll     $game\   # or -Mono.dll
```

The proxy auto-detects which backend to load — copying both DLLs is
fine if you don't know yet (the proxy only loads the one matching the
detected backend).

To uninstall: delete `version.dll` + `Discrete-*.dll` from the
game directory.

## Caveats

- **Anti-cheat**: any anti-cheat that integrity-checks `version.dll`
  (or whichever proxy variant) will detect the swap. This project is
  for offline / single-player games — don't use it on multiplayer
  games with anti-cheat.
- **Updates that overwrite the game directory** (Steam/EGS auto-update,
  manual reinstall) will remove the proxy. Re-deploy after updates.
- **Conflicts with other proxy-DLL mods** (RTSS, ReShade injector,
  MelonLoader). If `version.dll` is already taken, fall back to
  `winmm.dll`. If both are taken, use direct injection instead.
- **32-bit games**: not supported yet. The UI grays out 32-bit rows
  with `Unsupported (32-bit)` status and skips them in bulk ops.

## Source map

| File | What |
|---|---|
| [dll/src/proxy/loader.h](../dll/src/proxy/loader.h) | Public contract: `Proxy_LoadRealDll`, `Proxy_GetRealExport`, `Proxy_StartBackendLoader` |
| [dll/src/proxy/loader.cpp](../dll/src/proxy/loader.cpp) | Implementation: HMODULE cache, system32 path resolution, worker-thread backend loader |
| [dll/src/proxy/version/version.cpp](../dll/src/proxy/version/version.cpp) | Per-export `PROXY_THUNK` macros for `version.dll`'s 30+ exports |
| [dll/src/proxy/version/version.def](../dll/src/proxy/version/version.def) | Export aliasing (Proxy_name → name) |
| [dll/src/proxy/winmm/winmm.cpp](../dll/src/proxy/winmm/winmm.cpp) | Per-export thunks for `winmm.dll`'s 100+ exports |
| [dll/src/proxy/winmm/winmm.def](../dll/src/proxy/winmm/winmm.def) | Export aliasing |
| [ui/UnityDumpUI/ViewModels/ProxyDeployViewModel.cs](../ui/UnityDumpUI/ViewModels/ProxyDeployViewModel.cs) | UI VM — scan / install / uninstall / update commands |
| [ui/UnityDumpUI/Views/ProxyDeployWindow.axaml](../ui/UnityDumpUI/Views/ProxyDeployWindow.axaml) | Bulk install panel |
| `ui/UnityDumpUI.Core/Platform/WindowsProxyInstaller.cs` | Cross-process file ops + mutex install (swap if other kind present) |
| `ui/UnityDumpUI.Core/Platform/UnityGameScanner.cs` | Steam library + Registry scan + per-game backend / bitness detection |
