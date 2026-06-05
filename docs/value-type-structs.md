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

### 3.2 Phase B — generic value-type recursion  ✅ offset RESOLVED (live-smoke 2026-06-01)

> **The offset question is settled.** Live-smoke on Core Keeper (**Mono x64**)
> proved the convention via hex (see [archive/status-history.md](archive/status-history.md) "Phase 31 live-smoke 發現"):
> `PlayerController.srOffSetPos` (Vector3 @ field-offset 0xA34) is truly
> `(0,0,0)` in memory, but the existing live Inspector drill displayed
> `(0.6875,-0.0585,0)` — it read from `structBase + 0x10`, i.e. it used
> `mono_field_get_offset(Vector3.x) = 0x10` (header-inclusive) without
> stripping the header on an inline (header-less) struct. (Mono and IL2CPP
> share the convention: a `MonoObject`/`Il2CppObject` header = 2 pointers,
> so the over-read is **0x10 on x64, 0x08 on x86** — i.e. `2*ptrSize`.)
>
> **Locked formula**: inline value-type member read =
> **`fieldAddr + member.offset − 2*ptrSize`** (subtract the
> `Il2CppObject`/`MonoObject` header). Scalars are unaffected (their parent
> field offset already lands correctly because `instanceBase` IS the object
> base). Phase A is correct as shipped (it hardcodes struct-relative 0/4/8
> offsets, never touching `il2cpp_field_get_offset`), so it is in fact MORE
> correct than the current live drill.
>
> **Two real bugs this surfaced in the EXISTING live Inspector ValueStruct
> drill** (separate from the snapshot path; both need fixing):
> - **+0x10 header double-count** — the `WalkedFieldKind.ValueStruct` →
>   walkInstance-by-name path reads members at `structBase + headerInclusiveOffset`.
>   Fix: subtract `2*ptrSize` for value-type member addresses (or have the
>   struct walk emit struct-relative offsets).
> - **Nested-type name resolution** — `CharacterData.NonSerialized` (a nested
>   type) fails `class not found` because by-name lookup uses the `.` form;
>   nested types need the `/` (or `+`) separator. CE resolves it fine. This
>   also blocks Phase B for nested structs.
>
> **Implementation sketch (formula now locked — `member.offset − 2*ptrSize`)**, 5 sites:

For a field with `is_value_type && !is_enum && !primitive && !namedStruct`:
- resolve the struct's field layout via the existing class-info path
  (`ReadClassInfo` — Mono [MonoClassWalker.cpp](../dll/src/mono/MonoClassWalker.cpp) /
  IL2CPP [IL2CPPClassWalker.cpp](../dll/src/il2cpp/IL2CPPClassWalker.cpp));
  the struct's runtime class is already known from `class_from_*_type`.
- for each instance member, `decodeField(memberType, fieldAddr + memberOffset)`
  — **inline, NOT dereferenced** (value-type members are stored in place).
- recurse **one level** only; nested struct members render as their own
  summary/`(…)` (no unbounded recursion).

**Hardening (apply the walk-CTD lessons — [archive/status-history.md](archive/status-history.md) 2026-06-01 batch):**
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
the Copy CE XML / array-stride fixes in [archive/status-history.md](archive/status-history.md) 2026-05-30/31 batches).

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

### 31-5 implementation notes (build #965, 2026-06-02)

Done entirely in the DLL address-finder — **no ExportAPI / protocol / UI change**
(the existing `field.name` / `fieldType` / `fieldKind` wire keys carry the
component name automatically; the UI renders it as-is).

- `MonoAddressFinder.cpp` + `IL2CPPAddressFinder.cpp`: `ResolvedField` gained
  `valueTypeClass` (populated in `EnumerateInstanceFields` from the field's
  resolved struct class `fc`, when `isValueType && !isEnum`).
- New `NameStructComponent(parent, fieldOffset, fh)` called from `MakeFieldHit`
  when the containing field is an **exact** value-struct match and the hit is
  **interior** (`fieldOffset > field.offset`). It walks the struct class's own
  members; member offsets are header-inclusive, so struct-relative =
  `member.offset − kObjectHeader` matches the interior offset. On a member hit
  it rewrites the `FieldHit`: `name → parent.member` (e.g. `_position.y`),
  `typeName`/`offset`/`size`/`kind` → the member (a float component reports
  `kind="leaf"`). Recurses one more level for a nested struct member
  (`Bounds.center.y`). Caller already holds the `ThreadAttachGuard`.
- A hit exactly at the struct base keeps the whole-struct `value-struct` kind
  (interior == 0 → no-op). Primitives / pointers / enums are untouched.

So a value scan hitting the `.y` float of a Vector3 field now reverse-resolves
to `Class._position.y` instead of `Class._position` (or "interior").

