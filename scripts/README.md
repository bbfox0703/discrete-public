# UnityCEDumper — CE Lua Bridge

Three Lua scripts that drive the dumper from inside Cheat Engine. Phase 11
of the project; CE-side equivalent of running `DiscreteUI.exe` + the
in-app process picker.

## Install

Copy the contents of this folder into a `discrete` subfolder under your
Cheat Engine install:

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

(The DLLs come from the project's `dist/` folder. Run `.\build.cmd` once
to produce them.)

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
4. **(Optional) Register CE symbols** for static fields + IL2CPP method
   addresses, so cheat tables can reference them by name:
   ```lua
   dofile([[<CE>\discrete\unity_symbols.lua]])
   registerClassSymbols("Assembly-CSharp", "MainGame", "PLY_WORK")
   ```
   Resulting symbol names follow `Architecture.md §"Symbol Registration
   Pattern"`:
   ```
   Unity:Assembly-CSharp:MainGame.PLY_WORK.s_<staticFieldName>
   Unity:Assembly-CSharp:MainGame.PLY_WORK::<methodName>
   ```
5. **(Optional) Drive the standalone UI in parallel** — once the pipe is
   up the desktop UI (`DiscreteUI.exe`) can attach via *Connect* without
   re-injecting. Useful for running the export buttons / Inspector tab
   alongside CE.

## Notes

* `unity_dump.lua` uses CE's `loadLibrary()` to inject — same mechanism
  as the standalone UI's `WindowsDllInjector` (CreateRemoteThread →
  LoadLibraryW). Logs land at
  `%LOCALAPPDATA%\UnityCEDumper\Logs\<pid>\init\current.log`.
* `unity_dissect.lua` and `unity_symbols.lua` open the named pipe
  directly via `io.open([[\\.\pipe\UnityDumpBfx]], "r+b")`. Both ship
  a tiny purpose-built JSON parser (no external Lua deps); good enough
  for the DLL's response shape, not a general JSON library.
* All three scripts are safe to re-run: the DLL's `Init` / `StartPipe`
  are idempotent, and re-registering an existing CE symbol just
  overwrites the previous binding.
