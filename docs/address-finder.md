# Address Finder — design spec

## Goal

Given an arbitrary memory address (typically obtained from Cheat Engine's
*Find what writes / accesses*, pointer scan, or manual probe), determine
**which Unity managed instance + class + field** that address belongs to.

This is the **inverse** of the existing `findInstances` command:

```
findInstances:  class name        →  list of instance addresses
addressFinder:  arbitrary address →  instance + class + field + offset
```

A typical user story: CE's *Find what writes to this address* surfaces a
mystery write at `0x1F4` of some live object. The user wants to know
"what game data did that just modify?" — Address Finder answers
"`MainGame.PLY_WORK.HP` at offset `0x1F4` from instance `0x1FB9D39D0`."

## Use cases

1. **Reverse-engineer a write watch hit** — CE finds an instruction that writes
   to address X; we resolve X back to a managed field name without manual
   pointer-chain reconstruction.
2. **Pointer-scan triangulation** — CE pointer-scans land on candidate
   `base+offset` chains; rapid lookup of each candidate tells the user which
   ones map to known managed types vs. native engine memory.
3. **Static analysis of a save file / memory dump** — given a dumped object
   region, identify objects without running the game (limited form, see
   *Static-only mode* below).
4. **Sanity check during walks** — when the inspector walks a pointer field,
   call into the address finder to confirm the destination is what the
   metadata claims it is (catches stale / corrupted pointer chains).

## Out of scope (for v1)

- Native (engine-internal C++) objects like `Transform`, `GameObject`,
  `Component` — these live in unmanaged memory and don't carry a vtable
  pointer in the same way. Their managed wrappers do, and v1 will identify
  *those*, not the underlying native struct.
- Boxed value types loaded onto the heap dynamically (rare, but CE could land
  on one mid-write).
- Pointer-arithmetic into a structure's interior — v1 should at least say
  "address is inside instance X at offset Y" even when no field declares
  exactly Y, but mapping to "the array slot at index N" is best-effort.

## Algorithm — Mono / IL2CPP

The shared insight: every Unity managed object on the GC heap has its **first
8 bytes** point at a `MonoVTable*` (Mono) or `Il2CppClass*` (IL2CPP). That
pointer lands inside the runtime's class-table region, which is itself
walkable via `mono_class_*` / `il2cpp_class_*` APIs.

Two complementary strategies, picked by query type:

### Strategy A — backward scan from the queried address

For a single ad-hoc lookup, this is fastest:

1. Round `addr` down to 8-byte boundary, then walk **backwards** in 8-byte
   strides up to a configurable max distance (default 4 KB; covers the
   largest Unity classes in practice).
2. At each candidate position `p`, read 8 bytes as a pointer `klassPtr`.
3. Validate `klassPtr`:
   - Mono: `klassPtr & 7 == 0`, points into `mono-2.0-bdwgc.dll`'s
     class-arena range (cached at startup), and `mono_class_get_name(klassPtr)`
     returns a readable string (SEH-protected).
   - IL2CPP: `klassPtr & 7 == 0`, points into `GameAssembly.dll` class
     range (read at startup via `il2cpp_class_*` enumeration), and
     `il2cpp_class_get_name(klassPtr)` returns readable text.
4. If validated, `p` is the instance base. Compute `fieldOffset = addr - p`.
5. Walk class fields (including inherited) in offset order; pick the field
   whose `[offset, offset+sizeof(type))` contains `fieldOffset`.
6. If the field is a pointer to another class, recurse into the destination
   for nested context (optional, capped to e.g. 3 levels).
7. If the field is a `T[]` or `List<T>`, compute element index from
   `(addr - elements_base) / element_stride`.
8. Return the chain.

**Cost**: O(maxBackwardDistance / 8) class-pointer probes, each 8-byte read +
a name lookup. Bounded ~512 reads worst case, sub-millisecond on x64.

### Strategy B — heap walk + interval tree (cached)

When the user is going to query many addresses (e.g. processing a CE
pointer-scan result), build a snapshot of the GC heap once and binary-search:

1. Mono: walk via `mono_gc_walk_heap` (if available) or scan the GC bitmap
   region; for each live object record `[addr, addr + mono_object_get_size(o))`
   and the class pointer.