**Recursion-crash fix (build #990, session bug review)** — the first cut stored
`valueTypeClass` for PRIMITIVE value-type fields too, and `NameStructComponent`
had no depth cap. A reverse-lookup / value-scan hit landing INSIDE a primitive
field (e.g. +2 into a `float`) recursed into the primitive's self-typed internal
field (`System.Single.m_value` at struct-rel 0) with an unchanged interior
offset → unbounded recursion → stack overflow → **game CTD**. (The original 31-5
smoke missed it: that hit was at the struct base, interior 0, which early-
returns.) Fixed in both finders: (a) don't store `valueTypeClass` for primitives
(`TypeNameIsPrimitive` guard) so a primitive member never recurses; (b) a depth
cap of 4 on `NameStructComponent` as belt-and-suspenders. The 31-2 capture path
was already safe (its `ShouldExpandValueStruct` gate never recurses into
primitives, which decode to `value` not a bare `ptr`).

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

| Wave | Scope | Status (2026-06-01) | Verification |
|---|---|---|---|
| **31-1** | DLL Phase A (named structs) + protocol `Fields` + FormatFieldValue tuple | ✅ **SHIPPED** (dev) | DLL **needs build + live-smoke** (vec3 x/y/z vs CE); UI unit-tested |
| **31-3** | SPC/diff `ExpandStructMembers` fan-out + summary + count parity | ✅ **SHIPPED** (dev) | UI unit tests green |
| **31-4** | Pivot/KeyDiscovery per-component (dotted paths) + pickers | ✅ **SHIPPED** (dev) | UI unit tests green |
| **31-2** | DLL Phase B (generic value-type recursion) | ✅ **SHIPPED & live-verified (Mono x64, build #960)** | `member.offset − 2*ptrSize`; class captured during ReadClassInfo (nested-type-safe) |
| **31-5** | Value Search reverse-lookup names the component | ✅ **SHIPPED** (build #990) — needs live-smoke | DLL address-finder names `_position.y` on an interior value-struct hit; **recursion-crash fixed (build #990)** |
| **31-6** | Fix EXISTING live Inspector ValueStruct drill | ✅ SHIPPED (build #936) + live-verified | (a) +0x10 header double-count; (b) nested-type name |

### 31-2 implementation notes (build #960, 2026-06-02)

- **Class resolution**: instead of re-parsing the field's type name (ambiguous
  for nested types), the value-type field's runtime class is captured into
  `FieldEntry.valueTypeClass` during `ReadClassInfo` (both backends — set when
  `isValueType && !isEnum`). Recursion uses that exact class pointer, so nested
  structs resolve for free.
- **Helpers**: `CaptureMonoStructFields` / `CaptureIl2cppStructFields`
  (`ExportAPI.cpp`, anon namespace, backend-#ifdef'd). Each `ReadClassInfo`s the
  struct class, then for every instance member emits
  `decodeField(memberType, structBase + member.offset − kObjectHeader)`. Named
  math structs / primitives are still handled inside `decodeField` (Phase A), so
  the helper only fans out genuinely-custom structs; a nested generic-struct
  member recurses one more level (depth-capped). SEH safety inherited from
  `ReadClassInfo` (all accessors SEH-wrapped) + `Memory::SafeReadBytes` +
  `ProbeKlassValid` up-front + member/depth caps. A process-lifetime
  `ReadClassInfo` cache keeps repeated struct decode off the runtime (Mono BDWGC
  pressure).
- **Gate** (`ShouldExpandValueStruct`): `isValueType && !isEnum && valueTypeClass
  != null && entry has bare "ptr" (no "value"/"fields") && type ∉
  {System.IntPtr, System.UIntPtr}`. Excludes primitives, named structs, strings,
  and the IntPtr value types that legitimately ARE a pointer.
- **Wired into 6 sites**: Mono + IL2CPP `walkInstance` (always-on, interactive),
  Mono `decodeOneClass` + `captureMonoElementFields`, IL2CPP
  `Il2cppStreamDecoder::DecodeOneClass` + `CaptureElementFields` (the last four
  gated by the new `valueStructFields` session flag).
- **Flags** (`Snapshot::Session`): `valueStructFields` (default **true**) +
  `structMemberCap` (default **32**); parsed in both `snapshotBegin` handlers,
  echoed in the Begin response. Default-on means **no UI plumbing needed** (the
  UI already consumes `SnapshotFieldEntry.Fields` from 31-1/31-3); a flaky title
  can disable via the flag. walkInstance uses a fixed cap (64) + depth 4.
- **Live-verified (Core Keeper, Mono x64, 2026-06-02)**: `PlayerController.
  <WorldPosition>k__BackingField` (a Vector3-shaped backing field) fans out to
  `.x` / `.y` / `.z` component pseudo-rows in the diff grid, the SPC Query
  results, AND the Class Pivot trajectory — values match the parent summary
  tuple `(x, 0, z)` and move per-axis across snapshots. So the generic recursion
  + `member.offset − 2*ptrSize` formula are confirmed on Mono x64.
- **Still wants live-smoke** for completeness: Mono **x86** (4-byte header strip)
  and **IL2CPP x64** (same convention, not yet byte-verified on IL2CPP).

**Shipped 2026-06-01**: 31-1 (Phase A) + 31-3 + 31-4 — covers snapshot / SPC /
diff / pivot for all named Unity math structs (incl. character coordinates),
and is MORE correct than the current live drill.
**Shipped 2026-06-02 (build #960)**: 31-2 (Phase B generic value-type recursion)
+ 31-6 — generic custom structs (int/double/… members) now fan out their members
in walkInstance + snapshot on both backends. **Live-verified Mono x64** (Core
Keeper `<WorldPosition>k__BackingField` .x/.y/.z in diff/SPC/pivot).
**Shipped 2026-06-02 (build #965)**: 31-5 — the reverse-lookup / address-finder
now names the struct component (`_position.y`) on an interior value-struct hit
(DLL-only, both backends). **Pending live-smoke** (value-scan a Vec3 component →
reverse-lookup shows `Class.field.component`).
**New 31-6**: the live-smoke exposed two pre-existing bugs in the live
Inspector's ValueStruct expansion — (a) +0x10 header double-count (shows the
wrong/shifted members), (b) nested-type (`Outer.Inner`) name resolution
failing — both independent of the snapshot path; fix alongside 31-2.

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
