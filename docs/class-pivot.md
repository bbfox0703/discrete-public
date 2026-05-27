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
   snap 2. Concrete case: 활俠傳 `Mortal.Core.GameStat` for 銀兩 (Money)
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

A real Unity game has ~3000 classes (활俠傳 Assembly-CSharp). Brute-force
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
per field is a tiny bit-bucket update (count + null-check). For 활俠傳-
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
typical 활俠傳 snap this is < 50 K rows on top of ~1 M existing →
+5% memory. Acceptable.

### Layer B — inner-pivot on reference array elements (deferred to next Phase)

For `List<Item>` / `Item[]` where each element has its own key field
(`_propertyID`), the correct fix is pivot keyed by an *inner* field,
not by index. This needs DLL change:
`SnapshotArrayElement` must carry the inner instance's `Fields[]`,
not just `{Address, Ptr, Value}`. Plus instance-cap / depth-cap design
to bound capture size.

Layer B = own Phase, sequel to 29e. Tracked separately in plan.md once
29e ships.

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

Each wave ends green on the full unit test suite and writes a STATUS.md TL;DR.

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