2. IL2CPP: similar via il2cpp's GC API; bdwgc on Unity 6 exposes
   `GC_call_with_alloc_lock` style walks.
3. Build an interval tree (sorted vector of `(start, end, klass)` since GC
   intervals don't overlap once the walk completes; binary search is enough).
4. Per-query: binary search for the interval containing `addr`; if found,
   compute offset and resolve field as in Strategy A steps 5-8.

**Cost**: heap walk is O(N) on object count (Silksong: ~hundreds of
thousands). Walltime: a few seconds. Cache lifetime: invalidate on next
GC cycle (best-effort — we don't get a GC notification, so refresh on
demand or after a configurable TTL).

### Decision

v1 ships Strategy A (low risk, no heap walk needed, fast). Strategy B is
a follow-up if the user reports needing batch lookups.

## Pipe protocol

New command: **`whatIsAt`** (registered under both Mono and IL2CPP backends).

### Request

```jsonc
{
  "id": 42,
  "cmd": "whatIsAt",
  "args": {
    "address": "0x1FB9D39D8",
    "maxBackwardScan": 4096,    // optional, default 4096
    "recurseLevels": 2,         // optional, default 2 — pointer-field follow depth
    "includeArrayIndex": true   // optional, default true
  }
}
```

### Response — managed instance found

```jsonc
{
  "id": 42,
  "result": {
    "found": true,
    "instance": "0x1FB9D39D0",        // base of the containing managed object
    "assembly": "Assembly-CSharp",
    "namespace": "MainGame",
    "class": "PLY_WORK",
    "fieldOffset": 8,                  // bytes from instance base
    "field": {
      "name": "HP",
      "type": "System.Int32",
      "offset": 8,
      "size": 4,
      "isStatic": false,
      "kind": "leaf"                  // leaf | pointer-to-class | array-slot | array-element
    },
    "containingArray": null,           // populated if address is inside a T[] / List<T>
    "trace": [                         // optional pointer chain when recurseLevels > 0
      { "instance": "0x1FB9D39D0", "class": "MainGame.PLY_WORK", "field": "HP" }
    ]
  }
}
```

### Response — not a managed instance

```jsonc
{
  "id": 42,
  "result": {
    "found": false,
    "reason": "no_klass_in_range",    // one of:
                                      //   no_klass_in_range — no plausible vtable in 4 KB scan
                                      //   klass_validation_failed — pointer looked plausible but failed isValid()
                                      //   address_unreadable — SEH triggered, address probably bad
                                      //   field_offset_unmapped — instance found but offset not in any declared field
    "scannedRange": {
      "from": "0x1FB9D29D8",
      "to":   "0x1FB9D39D8"
    }
  }
}
```

### Response — bare offset, no field declared

If we land inside an instance but at an offset that doesn't match any
declared field (e.g. inside compiler padding or an unmanaged blob region):

```jsonc
{
  "id": 42,
  "result": {
    "found": true,
    "instance": "0x1FB9D39D0",
    "class": "MainGame.PLY_WORK",
    "fieldOffset": 247,
    "field": null,                     // no exact match
    "nearestField": {                  // closest declared field below the offset
      "name": "name",
      "type": "System.String",
      "offset": 240,
      "size": 8,
      "delta": 7
    }
  }
}
```

## UI surface

### Inspector tab right-click action

Right-click on any walked field row → "**Find what's at this address**" menu
item. Pre-fills the field's effective address into the lookup input.

### Dedicated *Address Lookup* tab

New tab next to *Fields / Inspector / Methods*. Single input + output
panel:

```
┌─ Address Lookup ────────────────────────────────────────────────┐
│  Address:  0x1FB9D39D8         [Lookup]                          │
│                                                                  │
│  Instance:  0x1FB9D39D0                                          │
│  Class:     MainGame.PLY_WORK                                    │
│  Field:     HP @ offset 0x08 (System.Int32, 4 bytes)             │
│                                                                  │
│  [Inspect this instance]  [Walk array]  [Copy CE XML]            │
└──────────────────────────────────────────────────────────────────┘
```

Buttons:
- **Inspect this instance** → switches to the *Inspector* tab with the
  identified class loaded and `instance` as the focus address.
- **Copy CE XML** → emits a single-field CE XML rooted at the field address.
- **Walk array** (only when result is an array slot) → swaps to array view.

### CE Lua bridge

```lua
dofile([[<CE>\discrete\unity_addrfind.lua]])
local hit = whatIsAt(0x1FB9D39D8)
if hit and hit.found and hit.field then
    print(string.format("%s.%s @ %X", hit.class, hit.field.name, hit.instance))
end
```

The Lua wrapper sits alongside `unity_dissect.lua` / `unity_symbols.lua`,
shares the same RPC + retry scaffolding, and registers a CE hotkey so the
user can highlight an address in CE's memory view and trigger lookup
without retyping.

## Implementation phases

| Sub-phase | Scope | Effort |
|-----------|-------|--------|
| 17a | DLL: `whatIsAt` handler with Strategy A backward scan; class-pointer range cache built at handshake | 1 day |
| 17b | DLL: field-offset resolution for value types, pointer-to-class, arrays, strings | 1 day |
| 17c | UI: dedicated *Address Lookup* tab + Inspector right-click action | 1 day |
| 17d | CE Lua bridge: `unity_addrfind.lua` + optional CE hotkey integration | 0.5 day |
| 17e | Strategy B (cached heap walk) — only if a user reports needing batch lookups | deferred |

Estimated total: ~3.5 days for v1.

## Open questions

1. **Class-pointer validation cost** — Mono / IL2CPP have no public "is this
   a valid class pointer?" API; we infer by reading the name and sanity-
   checking. SEH-protected. Need to benchmark whether the per-probe overhead
   is acceptable for a 512-iteration backward scan.

2. **Inheritance walk** — when the offset matches a parent-class field, the
   response should report the **declaring** class (e.g.
   `UnityEngine.MonoBehaviour.useGUILayout`), not just the most-derived one.
   `walkInstance` already handles this; reuse the same field iteration.

3. **Static fields region** — static field blocks are allocated separately
   from the GC heap. v1 doesn't try to identify those; output should be
   `found: false, reason: "no_klass_in_range"`. v1.1 could add a separate
   "is this in a known static-field block?" check by tracking the static
   blocks for every class as they get queried.

4. **Cross-bitness** — Mono 32-bit support (Phase 12d) intersects with this
   feature. The class-pointer range cache should be uintptr_t-typed; the
   x86 build needs separate testing.

5. **Performance under stress** — what if the user pastes 10 000 addresses
   from a pointer-scan result? Strategy A at 1 ms each = 10 s, acceptable.
   Above that, Strategy B becomes worth implementing.

## Acceptance criteria

- [x] Given the address of a known live `PLY_WORK.HP` field on SaGa F2R,
      `whatIsAt` returns instance + class + field name + offset, all
      matching DiscreteUI's `findInstances` + `walkInstance` output.
      *Verified live in 17b smoke test: `chrNo / objNoF / objNoM / purse /
      hpNow / lpMax / artPos` all resolve field-for-field against the UI
      screenshot via `Test-WhatIsAt.ps1 -Name 'MainGame/PLY_WORK'
      -InstanceAddress 0x000001E55B2518F0`.*
- [x] Given a value-type field's interior (mid-storage offset), `whatIsAt`
      reports the field with `kind: "interior"` and `interiorOffset` set
      to the byte offset within the field. *17b kind classifier:
      "leaf" / "value-struct" / "pointer" / "interior".*
- [x] Given an address inside a parent-class field, `whatIsAt` reports
      the declaring class via `field.declaredIn`, not just the most-
      derived one. *17b parent-chain walk emits `declaredIn` per field.*
- [x] Given a known-bad address (e.g. `0xDEADBEEF`), `whatIsAt` returns
      `found: false, reason: "no_klass_in_range"` after the configured
      scan window without crashing the host. *Verified live in
      `Test-WhatIsAt.ps1`'s negative-path probe.*
- [x] CE main thread stays responsive during a backward scan. *17a SEH-
      leaf primitives + per-RPC `processMessages()` + `sleep(1)` yield
      in `unity_addrfind.lua` (mirrors the dissect / symbols scripts).*

**Phase 18 closed** — all four sub-phases shipped:
17a DLL handler + class-pointer cache (build #188);
17b field-offset resolution + kind classifier (build #194);
17c UI Address Lookup tab + Inspector right-click handoff (build #197);
17d CE Lua bridge `unity_addrfind.lua` (build #198).
