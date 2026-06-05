# Class-centric Key/Value Pivot — Phase 29e spec

**Status**: design (not yet implemented). Owner: dev branch.
**Companion**: extends SPC tab (Phase 29d). Independent of DLL — pure UI feature.

---

## 1. Problem

Phase 29d SPC tab is **field-centric**: every field is tracked independently
across N snapshots, keyed by
`{assembly}|{fqn}|{instanceAddress}|{offset}|{fieldName}`. Two consequences:

1. **Cross-session collapse**. `instanceAddress` is the live managed pointer.
   After a game relaunch every instance gets a fresh address, so the
   intersection of `FieldIndex.Keys` across snapshots taken in different
   sessions collapses to statics only. The cross-session banner in
   `SnapshotViewModel.cs` already auto-enables StaticsOnly for this reason.

2. **Instance-index drift within a session**. The DLL emits instances in
   walk order. A class with 100 live instances at snap 1 may have 102 at
   snap 2; "instance #124" at snap 1 is not necessarily "instance #124" at
   snap 2. Concrete case: 活俠傳 `Mortal.Core.GameStat` for 銀兩 (Money)
   lands at instance #124 sometimes, #123 other times — addresses move,
   so SPC's key intersection ignores both rows.

The user's insight: **most game-meaningful data has a stable business key
inside the instance**. `_propertyID=101` identifies "this is the Money
stat" no matter where the instance lives. `_itemGroup="Weapon" AND
_propertyID=42` identifies "this is sword id 42 in the weapon group".

If we pivot the data — **group by key field value, project value
field across snapshots** — the cross-session and intra-session drift
problems both disappear at once.

This is what plan.md:296 calls the **Class-centric Key/Value pivot**.
Old-school: it is essentially the same shape as a Crystal Report
group-by / SQL `SELECT V FROM C GROUP BY K JOIN snap_i…snap_n`.

---

## 2. Three-stage funnel

A real Unity game has ~3000 classes (活俠傳 Assembly-CSharp). Brute-force
"for every class, for every field, score key candidates across N
snapshots" would scan millions of values. The funnel:

```
Stage 0 — class shortlisting (cheap, runs on full class list)
   ↓ user picks 1–3 candidate classes
Stage 1 — field-level key auto-discovery (runs on chosen classes)
   ↓ user picks key field(s) K + value field(s) V
Stage 2 — pivot (PivotEngine) — render result table
```

Stage 0 is the price of entry. Without it the user is forced to know
the class name in advance.

---

## 3. Stage 0 — class shortlisting

For each class C present across the selected snapshots, compute:

| Field            | Definition                                                 | Used for                       |
|------------------|------------------------------------------------------------|--------------------------------|
| `ClassFqn`       | `{Namespace}.{Name}`                                       | label                          |
| `Assembly`       | declaring assembly                                         | label / filter                 |
| `Presence`       | snap count where C has ≥1 instance                         | must = N to be a candidate     |
| `InstancesPerSnap` | array `[snap1.count, snap2.count, ...]`                  | shown as sparkline / tooltip   |
| `Avg`, `StdDev`  | mean + stddev of `InstancesPerSnap`                        | stability                      |
| `CV`             | StdDev / max(Avg, 1) — coefficient of variation            | **primary stability metric**   |
| `FieldCount`     | distinct field names observed across all instances of C    | rule out tiny classes          |
| `PrimitiveFieldRatio` | int/uint/long/ulong/float/double/bool/string field fraction | high = many key candidates |
| `NullFieldRatio` | (instance-fields that are null/error/empty) / (total)      | high = noisy                   |
| `StringFieldRatio` | quoted-string field fraction                             | neutral signal                 |
| `ArrayFieldRatio`  | fields whose `SnapshotFieldEntry.ArrayElements` is non-null / total | hint: enable array expansion |
| `IsSynthetic`    | name starts with `<` (mirrors existing `IsSyntheticClassName`) | hide by default            |

**Default sort**: `Presence == N` first, then `CV ascending`, then
`FieldCount descending`. So the user sees the most stable, most
field-rich classes at the top.

**Default filter**: `Presence == N AND !IsSynthetic AND FieldCount >= 2`.

**Cost**: O(Σ_snaps Σ_classes Σ_instances Σ_fields), but the inner work
per field is a tiny bit-bucket update (count + null-check). For 活俠傳-
sized snap × 5 snaps: estimated < 200 ms; well within "click button →
see table" UX.

**UI shape**: top half of new "Class Pivot" tab — sortable DataGrid:

```
☐  Class FQN                       Asm        Presence  Inst (avg±sd)  CV    Fld  Prim%  Null%  Arr%
─────────────────────────────────────────────────────────────────────────────────────────────────────
●  Mortal.Core.GameStat            Asm-Csh    5/5       101 ± 1.4      0.014  6   100%   2%     0%
●  MainGame.PLY_WORK               Asm-Csh    5/5       1   ± 0        0.0    18  78%    11%    22%
○  Mortal.Core.SkillData           Asm-Csh    5/5       42  ± 0.5      0.012  4   50%    0%     50%
○  <>c__DisplayClass17_0           Asm-Csh    5/5       8   ± 4.1      0.51   2   100%   0%     0%
```

User selects ≥1 row → Stage 1 enabled.

---

## 4. Stage 1 — field-level key auto-discovery

For each selected class C, score every field F (statics included) on its
suitability as a key. Compose 4 sub-scores in [0, 1]:

### 4.1 Stability score

How well does F's value set overlap across snapshots?

```
S_stable(F) = (avg pairwise Jaccard of value sets across snap pairs)
            ∈ [0, 1]
```

A stable key (`_propertyID`) has nearly identical value sets across
snaps → S_stable ≈ 1.0. A volatile counter (`_frameCount`) has disjoint
value sets → S_stable ≈ 0.

### 4.2 Cardinality score

A good key is unique-ish within each snapshot.

```
S_cardinality(F, snap) = distinct_values(F, snap) / instance_count(C, snap)
S_cardinality(F)       = avg over snaps
                       ∈ [0, 1]
