# Phase 31 — Inline value-type struct support (Vector3/4, Quaternion, custom)

**Status**: design (2026-06-01). Owner: dev branch.
**Touches**: DLL (Mono + IL2CPP) + protocol + snapshot UI + SPC/diff +
Pivot + Value Search. Unlike Phase 29i (UI-only), the root fix is **DLL
capture** — without it the UI has no component data to consume.

> User decisions (2026-06-01):
> 1. Scope = **Phase A (named Unity math structs) + Phase B (generic
>    value-type recursion, incl. custom int/double structs)**.
> 2. Snapshot representation = **component fan-out + summary tuple**.
> 3. Value Search = **single component scan + reverse-lookup** (no composite
>    vector scan).

---

## 1. Problem — verified current behaviour

Inline value-type structs (`UnityEngine.Vector3` = 3 floats, `Vector4` /
`Quaternion` / `Color` = 4 floats, `Vector2` = 2 floats, `Color32` = 4
bytes, and custom structs with int/double members) are captured as a
single **opaque pointer-sized word** everywhere.

### Root cause — DLL `decodeField`

`decodeField` ([ExportAPI.cpp:1453–1576](../dll/src/shared/ExportAPI.cpp))
is a flat **type-name ladder**: primitives matched by name
(`System.Int32`, `System.Single`, …) → `System.String` → a catch-all
`else` for everything else:

```cpp
} else {
    // Reference / unknown value type: report the raw pointer-sized word.
    std::uintptr_t v = 0;
    if (rd(addr, &v, sizeof(std::uintptr_t))) out["ptr"] = formatHex(v);
    else out["error"] = "unreadable";
}
```

So `Vector3` reads 8 bytes (x64) of the 12-byte struct and labels it a
`ptr`. **No recursion into x/y/z.** The walker DOES set `is_value_type`
on the entry and computes the correct inline `fieldAddr = addr + offset`
— it just doesn't decode the members.

### What already exists (reuse points)

- The **live Inspector CAN drill value structs** on demand:
  `WalkedFieldKind.ValueStruct` ([InstanceFieldClassifier.cs:123–134](../ui/UnityDumpUI.Core/Services/InstanceFieldClassifier.cs))
  → `LoadPointerChildrenAsync` re-walks the struct **by type-name at the
  field's own address** (the struct storage IS the value — no header, no
  deref). This proves the DLL can resolve a struct's field layout and
  decode members at an inline address; the bulk snapshot path just never
  calls it.
- Server-side classification already binds `is_value_type` (Mono
  `class_is_valuetype` [MonoClassWalker.cpp:250–265](../dll/src/mono/MonoClassWalker.cpp);
  IL2CPP [IL2CPPClassWalker.cpp:326–331](../dll/src/il2cpp/IL2CPPClassWalker.cpp)),
  with an `IsEnum` short-circuit so enums stay leaves.
- **Layer B precedent**: Phase 29f added `SnapshotArrayElement.Fields`
  (inner element fields, one level deep, captured at snapshot time). Inline
  struct capture is the exact same shape applied to a field instead of an
  array element.

### Consequence across the four features

| Feature | Today | Why |
|---|---|---|
| FormatFieldValue | `"(no value)"` | falls through the if-ladder ([SnapshotViewModel.cs:2910](../ui/UnityDumpUI/ViewModels/SnapshotViewModel.cs)) |
| SPC / diff | 1 row, value `(no value)` | `BuildFieldIndex` emits one row per field; only arrays fan out |
| Pivot / KeyDiscovery | key/value `(no value)`, cardinality 1 | both call `FormatFieldValue` |
| Value Search | scalar widths only | live scan takes one `dataType` width; no composite |

---

## 2. Architecture

```
Layer 1 — DLL capture (root)
   decodeField gains a value-struct branch → emits member sub-values
        Phase A: named Unity math structs (read N floats/bytes by name)
        Phase B: generic value-type — ReadClassInfo on the struct class,
                 decode each member at fieldAddr+memberOffset (1 level, SEH)
   ↓ frozen into payload_gzip
Protocol — SnapshotFieldEntry gains `Fields` (struct members, like
   SnapshotArrayElement.Fields). Old .db compatible (absent ⇒ no members).
   ↓
UI consumers
   FormatFieldValue   → render struct as "(x, y, z)" composite tuple
   BuildFieldIndex    → fan out members as pseudo-rows (name.x/.y/.z) +
                        keep a summary row (array Layer A model)
   Pivot/KeyDiscovery → benefit automatically (tuple value; per-component)
   Value Search       → single-component float scan + reverse-lookup that
                        names the component (_position.y)
```

**Representation = component fan-out + summary tuple** (user decision):
- summary row: `_position` → `(1.0, 2.0, 3.0)` (comparable string for
  "did it change at all").
- component pseudo-rows: `_position.x`, `_position.y`, `_position.z` (each
  a float scalar) so diff/SPC pinpoint WHICH axis moved and pivot can key/
  value a single axis.

