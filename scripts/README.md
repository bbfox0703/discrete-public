# UnityCEDumper — CE Lua Bridge

Eight Lua scripts that drive the dumper from inside Cheat Engine. Phase 11
of the project (Phase 17d added `unity_addrfind.lua`; Phase 19e added
`unity_invoke.lua`; Phase 20a added `unity_scene.lua`; later added
`mono_registersymbol_helpers.lua` for the DLL-free Mono register path);
CE-side equivalent of running `DiscreteUI.exe` + the in-app process picker.

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
    ├── unity_addrfind.lua
    ├── unity_invoke.lua
    ├── unity_scene.lua
    ├── mono_registersymbol_helpers.lua  <- DLL-free Mono register helpers
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

   **Phase 18 — overload-aware registration**: when a class has
   overloaded methods, `registerClassSymbols` only catches the first
   match (CE rejects duplicates). Use `registerMethodOverloads` to
   register every overload with a parameter-type suffix:
   ```lua
   registerMethodOverloads("UnityEngine.CoreModule", "UnityEngine", "Vector3", "Set")
   -- Optional sigFilter narrows on the rendered signature substring:
   registerMethodOverloads("Assembly-CSharp", "", "Foo", "Bar", {sigFilter="Vector3"})
   ```
   Symbol shape: `Unity:{Asm}:{Ns}.{Cls}.m_{Method}__{ParamSuffix}`
   where `ParamSuffix` matches DiscreteUI's overload picker:
   `__3float` / `__Vector3` / `__noargs` / `__string_Card_Point` etc.

5. **(Optional) Reverse address lookup** — given any process address
   (e.g. from CE's *Find what writes / accesses* hit, a pointer-scan
   result, or anything you've highlighted in the memory view),
   identify which managed instance + class + declared field it sits
   inside:
   ```lua
   dofile([[<CE>\discrete\unity_addrfind.lua]])
   lookupAddress(0x000001E55B2518F0)        -- pretty-prints to console
   local hit = whatIsAt(0x000001E55B2518F0) -- programmatic; returns table
   ```
   Output (Phase 17b shape):
   ```
   [UnityCEDumper.addrfind] address 0x000001E55B2518F0 -> instance=0x000001E55B2518F0  class=MainGame.PLY_WORK  fieldOffset=16
     field: chrNo  type=System.Byte  @+0x10  size=1  kind=leaf  declaredIn=PLY_WORK
   ```
   Optional: bind a global hotkey that picks up whatever address is
   currently selected in CE's memory view (or pops `inputQuery` for a
   manual paste when no memory view is open):
   ```lua
   bindAddrLookupHotkey({ VK_CONTROL, VK_F2 })
   ```

6. **(Optional) Call managed methods** — Phase 19e. Invoke any static
   or instance method on either backend (Mono / IL2CPP) with marshaled
   args + typed return value + managed exception capture. Closes the
   long-standing CLAUDE.md goal of "Lua-callable Unity methods". Full
   spec: [docs/lua-invoke.md](../docs/lua-invoke.md).
   ```lua
   dofile([[<CE>\discrete\unity_invoke.lua]])

   -- Static call (Mathf.Abs):
   local r = callUnityMethod(
       "UnityEngine.CoreModule", "UnityEngine", "Mathf", "Abs",
       { argInt32(-7) },
       { signatureMatch = "(System.Int32)" })
   print(r.ok, r.returnValue)   -- true, 7

   -- Instance call (item count on a live ItemBundle):
   local r = callUnityMethod(
       "Assembly-CSharp", "", "ItemBundle", "get_numberOfItems",
       {},
       { instance = "0x16F8CEA0BE0", signatureMatch = "()" })
   print(r.returnValue)         -- 1

   -- Managed exception (Convert.ToByte(-1) overflows):
   local r = callUnityMethod(
       "mscorlib", "System", "Convert", "ToByte",
       { argInt32(-1) },
       { signatureMatch = "(System.Int32)" })
   print(r.ok, r.exception)
   --> false, "System.OverflowException: Value was either too large or…"
   ```
   Arg builders for every wire type tag (`argNull` / `argBool` /
   `argByte` / `argSByte` / `argInt16` / `argUInt16` / `argInt32` /
   `argUInt32` / `argInt64` / `argUInt64` / `argFloat` / `argDouble` /
   `argChar` / `argString` / `argAddress`) — see the script header for
   the full list. Convenience: `prettyPrint(r)` logs a one-line summary;
   `pcallUnityMethod(...)` is the non-throwing variant; `findOverloads(asm,
   ns, class, method)` lists every overload so you can prep a
   `signatureMatch` (e.g. `Mathf.Abs` has 6).

   **Overload disambiguation gotcha**: the wire `signatureMatch` is a
   substring filter. Bare `"Int32"` matches `(Int32)` AND `(UInt32)`
   AND `(Int32, IFormatProvider)` — `Int32` is a substring of `UInt32`
   and any `(Int32, …)` overload contains it too. Default to the
   parens-bounded form `"(System.Int32)"` for guaranteed uniqueness.

   **GC-root caveat**: a managed pointer returned by `callUnityMethod`
   is valid for the next call you make right now. A GC that ran between
   the address read and the next invoke can have moved the object
   (Mono moving GC). Best practice: chain `findInstances` →
   `callUnityMethod` in a single Lua block when correctness matters,
   instead of stashing the address.