```

= 1.0 → perfect key (every instance has unique F value). = 1/N → F is a
constant. = m/N → F is an N-to-m mapping.

### 4.3 Type prior

```
TypePrior(F) = {
    int*/uint*/long*/ulong*:  1.00
    string:                   0.85
    enum-shaped:              0.85
    double/float:             0.20    (rarely meaningful keys)
    bool:                     0.10
    pointer:                  0.05
    array/error/null:         0.00
}
```

### 4.4 Name prior (weak signal)

```
NamePrior(F) = max over substring matches (case-insensitive):
    "id", "_id":              +0.30
    "name":                   +0.20
    "key", "hash":            +0.20
    "type", "kind":           +0.15
    "group", "category":      +0.15
    "code":                   +0.10
    floor 0.0, cap 1.0
```

### 4.5 Composite

```
Score(F) = 0.40 · S_stable
         + 0.30 · S_cardinality
         + 0.20 · TypePrior
         + 0.10 · NamePrior
```

Sort fields by `Score` descending, show top 10. User checks 1+ field as
**Key**, 1+ field as **Value**. Compound key supported (multi-check).

### 4.6 Greedy compound-key suggestion

Optional helper: if the top-scored single key still has collisions
(two instances share the same key value within one snapshot), suggest
the additional field that breaks the most collisions:

```
for each candidate K2 ∈ fields ≠ K1, sorted by Score:
    collisions = count(instances with same (K1, K2) tuple)
    if collisions < current_best.collisions:
        suggest K2
        break when collisions == 0
```

UI affordance: "+ Add key field" button next to the suggested K2.

---

## 5. Stage 2 — PivotEngine

```csharp
public sealed class PivotQuery
{
    public required IReadOnlyList<SnapshotEnvelope> Snapshots { get; init; }
    public required string   ClassFqn        { get; init; }
    public required string   Assembly        { get; init; }
    public required IReadOnlyList<string> KeyFields   { get; init; }
    public required IReadOnlyList<string> ValueFields { get; init; }

    // Optional projection filters (mirror SPC):
    public bool   IncludeStatics  { get; init; }
    public string? KeyFilter      { get; init; }   // substring on rendered key tuple
}

public sealed class PivotRow
{
    public required IReadOnlyList<string>        KeyTuple { get; init; }
    // [valueField][snapIdx] → display string (or null if no instance with this key in that snap)
    public required IReadOnlyList<IReadOnlyList<string?>> Values { get; init; }
    // Address per snap for handoff to Inspector / CE — null if absent
    public required IReadOnlyList<string?>       AddressPerSnap { get; init; }
    // Multi-instance collision marker — if >1 instance shared this key tuple in any snap
    public required bool HasCollision { get; init; }
}

public static class PivotEngine
{
    public static List<PivotRow> Run(PivotQuery q);
}
```

Algorithm:

```
for each snap in Snapshots:
    map[snap] = empty dict (string keyTuple → list of (instance, values))
    walk classEntry.Instances of (Assembly, ClassFqn):
        kTuple = render(KeyFields, instance.Fields)
        vList  = render(ValueFields, instance.Fields)
        map[snap][kTuple].append((instance.Address, vList))

allKeys = union over snaps of map[snap].keys
for each kTuple in allKeys:
    row.KeyTuple = kTuple.split
    row.HasCollision = any snap had >1 instance with this kTuple
    for snapIdx, snap in enum:
        if kTuple in map[snap]:
            pick first instance (or render "N instances: x, y, z" if collision)
            row.Values[vf][snapIdx] = display
            row.AddressPerSnap[snapIdx] = address
        else:
            row.Values[vf][snapIdx] = null    # absent
            row.AddressPerSnap[snapIdx] = null
    emit row
```

Collision policy: surface as a flag, render value cell as
`<2 instances: 50, 75>` so user sees the ambiguity (does NOT silently
pick one). Future refinement: "split into N rows" toggle.

Absent cell ("instance with this key wasn't in snap X"): rendered as
`—` in DataGrid.

---

## 6. Result UI

Bottom half of Class Pivot tab — pivot DataGrid:

```
KeyTuple (_itemGroup, _propertyID)   snap1._value  snap2._value  snap3._value  snap4._value  Address (latest)
─────────────────────────────────────────────────────────────────────────────────────────────────────────────
("Money", "101")                     10160         9910          9410          9410          0x1f3a...
("HP",    "201")                     1200          1180          900           1100          0x1f3a...
("MP",    "202")                     300           300           250           250           0x1f3a...
("Exp",   "301")                     45200         45200         46100         46100         0x1f3a...
```

Right-click row:
- **Inspect (latest snap)** — cross-tab to Inspector with `Address (latest)`
- **Inspect (snap N)** — submenu per snap
- **Copy key tuple**
- **Copy value sequence**

Column resize on; sort off (preserves snap order in the columns).

---

## 7. Array element handling — Layer A in this Phase, Layer B deferred

### Current state (confirmed)

DLL captures arrays well: `SnapshotArrayElements` carries
`{Index, Value, Ptr, String, Error, Address}` per element
(`ProtocolModels.cs:1054`). DB persists them verbatim in
`payload_gzip`. **But** `BuildFieldIndex` collapses arrays into a
single string row (`SnapshotViewModel.cs:2220`):

```csharp
if (f.ArrayElements is { Count: > 0 } arr)
    return $"[{arr.Length} elements, captured {arr.Count}]";
```

→ SPC tab today can detect "array length / capture count changed" but
not "element[3] changed value". The data is in the envelope, just
unreachable from FieldIndex.

### Layer A — primitive array index expansion (in 29e-1b)

Modify `BuildFieldIndex` so primitive arrays fan out:

```
Existing key shape:
    {asm}|{fqn}|{addr}|{offset}|inventory
Adds:
    {asm}|{fqn}|{addr}|{offset}|inventory[0]
    {asm}|{fqn}|{addr}|{offset}|inventory[1]
    ...
    {asm}|{fqn}|{addr}|{offset}|inventory[N-1]    where N = ArrayElements.Count