---

## 3. DLL capture (Layer 1)

### 3.1 Phase A — named Unity math structs (fast path)

A name table mapping struct FQN → component layout (offset, width, name):

| Struct | Members |
|---|---|
| `UnityEngine.Vector2` | x,y : float @0,4 |
| `UnityEngine.Vector3` | x,y,z : float @0,4,8 |
| `UnityEngine.Vector4` / `Quaternion` | x,y,z,w : float @0,4,8,12 |
| `UnityEngine.Color` | r,g,b,a : float @0,4,8,12 |
| `UnityEngine.Color32` | r,g,b,a : byte @0,1,2,3 |
| `UnityEngine.Rect` | x,y,width,height : float |
| `UnityEngine.Bounds` | center(Vector3), extents(Vector3) — nest one level |

When `decodeField`'s type name hits the table, read each member at
`addr + memberOffset` with the right width and emit them as `fields` (the
new member list). No class-layout resolution needed — pure offset reads
via `Memory::SafeReadBytes`. This covers the vec3/vec4 headline case with
near-zero CTD risk.

### 3.2 Phase B — generic value-type recursion

For a field with `is_value_type && !is_enum && !primitive && !namedStruct`:
- resolve the struct's field layout via the existing class-info path
  (`ReadClassInfo` — Mono [MonoClassWalker.cpp](../dll/src/mono/MonoClassWalker.cpp) /
  IL2CPP [IL2CPPClassWalker.cpp](../dll/src/il2cpp/IL2CPPClassWalker.cpp));
  the struct's runtime class is already known from `class_from_*_type`.
- for each instance member, `decodeField(memberType, fieldAddr + memberOffset)`
  — **inline, NOT dereferenced** (value-type members are stored in place).
- recurse **one level** only; nested struct members render as their own
  summary/`(…)` (no unbounded recursion).

**Hardening (apply the walk-CTD lessons — [STATUS.md](STATUS.md) 2026-06-01):**
- wrap every member accessor (`field_get_offset` / `field_get_type` /
  `class_get_name`) in the generic `SehCall`;
- runaway / garbage-class bail (depth + member-count cap, e.g. ≤ 64
  members) → clean error, not a crash;
- a member at a garbage offset → `member_error`, not a wild read.

### 3.3 Caps / opt-in

- Mirror Layer B's bitness policy: x64 blanket-on is fine; on x86 keep an
  eye on snapshot size (struct fan-out adds rows like array Layer A). New
  snapshotBegin flags: `valueStructFields` (bool) + `structMemberCap`
  (default 32). Off → today's behaviour (single ptr word), so the change
  is opt-in and old captures are unaffected.

### 3.4 Offset correctness (project history)

Value-struct member offsets are struct-relative and **bitness-independent**
for the float/byte members (a Vector3 is 12 bytes on x86 and x64). But the
OWNING field's inline address uses `addr + f.offset` which is already
bitness-correct. Double-check: members that are themselves references /
IntPtr inside a struct use pointer-size — read via `ManagedLayout::kPointerSize`,
not a hard-coded 8 (the project has repeatedly hit x86 4-vs-8 bugs — see
the Copy CE XML / array-stride fixes in [STATUS.md](STATUS.md)).

---

## 4. Protocol

`SnapshotFieldEntry` ([ProtocolModels.cs](../ui/UnityDumpUI.Core/Services/ProtocolModels.cs))
gains:

```csharp
/// <summary>Phase 31 — inline value-type struct members (Vector3 x/y/z,
/// custom struct fields), one level deep, captured at snapshot time when
/// valueStructFields is on. Null on primitives, references, arrays, and
/// old captures. Mirrors SnapshotArrayElement.Fields.</summary>
[JsonPropertyName("fields")] public List<SnapshotFieldEntry>? Fields { get; set; }
```

JSON key `fields` matches the array-element Layer B key, so the C++ side
emits the same shape. Schema version unchanged — `fields` rides inside
`payload_gzip`; old `.db` files have no member fields and honestly show
none.

---

## 5. UI consumers

### 5.1 FormatFieldValue — composite tuple

Add a branch before the `(no value)` fallback: when `f.Fields` is a
non-empty member list and the field isn't an array, render
`(m0, m1, …)` from each member's `FormatFieldValue`. Vector3 →
`(1, 2, 3)`. Gives SPC/pivot/diff a meaningful comparable string
immediately.

### 5.2 SPC / diff — component fan-out

`BuildFieldIndex` ([SnapshotViewModel.cs:2669](../ui/UnityDumpUI/ViewModels/SnapshotViewModel.cs))
already fans out arrays via `ExpandArrayElements` ([:2773](../ui/UnityDumpUI/ViewModels/SnapshotViewModel.cs)).
Add the analogous `ExpandStructMembers`:
- summary row key unchanged: `{asm}|{fqn}|{addr}|{offset}|_position` →
  value `(1, 2, 3)`.
