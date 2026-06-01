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

The AOBMaker plugin opens a named pipe at `\\.\pipe\AOBMakerCEBridge`
on every CE launch. DiscreteUI's connection-bar shows a green
**"AOBMaker: connected"** chip when the pipe is reachable; grey
**"not connected"** otherwise. The chip refreshes on every successful
or failed bridge call (no polling).

---

## End-user journey (the 5-click happy path)

1. **Launch Cheat Engine 7.6**. Open any table (an empty one is fine —
   `File → New Cheat Table`).
2. **Launch DiscreteUI**. The connection bar should turn green:
   `AOBMaker: connected`. (If it stays grey, see *Troubleshooting*.)
3. **Pick & inject** the target Unity game (existing button — Phase
   9.5). Wait for `Connected — build N`.
4. *(Optional but useful)*: browse to a class / call a method / run an
   address lookup so you have something to push to CE.
5. **Tools → "Inject Discrete Bridge into CE Table…"**. A picker opens —
   tick which optional tools to embed (Structure Dissect / Symbols /
   Address Finder / Invoke / Scene). **Core** (DLL inject + pipe:
   `utils.lua` + `unity_dump.lua`) is always included. Click **Inject**;
   the status banner walks the steps in ~1 s and ends with
   `Bridge ready (N files + bootstrap AA) — enable "Load Discrete
   Bridge" in CE to activate` (N = core + your ticks). Only the picked
   files are embedded, and the bootstrap AA loads exactly that subset.
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

## What lands in the CT

After step 5, `Table → Show table files` lists the **core + picked**
files. With every feature ticked you get all of them (`mono_registersymbol_helpers.lua`
rides along with the Symbols feature):

```
utils.lua             (~6 KB)   ← core (always)
unity_dump.lua        (~7 KB)   ← core (always)
unity_dissect.lua     (~9 KB)   ← Structure Dissect
unity_symbols.lua     (~15 KB)  ← Symbols
unity_addrfind.lua    (~14 KB)  ← Address Finder
unity_invoke.lua      (~18 KB)  ← Invoke
unity_scene.lua       (~36 KB)  ← Scene
```

Untick a feature and its file(s) are simply absent — the bootstrap AA's
`BRIDGE_FILES` list is generated over the same subset, so it never asks
`findTableFile` for something the CT doesn't carry.

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

## Generated entry shapes

### Methods grid → "Create AA in CE" (Phase 18c)

```
[ENABLE]
{$lua}
…discrete_logScan helper (idempotent across scripts)…
{$asm}
// MainGame.PLY_WORK.AddGrowHpMax(System.Int32) → 0x7FF876123450
aobscanmodule(PLY_WORK_AddGrowHpMax_int32, GameAssembly.dll, 48 8B …)
registersymbol(PLY_WORK_AddGrowHpMax_int32)

[DISABLE]
{$asm}
unregistersymbol(PLY_WORK_AddGrowHpMax_int32)
```

No bridge-loaded guard — this is pure AOB and works standalone.

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

## Troubleshooting

### "AOBMaker: not connected" stays grey

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
   the connection bar shows `AOBMaker: connected` (green).
4. *Tools → Inject Discrete Bridge into CE Table*. In the picker, leave
   **all features ticked** for this full smoke. Click *Inject*. Status
   banner walks `(1/10) Checking AOBMaker plugin availability…` →
   `(10/10) Creating Load Discrete Bridge AA entry…` →
   `Bridge ready (8 files + bootstrap AA) …`. Total ≤ 2 s. (Tick fewer
   features and the step/file count drops accordingly.)
5. CE → `Table → Show table files`. Verify the `*.lua` files for the
   ticked features are present, sizes match the dist files
   (`Get-ChildItem dist\*.lua`).
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
should list `_discrete_smoke.lua` (or all 8 bridge files in `-Full` mode); the
address list should gain `Discrete: smoke probe` (or `Load Discrete
Bridge` in `-Full` mode).

---

## Architecture references

- Phase 22 plan + sub-phases: [`plan.md` § Phase 22](plan.md#phase-22--live-ct-population-via-aobmaker)
- AOBMaker plugin protocol: `D:\Github\AOBMaker\plugins\CEPlugin\src\protocol.h`
- Discrete bridge client: [`AobMakerBridge.cs`](../ui/UnityDumpUI.Core/Services/AobMakerBridge.cs)
- Embedded lua manifest: [`BridgeLuaResources.cs`](../ui/UnityDumpUI.Core/Services/BridgeLuaResources.cs)
- Bootstrap AA template: [`BootstrapAaScriptGenerator.cs`](../ui/UnityDumpUI.Core/Services/Export/BootstrapAaScriptGenerator.cs)
- Inject orchestrator: [`BridgeInjector.cs`](../ui/UnityDumpUI.Core/Services/BridgeInjector.cs)
- Per-feature generators:
  [`CallMethodAaScriptGenerator.cs`](../ui/UnityDumpUI.Core/Services/Export/CallMethodAaScriptGenerator.cs),
  [`AddressLookupAaScriptGenerator.cs`](../ui/UnityDumpUI.Core/Services/Export/AddressLookupAaScriptGenerator.cs)
