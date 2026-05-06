# UnityCEDumper — CE Lua Bridge

Four Lua scripts that drive the dumper from inside Cheat Engine. Phase 11
of the project; CE-side equivalent of running `DiscreteUI.exe` + the
in-app process picker.

## Install

The dumper expects the Lua scripts and backend DLLs to live in
`<CE-install>/discrete/`. Two ways to put them there:

### Recommended: `build.cmd deploy`

From the project root, in any (non-admin) terminal:

```powershell
# Live iteration on the Lua bridge — copies scripts/*.lua + dist/*.dll
.\build.cmd deploy-scripts

# Post-build full deploy — copies everything from dist/
.\build.cmd publish     # build first
.\build.cmd deploy
```

Both commands invoke `tools/Install-CEScripts.ps1`, which self-elevates
via `Start-Process -Verb RunAs` when CE is in `Program Files` (a UAC
prompt appears). It auto-detects the CE install via `$env:CE_PATH`,
then `C:\Program Files\Cheat Engine`, then `C:\Program Files (x86)\Cheat
Engine`. Override with `-CePath`:

```powershell
.\tools\Install-CEScripts.ps1 -CePath 'D:\Tools\CE'
```

### Manual layout (no deploy script)

```
<CE-install>/
├── cheatengine-x86_64.exe
└── discrete/
    ├── UnityDumper-IL2CPP.dll      <- from dist/ after build
    ├── UnityDumper-Mono.dll        <- from dist/ after build
    ├── unity_dump.lua
    ├── unity_dissect.lua
    ├── unity_symbols.lua
    └── utils.lua
```

DLLs always come from `dist/`; run `.\build.cmd` once to produce them.

## Workflow

1. **Open the game** — File → Open Process → pick the Unity target.
2. **Inject the dumper**:
   ```lua
   dofile([[<CE>\discrete\unity_dump.lua]])
   ```
   Output:
   ```
   [UnityCEDumper] Backend: IL2CPP — DLL injected (build N)
   [UnityCEDumper] Pipe ready: \\.\pipe\UnityDumpBfx
   ```
3. **(Optional) Dissect a class** — populate CE's Structure Dissect
   directly from the live class layout:
   ```lua
   dofile([[<CE>\discrete\unity_dissect.lua]])
   dissectClass("Assembly-CSharp", "MainGame", "PLY_WORK")
   ```
   For nested types use `Outer/Inner` as the class name with an empty
   namespace, e.g. `dissectClass("Assembly-CSharp", "", "MainGame/PLY_WORK")`.
   The structure registers into CE's global structures list and shows
   up under *Memory View → Ctrl+D → Structures*.

4. **(Optional) Register CE symbols** for static fields + method
   addresses, so cheat tables can reference them by name:
   ```lua
   dofile([[<CE>\discrete\unity_symbols.lua]])
   registerClassSymbols("Assembly-CSharp", "MainGame", "PLY_WORK")
   -- Mono targets: pass {compileMethods=false} to skip JIT-on-register
   --   for unused methods (statics-only, faster).
   registerClassSymbols("Assembly-CSharp", "", "Player", {compileMethods=false})
   -- Verbose: log each method name before its monoCompileMethod call
   --   (useful when diagnosing a specific class).
   registerClassSymbols("Assembly-CSharp", "", "GameManager", {verbose=true})
   ```
   Resulting symbol names follow `Archiutecture.md §"Symbol Registration
   Pattern"`:
   ```
   Unity:Assembly-CSharp:MainGame.PLY_WORK.s_<staticFieldName>     (static fields)
   Unity:Assembly-CSharp:MainGame.PLY_WORK.m_<methodName>          (method native addresses)
   ```
   Verify with CE's `getAddress`:
   ```lua
   print(string.format("%X", getAddress("Unity:Assembly-CSharp:MainGame.PLY_WORK.m_SomeMethod")))
   ```
   *(Older releases used `::<methodName>` for methods; that form
   collides with CE's symbol resolver and is no longer emitted —
   migrate any hardcoded references in cheat tables to `.m_`.)*

5. **Switching to the standalone UI** — the DLL pipe server is
   single-instance, so the desktop UI (`DiscreteUI.exe`) and CE Lua
   bridge are **mutually exclusive while connected**. To swap:
   - In Lua, just stop sending RPCs (no Disconnect needed).
   - Launch `DiscreteUI.exe`, click *Connect* — DLL stays loaded, pipe
     re-binds.
   - To go back to CE Lua, click *Disconnect* in the UI first; then
     re-run any of the dofile lines.

## Notes

* `unity_dump.lua` uses CE's `loadLibrary()` to inject — same mechanism
  as the standalone UI's `WindowsDllInjector` (CreateRemoteThread →
  LoadLibraryW). Logs land at
  `%LOCALAPPDATA%\UnityCEDumper\Logs\<pid>\init\current.log`.
* `unity_dissect.lua` and `unity_symbols.lua` open the named pipe
  directly via `io.open([[\\.\pipe\UnityDumpBfx]], "r+b")`. Both ship
  a tiny purpose-built JSON parser (no external Lua deps); good enough
  for the DLL's response shape, not a general JSON library.
* **Pipe-open retry**: both scripts retry `io.open` up to 15× at 200 ms
  intervals before giving up (~3 s budget). The DLL's pipe-server
  worker thread may not have finished `CreateNamedPipeW` by the time
  the inject script returns *Pipe ready*, so the first RPC sometimes
  fails with `Invalid argument`. The retry handles it transparently.
* **Main-thread yield**: each RPC inserts `processMessages()` +
  `sleep(1)` so CE's Windows message pump can service paint/input
  between calls. Without this, `registerClassSymbols` against a
  300+ method class (Mono JIT path issues one RPC per method) makes
  CE *appear* hung for 30–60 s even though Lua is making progress.
  Cost ~1 ms per RPC, ~330 ms across a full Silksong `GameManager`
  registration.
* **`createStructure` is unregistered by default**: CE 7.5.x's
  `createStructure` returns a `TDissectedStruct` that is **not** added
  to the global structures list. `unity_dissect.lua` calls
  `s.addToGlobalStructureList()` after populating elements, and dedups
  same-label structures so re-runs replace instead of accumulating.
* **Method-symbol naming** (`Cls.m_<method>`) was changed from
  `Cls::<method>` because CE's symbol resolver treats `::` as ambiguous
  and falls through to a slow Mono-runtime enumeration that hangs the
  UI for minutes against a large game's symbol table.
* All scripts are safe to re-run: the DLL's `Init` / `StartPipe` are
  idempotent, re-registering an existing CE symbol just overwrites the
  previous binding, and `unity_dissect.lua` removes any prior
  same-label structure before re-creating it.
