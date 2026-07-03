# Lua logging & Lua Engine window convention

> **Rule (MUST follow) for every CE Lua surface in this repo** — the
> hand-written `scripts/*.lua` bridges **and** the `{$lua}` blocks emitted by
> the C# AA-script generators (`ui/.../Services/Export/*AaScriptGenerator.cs`).

## Why

Every Lua script here used bare `print()` for normal *and* error output. CE
routes `print()` to the **Lua Engine** form, and CE force-shows that form on any
output — so a routine "enable a cheat" / "inject the DLL" run pops the Lua
Engine on top and **covers CE's main window**. The user then has to dismiss it
every single time.

Two changes fix this:

1. **Gate normal output behind a DEBUG flag.** A clean run stays silent.
2. **Close the Lua Engine on a clean run; leave it open on error.** So success
   is invisible and only failures demand attention (with the message visible).

## The three rules

### 1. DEBUG flag gates informational output

- A flag controls informational / progress / data output. Default **off**.
- The flag is the global **`DISCRETE_DEBUG`** (set `DISCRETE_DEBUG = 1` before
  running a script to opt in) — or, in `utils.lua` consumers, `U.DEBUG = true`.
- When the flag is **off**, `log()` / `dbg()` / `warn()` print **nothing**.
- When the flag is **on**, everything prints **and** the Lua Engine is left open
  so you can actually read it (see rule 3).

### 2. Errors ALWAYS print, regardless of the flag

- `err(...)` prints unconditionally and marks the run as failed.
- A failed run keeps the Lua Engine window open (rule 3), so the error is seen.
- **Escalate, don't lose:** if a soft `warn()` is important enough that the user
  must see it even on a quiet run, make it an `err()`. A `warn()` on a quiet
  run is intentionally suppressed and the window closes over it.

| Level              | Prints when flag OFF | Prints when flag ON | Keeps window open |
|--------------------|:--------------------:|:-------------------:|:-----------------:|
| `log()` / `dbg()`  |          no          |         yes         |    only if flag ON |
| `warn()`           |          no          |         yes         |    only if flag ON |
| `err()`            |     **yes**          |         yes         |    **yes**         |

### 3. Close the Lua Engine before finishing — unless it was a debug run or an error

At the end of **every** entry point — the top-level `pcall` of a `scripts/*.lua`
file, and the last line of a generated AA `[ENABLE]` **and** `[DISABLE]` block —
close the Lua Engine so it stops covering CE:

```lua
synchronize(function()
  local e = getLuaEngine()
  if e then e.Close() end
end)
```

- Run it **only on a clean finish** (no `err()` emitted, no Lua error thrown).
- Do **not** run it when `DISCRETE_DEBUG` is on — the user turned on output
  precisely to read it; closing the window would defeat that.
- On error, **skip the close** so the Lua Engine stays up with the message.

