# Cheat Engine Bridge Integration (Phase 22)

> **Audience**: end-users producing a `.CT` to share, plus developers
> verifying Phase 22 changes against a live install. If you're new to
> Discrete, read [README.md](../README.md) first.

The Phase 22 pipeline lets DiscreteUI mutate a **currently-open** Cheat
Engine table via the AOBMaker plugin — no Ascii85 encoder, no offline
`.CT` builder, no manual `Table → Add File…` dance. End-users open a
table, click DiscreteUI buttons, save the table — done.

---

## What you need

| Component | Where | Notes |
|---|---|---|
| Cheat Engine 7.6+ | [cheatengine.org](https://www.cheatengine.org) | 7.5 also works for Phase 17/18/19; Phase 22 verified on 7.6. |
| AOBMaker CE plugin | [github.com/.../AOBMaker](https://github.com/) — `plugins/CEPlugin/` | DLL must be in CE's `autorun/` or `plugin/` folder. Confirm "AOBMaker" appears in CE's plugin list. |
| DiscreteUI | This repo, `dist/DiscreteUI.exe` | Single-file AOT build. |
| `Discrete-{IL2CPP,Mono}.dll` | This repo, `dist/` | Inject into the target Unity game via DiscreteUI's **Pick & inject…** button. |
| A Unity game | Your choice | Mono or IL2CPP, x64. See [test-matrix.md](test-matrix.md) for verified targets. |

The AOBMaker **CE plugin** opens a named pipe at `\\.\pipe\AOBMakerCEBridge`
on every CE launch. DiscreteUI's connection-bar shows a green
**"AOBMaker DLL plugin: connected"** chip when the pipe is reachable; grey
**"not connected"** otherwise. The chip refreshes on every successful
or failed bridge call (no polling).

> **Two separate AOBMaker integrations.** This chip is the **CE plugin**
> (`\\.\pipe\AOBMakerCEBridge`) — it pushes AA scripts / records into CE. A
> *second*, independent integration talks to the AOBMaker **standalone app**
> (`\\.\pipe\AOBMaker` → `GenerateAob`) for **unique** AOBs; it has its own
> **"AOBMaker App: connected/disconnected"** chip + ↻ refresh button. See
> [AOBMaker app integration](#aobmaker-app-integration--unique-aob-via-generateaob)
> below. Either, both, or neither may be running.

---

## End-user journey (the 5-click happy path)

1. **Launch Cheat Engine 7.6**. Open any table (an empty one is fine —
   `File → New Cheat Table`).
2. **Launch DiscreteUI**. The connection bar should turn green:
   `AOBMaker DLL plugin: connected`. (If it stays grey, see *Troubleshooting*.)
3. **Pick & inject** the target Unity game (existing button — Phase
   9.5). Wait for `Connected — build N`.
4. *(Optional but useful)*: browse to a class / call a method / run an
   address lookup so you have something to push to CE.
5. **Tools → "Inject Discrete Bridge into CE Table…"**. Embeds the **core**
   only — `utils.lua` + `unity_dump.lua` (inject the backend DLL + start the
   pipe) — plus the `Load Discrete Bridge` AA entry. The status banner ends
   with `Bridge ready (2 files + bootstrap AA) — enable "Load Discrete
   Bridge" in CE to activate`. Every other lua helper is embedded
   automatically by the action that needs it (see step 7) — you don't pick
   anything here.
6. **Switch to CE**, find the new **Load Discrete Bridge** entry in
   the address list, **toggle its checkbox**. Lua console prints
   `[Discrete] Load Discrete Bridge: ready — DLL build N`.
7. *(Now or later)* push individual features to CE:
   - **Methods grid** → right-click an overload → *"Create AA in CE"*
     (Phase 18c — AOB scan + registersymbol; works with or without
     the bridge loaded).
   - **MethodCallWindow** → fill in args → *"Send to CE Table…"*
     (Phase 22e — wraps `callUnityMethod(...)` so toggling the entry
     repeats the call).
   - **Address Lookup tab** → run a `whatIsAt` → *"Send to CE Table…"*
     (Phase 22e — registers the instance pointer as a CE symbol +
     adds a child memory record per resolved field offset).
8. **CE → File → Save** the `.CT`. Re-opening it in a fresh CE process
   recreates the same surface, provided the user re-runs Discrete's
   *Inject Discrete Bridge* (the table files survive the save; the
   bootstrap AA's `[ENABLE]` is the only thing that pulls the lua back
   in on a clean CE start).

That's the whole flow. Everything below is reference material.

---

## What lands in the CT (action-driven)

After step 5, `Table → Show table files` lists just the **core**:

```
utils.lua             (~6 KB)   ← core (bootstrap loads it)
unity_dump.lua        (~7 KB)   ← core (inject DLL + start pipe)
```

Each **Send to CE Table / Create AA** action then embeds the one lua helper
*its* generated AA self-loads, on demand — so the CT only ever carries the
lua its own entries actually use:

| Action | Embeds | The generated AA self-loads via `findTableFile` |
|---|---|---|
| MethodCall → *Send to CE Table* | `unity_invoke.lua`            | `callUnityMethod` |
| Methods → *Create AA in CE* (Mono JIT) | `unity_symbols.lua`     | `registerMethodSymbol` |
| Methods → *register (sig-match)* (DLL-free) | `mono_registersymbol_helpers.lua` | `discrete_mono_registerSymbolBySignatureMatch` |
| Address Lookup → *Send to CE Table* | *(none — pure asm symbol)* | — |

> **`register (sig-match)` is universal across Mono AND IL2CPP.** CE's
> "Activate Mono features" dissector drives the `mono_*` Lua API on *both*
> backends, so signature-match registration resolves the live method address on
> either — it is **not** Mono-only (the `mono_` prefix is a misnomer). It is the
> recommended register path: relaunch-safe *and* game-update-safe (resolves by
> class + method + signature, never a hardcoded address or baked module offset).
> The button surfaces for any connected Unity target; the only prerequisite is
> that the user enables **Activate Mono features** in CE for the game. Do **not**
> special-case IL2CPP with `GameAssembly.dll+offset` — a baked offset breaks the
> moment the game updates.

The UI-only tools (Structure Dissect, Scene explorer, reverse address
lookup) are **not** part of the bridge — DiscreteUI drives those over the
pipe in C#; an end-user with only a `.CT` never invokes their lua, so they're
never embedded.

### Backend DLLs go *beside the .CT*, not inside it

The DLLs are **not** embedded — ship them next to the distributed `.CT`
so they can be updated independently and stay off AV radar. Place all
three (the bridge picks the right one at runtime by backend + bitness via
`utils.lua`'s `M.dllPath`):

```
Discrete-Mono.dll        ← Mono, 64-bit
Discrete-IL2CPP.dll      ← IL2CPP, 64-bit (no 32-bit build exists)
Discrete-Mono-x86.dll    ← Mono, 32-bit (renamed from dist/x86/Discrete-Mono.dll
                            so it doesn't collide with the x64 name)
```

`M.dllPath` searches the `.CT`'s own folder first (then the CE-install
`discrete\` subfolder and `%LOCALAPPDATA%\Discrete_Dumper\`), checking
file existence at each, so a stale CE dialog path can't send it to the
wrong copy.

**Packaging helper.** `tools\Package-CTDist.ps1` assembles the
distribution folder for you — it copies the three DLLs out of `dist\`,
renaming `dist\x86\Discrete-Mono.dll` → `Discrete-Mono-x86.dll`, and can
drop your `.CT` in alongside:

```powershell
# Build all three first:
.\build.ps1 -Target DLL -Backend Both -Arch Both
# Then assemble a ready-to-zip folder:
.\tools\Package-CTDist.ps1 -Destination .\release\MyTrainer -CtFile .\MyTrainer.CT
```

A missing build (e.g. you only built Mono, or skipped `-Arch Both`) is
reported and skipped, not fatal — so a Mono-only or IL2CPP-only
distribution is one command too.

…and the address list gains one Auto-Assembler entry:

```
Load Discrete Bridge       [ ]
```

Each subsequent *Send to CE Table…* click adds one more entry. They all
prepend the bridge-loaded guard so toggling them before *Load Discrete
Bridge* prints a clear hint instead of silently failing.

### Teardown lifecycle (dormant only — no FreeLibrary)

**Disable `Load Discrete Bridge` → dormant.** The `[DISABLE]` block calls
`discreteShutdown()`, which invokes the DLL's `Discrete_Shutdown` export:
the pipe server thread is stopped + joined and the `runtime_invoke` MinHook
detour is removed. The DLL stays **mapped** — re-enabling is cheap, since
`unity_dump.lua`'s `main()` sees the module already mapped, skips
re-injection, and just restarts the pipe (no refcount growth across
toggles).

There is intentionally **no full-unload (FreeLibrary) entry.** Unmapping a
DLL that hooks the per-frame `runtime_invoke` can't be made crash-safe: for
an IL2CPP game the main thread is almost always inside the detour at the
moment of unmap, so `FreeLibrary` reliably CTDs the game. Dormant teardown
(hook removed, pipe stopped, ~1 MB of address space left mapped) is the
only teardown we expose — and it's sufficient for distribution: re-enable
to resume, or just close the game to fully reclaim everything.

---

## Navigation (nothing lands in the CT)

Two of the bridge's seven commands only *move CE's view*: they create no
memory record and leave the table untouched. Of the rest, four mutate the
open table and `CheckAvailability` just probes the pipe. Six UI actions
send these two.

| Wire command | Moves | UI actions that send it |
|---|---|---|
| `NavigateHexView` | Memory View, hex pane | Inspector row → **HEX** (the field's own address) · **HEX (ptr)** (what the pointer points at) |
| `NavigateDisassembler` | Memory View, disassembler pane | Browser Methods grid → *Go to method in CE* · Method Overloads window, per overload row · Field References → *Go to method head* · *Go to access address* |

The two Field-References entries are separate commands over one helper
(`GoToMethodHead` / `GoToAccessAddress` → `NavigateInCeAsync`): the first
jumps to the method's entry point, the second to the exact instruction that
touched the field.

Request shape is the smallest of the whole protocol — `type` plus a bare
hex address, no other fields:

```json
{"type": "NavigateDisassembler", "address": "7FF6A21C340"}
```

The address goes out as **bare hex**: `AobMakerBridge.StripHexPrefix` trims
a leading `0x`, so callers may pass either form. An empty address
short-circuits to `false` without touching the pipe.

Both use the standard 5 s `ResponseTimeoutMs`, not the 15 s
`InjectResponseTimeoutMs` that `InjectTableFile` needs.

Two failure modes are distinct and the UI reports them differently:

- **No address to go to.** Caught before the pipe. A Mono method that was
  never JIT-compiled, an abstract method, or a generic definition has no
  native entry — the caller says so ("no compiled body to jump to") and
  never sends a request. For Mono this is expected on first sight of a
  method; JIT it and the address appears.
- **CE not reachable.** `IsAvailable` is false, or the exchange fails.
  Every caller falls back to the same phrasing — "open CE with the AOBMaker
  plugin loaded, then retry". Note that `IsAvailable` is a *cached* result
  of the last exchange, so a stale true is possible; the request failing is
  what flips it back.

---

## Generated entry shapes

### Methods grid → "Create AA in CE" (Phase 18c)

```
[ENABLE]
{$lua}
if syntaxcheck then return end
-- WARNING: this AOB is the method prologue and is NOT guaranteed unique … (DLL pattern)
--   ─or─  -- NOTE: AOB generated by the AOBMaker app and verified unique … (app route)
…discrete_logScan helper (idempotent across scripts)…
{$asm}
// MainGame.PLY_WORK.AddGrowHpMax(System.Int32) → 0x7FF876123450
aobscanmodule(PLY_WORK_AddGrowHpMax_int32, GameAssembly.dll, 48 8B …)
registersymbol(PLY_WORK_AddGrowHpMax_int32)

[DISABLE]
{$asm}
unregistersymbol(PLY_WORK_AddGrowHpMax_int32)
```

No bridge-loaded guard — this is pure AOB and works standalone. The first
comment line after the `syntaxcheck` guard is an AOB-quality banner: a loud
"NOT unique — verify/adjust" warning for the DLL method-prologue pattern, or a
short "verified unique" note when the AOBMaker app produced the AOB (see
[AOBMaker app integration](#aobmaker-app-integration--unique-aob-via-generateaob)).
When a unique AOB starts *before* the method entry (Mode D extended it
backwards), the script scans into an `<sym>_aob` anchor and registers the real
method-entry symbol at `anchor + injectionOffset` in a `{$lua}` block.

### MethodCallWindow → "Send to CE Table…" (Phase 22e)

```
[ENABLE]
{$lua}
if syntaxcheck then return end
if not _G.discrete_bridgeLoaded then
  print('[Discrete] Enable "Load Discrete Bridge" first')
  return
end

local args = {
  argInt32(7),
}
local r = callUnityMethod(
  'Assembly-CSharp', 'MainGame', 'PLY_WORK', 'AddGrowHpMax',
  args,
  { signatureMatch = '(System.Int32)', instance = '0x000002201761E3D0' })
prettyPrint(r)

[DISABLE]
{$lua}
-- callMethod has no teardown — re-toggle to repeat the call
```

### Address Lookup → "Send to CE Table…" (Phase 22e)

Parent AA script:
```
[ENABLE]
{$lua}
if syntaxcheck then return end
if not _G.discrete_bridgeLoaded then
  print('[Discrete] Enable "Load Discrete Bridge" first')
  return
end

{$asm}
alloc(DiscreteAt_PLY_WORK_000002201761E3D0, 8)
registersymbol(DiscreteAt_PLY_WORK_000002201761E3D0)
DiscreteAt_PLY_WORK_000002201761E3D0:
dq 0x000002201761E3D0

[DISABLE]
{$asm}
unregistersymbol(DiscreteAt_PLY_WORK_000002201761E3D0)
dealloc(DiscreteAt_PLY_WORK_000002201761E3D0)
```

…with one child memory record per resolved field, e.g.:
- `HP @+0x10  (System.Int32, 4B)` — Address `DiscreteAt_PLY_WORK_…`,
  Offsets `[0x10]`, type `Dword`, signed.

---

## AOBMaker app integration (unique AOB via GenerateAob)

Separate from the CE-plugin bridge above, DiscreteUI can also talk to the
**AOBMaker standalone app**. When `AOBMaker.exe` is running it listens on
`\\.\pipe\AOBMaker` and exposes a `GenerateAob` command (spec:
`D:\Github\AOBMaker\docs\GenerateAob-Pipe-Command.md`) that turns a
module + address into a **unique** AOB (Mode D: it expands the pattern around
the seed instruction until it matches exactly once in the module).

**Why it matters.** The Methods-grid `Copy: aobscan` / `Push aobscan to CE`
actions otherwise use the DLL's *method-prologue* pattern, which is frequently
**non-unique** (many methods share opening bytes → the scan can land on the
wrong site). Routing through the app yields a verified-unique AOB and removes
the "adjust it yourself" caveat.

**Connection bar.** A second chip — **"AOBMaker App: connected / disconnected"**
— sits next to the DLL-plugin chip, with its own **↻** refresh button. It's
probed once at startup; click ↻ after starting `AOBMaker.exe` to re-check.

**How the routing works.**
- When the App chip is **connected**, both `Copy: aobscan` and `Push aobscan to
  CE` call `GenerateAob` per selected overload (IL2CPP / module-backed only).
  Expect a **few seconds** per overload (Mode D scans the whole module). On
  success the AA carries the unique AOB and the front banner becomes the
  *"verified unique via AOBMaker app"* note.
- When **disconnected** (or a generate fails, e.g. game unreadable / no unique
  pattern within the extend cap), it **falls back** to the DLL prologue pattern
  + the loud "NOT unique — verify/adjust" warning. Mono-JIT overloads always
  keep the bridge-resolve path (no module to AOB-scan).
- **Process targeting.** The app needs to know which process to scan; DiscreteUI
  resolves the **game PID** from the connected `\\.\pipe\DiscreteBfx` pipe's
  *server* end (`GetNamedPipeServerProcessId` — our backend DLL lives inside the
  game) and passes it as `processId`. If unknown, the app falls back to its own
  resolution (e.g. CE's attached process).

**Experimental gating.** The `Copy: register (sig-match)` button on the Method
Overloads window is now hidden unless **experimental features** are enabled
(footer checkbox). Its `Push register to CE ★` sibling is unaffected.

Client: [`AobMakerAppClient.cs`](../ui/UnityDumpUI.Core/Services/AobMakerAppClient.cs)
(same length-prefixed JSON wire format as the CE-plugin bridge; 30 s response
timeout for the Mode-D scan).

---

## Troubleshooting

### "AOBMaker DLL plugin: not connected" stays grey

- Confirm CE is **running** with the AOBMaker plugin installed
  (`Edit → Settings → Plugins` should list it).
- DiscreteUI's startup probe runs once at launch. Once CE comes up,
  you can either:
  - Click *Tools → Inject Discrete Bridge*; the very first step is a
    fresh `CheckAvailability` that flips the chip green if the pipe
    is now reachable.
  - Or click any AOBMaker-driven button (HEX/Disasm in Inspector;
    Create AA in CE in Methods); each successful or failed call
    updates the chip.

### "AOBMaker App: disconnected" stays grey

- This is the **standalone app** chip, not the CE plugin — confirm
  `AOBMaker.exe` (the GUI app) is actually running, then click the App **↻**.
- `Copy: aobscan` still works while disconnected; it just uses the DLL
  prologue pattern (with the "NOT unique" warning) instead of a unique AOB.

### `Copy: aobscan` produced the DLL pattern even though the App chip is green

- The app couldn't generate a unique AOB for that overload (game memory
  unreadable under usermode RPM for a strongly-protected target, or no unique
  pattern within Mode D's extend cap) and DiscreteUI fell back. The reason is
  logged under the `view` category. Mono-JIT overloads always use the
  bridge-resolve path (no module to AOB-scan) regardless of the app.

### Inject step succeeds but `Load Discrete Bridge` enable does nothing

- Open CE's Lua console (`Ctrl+Alt+L`) before flipping the checkbox.
  Errors print there, not in DiscreteUI.
- Most common cause: the `Discrete-{Backend}.dll` files aren't in
  `<CE-install>/discrete/`. The bootstrap calls `unity_dump.lua` →
  `injectDLL(<that path>)`. Either:
  - Inject the DLL via DiscreteUI's *Pick & inject…* first (preferred —
    DiscreteUI knows where its own DLL is), then enable the bridge;
  - Or copy the DLLs from `dist/` into `<CE-install>/discrete/`
    once. The proxy-deploy panel doesn't help here — that's for the
    proxy DLLs, not the dumper.

### `[Discrete] DLL build symbol not yet resolvable` after enable

The bridge loaded but `Discrete_GetBuildNumber` couldn't resolve via
CE's symbolhandler. Either:
- The DLL isn't injected (inject via DiscreteUI first); or
- CE's symbolhandler hasn't finished scanning the freshly-injected DLL.
  Wait a few seconds, then **disable + re-enable** the bridge entry.
  The bootstrap re-runs and the smoke usually passes the second time.

### "Send to CE Table" button is hidden

The button only appears when DiscreteUI has wired its session-wide
AOBMaker bridge into the spawning window. The MethodCallWindow opened
from `MainWindow`'s right-click path always wires it; the Address
Lookup button is wired by `MainWindowViewModel` at construction. If
the button is hidden in either place, file an issue with a log capture
(see below) — it likely means the wiring drifted.

### A bridge call shows `Stream size mismatch: wrote N, stream has 0`

AOBMaker plugin–side bug from before the upstream fix that switched
`InjectTableFile` from `Stream.write(string)` to
`createStringStream + copyFrom`. Update the AOBMaker DLL — see
[AOBMaker repo CHANGELOG](https://github.com/) for the relevant patch
date. Discrete's wire payload is the same shape regardless of plugin
version; you'll know you're hitting this if and only if the message
text matches verbatim.

### Re-enabling `Load Discrete Bridge` is a silent no-op

By design — `_G.discrete_bridgeLoaded` is set after the first run.
Disable + re-enable to pick up updated lua content. Toggling `[DISABLE]`
runs `discreteShutdown()` (stops the pipe + removes the hook, DLL stays
mapped) before clearing the flag, so re-enable restarts the pipe without
re-injecting. The DLL stays mapped for the process lifetime — see
*Teardown lifecycle* above for why there's no FreeLibrary unload.

---

## Manual smoke checklist

Run after any Phase 22 code change:

1. Build clean: `.\build.ps1 -Mode Publish`. Acceptance: 0/0 trim+AOT
   warnings, `xx/xx tests pass`.
2. Open CE 7.6 + a **new empty cheat table** (no surprise pre-existing
   files).
3. Launch the freshly-built `dist/DiscreteUI.exe`. Wait ~1 s. Verify
   the connection bar shows `AOBMaker DLL plugin: connected` (green).
4. *Tools → Inject Discrete Bridge into CE Table*. Status banner walks
   `(1/4) Checking AOBMaker plugin availability…` →
   `(4/4) Creating Load Discrete Bridge AA entry…` →
   `Bridge ready (2 files + bootstrap AA) …`. Total ≤ 2 s.
5. CE → `Table → Show table files`. Verify `utils.lua` + `unity_dump.lua`
   are present (the action helpers get embedded later, in steps 8–9).
6. CE address list shows `Load Discrete Bridge` entry. Enable it. Lua
   console prints `[Discrete] Load Discrete Bridge: ready — DLL build N`.
7. Inject the DLL via DiscreteUI's *Pick & inject…* (or skip if step 6
   already injected via the bootstrap). Browse a class.
8. Methods grid → right-click an overload → *Create AA in CE*. New
   memory record appears; enable it; CE Lua console prints `[discrete]
   <symbol> = <addr>`.
9. Methods grid → right-click → *Call method…* → fill an arg → *Send to
   CE Table*. New entry appears; enable it; Lua console prints the
   return value.
10. Address Lookup tab → paste a known instance address (typically from
    Inspector right-click → "Find what's at this address") → run lookup
    → *Send to CE Table*. New parent + child entries appear; enable
    parent; child shows the field's live value in CE's grid.
11. *File → Save* the table. Close CE entirely. Re-open the saved
    `.CT`. Re-enable *Load Discrete Bridge*. Verify all entries from
    steps 8–10 still enable cleanly without re-running the inject.

---

## PowerShell wire-only smoke

For a faster iteration loop that doesn't require a Unity game (just
CE + AOBMaker), use [`tools/Test-BridgeInject.ps1`](../tools/Test-BridgeInject.ps1).
It drives the AOBMaker pipe directly with stub payloads and reports
per-step success/failure. Useful when validating an AOBMaker plugin
update or a Discrete wire-format change in isolation.

```powershell
# Probe + minimal inject (1 file + 1 AA entry):
.\tools\Test-BridgeInject.ps1

# Full pipeline replay (mirrors the production InjectDiscreteBridge command):
.\tools\Test-BridgeInject.ps1 -Full
```

After the script reports OK, switch to CE: `Table → Show table files`
should list `_discrete_smoke.lua` (or the bridge files it pushed in `-Full`
mode); the address list should gain `Discrete: smoke probe` (or `Load
Discrete Bridge` in `-Full` mode). (Note: the production bridge now embeds
core only — the helper lua is added per action — so `-Full` is a wire smoke,
not an exact replica of the current inject set.)

---

## Architecture references

- Phase 22 plan + sub-phases: [`plan.md` § Phase 22](plan.md#phase-22--live-ct-population-via-aobmaker)
- AOBMaker plugin protocol: `D:\Github\AOBMaker\plugins\CEPlugin\src\protocol.h`
- AOBMaker app `GenerateAob` pipe spec: `D:\Github\AOBMaker\docs\GenerateAob-Pipe-Command.md`
- Discrete CE-plugin bridge client: [`AobMakerBridge.cs`](../ui/UnityDumpUI.Core/Services/AobMakerBridge.cs)
- Discrete AOBMaker-app client (GenerateAob): [`AobMakerAppClient.cs`](../ui/UnityDumpUI.Core/Services/AobMakerAppClient.cs)
- Embedded lua manifest: [`BridgeLuaResources.cs`](../ui/UnityDumpUI.Core/Services/BridgeLuaResources.cs)
- Bootstrap AA template: [`BootstrapAaScriptGenerator.cs`](../ui/UnityDumpUI.Core/Services/Export/BootstrapAaScriptGenerator.cs)
- Inject orchestrator: [`BridgeInjector.cs`](../ui/UnityDumpUI.Core/Services/BridgeInjector.cs)
- Per-feature generators:
  [`CallMethodAaScriptGenerator.cs`](../ui/UnityDumpUI.Core/Services/Export/CallMethodAaScriptGenerator.cs),
  [`AddressLookupAaScriptGenerator.cs`](../ui/UnityDumpUI.Core/Services/Export/AddressLookupAaScriptGenerator.cs)

### CE plugin SDK — background reading only

Discrete builds **no** CE plugin; it is a pipe client of AOBMaker's.
These two cover the peer's side of the fence and are reference material,
not something to implement against:

- `D:\Github\UE5CEDumper\docs\ce-plugin-api-reference.md`
  ([public mirror](https://github.com/bbfox0703/UE5CEDumper/blob/main/docs/ce-plugin-api-reference.md)) — plugin lifecycle,
  the `pluginsync` main-thread marshaling model, and CE's internal
  `TVariableType` ordinals. That last one is load-bearing here: it is where
  the "NOT SDK header order" caveat on `CeValueType` in
  [`AobMakerBridge.cs`](../ui/UnityDumpUI.Core/Services/AobMakerBridge.cs)
  comes from.
- `D:\Github\UE5CEDumper\docs\ce-plugin-sdk-notes.md`
  ([public mirror](https://github.com/bbfox0703/UE5CEDumper/blob/main/docs/ce-plugin-sdk-notes.md)) — the same ground read
  out of CE's Pascal rather than the C header, plus the `executeCodeEx`
  traps behind the "prefer pipe RPC" rule in
  [`lessons-learned.md`](lessons-learned.md).
