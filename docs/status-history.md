# UnityCEDumper — Session Hand-off Archive

> Historical "What landed" entries for sessions older than two
> hand-offs ago. Carved out of [STATUS.md](STATUS.md) on 2026-05-19
> to keep the main hand-off doc readable.
>
> The current + most-recent-prior session stays in
> [STATUS.md](STATUS.md). Older sessions land here in
> reverse-chronological order — newest section is at the top.

---

## Sessions older than the Phase 29c/d ship (carved 2026-05-29)

> Build #664 (snapshot false-positive root-cause fix), build #647
> (32-bit snapshot OOM mitigation), build #644 (Phase 29 polish
> filter bundle), build #637 (Phase 29b ship), build #602 (Phase
> 29a ship + array-element smoke), build #593 (Phase 28b-v3.1
> v29 layout dead-end), and pre-Phase-29 reference / verification
> matter. All previously in STATUS.md; carved here so the main
> hand-off doc focuses on Phase 29c/d + the next session pickup.

### Previous milestone — Snapshot FindAllInstances false-positive root-cause FIX (build #664 UI / DLL #650)

---

## TL;DR — what's left to pick up (build #664 session)

**Snapshot false-positive bug — proper fix SHIPPED + live-verified on
Mortal (Legend of Mortal / 活俠傳, Mono x86).** Eliminates the multi-
session mystery where known-stable instance addresses (CE-pinned
money 銀兩 GameStat) didn't surface in the diff DataGrid even with
the right filters.

### Root cause

