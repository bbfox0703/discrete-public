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

## Movement-driver-aware write (2026-07-08)

Unity moves characters four ways, and a plain `Transform.set_position` only
works for the first:

| Driver | Write path |
|---|---|
| **Transform** (script sets `position`) | `set_position` ✅ (default) |
| **CharacterController** (`.Move`/`.SimpleMove`) | disable the controller → `set_position` → re-enable |
| Rigidbody physics | (future) `Rigidbody.position`/`MovePosition` + sleep |
| NavMeshAgent | (future) `NavMeshAgent.Warp` |

A `CharacterController` **re-drives its GameObject's transform every frame**, so
writing the transform while it's enabled just gets fought — the character
orbits / snaps back (the live Utawarerumono symptom). `TeleportService` is now
**driver-aware**:

- `DetectMovementDriverAsync(transform)` — resolve
  `Type.GetType("UnityEngine.CharacterController, …")` (cached), then walk the
  target + ancestors (`GetComponent` + `get_parent` per rung — `GetComponentInParent`
  is stripped on many IL2CPP builds and even where present returns the *nearest*
  match) for the **OUTERMOST** CharacterController. The target is **retargeted to
  that controller's OWN transform** (`Component.get_transform(cc)`).
  - *Nearest, not outermost* (live Utawarerumono): a character can nest several
    CharacterControllers — `HigherRig(CC) → LogicalRoot(CC) → Model(CC) → bones` —
    and only the **middle** logical root is the correct teleport target (writing
    the inner model CC desyncs the visual → camera detaches / moves too fast;
    writing an outer rig CC also misbehaves). So the retarget uses the **nearest**
    CC (don't over-climb), and the correct rung is chosen by the **movement
    ranking** instead:
    - **Controller-root promotion** (`PromoteControllerRoot`, on by default): the
      movement DCA is the model/skeleton root; its immediate parent is promoted to
      **#1** (`controller root`) when that parent carries a CharacterController
      (`Type.GetType` + `GetComponent`), i.e. it's part of the controller
      hierarchy rather than a static scene container. So Using the default #1 lands
      on the logical root. If it still misbehaves, the model root sits at #2.
- `WritePoseAsync(…, driver)` — for a CharacterController driver: `Collider`
  `set_enabled(false)` (enabled lives on Collider, PhysicsModule) → position/euler
  write → `set_enabled(true)`. Wrapped so a throw during the write still
  re-enables; a re-enable failure is surfaced (don't silently leave the character
  frozen). No CC → the raw `set_position` path (unchanged).

The detection runs once at **Set Target** (`TeleportController` caches the driver
+ effective transform); markers / coords / directional all inherit it. The tab
shows `Driver: CharacterController …` under the pose. It's fully **auto-selected**
— the user never picks a mode. This is the first member of the driver family;
Rigidbody / NavMeshAgent slot into the same dispatch later.

This is why signal C (`FindObjectsByType(CharacterController)`) reported 0 on
Utawarerumono while the player *was* CharacterController-driven: that enumeration
API is stripped, but the component still exists and `GetComponentInParent` finds
it from the movement-detected transform.

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
   moving chains are **grouped into clusters by their top-level root**, and the
   resolution anchors on the cluster that most **looks like an animated character**
   (`ClusterCharacterScore` = member count + joint-named members + a real shared
   bone namespace, minus a camera-rig-name penalty) — **NOT** the single object
   that moved farthest. This is the fix for the live regression where a walking
   character's feet moved *less* than the follow-camera that panned to track it and
   the camera-parented VFX (`Fire*` / `Encount_*`): raw top-displacement anchoring
   locked onto `FieldCameraControl` / `Encount_Effect` and the real character
   (`P_Chara` / `AttachPoint`) fell off the list entirely. A real bone namespace is
   a common prefix that ends at a separator (`env_bone:`), so a coincidental
   character-run shared by unrelated props (`Fire04`/`Fire06` → `Fire0`) earns no
   skeleton credit; (d) a `get_parent` *error* is
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
- **Global hotkeys (rebindable)** — enable to fire while the *game* window is
  focused, no alt-tab. Each action (Save/Recall marker 1-3, Undo, Forward, Back)
  is a row: click **Set**, then press the combo (e.g. `Ctrl+F5`). A tunnelling
  KeyDown handler captures the press; `HotkeyKeyMap` converts Avalonia
  `Key`+`KeyModifiers` → Win32 `(mask, vk)`; the combo is claimed via
  `RegisterHotKey` and persisted to `%LOCALAPPDATA%\Discrete_Dumper\teleport-hotkeys.txt`
  (`action=mods,vk`, AOT-safe plaintext). Defaults are the old numpad scheme
  (`Ctrl/Alt+Num1..3`, `Ctrl+Num0`; NumLock ON) — rebind to F-keys to drop the
  NumLock requirement. A combo already held by another app is reported and the
  old binding kept. Registration is gated on the master **Enable** checkbox;
  bindings survive it.

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

## Keep-foreground (native run-in-background, 2026-07-12)

Native Win32 "keep the game running while unfocused" hook, ported from
UE5CEDumper's *Grausam*. A CheckBox on the Teleport tab ("Keep the game running
when unfocused (native hook)"). Distinct from the managed
`UnityEngine.Application.set_runInBackground` path the Scene tab drives:
**IL2CPP strips `set_runInBackground` on real games** (see
`reference_il2cpp_strips_runinbackground`), so the managed setter is
method-not-found there — the native hook works regardless of the managed
runtime, and even while the game thread is idle.

**Mechanism** (DLL `dll/src/shared/ForegroundHook.cpp`, backend-agnostic,
compiled into both DLLs; one atomic `g_enabled` gates three illusions):
1. MinHook `user32!GetForegroundWindow` → report the game's own (largest,
   visible, un-owned, this-PID) window as foreground when the real foreground
   belongs to another process (defeats poll-based idle / `Application.HasFocus`).
