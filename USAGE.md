# Discrete — Common Workflows

> Recipe-style companion to the main README (`dist/README.html` after
> unzip, or `scripts/README.md` in the repo). This file focuses on
> "how do I do X" for CT makers — for *what it is*, *what you need*,
> *how to deploy*, and *how to debug*, see the README first.

---

## Quick orientation

`DiscreteUI.exe` is the developer GUI for authoring `.CT` files
against Unity targets. You launch it, inject into the running Unity
game, browse the live managed type tree, generate addresses / AOB
scripts / structure dissects, and paste them into your CT. End users
of your finished `.CT` never see `DiscreteUI.exe` — they get the
silent bridge (Mode B from the README).

---

## Recipe 1 — Add a live class field to your CT

1. Open the Unity game in CE first.
2. Launch `DiscreteUI.exe` → **Pick & inject** → double-click the
   Unity process row. UI auto-connects.
3. **Fields** tab → pick assembly → pick class → tick the fields you
   want.
4. **Inspector** tab → click **Find instances** → pick the live
   instance from the list.
5. Right-click any decoded field row → **Copy CE field** (single
   field) or **Copy CE XML rooted here** (the whole sub-tree).
6. In CE, paste into the address list. Save the CT.

---

## Recipe 2 — Build an AA script that survives game updates

Hard-coded addresses break the first time the game patches. AOB
patterns survive most updates.

1. **Methods** tab → right-click an overload → **Find overloads…**.
2. In the modal, tick the row(s) you want patterns for.
3. Click **Push aobscan to CE** (requires the AOBMaker CE plugin
   loaded — see README *System requirements*).
4. Your CT's address list now contains a ready-to-activate AA
   script with the AOB pattern + `registersymbol` lines pre-filled.
   The symbol becomes available in CE under
   `Unity:{Asm}:{Ns}.{Cls}.m_{Method}__{ParamSuffix}`.

For static fields, **Fields** tab → right-click the static row →
**Copy registerSymbol script** for the same workflow without AOB.

---

## Recipe 3 — Reverse-engineer a "find what writes" hit

CE's *Find what writes to this address* gives you an address but
doesn't tell you which managed field it is.

1. Copy the address from CE.
2. DiscreteUI → **Address Lookup** tab → paste the address → **Lookup**.
3. The result panel shows instance + class + the specific declared
   field that contains the address (or the nearest field if it's an
   interior offset).
4. Click **Inspect this instance** to cross-tab into the Inspector
   and see live values.

---

## Recipe 4 — Call a Unity method from your CT

```lua
-- In the CT's autorun or activate script:
dofile(getCheatEngineDir() .. 'discrete\\unity_invoke.lua')

-- Static call:
local r = callUnityMethod(
    'Assembly-CSharp', 'MyNs', 'MyClass', 'MyMethod',
    { argInt32(42), argString("hello") },
    { signatureMatch = '(System.Int32,System.String)' })
-- r.ok, r.returnValue, r.exception

-- Instance call:
local r = callUnityMethod(
    'Assembly-CSharp', '', 'Inventory', 'AddItem',
    { argInt32(itemId), argInt32(count) },
    { instance = '0x16F8CEA0BE0',
      signatureMatch = '(System.Int32,System.Int32)' })

-- Engine icall (e.g. Transform.get_position) — flip main_thread = true
-- so it runs on Unity's main thread and doesn't freeze the game:
local r = callUnityMethod(
    'UnityEngine.CoreModule', 'UnityEngine', 'Transform', 'get_position',
    {},
    { instance = '0x...', main_thread = true })
```

Arg builders for every wire type:
`argNull` / `argBool` / `argByte` / `argSByte` / `argInt16` /
`argUInt16` / `argInt32` / `argUInt32` / `argInt64` / `argUInt64` /
`argFloat` / `argDouble` / `argChar` / `argString` / `argAddress`.

