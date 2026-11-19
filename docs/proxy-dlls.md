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

## Status (2026-08-27)

| Variant | Phase | Status |
|---|---|---|
| `version.dll` | 12a | ✅ Shipped |
| `winmm.dll`   | 12b | ✅ Shipped |
| `dxgi.dll`    | 12  | ❌ Not yet shipped (planned but not started — `version`/`winmm` cover the test matrix). ⚠ See [§Pick which proxy](#pick-which-proxy) before starting one |
| Pre-CRT gate  | 12f | ✅ Shipped (2026-08-27) — see [§Pre-CRT](#pre-crt--an-export-can-be-called-before-our-crt-exists) |
| `ProxyDeployWindow` (UI) | 12c | ✅ Shipped — bulk install panel, scans Steam library |
| 32-bit **Mono** support | 12e | ✅ Shipped (2026-06-04, UI/Core-only) — x86 build already produced dist/x86/ (proxy + Mono backend); installer now auto-picks it |
| 32-bit **IL2CPP** support | — | ❌ N/A — no 32-bit IL2CPP backend exists (no shipping IL2CPP target is 32-bit) |

The x86 build (`build.ps1 -Arch x86`) had long produced `dist/x86/`
(`version.dll` + `winmm.dll` + `Discrete-Mono.dll` + `inject-x86.exe`),
and direct injection on x86 Mono was already live-verified (Phase 12d).
The only gap was Proxy Deploy's UI/installer gating, closed in **12e**:
- `UnityGameEntry.IsSupported` now allows **x86 Mono** (IL2CPP x86 stays
  unsupported — there is no 32-bit IL2CPP backend).
- `WindowsProxyInstaller` reads the target .exe's PE machine field and
  sources from `dist/x86/` for x86 targets, copying only the x86 proxy +
  `Discrete-Mono.dll` (no IL2CPP). The source-version comparison reads the
  matching `dist/x86/` proxy so x86 rows don't falsely report "outdated".
- No C++ change — the proxy `loader.cpp` only ever detects Mono on an x86
  game, so it loads `Discrete-Mono.dll` without an IL2CPP branch.

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
                            │  1. MarkCrtReady() —     │
                            │     first statement      │
                            │  2. Spawn worker thread: │
                            │     • Detect Mono/IL2CPP │
                            │     • LoadLibrary the    │
                            │       sibling backend    │
                            │       DLL                │
                            │  3. Return TRUE          │
                            │                          │
                            │  NOTHING is resolved     │
                            │  here — see §Pre-CRT     │
                            └──────────┬───────────────┘
                                       │
                       game's calls to GetFileVersionInfo* etc.
                                       │
                                       ▼
                            ┌──────────────────────────┐
                            │  Per-export thunks:      │
                            │  pre-CRT gate, then      │
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
   `version.cpp` / `winmm.cpp`) is a
   `PROXY_THUNK(ret, conv, name, params, args, failval)` macro that on
   first call resolves the real DLL's same-named export via
   `GetProcAddress` and caches it in a static. Hot path is one
   indirect branch.
   - **Resolution is lazy, never eager.** `DllMain` deliberately does
     not resolve anything: that would `LoadLibraryW` under the loader
     lock while the host .exe's own static imports are still
     initialising. Each thunk resolves on its first call instead,
     which happens on a game thread after `DllMain` has returned.
   - **`failval`** is the export's own documented failure value,
     returned when the pre-CRT gate refuses. It is per-export on
     purpose — see [§Pre-CRT](#pre-crt--an-export-can-be-called-before-our-crt-exists).
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
   - **`CreateThread` itself failing is the one exception**, and it
     does log: this thread is the *only* thing the proxy does, so a
     silent failure here makes every downstream symptom ("installed
     but nothing attaches") point at the wrong place.
   - **Worker thread** is mandatory: `DllMain` holds the loader lock,
     so calling `LoadLibrary` directly there for a DLL that has its
     own `DllMain` would deadlock.
   - It is also where the pre-CRT counter is reported, because that is
     the first moment we are off the loader lock.

3. **Real-DLL HMODULE cache**. `Proxy_LoadRealDll(name)` caches up to
   4 system-DLL handles in a `SRWLOCK`-guarded array. The cache
   handles never get `FreeLibrary`'d — the proxy holds the references
   for the lifetime of the host process so per-export thunks stay
   valid.

See [loader.h](../dll/src/proxy/loader.h) /
[loader.cpp](../dll/src/proxy/loader.cpp) for the contract.

## Pre-CRT — an export can be called before our CRT exists

> Full write-up, evidence and the `dxgi.dll` checklist:
> [lessons-learned.md §Proxy DLLs](lessons-learned.md#proxy-dlls--the-loader-can-call-our-exports-before-we-exist).
> This section is the short version of what the code does and why.

Windows' AppCompat shim engine (`apphelp.dll` + `AcGenral.dll`) loads *ahead of
the CRT* on any game that carries a compat layer, and `apphelp!SE_DllLoaded`
fires when a module is **MAPPED — before its init routine runs**. A proxy export
entered in that window runs with `_DllMainCRTStartup` not yet executed. Our
shipped proxies are `/MT` (they import `KERNEL32.dll` and nothing else), so
`__acrt_heap` is still NULL and the first `malloc` becomes
`HeapAlloc(NULL, ...)` → access violation before the EXE entry point.

`version.dll` is one step from that window already: `AcGenral.dll` **statically
imports** `GetFileVersionInfoSizeW` / `GetFileVersionInfoW` / `VerQueryValueW`,
and `version.dll` is not a KnownDLL, so on a shimmed game the copy it binds to
is ours. We survive today only because a static dependency is initialised before
its dependent — an accident of load order, not a design.

**The latch** ([loader.h](../dll/src/proxy/loader.h)):

| symbol | what |
|---|---|
| `Proxy_MarkCrtReady()` | **First statement** of each proxy's `DLL_PROCESS_ATTACH` |
| `Proxy_CrtReady()` | Read by every `PROXY_THUNK` and by `Proxy_LoadRealDll` |
| `Proxy_NotePreCrtCall()` | `InterlockedIncrement` only — no allocation, no CRT |
| `Proxy_PreCrtCallCount()` | Reported once from the backend-loader thread, off the loader lock |

Both counters are plain `volatile LONG`s with **constant** initialisers so they
live in zero-filled `.data` with no dynamic initialiser. A `std::atomic` that is
not constant-initialised would be read before it was written.

The gate **refuses outright** rather than doing a kernel32-only, allocation-free
resolve: the pre-CRT caller *is* the loader, so a `LoadLibraryW` there is a
recursive load under a lock it still holds. Nothing is latched — the cache stays
empty, `fn` stays null, and the host's own next call resolves normally.

### Two null cases, kept apart on purpose

| state | meaning | answer |
|---|---|---|
| CRT not up | resolver never ran | the export's documented failure value, **do not forward** |
| CRT up, `fn` still null | resolver ran; the name is genuinely absent from the real DLL | fall through and **crash loudly, on purpose** |

⚠ `MMSYSERR_NOERROR`, `TIMERR_NOERROR` and `JOYERR_NOERROR` are **all `0`**, so a
blanket "return 0 when unresolved" would report *success* for a refused
`timeBeginPeriod` — the host would believe it got a 1 ms tick and never find out.
`winmm.cpp` therefore carries `kMmFail` / `kTimeFail` / `kJoyFail` / `kMciFail`,
and `timeSetEvent` inverts inside its own family (it returns a timer **ID**,
whose failure answer *is* `0`).

### What is deliberately NOT copied from UE5CEDumper

Their follow-up fix removed the `SRWLOCK` from their resolver, because it was
held **across `LoadLibraryW`** and the load re-entered their own thunk on the
same thread — `SRWLOCK` is non-recursive, so it deadlocked the loader.

`Proxy_LoadRealDll` never holds the lock across `LoadLibraryW` (shared scan →
release → load → exclusive insert → release → `FreeLibrary` the duplicate), so
it is structurally immune. **Keep the lock.** The transferable rule is the
general one: *never hold a non-recursive lock across `LoadLibraryW` in a proxy.*

### Regression rig

```bash
py tools/verify/proxy_precrt_gate.py --all
```

Maps each proxy with `LoadLibraryExW(..., DONT_RESOLVE_DLL_REFERENCES)` — image
mapped, exports callable, `DllMain` never runs — and calls a thunk from a child
process, so a fault is an exit code rather than a dead harness. Then repeats
with a normal `LoadLibraryW` and checks the export actually reaches the real
System32 DLL, which is what catches a gate that latched null.

Pass `--baseline <dir>` with pre-gate binaries for the negative control; without
it the run says so rather than implying the comparison happened.

**x64 verified, build #1429 (2026-08-27), against a #1424 pre-gate control:**
all 5 pre-gate probes fault
`0xC0000005`, all 5 gated probes return cleanly, `timeBeginPeriod` answers `97`
(`kTimeFail` — a value only the gate produces), and all 4 forward probes reach
the real DLL. **x86 is verified by construction only** — the rig needs a
same-bitness interpreter and there is no 32-bit Python here, so it reports x86
as *not tested*. See
[lessons-learned.md §Verifying it without a game](lessons-learned.md#verifying-it-without-a-game)
for the full output and what it does and does not prove.

### In-game

⚠ **Testing this in a game needs the right target, and "the game has an AppCompat
layer" is not the criterion.** A `/MT` build #1431 proxy ran end-to-end on
Utawarerumono with zero pre-CRT refusals — and that game *does* carry
`~ HIGHDPIAWARE`, the same layer as the crash in the audit, yet loaded
`apphelp.dll` **without** `AcGenral.dll`, which is the module that actually calls
into a mapped-but-uninitialised proxy.

The check is one grep against our own `init` log, which already prints every
loaded module:

```bash
grep -i acgenral "$LOCALAPPDATA/discrete/Logs/<pid>/init/current.log"
```

Details:
[lessons-learned.md §In-game so far](lessons-learned.md#in-game-so-far--and-why-has-a-compat-layer-is-the-wrong-test-criterion).

## Pick which proxy

| Variant | When to use |
|---|---|
| **`version.dll`** | Default — every Unity .exe imports it. First choice unless something else has already claimed `<game-dir>\version.dll`. |
| **`winmm.dll`** | Fallback — when another tool (RTSS, ReShade injector, MelonLoader's loader) has already deployed `version.dll`. Most Unity .exes also import `winmm.dll`. |
| **`dxgi.dll`** | Not shipped yet. Would cover D3D11/12 games where neither `version` nor `winmm` is imported. No game in the test matrix needs it yet. ⚠ **Read [lessons-learned.md §If you add a `dxgi.dll` proxy](lessons-learned.md#if-you-add-a-dxgidll-proxy-this-stops-being-latent) first** — `dxgi.dll` is one of the three names the AppCompat shim engine searches for, so the pre-CRT window stops being latent and becomes a game that will not start. |

When both `version` and `winmm` are unavailable, the user falls back
to direct injection via `DiscreteUI.exe` Pick & Inject or
`tools/Install-CEScripts.ps1`.

## ProxyDeployWindow (UI)

Phase 12c shipped a dedicated **Proxy Deploy** window — open via
"Proxy Deploy…" in the connection bar.

### Layout

```
┌─ Proxy DLL Deploy ──────────────────────────────────────────────┐
│ [filter…] [☑ Show 32-bit Mono] [☐ Show unsupported] [↻ Rescan]  │
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

Two independent visibility toggles (the old combined "Show 32-bit /
unsupported" checkbox split in two):

- **"Show 32-bit Mono"** — **on by default**. x86 Mono is a supported target
  (Phase 12e), so its rows list like any x64 game; uncheck only to hide them
  in a mixed library.
- **"Show unsupported"** — off by default. Surfaces truly-unsupported rows
  (x86 IL2CPP — no 32-bit IL2CPP backend — unknown backend, ARM64 / corrupt
  PE). Bulk operations skip them.

32-bit Mono is the only supported 32-bit case, so the two toggles never
gate the same row.

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

## Running under Proton / Wine

> **Best-effort, unverified.** Nothing in this section has been tested on
> Linux. It follows from how Wine loads DLLs and scopes named pipes; report
> what you find.

1. **DLL override.** Wine loads its own builtin `version.dll` / `winmm.dll`
   and ignores a same-name file next to the game unless told otherwise. Set
   the game's Steam launch options to
   `WINEDLLOVERRIDES="version=n,b" %command%` (or `winmm=n,b` for the
   `winmm.dll` proxy). Without it the backend never loads and no Discrete
   log appears.
2. **Deploy by hand.** Copy the proxy and `Discrete-*.dll` as in
   [Manual deploy](#manual-deploy-no-ui). The Proxy Deploy window's Steam
   scan reads the prefix's stub Steam registry entry and
   `steamapps\libraryfolders.vdf`, so it does not see the Linux Steam
   library.
3. **Same prefix for every client.** The pipe `\\.\pipe\DiscreteBfx` is only
   visible inside one Wine prefix (one wineserver). Start `DiscreteUI.exe`
   and Cheat Engine inside the game's prefix, e.g.
   `protontricks-launch --appid <appid> DiscreteUI.exe`. The same applies to
   direct injection and to `dist\x86\inject-x86.exe` for 32-bit Mono.
4. **Native Linux builds.** If the game also ships a Linux build, force a
   Proton compatibility tool in the game's Steam properties so the Windows
   build (which the proxy and backend need) is the one that runs.

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
- **32-bit Mono games**: supported (Phase 12e). The installer auto-sources
  the x86 proxy + `Discrete-Mono.dll` from `dist/x86/`. **32-bit IL2CPP**
  stays unsupported (no 32-bit IL2CPP backend) — those rows are hidden
  unless "Show unsupported" is on, and skipped in bulk ops.
- **Proton / Wine**: needs a DLL override, and the UI / CE must run in the
  game's prefix — see [Running under Proton / Wine](#running-under-proton--wine).

## Source map

| File | What |
|---|---|
| [dll/src/proxy/loader.h](../dll/src/proxy/loader.h) | Public contract: the pre-CRT latch, `Proxy_LoadRealDll`, `Proxy_GetRealExport`, `Proxy_StartBackendLoader` |
| [dll/src/proxy/loader.cpp](../dll/src/proxy/loader.cpp) | Implementation: pre-CRT gate + counter, HMODULE cache, system32 path resolution, worker-thread backend loader |
| [dll/src/proxy/version/version.cpp](../dll/src/proxy/version/version.cpp) | Per-export `PROXY_THUNK` macros — the 12 `version.dll` exports Unity needs |
| [dll/src/proxy/version/version.def](../dll/src/proxy/version/version.def) | Export aliasing (Proxy_name → name) |
| [dll/src/proxy/winmm/winmm.cpp](../dll/src/proxy/winmm/winmm.cpp) | Per-export thunks — the 48 `winmm.dll` exports Unity + SDL2 statically import |
| [dll/src/proxy/winmm/winmm.def](../dll/src/proxy/winmm/winmm.def) | Export aliasing |
| [ui/UnityDumpUI/ViewModels/ProxyDeployViewModel.cs](../ui/UnityDumpUI/ViewModels/ProxyDeployViewModel.cs) | UI VM — scan / install / uninstall / update commands |
| [ui/UnityDumpUI/Views/ProxyDeployWindow.axaml](../ui/UnityDumpUI/Views/ProxyDeployWindow.axaml) | Bulk install panel |
| `ui/UnityDumpUI.Core/Platform/WindowsProxyInstaller.cs` | Cross-process file ops + mutex install (swap if other kind present) |
| `ui/UnityDumpUI.Core/Platform/UnityGameScanner.cs` | Steam library + Registry scan + per-game backend / bitness detection |
