# Teleport (Unity) — feature spec / status

> **Status: wave 1 shipped, 待 live-smoke.** Ported in spirit from
> UE5CEDumper's Teleport tab, but re-architected for Unity's very different
> object model. See the porting assessment behind the design in the
> `project_ue5_teleport_port_assessment` memory.

## The core difference from UE

UE5CEDumper's teleport is **zero-config on any game** because Unreal bakes a
universal chain into engine base classes: `GWorld → GameInstance →
LocalPlayers[0] → PlayerController → Pawn → RootComponent.RelativeLocation`,
all resolvable by name.

**Unity has no gameplay framework** — no world singleton, no local player, no
controller, no pawn. "The player" is a game-authored `MonoBehaviour` on a
game-authored `GameObject`. So the two halves invert:

| | UE5 | Unity |
|---|---|---|
| **Find the player** (target acquisition) | universal (GWorld chain) | **inherently game-specific** — no universal chain |
| **Move the player** (write pos/rot) | game-specific | **universal** given a `Transform` pointer |

Discrete's answer: the **manipulation** half is a universal primitive; the
**target** is picked by the user with the tooling discrete already has
(findInstances / Address Lookup / Scene / Inspector — each exposes a
copy-address affordance). This is the opposite split from UE, and it's why the
Teleport tab has a "paste the player's instance address" box instead of a
one-click resolver.

## The unlock: value-struct `callMethod` marshaling

Unity's `Transform.position` / `rotation` are **native icall property
accessors** — the value lives in the native C++ transform, not managed fields,
so you can't poke a managed offset to move an object; you must invoke the
setter. That needs `callMethod` to pass/return a `Vector3` **by value**, which
Phase 19 couldn't do. Wave 1 added it (both backends):

- **Arg** `type: "value-struct"` with `bytes` (hex / byte array) — the struct's
  raw little-endian image; `params[i]` points at a padded 128 B backing buffer
  (a size mismatch can't OOB-read the trampoline).
- **Return** opt-in via `returnStruct:true` → `returnStructBytes` (hex) +
  `returnStructSize`, unboxed with the runtime's `class_value_size` (caller
  size-hint fallback).

Wire spec: [lua-invoke.md](lua-invoke.md) → "value-struct". This primitive is
general (any Vector3/Quaternion/Color-taking or -returning method), not
teleport-specific.

## Architecture (layers, all shipped)

```
DLL  callMethod value-struct arg + return marshaling (IL2CPP + Mono)
      ↑ Phase 20-MT main-thread dispatch already existed
C#   BackendAdapter.CallMethodAsync(returnStruct…) + CallArgFactory.Vector3/Quaternion
C#   TeleportService     resolve Transform → get/set position + eulerAngles + directional
C#   TeleportMarkerStore + TeleportMath (pure) ; TeleportController (save/recall/undo/coords/directional)
C#   TeleportCoordinateParser (copy/paste; "(1,2,3)" / "x y z" / 6-number)
UI   TeleportViewModel + Teleport tab (experimental-gated)
UI   WindowsGlobalHotkeyService (Win32 RegisterHotKey) → save/recall/undo hotkeys
Lua  argVector3 / argQuaternion / unityTeleportTo / unityGetPosition (unity_invoke.lua)
```

The engine hops (all `main_thread:true`, since Transform APIs are
main-thread-only):

- `Component.get_transform` (or `GameObject.get_transform`) → Transform pointer
- `Transform.get_position` / `get_eulerAngles` (returnStruct) → Vector3
- `Transform.set_position` / `set_eulerAngles` (value-struct arg)
- `Transform.get_forward` (returnStruct) — for directional teleport

`UnityEngine.CoreModule` is tried first, falling back to the monolithic
`UnityEngine` assembly on class-not-found.

## The Teleport tab

Experimental-gated (flip the footer checkbox) until live-verified. Sections:

- **Target** — paste a managed instance address (+ "GameObject" toggle) → Set
  Target resolves its Transform.
- **Current pose** — position + rotation, Refresh, Copy coords (clipboard).
- **Markers** — 3 slots (Save / Recall / Clear) + one-way **Undo last jump**
  (every jump auto-saves the pre-jump pose).
- **Teleport to coordinates** — accepts `(1,2,3)` / `x y z` / 6-number (with
  rotation) / pasted "Copy coords" output.
- **Directional** — distance + horizontal (ground-plane) toggle + Forward/Back.
- **Global hotkeys** — enable to fire while the game is focused: Save 1-3 =
  `Ctrl+Num1/2/3`, Recall 1-3 = `Alt+Num1/2/3`, Undo = `Ctrl+Num0` (NumLock ON).

Actions are disabled until a target resolves. Single-player only (online games
rubber-band / may flag teleports — the tab says so).

## Lua

```lua
local inst = "0x1F8CEA0BE0"          -- the player MonoBehaviour, e.g. from findInstances
unityTeleportTo(inst, 100, 20, -35)  -- resolve Transform + set_position
local x, y, z = unityGetPosition(unityResolveTransform(inst))
```

`argVector3` / `argQuaternion` / `argValueStruct` build value-struct args for
any managed method; `decodeStructFloats(result, n)` reads a value-struct return.
Requires CE 7.5+ (Lua 5.3+, for `string.pack`).

## Known Unity-specific limits (by design, not bugs)

- **No universal player resolver** — the user picks the target. The cached
  Transform pointer can go stale (GC move / despawn); re-run Set Target.
- **Velocity reset / "stop"** is game-specific (Rigidbody vs CharacterController
  vs NavMeshAgent vs custom) — not attempted in wave 1, so a physics/CC mover
  may snap the pawn back. (v2.)
- **Scene-name mismatch guard** deferred (UE's map check) — markers store
  coordinates only.
- **Managed Transform is a native peer** — a managed-field write won't move the
  object; the setter path is mandatory.

## 待 live-smoke (needs a running Unity game)

| # | Check | Pass sign |
|---|---|---|
| 1 | Mono + IL2CPP: set target → Refresh shows sane position | matches the in-game location |
| 2 | Save marker → move in-game → Recall | pawn returns to the saved spot |
| 3 | Undo after a bad jump | returns to the pre-jump pose |
| 4 | Teleport to coordinates (copy → paste round-trip) | lands exactly |
| 5 | Directional Forward/Back, horizontal on/off | moves along facing / on ground plane |
| 6 | Global hotkey with the GAME focused (NumLock on) | Ctrl+Num1 saves; Alt+Num1 recalls |
| 7 | Rotation restore on recall | facing restored (where the game doesn't override it) |

## v2 backlog

- Cursor raycast teleport (`Camera.main.ScreenPointToRay(Input.mousePosition)` +
  `Physics.Raycast`) — needs Ray/RaycastHit value-struct args+returns.
- Camera POV read (`Camera.main` transform + fieldOfView).
- Best-effort settle: `Rigidbody.velocity=0` (+ `linearVelocity` 2023 rename) /
  `CharacterController` disable-write-enable.
- Scene-name mismatch guard; marker persistence across sessions.
- One-click "Send to Teleport" handoff (Inspector/Scene right-click).
- Per-action hotkey rebinding UI + persistence.
- Movement knobs reframed as generic field-write + `Physics.gravity`.