**GC caveat**: a returned managed pointer is valid until the next GC
cycle. Don't stash it for later use — chain follow-up calls
immediately when correctness matters.

---

## Recipe 5 — Edit a field value live (test before scripting)

1. Inspector → walk to the field you want to modify.
2. Right-click the row → **Write value…**.
3. Enter the new value, click Apply. CE's address-list refreshes
   automatically.

Useful for finding "what does this field control?" before committing
the change to your CT.

From CE Lua:
```lua
dofile(getCheatEngineDir() .. 'discrete\\unity_invoke.lua')
setField('0x000001E55B2518F0', 'int32', 999)
-- or, by class+offset:
setInstanceField('0x...', 0x10, 'byte', 5)
```

---

## Recipe 6 — Find a value when you don't know which class

You see HP=100 on the screen; you want the class+field.

1. **Value Search** tab → Type: `int32`, Value: `100`, click
   **First Scan**.
2. Take damage in-game (HP becomes 95).
3. Next Scan: `decreased` → click **Next Scan**.
4. Result narrows to a handful of candidate fields. The class +
   field name shown in the grid is your answer.

For float values that display as integers (e.g. HP shown as "32"
but stored as 31.7), the default tolerance is 0.5 — same semantics
as CE's "Rounded (default)" float-exact match.

For strings: Type: `string`, encoding: `utf16` (Unity-managed) /
`utf8` / `ascii`, match mode: `contains` / `exact`.

---

## Recipe 7 — Generate a CE Structure Dissect

CE's *Structure Dissect* tool wants a structure definition. Discrete
can generate one straight from the live class layout.

From the UI:
1. Pick the class in the Browser.
2. **Export options ▾** → **Export .CSX** (Structure Dissect format).
3. In CE: *Memory Browser → Tools → Dissect data → Open file*.

---

## Recipe 8 — Embed the bridge into your CT (zero-install for users)

Mode B from the README. The shortcut from inside DiscreteUI:

1. Open your `.CT` in CE.
2. Make sure AOBMaker plugin is loaded (status bar shows *AOBMaker:
   connected*).
3. DiscreteUI's **Tools** menu → **Inject Discrete Bridge into CE
   Table**.
4. The wizard drops the backend DLL + needed Lua scripts into the
   table's `<Files>` section.
5. Save the CT in CE.
6. Add this to your `[ENABLE]` block:
   ```lua
   local discrete = require 'ce_table_bootstrap'
   discrete.bootstrap()
   ```
7. Test by closing + reopening the CT and clicking your script's
   checkbox — bridge auto-loads, AA scripts work as before.

---

## Troubleshooting hints

For full debug procedure (log file paths, PowerShell harnesses, what
to include in a bug report), see the README's *Debugging & bug
reports* section.

Quick checks specific to common workflow issues:

- **Inject fails on a 32-bit Unity game** — DiscreteUI auto-detects
  and routes through `dist\x86\inject-x86.exe`. Make sure the `x86\`
  folder ships next to `DiscreteUI.exe`.
- **AOBMaker says "not connected"** — open CE first, confirm the
  plugin is loaded under CE → Edit → Settings → Plugins.
- **Scene tab is empty** — Unity stripped the enumeration APIs on
  that build (`FindObjectsOfType` / `FindObjectsByType`). Use Symbol
  Finder for a class-based starting point instead.
- **Method invocation freezes the game** — for engine icalls
  (`Transform.*`, `Object.FindObjects*`, etc.) you must pass
  `main_thread = true`. See Recipe 4.
- **Value Search finds 5000 hits in compiler-generated arrays** —
  the noise filter usually catches `<PrivateImplementationDetails>`
  and similar synthetic types. If a real hit is being squeezed out,
  raise the result limit on the Value Search tab. The per-instance
  cap (3 candidates per instance) is hard-coded to prevent one large
  array from dominating results.

---

## Credits

Built by **bbfox** — [opencheattables.com](https://opencheattables.com)