7. **(Optional) Scene / GameObject explorer** — Phase 20a. Walk the
   live scene's GameObject list, drill into Transform child trees, and
   list Components attached to any GameObject. Pure composition over
   `findInstances` (heap scan) + `callMethod` (Phase 19) — no new wire
   commands. Concept borrowed from `vendor/UnityExplorer/`'s in-process
   Scene Explorer, reproduced for our out-of-process pipe architecture.
   ```lua
   dofile([[<CE>\discrete\unity_scene.lua]])

   -- All GameObjects in heap (fast; no name decode unless asked):
   local all = getAllGameObjects()
   print(#all .. " GameObjects")

   -- Substring search across the loaded scene:
   for _, h in ipairs(findByName("Camera")) do
       print(string.format("0x%X  %s", h.address, h.name))
   end

   -- Active scene roots (filters by transform.parent == null):
   local roots = getActiveSceneRoots()

   -- Children of a known GameObject:
   local kids = getChildren(roots[1].address)

   -- Components attached to a GameObject:
   local comps = getComponents(roots[1].address)
   for _, c in ipairs(comps) do
       print(string.format("0x%X  %s.%s", c.address, c.namespace or "", c.className or "?"))
   end
   ```
   Public API (all return Lua arrays, 1-indexed; throw on RPC error so
   callers can `pcall`):
   `getName(addr)` / `getAllGameObjects(opts)` / `findByName(pattern, opts)` /
   `getActiveSceneRoots(opts)` / `getChildren(addr)` /
   `getComponents(addr)`. Full `opts` shape lives in the script header.

   **Cost notes**: `getAllGameObjects()` is one heap scan (cheap).
   `getActiveSceneRoots()` does N+1 `callMethod`s (one per object) to
   filter on `transform.parent == null`; that's tens of ms on a small
   scene, several hundred ms on a busy one. `getComponents()` issues
   one `whatIsAt` per component to recover the runtime class, so it's
   the costliest op — defer until the user expands a node.

8. **Switching to the standalone UI** — the DLL pipe server is
   single-instance, so the desktop UI (`DiscreteUI.exe`) and CE Lua
   bridge are **mutually exclusive while connected**. To swap:
   - In Lua, just stop sending RPCs (no Disconnect needed).
   - Launch `DiscreteUI.exe`, click *Connect* — DLL stays loaded, pipe
     re-binds.
   - To go back to CE Lua, click *Disconnect* in the UI first; then
     re-run any of the dofile lines.

## Deployment for CE-table publishers

Two patterns for shipping a .CT that uses our framework. **DiscreteUI.exe
is a developer tool — do NOT distribute it to end users.** End users get
either:

### Mode A — Install once into CE folder

User extracts `discrete-runtime-{version}.zip` (or copies manually) into
`<CE-install>/discrete/`. Final layout:

```
<CE-install>/
  discrete/
    UnityDumper-Mono.dll        ← required (or UnityDumper-IL2CPP.dll)
    UnityDumper-IL2CPP.dll      ← required if game is IL2CPP
    utils.lua
    unity_dump.lua
    unity_dissect.lua           ← optional (only if CT uses it)
    unity_symbols.lua           ← optional
    unity_invoke.lua            ← optional
    unity_scene.lua             ← optional
    unity_addrfind.lua          ← optional
    mono_registersymbol_helpers.lua  ← optional
    x86/
      UnityDumper-Mono.dll      ← if targeting 32-bit Mono games
```

