# Snapshot memory & speed — streaming-decode + schema/value dedup (design)

> Status: **30a SHIPPED 2026-05-29 (both backends); 30b game stream-decode SHIPPED 2026-05-29 (IL2CPP); 30b-Mono / 30c + schema-retention deferred.**
> Motivated by the Utawarerumono (受讚頌者 循白之證, IL2CPP) live-smoke: snapshot took
> ~11 min of decode, pushed the game to ~10.8 GB and the UI to ~14.7 GB. P1c made the
> *find* phase 0.6 s; the *decode* phase was the entire cost.
>
> **Shipped (30a):** reduce-volume budget + low-population-first sort + memory
> watchdog (Phase 16.7); deduped wire (schema-once + per-instance `v[]`, IL2CPP +
> Mono); **value compaction** (the decisive win — values stored as raw JSON text, not
> retained `JsonElement`/`JsonDocument`). Live result: decode 660s→65s, game
> 10.8→6.9 GB, **UI 14.7 GB → ~1 GB/snap steady**. The original §3 "schema/value
> dedup" turned out lower-ROI than §1c value compaction — profiling showed the
> dominant UI cost was the per-value `JsonDocument`s, not schema repetition; the
> shipped fix targets that. **schema-retention dedup + 30b/30c streaming remain
> deferred (only needed for routinely capturing *full* snapshots).**

## 0. The numbers (two live smokes)

| game | find | classes | found inst | in-scope inst | decoded inst | game peak | UI peak |
|------|------|---------|-----------|---------------|--------------|-----------|---------|
| SaGa 2 FR (ok)   | ~7 s  | 10001 | 271998 | 118161 | 118161 | ~4.5 GB | — |
| Utawarerumono    | ~0.6 s| 22157 | 665059 | 263700 | 174079 (mem-truncated) | 10.8 GB | 14.7 GB |

Find is cheap. **Decode is everything**: time *and* memory scale with decoded
instance count × fields/instance.

## 1. Where the memory actually goes

The DLL is **injected into the game process**, so every DLL allocation is *game-
process* memory. The 10.8 GB peak = the game's own ~6.3 GB **+ our decode buffer
~4.5 GB**. After the snapshot streams out and frees, the game drops back to 6.3 GB
(observed). So the game's data didn't grow — **our transient decode buffer** sat on
top of it.

Two compounding design facts produce that buffer:

### 1a. Decode-all-then-stream (`snapshotBegin` builds the whole thing)
`snapshotBegin` decodes **every** class × instance × field into
`Session::classes` — a `std::vector<nlohmann::json>` holding the *entire* snapshot
([SnapshotSession.h:109](SnapshotSession.h)). `snapshotChunk` then just slices that
pre-built vector. So the game-side peak = the **whole** decoded snapshot, all at once.

### 1b. Field schema repeated per instance (the real bulk)
Each field is emitted as a json object
`{"name","type","offset","from", <value...>}` — **for every instance**
([ExportAPI.cpp decode loop](../dll/src/shared/ExportAPI.cpp)). For 174 079 instances
of (say) a 69-field class, the strings `"life_"`, `"System.Single"`,
`"CharacterData"`, the offset, etc. are stored **174 079 times**. nlohmann::json
objects are heavy (map node + key string + value node per entry ≈ hundreds of bytes
per field). That repetition is most of the 4.5 GB.

The UI mirrors the shape: `SnapshotClassEntry.Instances[i].Fields[j]` is a full
`SnapshotFieldEntry{ Name, Type, Offset, From, …, Value }`
([ProtocolModels.cs:1054](../ui/UnityDumpUI.Core/Services/ProtocolModels.cs)). 174 079 × 69
≈ **12 million** `SnapshotFieldEntry` objects, each ~100 B + a `JsonElement?` value
(which pins its `JsonDocument`). That's the 14.7 GB. 29g-1 interning dedups the
*strings* (so `"life_"` is one shared ref) but the **12 M objects still exist**.

## 2. Answers to the three questions

**Q: Why does the game working set look small (6–8 GB) but the snapshot showed 10 GB?**
Because 10.8 GB is the **game process private usage**, and our injected DLL's
~4.5 GB decode buffer is counted inside it. It's not the game's data — it's our
all-at-once decode tree. It frees after streaming (game → 6.3 GB).

**Q: Is it because instanceCap = 512 / too much garbage?**
Partly, linearly — more instances = bigger tree (that's why Phase 16.7's
budget+cap helps). But the *root* is the data design (§1a + §1b), not garbage
values. False-positive "instances" add their share but aren't the dominant cost.

**Q: Can we dedupe?**
**Yes — the single biggest win.** The field **schema** (name/type/offset/declaredIn)
is identical for every instance of a class. Store it **once per class**; per
instance store only the **values**. Removes the per-instance metadata repetition
(and, on the UI, the 12 M objects). Estimated **3–5× smaller** everywhere.

