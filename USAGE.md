# DiscreteUI — Usage Guide

A live Unity dumper for Cheat Engine table makers. Works on both
**Mono** (x86 / x64) and **IL2CPP** (x64) games. Generates CE
address-list XML, structure dissect (CSX), and AA scripts that push
straight into a currently-open `.CT` file.

---

## Requirements

- Windows 10 / 11
- Cheat Engine **7.6** or newer
- [AOBMaker](https://github.com/bbfox0703/AOBMaker) plugin loaded in CE
  (required for the *Push to CE* / *Inject Bridge* actions)

## Supported targets

| Backend | Bitness | Unity versions |
|---------|:-------:|----------------|
| Mono (`mono.dll`, `mono-2.0-bdwgc.dll`) | x86 + x64 | 5.x → 6.x |
| IL2CPP (`GameAssembly.dll`) | x64 | metadata v24 / v27 / v29 / v31 / v35 – v39 |

---

## Quick start

1. Unzip `dist\` to any folder.
2. Launch the Unity game.
3. Run `DiscreteUI.exe`.
4. Click **Inject and Connect** → pick the game's process.
5. Browse **Assemblies → Classes → Fields / Methods** in the three-pane
   layout.

> The dev-tool reminder in the top bar means *DiscreteUI is for you, the
> CT maker*. End users of your finished `.CT` do **not** need it — see
> *Deployment modes* below.

---

## Main panels

| Panel | What it gives you |
|-------|-------------------|
| **Assemblies / Classes / Fields / Methods** | Browse the live managed type tree. |
| **Inspector** | Pick a live instance, walk pointer chains, read/write field values. |
| **Methods** | Find every overload of a managed method; copy address / AOB / AA script. |
| **Address Lookup** | Paste any address from CE's scanner — DiscreteUI tells you which class + field it is. |
| **Symbol Finder** | Substring search across every class in an assembly (or all assemblies) for fields + methods. |
| **Scene** | List every `GameObject` in the active scene, filter by name, drill children, hand off to Inspector. |

---

## Common workflows

### Add a value to your CT

1. Browser → find the class.
2. Right-click a field → **Find instances**.
3. Inspector right-click the row → **Copy CE field** (or *Copy CE XML
   rooted here* for a whole sub-tree).
4. Paste into your CT's address list.

### Build an AA script that survives game updates

1. Methods tab → right-click an overload → **Find overloads…**.
2. Tick the row(s) you want → **Push aobscan to CE**.
3. CE's address list gets a ready-to-activate AA script with the AOB
   pattern + `registersymbol` lines already filled in.

### Call a Unity method from Lua

1. From the Methods grid: right-click → **Call method…** to interactively
   pick args + see the return value.
2. From a CT: `dofile([[..\unity_invoke.lua]])` then
   `callMethod('Assembly-CSharp', 'MyNs', 'MyClass', 'MyMethod', { instance=addr, args={...} })`.
3. Engine icalls (`Transform.get_position`, `Object.FindObjectsByType`,
   etc.) — flip `main_thread = true` so they run on Unity's main thread
   without freezing the game.

### Edit a field value live

1. Inspector → right-click a primitive row → **Write value…**.
2. Type the new value, hit Apply. CE's address-list refreshes
   automatically.

---

## CE Table deployment modes

You will probably never ship `DiscreteUI.exe` to end users. Two cleaner
options:

### Mode A — keep DiscreteUI for yourself

You use DiscreteUI to *author* the CT (generate addresses, AOB scripts,
struct dissects). Your finished `.CT` ships **without** the dumper —
end users just open the table.

### Mode B — embed the bridge inside your `.CT`

Use **Tools → Inject Discrete Bridge** while your CT is open in CE.
DiscreteUI drops the backend DLL + Lua scripts straight into the
table's `<Files>`. Save in CE. The CT is now self-contained: when an
end user opens it, the bridge auto-loads and your AA scripts work
without any external dependency.

> Mode B is the one most CT makers want. The bridge is silent — no UI
> pops up on the end user's machine.

---

## Troubleshooting

- **Inject fails on a 32-bit Unity game** — DiscreteUI auto-detects and
  routes through `dist\x86\inject-x86.exe`. Make sure the `x86\` folder
  ships alongside `DiscreteUI.exe`.
- **AOBMaker says "not connected"** — open CE first, confirm the
  AOBMaker plugin is loaded (CE menu → Edit → Settings → Plugins).
- **Scene tab is empty** — Unity has stripped the enumeration APIs on
  that build. Try the Symbol Finder for a class-based starting point
  instead.

---

## Credits

Built by **bbfox** — [opencheattables.com](https://opencheattables.com)