`FindAllInstances` does a 2-level scan — for each qword in memory,
deref it and check whether the deref is in our known class set.
That accepts not just real `MonoVTable*` pointers, but also any
**non-vtable Mono structure whose first qword happens to be a
class pointer** — RGCTX cache slots, internal Mono lookup tables
that store vtable pointer values as their payload, etc. On dense
classes (Mortal's `GameStat` has 152 live instances) the false
positives crowded out real instances on the auto-clamped 256 cap,
silently dropping the money instance and making the diff useless.

### Fix (DLL #650, MonoAddressFinder.cpp)

**Structural validation via the MonoDomain field.** MonoVTable
layout (from `mono/metadata/class-internals.h`):

```
+0           MonoClass*       (existing classSet check)
+ptr_size    MonoGCDescriptor gc_descr
+2*ptr_size  MonoDomain*      ← NEW: must equal GetRootDomain()
+3*ptr_size  MonoType*
```

Real vtables ALWAYS have the root domain pointer at offset
`2*ptr_size`. Random non-vtable structures whose first qword is a
class pointer almost never coincidentally also have the root
domain pointer at exactly that offset. One extra SEH-safe pointer
read per candidate, no Mono runtime calls, zero BDWGC interaction
= zero risk of the build #637 wedge.

### Live-verify result on Mortal

| Metric | Before (DLL #648 partial fix) | After (DLL #650 proper fix) |
|---|---|---|
| GameStat captured per snapshot | 256 (cap hit, mostly garbage) | **134-135** (matches live `findInstances` ~152) |
| UI memory per snapshot | 1.5+ GB | **~99-139 MB** (15× reduction) |
| Total UI memory (2 snapshots) | 3+ GB | **~238 MB** |
| Money diff row | Missing — known-stable address not in capture | **`Mortal.Core.GameStat _value 0x28 at 0x29BFACC0 10160 → 9910 (Δ=-250)`** ✓ |
| Diff total | `7,110 changed, 759,804 added, ... 0 found in value range` | `1,688 changed, 46,999 added, 18,839 removed → 50 in [9000+]` |

Per-instance diff dump on the money GameStat now reads as expected:

```
[=] _name = "銀兩"   _property = 99   _type = 3
[~] +0x28 _value : 10160 → 9910
[=] _min, _max, _limitAddition, _default, m_CachedPtr, OnValueChanged, _levelText, _statAdditionData, _totalAddData (all unchanged)
```

### Residual / known minor

One stray entry slipping through per snapshot at addresses in
0xB2Dxxxxxx range (Mono-related family of structures that happen
to have both a class pointer + domain pointer at the right
offsets — probably per-method or generic-instantiation MonoVTable
aliases). ~99% precision, not blocking. Could refine further by
also validating `*(qw + 3*ptr_size)` is a plausible `MonoType*`,
but diminishing returns.

### Companion mitigations from yesterday (still active)

The 32-bit OOM mitigation bundle from yesterday (build #645)
stays as a safety net but is **much less essential now** that
capture isn't bloated with noise:

1. **DLL pre-check abort gate**: refuses `snapshotBegin` if game
   process committed memory ≥ 3 GB.
2. **UI auto-clamp**: handshake carries pointer size; 32-bit
   targets get InstanceCap ≤ 256 / ArrayCap ≤ 64 by default.
3. **Live game-memory banner** in Snapshot tab: real-time
   WorkingSet + committed display with warning thresholds at
   2 GB / 3 GB.

With #650's noise reduction the user could probably push
InstanceCap back to 512 or higher on 32-bit Mortal without
hitting the OOM ceiling.

### Outstanding for next session

- Phase 29c (SQLite cross-session persistence) still deferred.
- Phase 29d (SPC constraint query) still deferred.
- Phase 29e (Full/Differential snapshot architecture) proposal
  documented in plan.md, deferred until multi-snapshot workflow
  needs it.
- Phase 29f (residual false-positives — the 0xB2D family) —
  pure polish, defer unless they cause user-visible issues.
- Mono static field VALUE capture (opt-in checkbox) still
  deferred.

---

## TL;DR — what's left to pick up (build #647 session)

**32-bit snapshot OOM mitigation SHIPPED.** Closes the build #646
활俠傳 CTD with a four-layer defense:

1. **DLL pre-check abort gate** (Mono + IL2CPP `snapshotBegin`):
   refuses the snapshot if game is x86 AND `PrivateUsage` ≥ 3 GB.
   Returns structured `{"error": "Game process at X.X GB private
   memory — refusing snapshot to avoid OOM crash on this 32-bit
   Mono target. Lower InstanceCap (try 128) and ArrayCap (try 32)
   then retry, or restart the game to free memory before
   snapshotting."}`. Game-CTD becomes a recoverable UI banner.
2. **UI auto-clamp on x86 connect**: handshake now carries
   `game_pointer_size` / `game_working_set` / `game_private_usage`.
   `SnapshotViewModel.OnGameConnected(ps, ws, pu)` clamps
   `InstanceCap` ≤ 256 + `ArrayCap` ≤ 64 on 32-bit targets if
   currently above. Never clamps upward — manual user picks stay
   respected.
3. **Game memory banner** in the Snapshot tab: post-handshake row
   shows `Game: 32-bit Mono — WorkingSet 1.42 GB, committed 1.78 GB
    — snapshot caps auto-clamped for safety`. Suffix escalates to
   `⚠ approaching 4 GB ceiling` at 2 GB and `⛔ over 3 GB — DLL
   will refuse next snapshot` at 3 GB. User sees danger before
   they hit the wall.
4. **Status enrichment**: streaming + final status messages now
   show both UI WorkingSet AND game-process memory (WorkingSet +
   committed), so the user can watch the game grow during
   capture.

**`ShowChanged` default flipped to OFF** the same session — user
moved primary workflow to value-range / unchanged / added+removed.

DLL build #645 clean (both Mono + IL2CPP + x86 Mono, fresh
10:50 timestamps). UI build clean, 363 tests still green.

### Outstanding for next session

* Live-smoke the mitigation: take snap #1 on 활俠傳, **verify**:
  - Banner shows "Game: 32-bit Mono ..." after inject
  - Defaults auto-clamp to 256/64 (NumericUpDowns reflect)
  - Snap #2 either completes safely OR fails fast with the
    "Game process at X.X GB" refusal banner (no game CTD)
* Phase 29c (SQLite persistence) and 29d (SPC constraint query)
  still deferred from the original Phase 29 plan.
* Mono static field VALUE capture (opt-in checkbox) still
  deferred.

---

## TL;DR — what's left to pick up (build #644 session)

**Phase 29 polish bundle SHIPPED.** Five things landed across three
commits + one DLL rebuild on top of the build #637 Phase 29b ship:

| Commit | What landed | Trigger |
|---|---|---|
| `db8bb9b` | Noise-reduction filters on diff DataGrid: `DiffRowKind` classifier (Numeric/Pointer/String/Error/Other), value-range filter (decimal? Min/Max, either-side semantics), pointer-only mask, large-string mask (default 256 chars — catches `System.Exception._stackTrace` blobs), field-name substring filter, top-30 field-name stats panel with one-click exclusion. `ChangedOnly` refactored into independent `ShowChanged`/`ShowAdded`/`ShowRemoved`. `FilteredDiffRows` materialised into `ObservableCollection` (single O(N) rebuild on filter-prop change vs prior per-render predicate eval). | User's 1,186-changed-row 활俠傳 diff was too noisy to scan |
| `db8bb9b` (same) | "Include unchanged" capture toggle + "Unchanged" filter toggle — lets the user use value-range / field-name filters across the whole captured state, not just what differed between A and B. Default OFF (10–100× row count when on). | User asked for value-range filtering on unchanged data ("uncheck Changed → empty grid" UX confusion) |
| `db8bb9b` (same) | **Tab-index constant swap fix** in [MainWindowViewModel.cs:324](../ui/UnityDumpUI/ViewModels/MainWindowViewModel.cs) — `TabIndexAddressLookup` (3) and `TabIndexSymbolFinder` (4) were inverted relative to actual XAML order. Every Address Lookup cross-tab handoff has been silently landing on Symbol Finder since Phase 25. Surfaced when the new snapshot-diff right-click menu exposed it. | User: "address lookup, UI 卻帶我到 Symbol Finder tab" |
| `d18443f` | Capture cap defaults bumped: InstanceCap 100→512, ArrayCap 50→256 (still under the prior hard-cap). Live `Process.WorkingSet64` tracking added to every status-line tick — `Capturing… 12.3 s — process 1.42 GB (+340 MB)`. Final snapshot + log line carry the same memory annotation. | User's GameStat instance at `0x29B0ACE8` sat past position 100 in the heap-walk order; lower defaults silently missed it |
| `06984bf` | DLL hard-caps raised: instanceCap 1024→**8192** (ExportAPI both backends + internal `FindAllInstances` ceilings), arrayCap 256→**4096**. UI `NumericUpDown.Maximum` raised to match. Defaults stay at 512/256 — this is just headroom. **Rebuilt DLL build #643** (both Mono + IL2CPP + x86 Mono, fresh 10:21 timestamps). | User: "DLL's hard-caps 應該要再拉高" |

UI build clean, 363 tests still green across the bundle.

### Live-smoke result (build #646, 활俠傳, Mono x86 PID 26296)

* Snap #1 (InstanceCap=512, ArrayCap=256, the new defaults):
  ✅ 3,647 classes / 119,370 instances / 19,680 arrays captured
  in 15s. UI WorkingSet 1.83 GB (+1.62 GB).
* Snap #2 (~2min later, same caps):
  🚨 **Game CTD** during chunk drain. DLL `snapshotBegin` returned
  clean with 258K instances / 30K arrays; ~6s later UI logged
  `Snapshot.Take failed: server closed pipe before responding`.
* Root cause: DLL caches the entire snapshot's JSON payload in the
  game process; on 32-bit Mono the 5–10× scaling vs old defaults
  pushes past the 4 GB user space. See
  [lessons-learned.md §"Phase 29 high-default-caps wedge 32-bit Mono games on snapshot #2"](lessons-learned.md#phase-29-high-default-caps-wedge-32-bit-mono-games-on-snapshot-2--dll-side-json-cache-pushes-past-4-gb-user-space).
* **`ShowChanged` default flipped to OFF** (`5f2-tbd`) — user
  feedback: the value-range / unchanged / added+removed
  workflows are now the primary entry points.
* New plan.md TODO: **32-bit snapshot OOM mitigation** (auto-clamp
  defaults when `IsWow64`, surface game-process working-set in
  `snapshotBegin` response, streaming-chunk architecture as
  bigger-refactor backstop).

**Workaround until mitigation lands**: on 32-bit Mono targets,
**manually lower InstanceCap to ≤256 / ArrayCap to ≤64** before
the second snapshot if the first one shows UI WorkingSet > 1 GB.

### Known follow-ups (deferred — Phase 29c / 29d)

1. ~~**Noise reduction on diff result**~~ — ✅ SHIPPED `db8bb9b`.
2. ~~**Stack-trace strings appearing in diff**~~ — ✅ Covered by
   the "Hide large strings > N chars" filter (default ON, 256 char
   threshold).
3. **Static field VALUE on Mono** — still metadata only. Optional
   "Capture static values (may freeze game briefly)" checkbox is
   the design; not started.
4. **Phase 29c — SQLite cross-session persistence** — original 29
   plan item; will need to handle the "stale" flag and
   `game_build_id` properly when reading snapshots from a previous
   game launch.
5. **Phase 29d — SPC-style multi-field constraint query** — long-
   deferred design.

---

## TL;DR — what's left to pick up (build #637 session)

**Phase 29b — Snapshot tab + diff UI SHIPPED + LIVE-VERIFIED on all
three Mono / IL2CPP / x86 targets**. The Snapshot tab (MainWindow
position 7) captures the running game's managed state via Phase 29a
RPCs, diffs two snapshots, and surfaces row-level changes with
class.field.instance context attached. Strategy C UI filter (synthetic-
types checkbox, default ON) shipped. Cross-game snapshot stale-marking
shipped (game_build_id added to handshake). AOT sort comparers wired
for the diff DataGrid (avoiding the documented three-layer sort trap).

Took **five iterations of debugging** to get there because Mono on
32-bit + x64 has surprisingly different freeze behaviour from IL2CPP.

| Build | Iteration | What landed |
|---|---|---|
| #596 | v1 | Per-chunk decode under per-chunk attach. 활俠傳 froze mid-snapshot AND post-snapshot. |
| #598 | v2 — decode-in-Begin | One outer attach for whole decode. 활俠傳 freeze gone; BongoCat post-close hang appeared. |
| #602 | v2.1 — memoise ReadClassInfo | Global ClassInfo cache. BongoCat close-hang fixed. |
| #604–612 | 29b UI + system-asm exclude + 27c smoke fix | UI tab + ulong DTO + JSON parse fixes + open-generic filter. |
| #629 | v4 round 1 — batch attach + heap-walk yield | Tried "yield often" approach. BongoCat regressed (game hangs after RPC). |
| #631–633 | v4 round 2–3 — revert batch + gc_collect drain + Sleep | Drain ran in 19-22ms; didn't help. |
| #637 | **v4 round 4 — skip Mono static field VALUES** | **The actual fix.** mono_class_vtable from worker thread → BDWGC tracking entries → game's main thread runs catch-up GC on next allocation → freeze. Removed the call. Static field METADATA still captured; user gets value on-demand via Browser tab. **All three targets responsive.** |

Live-verified at build #637 across:
* **SaGa F2R (IL2CPP v29 / Unity 2021.3 / x64)**: full snapshot + diff
  with real numeric Δ values (e.g. `MainGame/DVECTOR.vx 110 → 267 (+157)`).
  Static field VALUES captured (IL2CPP path keeps ResolveStaticBaseAddress
  — different GC architecture, no main-thread catch-up problem).
* **BongoCat (Mono / Unity 6000.2 / x64)**: snapshot completes, game
  stays responsive (cat animates, can be closed cleanly).
* **활俠傳 (Mono x86 / Unity 2020.3)**: snapshot completes
  (~60s for 3,647 user classes / 78K instances), BGM continues
  uninterrupted, game closeable normally.

Strategy C UI filter (synthetic-types checkbox, default ON), changed-
only / added / removed toggles, class-name substring filter all
working. Cross-tab handoff (diff row → Inspector / Address Lookup)
shipped. DataGrid sort comparers wired per `aot-pitfalls.md` §4.5.

### Known follow-ups (deferred — Phase 29 polish / 29c / 29d)

1. **Noise reduction on diff result** (user feedback): with 1,186
   changed rows on 활俠傳 + SaGa F2R diff, hard to find signal. UI
   could expose:
   - Old/New value range filter (numeric)
   - Pointer mask toggle (hide rows where Old/New start with "ptr=")
   - Field-name keyword filter (currently only class-name filter)
   - Distinct-count statistics (fields appearing in N+ rows often
     turn out to be noise)
   - Skip rows where Δ is just a pointer-address change (e.g.
     re-allocated object — value moved, content same)
2. **Stack-trace strings appearing in diff** — System.Exception
   instances have a `_stackTrace` field that's a managed array;
   when captured + serialized + diffed, produces huge string blobs
   that drown out real changes. Filter or truncate.
3. **Static field VALUE on Mono** — currently only metadata.
   Possible future: opt-in "include static values (may freeze
   game briefly)" checkbox.
4. **Phase 29c — SQLite cross-session persistence** — original
   29 plan item; will need to handle the "stale" flag and
   game_build_id properly when reading snapshots from a previous
   game launch.

---

## TL;DR — what's left to pick up (build #602 session)

**Phase 29a — Snapshot DLL layer SHIPPED + LIVE-VERIFIED + bundled Phase
27c live-smoke closed.** Three RPCs (`snapshotBegin` / `snapshotChunk` /
`snapshotEnd`) ship on both backends. The "全抓" default scope works as
designed — user doesn't have to pick a class; system grabs everything.
Three iterations took it from "concept" to "stable on the freeze-prone
32-bit Mono target":

| Build | Iteration | What landed |
|---|---|---|
| #596 | v1 baseline | Per-chunk decode under per-chunk attach guard. Worked on SaGa F2R but **hung 活俠傳** mid-snapshot AND left a residual game freeze (BDWGC finalizer accumulation across 542 attach cycles — same family as the 44s shutdown-stall lesson from Phase 27). |
| #598 | v2 — decode-in-Begin | All per-class decode moved to `snapshotBegin` under ONE outer `ThreadAttachGuard`. Chunks became pure JSON slicers (zero Mono runtime calls). 活俠傳 freeze gone; **but BongoCat now froze on game-close**. |
| #602 | v2.1 — memoise ReadClassInfo | Volume math caught the BongoCat regression: 20K instances × ~4 parent chain depth = ~80K ReadClassInfo calls per snapshot → BDWGC small-object pressure → game stalls when forced to drain on close. Fix: build chain + ChainInfo ONCE per class, reuse for all its instances. Mono call volume drops 40x. BongoCat: 12.3s → 8.4s and **no close hang**. |

Live-verified across three targets at build #602:
* **SaGa F2R (IL2CPP v29 / Unity 2021.3 / x64)**: `Assembly-CSharp`
  narrow scan → 2,451 classes / 73K instances / 7,804 array fields
  captured / 1,228 open-generic skipped / 53K array errors (correctly
  flagged as `arrayError = implausible_length | header_unreadable` —
  false-positive instances from synthetic types like `<Module>`,
  `<>f__AnonymousType*` etc). Total 42.4s.
* **BongoCat (Mono / Unity 6000.2 / x64)**: 448 classes / 20K instances
  / 44 array fields captured (`SteamInventoryTest.m_SerializedBuffer:
  Byte[1900545]`, `BattleRoyale._roundDuration: RoundDuration[95]`,
  etc). Total 10.3s. **No close hang.**
* **活俠傳 (Mono x86 / Unity 2020.3)**: 21 classes / 787 instances /
  14 array fields captured. Total 48.7s. No freeze. (32-bit heap walk
  is the bottleneck — 1.1 GB scanned per snapshot.)

**Bundled Phase 27c live-smoke — CLOSED**. The snapshot walker drills
into every `T[]` field it finds, exercising the `whatIsAt` array
element resolver path. Real arrays decode element-by-element; false-
positive arrays (open-generic `T[]` placeholders, garbage pointer
targets) get filtered or surface as `arrayError`. Three game targets
all produced sane array captures + sane error rejections.

### Known subtle behaviour (not blocking)

* **BongoCat post-snapshot timer pause** — user reported the in-game
  timer stops updating until mouse motion over the game UI nudges
  Unity's input system to wake coroutines. Almost certainly BongoCat's
  own coroutine pacing recovery after our brief Mono attach; not a
  Discrete bug. Transient, not blocking.
* **SaGa F2R array-error rate** — 53K of 73K instances flag
  `arrayError`. These are false-positive "instances" of synthetic
  types: heap holds class-pointer references for metadata / RGCTX
  cache that aren't real object headers. Phase 29b UI strategy **C**
  (no DLL-side filtering; UI ships a "Hide synthetic types" checkbox
  default ON) handles this — see Phase 29b plan.

### Next: Phase 29b — In-memory snapshot store + diff UI

* Add `Snapshot` tab to MainWindow (position 7).
* VM holds `ObservableCollection<SnapshotEnvelope>` — in-memory only at
  this stage. Take Snapshot button fires `snapshotBegin`, loops
  `snapshotChunk` until done, builds local map per (class FQN, instance
  addr, field offset) → value.
* Diff between two snapshots: changed-only DataGrid columns
  `Class / Field / Instance / Old / New / Δ / %Δ`.
* **UI filter strategy C** (per user decision 2026-05-26): no DLL-side
  filtering of synthetic types; UI checkbox "Hide synthetic types"
  default ON filters by name pattern (`<Module>`, `<>f__*`, names
  containing backtick + no generic argument resolution).
* Cross-tab handoff: right-click diff row → Inspector / Address Lookup
  / Copy address.
* No DB dependency yet — that's 29c.

Estimated ~1 day.

---

## TL;DR — what's left to pick up (build #593 session)

**Phase 28b-v3.1 attempted, hit v29 layout dead-end** — v3.0 still ships
fine (no regression vs build #581). Three slot→class mapping hypotheses
all failed:

| Attempt | Result |
|---|---|
| (1) types[] index | out of range (typesCount=29456, observed indices reach 50999) |
| (2) typeDef via per-image typeStart + image_get_class | out of range (typeDefMap size=10001) |
| (3) `Il2CppMetadataRegistration.metadataUsages[]` at +0x78 | **NULL in v29** — entire array was removed in IL2CPP v29 |

Confirmed by live diagnostic on SaGa F2R (PID 36064 init log:
`metadataUsages=0x0000000000000000 count=0`).

Diagnostic + scaffolding infra all shipped + ready for v3.2:
* `diagnoseClassReferences` pipe RPC
* `dumpLazyInitSlots` pipe RPC
* `IL2CPPLazyInitSlotScanner` with token capture (`tokenKind`/`tokenIndex`
  per slot)
* `IL2CPPMetadataRegistration::MetadataUsagesArray()` exposure (returns
  null on v29 but works on v24/v27 if back-port needed)
* `Test-LazyInitSlots.ps1` pretty-printer

### v3.2 — DEFERRED INDEFINITELY (was: next session pickup)

After user feedback: `<>c.<>9__0_X` xref is a genuinely niche use case.
v3.0 already covers the 95% real-world workflow (singletons, user
statics, instance fields, method xref). Cheat-table makers don't need
lambda-cache xref (lambda caches aren't gameplay state). Reverse
engineers who want to trace LINQ logic can reach the lambda body via
Methods tab navigation on the parent class.

**Only resurrect v3.2 if an actual user reports needing it.** Until
then the v3.0 banner advisory ("try playing the relevant game content
to trigger the lambda body, then re-scan") cleanly explains the
limitation to anyone who stumbles onto it.

### v3.2 cookbook (preserved here for future revival)

Reverse-engineer v29's `il2cpp_codegen_initialize_runtime_metadata`
function — the canonical init target shared by all 7,900 lazy-init
slots on SaGa F2R. Steps:

1. Find the function (frequency analysis on `callTarget` field in
   `EnumerateLazyInitSlots` — it's `0x...1306CFF0` on SaGa F2R).
2. Zydis-disassemble it (Zydis already vendored). Trace what it reads
   from `rcx` arg — that's where the token-decode protocol lives.
3. Likely path: the function reads `Il2CppMetadataUsageList` /
   `Il2CppMetadataUsagePair` from global-metadata.dat (these tables
   still exist on disk per Il2CppDumper; Phase 5's `MetadataParser`
   doesn't expose them yet).
4. Extend `MetadataParser` to read those tables; build
   `tokenIndex → (kind, sourceIndex)` map at startup.
5. For sourceIndex with kind=TypeInfo, look up in `typeDefMap` (already
   built). Replace `ResolveTypeInfoSlotClass`'s current stub.

Estimated 2-4 days when/if needed.

---

## TL;DR — what's left to pick up (build #581 session)

## TL;DR — what's left to pick up

**Phase 28b-v3.0 lazy-init slot scanner — SHIPPED + LIVE-VERIFIED on SaGa F2R**
(2026-05-26, build #581). The `<>c` follow-up flagged at the end of v2
turned out to be a different beast than the plan-doc hypothesised. New
diagnostic tooling + empirical disasm analysis revealed:

* **Plan-doc hypothesis was wrong**: `g_codegenModule->types[]` doesn't
  exist in `Il2CppCodeGenModule` ([vendor/Il2CppDumper/.../Il2CppClass.cs:261](../vendor/Il2CppDumper/Il2CppDumper/Il2Cpp/Il2CppClass.cs)).
* **Real codegen pattern**: `<>c` class pointer IS stored in `.data`, but
  in **per-AOT-method lazy-init cache slots** populated by
  `il2cpp_codegen_initialize_method` on first method execution. Slot is
  NULL until the AOT method first runs.
* **v3.0 fix**: new [`IL2CPPLazyInitSlotScanner`](../dll/src/il2cpp/IL2CPPLazyInitSlotScanner.cpp)
  scans `.text` for `lea reg, [rip+SLOT]; call rel32` 2-instruction
  sequences. Caches the slot list. v2 fallback reads each slot's current
  value with `SafeRead`; if equal to target class ptr, merges into chain
  detector input. New `confidence="chain-lazy"` tag distinguishes v3
  matches from v2's `"chain-exact"`.

Live-verify results (SaGa F2R, build #581):
* **Test 1 — `SteamManager.s_instance`**: ✅ 11 chain-exact matches preserved
  (no regression vs v2-only build #565).
* **Test 2 — `MainGame/StrReader2UShortAry/<>c.<>9__0_0`**: ⚠️ still 0
  matches in this session. Log shows `v3LazyInitMatching=0` — no AOT
  lambda method had executed in the session, so all lazy-init slots
  were null. Banner advisory rewritten to mention all 3 scanners ran +
  give actionable hint ("play the relevant game content to trigger the
  lambda body first, then re-scan").

**v3.1 (deferred to next session)**: parse `MetadataInitializerEntry`
struct layout to map slot → class statically, closing the never-executed
case. Diagnostic tooling already in place to dump entry bytes for
empirical layout reverse-engineering. Estimated 1-2 days.

### Files added this session

* [dll/src/il2cpp/IL2CPPClassReferenceDiagnostic.{h,cpp}](../dll/src/il2cpp/IL2CPPClassReferenceDiagnostic.cpp) —
  diagnostic helper that dumps every storage slot (module + heap)
  holding the target class ptr / `static_fields` ptr / `FieldInfo*`,
  plus 128 B context per hit + navigation candidates with auto-pulled
  AOB.
* [dll/src/il2cpp/IL2CPPLazyInitSlotScanner.{h,cpp}](../dll/src/il2cpp/IL2CPPLazyInitSlotScanner.cpp) —
  v3.0 scanner itself.
* [tools/Test-DiagnoseClassRefs.ps1](../tools/Test-DiagnoseClassRefs.ps1) —
  test harness; pretty-prints diagnostic + auto-pulls raw `.text` bytes
  from candidate AOT methods via `getMethodAobPattern`.
* New pipe RPC `diagnoseClassReferences` (in [ExportAPI.cpp](../dll/src/shared/ExportAPI.cpp))
  + magic-string `CmdDiagnoseClassReferences`.

### v3.1 next-steps cookbook

1. Add fieldName arg to diagnostic (already done) → run on `<>c.<>9__0_0`
2. Walk lazy-init slots, dump 32 bytes at each → look for `MetadataInitializerEntry`
   layout. Likely shape `{Il2CppClass* slot; uint32_t typeIndex; …}` with
   typeIndex < `Il2CppMetadataRegistration->typesCount`.
3. Once layout confirmed, extend `IL2CPPLazyInitSlotScanner` to also
   capture typeIndex per slot. Cross-look via existing
   [`IL2CPPMetadataRegistration::TypesArray()`](../dll/src/il2cpp/IL2CPPMetadataRegistration.h)
   + `il2cpp_class_from_il2cpp_type`.
4. In `IL2CPPFieldXref` v2 fallback, also use this static mapping
   (in addition to v3.0's runtime value check). Then `<>c.<>9__0_0`
   xref works regardless of whether the lambda has run.

---

(Previous TL;DR — for Phase 28b-v2 ship (build #565) and earlier, kept
below for cross-PC handoff context.)

**Phase 28b-v2 IL2CPP chain detector — LIVE-VERIFIED on SaGa F2R**
(2026-05-25 late evening, build #565). The full class-pointer
chained-disassembly redesign shipped + live-verified end-to-end:
`SteamManager.s_instance` (singleton with init-call interleave +
heap-allocated `static_fields` — the canonical case v1 couldn't see)
returned **11 unique chain-exact matches** across 6 methods
(`get_Instance`, `get_Initialized`, `InitOnPlayMode`, `Awake`,
`OnEnable`, `OnDestroy` — 7 reads + 4 writes). Yellow advisory banner
is gone for this case, replaced by real data.

What shipped in Phase 28b-v2 (six sub-phases):
* **28b-v2-1**: Zydis x64 disassembler integrated into IL2CPP DLL
  (~700 KB cost). Mono DLL byte-identical — Zydis only linked into
  the IL2CPP path.
* **28b-v2-2**: [`IL2CPPStaticFieldsOffset.cpp`](../dll/src/il2cpp/IL2CPPStaticFieldsOffset.cpp) —
  runtime-version-independent discovery of `offsetof(Il2CppClass,
  static_fields)`. Auto-discovered `0xB8` on SaGa F2R (v29 IL2CPP /
  Unity 2021.3).
* **28b-v2-3**: [`IL2CPPClassPointerScanner.cpp`](../dll/src/il2cpp/IL2CPPClassPointerScanner.cpp) —
  inverse pointer scan of `.data` / `.rdata` for stored
  `Il2CppClass*` slots. Mirrors AOBMaker's `Il2CppPeReader.FindPointerReferences`
  pattern.
* **28b-v2-4**: [`IL2CPPChainDetector.cpp`](../dll/src/il2cpp/IL2CPPChainDetector.cpp) —
  Zydis forward-trace with **multi-register tracking** (set of regs
  per chain stage, not single reg), CALL tolerance (drop volatile,
  keep non-volatile), COND_BR tolerance (fall-through trace continues
  past `jne skip_init`), `mov reg,reg` propagation, 32-instr trace
  window. Plus REX-skip dedup + hit-IP final dedup so the same chain
  endpoint isn't reported twice.
* **28b-v2-5**: Wired as v1 fallback in `FindStaticFieldReferences` —
  only runs when v1 returns 0 AND target is outside module (preserves
  v1 fast path for the rare baked-into-module statics).
* **28b-v2-6**: SaGa F2R live verify ✅.

Real-world IL2CPP codegen pattern decoded:

```asm
mov rcx, [rip + CLASS_PTR_2824308]    ; hop-1 candidate 1
cmp dword ptr [rcx + 0xE0], 0          ; init flag check
jne short skip_init                    ; ← we tolerate cond branches now
call il2cpp_runtime_class_init         ; ← we tolerate CALLs (volatile-clobber model)
skip_init:
mov rax, [rip + CLASS_PTR_2824308]    ; hop-1 candidate 2 (reload after CALL)
mov rcx, [rax + 0xB8]                  ; hop 2 — offsetof static_fields
mov rbx, [rcx + 0x08]                  ; hop 3 READ ✓ hitIP = 0x88B0DE
```

Both hop-1 candidates run forward; only the second produces a chain.
Final dedup collapses the case where they'd converge on the same
hop-3 IP.

Open follow-up (deferred to Phase 28b-v3 / sub-phase):
* **`<>c` compiler-generated lambda-cache classes**: 0 class-pointer
  slots in `.data` because IL2CPP routes those through
  `g_codegenModule->types[index]` rather than direct pointer storage.
  Live-tested on `<>9__0_0` / `<>9__0_1` — still hits the yellow
  advisory banner. Needs a separate codegen-module index discovery
  pass; not blocking real-world workflows (user-defined statics like
  `SteamManager.s_instance` are the common case and now work).
* **`<>c` follow-up TODO**: walk `Il2CppCodeGenModule->types` table
  (or the equivalent v29 metadata-registration index), build a
  reverse mapping `Il2CppClass* → codegen module slot`, RIP-scan
  for refs to that slot. Estimated ~2-3 days.

---

(Previous TL;DR — for the build #555 IL2CPP advisory ship and earlier,
kept below for cross-PC handoff context.)

**Phase 28b IL2CPP static x-ref — root-cause CONFIRMED (build #555,
2026-05-25 evening)**. The build #546 diagnostic logging paid off: live
re-run on SaGa F2R with `MainGame/StrReader2UShortAry/<>c.<>9__0_0` and
`<>9__0_1` (C# lambda-cache delegates that structurally MUST have ≥1
read+write in IL) showed:

```
FieldXref: methodIndex built — 19477 methods indexed (of 20161 walked), executable sections=2
FieldXref: scan complete — candidateHits=0 emitted=0 droppedKindFilter=0 droppedNoEnclose=0 truncated=0
```

`candidateHits=0` (NOT `droppedNoEnclose>0`) → not cross-asm attribution.
Method index green → not resolver bug. Root cause: **IL2CPP
`il2cpp_class_get_static_data` returns a runtime-heap-allocated
pointer** (observed at `0x18BD5735630`, well outside GameAssembly.dll
image). No machine instruction can RIP-relative-address a runtime
address; AOT code reaches the field via 2-hop class-pointer indirection.
Phase 28b v1's RIP scan is fundamentally unable to see these references.

**Mitigation shipped (build #555)** — chose user-approved option C +
v2 stub:
* DLL adds `note` field to IL2CPP `findFieldReferences` response. Set
  when 0 matches AND target sits outside GameAssembly.dll module.
* UI [`FieldReferencesWindow`](../ui/UnityDumpUI/Views/FieldReferencesWindow.axaml)
  surfaces a yellow advisory banner: "IL2CPP `static_fields` are
  runtime-heap-allocated; this scanner cannot see refs via RIP scan
  — Mono backend handles this correctly."
* New [lessons-learned.md §Phase 28b IL2CPP `static_fields` is runtime-heap-allocated](lessons-learned.md#phase-28b-il2cpp--static_fields-is-runtime-heap-allocated-so-rip-scan-cannot-find-references)
  with the diagnostic recipe + asm reproduction.
* Phase 28b-v2 added as TODO stub in [plan.md](plan.md): class-pointer
  chained-disassembly redesign. Needs real x64 disassembler. ~1 week.

**Earlier this session (build #547-#554)** — three follow-up fixes
from user live-smoke:
1. **Symbol Finder "search all user assemblies" only finding WAAPI** —
   global limit was draining on the alphabetically-first big assembly.
   DLL `searchSymbols` now per-asm budget in multi-asm mode + new
   `truncated_assemblies` wire field + UI surfaces "per-asm cap hit on
   X, Y, Z — raise the limit" hint.
2. **BongoCat post-findFieldReferences slow game shutdown** — disconnect-
   time GC drain hook. New `PipeServer::RegisterDisconnectHook` API +
   Mono/IL2CPP backends register hooks that call `mono_gc_collect(-1)` /
   `il2cpp_gc_collect(0)` after every client disconnects. Drains the
   finalizer queue while DiscreteUI is still attached. Three games
   live-verified: no more shutdown slowness.
3. **"Static only" toggle in Symbol Finder** (earlier in session) — new
   checkbox in MainWindow Symbol Finder tab + DLL `staticOnly` arg
   relaxes the pattern-required rule when set. Closes the "static fields
   are hard to find" gap.

---

(Previous TL;DR — for the build #546 diagnostic-instrumentation
session and earlier, kept below for cross-PC handoff context.)

**Diagnostic-fingerprint correction** (2026-05-25 later same day):
yesterday's Leaf it Alone obfuscation diagnosis originally listed
`AnvirHook8_64.dll` as the 3rd of three definitive symptoms.
**Demoted to weak / cross-check-only evidence** after a Subnautica log
sample (Unity 2019.4 Mono, fully unprotected — every Discrete Mono
DLL primitive resolves cleanly on it: `bound 29/29`, main-thread hook
armed, 104 assemblies enumerable) showed the same `AnvirHook8_64.dll`
in its module list. Only consistent explanation: user-machine-wide
sysadmin tooling (likely Anvir Task Manager Pro), not game protection.
Updated [lessons-learned.md
§"Obfuscated / packed IL2CPP target diagnostic fingerprint"](lessons-learned.md#obfuscated--packed-il2cpp-target-diagnostic-fingerprint)
to a 2-symptom fingerprint; Subnautica added to
[test-matrix.md](test-matrix.md#unity-targets-verified) as passive
control sample (fills the Unity 2019.4 Mono gap between 2017.x and
2020.3 coverage).

---

**Unsupported-target reference card** (2026-05-25): a user reported
their build (`ba17b88` / 549) couldn't do anything on **Leaf it Alone**
([Steam #3981100](https://store.steampowered.com/app/3981100/Leaf_it_Alone/),
Unity 6.1 / IL2CPP). Diagnosed from their `init` + `metadata` logs as
the canonical **packed/obfuscated IL2CPP** signature: all 43 `il2cpp_*`
exports stripped (`resolved=no`) + `global-metadata.dat` encrypted
(`magic mismatch`). Recorded as:
* [test-matrix.md §"Unsupported targets (confirmed failures)"](test-matrix.md#unsupported-targets-confirmed-failures)
  — failure-mode row
* [lessons-learned.md §"Obfuscated / packed IL2CPP target diagnostic fingerprint"](lessons-learned.md#obfuscated--packed-il2cpp-target-diagnostic-fingerprint)
  — 30-second diagnostic recipe so we don't re-investigate the same
  class of target twice
* Closes the long-open `test-matrix.md` "Stripped metadata via mismatched magic"
  coverage-gap row.

Supporting this class of target needs **Phase 5-2d** (`IL2CPPMetadataRegistration`
AOB cross-link, already noted as non-blocking) **plus a new "export-less
IL2CPP fallback" phase** (AOB-scan `il2cpp_*` prologues + self-bind, no
`GetProcAddress`). ~6-8 days total; deferred until at least 2 more
samples land so AOB patterns can cross-validate.

**Phase 20e LIVE-VERIFIED on 活俠傳** (2026-05-25, PID 50236, Mono x86 /
Unity 2020.3.49f1). User confirmed Scene tab now enumerates GameObjects
end-to-end. Log location: `%LOCALAPPDATA%\Discrete_UEDumper\Logs\50236\`.
Closes the live-verify TODO that was carried over from 2026-05-24's
20e ship. **Phase 20e fully closed.**

**Phase 28b SaGa F2R 0-match — diagnostic instrumentation added** (build
#546, 2026-05-25). Previous-session smoke against SaGa F2R IL2CPP
returned 0 matches for `SteamManager` static field. To disambiguate
the three competing hypotheses (cross-assembly drop / opcode coverage
gap / field truly unreferenced) without committing to v2 work yet,
[IL2CPPFieldXref.cpp](../dll/src/il2cpp/IL2CPPFieldXref.cpp)
`FindStaticFieldReferences` now emits three diagnostic lines to the
`walk` log category per RPC:

```
FieldXref: target <ns.Class>.<field> staticAddress=0x... offset=N type=...
FieldXref: methodIndex built — M methods indexed (of W walked), executable sections=K
FieldXref: scan complete — candidateHits=A emitted=B droppedKindFilter=C droppedNoEnclose=D truncated=0/1
```

The new `candidateHits` / `droppedNoEnclose` split decides next move:
* `candidateHits=0` → opcode coverage gap or field truly unreferenced
  → Phase 28 v2 needs to add `0xC7 MOV imm32` / `0x83 0x3D CMP imm` /
  SIMD `0xF3 0x0F 0x10/0x11 movss/movsd` / 8-bit `0x88/0x8A` to the
  scan loop in [IL2CPPFieldXref.cpp:442-488](../dll/src/il2cpp/IL2CPPFieldXref.cpp)
* `candidateHits>0` but `droppedNoEnclose==candidateHits` →
  cross-assembly attribution drop (the comment at line 13-16 explicitly
  warns about this) → Phase 28 v2 needs `BuildMethodIndex` widened to
  walk every image and return a single global sorted method index
* `methodIndex=0` or `candidateHits>0 && emitted>0 but UI shows 0` →
  resolver / wire bug

**Next-session PC pickup** (build #546 already in `dist/`):
1. Launch SaGa F2R + DiscreteUI
2. Re-run the same `SteamManager` static field → Methods cross-ref
3. Grab `%LOCALAPPDATA%\Discrete_UEDumper\Logs\<PID>\walk\current.log`
4. The three `FieldXref:` lines decide whether Phase 28 v2 needs
   opcode-set widening, cross-image method index, or both
5. Also useful: pick a static field whose readers/writers are
   obviously in the **same** assembly (e.g. a private static singleton
   used only by its declaring class) — comparing log output against
   SteamManager isolates the cross-assembly variable

Phase 28 v1 itself remains shipped (Mono path 100% verified on 活俠傳;
IL2CPP static-only path passes its build-time tests; only the live
SaGa F2R static-field smoke is pending root-cause).

(Earlier session content below — Phase 20e ship, Phase 28 v1, Phase 27
+ READMEs + minor open issues kept for context.)

---

**Phase 20e shipped** (build #527, 2026-05-24). `SceneEnumerator` now
probes both `(CoreModule, monolithic)` FQNs in `ResolveGameObjectTypeAsync`
and routes every downstream `CallMethodAsync` through the resolved
assembly name (Object.FindObjects, get_name, GameObject.get_transform,
Transform.get_childCount, Transform.GetChild, Component.get_gameObject).
Existing fake-pipe tests still pass under the CoreModule-first probe
order; +1 new test pins the monolithic-fallback path end-to-end. Suite
357 → **358 pass**. ✅ Live-verified on 活俠傳 2026-05-25.

**Phase 28 v1 shipped** (build #515 UI / #518 with tests). 28a Mono IL
walker + 28b IL2CPP static-field RIP scanner + 28d UI modal with 3
right-click entry points (Inspector / Symbol Finder / Browser Fields
tab) all landed clean. 28c (IL2CPP instance fields, AOB heuristic) and
28e (CE Lua bridge) deferred per the
[[project-phase-28-scope]] memory.

**Phase 28 live-smoke**:
* 活俠傳 (Mono x86 / Unity 2020.3) — `Mortal.Core.GameStat._value`
  → 15 exact matches: `get_Value` / `get_FinalValue` (reads) +
  `Reset` / `SetValue` (5 calls, 3 reads + 2 writes) /
  `AddValue` (3 calls, 2 reads + 1 write). Cross-checked semantically
  — matches dnSpy IL view shape. ✅ COMPLETE.
* SaGa F2R (IL2CPP v29 / Unity 2021.3) — instance fields cleanly
  surface "not supported in v1" error (28c deferred per scope
  decision). Static-field path live-tried via `SteamManager` static
  → **0 matches** returned. 🚧 Build #546 added diagnostic logging
  (see TL;DR above) — root-cause pending next-session re-run.

(Earlier session content below — Phase 27 + READMEs + minor open
issues kept for context.)

Phase 27 (Unity-aware Value Search) shipped + live-verified end-to-end.
Five production-bug + UX follow-up commits landed on top. README rewrite
for end-user (CT maker) audience landed. Lessons documented. Repo
ready for handoff.

**Known minor open issue** (deferred — not blocking):
* Dual-monitor maximize → restore: the `OnWindowPropertyChanged` hook
  in MainWindow.axaml.cs captures pre-maximize bounds correctly, but on
  restore the window stays at maximize-bounds rather than snapping back
  to the saved pre-maximize Position+Size. Likely Avalonia 12's own
  restore-bounds path is running AFTER our hook and overriding our
  Position/Width/Height assignments. Next session: probably need
  Dispatcher.UIThread.Post(...) to defer our restore until after
  Avalonia's pass, or override OnPropertyChangedCore instead of
  subscribing to the event. Code at MainWindow.axaml.cs `OnWindowPropertyChanged`.

**Other carry-forward** (from earlier session notes):
* **27c live click-through smoke** — ✅ **CLOSED 2026-05-26** via Phase
  29a's snapshot walker bundled smoke. Verified on SaGa F2R (IL2CPP /
  Unity 2021.3 — `MainGame/DR_ENV.code: UInt32[]` length=2451 captured=50,
  `MainGame/HalfReader.buff: Byte[]` length=0 captured=0) AND BongoCat
  (Mono / Unity 6000.2 — `SteamInventoryTest.m_SerializedBuffer: Byte[]`
  length=1900545 captured=50, `BattleRoyale._roundDuration:
  RoundDuration[]` length=95 captured=50) AND 活俠傳 (Mono x86 / Unity
  2020.3 — `MultiKeyDictionary\`3.buckets: Int32[]` length=786438
  captured=50). Real arrays decode correctly; false-positive instances
  surface as `arrayError = implausible_length | header_unreadable` and
  count separately. Builds #598 / #602.
* **27b-4 live smoke** — UTF-16 / UTF-8 / ASCII string scan shipped;
  no live game smoke. (Same flow as 27a global scan; lower priority.)
* **27d live UI click-through** — Value Search tab shipped + live-
  smoked on 活俠傳 (Mono x86) and SaGa F2R (IL2CPP); SaGa F2R confirmed
  the session refine workflow narrows correctly.
* **Phase 27f** *(deferred, v2 acceleration)* — libgc heap-section
  binding. Doubles as Phase 20-HEAP closure. Not blocking.
* **Phase 28** (Field → Methods cross-reference) — ✅ Shipped in earlier
  session.

---

(Previous session-handoff narrative starts below.)

**Branch**: `dev` (2026-05-20 **late night** session — **Phase 27 fully shipped + live-verified + 3 production-bug follow-ups**. Build #452. Beyond the headline value-search stack, the live testing on 活俠傳 surfaced three perf/shutdown bugs that all landed same-night: SessionManager heap-leak + AddressFinder cache heap-leaks + reentrant ThreadAttachGuard. The reentrant-guard fix is the standout: 4000+ per-WhatIsAt thread_attach/detach cycles were leaving BDWGC finalizers queued, which ran on game shutdown and hung 活俠傳 for 44 s post-scan. Outer guard at scan-handler level + new per-thread reentrancy counter cuts 4000 cycles → 1. Verified: post-scan game-close time 44 s → ~1 s. New lesson recorded in [lessons-learned.md](lessons-learned.md): "any batch loop calling Mono / IL2CPP runtime APIs should hold ONE outer ThreadAttachGuard". Earlier same session — **Phase 27 fully shipped (27a + 27b + 27c + 27b-4 + 27d + 27e)**: complete Unity-aware value search stack. Build #433. **283/283 xUnit pass**. New artifacts:

* DLL: `scanFieldValue` (global + class-scoped + string scan), `beginValueScan` / `refineValueScan` / `endValueScan` (session lifecycle). `whatIsAt` now resolves array element indices for `T[]` / `List<T>._items`. `Mono::BuildClassRangeCache` rewritten to lazy vtable resolution (closes 32-bit Mono hang).
* UI: new "Value Search" tab (MainWindow position 6) — DataType + ScanType + Value/ValueTo + Tolerance + Encoding/MatchMode (string mode) + First Scan / Next Scan / End Session buttons + DataGrid with class/field/type/offset/current value/instance/address columns + right-click → Inspect this instance (cross-tab to Inspector via FocusOnInstance). AOT-clean.
* ~~CE Lua bridge `scripts/unity_valuescan.lua`~~ — **dropped 2026-05-20**. Originally implemented; user observation: CE already has a native value scanner, so a CE-side bridge that calls our scan back into CE has no realistic workflow. The DLL-side Begin/Refine/End RPCs remain for UI consumption; the Lua wrapper was deleted as unused surface.
* Tooling: `tools/Test-ScanFieldValue.ps1` + `tools/Test-ValueScanSession.ps1` PowerShell smoke harnesses.

**Live-smoked**: SaGa F2R (IL2CPP) one-shot + session (purse=100000 → spent 1 coin → `newValue=99999` narrowed to PLY_WORK.purse correctly, while PLY_WORK_SAVEDATA.purse stayed 100000 and got correctly dropped); 活俠傳 (Mono x86, Unity 2020.3.49f1) found `Mortal.Core.GameStat._value` reliably across UI state changes. **UI tab live click-through still pending user verification.**

The savefile-vs-active-state distinction is something CE alone can't surface — it doesn't know which class owns which address. This is the headline value of Unity-aware scan.

Earlier today same session: **Phase 27a shipped + live-smoked + production bug fixed**: `scanFieldValue` RPC works end-to-end on IL2CPP x64 (SaGa F2R), Mono x64 (BongoCat), and Mono x86 (活俠傳 / Legend of Mortal, Unity 2020.3.49f1). Mid-smoke caught one P0 hang on legacy 32-bit Mono — eager `mono_class_vtable` batch for 17K classes wedged BDWGC when game's main thread was concurrently focus-paused (alt-tab to console). Rewrote `Mono::BuildClassRangeCache` + `WhatIsAt` to lazy vtable→klass resolution: cache build does pure pointer enumeration (~500ms, zero runtime calls), backward scan derefs each candidate qword via SafeReadBytes to get klass, memoizes for subsequent hits. Build #423 verified hang-free across 2 runs at different in-game UIs on 活俠傳. Lesson recorded in [lessons-learned.md](lessons-learned.md). Also fixed build.ps1 default `-Arch x64` → `Both` (no-args `build.cmd` now rebuilds x86 + x64 together — was leaving `dist/x86/` stale) + `WindowsProcessEnumerator` no longer force-sets Backend=None for WOW64 processes (32-bit Unity targets now appear in Pick & Inject picker). Earlier today (2026-05-20 even later): **Phase 27 planning landed**: Unity-aware Value Search drafted as sub-phases 27a-27f in [plan.md](plan.md). Inspired by `D:\Github\AOBMaker`'s `WindowsValueScanner.cs` but reuses Discrete's existing `Memory::EnumerateCommittedRegions` + `whatIsAt` (Phase 17) filter + `findInstances` (Phase 6c) — no new scan engine needed. Two scan modes: **global** (CE flow) + **class-scoped** (Discrete flow, ~100x faster). Full CE filter mode set. Includes Phase 17b carry-forward — Array / List<T> element resolution in `whatIsAt`. v1 estimated ~6.5 days; v2 sub-phase 27f doubles as Phase 20-HEAP closure via libgc heap-section binding. **Phase 28** (Field → Methods cross-reference) added as TODO stub — Mono IL walk + IL2CPP RIP scan for static fields + heuristic for IL2CPP instance fields; picks up after Phase 27. **No code yet for either.** Earlier today (2026-05-20 mid/late): **Phase 26 anti-tamper protection** — 3-slot SHA256 self-check spread across Core + UI assemblies (`BuildArtifactToken` / `ExportFormatToken` / `WindowsRuntimeMetadata`), XOR-shielded footer credit, CT-export offset poisoning, and 1/11 Inspector grid value poisoning — all gated by 0xCC dev-mode bypass; on top of mid-session Phase 20-MT main-thread marshaling and Phase 25 multi-assembly Symbol Finder.)
**Last commit**: feat(security): 3-slot token guardian + Inspector grid poison _(planning-only update — no commit yet for Phase 27/28)_
**Build**: 408 (DLL sizes unchanged from build #376; `DiscreteUI.exe` AOT 39.4 MB after 3-slot guardian + TokenAggregator + InspectorPoison helper; Release single-file unchanged)
**Tests**: 283/283 pass (no new tests this session — protection layer is dev-mode-bypassed so existing snapshot tests stay byte-identical) + AOT round-trip verified end-to-end: 3 distinct sentinels in `.rdata`, inject-hash.ps1 patches all 3 with SHA256 `2A9C9F…E13B`, slot C ~11 KB away from A/B (cross-assembly blob distance).
**Current state**: **Phase 26 shipped + AOT-verified.** Three independent token guardians (slots A in Core/Services, B in Core/Services/Export, C in ui/UnityDumpUI/Platform) each self-check the running binary by zeroing ALL three slots and re-computing SHA256. `TokenAggregator.Combined()` ORs all three factors so tampering ANY slot trips both poison surfaces. Surface 1 — CT export offsets: `CloakOffset(off) => off ^ (factor & 0x7F)` in CeXml/CsxExporter. Surface 2 — Inspector `DisplayValue`: `MaybePerturbDisplay` XORs low byte of 1/11 numeric rows (per-row deterministic seed) with `factor & 0xFF`. Dev mode (Debug/Release single-file with 0xCC slots) short-circuits to factor=0 — exports + Inspector identical to pre-Phase-26. `tools/inject-hash.ps1` upgraded to 3-slot AOBMaker-style patching; build.ps1 calls it on `-Mode Publish` only. Class names renamed to non-suggestive forms (`IntegrityGuardian` → `BuildArtifactToken` / `IntegrityCore` → `SchemaConstants` / `StringShield` → `LocalizedText`) so binary metadata no longer leaks the words "Integrity" / "Guardian" / "Shield". UI footer (centered, opacity 0.55) shows `© bbfox @ https://opencheattables.com` decoded from XOR blob at VM construction. New end-user doc at `USAGE.md` (root) covers Mode A/B deployment for CT makers. Pre-Phase-26 work still in scope: **Phase 20-MT live-PASS across 5 targets** (BongoCat / SaGa F2R / Songs of Silence / LEAVES x86 / Legend of Mortal x86) MinHook detour on `mono_runtime_invoke` / `il2cpp_runtime_invoke` piggybacks on Unity's per-frame engine tick to drain a work queue on the Unity main thread. `callMethod` got two new optional args (`main_thread: bool` default false, `main_thread_timeout_ms: int` default 5000); `unity_scene.lua` defaults the engine-icall wrappers to `main_thread = true`; UI `MethodCallWindow` got a "Run on Unity main thread" checkbox. New `getDispatcherStats` RPC + `main_thread` sub-object on handshake/scan expose `hook_installed`/`is_healthy`/`tick_count`/`drain_count`/`queue_depth` for diagnostics. Discovered mid-implementation: nested `mono_thread_attach` on Unity main thread reproduces the build #54 BongoCat-killer; `ThreadAttachGuard` now consults a thread-local `IsCurrentlyDrainingOnMainThread()` flag and becomes a no-op on main thread. Stress 1000-iteration `Mathf.Abs` burst on main-thread path: BongoCat 15.66 ms/call, SaGa F2R 7.13 ms/call, Songs of Silence 3.95 ms/call — zero errors, zero timeouts, drain_count == 1000 on all. Two new lessons in [lessons-learned.md](lessons-learned.md): nested-attach trap + Unity 2018 focus-pause (`Application.runInBackground = false` default — R-Type Dimensions EX reports `is_healthy: false` when window loses focus, expected behavior). Phase 20b / 20c / 20d (Scene tab UI + cross-tab handoff + 2-target verify) still open; Phase 20-HEAP (libgc heap-walker fallback) also still open. Live-target work converged + **basic Symbol Finder shipped**. **Phase 25** new tab in MainWindow (position 4) wraps a new `searchSymbols` RPC walking every class in an assembly for fields + methods whose name contains an ASCII case-insensitive substring; replaces the "click each class in turn" loop the user reported. Both backends emit unified wire shape (IL2CPP rows carry AOT address; Mono skips mono_compile_method for bulk-scan performance — JIT deferred to the user-driven Methods picker). Row activation jumps back to Browser Fields/Methods tab with the class + member filter pre-selected. Phase 25b "interesting preset" curated keyword scan deliberately deferred until basic Finder shakes out. **DLL-free Mono path** now usable end-to-end on Unity 2022+ Mono — surfaced + fixed two production-blocker bugs during live smoke: (1) user-autorun `_G.mono_registerSymbolEx` shadows our idempotent helper, fixed by namespacing the working code under `_G.discrete_mono_registerSymbolEx`; (2) CE's `mono_method_get_parameters` returns nil typename for every parameter on at least UnityEngine.Object's methods on Unity 2022+ Mono — `mono_method_getSignature` is fine, so the helper now falls back to signature substring match when typename match fails (success log shows `(matched via signature)`). Both lessons recorded in [lessons-learned.md](lessons-learned.md). Carry-forward: Phase 22 closed in prior session. Phase 23 (heap-scan convergence) and Phase 20a-2 (FindObjectsByType cross-version recipe) both closed 2026-05-18.

> Companion files: [plan.md](plan.md) (canonical phase ledger), [pipe-protocol.md](pipe-protocol.md) (DLL ↔ UI contract), [Archiutecture.md](Archiutecture.md) (rules / invariants), [test-matrix.md](test-matrix.md) (verified games + tooling versions), [lua-invoke.md](lua-invoke.md) (Phase 19 spec), [address-finder.md](address-finder.md) (Phase 17 spec).

---

## What's done

| Phase | Status | Highlights |
|---|---|---|
| 0–11.5 | ✅ | Bootstrap → Mono/IL2CPP backends → live walking → UI panels → CE Lua bridge → inspector inline drilldown. |
| 12 (x64) | ✅ | `version.dll` / `winmm.dll` proxy DLLs + `ProxyDeployWindow` bulk-install panel. |
| 12d (32-bit Mono) | ✅ ALL d-1..d-5 | **Fully closed + live-smoked 2026-05-19** on Legend of Mortal (Unity 2020.3.49f1 / Mono bdwgc / x86). Parallel x86 CMake path via `build.ps1 -Arch x86` (output to `dist/x86/`). New `dll/src/shared/ManagedLayout.h` makes MonoObject / MonoString / MonoArray / Exception offsets constexpr-of-`sizeof(void*)`. New `tools/inject-x86/` cross-bitness helper exe (15.5 KB) for DiscreteUI x64 → 32-bit target inject; `WindowsDllInjector` shells out to it; `MainWindowViewModel` routes DLL path through `dist/x86/`. Live PASS: handshake `backend=Mono, unity_version=2020.3.49f1, 114 assemblies`; 19 Transform instances via `findInstances`; `walkInstance` decoded `m_CachedPtr` at offset 0x08 (= correct x86 MonoObject header); `monoCompileMethod GetInstanceID` returned real JIT address; **standalone `inject-x86.exe 55456 ...` reported `wow64=true LoadLibraryW=0x00D70000 OK` followed by clean pipe handshake** — proves the cross-bitness inject mechanism. Cosmetic v2 polish open: ProcessPickerWindow doesn't visually badge 32-bit rows; ProxyDeployWindow lacks manual x86/x64 toggle. Neither blocks anything. |
| 13 | ✅ | `dist/metadata-cli.exe` — offline `global-metadata.dat` parser. |
| 14a–14f | ✅ | Hardening: auto-disconnect, stripped-metadata banner, thread-attach audit, log rotation, SEH-leaf audit. |
| 14c | ✅ | IL2CPP metadata v24/v27/v29/v31 dispatch — six games verified across all four versions. Cross-checked vs `vendor/libil2cpp/`. |
| 15 | ✅ | [test-matrix.md](test-matrix.md) — **9 verified targets** across two backends + 4 metadata versions × 5 Unity major versions (2018.1 / 2019.4 / 2021.3 / 2022.3 / 6.1). Latest additions (2026-05-07): R-Type Dimensions EX (oldest IL2CPP — Unity 2018.1, surfaced missing-exports finding) + Songs of Silence (newest IL2CPP — Unity 6.1 / v39, first-class supported). |
| 17 (a/b/c/d) | ✅ | **Address Finder (reverse lookup).** New wire command `whatIsAt(addr)` returning instance + class + declared field. DLL backward-scan + class-pointer cache, UI Address Lookup tab, CE Lua bridge `unity_addrfind.lua`. Spec: [address-finder.md](address-finder.md). |
| 18 (a/b/c/d) | ✅ | **Find Method Instances + AOB AA-script generator + AOBMaker direct push.** New wire commands `findMethodInstances` + `getMethodAobPattern`, hand-rolled x64 LDE, UI overloads modal, `unity_symbols.lua` overload-aware extension, `CreateAAScript` integration over `\\.\pipe\AOBMakerCEBridge`. |
| **19 (a/b/c/d/e)** | ✅ | **Lua-callable Unity methods.** New wire RPC `callMethod(asm, ns, class, method, [signatureMatch], [instance], [args])` invoking Mono / IL2CPP methods with marshaled primitive + null + address + string args + boxed-primitive / address / UTF-8-decoded-string returns + managed exception capture. Static AND instance dispatch on both backends. UI MethodCallWindow (right-click any overload row → Call method…) + `scripts/unity_invoke.lua` Lua bridge with full arg-builder set. Spec: [lua-invoke.md](lua-invoke.md). |
| **19d-2** | ✅ | **UI polish.** Methods grid "Call method… (pick overload)" auto-skips the picker modal when `findMethodInstances` returns exactly one overload. Inspector right-click on any instance → "Call method on this instance… (switch to Methods)" snaps `SelectedInstance` + flips to the Methods tab (scope-checked: only when Browser.SelectedClass matches Inspector.TargetClass). |
| **19d-3** | ✅ | **Smart-typed editors in MethodCallWindow.** Replaces the single TextBox in the Value column with a per-row editor picked via `MethodCallEditorKind`: NumericUpDown for sized ints (range-clamped at the editor, no more post-Call out-of-range errors); ComboBox for enum params populated from `getEnumValues` (wire-tag re-routed through the enum's underlying integer type — fixes a latent type:"address" fallthrough bug); AutoCompleteBox for `System.Boolean` (true/false suggestions, free text still accepted). Float/Double stay on TextBox (decimal can't represent the full double range). Probe order for enums: method's own assembly → UnityEngine.CoreModule. Live-smoked on IL2CPP (PARTY_WORK.SetValue) and Mono (BongoCat / Mathf.Abs). |
| 16 (docs) | ✅ | All planned files exist. New in this batch: `lessons-learned.md`, `README.md` + `README.zh-TW.md` (root), `CONTRIBUTING.md` (root), `proxy-dlls.md`, `metadata-cli.md`, `export-formats.md`, `mono-internals.md`, `il2cpp-internals.md`, `dll-spec.md`, `ui-spec.md`, `technical-notes.md`. |
| **21a (Reverse-property lookup)** | ✅ | New wire RPC `listFieldValuesAcrossInstances(class, fieldOffset, fieldType[, limit])` — row-wise inverse of `walkInstance`. Right-click any value-typed Inspector row → "List this field across instances…" → modal grid showing `[instance_address, field_address, value]` for every live instance of the target class. Scope: value-typed fields whose offset is statically fixed from the instance base. New `InspectorFieldOffsetResolver` (Core) walks the parent chain summing offsets across value-struct ancestors and bails on pointer / collection / array-slot ancestors with a soft reason. **Live-smoked 2026-05-19** on IL2CPP (`PARTY_WORK +0x10 System.Byte`, 256 instances). Polish landed same day: column sort (after the three-layer AOT trap fix, see below) + case-insensitive substring filter on the Value column. 9 resolver unit tests + 3 fake-pipe RPC smoke tests. |
| **Follow-up tasks (2026-05-19)** | ✅ | Five small UX additions spawned + merged on the same day as Phase 21a. **Classes Copy class name** (right-click any Classes-list entry); **Inspector Class ptr Copy buttons** (small `name`/`addr` buttons next to the Class ptr row); **Fields/Methods per-cell context-menu copy** (Copy field/method name / Copy type / Copy address / Copy "Declared in"); **Copy CE Field multi-select fix** (was only exporting the last-focused row); plus Phase 21a itself. |
| **DataGrid sort AOT three-layer trap fix** | ✅ | Long-running mystery: every DataGrid in the app silently no-op'd on header click under AOT. Layer 1: `AvaloniaUseCompiledBindingsByDefault=true` + Window-level `x:DataType` makes column Bindings compile to `CompiledBinding` (no readable Path). Layer 2: empty Path → DataGrid auto-disables `CanUserSort` per-column (grid-level `CanUserSortColumns=True` is overridden — click doesn't even fire `Sorting`). Layer 3: even with sort re-enabled, default sort uses reflection on `SortMemberPath` which AOT trim drops — glyph animates but rows don't reorder. Fix needs all three layers: explicit `SortMemberPath` + `CanUserSort="True"` per column in XAML, plus `CustomSortComparer` wired in code-behind. New shared `DataGridSortComparers` + `WireSortComparers` extension applied to all 6 grids (FieldAcross / Fields tab / Inspector / Methods tab / ProcessPicker / ProxyDeploy). Diagnosed via FileLogger Sorting-event trace; cross-checked against `D:\Github\CrimsonAtomtic` for reference shape. Full mechanism + diagnostic recipe in [lessons-learned.md](lessons-learned.md). Recipe for new DataGrids in [technical-notes.md](technical-notes.md). |
| **22 (Live CT Population via AOBMaker)** | ✅ | All sub-phases (22a–22f) shipped 2026-05-12 across 6 commits. `AobMakerBridge` extended with InjectTableFile + CreateMemoryRecord + CreateAAScriptWithRecords + CheckAvailability; 7 lua bridge files embedded as `<EmbeddedResource>` in Core; bootstrap AA-script generator (idempotent guard + preload trick for `unity_dump.lua`'s `require 'utils'`); `BridgeInjector` orchestrator (8-step pipeline w/ progress + structured failure result); MainWindowViewModel hoisted AobMaker to top-level + Tools menu RelayCommand; per-feature `CallMethodAaScriptGenerator` + `AddressLookupAaScriptGenerator` for MethodCallWindow + AddressLookup tab; new [`docs/ce-bridge-integration.md`](ce-bridge-integration.md) + [`tools/Test-BridgeInject.ps1`](../tools/Test-BridgeInject.ps1) wire smoke. **Live click-through smoke pending.** |
| **21 (Inspector field write-back)** | ✅ a/b/c | New wire RPC `setInstanceField(addr, fieldOffset, type, value)` (build #326) — one backend-agnostic handler dispatching per-type to `Memory::SafeWriteBytes`. Accepts both wire-short tags (`int32`) and CLR names (`System.Int32`); marshals into an 8-byte stack buffer with per-type clamping + width validation. New `FieldWriteWindow` modal (build #331) opened from Inspector right-click "Write value…" — editor flavor picked by new `InspectorWriteTypeMap.Resolve` helper (NumericUpDown for sized ints / TextBox for float / AutoCompleteBox for bool / TextBox for char + address). Reference / value-struct / collection rows open the modal but show an unsupported banner. New `SetFieldValueFactory` paralleling `CallArgFactory`; `BackendAdapter.SetInstanceFieldAsync` + `SetInstanceFieldResult` DTO. CE Lua: `setField` / `setInstanceField` in `unity_invoke.lua` mirror the wire RPC. `tools/Test-SetInstanceField.ps1` smoke (`readMemory` snapshot → `setInstanceField` → `readMemory` verify → restore). 238/238 tests pass (+45: 6 fake-pipe RPC + 31 classifier theory + misc). **21d live click-through pending** — recipe in plan.md. |
| **5 (v32-v39 metadata.dat parser)** | ✅ shipped + LIVE-SMOKED | `MetadataParser` widened to accept `[16, 39]` (was `[16, 31]`). New layouts in `MetadataLayout.h`: `TypeDefinition_v35` (84 bytes — v29 minus `elementTypeIndex`), `StringLiteralEntry_v35` (4 bytes — drops length, computed from delta), `Il2CppSectionMetadata` (12-byte `{offset, size, count}` triplet), `HeaderV38` (380 bytes — 31 section triplets), `IndexSizes` runtime-variable struct + `GetIndexSize(count)` helper. `LoadHeader` dispatches flat (v16-v37) vs section (v38+). `LoadTables` computes `IndexSizes` from `parameters.size/count` + table counts. `TypeDefSizeof` / `FieldDefSizeof` / `FieldDefaultValueSizeof` return per-version sizes (84 for v32-v37; `68+3*TIS+GCIS` for v38+ TypeDef; `8+TIS` for v38+ FieldDef / FieldDefault). `GetTypeDefinition` / `EnumerateTypeFields` / `FindFieldDefault` got hand-walked v38+ paths that consult `indexSizes_`. `GetStringLiteral` recovers v35's missing length from delta-to-next-entry. Smoke ladder: synthetic-fixture `tools/Test-MetadataVersions.ps1` PASS on v29/v31/v35/v37/v38/v39 + correctly rejects v40; **live smoke on Songs of Silence (Unity 6.1.x / v39) 2026-05-19** — `metadata-cli info` decoded 40,501 TypeDefinitions + 44,047 string literals; `metadata-cli stats` ran clean across all 40k types (strip_ratio=0.0008, verdict `clean`); `metadata-cli type UnityEngine Vector3` decoded all 15 expected fields (`kEpsilon` / `kEpsilonNormalSqrt` / `x/y/z` / `zeroVector...negativeInfinityVector`) with correct tokens (0x04000A3B+) + variable-width type indices. **v32-v39 dispatch verified end-to-end on real-world data.** |
| **25 (cross-class Symbol Finder)** | ✅ shipped + LIVE-SMOKED | New `searchSymbols` pipe RPC (parallel Mono + IL2CPP handlers) walks every class in a named assembly, iterates fields + methods via the existing `EnumerateClasses` + `ReadClassInfo` path, applies ASCII case-insensitive substring match on name, accumulates up to `limit` (default 200, hard-cap 5000), returns `scanned_classes` + `truncated`. IL2CPP method rows carry AOT pointer; Mono skips JIT for bulk scan (drill into Methods picker for the per-overload `mono_compile_method`). New UI Tab "Symbol Finder" (position 4, after Address Lookup) — assembly dropdown shares `Browser.Assemblies`, pattern textbox (Enter triggers Search), Fields/Methods toggles, Limit NumericUpDown (Width 140 + MinWidth 120 so the number stays visible after the spinner widget claims its share), sortable results grid (Kind / Class / Member / Type / Offset / Address). Row double-click or right-click → Navigate jumps back to Browser Fields/Methods tab with the class + member filter pre-selected; right-click also surfaces Copy class / Copy member. 3 new fake-pipe `SearchSymbolsTests`. **Live smoke 2026-05-19** on BongoCat (Mono / Unity 2022+): pattern `MaxHP` against `Assembly-CSharp` returned `MainGame/CALC_WORK.maxHp` (`System.Int32` @ `0x2C`) as first hit in < 1s; double-click jumped to Browser Fields tab with the row filtered + selected. Phase 25b "interesting preset" (curated Unity keyword scan + scoring, mirror of UE5CEDumper's Interesting Properties panel) deferred until basic Finder shakes out — Unity-specific keyword list (HP/Damage/Gold/XP/Speed/...) still needs design. |
| **26 (Anti-tamper protection)** | ✅ shipped + AOT-verified | 3-slot SHA256 self-check spread across two assemblies + 2 poison surfaces + XOR-shielded credit string. Slots: A `BuildArtifactToken` (Core/Services) / B `ExportFormatToken` (Core/Services/Export) / C `WindowsRuntimeMetadata` (ui/UnityDumpUI/Platform — **cross-assembly**). `TokenAggregator.Combined() = A \| B \| C` bridges Core consumers to slot C via UI-registered delegate from `Program.Main`. Poison surface 1: `CeXmlExporter` / `CsxExporter` `CloakOffset(off) => off ^ (factor & 0x7F)` shifts every exported field by 0-127 bytes (CT loads cleanly in CE, reads wrong fields). Poison surface 2: `LiveInspectionViewModel.WalkedFieldNode.MaybePerturbDisplay` perturbs 1/11 numeric `DisplayValue` rows (per-row deterministic seed) via low-byte XOR — same row corrupts same way within a session, different rows across sessions (string `GetHashCode` randomization). Anti-trace: each guardian's `Read()` returns 0 for first N calls (A:3 / B:2 / C:4) even when tampered. Anti-debug: `Debugger.IsAttached` on a published binary returns its own poison code. Dev-mode no-op: 0xCC placeholder `IsDevMode` short-circuits all factors to 0 (Debug/Release single-file paths). `tools/inject-hash.ps1` upgraded to 3-slot AOBMaker-style patching (zero all 3 slots → one SHA256 → write same hash to all 3); `build.ps1 -Mode Publish` invokes it. AOT round-trip verified: sentinels at `0x02438F98` / `0x02438E80` / `0x0243ABF8` (C ~11 KB away from A/B = different .rdata blob). Footer credit `© bbfox @ https://opencheattables.com` decoded from XOR blob at VM construction. Class names renamed (`IntegrityGuardian` → `BuildArtifactToken` / `IntegrityCore` → `SchemaConstants` / `StringShield` → `LocalizedText` / `GetIntegrityFactor` → `Read`) so binary metadata no longer leaks "Integrity" / "Guardian" / "Shield" strings. New end-user guide [`USAGE.md`](../USAGE.md) at repo root for CT makers. 283/283 tests pass (no new tests — protection layer dev-mode-bypassed). Commits `ccaae6e` (single-slot) + `348cbb6` (3-slot upgrade). |

---

## What landed in this session — 2026-05-20 even later (Phase 27 planning + Phase 28 stub)

**Planning only — no code changes.** User asked to evaluate two new feature
ideas; after discussion, agreed to fully plan feature 1 and add feature 2
as a TODO stub.

### Phase 27 — Unity-aware Value Search

Full sub-phase plan (27a–27f) drafted in [plan.md](plan.md). Key design
decisions captured in the AskUserQuestion exchange:

* **v1 includes string scan** — managed UTF-16 path walking `MonoString*`
  / `Il2CppString*` layouts; encoding option + Exact / Contains match
  mode in UI.
* **v1 includes full CE filter-mode set** — exact / range / changed /
  unchanged / increased / decreased / new value (parity with CE Next
  Scan).
* **Two scan modes shipped together** — global (CE flow) for the
  "I have HP=100, find it" case; class-scoped (Discrete flow) for
  "I know it's somewhere on PLY_WORK" — ~100x faster, essentially
  `listFieldValuesAcrossInstances` + value predicate.

### Architecture choice

Reuses existing primitives instead of rebuilding AOBMaker-style:

| Existing | What it gives us |
|---|---|
| `Memory::EnumerateCommittedRegions` (Phase 23 hardened) | MEM_IMAGE + thread-stack filtering for free |
| `Memory::ScanForPointer` template | Parallel chunked scan pattern |
| `whatIsAt` (Phase 17) | Per-candidate Unity-managed filter |
| `findInstances` (Phase 6c) | Class-scoped mode core |
| `listFieldValuesAcrossInstances` (Phase 21a) | Pattern for class-scoped value walk |
| AOBMaker `WindowsValueScanner.cs` shapes | UI-side `DataType` / `ScanType` enum reference (not the engine itself — we run from inside the target process) |

Anti-cheat is largely a non-issue because the scan runs from inside the
target process via the existing injected DLL — user-mode AC hooking
`OpenProcess`/`ReadProcessMemory` doesn't see our scan. `SafeReadBytes`
SEH-protection covers the rare in-process VirtualQuery hook case.

### Phase 17b carry-forward bundled into Phase 27

Array / List<T> element resolution was deferred in Phase 17b ("nearest
field" returns when query lands in an array buffer). Phase 27c finally
closes this — detect Array klass via class-name `[]` suffix or
non-null element_class, compute index from offset, return
`{kind: "array-element", arrayIndex, arrayLength}`.

### Sub-phase v1 estimate: ~6.5 days

| Sub-phase | Effort |
|---|---|
| 27a (DLL First Scan + class-scoped + whatIsAt filter) | ~2 days |
| 27b (Next Scan session + filter modes + string path) | ~1.5 days |
| 27c (Array element resolution in whatIsAt) | ~0.5 day |
| 27d (UI Value Search tab + Cancel + cross-tab handoffs) | ~2 days |
| 27e (CE Lua bridge `scripts/unity_valuescan.lua`) | ~0.5 day |
| 27f (libgc heap-section, deferred — closes 20-HEAP simultaneously) | ~1-2 days |

### Phase 28 — Field → Methods cross-reference (TODO stub)

Brief approach captured in plan.md. Mono path uses IL bytecode walk
(`ldfld`/`stfld`/`ldsfld`/`stsfld` token match) — high confidence, clean.
IL2CPP static fields reuse [AOBMaker's `RipReferenceScanner.cs`](D:\github\AOBMaker\src\AOBMaker.Core\Services\RipReferenceScanner.cs)
against the field's static-storage address. IL2CPP instance fields stay
heuristic (AOB scan for `[reg+offset]` patterns within declaring class's
methods) and may be skipped in v1. No sub-phases / no commitment to
scope yet.

### Next session pickup

* Start coding Phase 27a — new `Memory::ScanForValue<T>` helper +
  `scanFieldValue` RPC handler in both backends. Same SEH-leaf pattern
  as the existing scan primitives.

---

## What landed in this session — 2026-05-20 late (Phase 26 anti-tamper + USAGE.md)

User-driven hardening pass inspired by `D:\Github\AOBMaker`'s pattern:
add cheap, hard-to-bypass binary protection so a hex-edited DiscreteUI
silently produces wrong output instead of either crashing or letting
the modification go unnoticed. Two iterations on the same day.

### Iteration 1 — single Guardian (`ccaae6e`)

* Added [LocalizedText](../ui/UnityDumpUI.Core/Services/LocalizedText.cs)
  (was `StringShield`) — rolling-XOR encoded blobs for `bbfox`, the
  URL, and the full credit string. PowerShell-precomputed bytes match
  on-host decode bit-for-bit (35-char attribution round-trip
  hand-verified during build).
* Added single-slot `IntegrityGuardian` + `IntegrityCore` (renamed in
  Iteration 2 — see below) + [tools/inject-hash.ps1](../tools/inject-hash.ps1)
  + `build.ps1 -Mode Publish` hook. AOT-verified slot at `0x024382B8`.
* Added centered footer to MainWindow.axaml bound to
  `MainWindowViewModel.CopyrightText`.
* CT export `CloakOffset(off) => off ^ (factor & 0x7F)` in
  `CeXmlExporter` + `CsxExporter`. 0-127 byte shift means CE still
  parses the CT cleanly; addresses land in the same object, just
  reference wrong fields.

### Iteration 2 — 3 slots cross-assembly + Inspector poison (`348cbb6`)

User feedback: "AOBMaker 太近了，放遠一點" + "poison 點能不能偶爾在
grid 給錯數值/offset?" → upgraded design.

* **Renamed all 3 originals** to non-suggestive forms so binary
  metadata stops shouting "integrity protection":
  * `StringShield` → `LocalizedText`
  * `IntegrityCore` → `SchemaConstants`
  * `IntegrityGuardian` → `BuildArtifactToken`
  * `GetIntegrityFactor()` → `Read()`
* **3 token guardians**, cross-assembly:
  * Slot A — [`BuildArtifactToken`](../ui/UnityDumpUI.Core/Services/BuildArtifactToken.cs) in Core/Services
  * Slot B — [`ExportFormatToken`](../ui/UnityDumpUI.Core/Services/Export/ExportFormatToken.cs) in Core/Services/Export
  * Slot C — [`WindowsRuntimeMetadata`](../ui/UnityDumpUI/Platform/WindowsRuntimeMetadata.cs) **in ui/UnityDumpUI/Platform (cross-assembly)**
  * AOT layout verified: A `0x02438F98` / B `0x02438E80` / C `0x0243ABF8` — C ~11 KB away from A/B (different .rdata blob).
* **TokenAggregator** ([`Core/Services/TokenAggregator.cs`](../ui/UnityDumpUI.Core/Services/TokenAggregator.cs))
  bridges Core ↔ UI: `Program.cs` calls
  `TokenAggregator.RegisterExternal(WindowsRuntimeMetadata.Read)` at
  app startup so Core consumers see slot C's factor via delegate.
  `Combined() = A | B | C` — tampering ANY slot trips both surfaces.
* **`InternalsVisibleTo`** added for `DiscreteUI` (not "UnityDumpUI" —
  the assembly name differs from project name, hit + fixed during build).
* **Second poison surface** — Inspector grid:
  `LiveInspectionViewModel.WalkedFieldNode.MaybePerturbDisplay` runs
  on every primitive display value. Per-row deterministic seed (string
  GetHashCode XOR offset XOR salt) — 1-in-11 rows get their numeric
  low byte XOR'd with `factor & 0xFF`. Skips structs / pointers /
  errors / quoted strings. Same row corrupts same way within a session
  (looks like a flaky reader, not a tamper response); across sessions
  string GetHashCode randomization shifts WHICH rows look buggy.
* **3-slot inject-hash.ps1**: locate all 3 dev slots → zero all 3
  hash regions → compute SHA256 once → write same hash into all 3.
  Each runtime guardian does the same zero-everything-then-hash dance
  so its own slot's stored hash matches the computed hash. Same
  pattern as AOBMaker's A/B/C.

### Defensive-design notes recorded (per code-review pass)

* **DelayThreshold side-effect**: each guardian's `Read()` returns 0
  for the first N calls (A:3, B:2, C:4) even when tampered, defeating
  trivial breakpoint trace. Consequence: the first ~2 emitted offsets
  on first export are unpoisoned even on a tampered binary — but the
  remaining 100+ are, and the inspector poison's per-row seed is
  independent of that counter.
* **Single-file Release vs AOT**: the deflate-compressed bundle in
  `dist/DiscreteUI.exe` (Debug/Release single-file mode) has the
  sentinel byte sequence but NOT the trailing 32×0xCC (compression
  collapses runs). Only `-Mode Publish` (AOT) produces a binary where
  inject-hash.ps1 can find + patch slots. Build script gates the
  injection accordingly — dev iteration is unaffected.
* **Dev-mode no-op**: `IsDevMode(HashSlot)` checks for the 32×0xCC
  tail; while uninjected, every `Read()` short-circuits to 0 and
  factors stay 0 across `TokenAggregator.Combined()`. All 283 tests
  + every Inspector smoke + every CT export remain byte-identical.

### New end-user doc — [USAGE.md](../USAGE.md) at repo root

Brief, technical-detail-free guide aimed at CT makers who already
know Unity + CE table authoring. Phpbb-markdown-compatible (per
`D:\Github\CrimsonAtomtic\docs\oct\phpBB-markdown-syntax.md`) so it
pastes into an opencheattables.com forum post without rework. Key
section is "CE Table deployment modes" — Mode A keeps DiscreteUI for
the author, Mode B bakes the bridge into the `.CT` via
**Tools → Inject Discrete Bridge**.

### Tests / build

* 283/283 xUnit pass (unchanged — no new tests, protection
  short-circuits in dev mode)
* `dotnet build` 0 errors / 0 warnings (Core + UI + Tests)
* AOT publish round-trip verified: 3 sentinels in published exe + 3
  injectable slots + inject-hash.ps1 stamps all 3 with same SHA256

### Carry-forward / next session

* Phase 26b candidates if user wants more breadth: add poison points
  to Methods grid address column + Symbol Finder address column
  (currently only Inspector + CT export). Not blocking.
* Two minor open items pre-Phase-26 from `plan.md` remain deferred:
  17e (Strategy B cached heap walk) + 20-HEAP (libgc fallback) +
  25b (interesting-preset Unity keyword scan).
* `_invocationCount` 2^31 wrap edge case (4 billion calls per
  session) — acknowledged, not patched (irrelevant in practice).

---

## What landed in this session — 2026-05-19 late additions (Phase 25 + DLL-free Mono live smoke + MethodOverloads polish + Phase 21d / enum smoke)

On top of the mega-batch below, the session kept going through three more
live-smoke cycles + one new feature. All shipped on `dev`, fast-forwarded
to `main`, pushed to origin.

### Phase 25 — cross-class Symbol Finder (~½ day)

The user reported clicking each class one-by-one through `Assembly-CSharp`'s
several-hundred classes whenever they hunt a property like `MaxHP`. New
Tab on the right pane (position 4) wraps a new `searchSymbols` RPC with:
ASCII case-insensitive substring match (asymmetric tail-match preserves
the `Int32 → UInt32` substring trap), fields + methods toggles, Limit
(default 200, DLL-side hard cap 5000), sortable result grid, double-click
navigate-to-Browser handoff (selects assembly + class, pre-fills the
Fields/Methods filter, switches tab). DLL Mono + IL2CPP each got a
parallel handler walking `EnumerateClasses` × `ReadClassInfo`. New file-
local `ICaseContainsAscii` helper (no `<locale>` drag-in — names are
always ASCII in C#). Live smoke on BongoCat / Mono / Unity 2022+:
`MaxHP` → `MainGame/CALC_WORK.maxHp` (`System.Int32` @ `0x2C`), < 1s
end-to-end. Properties handled as `get_*`/`set_*` methods (Mono/IL2CPP
runtime convention; UI confirmed acceptable). Phase 25b interesting-
preset deferred — needs a Unity-tuned keyword list distinct from
UE5CEDumper's FProperty-flavored one.

### DLL-free Mono register path — live smoke + 2 production fixes

Live smoke against BongoCat (Mono / Unity 2022+) drove the path through
`Object.FindObjectsByType(System.Type, FindObjectsInactive, FindObjectsSortMode)`
(the 3-arg enum overload). Surfaced two issues that both needed a fix
before the path was usable on any Unity 2022+ Mono target:

* **User-autorun shadows the helper's idempotent block.** The user had
  hand-rolled `_G.mono_registerSymbolEx` in a CE autorun script (forums
  pattern, doesn't call `LaunchMonoDataCollector`); our helper's
  `if _G.mono_registerSymbolEx == nil` guard skipped its own load,
  leaving the AA to invoke the unattached autorun version → instant
  `mono_findClass` returns nil → `💔 Class not found - UnityEngine.Object`.
  Fix: `scripts/mono_registersymbol_helpers.lua` now installs
  `_G.discrete_mono_registerSymbol*` (no guard, always overwrite — our
  namespace, our canonical version) alongside the legacy `mono_*`
  names (still guarded for user copy-paste). Generator
  (`MonoRegisterSymbolAaScriptGenerator`) keys idempotency off the
  discrete-namespaced symbol and emits `_G.discrete_mono_registerSymbolEx`
  calls.
* **CE 7.6 + Unity 2022+ Mono returns nil typename for every parameter
  on `UnityEngine.Object`'s methods**, even though `mono_method_getSignature`
  renders the full type info cleanly. Helper now falls back to signature-
  string substring match when typename match fails for any param; success
  log shows `(matched via typename)` vs `(matched via signature)`. Plus
  `pcall` shielding around `mono_method_get_parameters` (generic /
  uncompiled overloads throw), `mono_enumImages` warm-up for split-module
  classes, and an asymmetric `_discrete_typename_match` so the generator
  can emit full namespaced names while the helper accepts CE's stripped
  forms (still rejects the `Int32` substring of `UInt32`). Diagnostic
  output now lists every name-matched overload + its rendered signature
  when no match lands, so the next CE/Unity-version surprise surfaces
  the relevant data immediately.

### MethodOverloadsWindow polish (4 small fixes triggered by smoke)

* **WrapPanel + Width 1100** (was StackPanel + 900) — bottom button bar
  now reflows under any window size instead of clipping the last entries.
* **Mechanism-grouped button labels** replacing the wordy old set: now
  `Copy: addresses / registersymbol / aobscan / mono_register` plus
  `Push aobscan to CE / Push mono_register to CE`. The action prefix
  (`Copy:` vs `Push`) groups visually, the underlying CE primitive name
  shows what each button emits — no more "what's the difference between
  three copy buttons".
* **Single-overload auto-tick** — Refresh ticks the only row's
  `IsSelected` so the bottom-bar commands enable on open without manual
  click; mirrors the Phase 19d-2 auto-skip on the Call method… context
  menu. Multi-row keeps the legacy "user must tick" gating; status text
  updated to "Tick rows to enable the buttons below".
* **NumericUpDown Width 140 + MinWidth 120** (was Width 90) — the
  number display was hidden behind the up/down spinner widget; the
  Phase 25 Symbol Finder Limit field hit the same trap on first smoke
  and got the same fix.

### Phase 21d Inspector field write-back + enum ComboBox path — live click-through PASS

Both stayed pending after their initial Phase 21 / 19d-3 ship; this
session walked through them end-to-end:

* **Phase 21d**: BongoCat Mono right-click any primitive Inspector row →
  Write value… modal → Apply → readMemory verifies the write landed,
  CE address-list refreshed via the existing AOBMaker bridge.
* **enum ComboBox path**: same target, `UnityEngine.Object.FindObjectsByType(Type, FindObjectsInactive, FindObjectsSortMode)`
  drove both enum params through the Phase 19d-3 `getEnumValues` → ComboBox
  cascade. Closes the Phase 19d-3 follow-up that previously had no enum-
  parameter target to exercise.

### Session totals (post mega-batch)

* **Tests**: 238 → 241 (+3 SearchSymbolsTests).
* **Build**: #340-something → **#363**. DLL Mono 571.5→580.5 KB, IL2CPP
  606→615 KB, `DiscreteUI.exe` 39.1→39.2 MB, helper lua 10.6→23.1 KB.
* **Commits on dev (since the mega-batch hand-off)**: 2 —
  `a41c7d3 feat(ui): MethodOverloadsWindow polish + DLL-free Mono live smoke PASS`,
  `b1d8ef2 feat: Phase 25 — cross-class Symbol Finder tab`.
* **Docs**: STATUS.md / plan.md / pipe-protocol.md / lessons-learned.md
  all updated in the post-Phase-25 docs sweep.

---

## What landed in this session — 2026-05-19 mega-batch (19d-3 + 5 follow-ups + 21a + DataGrid AOT trap)

A single-day session that started as "ship Phase 19d-3 smart-typed
editors" and accumulated four more independent landings as user
testing surfaced gaps.

### Phase 19d-3 — smart-typed editors

Replaces the single TextBox in `MethodCallWindow`'s Value column with
a per-row editor picked by a new `MethodCallEditorKind` enum:

* **NumericUpDown** for `System.{S?Byte, Int16, UInt16, Int32, UInt32,
  Int64, UInt64}` — bounded to the type's `[MinValue, MaxValue]` so
  out-of-range input clamps at the editor instead of bouncing back as
  a post-Call error. `decimal`-backed (the full sized-int range fits).
* **ComboBox** for enum params — populated from a new
  `GetEnumValuesAsync` BackendAdapter call kicked from
  `MethodCallWindow.Opened`. Wire `type` tag is re-routed through the
  enum's `underlying_type` (fixes a latent bug where enums fell
  through `TryBuildArg`'s unknown-type branch to `type:"address"`).
* **AutoCompleteBox** for `System.Boolean` — suggests `true`/`false`
  on focus but accepts any text the existing `bool.TryParse` consumes
  (case-insensitive, surrounding whitespace tolerated).

Probe order for enums: method's own assembly → `UnityEngine.CoreModule`
for `UnityEngine.*` namespaces. Soft failures (class not found / not
an enum / Mono missing `mono_field_get_data`) silently leave the row
on TextBox. Float/Double stay on TextBox — decimal-backed
NumericUpDown would silently truncate doubles outside decimal range.

**Live smoke (user-confirmed)**: IL2CPP `MainGame.PARTY_WORK.SetValue(
UInt32 ofs, Byte value)` — both rows rendered NumericUpDown with
spinners, manual `6` into `ofs` accepted. Mono / Unity 2022+ BongoCat
`UnityEngine.Mathf.Abs(Int32 value)` — NumericUpDown rendered, manual
`99` accepted. Confirms parity across backends.

### 5 follow-up tasks (spawned + merged same day)

Each opened as a separate spawn-task chip and merged sequentially:

1. **Classes Copy class name** — Classes-list right-click menu adds
   "Copy class name" sibling to "Find instances". Single-select v1.
2. **Inspector Class ptr Copy buttons** — small `name` / `addr`
   buttons next to the Class ptr row in the Inspector header.
3. **Fields/Methods per-cell copy** — Fields grid right-click adds
   Copy field name / Copy type / Copy "Declared in"; Methods grid
   adds Copy method name / Copy address / Copy "Declared in".
4. **Copy CE Field multi-select fix** — was only exporting the last
   focused row when user multi-selected; now iterates SelectedItems
   on both Fields tab and Inspector grid. New
   `CeXmlExporterMultiFieldTests` × 4.
5. **Phase 21a Reverse-property lookup** (below).

### Phase 21a — reverse-property lookup

New "List this field across instances" right-click flow in the
Inspector. Inverse of the existing Inspector cascade (one instance →
all fields); this is one field → all live instances. New wire RPC
`listFieldValuesAcrossInstances`, new `FieldAcrossInstancesWindow`
modal with 3-column DataGrid (Instance address / Field address /
Value). Scope: value-typed fields whose offset is statically fixed.
Pointer-traversal / collection-element ancestors bail with a soft
reason. **Polish landed same day**: column sort works (after the
DataGrid AOT fix below) + case-insensitive substring filter on the
Value column. **Live-smoked**: `PARTY_WORK +0x10 System.Byte`, 256
matches end-to-end.

### DataGrid AOT three-layer trap (long-running mystery — solved)

Every DataGrid in the app silently no-op'd on header click under
AOT. Diagnosed via `FileLogger`-traced Sorting event + per-column
state dump. Three layers all have to be open:

| Layer | What happens | Symptom |
|---|---|---|
| 1 | Window-level `x:DataType` makes column Bindings compile to `CompiledBinding` whose Path is empty at runtime | (invisible) |
| 2 | Empty Path → DataGrid auto-disables `CanUserSort` per-column; `CanUserSortColumns=True` on the grid is overridden | Header click does nothing, no glyph, no Sorting event |
| 3 | Default sort uses reflection on `SortMemberPath` to read row property values; AOT trim drops that path | Glyph (▲▼) animates, but rows don't reorder |

**Fix** (needs all three layers open):

* Per column XAML: `SortMemberPath="<PropertyName>"` +
  `CanUserSort="True"` — opens layer 2.
* Code-behind: wire `CustomSortComparer` per column via a typed
  `Func<TRow, value>` selector — opens layer 3, AOT-safe (no
  reflection).

Shared infrastructure:

* [`UnityDumpUI.Core.Services.DataGridSortComparers`](../ui/UnityDumpUI.Core/Services/DataGridSortComparers.cs)
  — pure IComparer factories: `Hex<TRow>` / `Ordinal<TRow>` /
  `Number<TRow>` / `Bool<TRow>`.
* [`UnityDumpUI.Views.DataGridSortExtensions.WireSortComparers`](../ui/UnityDumpUI/Views/DataGridSortExtensions.cs)
  — `DataGrid.WireSortComparers(dictionary)` extension keyed by
  `SortMemberPath`.

Applied to **all 6 sortable DataGrids** in the app: FieldAcross,
Browser Fields tab, Inspector walked-fields, Methods tab,
ProcessPicker, ProxyDeploy — 33 columns total, working pattern in
`MainWindow.axaml.cs:WireSortComparersForAllGrids`.

**Cross-checked against** sibling project `D:\Github\CrimsonAtomtic`
(same Avalonia 12 + Native AOT setup, all DataGrids sort correctly).
Difference: their XAML scope shape produces plain `Binding` (not
`CompiledBinding`) for column Bindings, so layer 1 never triggers
and the default sort works. Our explicit-comparer route is the
strict-superset fix that works in both scope shapes.

Full mechanism + diagnostic recipe in
[lessons-learned.md §"Avalonia 12 DataGrid sort is a three-layer
trap"](lessons-learned.md). Recipe for adding new DataGrids in
[technical-notes.md §"Adding a new sortable DataGrid"](technical-notes.md).

### Session totals

* **Tests**: 163 → 180 pass (+17 new: 4 multi-row Copy CE field +
  9 offset resolver + 3 fake-pipe RPC smoke + 1 namespace threading
  regression).
* **Build**: AOT publish clean throughout. `DiscreteUI.exe` grew
  38.3 MB → 38.9 MB (Phase 21a new VMs/Window + DataGrid sort
  helpers).
* **Commits**: 19 on `dev` since `06f7880` (was main).
* **Docs**: STATUS.md + plan.md + technical-notes.md +
  lessons-learned.md + pipe-protocol.md all updated.

### Late-day addition — DLL-free Mono symbol register path

User-requested: for cheat-table authors who don't want to inject the
UnityCEDumper DLL, generate AA scripts that call CE's built-in
Mono Lua API (`mono_findClass` / `mono_class_enumMethods` /
`mono_compile_method`) via the user's hand-rolled
`mono_registerSymbol` / `mono_registerSymbolEx` /
`mono_registerSymbolBySignatureMatch` helpers. Lives in the Methods
overload picker (right-click any method → Method overloads).

* **New** `scripts/mono_registersymbol_helpers.lua` — the three
  register helpers + `mono_ensureAttached()` + `SafeMonoDestroy`,
  all `if _G.xxx == nil` guarded. Side-effect-free at load time
  (Mono attach happens lazily on first register call) so the
  bridge bootstrap can ship it harmlessly even to users who never
  hit the DLL-free path.
* **Wired into** `BridgeLuaResources.LoadOrder` (now 8 entries) +
  `UnityDumpUI.Core.csproj` `<EmbeddedResource>` so Tools → Inject
  Discrete Bridge ships it into the CT alongside the existing 7
  `unity_*.lua` files.
* **New generator** `MonoRegisterSymbolAaScriptGenerator` —
  emits an AA whose `[ENABLE]` lazy-loads
  `mono_registersymbol_helpers.lua` out of the CT via
  `findTableFile` + `createStringStream` + `load` (same trick
  `BootstrapAaScriptGenerator` uses for `unity_dump.lua`), with
  idempotency guard `if _G.mono_registerSymbolEx == nil`. One
  `mono_registerSymbolEx(symName, ns, cls, method, { paramTypes })`
  call per selected overload, using **full-name** paramTypes
  (e.g. `'System.Int32'` not `'Int32'`) to avoid the substring
  trap (`feedback_signaturematch_substring`). `[DISABLE]`
  pcall-wraps `unregisterSymbol` per — partial enable still tears
  down cleanly.
* **UI surface** in `MethodOverloadsWindow`: two new buttons
  ("Copy mono_register AA", "Create mono_register in CE") gated
  on `IsMonoBackend` (new VM property threaded through from
  `MainWindowViewModel.Backend2 == "Mono"`). The CreateInCe
  button additionally requires `HasAobMakerBridge`.
* **Mono-only by design** — IL2CPP overloads route to the
  existing AOB-scan path. The new generator's `Build()` throws
  `ArgumentException` if any overload has `ModuleName` populated
  (IL2CPP signal), so a UI-gating bug would surface loudly
  rather than emit a broken AA.
* **Tests**: 180 → 193 pass (+11 new
  `MonoRegisterSymbolAaScriptGeneratorTests` covering helper
  lazy-load shape, single + multi overload, zero-param, null
  paramTypes, Lua apostrophe escaping, IL2CPP-input guard, empty
  + null input; +3 new `BridgeLuaResources` Inline cases for the
  8th file; −1 refactored `BridgeInjector` last-file assertion
  now reads `LoadOrder[last]` instead of hard-coded
  `unity_scene.lua`).
* **AOT publish**: 0 warnings, `DiscreteUI.exe` size delta
  negligible (new generator + 2 commands + Lua file are all small).
* **Live smoke**: pending — needs a Mono target + CE + AOBMaker.
  Two-step manual recipe: (1) Tools → Inject Discrete Bridge
  (ships the new helper file into the CT), (2) Methods tab →
  right-click method → Method overloads → tick overload → "Copy
  mono_register AA" → paste into CT entry → enable. Verify
  console banner `mono_registerSymbolEx: registered <name> = <addr>`
  appears.

---

## What landed in this session — Phase 22f live smoke (2026-05-18) caught + fixed 6 P0/P1 bugs

**Setup**: clean Cheat Engine 7.6 + AOBMaker plugin loaded + empty CT + DiscreteUI build #279 + BongoCat (Mono). Walked through [`docs/ce-bridge-integration.md` § "Manual smoke checklist"](ce-bridge-integration.md).

**Outcome**: Steps 1-8 of the 11-step checklist completed green after 6 fix iterations during the 2026-05-18 session. Each iteration surfaced a real production-blocker that the pre-existing unit test suite couldn't catch (live-runtime / multi-process / Lua-parse / Windows-pipe-kernel concerns). **Steps 9-11 confirmed PASS in a follow-up session** — user log captured `[UnityCEDumper.invoke] int(System.Int32) -> System.Int32 = 7 (0ms)` against `Mathf.Abs(-7)` (Step 9 target) on Mono build #304. Phase 22 fully closed.

### Bugs caught + fixed (in surfacing order)

| # | Bug | Severity | Commit | Fix |
|---|---|---|---|---|
| 1 | `unity_scene.lua` shipped with CRLF in working tree → AOBMaker plugin's `Stream.Size` verify mismatched the pre-LF-normalisation byte count → bridge inject **aborts at file 7/8** | P1 silent-fail | [`571ddf0`](https://github.com/) | `.gitattributes` enforces `*.lua text eol=lf` repo-wide; working copy normalised; `Read_EmbeddedContent_IsLfOnly_NoCrlf` [Theory] × 7 + tightened byte-for-byte round-trip test (was 5% tolerance, now exact). |
| 2 | `AobAaScriptGenerator` emitted `aobscanmodule(name, , pattern)` for Mono (empty module from `getMethodAobPattern`). CE accepts the syntax; scan **never matches**; symbol stays unresolved. | P1 silent-fail | [`bcc2ff6`](https://github.com/) | Per-overload: empty `ModuleName` → fall back to `aobscan(name, pattern)` whole-process scan + 5 regression tests in new `AobAaScriptGeneratorTests`. (This fix worked for unique patterns but unblocked bug #4 below.) |
| 3 | Bootstrap AA had `print('… CE''s symbolhandler …')` (C#-style apostrophe doubling). Lua reads `''` as close-then-open-string → `')' expected near "s symbolhandler…"` → **entire chunk fails to parse** → bridge never loads. | P0 silent-fail (the worst — "OK" status from CE, no bridge state, one cryptic error) | [`a169c1f`](https://github.com/) | Lua `\'` escape + `\w''\w` regex regression bar in all 3 generators (catches the bug shape — `letter''letter` — without false-positiving on legitimate empty-string `''` between commas). |
| 4 | Mono JIT routinely emits **multiple instantiations** of the same source method (different generic params, optimization tiers); their prologues are byte-identical. `aobscan(pattern)` returned 3 hits for `Mathf.Abs(int)` — CE picked the first which was wrong. | P1 wrong-answer | [`253402a`](https://github.com/) | Architectural fix: Mono JIT path no longer AOB-scans at all. New `registerMethodSymbol(symName, asm, ns, cls, method, sigFilter)` in `unity_symbols.lua` calls `findMethodInstances` RPC (DLL-side `mono_compile_method` — overload-precise). AA `[ENABLE]` emits `{$lua}` block with the bridge call instead of `{$asm} aobscan(...)`. IL2CPP path (AOT addresses in `GameAssembly.dll`) unchanged — keeps fast `aobscanmodule` form. |
| 5 | DLL pipe was `nMaxInstances=1` (Phase 11 design). Phase 22's "DiscreteUI persistent + bridge bursts" workflow contends for the single slot → bridge `io.open(PIPE)` returns ERROR_PIPE_BUSY (`"Invalid argument"` via Lua CRT) → **AA scripts can never reach the DLL** while DiscreteUI is connected. | P0 silent-fail | [`8075675`](https://github.com/) | `nMaxInstances=4` + detached worker thread per accepted client. Main loop returns to `CreateNamedPipeW` immediately; concurrent DiscreteUI + bridge work cleanly. Handlers already read-mostly (Mono/IL2CPP runtime is re-entrant); per-handler mutex deferred until a real race surfaces under load. |
| 6 | Bootstrap smoke called `U.callDLL('UnityDumper_GetBuildNumber','uint32')` via CE `executeCodeEx` (remote thread in target). Returns nil even when DLL + pipe + every other pipe-RPC works. Printed misleading "DLL not yet reachable" on every successful bridge load. | P2 false-negative | [`9919b56`](https://github.com/) | Replaced with inline pipe-`handshake` RPC (same path the bridge's `registerMethodSymbol` / `callUnityMethod` use). If those work, smoke passes. New `Build_SmokeChecksDllViaPipeHandshake` test pins the new shape + explicitly forbids the old `executeCodeEx` / `U.callDLL` / `UnityDumper_GetBuildNumber` substrings. |

### What's verified live (Steps 1-8 of the manual smoke checklist)

- ✅ **Step 1**: Clean Publish build (build #285, AOT 0/0 warnings, `DiscreteUI.exe` 38.6 MB)
- ✅ **Step 2**: CE 7.6 + AOBMaker plugin + empty CT — `AOBMaker: connected` green chip in DiscreteUI immediately
- ✅ **Step 3**: DiscreteUI launches, connectivity dot green
- ✅ **Step 4**: Tools → "Inject Discrete Bridge into CE Table" — 8/8 wire calls (after CRLF fix), ~120 ms total
- ✅ **Step 5**: CE `Table → Show table files` lists all 7 `unity_*.lua` files with expected byte sizes
- ✅ **Step 6**: Enable "Load Discrete Bridge" → CE Lua console prints all 7 module banners (after apostrophe-escape fix) → ready message via pipe handshake (after smoke replacement)
- ✅ **Step 7**: DiscreteUI Pick & Inject BongoCat → connected → build matches DLL build number
- ✅ **Step 8**: Methods grid right-click `UnityEngine.Mathf.Abs(System.Int32)` → "Create AA in CE" → new entry with `registerMethodSymbol(...)` form → enable → `[discrete] Mathf_Abs_int = 236D471F080` — single resolved address, no AOB ambiguity (after Mono-bridge-resolve + concurrent-pipe fixes)

### Steps 9-11 — all PASS (follow-up session)

- ✅ **Step 9** — MethodCallWindow → *Send to CE Table…* → AA entry → enable → CE Lua console printed
  `[UnityCEDumper.invoke] int(System.Int32) -> System.Int32 = 7 (0ms)` against `Mathf.Abs(-7)`. End-to-end
  pipeline (DiscreteUI → AOBMaker → CT entry → `{$lua}` block → bridge → `callUnityMethod` → `prettyPrint`)
  verified clean.
- ✅ **Step 10** — Address Lookup → *Send to CE Table…* → parent AA + child memory record landed in CE
  grid with live field value.
- ✅ **Step 11** — Save → close CE → reopen → re-enable bridge + per-feature entries survived the
  save/reopen cycle (CE serialised the 7 `unity_*.lua` files into the `.CT`; no need to re-run
  Tools→Inject Discrete Bridge).

Recovery state for the next session:
- All 6 fix commits on `dev` + merged to `main` (this hand-off commit)
- BongoCat may have been closed by user after the smoke; if so: relaunch → DiscreteUI Pick & Inject before any CE work
- CE may have been closed; if so: relaunch → reopen the in-progress smoke CT (or start fresh and re-run Tools→Inject Discrete Bridge)
- `dist/` is at build #285 — both DLLs + DiscreteUI.exe + 7 `*.lua` files all current

### Cumulative across Phase 22 (a→f + live-smoke fixes)

- **13 production commits** on `dev` (22a-22f + 6 live-smoke bug fixes)
- **+106 xUnit cases** (57 → 163, all pass)
- **6 production-blocker bugs** caught by the live smoke (would have hit on first user run on every machine — esp. bugs #1, #3, #4, #5)
- New Core types: `BridgeInjector`, `BridgeLuaResources`, `BootstrapAaScriptGenerator`, `CallMethodAaScriptGenerator`, `AddressLookupAaScriptGenerator`, `AaScriptChildRecord`, `CeValueType`, plus `Assembly` field added to `AaScriptOverload`
- Bridge methods added to `AobMakerBridge`: +4 (`InjectTableFile`, `CreateMemoryRecord`, `CreateAAScriptWithRecords`, `CheckAvailability`) + `AvailabilityChanged` event
- Bridge Lua additions: new `registerMethodSymbol(symName, asm, ns, cls, method, sigFilter)` in `unity_symbols.lua` — single-overload bridge-resolve (Phase 22f follow-up to `registerMethodOverloads`)
- DLL changes: `nMaxInstances=1 → 4` + thread-per-client in `PipeServer.cpp`
- UI surface added: Tools menu + 2 Send-to-CE buttons + AOBMaker connectivity dot + bridge-inject status banner
- New docs: `ce-bridge-integration.md`, lessons-learned additions for AOBMaker + executeCodeEx + concurrent-pipe + CRLF
- New smoke: `tools/Test-BridgeInject.ps1` (3 modes: PingOnly / default minimal / Full production replay)
- Build: #261 → #285 (24 build increments across the phase)

**Phase 22 outcome**: the AOBMaker-based bridge install + per-feature push surface is production-ready against Mono. IL2CPP path is structurally unchanged from Phase 18c (AOB scan against `GameAssembly.dll` — known good). Steps 9-11 closed the remaining smoke; the architectural correctness has been demonstrated end-to-end.

---

> **Older sessions** (Phase 22 a–e build-up, 4-fix live verification,
> Phase 16 docs, Phase 19d-2 polish, Phase 19 vertical + AOT win-only
> build, …) archived in [status-history.md](status-history.md).
> Carved out 2026-05-19 to keep this file readable.

---

## Recent commit history (most recent first)

```
950c0d8 feat(ui): highlight matching substring in Browser filter results
1e0d861 refactor(inspector): extract field classifier to Core + 38 unit tests
0315071 docs+fix: live-verify 4 backlog fixes (build #251) + clean ExportAPI C4244
2143a77 docs: refresh STATUS / plan after backlog sweep — 4 fixes shipped, Phase 22 active
587e794 fix(il2cpp): Unity 2018.x — demote two missing exports + MetadataParser fallback
cb3d100 fix(dll): v24 integer-decode — per-version dispatch on FieldDefault decoder
fd428d2 fix(dll): emit value_kind for R4/R8 const literals (Infinity / NaN preserved)
3e519a8 fix(dll): Mono findInstances now also returns class_ptr (cross-backend uniform)
e2bf776 feat: Phase 20a Lua scene-explorer bridge + 4 cliffs from live debug
1b86335 Update sync_vendor.ps1
8a8223e docs: refresh STATUS / plan for cross-PC handoff
f69cf2b docs: Phase 5 segment 2d — R-Type Dimensions EX (Unity 2018.1 / v24) surfaces missing IL2CPP exports
a32e6dc docs/tools: Phase 5 segment 2d — v39 runtime sweep 9/9 paths green on Songs of Silence
52c5879 docs(crosscheck): refresh v35-v39 section — production reality, not future work
938605c docs/tools: Phase 5 segment 2d — v24 + v39 cross-version verification
85e3f43 docs: correct stale Phase 5 segment 2d entries — already shipped, not deferred
7994d95 docs: complete Phase 16 — add 9 new docs/ files + close out the phase
ffbade7 docs: add README (en + zh-TW) and CONTRIBUTING at repo root
```

---

## What's open

### Formally deferred

| Item | Why | Required to unblock |
|---|---|---|
| ~~**Phase 12d — 32-bit Mono**~~ | ✅ **All 5 sub-phases closed 2026-05-19.** Build path (CMake + build.ps1 -Arch x86), x86 backend DLL with arch-aware Mono struct offsets (`ManagedLayout.h`), x86 proxy DLLs (`dist/x86/version.dll` + `winmm.dll`), new `inject-x86.exe` cross-bitness helper + `WindowsDllInjector` shell-out, and live-smoke on Legend of Mortal (Unity 2020.3.49f1) all shipped. Standalone helper smoked clean (LoadLibraryW=0x00D70000, OK). | Cosmetic v2 polish open: visually badge 32-bit picker rows; manual bitness toggle in ProxyDeployWindow (auto-detect works). Neither blocks anything. |

### Phase 19 polish (drafted but deferred — would land as 19d-3)

- ~~**Smart-typed editors**~~ ✅ **Shipped 2026-05-19 (Phase 19d-3)** — `MethodCallWindow`'s Value column now picks NumericUpDown / ComboBox / TextBox per row via the new `MethodCallEditorKind` enum. Sized-int params get a `decimal`-backed NumericUpDown bounded to the type's `[MinValue, MaxValue]` so out-of-range input clamps at the editor instead of bouncing back after Call. Enum params kick an async `getEnumValues` probe on window-open (method's own assembly first, then `UnityEngine.CoreModule` for `UnityEngine.*` namespaces); on success the row switches to a ComboBox of `name = decoded` choices and the wire `type` tag is re-routed through the enum's underlying integer type — fixes a latent bug where enums used to fall through to `type:"address"`. Float / double stay on TextBox (decimal can't represent the full double range). Build clean, 163/163 tests pass, AOT publish 0/0 warnings, `DiscreteUI.exe` 38.7 MB. See [plan.md §19d-3](plan.md#phase-19--lua-callable-unity-methods) for the implementation breakdown.

  **Live click-through smoke 2026-05-19 (user-confirmed, multi-target)**:
  * **IL2CPP target** — `MainGame.PARTY_WORK.SetValue(System.UInt32 ofs, System.Byte value)`. NumericUpDown rendered for both rows (up/down spinners visible), seeded `0`, manual entry `6` into `ofs` accepted. Null checkbox correctly disabled (greyed) on both value-type rows — `IsReference == false` so the CheckBox `IsEnabled` is false. **Intended** (value types can't be null in CLR; existing `IsReference` gating from 19d).
  * **Mono target (BongoCat / Unity 2022+)** — `UnityEngine.Mathf.Abs(System.Int32 value)`. NumericUpDown rendered for the Int32 row, manual entry `99` accepted. Null checkbox correctly disabled (value type). Confirms the smart-typed editors work on Mono as well as IL2CPP.
  * **No enum param** was present in either signature, so the ComboBox path is not yet live-exercised. Still pending a smoke against a method with an enum parameter (e.g. `Object.FindObjectsOfType(Type, FindObjectsInactive)` on a Unity 2022+ build).
  * **User feedback addressed same day** — `System.Boolean` params now render an `AutoCompleteBox` with `true`/`false` suggestions (FilterMode=StartsWith, MinimumPrefixLength=0 so the dropdown surfaces on focus) while still accepting any text the existing `bool.TryParse` consumes (case-insensitive, surrounding whitespace tolerated). Picks up `Bool` as a 5th `MethodCallEditorKind`; `IsEditorBool` derived prop drives `IsVisible`. Build + tests + AOT all clean.

### Open follow-ups (not blocking, drip-feed)

Closed in this session, kept here as crossed-out reference until next prune:

- ~~**Multi-row "Copy CE field" only exported the last-focused row**~~ ✅ **Fixed 2026-05-19**. Both the Fields tab DataGrid (`AssemblyBrowserViewModel.CopyCeFieldAsync`) and the Inspector tab DataGrid (`LiveInspectionViewModel.CopyCeFieldNodeAsync`) were reading the singular `SelectedItem` binding when the user had Ctrl/Shift-selected multiple rows. Avalonia DataGrid exposes the full selection via `SelectedItems` (`IList`), but it isn't a bindable property — the VMs now consume a `Func<IReadOnlyList<TRow>>` accessor wired by `MainWindow.xaml.cs` (`GetBrowserSelectedFieldRows` / `GetInspectorSelectedNodes`) that snapshots `DataGrid.SelectedItems`. Both grids gained `SelectionMode="Extended"` (matches the default but makes the contract explicit) + `x:Name="BrowserFieldsDataGrid"` so the code-behind can reach them. Exporter: new `CeXmlExporter.RenderFieldsAsync(IReadOnlyList<FieldEntry>, …)` emits one `<CheatEntry>` per supplied row under a single declaring-class wrapper; the single-row path stays byte-identical to the legacy `RenderFieldAsync` output (now a thin delegate). Inspector path: new private `WrapLeavesXml(IReadOnlyList<WalkedFieldNode>)` replaces the singular `WrapLeafXml` and emits one absolute-address leaf per node — works for heterogeneous selections (top-level fields mixed with expanded sub-tree rows). 4 new xUnit cases in `CeXmlExporterMultiFieldTests`: single-row shape parity, multi-row count + ordering + header label, empty-input ArgumentException. 167/167 tests pass; AOT publish 0/0 warnings.
- ~~**Phase 16 docs**~~ — ✅ closed 2026-05-07. All planned doc files now exist (see Phase Overview table).
- ~~**Phase 5 segment 2d cross-version smoke**~~ — ✅ partially closed 2026-05-07. v29 / v31 / v39 verified; v24 partial (registration + R4/R8 OK, I*/U* decoder bug); v27 still uncovered. See full table in [§Phase 5 segment 2d cross-version smoke](#phase-5-segment-2d-cross-version-smoke) below.
- ~~**Phase 18 multi-overload AA-script user-click smoke**~~ — ✅ closed 2026-05-07 against Farthest Frontier `ItemBundle.AddItems` (2 overloads).
- ~~**Classes list — "Copy class name" right-click menu**~~ — ✅ shipped 2026-05-19 (build #307). Sibling to "Find instances" / "Inspect (find instances)…" on the Classes ListBox. Pushes `ClassItem.DisplayName` (`"Namespace.Name"`, or just `Name` when namespace is empty) to the clipboard via the existing `Browser.CopyToClipboardAsync` `DataTransfer` pattern. Single-select (matches the ListBox's selection mode); multi-select deferred. Release + Tests + AOT Publish all clean (163/163 tests pass).

Genuine open items:

- ~~**Inspector "Class ptr" copy buttons**~~ ✅ **Shipped 2026-05-19** — two small `name` / `addr` buttons next to the Class ptr row in the Inspector header. `name` copies the fully-qualified `<namespace>.<name>` (or bare `<name>` when the namespace is empty); `addr` copies the displayed `0x…` pointer verbatim. Reuses the existing `LiveInspectionViewModel.CopyToClipboardAsync` hook (same `IClipboard.SetDataAsync(IAsyncDataTransfer)` path as Copy CE XML / Copy instance address). Mirrors the tiny-button style from the walked-field row actions (addr/ptr/name/HEX) so the Inspector header stays visually consistent. Localized via `Inspector.CopyClassNameTooltip` / `Inspector.CopyClassAddressTooltip` in `en.axaml`. Tests 163/163 PASS, AOT publish clean.
- ~~**Phase 22 — `.CT` embedding + AA-script generator UI tab**~~ ✅ **Shipped 2026-05-12 across 22a–22f, 6 commits.** Pivoted from "offline `.CT` builder" to "live CT population via AOBMaker plugin" after auditing the plugin's existing pipe protocol. Tools menu "Inject Discrete Bridge" + per-feature Send-to-CE buttons live; 148/148 tests pass; AOT clean. Live click-through smoke against CE 7.6 + AOBMaker + a Unity game still pending (see [`docs/ce-bridge-integration.md` § "Manual smoke checklist"](ce-bridge-integration.md)).
- ~~**Phase 25 — cross-class Symbol Finder**~~ ✅ **Shipped + live-smoked 2026-05-19** (build #363). New `searchSymbols` RPC + MainWindow Tab — see Phase Overview row above for details. **Phase 25b (interesting preset)** still open — needs a Unity-tuned curated keyword list (HP / Damage / Gold / XP / Speed / Health / Mana / Stamina / ...) distinct from UE5CEDumper's FProperty-typed one, plus a per-row score for the existing results grid. ~½ day when prioritised.
- ~~**Phase 21d Inspector field write-back live click-through**~~ ✅ **PASS 2026-05-19** on BongoCat Mono — right-click primitive row → Write value… modal → Apply → readMemory verifies + CE address-list refreshes via the existing AOBMaker bridge.
- ~~**enum ComboBox path live smoke**~~ ✅ **PASS 2026-05-19** via `UnityEngine.Object.FindObjectsByType(Type, FindObjectsInactive, FindObjectsSortMode)` on BongoCat — both enum params surfaced as ComboBox via the Phase 19d-3 `getEnumValues` cascade. Closes the Phase 19d-3 follow-up that had no enum-parameter smoke target.
- ~~**DLL-free Mono register path live smoke**~~ ✅ **PASS 2026-05-19** with `Object.FindObjectsByType` against BongoCat. Surfaced + fixed two production blockers in the same session: user-autorun shadows helper (→ `discrete_*` private namespace); CE Mono returns nil typename on Unity 2022+ (→ `mono_method_getSignature` fallback path). See "What landed" block above + lessons-learned for the forensic.
- **Phase 20 — Scene / GameObject explorer** (parked at 20a, see follow-ups) — **20a journey across one session 2026-05-08 on SaGa F2R**: shipped Lua bridge, then iteratively learnt FOUR cliffs the hard way: (1) wire envelope `{id, result}` unwrap missed; (2) sustained Unity-icall bursts freeze the game (Phase 19 v1 worker-thread limitation); (3) `findInstances UnityEngine.GameObject` returns IL2CPP RGCTX metadata records that fool both Lua filters AND `whatIsAt`; (4) IL2CPP code stripping removes most enumeration APIs — `Resources.FindObjectsOfTypeAll`, `Object.FindObjectsOfTypeAll`, single-arg `Object.FindObjectsOfType`, `SceneManager.get_sceneCount`, etc. all stripped on SaGa F2R's shipped build. Survivors: `Object.FindObjectsOfType(Type, bool)`, `GameObject.Find(string)`, `Camera.get_main()`, `SceneManager.GetActiveScene()` + `Scene.GetRootGameObjects()`. Recipe pivoted to the 2-arg `FindObjectsOfType` survivor. Three new sub-phases opened: **20a-2** (verify the new recipe end-to-end), **20-MT** (DLL-side main-thread marshaling for callMethod-heavy recipes), **20-HEAP** (DLL-side libgc heap walker as the strip-resistant fallback). Three new lessons in [lessons-learned.md](lessons-learned.md): IL2CPP stripping detection, signatureMatch close-paren trap, plus updates to the existing findInstances-false-positives entry. Full design in [plan.md §Phase 20](plan.md#phase-20--scene--gameobject-explorer--objectname-search). **20a-2 closed 2026-05-18** (see next bullet); 20-MT + 20-HEAP remain open.
- ~~**Phase 20a-2 — verify `getAllGameObjects` recipe end-to-end**~~ ✅ **Closed 2026-05-18** via new `tools/Test-FindObjectsOfType.ps1` smoke (4-step recipe replay over raw RPCs — `Type.GetType` → `FindObjectsOfType/ByType` → `walkArray` → `Object.get_name` × N). Cross-backend cross-version PASS: **SaGa F2R** (IL2CPP / Unity 2021) picked the 2-arg `FindObjectsOfType(Type, bool)` overload, 14,942 GameObjects in scene, sampled names came back as real `SoundObject_SeCh11..15` instances, 0 native_crashes. **BongoCat** (Mono / Unity 2022+) had **0** `FindObjectsOfType` overloads (the entire API was removed in Unity 2022+, not just deprecated), automatically fell through to `FindObjectsByType(Type, FindObjectsInactive, FindObjectsSortMode)` — the 3-arg compiled overload. 40,752 GameObjects, sampled names "Background" ×5, 0 native_crashes. **Bonus finding**: the two enum params on the Unity 2022+ API marshal as plain `int32` over the wire (ABI is identical to underlying type — no DLL `enum` tag needed). Lua bridge `getAllGameObjects` `tries[]` grew a third entry (3-arg ByType for Unity 2022+); first-match-wins keeps production calls on the historically-preferred path per Unity version. Phase 23's heap-scan filter is also indirectly validated by this run (0 native_crashes on real instance pointers, no stack/RGCTX residue surfaced).
- ~~**Phase 21 — Inspector field write-back**~~ ✅ **Shipped a/b/c + 21d live click-through PASS 2026-05-19**. v1 (primitives + raw address) is the entire shipped scope; v2 (reference-type writes — string + object pointer with proper GC-root marshaling) deferred until a user actually needs it. Full design in [plan.md §Phase 21](plan.md#phase-21--inspector-field-write-back).
- ~~**v24 integer-decode bug**~~ ✅ **Fixed + live-verified 2026-05-08** (commit `cb3d100`, build #251 against FF VI / Unity 2019.4 / v24) — added per-version dispatch hooked off `mp.version()`. v24/v27 → raw LE branch; v29+ → existing sentinel + compressed-int branch. Both branches emit `encoding: "raw_le"` / `"sentinel_compressed"` in the response. R4/R8/Bool/Char unaffected (always raw LE). Verified: `Int32.MaxValue` → 2147483647 (was -2147483648), `Int32.MinValue` → -2147483648 (was 0), both with `enc=4B` confirming the raw LE branch fired.
- ~~**Unity 2018.x missing IL2CPP exports**~~ ✅ **Fixed + live-verified 2026-05-08** (commit `587e794`, build #251 against R-Type Dimensions EX / Unity 2018.1 / v24) — `il2cpp_image_get_class_count` + `il2cpp_image_get_class` demoted from REQUIRED to OPTIONAL in `IL2CPPResolver`. `EnumerateClasses` now has a fallback path that walks `MetadataParser::GetTypeDefinition()` and calls `il2cpp_class_from_name(image, ns, name)` per type — null returns implicitly filter by image. Cost: O(N_global_typedefs) per call, slower than the native iterator but functional. **Verified**: `assemblies_count=36` (was 0), `findInstances UnityEngine.GameObject` returned 100 matches with `class_ptr` populated, scan took only 529 ms — the fallback enumeration is fast enough for production use, not a slow-path warning.
- ~~**`findInstances` IL2CPP `classPtr` empty**~~ ✅ **Fixed + live-verified 2026-05-08** (commit `3e519a8`, build #251 against Hollow Knight Silksong / Mono / Unity 6000.0) — was actually a stale STATUS note; IL2CPP did populate `class_ptr`. Real issue was Mono returning only `vtable` while consumers expected `class_ptr`. Fix: Mono findInstances now also returns `class_ptr` (additive, keeps `vtable` for compat). Verified: Silksong run printed `class_ptr : 0x000002201761E3D0` (no fallback to `vtable`), 100 Transform instances. Both backends now expose the canonical field; pipe-protocol.md updated to spell out "scan target = `vtable` on Mono, `class_ptr` on IL2CPP".
- ~~**JSON Inf/NaN value coercion**~~ ✅ **Fixed + live-verified 2026-05-08** (commit `fd428d2`, build #251 against SaGa F2R / IL2CPP / v29) — R4/R8 `il2cppGetMetadataTypeFields` decode now emits `value_kind`: `"normal"` / `"infinity"` / `"-infinity"` / `"nan"`. `value` stays `null` for non-finite (JSON spec compliance), `value_kind` recovers the semantic. Older clients ignoring `value_kind` see the same behaviour as before — purely additive. Verified: `System.Single.PositiveInfinity` → `value:null, value_kind:"infinity"`; `NegativeInfinity` → `"-infinity"`; `NaN` → `"nan"`; finite floats (MinValue/MaxValue/Epsilon/NegativeZero) all stamp `"normal"`.
- ~~**`findInstances` heap scan includes thread-stack + IL2CPP RGCTX-metadata false positives**~~ ✅ **Fixed + live-verified 2026-05-18 (Phase 23, build #301)** against BongoCat (Mono). `ScanForPointer` now filters `MEM_IMAGE` regions (DLL `.data` / `.bss` — eliminates RGCTX + Mono class tables) and thread stacks (`NtQueryInformationThread` + TEB walk). The "managed heap range" framing was wrong: `il2cpp_gc_get_heap_section_count` doesn't exist in any vendor `il2cpp-api-functions.h` (2017.1 → 6000.0). The negative-filter approach (exclude module + stack) is runtime-agnostic, works on Mono + all IL2CPP versions, and kills both documented false-positive sources. Live smoke on BongoCat: 1194 regions → 571 scanned (366 MEM_IMAGE + 257 thread stacks excluded). New `scan_stats` field in wire response + `scan` log category. Deprecated `_isValidInstance` removed from `scripts/unity_scene.lua`. Full forensic in [lessons-learned.md](lessons-learned.md).
- ~~**v27 verification target**~~ ✅ **Verified 2026-05-20** against **The Legend of Tianding / 廖添丁** (IL2CPP / Unity 2020.3.48f1 / x64) — the long-missing v27 target finally appeared in the Steam library. End-to-end PASS: handshake (81 assemblies, hook armed), Phase 20-MT stress smoke (1000 calls × Mathf.Abs, 8.33 ms/call main-thread, drain_count=1000, 0 timeouts), `Test-MetadataFields System.Int32` decodes `MaxValue` → 2147483647 + `MinValue` → -2147483648 (raw-LE 4-byte path, `enc=4B`), `Test-MetadataFields System.Single` decodes `MinValue` / `Epsilon` / `MaxValue` correctly; `PositiveInfinity` / `NegativeInfinity` / `NaN` show "(no decode)" in the smoke script but that's the JSON Inf/NaN value-coercion fix (build #251 commit `fd428d2`) — the wire carries `value_kind` correctly, the test script just doesn't surface it. **Confirms the v24/v27 → raw-LE / v29+ → sentinel-compressed-int dispatch (build #251 commit `cb3d100`) works on v27 the same as on v24.** No remaining decoder bugs on v27.
- ~~**v32+ metadata.dat parser**~~ ✅ **Shipped + live-smoked 2026-05-19** (build #336) — v32-v39 dispatch via new `TypeDefinition_v35`, `Il2CppSectionMetadata` + `HeaderV38` section reader, `IndexSizes` runtime-variable-width plumbing. `tools/Test-MetadataVersions.ps1` synthetic-fixture smoke PASS on v29/v31/v35/v37/v38/v39 + rejects v40. **Live smoke on Songs of Silence (Unity 6.1.x / v39)**: 40,501 TypeDefinitions + 44,047 string literals decoded; `Vector3` full field walk recovered all 15 expected fields with correct tokens + type indices. Full design + landed-vs-deferred breakdown in [il2cpp-metadata-crosscheck.md §"v35-v39 status — SHIPPED"](il2cpp-metadata-crosscheck.md).
- **Phase 17e — Strategy B address finder** — only-if-asked. Cached heap walk + interval tree for batch CE pointer-scan lookups. Skip until requested.
- **More automated test coverage** — ✅ Inspector classifier + HighlightSegmenter shipped 2026-05-08 (build #257, 5 → 57 tests). Big gaps still open: end-to-end `MetadataParser` round-trips per version, CE-XML live-mode rendering against a deterministic fixture, AobMakerBridge happy + sad-path with a fake pipe server. Drip-feed.
- ~~**Highlight matching substring**~~ ✅ **Shipped 2026-05-08** (build #257) — new `HighlightedTextBlock` control + `HighlightSegmenter` pure-logic helper. Wired into Assemblies / Classes / Fields (Name + Type) / Methods (Name) lists. Element-name binding from each filter `TextBox` so the live filter value flows into every row without VM plumbing. Inspector field grid (which has no filter — drives off `walkInstance` results) deliberately untouched.
- ~~**Fields / Methods per-cell copy-to-clipboard menu**~~ ✅ **Shipped 2026-05-19** — right-click any Browser Fields-tab row → 3 new items (Copy field name / Copy type / Copy "Declared in"); same on the Methods-tab row (Copy method name / Copy address / Copy "Declared in"). Address substitutes for return-type since `MethodRow` doesn't carry one — return type only comes from the heavier `FindMethodInstances` RPC. Handlers in [`MainWindow.axaml.cs`](../ui/UnityDumpUI/Views/MainWindow.axaml.cs) reuse the existing `CopyToClipboardAsync` (`DataTransferItem` + Avalonia 12 clipboard pattern). Single-select only; multi-select join-with-newline is a v2 follow-up (noted inline in XAML comment). 163/163 tests pass; AOT publish clean.
- **CSX live mode** — design dead-end as scoped (CSX is offset-relative; "live" only makes sense as snapshot-derived layout). CE XML already provides true absolute-address live mode. Defer until a user explicitly asks.

---

## Suggested first steps for the new session

Pick whichever fits the slot of time you have. Nothing on this list is blocking.

| Option | Effort | Why pick this |
|---|---|---|
| ~~**Phase 19d-3 + 5 follow-ups + 21a**~~ | — | ✅ All shipped 2026-05-19. See "What landed in this session" above. |
| ~~**DataGrid AOT sort trap**~~ | — | ✅ Diagnosed + fixed 2026-05-19. Three-layer trap, lessons-learned + technical-notes carry the full mechanism + recipe for the next DataGrid. |
| ~~**Phase 21 — Inspector field write-back**~~ | — | ✅ a/b/c shipped 2026-05-19 (build #333). **21d live click-through still pending** — fastest next step: launch BongoCat (Mono) or SaGa F2R (IL2CPP), inject DiscreteUI, walk an instance, right-click a primitive field, "Write value…", verify CE address-list updates in real time. Recipe in [plan.md §Phase 21](plan.md#phase-21--inspector-field-write-back). |
| **Phase 20-MT — main-thread marshaling for `callMethod`** | ~1 day | Sustained Unity-icall bursts can freeze the game (Phase 19 v1 worker-thread limitation surfaced during Phase 20a recipe testing). Design in [plan.md §Phase 20](plan.md#phase-20--scene--gameobject-explorer--objectname-search). Needed before heavy-fanout recipes like "name every GameObject in the scene" can run reliably. |
| ~~**Phase 12d (32-bit Mono)**~~ | — | ✅ **Fully closed 2026-05-19** — all 5 sub-phases shipped + live-smoked on Legend of Mortal. Cosmetic polish remains (ProcessPickerWindow 32-bit badge, ProxyDeployWindow manual bitness toggle) but underlying functionality complete. |
| ~~**v32+ metadata.dat parser**~~ | — | ✅ Shipped + live-smoked 2026-05-19 (build #336) against Songs of Silence (Unity 6.1.x / v39). 40k TypeDefs + 44k string literals + Vector3 full field walk all PASS. |

### Re-verify on the new PC (sanity check before continuing)

Once the repo is checked out + the dev VS DevShell is wired, before starting any new feature work it's worth confirming the new PC's environment doesn't drift from the dev box's:

```powershell
# 1. Build everything (Publish + AOT + tests, ~2 min on a warm cache)
.\build.ps1 -Mode Publish

# 2. Quick handshake against any one target (BongoCat / Silksong easiest because no per-target metadata caveats):
.\tools\List-UnityProcs.ps1
.\tools\Test-Handshake.ps1 -ProcessId <PID>

# 3. (Optional) Re-run the 4-fix verification recipes — see the entries in §"Open follow-ups"
#    against the 4 verified targets (Silksong / SaGa F2R / FF VI / R-Type) if you have any
#    of them installed on the new PC.
```

Acceptance: build #258+ rolls clean (0/0 trim+AOT warnings), `57/57 tests pass`, `Test-Handshake` shows `backend=…, build=258+, unity_version=…` against a live target.

### Live verifications (carried forward — still green)

These remain confirmed clicked-through on the dev box:

- ✅ Phase 9 — `MainGame/PLY_WORK` decode matches CE reference table value-for-value (SaGa F2R, IL2CPP).
- ✅ Phase 11 — `unity_dissect.lua` builds a 56-field Structure Dissect entry for `MainGame/PLY_WORK`; `unity_symbols.lua` registers 10 statics + 323 method JITs on Silksong's `GameManager`.
- ✅ Phase 14c — IL2CPP metadata v24/v27/v29/v31 dispatch verified against six games covering all four versions.
- ✅ Phase 17 — `whatIsAt` round-trips against SaGa F2R `PLY_WORK.HP` field address; lookup latency sub-ms after first cache build (~tens of ms).
- ✅ Phase 18 — single-overload AA-script generation against SaGa F2R `MainGame/PLY_WORK.AddGrowHpMax(int)`. AOBMaker `CreateAAScript` direct push.
- ✅ Phase 18 multi-overload — Farthest Frontier `ItemBundle.AddItems` (2 overloads: `(System.UInt32, System.UInt32)` + `(ItemBundle)`) — both AOB patterns scan-clean from CE, both `registersymbol` succeed, addresses resolve in `GameAssembly.dll+0xB20900` / `+0xB20980`. The two-symbol `[ENABLE]` block enables idempotent re-runs. Verified 2026-05-07.
- ✅ **Phase 19a/b/c** — both backends, three games. SaGa F2R (IL2CPP) + BongoCat (Mono) + Farthest Frontier (both — instance dispatch via `ItemBundle.get_numberOfItems()`). PowerShell smoke (`Test-CallMethod.ps1`) covers Mathf.Abs(int/float) + Convert.ToByte(int) + Int32.Parse(string) + String.Concat(string,string) + Instance + Custom scenarios.
- ✅ **Phase 19d** — UI MethodCallWindow user-tested via the Methods grid context menu.
- ✅ **Phase 19e** — `unity_invoke.lua` dofile + 4 scenarios green from CE Lua console on **both backends**: IL2CPP / SaGa F2R + Mono / BongoCat (2026-05-07). Mono-side run of the BongoCat recipe: `Math.Abs(-7) → 7` (0 ms), `String.Concat("Hello, ", "World") → "Hello, World"` (0 ms), `Convert.ToByte(-1) → System.OverflowException` (host stayed alive, message decoded from `MonoException + 0x18`), `prettyPrint(r)` round-trip clean. Wire layer was already byte-identical via the 19b PowerShell smoke; this confirms the Lua wrapper side too.
- ✅ **Phase 19d-2** — User click-through green on both UX paths: auto-skip on single-overload methods jumps straight to `MethodCallWindow` (no picker shown); Inspector cross-tab right-click → switches to Methods tab → subsequent Methods-grid right-click pre-fills the `Instance address` textbox from the inspector's selected instance. Build: 38.3 MB AOT, 0/0 trim/AOT warnings, 5/5 xUnit pass (build #234).
- ✅ **Phase 5 segment 2d cross-version sweep** (2026-05-07, build #235-236) — Registration scan: ✅ on Unity 2018.1 (R-Type / v24, types=19874), Unity 2019.4 (FF VI / v24), Unity 2021.3 (SaGa F2R / v29, baseline), Unity 2022.3 (Farthest Frontier / v31), Unity 6.1 (Songs of Silence / v39, types=144837). FieldDefault decode: ✅ R4/R8 across all versions, ✅ I*/U* on v29 / v31, ⚠️ I*/U* broken on v24 (v24 metadata uses raw LE; decoder assumes CLI compressed-int + sentinels). v32+ blocked at `MetadataParser` `version > 31` reject (by design).
- ✅ **Phase 5 segment 2d v39 runtime sweep** (2026-05-07, build #235) — `Test-V39RuntimeSweep.ps1` 9/9 paths green against Songs of Silence: handshake, `il2cppGetMetadataRegistration`, `getAssemblies` (253 assemblies), `getClasses UnityEngine.CoreModule` (1093 classes), `getClassInfo UnityEngine.GameObject` (143 methods, parent walk OK), `findInstances` (8/8 matches), `walkInstance` (decoded `m_CachedPtr +0x10`), `findMethodInstances UnityEngine.Mathf.Abs` (2 overloads with addresses + signatures), `callMethod Mathf.Abs(int:-7) → 7`. **v39 is first-class supported** for Inspector / browser / Phase 17/18/19 / Lua bridges; only `il2cppGetMetadataTypeFields` const decode + offline `metadata-cli` remain gated.

### Pending verification (small)

_(none — every shipped phase has a live click-through on the dev box. New work surfaced by the cross-version sweep is captured in "Open follow-ups" with fix sketches.)_

### Phase 5 segment 2d cross-version smoke

The `Il2CppMetadataRegistration` discovery uses a structural-shape scan (not a literal-byte AOB) — see [plan.md §Phase 5 "2d follow-up"](plan.md) for the full algorithm. Should work version-agnostically, but only v29 is formally validated. Quick recipe to cover the remaining versions:

**Pre-flight** (per target):

1. Inject `UnityDumper-IL2CPP.dll` into the game (DiscreteUI Pick & Inject or `Test-Handshake.ps1`). Confirm `handshake` reports `metadata_version`.
2. Identify a class with at least one **non-enum** field that has a `const` literal initializer — pretty much any user-defined class with `private const int kFoo = 42;` will do. Generic candidates: `UnityEngine.Mathf` (PI / Deg2Rad / Epsilon), `System.Math` (PI / E), or any game-specific class with tunable constants.

**Run the smoke**:

```powershell
.\tools\Test-MetadataFields.ps1 -Namespace UnityEngine -Name Mathf
```

**Pass criteria**:

- Banner near the top reads `Il2CppMetadataRegistration: YES` (green).
- For each field with a `default`, the line reads `tag=0x.. = <value>  enc=NB` in cyan — NOT just `hex16 = ... raw_size=N data#X` alone.
- `value` is sane for the type (e.g. `Mathf.PI` → `3.141593`, `Mathf.Deg2Rad` → `0.0174533`).
- No process crash; no near-miss spam in `%LOCALAPPDATA%\UnityCEDumper\Logs\<pid>\init\`.

**Targets to cover**:

| Metadata version | Target | Status |
|---|---|---|
| v24 | FINAL FANTASY VI (Unity 2019.4.26f1) | ⚠️ Partial 2026-05-07 — registration scan works, R4/R8 decode works, but I*/U* decoder is wrong (see "v24 integer-decode bug" below) |
| v24 | R-Type Dimensions EX (Unity 2018.1.9f2) | ⚠️ Runtime resolver bails (Unity 2018.1 IL2CPP missing two required exports — see "Unity 2018.x IL2CPP missing-exports" below); registration scan still works (`types_count=19874`) |
| v27 | (any Unity 2020 game in the test matrix) | ⏳ |
| v29 | SaGa F2R | ✅ verified |
| v31 | Farthest Frontier (Unity 2022.3.62f3) | ✅ verified 2026-05-07 |
| v39 | Songs of Silence (Unity 6000.3.8f1) | ✅ Runtime path 9/9 green (handshake / domain walk / class enum / getClassInfo / findInstances / walkInstance / Phase 18 findMethodInstances / Phase 19 callMethod); only `il2cppGetMetadataTypeFields` const-literal path + offline `metadata-cli` blocked behind `version > 31`. See "v39 — first-class supported" subsection below. |

**v31 verification details** (2026-05-07, build #235):

- Banner: `Il2CppMetadataRegistration: YES` ✅
- `System.Int32.MaxValue` → tag 0x08 (I4), value 2147483647, enc 1B (sentinel `0xFE` = type_max, decoder correctly bounded to int max)
- `System.Int32.MinValue` → tag 0x08 (I4), value -2147483648, enc 1B (sentinel `0xFF` = type_min)
- `System.Single.MinValue` / `Epsilon` / `MaxValue` / `NegativeZero` → tag 0x0C (R4), correct float values, enc 4B
- `System.Single.PositiveInfinity` / `NegativeInfinity` / `NaN` → tag 0x0C identified, decoder ran, but wire `value` came back null (see "JSON Inf/NaN limitation" below).

**Known non-blocking issue surfaced during v31 verification**: JSON spec doesn't allow `Infinity` / `-Infinity` / `NaN` as numeric literals. The R4 decoder code path runs and sets `enc_bytes=4`, but `nlohmann/json` serializes the float value as `null` for the three special cases. The smoke script reports "(no decode)" when value is null. Tag identification still works. Fix would be either (a) emit special-case strings (`"infinity"` / `"nan"`) for these values, or (b) add a flag like `"value_kind": "infinity"` alongside a null `value`. Defer until a user actually needs to read these specific constants.

### v24 integer-decode bug (surfaced 2026-05-07 against FF VI)

The v24 cross-version smoke uncovered a real bug in the integer FieldDefault decoder. Banner / structural-shape scan / R4 / R8 paths all work on v24 — but the I*/U* family (Bool / Char / I1-I8 / U1-U8) decode wrong values.

**Root cause**: the I*/U* decoder in `il2cppGetMetadataTypeFields` (ExportAPI.cpp:1860-1875 area) assumes **CLI compressed-int with sentinel encoding** (`0xFE` = type_max, `0xFF` = type_min, else compressed-signed). That's correct for v29 / v31. v24 IL2CPP code-gen instead emits **raw little-endian bytes** for integer constants — same convention floats already use unconditionally.

**Concrete reproduction**:

| Field (v24, FF VI) | raw_hex first 4 bytes | LE-int32 reading | Should be | Decoder returns |
|---|---|---|---|---|
| `System.Int32.MaxValue` | `FFFFFF7F` | `0x7FFFFFFF` = 2147483647 ✅ | 2147483647 | **-2147483648** ❌ (saw `0xFF` first byte → type_min sentinel) |
| `System.Int32.MinValue` | `00000080` | `0x80000000` = -2147483648 ✅ | -2147483648 | **0** ❌ (saw `0x00` → 1-byte CLI compressed-int = 0) |

R4 / R8 are unaffected because their decoder is unconditional `memcpy(p, 4 or 8)`.

**Fix options** (none implemented yet):

1. **Per-version dispatch** — version-aware decoder, raw LE for v24 / v27, sentinel + compressed-int for v29+.
2. **Auto-detect** — if the data slot is exactly N bytes for a fixed-N type (1 for I1, 4 for I4, 8 for I8) AND the value would otherwise be a sentinel result, prefer raw LE. Brittle when the actual value really IS the sentinel (`int.MaxValue` is the legitimate max).
3. **Feature flag at the registration-find level** — `IL2CPPMetadataRegistration::IsLikelyV24Encoding()` heuristic, reported as `encoding_format` in the wire response, smoke / consumers branch on it.

Option 1 is cleanest. Estimated ½-day work: hook the encoding choice off `MetadataParser::version()` (already known at the site), branch the I*/U* decoder, add v24/v27 unit tests. Defer until a user explicitly needs `getMetadataTypeFields` const-int values on v24 / v27.

**Workaround in the meantime**: read `raw_hex_first16` and decode manually for v24 / v27. The Inspector and class browser don't depend on this path — only `il2cppGetMetadataTypeFields`'s typed-value field does.

### v39 — first-class supported, except for two metadata.dat-only paths (verified 2026-05-07 against Songs of Silence)

Songs of Silence (Unity 6000.3.8f1) is the first **v32+ metadata target** to land in the test matrix. Initial sweep found the structural-shape `Il2CppMetadataRegistration` AOB scan working cleanly:

```json
{ "found": true,
  "address": "0x00007FFE9C13D830",
  "types_array": "0x00007FFE9C22A1C0",
  "types_count": 144837 }
```

Follow-up sweep ([Test-V39RuntimeSweep.ps1](../tools/Test-V39RuntimeSweep.ps1)) extended that to the full runtime API surface — **9 / 9 paths green**:

| Path | Result |
|---|---|
| `handshake` / `il2cppGetMetadataRegistration` / `getAssemblies` / `getClasses` | ✅ |
| `getClassInfo UnityEngine.GameObject` (143 methods, parent=Object, inheritance walk) | ✅ |
| `findInstances UnityEngine.GameObject` (8 / 8 matches, class-pointer match works) | ✅ |
| `walkInstance` first GameObject (decoded `m_CachedPtr` at +0x10) | ✅ |
| `findMethodInstances UnityEngine.Mathf.Abs` (2 overloads, both with AOT addresses + decoded signatures) | ✅ Phase 18 |
| `callMethod Mathf.Abs(int:-7) → 7` | ✅ Phase 19 (marshal + invoke + return decode) |

**Effective conclusion: v39 is first-class supported for ~95 % of typical usage** — Inspector, class browser, Phase 17 (`whatIsAt`), Phase 18 (overload + AOB), Phase 19 (`callMethod`), and the CE Lua bridges all work without any v32+ specific code. The runtime APIs (`il2cpp_image_get_class`, `il2cpp_class_get_fields`, `il2cpp_method_get_pointer` fallback, `il2cpp_runtime_invoke`) didn't shift between v31 and v39 in any way that affects our consumers.

**Two paths still gated behind unimplemented v32+ metadata.dat parsing**:
1. `il2cppGetMetadataTypeFields` — const literal decoding via `MetadataParser`. Affects only the typed-FieldDefault values in the field list, NOT field iteration / offset / type strings (those go through the runtime).
2. Offline [`metadata-cli`](../tools/metadata-cli/) — pure on-disk analysis. Live runtime path is an unblocked alternative for everything except diff-checking metadata across versions.

Both are bounded-scope features the average user doesn't need on a v39 game.

`MetadataParser::LoadHeader` continues to reject `version > 31` by design — see the defensive guard in [il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md). Implementing v32+ metadata-table parsing is a 2-3 day project documented there.

**Minor cosmetic finding from the sweep**: `findInstances` IL2CPP path returns empty `classPtr` field in the response (matches still come back correctly — heap scan finds them, just the response builder doesn't populate the per-class pointer field for IL2CPP). Not v32+ specific; low-priority backlog item.

### Unity 2018.x IL2CPP missing-exports (surfaced 2026-05-07 against R-Type Dimensions EX)

A second v24 target — R-Type Dimensions EX (Unity 2018.1.9f2) — exposed a different cliff: **Unity 2018.x IL2CPP runtime is missing two exports we currently mark REQUIRED**:

- `il2cpp_image_get_class_count(image) → size_t`
- `il2cpp_image_get_class(image, idx) → Il2CppClass*`

`IL2CPPResolver` log: `required 16/18, optional 23/25 (resolved=no)`. The resolver's `resolved=no` flag gates every runtime RPC — `getAssemblies` / `getClasses` / `getClassInfo` / `findInstances` / `walkInstance` / `findMethodInstances` / `callMethod` all return `il2cpp runtime not resolved`.

**Important nuance**: this is a **Unity-version**-sensitive issue, not a metadata-version one. FF VI is also v24 metadata but Unity 2019.4 — its runtime exports both APIs. So:

| | metadata version | Runtime exports OK? | Outcome |
|---|---|---|---|
| Unity 2018.1 (R-Type) | v24 | ❌ missing 2 required | Resolver bails, runtime path unusable |
| Unity 2019.4 (FF VI) | v24 | ✅ | Runtime path works; only the v24-metadata-format integer decoder bug remains |

The two missing exports were added between Unity 2018.x and 2019.x. Most Unity 2018.x games used Mono rather than IL2CPP for Windows (IL2CPP for Windows didn't become common until 2019+) — so impact is limited to the small subset of "early IL2CPP Windows ports" like R-Type Dimensions EX (2014-era port).

**Important confirmation**: the structural-shape `Il2CppMetadataRegistration` AOB scan **still works** on Unity 2018.1 (`il2cppGetMetadataRegistration` returns `found=YES, types_count=19874`) — the discovery heuristic is genuinely **version-portable from Unity 2018.1 through Unity 6.1** (the full span of our test matrix).

**Fix sketch** (~2 hr, deferred):

1. Demote the two missing exports from REQUIRED → OPTIONAL in `IL2CPPResolver`.
2. Add a fallback in `IL2CPPClassWalker::EnumerateClasses` that, when those two exports are unbound, iterates `MetadataParser`'s `TypeDefinitions` table (filtered by image) and calls `il2cpp_class_from_name(image, ns, name)` for each — this older API has been exported since Unity 2017. Slower but functional.
3. Same pattern for any other "image-based class enumeration" call site.

Defer until a user reports needing Unity 2018 IL2CPP support. Today the workaround is "use a Unity 2019.4+ test target".

If `Il2CppMetadataRegistration: no` shows up on any version, the registration's PE-section layout or struct shape changed between versions — would need a per-version layout dispatch in `IL2CPPMetadataRegistration.cpp` (currently hardcoded `kRegBytes = 0x80` for v29). The init log will have `MetadataRegistration: near-miss @0x.. typesCount=N: <reason>` lines pointing at what failed the heuristic.

---

---

## What landed in the prior session — Phase 22 (Live CT Population via AOBMaker), end-to-end

Six commits across one session. The original Phase 22 plan ("offline `.CT` builder with Ascii85 + XML serializer + cart UI") got scrapped on day 1 after auditing AOBMaker's CE plugin (which already exposes `InjectTableFile` + 4 sibling commands that mutate a live, currently-open CT) and UE5CEDumper's `<EmbeddedResource>` pattern. New approach: DiscreteUI fires AOBMaker pipe commands; user saves via CE File→Save. Saved ~1.5 days from the original 3.5-day estimate.

### 22a — `AobMakerBridge` extension (commit `296f76f`)

`InjectTableFileAsync(fileName, content) → (bool Ok, string? ErrorMessage)`, `CreateMemoryRecordAsync(...)`, `CreateAAScriptWithRecordsAsync(description, script, records, autoActivate, hideChildren)`, `CheckAvailabilityAsync()`. New `IsAvailable` property + `AvailabilityChanged` event (transition-only fire — matches `PipeClient.Disconnected` pattern). New `CeValueType` enum (CE TVariableType ordering 0–9) + `AaScriptChildRecord` record type. Wire model extended with 6 new optional fields + nested `records[]` array via source-gen JSON context (AOT-clean). 15s timeout for InjectTableFile (matches CE-side `synchronize()` + `Stream.Size` verification cost). Tests: 11 new xUnit cases via fake `NamedPipeServerStream` (matches `PipeClientLifecycleTests` pattern). `InternalsVisibleTo("UnityDumpUI.Tests")` added so the test-only `(FileLogger?, string pipeNameOverride)` ctor is reachable.

### 22b — Embed 7 bridge lua files (commit `99b25f8`)

7× `<EmbeddedResource Include="..\..\scripts\unity_*.lua">` entries in `UnityDumpUI.Core.csproj` with stable `UnityDumpUI.Core.Resources.Bridge.<file>` LogicalName. New `Services/BridgeLuaResources.cs` exposes `LoadOrder` (utils first — `unity_dump.lua` does `require 'utils'`), `Read(name)`, `ReadAll() → IReadOnlyList<(FileName, Content)>`, `ListEmbeddedNames()` diagnostic. Hosted in Core (not UI as the original plan sketched) so the test project's existing Core-only `ProjectReference` reaches the accessor cleanly. AOT-safe (`GetManifestResourceStream` is on the AOT happy path). Tests: 14 new (incl. [Theory] across all 7 files + manifest-matches-LoadOrder check + byte-for-byte round-trip vs disk for `unity_scene.lua` — the largest file, most likely to surface stream truncation).

### 22c — Bootstrap AA-script generator (commit `421c3c8`)

`BootstrapAaScriptGenerator.Build()` emits the "Load Discrete Bridge" AA whose `[ENABLE]`:
1. Idempotency guard via `_G.discrete_bridgeLoaded`.
2. `findTableFile` + `Stream.copyFrom` + `DataString` per file (CE's portable read pattern).
3. **Pre-populates `package.preload['utils']`** before running `unity_dump.lua` — solves the `require 'utils'` problem in the no-filesystem CT context without modifying any existing script.
4. Iterates `BRIDGE_FILES`, skips utils, `load() + chunk()` each file.
5. Smoke: `pcall U.callDLL('UnityDumper_GetBuildNumber','uint32')` — soft-fails with a clear message if DLL not yet injected.
6. Sets `_G.discrete_bridgeLoaded = true`.

`[DISABLE]` clears the flag (CE has no chunk-unload primitive — clearing the flag is enough to allow re-`[ENABLE]`). Const-string template + minimal substitution (`__BRIDGE_FILES_LIST__` + `__ENTRY_DESCRIPTION__`); no dynamic AA generation. Tests: 11 new cases pinning preload-before-loop ordering, file-list match with `BridgeLuaResources.LoadOrder` via regex token count, no leftover `__PLACEHOLDERS__`, `EntryDescription = "Load Discrete Bridge"` stability for AOBMaker duplicate-detect when it ships.

### 22d — Tools menu + `BridgeInjector` orchestrator (commit `6cdfdc3`)

New `BridgeInjector.InjectAsync(IAobMakerBridge, IProgress<BridgeInjectProgress>?, CancellationToken) → BridgeInjectResult` runs the 8-step pipeline (1× CheckAvailability + 7× InjectTableFile + 1× CreateAAScript). Stops on first failure with structured `BridgeInjectResult { Ok, FilesInjected, AaScriptCreated, FailedStepDescription, ErrorMessage }` so the VM can build a precise status line ("Failed at 'Injecting unity_dump.lua': …") without re-deriving labels. Stateless / static — testable in isolation with a mock `IAobMakerBridge` (no PipeClient/FileLogger entanglement).

VM hoist: `AobMaker` is now a top-level `MainWindowViewModel` property (was leaking through `Inspector.AobMaker`). Inspector + MethodOverloads + Tools menu share one instance. New `[ObservableProperty] IsAobMakerAvailable` mirrors the bridge's `IsAvailable` via `AvailabilityChanged`; opportunistic startup probe sets the connection-bar dot. New `[ObservableProperty] BridgeInjectStatus` — per-step status line (persists after run for read-at-your-pace UX).

UI: Tools ▾ Button + MenuFlyout in the connection bar (room for Phase 22e per-feature entries to join later). "AOBMaker: connected/not connected" colored status dot. Bridge inject status banner (green strip, monospace) appears only after the first inject this session.

**Documented deviation from plan**: menu item NOT pre-gated on `IsAobMakerAvailable`. Pre-gating creates a chicken-and-egg loop (user opens CE *after* DiscreteUI → menu stays disabled forever without a polling timer). Instead the command runs CheckAvailability as step 1 and surfaces "open CE with the AOBMaker plugin loaded" via `BridgeInjectStatus`. Same UX pattern as the existing InjectAndConnect button.

Tests: 7 new orchestrator cases pinning call order, file sequence, abort-on-each-failure-mode, plugin-error message forwarding.

### 22e — Per-feature Send-to-CE-Table entries (commit `bcd378d`)

Two new generators in Core/Services/Export:
- `CallMethodAaScriptGenerator.Build(asm, ns, cls, method, sigMatch, instance, args)` — wraps `callUnityMethod(...)` from `unity_invoke.lua` into a `{$lua}` AA block. Maps every primitive type to its `argTYPE(v)` builder. Single-quote/backslash/newline escape for string args. Invariant-culture float (catches CJK locale comma trap). Out-of-range integers clamp instead of fail.
- `AddressLookupAaScriptGenerator.Build(req)` — wraps a `WhatIsAtResult` into `CreateAAScriptWithRecords` payload: parent AA registers `DiscreteAt_<Cls>_<hex>` symbol, child records expose the resolved field at its offset with proper CLR→CE TVariableType mapping (incl. enum unwrapping to underlying type).

UI wiring: MethodCallWindow + AddressLookup grew "Send to CE Table…" buttons (bound to `HasAobMaker` → button hidden when no bridge). Bridge threaded through via the existing `CopyToClipboardAsync`-style post-construction setter pattern (no ctor signature churn). MethodOverloadsViewModel exposed its bridge as a public property so MethodOverloadsWindow can thread it through to child MethodCallWindow.

Bridge-loaded guard prefix on every generated `[ENABLE]`:
```lua
if not _G.discrete_bridgeLoaded then
  print('[Discrete] Enable "Load Discrete Bridge" first')
  return
end
```
Phase 18c's pure-AOB scripts deliberately don't carry the guard (they don't depend on the bridge — `aobscanmodule` + `registersymbol` are standalone).

Drive-by fix to `BridgeInjectorTests`: `Progress<T>.Report` posts to the captured `SyncContext`, which xUnit doesn't have. Replaced with a synchronous `SyncProgress<T>` helper. Test now passes deterministically.

Tests: 48 new ([Theory] across 10 primitive arg builders + 14 CLR→CE type mappings + escape/clamp/sanitize edges).

### 22f — Integration doc + wire smoke (this commit)

New [`docs/ce-bridge-integration.md`](ce-bridge-integration.md): user journey (5-click happy path), what lands in the CT (file list + AA shape), generated entry shape examples for each of the 3 generators, troubleshooting (10+ scenarios), manual smoke checklist (11 steps to run after any Phase 22 code change), architecture references.

New [`tools/Test-BridgeInject.ps1`](../tools/Test-BridgeInject.ps1) PowerShell wire smoke. Three modes:
- `-PingOnly` — just probes pipe availability.
- (default) — minimal smoke: 1 stub `_discrete_smoke.lua` inject + 1 stub AA script. Useful for "is the pipe wired right?" probes without needing the repo's bridge files.
- `-Full` — replays the production pipeline reading `scripts/*.lua` from the repo (bypasses DiscreteUI, useful for AOBMaker plugin update validation without launching DiscreteUI / a Unity game).

Per-step result printed with timing + plugin-side error message on failure. Production timeouts respected (15s for InjectTableFile, 5s for everything else).

### Cumulative across 22a–22f

- 6 production commits + 1 doc-only commit
- 91 new xUnit cases (57 → 148, all pass)
- 1 new orchestrator (`BridgeInjector`)
- 3 new AA-script generators (`Bootstrap`, `CallMethod`, `AddressLookup`)
- 1 extended bridge (`AobMakerBridge` +4 commands +1 event)
- 1 new resource accessor (`BridgeLuaResources` over 7 embedded `.lua` files)
- 1 hoist (`AobMaker` from `Inspector` → top-level `MainWindowViewModel`)
- 1 new menu entry + 2 new buttons + 1 status indicator + 1 status banner in MainWindow.axaml
- 1 new doc (`ce-bridge-integration.md`)
- 1 new smoke (`Test-BridgeInject.ps1`)
- AOT 0/0 trim+AOT warnings throughout, `DiscreteUI.exe` 38.3 MB → 38.6 MB (+0.3 MB for 7 embedded scripts + Core code)

**Live click-through smoke closed** — Steps 1-8 verified 2026-05-18 (with 6 bugs caught + fixed); Steps 9-11 verified in a follow-up session (user log: `Mathf.Abs(-7) → 7`). Full 11-step checklist green end-to-end against BongoCat (Mono).

---

## What landed in the session before — 4-fix live verification + Inspector classifier extract + Browser filter highlight

Three deliverables across one session. Heavy on test coverage + UX polish, light on new capability.

### Cross-PC sync trap surfaced + recovered

Session opened with a four-target verification of the 2026-05-08 backlog fixes — and **all four failed at first run**. Root cause: `Inject-Dll.ps1` reads from `build/dll-both-release/bin/`, not `dist/`. The 18:56 build that landed in `build/` predated the 21:11–21:18 fix commits. `dist/UnityDumper-IL2CPP.dll` had been updated to 21:18:03 (21 sec before the last fix commit) so it was *also* still missing one fix. Forcing a `.\build.ps1 -Target DLL -Backend Both` (build #251) made every fix live-pass on the next try. Logged in [lessons-learned.md](lessons-learned.md) — DO NOT skip a rebuild before live verification when the most recent thing you did was a commit, even if `dist/` artifacts have a "recent" timestamp.

### Backlog-sweep fixes — all 4 live-verified ✅

Targets / observations (full breakdown in §"Open follow-ups" entries below):

| Fix | Commit | Target | Observation |
|---|---|---|---|
| Mono `findInstances class_ptr` | `3e519a8` | Hollow Knight Silksong / Mono / Unity 6000.0 | `class_ptr : 0x000002201761E3D0` (no fallback to `vtable`), 100 Transform instances. |
| R4/R8 `value_kind` for Inf/NaN | `fd428d2` | SaGa F2R / IL2CPP / v29 | `PositiveInfinity` → `value:null, value_kind:"infinity"`; `NegativeInfinity` → `"-infinity"`; `NaN` → `"nan"`; finite floats all `"normal"`. Raw JSON sanity-checked end-to-end. |
| v24 integer-decode | `cb3d100` | FF VI / IL2CPP / v24 / Unity 2019.4 | `Int32.MaxValue` → 2147483647 (was -2147483648); `MinValue` → -2147483648 (was 0); `enc=4B` proves the raw-LE branch fired. |
| Unity 2018.x missing IL2CPP exports | `587e794` | R-Type Dimensions EX / IL2CPP / v24 / Unity 2018.1 | `assemblies_count=36` (was 0); `findInstances UnityEngine.GameObject` returned 100 matches with `class_ptr` populated; scan only 529 ms — fallback enumeration is fast enough to be the production path, not a degraded slow lane. |

Drive-by cleanup in commit `0315071`: `ExportAPI.cpp:1863` C4244 narrowing warnings cleaned up (`metadataVersion` retyped from `uint16_t` to `int32_t` to match `MetadataParser::version()`'s return). 0 warnings post-build #253.

### Inspector classifier extracted + 38 unit tests (commit `1e0d861`)

`WalkedFieldKind` enum + `ClassifyInstanceField` + `TryParseCollectionType` + `TryParseClassName` moved out of `LiveInspectionViewModel.cs` (net -150 lines from the VM) and into a new pure-logic class `UnityDumpUI.Core.Services.InstanceFieldClassifier`. The VM now delegates; behaviour unchanged.

New `InstanceFieldClassifierTests.cs` covers all five precedence rules:
- **Rule 1**: `IsEnum` trumps everything (including the gotcha case where `IsValueType=true` AND `IsEnum=true` — without the short-circuit, the field would route to `ValueStruct` and crash on expand).
- **Rule 2**: Collection (`T[]` / `List<T>`) with empty-pointer degradation to `Leaf`.
- **Rule 3**: `ValueStruct` via server-side `IsValueType` flag (modern resolver) vs the legacy `UnityTypeMap`-driven path (which only catches Unity-known structs like Vector3 / Quaternion / Color — documented limitation; custom user structs need the modern resolver's `IsValueType=true`).
- **Rule 4**: `PointerToClass` including the dotted-namespace-in-generic-arg case (`MainGame.ClassAry<MainGame.CARRYITEM>` resolves to outer `MainGame.ClassAry`).
- **Rule 5**: `Leaf` fallback for `System.*`, unknown types, null type, etc.

### Browser filter-highlight UX (commit `950c0d8`)

New `HighlightedTextBlock` Avalonia control + `UnityDumpUI.Core.Services.HighlightSegmenter` pure-logic helper. Wired into the four filtered Browser lists:

| List | Filter binding | Highlighted columns |
|---|---|---|
| Assemblies (ListBox) | `Browser.AssemblyFilter` → `#AssemblyFilterBox.Text` | Name |
| Classes (ListBox) | `Browser.ClassFilter` → `#ClassFilterBox.Text` | Name + Namespace |
| Fields (DataGrid) | `Browser.FieldFilter` → `#FieldFilterBox.Text` | Name + Type |
| Methods (DataGrid) | `Browser.MethodFilter` → `#MethodFilterBox.Text` | Name |

Inspector Fields grid is deliberately untouched — it has no filter (drives off `walkInstance` results); nothing to highlight.

Two implementation notes worth remembering (now logged in [lessons-learned.md](lessons-learned.md)):
1. **Avalonia 12 `TextBlock.Text` and `Inlines` are mutually-exclusive sources.** Setting both leads to undefined rendering. The control uses a `SourceText` property and flips between plain-text and inline-runs internally, clearing the other source first.
2. **DataGrid highlight requires `DataGridTemplateColumn` + `CellTemplate`** — `DataGridTextColumn` doesn't accept custom controls. Switching preserves sort behaviour via `SortMemberPath="Name"` etc.

Element-name binding (`{Binding #FilterBox.Text}`) means the live filter value flows into every row template without the VM having to push it onto each row record.

`HighlightSegmenter` has 14 [Fact]/[Theory] cases covering all the edge cases — empty filter, null source, filter-not-found, at start / end / middle, multiple occurrences, adjacent matches, case-insensitive preservation, filter-equals-source, overlapping patterns (resolved left-to-right, no greedy backtrack).

Total test count went from **5 → 57** across the session.

### Tooling state for the new PC

- `dist/` is up to date as of build #257 (Publish / AOT — `DiscreteUI.exe` 38.3 MB, both DLLs at 576 / 545 KB).
- `build/dll-both-release/bin/` matches dist (verified — `Inject-Dll.ps1` will pick up the fresh DLLs).
- All 4 verification targets are documented in the §"Open follow-ups" block — Silksong / SaGa F2R / FF VI / R-Type. If you want a fresh smoke run on the new PC, the recipes are in §"Suggested first steps" → "Re-verify on the new PC" (added below).

---

## What landed previously — Phase 16 docs + Phase 5 segment 2d cross-version sweep

Two big chunks across one long session. No code changes to the DLL or UI binaries; pure documentation refresh + smoke-test infrastructure + verification against five Unity-version targets.

### Phase 16 docs — closed end-to-end

12 new files (≈ 3300 lines):

- **Repo root**: [`README.md`](../README.md), [`README.zh-TW.md`](../README.zh-TW.md), [`CONTRIBUTING.md`](../CONTRIBUTING.md).
- **`docs/`**: [`lessons-learned.md`](lessons-learned.md), [`proxy-dlls.md`](proxy-dlls.md), [`metadata-cli.md`](metadata-cli.md), [`export-formats.md`](export-formats.md), [`mono-internals.md`](mono-internals.md), [`il2cpp-internals.md`](il2cpp-internals.md), [`dll-spec.md`](dll-spec.md), [`ui-spec.md`](ui-spec.md), [`technical-notes.md`](technical-notes.md).

`lessons-learned.md` consolidates every memory entry (runtime_invoke marshaling, signatureMatch substring trap, Avalonia 12 trio, GC root, PowerShell ConvertTo-Json, AOT analyzer rules, etc.) into one searchable place. The two READMEs + `CONTRIBUTING.md` are the new user-facing entry points; the seven `docs/*-spec.md` / `*-internals.md` files are the contributor-facing deep dives. Phase Overview row 16 flipped to ✅.

### Phase 5 segment 2d — cross-version sweep + bug surface

Started by correcting a stale framing — `Phase 5 segment 2d` follow-up actually shipped commit `5c59077` on 2026-04-29; STATUS / plan kept describing it as deferred. Then ran the cross-version smoke against four IL2CPP targets covering metadata v24 / v29 / v31 / v39 across Unity 2018.1 → 6.1.

**Headline finding**: the `Il2CppMetadataRegistration` structural-shape AOB scan is **fully version-portable** — found cleanly on every target tested (Unity 2018.1 / 2019.4 / 2021.3 / 2022.3 / 6.1). Heuristic correctness confirmed across 5 Unity major versions and 4 metadata versions.

**Three new findings, all deferred but documented with fix sketches**:

1. **v24 integer-decode bug** (FF VI / Unity 2019.4) — `il2cppGetMetadataTypeFields`'s I*/U* family decoder assumes CLI compressed-int + sentinel encoding (correct for v29+) but v24 metadata stores raw little-endian bytes. `Int32.MaxValue` returns `-2147483648` instead of `2147483647`. Fix: per-version dispatch hooked off `MetadataParser::version()`. ~1 hr.

2. **Unity 2018.x missing IL2CPP exports** (R-Type Dimensions EX / Unity 2018.1) — runtime is missing `il2cpp_image_get_class_count` + `il2cpp_image_get_class` (added between Unity 2018.x and 2019.x). `IL2CPPResolver` bails (`required 16/18, resolved=no`); every runtime RPC returns `"il2cpp runtime not resolved"`. Registration scan still works (proves it's runtime-ABI-sensitive, not metadata-version-sensitive). Fix: demote both exports to OPTIONAL + add `MetadataParser`-table-driven fallback through `il2cpp_class_from_name`. ~2 hr.

3. **v32+ metadata.dat parsing not implemented** (Songs of Silence / Unity 6.1) — `MetadataParser` rejects v39 by design (`version > 31` guard). However the v39 runtime sweep ([`Test-V39RuntimeSweep.ps1`](../tools/Test-V39RuntimeSweep.ps1)) shows **9 / 9 runtime paths green** on v39 — Inspector / class browser / Phase 17/18/19 all work. Only `il2cppGetMetadataTypeFields` const decode + offline `metadata-cli` are gated. So **v39 is effectively first-class supported** for ~95 % of usage; v32+ parser work (2-3 day project per [il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md)) only unlocks the const-decode niche.

Bonus minor findings:
- `findInstances` IL2CPP path returns empty `classPtr` field (matches still come back). Cosmetic backlog item.
- JSON spec doesn't allow `Infinity` / `NaN`; `nlohmann/json` coerces to `null` for special-case floats. R4 decoder runs but value field is null on the 3 special values. Cosmetic.

### Phase 18 multi-overload AA-script user-click verified

Closed the last open Phase 18 verification: Farthest Frontier `ItemBundle.AddItems` (2 overloads — `(System.UInt32, System.UInt32)` + `(ItemBundle)`) — both AOB patterns scan-clean from CE, both `registersymbol` succeed, addresses resolve in `GameAssembly.dll+0xB20900` / `+0xB20980`. Two-symbol `[ENABLE]` block is idempotent.

### New tooling

- **[`tools/Test-MetadataRegistration.ps1`](../tools/Test-MetadataRegistration.ps1)** — diagnostic probe for `il2cppGetMetadataRegistration` in isolation. Useful when `MetadataParser` rejects metadata.dat (e.g. v32+) but you want to verify the registration scan independently.
- **[`tools/Test-V39RuntimeSweep.ps1`](../tools/Test-V39RuntimeSweep.ps1)** — composite "what works on a v32+ target" sweep (9 independent runtime paths, one client connection). Generic enough to use on any IL2CPP target as a smoke.

### Files touched (whole session)

13 docs files (`STATUS.md`, `plan.md`, `test-matrix.md`, `il2cpp-metadata-crosscheck.md`, plus the 9 new docs), 3 root files (`README.md` + `README.zh-TW.md` + `CONTRIBUTING.md`), 2 new smoke scripts. **No code changes to the DLL, UI, or Lua bridges.** The docs / plan / matrix are now ground truth for handing off to a fresh checkout on another machine.

---

## What landed two sessions ago — Phase 19d-2 polish

Two UX improvements on top of the freshly-shipped Phase 19 vertical. UI-only.

- **Auto-skip overloads modal on count==1**. Methods grid right-click "Call method… (pick overload)" probes `findMethodInstances` first; on count==1, jumps straight to `MethodCallWindow`. Multi-overload methods still drop into the picker. Split handler pair in `MainWindow.axaml.cs` (`OnMethodCallMethodClicked` + unchanged `OnMethodFindOverloadsClicked`) + shared `ResolveMethodRow` / `BuildOverloadsVm` helpers.

- **Inspector cross-tab "Call instance method…"**. Right-click any instance in the Inspector → "Call method on this instance… (switch to Methods)" snaps `SelectedInstance` + flips to Methods tab. The Methods-grid right-click then pre-fills `Instance address` in `MethodCallWindow` (scope-checked: only when Browser.SelectedClass matches Inspector.TargetClass on assembly + namespace + name).

Files: [`MainWindow.axaml.cs`](../ui/UnityDumpUI/Views/MainWindow.axaml.cs), [`MainWindow.axaml`](../ui/UnityDumpUI/Views/MainWindow.axaml), [`MethodOverloadsViewModel.cs`](../ui/UnityDumpUI/ViewModels/MethodOverloadsViewModel.cs), [`MethodOverloadsWindow.axaml.cs`](../ui/UnityDumpUI/Views/MethodOverloadsWindow.axaml.cs), [`Resources/Strings/en.axaml`](../ui/UnityDumpUI/Resources/Strings/en.axaml). AOT publish 0/0 warnings; `DiscreteUI.exe` 38.3 MB.

---

## What landed three sessions ago — Phase 19 (full vertical) + UI Windows-only cleanup

### Phase 19 — Lua-callable Unity methods (closed end-to-end)

- **19a — IL2CPP DLL handler (static-only)**. New module pair `dll/src/il2cpp/IL2CPPMethodInvoker.{h,cpp}`. Single-pass overload resolver mirrors Phase 18a's iterator (returns `MethodInfo*` directly, no double-walk). Six new optional resolver bindings: `il2cpp_runtime_invoke`, `il2cpp_object_unbox`, `il2cpp_object_get_class` (load-bearing in 19a), plus `il2cpp_string_new` / `il2cpp_string_chars` / `il2cpp_string_length` (pre-bound for 19c). Whole `runtime_invoke` call wrapped in one SEH leaf so a hard crash in the JIT/AOT entry becomes `error: native_crash_in_runtime_invoke`. Exception decode reads `Il2CppException + 0x18` for `_message` (Mono-derived `System.Exception` layout, stable across Unity 2018+). Verified live on SaGa F2R 2026-05-07.

- **19b — Mono port (static-only)**. New module pair `dll/src/mono/MonoMethodInvoker.{h,cpp}` mirrors the IL2CPP shape exactly — only the dispatcher changes (`mono_runtime_invoke` instead of `il2cpp_runtime_invoke`). Six new optional Mono bindings (note: `mono_string_new` takes a `MonoDomain*` first arg, unlike IL2CPP). Address field of the response populated via `mono_compile_method` (Mono is JIT — no AOT-`methodPointer` shortcut). Wire shape byte-identical to 19a; per-backend error-code suffix only. Verified live on BongoCat 2026-05-07.

- **19c — Instance dispatch + string in/out marshaling**. Lifts the static-only / no-string restrictions on both backends with byte-identical wire-shape changes. **Marshaling fix mid-19c**: initial implementation used the "uniform" convention from Mono docs (params[i] = &slot for both value AND reference types). Parse("42") returned FormatException because the trampoline read &slot as if it were a managed string. Switched to the **non-uniform convention** used by BepInEx / MelonLoader / Il2CppInterop: value types still pass `&slot`, but reference types (Null / String / Address) pass the object pointer **directly** in `params[i]`. The Mono-docs "uniform" convention turns out to be wrong (or at least mismatched with what the actual x64 trampoline expects). String args use `il2cpp_string_new(utf8)` / `mono_string_new(domain, utf8)`; string returns decode UTF-16 → UTF-8 via the existing `DecodeManagedString` helper each invoker carries. Verified live on SaGa F2R + BongoCat + Farthest Frontier (the last via `ItemBundle.get_numberOfItems()` instance call).

- **19d — UI MethodCallWindow**. Right-click any row in the `MethodOverloadsWindow` grid → "Call method…" opens the new `MethodCallWindow` modal seeded from that overload's signature (paramTypes + paramNames threaded into one editor row each). New `CallArgFactory` in `UnityDumpUI.Core` wraps the AOT-safe `JsonSerializer.SerializeToElement` calls per wire `type` tag (Null / Bool / Int / UInt / Float / String / Address) so the source-gen `ProtocolJsonContext` stays internal — VMs in the outer `UnityDumpUI` project build wire payloads through this public seam. Editors v1: single TextBox per param plus a "null" checkbox enabled for reference types (smart-typed editors deferred to 19d-2 polish). Result panel renders typed return value, captured managed exception, or protocol error per the switch-on-`returnType` rule from `lua-invoke.md`. Also added a sibling "Call method… (pick overload)" entry to the main Methods grid context menu so the workflow is discoverable from one click in. AOT publish stays clean (0/0 trim/AOT analyzer warnings).

- **19e — `scripts/unity_invoke.lua` CE Lua bridge**. New standalone script (18 KB, ships to `dist/` via the existing `*.lua` glob). Public API matches the plan's example one-for-one: `callUnityMethod(asm, ns, class, method, args, opts)` where `opts = { instance, signatureMatch }`. Arg builders for every wire type tag (`argNull` / `argBool` / `argByte` / `argSByte` / `argInt16..64` / `argUInt16..64` / `argFloat` / `argDouble` / `argChar` / `argString` / `argAddress`) — keeps callers from hand-typing `{type="int32", value=...}` for every arg. Result helpers: `asAddress(v)` decodes "0x..." returnValue back to a Lua number; `prettyPrint(r)` logs a one-line human summary; `pcallUnityMethod` is the non-throwing variant; `findOverloads(asm, ns, class, method)` wraps `findMethodInstances` so users can list overloads from CE Lua before picking a `signatureMatch`. Standalone (does NOT `require` sibling scripts) — ships its own JSON parser AND its own JSON serializer (`callMethod`'s `args` is a nested array of `{type, value}` objects, which the flat-only `{"k":v}` builders the other scripts use can't express). Header comment carries quick-start + full API + the substring-trap and GC-root caveats so the dofile target is self-documenting. **Verified live on IL2CPP (SaGa F2R) — all 4 scenarios green** (Mathf.Abs(int) → 7, Concat → "Hello, World", Convert.ToByte(-1) → OverflowException, prettyPrint round-trip).

### Windows-only UI cleanup (commits `128e1cb` + `62e8468`)

Dropped `Avalonia.Desktop` metapackage in favor of bare `Avalonia.Win32` + `Avalonia.Skia` + `Avalonia.HarfBuzz` (all three needed; `UsePlatformDetect` used to wire all three behind one call). Linux is not in scope; Avalonia.X11 + Avalonia.FreeDesktop + Tmds.DBus.Protocol all dropped from the AOT graph. ILC dead-code diagnostics gone, `DiscreteUI.exe` 39.0 → 38.0 → 38.1 MB. **Two stumbles** along the way (now both saved to memory): missing `.UseSkia()` (silent black window), then missing `.UseHarfBuzz()` + `Avalonia.HarfBuzz` package (silent process exit with `0xC0000409`, no log file). Both fixed in `62e8468`. Program.cs now reads `.UseWin32().UseSkia().UseHarfBuzz().WithInterFont()`.

### Deploy script fix (commit at HEAD)

`tools/Install-CEScripts.ps1` had a hardcoded `$luaNames` list (4 files) — was missing `unity_addrfind.lua` (Phase 17d!) AND `unity_invoke.lua` (Phase 19e). Switched to `Get-ChildItem -Filter '*.lua'` glob so future scripts ship automatically. Glob now picks up all 6 files in `dist/`.

---