2. `SetWindowLongPtrW(GWLP_WNDPROC)` subclass on every top-level game window →
   rewrite `WM_ACTIVATEAPP`/`WM_ACTIVATE`/`WM_NCACTIVATE`→active, swallow
   `WM_KILLFOCUS` (defeats event-driven pauses).
3. MinHook `user32!ClipCursor` + `SetCursorPos` → while backgrounded, release
   cursor confinement / swallow recentring so the mouse isn't trapped
   desktop-wide; pass through when the game is genuinely foreground.

Disable is **soft** (flip the atomic + release the cursor) — hooks/subclass are
never removed in place to avoid a teardown race; they pass through when off.
Full teardown at DLL shutdown (`ForegroundHook::Shutdown`). MinHook is
per-target-enabled (never `MH_ALL_HOOKS`) so it doesn't disturb the shared
main-thread hook.

**Wire:** `setKeepForeground {enable}` / `getKeepForeground` →
`{state: 1|0|<0}` (1 on, 0 off, negative = hook-install error). Light commands
on the interactive pipe. The Teleport tab syncs the toggle to the DLL's real
state on connect (the hook can survive a UI reconnect); the state clears when
the game restarts. **No auto-disable on UI disconnect** — discrete's 2-lane
pipe + post-hook client-count decrement make a reliable "last client left"
trigger racy, and the enabled state is benign (game runs when unfocused, cursor
freed, normal when focused).

## 待 live-smoke (needs a running Unity game)

| # | Check | Pass sign |
|---|---|---|
| 1 | Mono + IL2CPP: set target → Refresh shows sane position | matches the in-game location |
| 2 | Save marker → move in-game → Recall | pawn returns to the saved spot |
| 3 | Undo after a bad jump | returns to the pre-jump pose |
| 4 | Teleport to coordinates (copy → paste round-trip) | lands exactly |
| 5 | Directional Forward/Back, horizontal on/off | moves along facing / on ground plane |
| 6 | Global hotkey with the GAME focused (default NumLock on, or rebind to an F-key) | the bound combo saves / recalls without alt-tab |
| 7 | Rotation restore on recall | facing restored (where the game doesn't override it) |
| 8 | Keep-foreground ON (Mono): tick → alt-tab to another window | game keeps ticking (audio/anim) + mouse freed; untick → pauses again |
| 9 | Keep-foreground ON (IL2CPP where managed runInBackground is stripped) | keeps running even though the Scene-tab managed toggle reports SetterUnavailable |
| 10 | Reconnect the UI with the hook ON | Teleport tab re-syncs the checkbox to "On" |

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
- Movement knobs reframed as generic field-write + `Physics.gravity`.