- member pseudo-rows: `{asm}|{fqn}|{addr}|{offset}|_position.x` (and `.y`,
  `.z`), value = that member's scalar.
- nested struct members (Bounds.center.x) → dotted path, one extra level.

So diff shows `_position.y : 0 → 5` and SPC tracks each axis. The summary
row keeps "did the whole vector change" working without expansion.

`CountFieldValues` ([:2746](../ui/UnityDumpUI/ViewModels/SnapshotViewModel.cs))
must count members too (memory estimate parity).

### 5.3 Pivot / KeyDiscovery — automatic + per-component

- A struct VALUE field now renders `(x, y, z)` across snaps (trajectory
  works). A struct KEY uses the same tuple (stable composite identity).
- Because BuildFieldIndex fans out members, the pivot field pickers also
  surface `_position.x` etc. as selectable key/value fields — so the user
  can pivot a single axis.
- KeyDiscovery's TypePrior: a struct field (has `Fields`, no scalar Value)
  should score like a reference (low) for keying, while its float members
  score normally. Add a guard so a struct summary isn't mis-scored as a
  perfect key.
- `ClassShortlistEngine` / `ClassInterestEngine`: a struct member that
  drifts (position.x) now contributes to volatility — automatic once
  fan-out lands.

### 5.4 Value Search — single component + reverse-lookup

Value Search stays a **live scalar scan** (unchanged scan path). The Vec3
story:
- user scans one component as a `float` (e.g. position.x ≈ 123.4);
- the existing **reverse-lookup / address-finder** maps the hit address to
  a field — and with Phase 31 capture it can now name the **component**
  (`_position.y` at `fieldOffset + 4`) instead of just `_position` or
  nothing. Extend the address-finder's field-offset resolver
  ([InspectorFieldOffsetResolver.cs](../ui/UnityDumpUI.Core/Services/InspectorFieldOffsetResolver.cs))
  to test struct-member sub-offsets.
- No composite "scan 3 floats" type is added (user decision — ROI low).

---

## 6. Wave delivery

| Wave | Scope | Ships alone? | Verifiable |
|---|---|---|---|
| **31-1** | DLL Phase A (named structs) + protocol `Fields` + FormatFieldValue tuple + Inspector shows members | yes | DLL build + **live-smoke** (vec3 in real game) + UI unit tests for tuple render |
| **31-2** | DLL Phase B (generic value-type recursion + SEH hardening + caps) | yes | DLL build + **live-smoke** (custom int/double struct) |
| **31-3** | SPC/diff `ExpandStructMembers` fan-out + summary + count parity | yes (UI-only on captured data) | UI unit tests (fan-out rows, nested) |
| **31-4** | Pivot/KeyDiscovery struct guards + per-component pickers + shortlist/interest parity | yes (UI-only) | UI unit tests |
| **31-5** | Value Search reverse-lookup component naming | yes | UI unit tests + live-smoke |

Suggested order: **31-1 → 31-3 → 31-4 → 31-2 → 31-5** — get the named-vector
capture + the UI consumers working end-to-end on the common case first
(31-1/3/4), then the generic struct path (31-2), then the value-search
polish (31-5). Each UI wave ends green on the unit suite; each DLL wave
needs a real-game live-smoke (can't be unit-tested) — flag it in STATUS.

---

## 7. Test plan

### Unit (UI, per wave)
- **31-1**: `FormatFieldValue` renders `(1, 2, 3)` for a Vector3-shaped
  entry with 3 member Fields; `(no value)` only when truly empty.
- **31-3**: `ExpandStructMembers` emits N member rows + 1 summary row;
  nested (Bounds) one extra level; empty member list → summary only;
  existing array + SPC tests stay green; `CountFieldValues` parity.
- **31-4**: a struct value field produces a `(x,y,z)` trajectory in Pivot;
  a struct member is selectable + pivots per-axis; KeyDiscovery doesn't
  crown a struct summary as a perfect key.
- **31-5**: field-offset resolver maps `fieldOffset+4` to `_position.y`.

### Live-smoke (real game inject — NOT unit-testable)
- **31-1/31-2**: capture a known game (受讚頌者 x64 + 活俠傳 x86), inspect a
  Transform/position Vector3 → x/y/z match CE byte-for-byte; an x86 game to
  confirm member offsets + any in-struct pointer uses 4-byte size.
- **31-2**: a custom struct with mixed int/double members decodes each.
- diff/SPC/pivot over two captures show a moving position per-axis.

---

## 8. Cost / risk

- **Memory**: member fan-out adds rows ≈ Σ struct-member counts, same order
  as array Layer A (+a few %). `structMemberCap` bounds it.
- **CTD risk**: Phase B walks an arbitrary value-type class layout — the
  same surface that produced the 2026-06-01 walk-CTD batch. Reuse
  `SehCall` / runaway-bail; Phase A (offset reads only) is the safe
  fallback if Phase B proves flaky on a title.
- **Persistence**: schema unchanged; old `.db` compatible.