```

Value display per pseudo-row mirrors the element's stored form
(`Value` JsonElement → string ladder, `String` → quoted, `Ptr` → ptr=).
The summary row stays for compatibility.

**Constraint**: this is **index-keyed**, so if the game shuffles the
array (sorted inventory) between snaps, `inventory[3]` will appear to
change while semantically the same item just moved. Adequate for many
real cases (combat stat slots are fixed, GameStat arrays are usually
positional), inadequate for sortable inventories.

**Scope**: primitive-only. Reference-type arrays (the inner element
has its own fields) require DLL-side capture of element fields, which
is Layer B.

**Cost**: per snap, fan-out adds rows = sum of array lengths. For a
typical 活俠傳 snap this is < 50 K rows on top of ~1 M existing →
+5% memory. Acceptable.

### Layer B — inner-pivot on reference array elements (Phase 29f — planned 2026-05-28)

For `List<Item>` / `Item[]` where each element has its own key field
(`_propertyID`), the correct fix is pivot keyed by an *inner* field, not
by index. Motivating case: `Mortal.Core.GameStat._statAdditionData :
StatConvertCollectionData[]` on 活俠傳 — each element is an object with
its own fields; we want to key elements by `_propertyID` and track an
inner value across snaps, sidestepping BOTH Layer A's index-shuffle
weakness and address drift.

**Why DLL-side (can't be done in the UI after the fact)**: cross-session
is the primary Pivot use case, and an element object's address is dead in
older snapshots. So the element's inner fields must be frozen into the
gzipped blob at capture time. `SnapshotArrayElement` gains
`Fields : List<SnapshotFieldEntry>?` (one level deep only — element
objects' own array/reference fields render as summary/`ptr=`, no further
recursion).

**Confirmed DLL extension points** (from the 2026-05-28 source survey):

| Concern | Location | Action |
|---|---|---|
| Array element loop | `walkArrayElementsInline` ([ExportAPI.cpp:1081](../dll/src/shared/ExportAPI.cpp)) | when `is_pointer` + feature on, decode element fields |
| Element runtime class | Mono `object_get_class` / IL2CPP same ([MonoResolver.h:187](../dll/src/mono/MonoResolver.h)) | per-element `object_get_class(elemPtr)` under SEH — element type is **not** known statically from `element_type` |
| Field layout | `ReadClassInfo` ([MonoClassWalker.cpp:197](../dll/src/mono/MonoClassWalker.cpp) / [IL2CPPClassWalker.cpp:272](../dll/src/il2cpp/IL2CPPClassWalker.cpp)) | reuse the global class-info cache |
| Field value decode | existing `decodeField` lambda (same one top-level instance fields use) | apply at `elemPtr + offset` |
| Caps / params | snapshotBegin ([ExportAPI.cpp:2117](../dll/src/shared/ExportAPI.cpp) / :5131) + session struct | new `arrayElementFields` (bool) + `elementFieldCap` (default 16, hard cap 64) |
| Safe reads | `Memory::SafeReadBytes` ([Memory.cpp:35](../dll/src/shared/Memory.cpp)) | already SEH-wrapped; null/garbage element → `element_class_error` |

**Mono freeze note**: inner fields are *instance* fields (not statics),
decoded by raw offset reads — they do **not** call `mono_class_vtable`,
so the Phase 29a-v4 worker-thread wedge does not apply (top-level
instance fields are already captured fine on Mono).

**Capture model — bitness-adaptive** (user decision 2026-05-28):

| Target | Default | Override |
|---|---|---|
| **x64** | **Blanket** — inner fields captured for every reference array (true 全抓; memory is plentiful) | user may narrow to a target set |
| **x86 (32-bit)** | **OFF** + banner ("Inner-field capture disabled on 32-bit — OOM risk; pick a target class.arrayField to enable") | user may enable, but **targeted only** (must nominate one or more `class.arrayField` — blanket on x86 is not offered) |

The 활俠傳 motivating case is x86, so the targeted-on-x86 path is the one
the real use case runs through. The Phase-29-polish OOM gate (DLL refusal
≥ 3 GB private + auto-clamp) stays as the backstop.

**Pivot key scope — compound** (user decision 2026-05-28): result rows
key on `(owner-instance Stage-1 key, element inner key)`. The outer key
identifies the owning instance (drift-proof via existing Stage 1), the
inner key identifies the element within the array — both segments dodge
drift. Value = element's inner value field(s) across snaps.

**Persistence**: schema unchanged — `Fields` rides along inside
`payload_gzip`. Old `.db` files stay compatible; their elements have no
inner fields, so Layer B honestly shows nothing for them.

See plan.md **Phase 29f** for the wave breakdown + acceptance criteria.

---

## 8. Cross-session banner repurposing

Phase 29c-5's blue banner ("Cross-session diff: instance fields
suppressed") was designed for the diff tab where address-keyed
matching breaks. On the **Class Pivot** tab, cross-session is the
*primary use case*, so the banner inverts:

- Same session (all snaps same `session_id`)
  → no banner.
- Cross-session, same build (different `session_id`, same `build_id`)
  → blue banner: "Cross-session pivot — keying by `({k1}, {k2})`
    sidesteps address drift."
- Cross-build (different `build_id`)
  → yellow banner: "Cross-build pivot — field offsets may have
    shifted; key fields verified present on both sides
    (`{ok or warnings}`)." Engine warns if a chosen key field is
    missing on one side.

---

## 9. Persistence impact: **none**

`SnapshotStore` schema is unchanged. Existing `.db` files are fully
compatible — Stage 0 / Stage 1 / Stage 2 all read from `Classes[]`
which is the gzipped JSON payload. Once 29e ships, every existing
saved snapshot gains pivot capability without re-capture, without
migration, without manual deletion.

The `schema_version` meta key + migration runner
(`SnapshotStore.cs:540`) is in place for the eventual Layer B schema
bump.

---

## 10. Test plan

### 29e-1 (PivotEngine + unit tests)

Mirror `SpcQueryEngineTests` shape:
- single-key single-value, all keys present in all snaps
- compound key (2 fields)
- key collision within one snap → `HasCollision = true`
- key absent in some snap → null cells in `Values`
- cross-session (mixed session_id snaps) — no special branch, engine
  just works (pivot is address-agnostic)
- StaticsOnly path — class with no instances, only statics, key = "static"
- empty value field, malformed numerics — pass through as null
- ≥12 tests target

### 29e-1b (Layer A array expansion)

- primitive int[] / float[] / string[] expansion produces N pseudo-rows
- array of pointers expansion (`Ptr` filled) produces ptr= rows
- empty array → only summary row, no pseudo-rows
- captured < Length (cap hit) → only pseudo-rows for captured indices,
  summary row reflects total Length
- existing SPC tests must keep passing
- ≥6 tests target

### 29e-2 / 29e-3 (UI tabs + Stage 0 + Stage 1)

ViewModel-level integration tests:
- Stage 0 sortable, default filter (`Presence==N && !synthetic && Fld>=2`)
- selecting a class enables Stage 1
- Stage 1 score ranking sanity: a hand-crafted snap pair where
  `_propertyID` has S_stable=1, S_cardinality=1 → tops chart
- compound-key greedy suggestion fires when single-key has collisions
- "Run pivot" wires through to PivotEngine and populates result DataGrid
- cross-session banner copy verified

### 29e-4 — none (covered above)

---

## 11. 4-wave delivery

| Wave    | Scope                                              | Shippable independently? |
|---------|----------------------------------------------------|---|
| **29e-1**  | `PivotEngine` + unit tests, no UI                | yes (engine alone is useful for scripting/tests) |
| **29e-1b** | Layer A: primitive array index expansion in `BuildFieldIndex` + SPC tab inherits | yes (SPC immediately benefits) |
| **29e-2**  | "Class Pivot" tab — Stage 2 only, with manual class+K+V picker, cross-session banner | yes |
| **29e-3**  | Stage 0 class shortlisting DataGrid              | yes (replaces manual class picker) |
| **29e-4**  | Stage 1 field auto-discovery + greedy compound-key | yes |
| deferred   | Layer B reference array inner pivot — own Phase  | needs DLL change |
| deferred   | Anomaly auto-surface (plan.md stretch goal)      | research |

Each wave ends green on the full unit test suite and adds a plan.md "Recent batches" entry.

---

## 12. Open questions (mark before implementation)

- [ ] `S_stable` Jaccard cost for very large value sets (N>10K) —
      need to confirm worst-case isn't the bottleneck of Stage 1.
      Mitigation: cap at instance-count fraction; sample if huge.
- [ ] Whether to expose `S_stable` / `S_cardinality` / `TypePrior` /
      `NamePrior` as separate columns in Stage 1 DataGrid (transparent)
      vs collapse to a single `Score` (clean). Lean transparent —
      user can see WHY a field ranked high.
- [ ] Stage 0 array-of-reference-type: do we count their fan-out
      potential into `ArrayFieldRatio` (no — Layer B not in this Phase)
      or surface a separate `RefArrayFieldRatio` hint that "this class
      would benefit when Layer B lands"? Lean: yes, surface hint.

These are settled at implementation time, not blockers for starting.

---

## 13. Live-smoke findings (29e-2, 活俠傳, 2026-05-28)

First live run on 活俠傳 Mono (4 captures across 3 game sessions,
3,647 classes / 12-18 K instances each). The core flow works:
`Mortal.Core.GameStat`, key=`_type`, value=`_value` → key `3` shows
`10160 → 9910 → 10160 → 10060`, exactly the 銀兩 (Money) spend
trajectory. Findings + resolutions:

1. **Collision cells were unreadable** — a 21-way key collision dumped
   all 21 values inline (`<21: 100, 100, 100, …>`) and overflowed the
   grid. **Fixed (29e-2 polish)**: collision cells now render the
   instance count + DISTINCT value set, capped at 6 with a `,+N`
   suffix: `⟨21: 100, 50, 250, 18, 60, 43,+2⟩`. All-agree collisions
   collapse to `⟨21: 100⟩`. The `⟨…⟩` brackets flag the cell visually.
   The Trajectory column is now a wrapping TemplateColumn with a
   ToolTip so even long non-collision sequences are fully readable.

2. **Field-name autocomplete required typing** — empty box showed
   nothing. **Fixed**: `MinimumPrefixLength=0` on the Class / Key /
   Value boxes so the dropdown opens on focus and lists everything
   available. (Field names still require at least one hydrated
   snapshot of the chosen class.)

3. **Bad-key collisions are expected, not a bug** — `_property=99`
   matches many GameStat instances, so keying by it produces
   collision rows. The user resolved this by switching to `_type`
   (more unique paths). A compound key (e.g. `_type, _property` or
   just `_name`) disambiguates further. This is the intended
   workflow; 29e-4's key auto-discovery will surface good keys
   automatically.

### Known constraints surfaced during smoke

- **SPC cross-session on Mono finds nothing.** Mono static-field
  values aren't captured (`static_value_skipped_mono` — reading them
  wedges the game's main thread, see Phase 29a notes). Cross-session
  SPC auto-enables StaticsOnly because instance addresses don't align
  across launches — but on Mono those statics have no values, so the
  result is empty. **Class Pivot is the cross-session answer**: it
  keys by instance field VALUES, which survive session boundaries, so
  the user gets real instance-data trajectories where SPC can't.

- **Instance "Inspect" only resolves for the live session.** Pivot's
  value trajectory is just stored strings — it works across dead
  sessions fine. But the per-snap `AddressPerSnap` entries are
  process-local managed pointers; clicking "Inspect (latest snap)"
  reads that address in the *currently connected* game process. If
  the latest selected snapshot came from an earlier, now-dead
  session, the address is stale and the Inspector will read garbage
  (or fail). **Practical rule: take your final capture in the game
  session you're still attached to**, so the latest snapshot's
  addresses are live. The trajectory across older snapshots remains
  valid regardless; only the address handoff needs the live session.
  A future polish could tag each snapshot with whether its session
  is the live one and warn on Inspect — tracked but not yet built.

### Deferred to later 29e waves (confirmed with user)

- **Assembly → class picker with stability scoring** = Phase 29e-3
  (Stage 0). Replaces the manual Class FQN box: pick an assembly,
  see a sortable shortlist of classes with instance-count CV, null
  ratio, field-type ratios, so the user narrows to a good candidate
  class without knowing the FQN up front.
- **Scored field picker (popup with filter)** = Phase 29e-4
  (Stage 1). Instead of typing field names, the user sees ranked
  key candidates (S_stable / S_cardinality / TypePrior / NamePrior)
  + a greedy compound-key suggestion, in a filterable picker.

---

# Phase 29i — Pivot usability & value-driven discovery

**Status**: design (2026-06-01). Owner: dev branch. Pure-UI / engine work —
no DLL change (29i-1..-4 all read the existing gzipped snapshot blob).

## 29i.0 — Motivation

29e shipped the three-stage funnel, but live use exposed that the funnel
still assumes the user can **name** things:

1. To read a class's fields you must type its FQN, hydrate, and read the
   `KnownFieldNames` autocomplete — one class at a time. No way to *browse*
   an assembly's classes and see their shape side-by-side.
2. The Layer B array inner-pivot fields (`ArrayFieldName` / inner key /
   inner value) are free-text `AutoCompleteBox`es with **no scoring** —
   the user is back to "remember the field name", the exact problem
   Stage 1 solved for top-level instance fields.
3. Stage 0 ranks by **stability** (CV), but "stable" ≠ "holds the data I
   care about". A constant class and a noise class both score poorly/
   well for the wrong reasons. There is no signal for "this class has a
   stable identity whose *value* drifts across snaps" — which is exactly
   what gameplay state (money / HP / exp) looks like.
4. The fundamental asymmetry: **the user already knows the VALUE** (money
   reads 9410 in-game; it dropped from 10160). Today they must guess the
   class to find that value. The inverse — *value → (class, field, key)* —
   is the highest-utility operation and is missing.

The four waves below each close one gap. They share engines and **feed
each other**: 29i-1's field index powers 29i-2/-3's pickers; 29i-4's hit
hands off into Stage 1 + Stage 2 to confirm the trajectory.

```
29i-4 value/delta  ─► (class, field) candidate
                          │ auto-fill ClassFqn + value field
                          ▼