**Q: Does streaming-decode only fix walk-time memory, leaving the snapshot itself
just as big?**
**Correct — they're orthogonal.**
- **Streaming** bounds the *transient peak* during capture (game: decode→send→free
  per chunk; UI: receive→persist→release per chunk). Total data **unchanged**.
- **Dedup** shrinks the *actual* snapshot — memory everywhere **and** wire bytes,
  SQLite size, and diff-load cost.

Streaming alone → game peak ↓, UI peak ↓ (if the UI also streams to disk), but the
snapshot size / transfer / diff-load are the same. **Dedup is what makes the
snapshot itself small.** Recommendation: **dedup first**, streaming second.

## 3. Fix A — schema/value dedup (Phase 30a, highest ROI)

### Wire format (per class)
```jsonc
{
  "assembly":"Assembly-CSharp", "namespace":"", "name":"CharacterData",
  "parent":{...},
  "schema":[                              // ONCE per class
    {"name":"life_","type":"System.Single","offset":64,"from":"CharacterData","vt":true},
    {"name":"name_","type":"System.String","offset":40,"from":"CharacterData"}
    // …
  ],
  "statics":[ … ],                        // few; can stay inline (schemaIdx + value)
  "instances":[
    {"a":"0x…","v":[325.0, "娜娃", …]}     // values aligned to schema order
  ]
}
```
- `v[i]` ↔ `schema[i]`. The value keeps today's payload shape (number / string /
  `ptr` / `arrayElements` / `arrayError` …) **minus** name/type/offset/from.
