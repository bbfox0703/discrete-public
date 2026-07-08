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

Wave 1's live feedback: manual target-picking is the tab's adoption blocker —
a user who hasn't already reverse-engineered the game has no way to know WHICH
instance is the player. The answer is the **auto-detect candidate scorer**
below: not a universal chain (Unity still has none), but a multi-signal
best-effort resolver built entirely from verified-surviving engine APIs.

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

## Auto-detect player (target acquisition, wave A)

`PlayerLocator` (UnityDumpUI.Core) is a multi-signal candidate scorer — pure
composition over `BackendAdapter` (same idiom as `SceneEnumerator`), **zero DLL
changes**. Every signal degrades gracefully: a stripped / missing API just
means that signal contributes no candidates.

### Signals

| # | Signal | How | Base score | Notes |
|---|--------|-----|-----------|-------|
| A | Camera anchor | `Camera.get_main()` → transform → position; walk `Transform.get_parent` chain (≤ 8 levels) | 60 per ancestor | The camera sits at/near the player in almost every 3D game; FPS + many TPS rigs parent the camera under the player root. `Camera.get_main` survives IL2CPP stripping (verified live, see lessons-learned). Camera position also anchors distance scoring. |
| B | Tag `"Player"` | `GameObject.FindWithTag("Player")` | 100 | `Player` is a built-in tag — authoritative when the game assigns it. Stripped on many IL2CPP builds (SaGa: count=0); any error → signal unavailable. Mono always keeps it. |
| C | CharacterController | `Type.GetType("UnityEngine.CharacterController, UnityEngine.PhysicsModule")` (monolithic fallback) → `FindObjectsByType`/`FindObjectsOfType` probe chain (same 3-attempt list as SceneEnumerator) | 80 | Games that use a CharacterController almost always have exactly one — the player. |
| D | Well-known GameObject names | `GameObject.Find(name)` over ~14 common names (`Player`, `PlayerArmature`, `Character`, …) | 70 exact-"Player" / 50 others | `GameObject.Find(string)` survives stripping (verified). Active objects only. |
| E | User-class name heuristic | `getAssemblies` → `getClasses` on non-engine assemblies → player-ish class-name filter (cap ~16, soft-excluding `*Manager`/`*Settings`/… ) → `findInstances(aliveOnly, sane)` → `Component.get_transform` per instance | 50 (exactly 1 live instance) / 30 (2–3) | The class list is an offline pipe RPC — no main-thread cost until transform resolution. Obfuscated builds get nothing here (wave B's movement correlation is the counter). |
| E′ | Player-singleton field walk (**diagnostic only**) | when a player-named singleton (exactly 1 instance) **isn't a Component** (get_transform fails), `walkInstance` it and **log** its field shape (Transform/GameObject/Vector3 fields point at the real object it drives) | none (logs only) | An instance whose get_transform fails is very likely a `findInstances` **false positive**, so its field pointers are garbage — invoking any engine method (get_transform/get_gameObject) on a garbage `this` **hangs the game** (learned live on Utawarerumono). So this NEVER surfaces a candidate or dereferences a field pointer; it only records the shape (field names/types are reliable class metadata) for follow-up. Capped (`MaxSingletonFieldWalks`). |

Every candidate resolves to a **Transform address** + GameObject name; then
distance-to-camera is scored on top (+30 < 2 m, +20 < 10 m, +10 < 30 m).
Duplicates (same Transform) merge and their scores sum **across signal
families** (max within a family, so `Find("Player")`/`Find("player")` aliases
don't double-count) — CharacterController *plus* near-camera beats any single
name hit. The ranked list shows Confidence / Score / Name / Distance / Sources;
**Use** feeds the Transform address through the normal Set Target path
(`Component.get_transform` on a Transform returns itself, so the target is
revalidated for free).

**Camera-rig guard (live-fix, 2026-07-07).** First live test (Utawarerumono,
IL2CPP) had *only* the camera chain fire — every other signal came up empty —
so auto-detect confidently returned the camera's parent (a camera rig), not the
player. Three corrections keep that from reading as a real hit:

- **Camera chain is weak evidence** — base score dropped 60 → 40; it's a hint,
  not a primary signal.
- **No circular distance bonus** — the distance-to-camera bonus is suppressed
  for a candidate whose *only* source is the camera chain (it sits next to the
  camera by construction, so bonusing it just guaranteed it won).
- **Camera-rig name demotion** — a candidate whose GameObject name reads like a
  rig (`camera`/`cam`/`cinemachine`/`vcam`/`rig`/`pivot`/`holder`/`boom`/`dolly`/…)
  takes a −45 penalty, sinking it below any real player signal.

**Confidence.** Each candidate carries `PlayerConfidence` High/Medium/Low.
Strong signals (tag / CharacterController / single-instance user class) or ≥2
agreeing families → **High**; a lone name hit → **Medium**; camera-chain-only or
a camera-rig name → **Low**. When the top pick is **Low**, the UI stops
pretending — it shows the "this looks like the camera, not the player; use
Detect by movement or pick manually" guidance instead of a false "found it".

### Cost & failure modes

- ~30–90 main-thread icalls ≈ a few seconds at the dispatcher's 5–15 ms/call.
- The run aborts early with the focus hint when the **first** camera call hits
  the main-thread drain timeout (`runInBackground=false` + game unfocused —
  same failure mode as the Scene tab, same remedies).
- 0 candidates → status message points back to the manual paste box. Games
  where the player isn't one Transform (DOTS/ECS, vehicle swap, custom managed
  Vector3 fields) still need the manual path.

## Detect by movement (wave B — shipped 2026-07-07)

The robust fallback for games where **no static signal fires** (SRPG/VN,
console ports, obfuscated builds — Utawarerumono is the canonical case). The
player is whatever the user controls, so: sample every scene object's position,
let the user move, sample again — **the mover is the player**. Name-free and
tag-free.

Two-phase, so the UI can run the countdown in between:

1. `BeginMovementSampleAsync` — build the pool and record start positions. Pool =
   the five static signals' hits ∪ the camera ∪ a capped
   `FindObjectsByType(Transform)` sweep (default cap 600; each pooled transform
   costs one `get_position` per pass). Returns a `MovementSample` (the tracks +
   whether the sweep was truncated).
2. UI countdown — "**MOVE YOUR CHARACTER NOW — Ns left**" for
   `MovementSampleSeconds` (default 4 s, clamped 2–20).
3. `FinishMovementSampleAsync` — re-read every tracked position, rank by
   displacement. The camera transform is excluded (view rotation moves it);
   movers below `MinMoveDistance` (0.2 u) are dropped. Top movers are enriched
   with names and returned **High**-confidence (a rig-named mover — e.g. a
   follow-cam that tracks the player — is demoted to Medium).
4. **Resolve to root** (`ResolveToRoot`, on by default). An animated character
   exposes its *skeleton*, so the raw top movers are the leaf bones that swing
   MOST — `env_bone:skirt`, `neckless`, `beltL5`, `RightFoot` — because their
   displacement is locomotion **+** secondary animation. Teleporting a bone just
   deforms the mesh. So after ranking, the top movers' `get_parent` chains are
   walked and the **shared-ancestor ladder** (deepest common ancestor → root) is
   surfaced as the primary candidates (High, scored above the bones). The DCA is
   the tightest subtree containing all the moving parts = the character / armature
   root; the ladder rungs above it are the climb toward the character's controller
   GameObject. The user picks the rung whose name reads like their character; the
   leaf bones stay below as evidence. This is what made the first Utawarerumono
   movement run return `env_bone:*` bones instead of a teleportable target.

   Hardening (adversarial review): (a) resolution runs only when **≥2** movers
   share a root — a lone mover is left as its own row (it's usually the object
   itself, not a skeleton); (b) leaf movers are excluded from the ladder so the
   top rung is never a swinging bone (an *internal* mover that is an ancestor of
   another mover stays eligible — it may be a moving character root); (c) the
   cluster is **anchored on the highest-displacement mover** (the object the user
   deliberately moved), not on whichever hierarchy has the most sampled bones, so
   a heavily-animated NPC can't outvote the player; (d) a `get_parent` *error* is
   distinguished from a genuine `0x0` root, and the errored chain is dropped
   rather than seeding a false cluster. **Caveat:** when several characters are
   parented under one shared container GameObject, the movers converge only at
   that container, giving a one-rung ladder — surfaced at **Medium** confidence
   (teleporting a container moves everything under it; pick a different target).

   **Character-root detection (name-based).** The DCA is only the tightest
   ancestor of the *sampled* bones — an upper-body sample (eyelid + spine + pinky)
   converges at a spine bone, not the character. But a skeleton names its bones
   with a shared prefix (`env_bone:`) or joint words (`Hips`, `Spine`, `GlobalSRT`,
   `Bip01 …`), so the ladder climbs through bones until it hits the character
   GameObject (live example: `env_bone:Spine1 → env_bone:Hips → env_bone:GlobalSRT
   → P_Chara_00109@fld → AttachPoint`). The first rung whose name **breaks** the
   bone prefix / isn't a joint word (`P_Chara_00109@fld`) is the real teleport
   target and is pulled to **#1**; its container ancestors follow; the skeleton
   bones drop to **Medium** below it. (`root` is deliberately *not* a joint word —
   it appears in both `Bip01Root` bones and `CharacterRoot` GameObjects.) When no
   rung breaks the bone pattern, the DCA-first ladder is kept as the fallback.

**Honest limits.** The sweep is capped, so on a huge scene (Utawarerumono has
~15 k GameObjects) the player might be outside the sample — the status line
says so and points at raising the cap / moving a larger distance. A displacement
tie (player + follow-cam move together) surfaces both; the user picks.

### Wave B still-backlog

- **Cinemachine `Follow` target** — near-authoritative when the game ships
  Cinemachine (`CinemachineVirtualCamera.Follow` / CM3 `CinemachineCamera`
  tracking target IS the player transform).
- **Camera follow-script field walk** — walkInstance the main camera's
  components for a Transform-typed field named `target`/`follow`/`player`.
- **Proximity-prioritised sweep** — a DLL-side batch position read would let the
  movement sweep cover the whole scene instead of a cap (native transform layout
  is version-fragile — deferred).

## The Teleport tab

Experimental-gated (flip the footer checkbox) until live-verified. Sections:

- **Target** — **Auto-detect** runs the static PlayerLocator scorer;
  **Detect by movement** (for `N` s) samples the scene while you move your
  character and ranks by displacement. Both list ranked candidates
  (Confidence / Score / Name / Distance / Sources; Use → sets target). Or paste
  a managed instance address (+ "GameObject" toggle) → Set Target.
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

- **No universal player resolver** — auto-detect is a best-effort scorer, not
  a guarantee; the manual paste box stays as the fallback. The cached Transform
  pointer can go stale (GC move / despawn); re-run Auto-detect / Set Target.
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

- Auto-detect wave B — movement correlation / Cinemachine Follow /
  follow-script field walk (see the wave B list above).
- Cursor raycast teleport (`Camera.main.ScreenPointToRay(Input.mousePosition)` +
  `Physics.Raycast`) — needs Ray/RaycastHit value-struct args+returns.
- Camera POV read (`Camera.main` transform + fieldOfView).
- Best-effort settle: `Rigidbody.velocity=0` (+ `linearVelocity` 2023 rename) /
  `CharacterController` disable-write-enable.
- Scene-name mismatch guard; marker persistence across sessions.
- One-click "Send to Teleport" handoff (Inspector/Scene right-click).
- Per-action hotkey rebinding UI + persistence.
- Movement knobs reframed as generic field-write + `Physics.gravity`.