CE API notes (verified against `celua.txt`):
- `getLuaEngine()` — returns the Lua Engine form (creates it if needed).
- `synchronize(fn)` — runs `fn` on the main thread; form methods must run there.
- `celua.txt` documents lowercase `close()` / `hide()`. CE also exposes the
  published Pascal `Close()` (the form's real method, what the user runs). The
  `utils.lua` helper tries `Close()` → `close()` → `hide()`, each `pcall`-guarded,
  so it works on any CE build. Inline snippets use `e.Close()` for brevity;
  wrap the whole `synchronize` in `pcall` if the surrounding block isn't already
  guarded.

## Reference implementation — `scripts/utils.lua`

Files that `require 'utils'` get the whole convention for free:

```lua
local U = require 'utils'

U.DEBUG = false               -- or set global DISCRETE_DEBUG = 1

U.log("attached pid %d", pid) -- gated: silent unless DEBUG
U.warn("pipe slow to start")  -- gated
U.err("no process attached")  -- always prints; marks run failed → window stays

U.guard(function()
  -- ... the whole script body ...
end)
-- guard(): runs body under pcall; on a clean run (no err(), DEBUG off)
-- it closes the Lua Engine; on any error it prints it and leaves the window up.
```

`utils.lua` API added for this convention:

- `U.DEBUG` — per-module flag (default `false`). Global `DISCRETE_DEBUG == 1`
  also enables output.
- `U.log(fmt, ...)` / `U.dbg(fmt, ...)` — DEBUG-gated info.
- `U.warn(fmt, ...)` — DEBUG-gated warning.
- `U.err(fmt, ...)` — always prints; sets the sticky error flag.
- `U.hadError()` / `U.resetError()` — inspect / clear the sticky flag.
- `U.closeLuaEngine()` — force-close the Lua Engine (best-effort, main-thread).
- `U.closeLuaEngineIfQuiet()` — close only if no error and DEBUG off.
- `U.guard(body) -> ok, result` — the one-call wrapper for an entry point.

## Shared globals (the cross-script protocol)

Standalone files and generated AA blocks can't share `utils.lua`'s module
state, so two globals carry the convention across every surface:

- **`_G.DISCRETE_DEBUG`** — set to `1` to enable verbose output and keep the
  Lua Engine open (so you can read it). Off/absent by default.
- **`_G.DISCRETE_HAD_ERROR`** — the shared "did anything fail?" flag. Every
  `err()` sets it `true`; an entry point resets it `false` at the start; the
  close-tail closes the Lua Engine only when it's still `false`. `utils.lua`'s
  `err()`/`resetError()` mirror the module-local flag to this global too.

## Standalone files (no `require 'utils'`)

`unity_symbols.lua`, `unity_invoke.lua`, and `mono_registersymbol_helpers.lua`
are drop-in single files (embedded individually in a `.CT`), so they can't
depend on `utils.lua`. They are **libraries** — the user calls their functions
interactively or from an AA — so they do NOT auto-close the Lua Engine (that
would disrupt an interactive session); they only DEBUG-gate their output. Each
embeds this self-contained header:

```lua
local function _dbgOn() return (tonumber(rawget(_G, "DISCRETE_DEBUG")) or 0) >= 1 end
local function dbg (fmt, ...) if _dbgOn() then print(string.format(fmt, ...)) end end
local function warn(fmt, ...) if _dbgOn() then print(string.format("WARN "  .. fmt, ...)) end end
local function err (fmt, ...)
  rawset(_G, "DISCRETE_HAD_ERROR", true)          -- shared cross-script failure signal
  print(string.format("ERROR " .. fmt, ...))
end
```

For a file that already routes everything through bare `print()` (e.g.
`mono_registersymbol_helpers.lua`), the least-churn adoption is to **shadow
`print` with a gated local** and add an always-printing `eprint` for failures —
then only the error lines change:

```lua
local _rawprint = print
local function _dbgOn() return (tonumber(rawget(_G, "DISCRETE_DEBUG")) or 0) >= 1 end
local function print(...) if _dbgOn() then _rawprint(...) end end          -- info: gated
local function eprint(...) rawset(_G, "DISCRETE_HAD_ERROR", true); _rawprint(...) end  -- error: always
```

(Both must be declared before the functions that use them so the `_G.*`
closures capture them as upvalues.) Convert only the failure `print()`s to
`eprint()`; every remaining `print()` auto-gates.

An entry-point file that the user runs whole (not a library) — e.g.
`ce_table_bootstrap.lua`'s `bootstrap()`/`teardown()` — also gates its progress
output and closes the Lua Engine on a clean finish:

```lua
local function closeLuaEngineIfQuiet()
  if _dbgOn() or rawget(_G, "DISCRETE_HAD_ERROR") then return end
  pcall(synchronize, function()
    local e = getLuaEngine()
    if e and not pcall(function() e.Close() end)
        and not pcall(function() e.close() end) then pcall(function() e.hide() end) end
  end)
end
```

## Generated AA `{$lua}` blocks (C# generators)

CE runs `{$lua}` **before** `{$asm}` (see
[[reference_ce_lua_runs_before_asm]] / `docs/ce-bridge-integration.md`). Each
generated `[ENABLE]` and `[DISABLE]` block:

1. **Resets the flag** right after the `if syntaxcheck then return end` guard:

   ```lua
   _G.DISCRETE_HAD_ERROR = false
   ```

2. **Gates its own progress `print()`s** behind `if DISCRETE_DEBUG == 1`. Keep
   the "file missing from CT" / "parse error" / "Enable Load Discrete Bridge
   first" prints **ungated** — those are `err`-level, and each is immediately
   followed by `return`, which skips the close-tail so the block ends with the
   Lua Engine visible. (Failures raised inside a self-loaded helper set
   `_G.DISCRETE_HAD_ERROR` via that helper's `err()`/`eprint()`.)

3. **Closes the engine as the last statement**, guarded on both globals:

   ```lua
   if _G.DISCRETE_DEBUG ~= 1 and not _G.DISCRETE_HAD_ERROR then
     synchronize(function() local e = getLuaEngine(); if e then e.Close() end end)
   end
   ```

Emit steps 1 and 3 via a shared helper (extend `AaLuaLoader`, e.g.
`AppendHadErrorReset` / `AppendCloseLuaEngineTail`) so every generator produces
the identical tail. **Generator unit tests that assert on the emitted string
must be updated in lockstep.**

Two per-generator caveats:

- **`AobAaScriptGenerator`** reports its result from a **deferred timer**
  (`discrete_runAfterAsm`) because the `{$asm}` scan hasn't run when the
  synchronous `{$lua}` block ends. So its `discrete_logScan` must set
  `_G.DISCRETE_HAD_ERROR = true` on a `NOT FOUND` (and gate its success line),
  and the close-tail must go at the **end of the deferred callback**, not the
  end of the synchronous block — otherwise it would close before success/failure
  is known.
- **`CallMethodAaScriptGenerator`** is the deliberate **exception** to the close
  rule. A call-method AA exists to *show* the result, so `prettyPrint` **always
  prints** (its summary/note go through an ungated `emit`; only failures ride
  `err`), and this generator emits **no** flag reset and **no** close-tail — CE's
  Lua Engine is left open with the result on screen. Every other generated action
  is quiet setup and does close on a clean run.

## Rollout status

- [x] `scripts/utils.lua` + `dist/utils.lua` — reference implementation
      (`DEBUG` flag, gated `log`/`dbg`/`warn`, always-print `err` + sticky flag,
      `closeLuaEngine`/`closeLuaEngineIfQuiet`/`guard`, mirrors `_G.DISCRETE_HAD_ERROR`).
- [x] `scripts/unity_dump.lua` — first adopter (`U.guard(main)`; success is
      silent + closes the window, every error path keeps it open).
- [x] `scripts/unity_symbols.lua` — library: `log`/`warn` gated, `err` always +
      sets the shared flag (no auto-close — interactive).
- [x] `scripts/unity_invoke.lua` — library: same; `prettyPrint` summary rides
      gated `log`, exceptions ride always-`err`.
- [x] `scripts/ce_table_bootstrap.lua` — entry point: `_msg` gated,
      `bootstrap()`/`teardown()` close the engine on a clean finish.
- [x] `scripts/mono_registersymbol_helpers.lua` — shadow-`print` gate + `eprint`
      for failures.
- [x] C# generators: `MonoRegisterSymbolAaScriptGenerator`, `AobAaScriptGenerator`
      (close-tail inside the deferred `discrete_runAfterAsm` callback),
      `AddressLookupAaScriptGenerator`, `BootstrapAaScriptGenerator` (+ their tests)
      and the shared `AaLuaLoader` `AppendHadErrorReset`/`AppendCloseLuaEngineTail`
      tail (also flags the two `AppendSelfLoad` failure prints).
- [x] `CallMethodAaScriptGenerator` — **deliberate exception**: emits NO close-tail
      (a call-method AA shows its result), and `prettyPrint` always prints (`emit`).
- [x] `dist/*.lua` re-synced (all six twins byte-identical, LF-only).

**Verified 2026-07-03**: full UI suite green — **864 tests, 0 failed**
(`dotnet` 10.0.301); Python confirms 0 CR bytes across all `scripts/`+`dist/`
`.lua` and the `-- =` line-1 invariant holds for the five embedded files.