- Arrays/strings are still per-instance (can't dedup across instances); only the
  field **metadata** is deduped — which is the bulk of the structural overhead.
- Bump a `formatVersion` on the snapshotBegin response so the UI knows which shape
  to parse.

### UI model
```csharp
class SnapshotClassEntry { …; List<SnapshotFieldSchema> Schema; List<SnapshotInstanceValues> Instances; }
class SnapshotFieldSchema   { string Name, Type, From; ulong Offset; bool IsEnum, IsValueType; } // F per class
class SnapshotInstanceValues{ string Address; JsonElement?[] Values; }                            // aligned to Schema
```
Replaces 12 M `SnapshotFieldEntry` with F schema entries/class + the value array.
A thin accessor (`entry.Field(i) => (Schema[i], Values[i])`) keeps call-sites readable.

### Touch list (the migration cost)
DLL: `decodeOneClass*` (both backends) emit schema once + values; `SnapshotStore`
SQLite write/read; `walkArrayElementsInline` element-field shape unchanged.
UI: `SnapshotFieldEntry` consumers — **diff engine**, **FieldIndex builder**,
**Pivot / ArrayPivot**, **SPC**, **CE-XML / CSX / SDK export**, **SnapshotInterner**,
the inspector-from-snapshot path. Mechanical but broad. Keep a back-compat reader for
old (non-deduped) snapshots already in SQLite (detect by `formatVersion` / absence of
`schema`).

### Expected win
Game decode tree ≈ 4.5 GB → **~1 GB**; UI ≈ 14.7 GB → **~3–4 GB** for the same
174 079 instances. SQLite + transfer shrink similarly.

## 4. Fix B — streaming decode (Phase 30b/c, bounds peak)

> **30b game-side SHIPPED 2026-05-29 (IL2CPP).** `snapshotBegin` now builds an
> `Il2cppStreamDecoder` (owns the moved plan + heap-walk context) and does **no**
> decode; `snapshotChunk` calls `decoder->DecodeRange(cursor,limit,out)` under a
> per-chunk `ThreadAttachGuard` (safe on IL2CPP), and each chunk's payload is
> freed after the response ships — so `Session::classes` is never the whole
> snapshot. Final `decodeStats` + `truncatedByMemory/Count` + post-stream
> `gameWorkingSet/gamePrivateUsage` ride the `done=true` chunk; `SnapshotChunkResult`
> gained those fields and `SnapshotViewModel` prefers them over the Begin values
> (falls back to Begin for a non-streaming DLL). Interface
> `ISnapshotStreamDecoder` lives in `SnapshotSession.h`; the concrete decoder is in
> the IL2CPP `#ifdef` branch of `ExportAPI.cpp`. **Mono mitigation #3 chosen** —
> Mono keeps decode-in-Begin (its standalone DLL build is untouched). DLL builds
> clean both backends; 597 UI tests green. **Live-smoke verified 2026-05-29
> (Utawarerumono): game stays ~6.4 GB private during AND after capture even at
> budget 100000 (DOUBLE 30a's 50000) — vs 30a's 6.9 GB at budget 50000. The
> ~4.5 GB transient decode buffer is gone and the game peak is decoupled from the
> budget (raising it 2× cost the game nothing). No freeze — game CPU kept
> advancing across both captures (969→1582→2237 s) with WS steady. The UI side is
> now the peak (~7.5 GB private during the 100000 capture, settling to ~3.1 GB) → 30c.**

### 30b — game side: decode-on-demand in `snapshotChunk`
- `snapshotBegin`: heap walk + plan + ascending-population sort **only** (plan is
  ~10s of MB). Return counts. **No decode.**
- `snapshotChunk(cursor,limit)`: decode `plan[cursor..cursor+limit]` *now*, emit the
  (deduped) payload, **free** the locals once the response is serialized. Game peak
  bounded to ~`limit` classes instead of the whole snapshot.
- **Mono caveat (the reason decode currently lives in Begin):** per-chunk
  `ThreadAttachGuard` accumulated BDWGC finalizers → the 32-bit Mono 活俠傳 freeze.
  Mitigations, in order of preference:
  1. Hold **one** attach for the session on the pipe-server thread (attach on first
     Chunk, detach on End) — needs the pipe handler to run chunks on a stable thread
     (verify `PipeServer` threading).
  2. `gc_collect(0)` drain per chunk (already done once post-decode; cheap-ish).
  3. Ship streaming for **IL2CPP only** first (no BDWGC finalizer issue — this is the
     game that hurts), keep Mono on decode-in-Begin until (1)/(2) is proven.

### 30c — UI side: stream to disk, don't retain the envelope
> **SHIPPED 2026-05-29.** When AutoSave is ON and a store is open, the capture loop
> writes each chunk's classes straight to SQLite and drops them: `BeginStreamingSaveAsync`
> (insert the `snapshots` row with `payload_bytes = -1` sentinel) →
> `AppendClassesAsync(rowId, chunk.Classes)` per chunk (gzip + insert the same
> per-class rows `SaveSnapshotAsync` writes — each class is in exactly one chunk, no
> PK clash) → `FinishStreamingSaveAsync(rowId, payloadBytes, fieldValueCount)`
> (stamps the real `payload_bytes`, clearing the sentinel). The capture builds a
> **metadata-only** `SnapshotEnvelope` (`PersistedId` set, `Classes` empty,
> `ClassesLoaded = false`) — diff/pivot/SPC/export hydrate it on demand via
> `EnsureLoadedAsync` → `LoadClassesAsync`, exactly like a snapshot loaded from a
> previous session. **No schema change** (the store was already one gzipped row per
> class; `field_value_count` already existed). Crash safety: an in-progress row
> (`payload_bytes < 0`) is hidden by `ListSnapshotsAsync` and swept by `OpenAsync` on
> reopen; a mid-stream chunk error deletes the partial row. **Scope: AutoSave-ON
> only** — that path already persisted every capture, so the only change is *how*
> (incremental) + dropping the in-memory copy; AutoSave-OFF keeps the in-memory +
> dirty + manual-💾 path byte-for-byte. UI peak now ~one chunk + write buffer instead
> of the whole snapshot. 603 UI tests green (incl. streamed-vs-one-shot round-trip
> parity + the sweep). Live-smoke (UI peak vs the 30b ~7.5 GB) still TODO.

- Today the UI accumulates every chunk into `classes` + a `SnapshotEnvelope` held in
  memory ([SnapshotViewModel.cs:992](../ui/UnityDumpUI/ViewModels/SnapshotViewModel.cs)),
  *then* saves to SQLite — so UI peak = whole snapshot.
- Change: write each chunk's classes to SQLite **incrementally** and **don't** keep
  the full envelope after capture. The snapshot lives on disk; `EnsureLoadedAsync`
  already loads on demand for diff. UI peak bounded to ~chunk + write buffer.
- Diff/Pivot/SPC already load snapshots on demand; with dedup they're small.

## 5. Recommended sequence & rationale

1. **30a dedup** — do first. Broadest win (memory everywhere + disk + transfer +
   diff-load), and may alone bring mid-size games under control even with
   decode-in-Begin. Risk: broad but mechanical UI refactor + SQLite back-compat.
2. **30c UI stream-to-disk** — bounds UI peak for huge games; medium risk.
3. **30b game stream-decode** — bounds game peak; gated on the Mono-attach question
   (ship IL2CPP-first).

Interim (already shipped, Phase 16.7): `instanceDecodeBudget` + ascending-population
sort + lower `instanceCap` keep the current build usable while Phase 30 lands.

## 6. Risks / open questions
- **Diff stability across format change** — the diff matches by class + field; with
  schema/value split, match by `(class, schema.name, instance-key)`. Verify the
  cross-session matching (instance addresses aren't stable; statics + singletons are).
- **SQLite back-compat** — old snapshots must still load. Version-tag the row.
- **Pipe threading for the session-held attach** (30b mitigation 1) — confirm chunks
  run on one thread.
- **`JsonElement?[]` retention** — values still pin a `JsonDocument`; pair with the
  deferred 29g-3 `RawJsonValue` (string-backed) to fully drop the JsonDocument pin.