Pro: one shared install across N CT files.
Con: end user has to copy files once.

CE table activate-script does the usual `dofile(getCheatEngineDir() ..
'discrete\\unity_dump.lua') ; inject(getOpenedProcessID())`.

### Mode B — Everything embedded in the .CT (zero-touch)

User just opens the .CT. The .CT contains the backend DLL + lua scripts
as embedded files (Table menu → Add file…). Activate script extracts to
`%TEMP%\Discrete_UEDumper\<table-id>\` and injects from there.

Reference impl: [`scripts/ce_table_bootstrap.lua`](ce_table_bootstrap.lua).
Copy its `bootstrap()` function into your CT's autorun Lua, customise
`FILES_TO_EXTRACT` for the files you embedded, call `bootstrap()` from
the [ENABLE] script.

Pro: end user does literally nothing — just open the .CT. No
"download these N files first" instructions.
Con: each CT is ~1 MB larger (backend DLL); updating the runtime
requires re-publishing every CT that embeds it.

### Proxy DLL deployment (alternative to runtime inject)

CE Lua `loadLibrary()` is the **simplest** end-user injection path —
`unity_dump.lua` calls it at table-activate. Some CT publishers prefer
proxy-DLL deployment instead (user drops `version.dll` / `winmm.dll`
next to the game .exe → Windows auto-loads it before Unity starts).
Trade-offs:

| Injection style | Pros | Cons |
|---|---|---|
| **CE Lua `loadLibrary` at activate** (default) | Zero install. User opens .CT → it Just Works. Works on protected processes that allow LoadLibrary but block proxy load. | Inject happens *after* Unity initializes; backend detection fires post-handshake. |
| **Proxy DLL (`version.dll` / `winmm.dll`)** | DLL loads before Unity → catches very-early init events. Survives game restart without re-running CT activate. Some games block `LoadLibrary` from CE specifically (rare). | User has to drop the proxy file into the game folder manually. Anti-virus often complains about generic `version.dll` proxies. |

**If you ship a proxy DLL** — pick the right arch:

| Target | Proxy from |
|---|---|
| 64-bit Unity (most modern games) | `dist/version.dll` or `dist/winmm.dll` (x64) |
| 32-bit Unity (legacy Mono, e.g. older Unity 2018-2020 games) | `dist/x86/version.dll` or `dist/x86/winmm.dll` |

The proxy auto-loads the appropriate `UnityDumper-Mono.dll` /
`UnityDumper-IL2CPP.dll` from the same directory at game startup.

The matching backend DLL (`UnityDumper-{Mono,IL2CPP}.dll` for the right
arch) must be deployed alongside the proxy. **Don't mix arch**: a 64-bit
proxy with a 32-bit backend DLL silently fails.

### Files NOT to deploy

| File | Don't ship to end users | Why |
|---|---|---|
| `DiscreteUI.exe` | ✋ | Developer UI; surface area + complexity end users don't need |
| `metadata-cli.exe` | ✋ | Offline metadata dump tool (dev/forensics) |
| `inject-x86.exe` | ✋ usually | DiscreteUI's cross-bitness helper; CE 7.5+ `loadLibrary()` handles 32-bit targets directly |
| `*.pdb` | ✋ | Debug symbols |

## Notes

* `unity_dump.lua` uses CE's `loadLibrary()` to inject — same mechanism
  as the standalone UI's `WindowsDllInjector` (CreateRemoteThread →
  LoadLibraryW). Logs land at
  `%LOCALAPPDATA%\Discrete_UEDumper\Logs\<pid>\init\current.log`.
* `unity_dissect.lua`, `unity_symbols.lua`, `unity_addrfind.lua`, and
  `unity_invoke.lua` open the named pipe directly via
  `io.open([[\\.\pipe\UnityDumpBfx]], "r+b")`. All four ship a tiny
  purpose-built JSON parser (no external Lua deps); good enough for the
  DLL's response shape, not a general JSON library. `unity_invoke.lua`
  also ships a small JSON serializer because `callMethod`'s `args` is a
  nested array; the other scripts get away with hand-built request
  strings because they only send flat key-value args.
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