29i-1 class browser ─► pick/confirm class, see full field shape
                          │
                          ▼
29i-3 unsupervised  ─► (when no known value) rank classes by
   ranking + preview      identity-stable / value-volatile, preview rows
                          │
                          ▼
29i-2 element picker ─► scored inner key/value for array pivot
                          │
                          ▼
        Stage 1 (KeyDiscoveryEngine) ─► Stage 2 (PivotEngine) trajectory
```

---

## 29i-1 — Class structure browser (Direction 3)

**Goal**: from a single assembly, see every class's field shape at a
glance — no per-class FQN round-trip.

### Engine change — `ClassShortlistEngine`

Today `Accumulator` keeps `FieldNames : HashSet<string>` only to count
`FieldCount`; the names are discarded. Change it to **retain** an ordered
field summary:

```csharp
// new on Accumulator
public Dictionary<string,(string Type, bool IsEnum, bool IsArray)> Fields = new(Ordinal);
// preserve first-seen order so the preview is deterministic
public List<string> FieldOrder = new();
```

`ClassShortlistRow` gains:

```csharp
public required IReadOnlyList<ClassFieldInfo> Fields { get; init; } // full list
public string FieldPreviewDisplay { get; }   // first 10 names, ", "-joined, "+N more"
```

`ClassFieldInfo = { Name, Type (demangled), IsEnum, IsArray }`. Cost is
~free: Stage 0 already visits every field; we keep the name+type instead
of throwing them away. Memory: ~one small list per class (~3,600 classes ×
~20 fields × 2 refs ≈ negligible vs the hydrated blob).

### UI

- **Shortlist grid**: add a `Fields (preview)` column bound to
  `FieldPreviewDisplay` (`Width="Auto" MinWidth="280"`, per the no-star
  rule). Wrapping `TextBlock` + `ToolTip` shows the full list on hover.
- **Detail panel** below the grid (or a flyout): when a shortlist row is
  selected, a read-only sub-grid lists `Name | Type | Enum? | Array?` for
  the full field set. This is the "see the structure without switching
  class" view.
- **Goto class**: reuse the existing `OnPivotShortlistDoubleTapped` →
  `UseShortlistRow` (already fills `ClassFqn` + `AssemblyFilter`). Add a
  per-field context menu in the detail panel: **Add to Key fields** /
  **Add to Value fields** (reuse `AddFieldToKeys` / `AddFieldToValues`),
  so the browser doubles as a picker.

### Tests
- `FieldPreviewDisplay` truncates at 10 + "+N more".
- `Fields` retains type + enum/array flags; ordering deterministic.
- existing `ClassShortlistEngine` ratio tests stay green.

---

## 29i-2 — Array inner-field scored picker (Direction 2)

**Goal**: bring Stage 1-style scoring to the Layer B inner key/value
fields, replacing the free-text boxes.

### Technique — reuse `KeyDiscoveryEngine`, element mode

The element fields live in
`SnapshotFieldEntry.ArrayElements.Elements[].Fields[]` (Layer B capture).
Treat each captured element as a **pseudo-instance** and feed the same
four sub-score machine. Refactor `KeyDiscoveryEngine.Run` so the
instance source is pluggable:

```csharp
// today: InstancesOf(snap, asm, fqn) yields SnapshotInstanceEntry
// add:   an element source that yields each element's Fields[] as the
//        field set, scoped to (class, arrayField).
public sealed class KeyDiscoveryQuery {
    ...
    // null = top-level instance mode (today). Set = element mode.
    public string? ArrayField { get; init; }
}
```

Internally, when `ArrayField` is set, the per-snap value-set pass iterates
elements instead of instances; everything downstream (S_stable,
S_cardinality, TypePrior, NamePrior, greedy compound key, IsAddressLike
penalty) is **unchanged** — it operates on a flat `IEnumerable<Fields>`.

### UI

A new collapsible **"Score element fields"** sub-section inside the
Layer B expander, mirroring the Stage 1 grid:
- `Score element fields` button → runs `KeyDiscoveryEngine` in element
  mode for the typed `ArrayFieldName`.
- grid identical to `PivotFieldScoreGrid` (Field / Type / Score / Stable /
  Card / Type pr. / Name pr.), bound to a new `ElementFieldScores`
  collection.
- double-tap → `InnerKeyFieldsText`; context menu "Add to inner value" →
  `InnerValueFieldsText`. Suggested-key Apply fills `InnerKeyFieldsText`.

The existing `KnownArrayFields` autocomplete for picking *which* array
stays (it's the one free-text that's genuinely a short list); but the
**Array field** box also gets a tiny "⋯" affordance listing reference
arrays of the class with their element-count, so the user doesn't type.

### Tests
- element-mode `KeyDiscoveryEngine`: hand-crafted Layer B snapshot where
  element `_propertyID` has S_stable=1/S_card=1 → tops the chart.
- top-level mode unchanged (regression).
- empty / no-Layer-B class → 0 element scores, friendly status.

---

## 29i-3 — Unsupervised class ranking + preview (Direction 1-A)

**Goal**: when the user has *no* known value, surface "which classes look
like they hold trackable gameplay state" with an inline preview.

### Technique — add a **value-volatility** signal + gated lazy preview

Stage 0's CV measures *population* stability. The missing signal is
**field-value drift under a stable key**:

> A class is *interesting* ⇔ it has a stable identity (good key) **and**
> a numeric value field whose per-key value **changes** across snaps but
> is **not** pure noise.

Two-pass funnel (cost-gated — never run the expensive pass on 3,600
classes):

1. **Pass A (cheap, all classes)** — existing `ClassShortlistEngine`.
   Take the top `N≈40` by the current funnel order, narrowed to the
   chosen assembly + `Presence==N` + `FieldCount≥2`.
2. **Pass B (expensive, top-N only)** — per candidate class:
   - run a **lightweight** `KeyDiscoveryEngine` (compute the four
     sub-scores; pick the best single/greedy key — but cap the greedy
     `CountCollisions` re-walks, or skip greedy and take the top-scored
     field, to keep Pass B affordable);
   - for each numeric non-key field F, compute a **volatility score**:
     ```
     vol(F) = (# keys whose F value differs across ≥1 snap pair)
              / (# keys present in ≥2 snaps)
         gated by:  distinctValues(F) ≤ instanceCount   (drop noise)
                    AND distinctValues(F) ≥ 2            (drop constants)
     ```
   - `interestingness(C) = bestKeyScore(C) · max_F vol(F)`.

Sort the shortlist by `interestingness` (new optional sort column /
toggle "rank by likely game-state").

### Preview (lazy, per row)

Expanding a ranked row shows, computed on demand:
- **Top-3 key combos**: the greedy suggestion + the next two
  highest-scoring single keys.
- **Top-3 value fields**: by `vol(F)` descending.
- **3 sample trajectory rows**: run `PivotEngine.Run` with bestKey +
  bestValue, take the first 3 `PivotRow`s, render like the Stage 2
  grid (`v0 → v1 → …`).

Preview is cheap (one `PivotEngine` pass on one class) and **only fires on
expand**, so the assembly-wide ranking stays responsive.

### Cost
Pass A unchanged (~200 ms). Pass B = N×(one value-set pass + bounded
greedy) ≈ N×Stage-1 cost; with N≈40 on 活俠傳 that's a few hundred ms.
Preview is per-row on demand. All well within "click → table".

### Tests
- `vol(F)` = 1.0 for a field that changes every snap under a stable key;
  0.0 for a constant; gated to 0 for a field with distinct==instanceCount
  (noise).
- `interestingness` ranks a money-like synthetic class above a constant
  class and a noise class.
- preview returns ≤3 rows and never throws on a class with no numeric
  value field.

---

## 29i-4 — Value-driven class location (Direction 1-B)  ★ highest utility

**Goal**: user types a value they can see in-game (or a cross-snap delta);
the tool finds the `(class, field[, array element])` that holds it —
**cross-session capable** because it reads the stored blob, not live
memory (unlike Value Search).

### Engine — new `SnapshotValueLocatorEngine`

```csharp
public sealed class ValueLocatorQuery {
    public required IReadOnlyList<SnapshotEnvelope> Snapshots { get; init; }
    public string?  AssemblyFilter { get; init; }     // scope
    public string?  ExactValue     { get; init; }     // "9410" / "Money" — FormatFieldValue match
    public double?  NumericTolerance { get; init; }    // optional ± for floats
    // delta mode (across exactly two chosen snaps):
    public int?     SnapA { get; init; }
    public int?     SnapB { get; init; }
    public double?  Delta { get; init; }              // e.g. -750 (10160 → 9410)
    public bool     IncludeArrayElements { get; init; }
    public bool     NumericOnly { get; init; }
}

public sealed class ValueLocatorHit {
    public required string Assembly, ClassFqn, FieldName;
    public required bool   InArrayElement;            // field is inside an array element
    public required int    SnapHitCount;              // snaps where the value appears
    public required string SampleValue;               // matched rendered value
    public required string? SampleAddress;            // an instance address for handoff
    public double Score => /* SnapHitCount + delta-match bonus + cardinality penalty */;
}
```

### Algorithm — **set-membership, not instance-matching** (dodges the
key chicken-and-egg)

- **Exact mode**: for each `(class, field)` collect the rendered-value
  multiset per snap (reuse `SnapshotEnvelope.FormatFieldValue`; numeric
  parse for tolerance). A `(class, field)` is a hit if `ExactValue` ∈ the
  set in ≥1 snap. Rank by `SnapHitCount` then by *lower* field cardinality
  (a field where the value is rare is a stronger signal than one where
  every instance is 9410).
- **Delta mode**: a `(class, field)` is a hit if **∃ Va ∈ set(SnapA) and
  Vb ∈ set(SnapB) with Vb − Va == Delta** (within tolerance). This finds
  "a field whose value universe moved by Δ" without needing to align
  individual instances across snaps — the alignment is what Stage 1 + the
  Pivot do *next*, on the confirmed field.
- Array elements (`IncludeArrayElements`): same, but iterate
  `ArrayElements.Elements[].{Value,String}` (Layer A) and, if present,
  `.Fields[]` (Layer B).

### UI — new top "🎯 Find by value" expander on the Pivot tab

- inputs: Assembly (reuse `KnownAssemblies`), Value, optional tolerance,
  mode toggle (Exact / Delta), Delta + SnapA/SnapB pickers, "include
  array elements", "numeric only".
- result grid: `Class FQN | Field | In array? | Snaps hit | Sample | Score`.
- double-tap a hit → **auto-handoff**: fills `ClassFqn` + `AssemblyFilter`
  + the matched field into `ValueFieldsText`, then offers a one-click
  "Suggest key & run" that chains Stage 1 (`ScoreFieldsAsync` → apply
  suggested key) → Stage 2 (`RunAsync`). This closes the loop: value →
  class → key → trajectory, no manual typing.

### Cost
One linear pass over the hydrated blob per query (same budget as Stage 0;
no Jaccard). Delta mode is two set builds + a membership test per field.

### Tests
- exact match finds the right `(class, field)`; ranks rarer-value field
  above a field where everything equals the target.
- delta mode finds a field whose value set moved by Δ across the two
  snaps; ignores fields that didn't.
- array-element hit (Layer A primitive + Layer B inner field).
- numeric tolerance: float `9410.0` matches `"9410"` within ±0.5.
- cross-session snaps (mixed session_id) — engine is address-agnostic, so
  the hit is found regardless; the handoff address is the live-snap one.

---

## 29i — wave delivery

| Wave    | Scope                                                  | Ships alone? |
|---------|--------------------------------------------------------|---|
| **29i-1** | Class structure browser (retain field names in Stage 0 + preview column + detail panel + per-field add-to-key/value) | yes |
| **29i-2** | Array inner-field scored picker (`KeyDiscoveryEngine` element mode + grid) | yes |
| **29i-3** | Unsupervised ranking (`interestingness` + value-volatility) + lazy per-row preview | yes |
| **29i-4** | `SnapshotValueLocatorEngine` + "Find by value" expander + auto-handoff to Stage 1/2 | yes (★ build first if optimizing for utility) |

Each wave ends green on the full UI test suite and adds a plan.md "Recent
batches" entry. Suggested order: **29i-1 → 29i-2 → 29i-4 → 29i-3** (1 unblocks the
pickers and is lowest-risk; 4 is the highest-utility payoff; 3 is the
"no known value" fallback and the most heuristic, so last).

---

## Class Pivot UI/UX overhaul (TODO — 大修, after Phase 31-5)

**Status**: design backlog (2026-06-02). User feedback from live use on Core
Keeper (Mono x64). The Class Pivot tab's ENGINE is solid (29i-1..4 all work +
31-2 value-struct components now show as `.x/.y/.z` in pivot trajectories), but
the **layout/interaction is poor** — every analysis mode is a stacked
collapsible panel at the top, so by the time the parameter forms are visible the
results DataGrid is squeezed to near-nothing. This is a UI-only re-plan; the
engines stay.

### Problems (verbatim user report)

1. **Stacked-panel layout eats all vertical space.** The top has many parameter
   panels (Find by value / Class shortlist / Likely game-state / Suggest
   key-value / Pivot query / Array inner-pivot / Snapshots). With them expanded
   there is essentially no room for the results DataGrid at the bottom; even
   collapsed they dominate. → **Re-plan**: collapse all modes into a SINGLE
   toolbar/bar of buttons; clicking a mode opens it in a **separate window
   (dialog)**; the dialog does its work and **returns the result back** into the
   main tab's results grid. The tab body is then mostly the results grid.

2. **Missing keyword filters.** Some panels have a class/field substring filter,
   others don't. → **Every** mode's pickers/lists must have a keyword filter
   (consistent with Symbol Finder / Snapshot filters).

3. **Undefined "Stage 0 / Stage 1" jargon leaks to the user.** "Pivot query
   depends on the Stage 0 selection" — but the user has no idea what "Stage 0"
   is (it's an internal dev-pipeline name). Likewise the dependency itself is
   implicit. → Rename to user-facing terms ("Pick a class" / "Pick key & value
   fields"), and make the **dependency explicit + ordered**: a dependent step is
   disabled/greyed until its prerequisite is satisfied, with a hint ("pick a
   class first").

4. **"Layer B" jargon + all-manual params in Array inner-pivot.** "Array
   inner-pivot (Layer B)" — user doesn't know what "Layer B" means; rename to
   plain language ("Pivot array elements by an inner field"). Worse, every param
   (class FQN, key field, value field) is a manual text box, so the user has to
   go Inspector → Copy name → switch back to this tab → paste, repeatedly. →
   Provide **pickers populated from the loaded snapshot schema** (dropdown /
   autocomplete of classes + their fields), and accept a **handoff from
   Inspector/Browser/Snapshot** (right-click → "Pivot this class/field here") so
   no copy-paste round-trip is needed.

5. **UI freezes during long compute (Snapshot / SPC / Pivot) + DB I/O.** These
   operations block the UI thread for a noticeable spell with no feedback —
   looks hung. Part of it is likely the **SQLite hydrate / DB round-trip** done
   synchronously. → Run the compute + DB work **off the UI thread** (async, as
   per CLAUDE.md "Async everywhere"), show a **busy/status indicator** (spinner +
   "querying… / hydrating N envelopes…"), and keep the window responsive (the
   existing `BulkOperationGate` busy pattern is a precedent). Audit Snapshot, SPC
   Query, and Class Pivot tabs for synchronous DB/compute calls on the UI thread.

### Scope notes
- UI/engine only (no DLL). Touches `ClassPivotView` + VM, possibly shared
  busy-indicator infra, and the SPC/Snapshot tabs for the async/responsiveness
  fix (#5 spans all three).
- Keep all existing engines + handoff entry points working; this is a
  presentation + threading refactor, not a behaviour change.
- Sequence AFTER Phase 31-5 (per user).

### Wave plan + progress

| Wave | Scope (maps to problem #) | Status |
|---|---|---|
| **A** | Responsiveness (#5): move every engine `.Run()` off the UI thread (`Task.Run`) + indeterminate busy bar bound to `IsRunning` + disable Run buttons while busy (`[NotifyCanExecuteChangedFor]`). Pivot **and** SPC Query tabs. | ✅ **DONE** (dev, 685 tests green) — needs user visual verify |
| **B** | Layout (#1): replace the 7 stacked panels with a single toolbar of buttons; each opens a dialog bound to the SHARED VM; Results grid + busy bar fill the tab body. No engine/command behaviour change. | ✅ **DONE** (dev, 685 tests green) — **needs user runtime verify** |
| **C** | Jargon (#3) + keyword filters (#2): rename Stage 0/Stage 1/Layer B to plain labels; explicit ordered dependencies (disable until prereq met + hints); a keyword filter on every list. | ✅ **DONE** (dev, 685 tests green) |
| **D** | Pickers + handoff (#4, #6): array-pivot params → schema-populated pickers; Inspector/Browser/Snapshot right-click → "Pivot this class/field here". | ✅ **DONE** (dev, 685 tests green) |

**Wave A notes (2026-06-02)**: `PivotViewModel` — all 6 engine call sites
(`ClassShortlistEngine`, `KeyDiscoveryEngine` ×2, `SnapshotValueLocatorEngine`,
`ClassInterestEngine`+`PivotEngine` preview block, `PivotEngine`/`ArrayPivotEngine`)
now `await Task.Run(() => Engine.Run(query))`; the query objects are built on the
UI thread first, the compute runs on the pool, results marshal back. Indeterminate
`ProgressBar` busy bars added to the Class Pivot + SPC Query tabs (bound to
`IsRunning`); without the off-threading the bar couldn't even animate (it lives on
the UI thread). `_isRunning` gained `[NotifyCanExecuteChangedFor]` for every gated
command on both VMs so the buttons grey out during a run — important now that the
UI stays live and a second click could start a racing concurrent run.
Snapshot-tab diff/hydrate audit still pending (its capture path already uses the
bulk pipe + `BulkOperationGate`; the diff compute is the remaining candidate).

**Wave B notes (2026-06-02)**: the Class Pivot tab body is now a **toolbar of 7
buttons** (Snapshots / Pivot query / Find by value / Browse classes / Likely
game-state / Pick key·value / Array-element pivot) + the busy bar + status
banners + the **Results grid filling the rest**. Each button opens a `Window`
(`PivotQueryWindow`, `PivotSnapshotsWindow`, `PivotFindByValueWindow`,
`PivotBrowseClassesWindow`, `PivotLikelyStateWindow`, `PivotPickFieldsWindow`,
`PivotArrayWindow`) whose **DataContext is the shared MainWindowViewModel**, so
the existing `{Binding ClassPivot.*}` bindings + RelayCommands + the per-grid
DoubleTapped/context-menu handlers move **verbatim** (handlers relocated into
each dialog's code-behind). No engine/command/VM behaviour change. When a dialog
runs (e.g. Pivot query → Run, or Find-by-value → load + Suggest-key-and-run) it
populates `ClassPivot.Results`, which the tab grid reflects on close. **Builds +
685 tests green; runtime (dialog open / grid populate) NOT yet user-verified.**
Follow-ups for Wave C: (a) the old per-panel handlers in `MainWindow.axaml.cs`
are now dead (their panels moved) — harmless but should be pruned; (b) the
Results-grid context-menu handlers + `OnPivotResultDoubleTapped` stay in
`MainWindow.axaml.cs` (the grid stayed).

**Wave B fixups + Wave C notes (2026-06-02)**:
- **Sort regression fixed** — moving grids to dialogs orphaned MainWindow's
  `WireSortComparers` (FindControl returned null). Each dialog now wires its own
  comparers in `Loaded` (FieldAcrossInstancesWindow precedent). The dead
  MainWindow wirings + 4 now-unused comparer dicts + 16 moved handler methods
  (~142 lines) were pruned; only the Results-grid comparers/handlers remain.
- **Filters added** — Likely-game-state grid (`InterestFilter` + `_allInterestRows`
  / `RebuildInterestView`) and the Browse-classes field-structure grid
  (`ShortlistFieldFilter` + `_allShortlistFields` / `RebuildShortlistFieldsView`);
  the field-structure grid is now sortable too. Snapshots picker grid is
  header-sortable by capture time (already built in time order).
- **Jargon dropped** (user-facing only; internal comments keep Stage/Layer
  names) — "Stage 0 shortlist" → "Browse classes…", "Stage-0 picks" → "candidate
  classes", "(Layer B)" removed from the Snapshot-tab capture checkbox + tooltips
  + the Find-by-value tooltip.
- **Dependency gating** — "Score fields" / "Score element fields" now require a
  class (`CanScoreFields` = snapshot+busy gate AND `HasClassFqn`); the buttons
  grey out and the Pick-key/value + Array dialogs show a "① Pick a class first…"
  hint banner until a Class FQN is set.

**Wave D notes (2026-06-02)**:
- **Cross-tab handoff "Pivot this class…"** kills the copy-paste loop (#6):
  `MainWindowViewModel.PivotForClass(asm, fqn)` → `PivotViewModel.ReceiveClassHandoff`
  (fills `AssemblyFilter` + `ClassFqn`) → switch to `TabIndexClassPivot`. Wired
  from three sources: the **Browser** class-list context menu
  (`OnPivotThisClassFromBrowserClicked`, FQN from `ClassItem.DisplayName` +
  `SelectedAssembly`), the **Snapshot diff** grid context menu
  (`OnSnapshotPivotClassClicked`, FQN from `SnapshotDiffRow.ClassDisplay`, assembly
  resolved at Run), and the **Inspector** toolbar "→ Pivot" button
  (`LiveInspectionViewModel.SendToPivotCommand` → `SendToPivotRequested` →
  `OnInspectorSendToPivot`, FQN = `TargetNamespace.TargetName`).
- **Array-pivot pickers (#4)** — the array-field / inner-key / inner-value inputs
  are already `AutoCompleteBox`es bound to `KnownArrayFields` /
  `KnownElementFieldNames` (snapshot schema), and `ClassFqn` to `KnownClasses`;
  combined with the handoff filling the class, no copy-paste round-trip is needed.

**Phase 31 Class-Pivot overhaul COMPLETE** (Waves A–D) — all merged to main.

**Post-overhaul bug review (build #990, merged `f621837`)** — a leak/bug pass
over the overhaul found one HIGH issue here: Wave A moved the Pivot/SPC engines
to `Task.Run`, and `SnapshotEnvelope.Unload()` (Snapshot-tab Diff/Drop/evict on
the UI thread) cleared `env.Classes` IN PLACE — clearing the list mid-`foreach`
on the background thread throws "collection modified". Fixed by making `Classes`
settable and having `Unload()` REPLACE the reference (atomic) instead of clearing,
so an in-flight reader keeps enumerating its old list. Also a LOW re-entrancy
guard on `SuggestKeyAndRunAsync`. Everything else in the overhaul (dialog leaks,
the `SendToPivotRequested` subscription, sort wiring, dead-code prune, the
Grid+splitter layout) was verified clean. (The review's CRITICAL finding was in
the unrelated 31-5 address-finder — see value-type-structs.md.)
