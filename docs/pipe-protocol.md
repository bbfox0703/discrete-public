# Discrete pipe protocol

The injected DLL exposes a named-pipe JSON-RPC surface for inspecting the host
Unity process. This document is the contract between the DLL and any UI /
script that talks to it.

## Connection

| Property | Value |
| --- | --- |
| Pipe name | `\\.\pipe\DiscreteBfx` |
| Direction | duplex (`InOut`) |
| Framing | line-delimited UTF-8 JSON; each request and each response is one line terminated with `\n` |
| Concurrency | one client at a time; the server queues additional connect attempts |
| Lifetime | created by `Discrete_StartPipe` after `Discrete_Init`; torn down by `Discrete_StopPipe` or DLL unload |

Open from PowerShell:

```powershell
$client = New-Object System.IO.Pipes.NamedPipeClientStream(
    '.', 'DiscreteBfx', 'InOut')
$client.Connect(5000)
$writer = New-Object System.IO.StreamWriter($client); $writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)
```

## Envelope

### Request

```jsonc
{
  "id": <any>,        // echoed back; correlate request/response. May be omitted.
  "cmd": "<string>",  // required.
  "args": { ... }     // command-specific. Empty object if omitted.
}
```

### Response

Successful dispatch:

```jsonc
{
  "id": <echoed>,
  "result": { ... }   // command-specific payload.
}
```

Dispatcher-level failure:

```jsonc
{
  "id": <echoed-or-null>,
  "error": { "code": <int>, "message": "<string>" }
}
```

Error codes raised by the dispatcher itself:

| Code | Reason |
| --- | --- |
| `-32700` | request line was not valid JSON |
| `-32600` | request had no `cmd` field |
| `-32601` | `cmd` not registered (unknown command) |
| `-32000` | handler threw a C++ exception (message is `what()`) |
| `-32603` | internal: response failed to serialize (rare; should not happen with `error_handler_t::replace`) |

**Handler-level errors** are different — most handlers report soft failures
inside `result` itself rather than as an `error` envelope. The convention is:

```jsonc
{ "id": 1, "result": { "error": "class not found: Foo.Bar" } }
```

When you get `result.error`, the rest of the result object may carry partial
context (e.g. `step`, `address`, `trace` for `resolvePointer`).

## Common argument conventions

### Addresses

Anywhere an `address`, `base`, or similar pointer-sized integer is accepted,
the DLL will parse:

* hex string with or without `0x` prefix — `"0x7FF6CAFEBABE"` / `"7FF6CAFEBABE"`
* unsigned integer — `140711840456894`
* signed integer — same shape, sign respected

Returned addresses are always uppercase 16-digit hex strings, e.g.
`"0x00007FF6CAFEBABE"`.

### Class specs

Many commands resolve a runtime class by `assembly` + `namespace` + `name`:

```jsonc
{ "assembly": "Assembly-CSharp", "namespace": "", "name": "ItemMenu" }
```

`namespace` is the empty string for top-level types. `assembly` is the
short name (no `.dll` suffix), e.g. `"UnityEngine.CoreModule"` or
`"mscorlib"`.

### Backend tagging

Each command below is annotated with the backend that exposes it:

* **Both** — registered in the backend-agnostic block
* **Mono** — registered only when the Mono variant DLL is loaded
* **IL2CPP** — registered only when the IL2CPP variant DLL is loaded

The `handshake` `dll_variant` field tells you which variant you're talking to.

---

## Connection commands

### `ping` *(Both)*

Round-trip liveness check.

```jsonc
// request
{ "id": 1, "cmd": "ping" }
// response
{ "id": 1, "result": { "pong": true } }
```

### `handshake` *(Both)*

Identifies the runtime, the DLL build, and the backend it's compiled for.

```jsonc
{ "cmd": "handshake" }
// result:
{
  "engine": "Unity",
  "backend": "IL2CPP",            // detected from the host process
  "dll_variant": "IL2CPP",        // which DLL variant is loaded
  "game_module": "Game.exe",
  "unity_version": "2021.3.39f1",
  "metadata_version": 29,
  "assemblies_count": 57,
  "build": 44,
  "protocol": 1
}
```

### `scan` *(Both)*

Same shape as `handshake`. Re-runs backend detection — useful after a domain
reload (Mono) or assembly load.

---

## Memory commands

### `readMemory` *(Both)*

Read raw bytes from the host process.

| Arg | Type | Notes |
| --- | --- | --- |
| `address` | address | required |
| `size` | uint | required, `[1, 1048576]` |

```jsonc
// result:
{
  "address": "0x...", "size": 16,
  "hex": "DEADBEEF..."   // uppercase, 2 chars per byte, no separators
}
```

Soft errors: `result.error == "memory not readable"` (region uncommitted /
PAGE_NOACCESS / past PAGE_GUARD).

### `writeMemory` *(Both)*

Write raw bytes. Caller is responsible for not corrupting the runtime.

| Arg | Type | Notes |
| --- | --- | --- |
| `address` | address | required |
| `hex` | string | required; spaces / commas / newlines stripped before parse; even nibble count |

```jsonc
// result:
{ "address": "0x...", "written": 4 }
```

Soft errors: `result.error == "memory not writable"`.

### `resolvePointer` *(Both)*

Cheat Engine pointer-chain semantics: every offset except the last is
dereferenced; the last is added but not.

| Arg | Type | Notes |
| --- | --- | --- |
| `base` | address | required |
| `offsets` | int64[] | optional; signed offsets |
| `size` | uint | optional; if present and ≤ 4096, the final address is read and `hex` is included |

```jsonc
// result:
{
  "final_address": "0x...",
  "trace": ["0x...", "0x...", "0x..."],   // base, then post-each-step
  "size": 8, "hex": "0102030405060708"     // only if size given and read OK
}
```

Soft error mid-chain: `result.error == "deref failed at step"` plus
`step`, `address`, and `trace` so far.

---

## Symbol registry *(Both)*

A simple in-DLL named-pointer map. CRUD via a single command (action chooses
the verb) plus two convenience reads.

### `registerSymbol`

| Arg | Type | When |
| --- | --- | --- |
| `name` | string | always required |
| `action` | `"set"` (default) / `"get"` / `"delete"` | optional |
| `address` | address | required for `"set"` |

```jsonc
// set
{ "cmd": "registerSymbol",
  "args": { "name": "world_root", "address": "0x1F8A0000" } }
// → { "name": "world_root", "address": "0x...", "set": true }

// get
{ "cmd": "registerSymbol",
  "args": { "name": "world_root", "action": "get" } }
// → { "name": "world_root", "address": "0x..." }

// delete
{ "cmd": "registerSymbol",
  "args": { "name": "world_root", "action": "delete" } }
// → { "name": "world_root", "deleted": true }
```

### `lookupSymbol`

```jsonc
{ "cmd": "lookupSymbol", "args": { "name": "world_root" } }
// → { "name": "...", "address": "0x..." }
```

### `listSymbols`

```jsonc
{ "cmd": "listSymbols" }
// → { "count": 2, "symbols": [ {"name":"...","address":"0x..."}, ... ] }
```

The map is in-memory; it does not survive DLL unload.

---

## Assembly / class introspection

### `getAssemblies` *(Both)*

```jsonc
// → { "count": 57,
//      "assemblies": [{ "name": "Assembly-CSharp", "path": "..." }, ...] }
```

### `getClasses` *(Both)*

| Arg | Type | Notes |
| --- | --- | --- |
| `assembly` | string | required |

```jsonc
// IL2CPP result entry: { "namespace": "...", "name": "..." }
// Mono result entry  : { "namespace": "...", "name": "...", "token": <uint> }
```

### `getClassInfo` *(Both)*

Args: class spec.

```jsonc
{
  "namespace": "UnityEngine", "name": "Transform",
  "parent": { "namespace": "UnityEngine", "name": "Component" },
  "fields":  [{ "name": "m_CachedPtr", "type": "System.IntPtr",
                "offset": 16, "flags": 0, "static": false }, ...],
  "methods": [{ "name": "GetChild", "token": <uint>, "flags": <uint>,
                "static": false,
                "address": "0x..."  // IL2CPP only — AOT entry point
              }, ...]
}
```

`flags` is the raw CLR `FieldAttributes` / `MethodAttributes` bitfield.
The `static` boolean is `(flags & 0x10) != 0` for fields.

### `getFieldInfo` *(Both)*

Args: class spec + `"field": "<name>"`. Same per-field shape as `getClassInfo`.

### `getMethodInfo` *(Both)*

Args: class spec + `"method": "<name>"`. Returns `{ "matches": [...] }` since
overloads share a name. Same per-method shape as `getClassInfo`.

### `getStaticFieldData` *(Both)*

Args: class spec + `"field": "<name>"`. Returns the in-process address of the
static field's storage so callers can `readMemory` it directly.

```jsonc
// → { "field": "...", "type": "...", "offset": <int>, "flags": <uint>,
//      "address": "0x..." }
```

### `monoCompileMethod` *(Mono)*

Args: class spec + `"method": "<name>"`. JIT-compiles the named method (or
the first overload) and returns the native entry-point.

```jsonc
// → { "method": "...", "token": <uint>, "address": "0x..." }
```

### `monoGetVTable` *(Mono)*

Args: class spec. Returns the `MonoVTable*` for the class in the root
domain — the same value `findInstances` searches for.

```jsonc
// → { "vtable": "0x..." }
```

---

## Instance discovery & walking

### `findInstances` *(Both)*

Heap-scans every committed RW page for the class header pointer. In IL2CPP
that's the `Il2CppClass*` (the first qword of every object); in Mono it's the
`MonoVTable*`. Aligned to 8 bytes.

| Arg | Type | Notes |
| --- | --- | --- |
| `assembly`, `namespace`, `name` | class spec | required |
| `limit` | uint | optional, default 256, capped at 4096. **Bounds the RAW scan** — filtering (below) happens after, so a filtered query can return far fewer. Raise `limit` if the target may sit past the first `limit` raw hits. |
| `validate` | bool | optional, **default true**. Re-read the candidate's first qword; keep only if it still equals the class header (`MonoVTable*` / `Il2CppClass*`). Drops transient stack/register-spill slots. |
| `aliveOnly` | bool | optional, default false. For `UnityEngine.Object`-derived classes: require `m_CachedPtr` (at `+cachedPtrOffset`) `!= 0`. Drops destroyed Unity shells. |
| `cachedPtrOffset` | uint | optional, default = object header size (`0x10` on x64, `0x8` on x86). The `m_CachedPtr` offset used by `aliveOnly`. |
| `sane` | bool | optional, default false. Read this class's **managed reference fields** (non-static, non-value-type — from class metadata) and require each to be null OR point to readable committed memory. Drops stale/freed/torn slots that hold garbage (e.g. `0x8800D300C75AB77B`) in a reference field. Generic, no per-class config. Response reports `filter.sane_dropped` + `filter.sane_ref_fields` (0 ⇒ metadata unavailable, sane skipped). Value types and `IntPtr` (native pointers like `m_CachedPtr`) are NOT checked. |
| `fieldFilter` | array | optional. AND-combined predicates `{offset,type,op,value}` evaluated per candidate (see below). The **generic targeting engine** — the caller supplies the per-class predicate; nothing is class- or game-specific. |
| `liveOnly` | bool | optional, default false. **Mono only.** Enumerate via `mono_gc_walk_heap` (live objects only) instead of the raw scan. ⚠️ **`mono_gc_walk_heap` is a no-op stub on Unity's bdwgc/Boehm builds** (verified on Core Keeper: 0 objects) — the functional bdwgc path is the unity-liveness API (deferred). When the walk yields nothing, `liveOnly` falls back to the raw scan and sets `filter.live_walk_unsupported`; `filter.live_walk` reports whether the walk actually drove the result. |

**`fieldFilter` predicate** — each entry: `{ "offset": <uint>, "type": <managed type>, "op": <op>, "value": <operand> }`
- `type`: numeric (`System.Int32`/`UInt32`/`Int64`/`UInt64`/`Int16`/`UInt16`/`Byte`/`SByte`/`Single`/`Double`/`Boolean`/`Char`), pointer (`ptr`/`System.IntPtr`/`System.UIntPtr`/`ref`/`object`), or `string`/`System.String`.
- `op`: numeric → `==`,`!=`,`<`,`<=`,`>`,`>=`,`nonzero`(default),`zero`; pointer → `nonnull`(default),`null`,`==`,`!=`; string → `nonempty`,`empty`,`==`,`!=`,`contains`.
- `value`: numeric/bool for numeric ops; hex string or number for pointer `==`/`!=`; string for string ops. Omit for `nonzero`/`nonnull`/`zero`/`null`/`empty`/`nonempty`.
- Example — isolate Core Keeper's local player: `"fieldFilter":[{"offset":1532,"type":"System.Int32","op":"==","value":1}]` (`playerIndex==1`, offset `0x5FC`). The same engine works for any game by supplying a different predicate.

```jsonc
// IL2CPP result:
{
  "class_ptr": "0x...",         // Il2CppClass* — and also the searched-for first-qword pointer
  "count": 100, "limit": 256,
  "matches": ["0x...", "0x...", ...],
  "filter": { "applied": true, "raw_count": 100, "validate_dropped": 2,
              "alive_dropped": 0, "field_dropped": 0, "kept": 98 }
}
// Mono result:
{
  "class_ptr": "0x...",         // MonoClass* — class identity (added 2026-05-08 for cross-backend uniformity)
  "vtable": "0x...",            // MonoVTable* — the first-qword pointer the heap scan matched on
  "count": 100, "limit": 256,
  "matches": ["0x...", "0x...", ...],
  "filter": { "applied": true, "raw_count": 100, "validate_dropped": 0,
              "alive_dropped": 5, "field_dropped": 76, "kept": 1,
              "live_walk": false }   // live_walk=true when mono_gc_walk_heap was used
}
```

> **Reducing false positives — which lever?** Three fake profiles, three tools:
> 1. *Transient stack/register slots* → `validate` (on by default).
> 2. *Destroyed Unity shells* → `aliveOnly` (m_CachedPtr); or Mono `liveOnly`.
> 3. *Dead/uncollected objects + runtime bookkeeping* → Mono `liveOnly` (`mono_gc_walk_heap`); IL2CPP `excludeArena` (default on).
> 3b. *Stale / freed / torn slots* (valid header, but reference fields hold garbage) → `sane` (reference-field pointer validation). Generic, both backends.
> 4. *Live but semantically-uninteresting siblings* (pooled / template / prefab instances of the same class — e.g. Core Keeper's ~80 PlayerControllers) → **only `fieldFilter`** can separate these, because they are genuinely live, valid objects. Liveness/arena filters do not remove them.

**Both backends now return `class_ptr`** so cross-backend consumers can
read one canonical field. **Mono's heap scan target is `vtable`** (the
MonoObject header's first qword is MonoVTable*, not MonoClass*) — if a
client validates instances by comparing `*(addr) == header`, use
`vtable` on Mono and `class_ptr` on IL2CPP.

⚠️ **False positives are common, especially for hot Unity classes.**
Heap scan is "first qword equals target" across all RW pages — it picks
up:
- **Thread stacks** (stale local variables of the matching pointer
  type — addresses look like `0x67_xxxx`, `0xB5_xxxx`, `0xF5_xxxx`).
- **IL2CPP metadata structures** (RGCTX entries, `Il2CppType` records,
  vtable pieces — these legitimately store class pointers at
  offset 0). Even Phase 17 `whatIsAt` can be fooled.

For consumers that intend to invoke `runtime_invoke` on the matches:
pre-filter with a class-pointer validation memory read AND a layout
sanity check (e.g. for `UnityEngine.Object` derivatives, validate
`m_CachedPtr` at +0x10 is a plausible native pointer). For
`UnityEngine.*`-derived classes, **prefer Unity's own enumeration
APIs** (e.g. `Object.FindObjectsOfType(Type, bool)` if not stripped) —
zero false positives. See
`docs/lessons-learned.md` "findInstances heap scan picks up
thread-stack false positives — validate before invoking".

### `walkInstance` *(Both)*

Given an instance start address + class spec, walk the inheritance chain and
decode every managed instance field. Static and literal fields are skipped.

| Arg | Type | Notes |
| --- | --- | --- |
| `address` | address | required; instance start (first qword is the class header) |
| `assembly`, `namespace`, `name` | class spec | required |

```jsonc
// → {
//   "address": "0x...", "namespace": "...", "name": "...",
//   "depth": 4,                 // chain length (self + ancestors), capped at 16
//   "count": 12,                // total fields decoded, capped at 4096
//   "fields": [
//     { "name": "x", "type": "System.Single", "offset": 16,
//       "from": "Vec3", "address": "0x...",
//       "value": 1.5 },
//     { "name": "label", "type": "System.String", "offset": 32,
//       "from": "Item", "address": "0x...",
//       "ptr": "0x...", "string": "Sword", "string_length": 5 },
//     { "name": "owner", "type": "Player", "offset": 40,
//       "from": "Item", "address": "0x...",
//       "ptr": "0x..." },                        // unknown reference type
//     ...
//   ]
// }
```

Per-field decoding rules:

| `type` | Output keys |
| --- | --- |
| `System.Boolean` | `value` (bool) |
| `System.Byte` / `System.SByte` | `value` (int) |
| `System.Int16` / `System.UInt16` / `System.Char` | `value` (int) |
| `System.Int32` / `System.UInt32` | `value` (int) |
| `System.Int64` / `System.UInt64` | `value` (int) |
| `System.Single` / `System.Double` | `value` (number) |
| `System.IntPtr` / `System.UIntPtr` | `ptr` (hex string) |
| `System.String` | `ptr` + (`string` + `string_length`) on success, or `string_error` |
| anything else | `ptr` (8-byte qword as hex) |

If the address is unreadable, the decoded entry carries `"error":
"unreadable"` instead of a value.

The string layout (`length` at `+0x10`, UTF-16 chars at `+0x14`) is identical
between Mono-bdwgc and IL2CPP, so the same decoder serves both backends.

### `setInstanceField` *(Both)*

Phase 21a — **inverse of `walkInstance`'s per-field read**. Given an instance
address + cumulative field offset + a wire-type tag + a JSON-typed value,
writes `sizeof(type)` bytes at `instance + fieldOffset` via SEH-protected
`SafeWriteBytes`. Backend-agnostic (just process memory write — no Mono /
IL2CPP API calls; the caller already knows the field offset from a prior
`walkInstance` response).

v1 scope: primitives (bool, signed + unsigned 8/16/32/64, float, double, char)
plus raw address. Reference-type writes (`System.String`, object pointer)
are out of scope — they need `il2cpp_string_new` / `mono_string_new` plus
proper GC-root marshaling, deferred to v2.

| Arg | Type | Notes |
| --- | --- | --- |
| `address` | address | required; instance start (first qword is the class header) |
| `fieldOffset` | int | required; cumulative bytes from `address` to the field storage (0 if `address` already points at the field). Must be in `[0, 16_777_216]`. |
| `type` | string | required; one of `bool` / `int8` / `uint8` / `int16` / `uint16` / `int32` / `uint32` / `int64` / `uint64` / `float` / `double` / `char` / `address`. CLR names (`System.Int32`, `System.Boolean`, …) also accepted. |
| `value` | typed | required; JSON number for numeric types, `true`/`false` (or 0/1) for `bool`, `0x...` hex string for `address`, single-char string or numeric code point for `char`. |

```jsonc
// → {
//   "ok":           true,                 // false when memory not writable / value out of range
//   "address":      "0x...",              // echoed instance address
//   "fieldOffset":  32,                   // echoed
//   "fieldAddress": "0x...",              // computed instance + offset
//   "type":         "int32",              // echoed
//   "written":      4                     // bytes actually written
// }
```

Soft errors (memory unwritable, value out of range for type, unsupported
type tag) land in the result envelope as `error: "..."` with `ok: false`
and (for unwritable memory) the echoed `address` / `fieldOffset` /
`fieldAddress` / `type` / `size` fields so the caller can show the rejected
write verbatim. For an unsupported type tag the result also carries a `hint`
pointing at the deferred-to-v2 reference-type story.

**Caveats**:
- The same heap-scan false positives as `findInstances` / `walkInstance`
  apply: writing to an address that *looks* like the right instance but is
  actually thread-stack residue or a metadata structure will overwrite
  unrelated memory. Validate the instance with a prior `walkInstance` /
  `whatIsAt` round-trip before writing if correctness matters.
- For value-type fields (`int`, `float`, `Vector3.x`), the offset is stable
  between reads and writes. For reference-type fields (deferred), the
  pointer at `instance + offset` may shift between the read and the
  user's Apply click — chain reads + writes inside one request when
  correctness matters.
- Boolean writes emit exactly one byte (`0x00` or `0x01`). The CLR
  semantically treats any non-zero byte as `true`, so a write of `1` and a
  prior value of `2` will both read back as `true` after the write.
- Float writes go through `double → float` narrowing; values outside
  `[FloatMin, FloatMax]` clamp to ±Infinity rather than failing.

### `listFieldValuesAcrossInstances` *(Both)*

Phase 21a — **row-wise inverse of `walkInstance`**. Where `walkInstance` is
"one instance, all fields", this is "all instances, one field". Heap-scans
for every live instance of the class (same scan as `findInstances`), then
decodes the field at `instance + fieldOffset` for each match.

Useful when chasing per-entity state across a class — e.g. "what `chrNo` does
every live `PARTY_WORK` instance have right now?", "which `Inventory`
instance has its `gold` set to 9999?".

| Arg | Type | Notes |
| --- | --- | --- |
| `assembly`, `namespace`, `name` | class spec | required |
| `fieldOffset` | int | required; **cumulative** byte offset from instance base (the UI walks the Inspector node's parent chain and sums offsets across value-struct ancestors) |
| `fieldType` | string | optional managed CLR type, e.g. `"System.Int32"`; selects the decoder. Unknown / unsupplied → raw 8-byte qword `ptr`. |
| `limit` | uint | optional, default 256, capped at 4096 |

```jsonc
// IL2CPP result:
{
  "class_ptr":   "0x0000000123456789",
  "count":       2,
  "limit":       256,
  "fieldOffset": 32,
  "fieldType":   "System.Int32",
  "rows": [
    { "instance": "0x...", "fieldAddress": "0x...", "value": 5 },
    { "instance": "0x...", "fieldAddress": "0x...", "value": 42 }
  ]
}
// Mono result is identical except an additional "vtable" field is emitted
// at the top level (same Mono-only quirk as findInstances).
```

Per-row decoding uses the same per-type rules as `walkInstance` —
primitives → `value`, `System.String` → `ptr` + `string` + `string_length`
(or `string_error`), everything else → raw 8-byte qword `ptr`. Unreadable
field memory for an instance lands on `row.error = "unreadable"` so the
caller can render a placeholder for that row without losing the other
matches.

**Caveats**:
- Caller is responsible for the *cumulative* offset. The DLL doesn't walk
  metadata to recompute it — it trusts the supplied offset. The UI's
  `InspectorFieldOffsetResolver` does the walk client-side; it bails out
  when the chain crosses any non-value-struct ancestor (pointer field,
  collection element) because those would need a per-row dereference /
  per-row index resolution at lookup time.
- The same heap-scan false positives as `findInstances` apply — thread
  stacks and metadata structures can match the class pointer pattern. Rows
  whose `fieldAddress` lands on unreadable memory at least flag themselves
  via `error: "unreadable"`, but a false-positive instance whose
  `instance + fieldOffset` happens to be readable will return a garbage
  value. See `findInstances` for the validation strategy.

### `searchSymbols` *(Both)*

Phase 25 — **cross-class field + method finder**. Walks every class in
the named assembly, iterates each class's fields + methods via the same
`EnumerateClasses` × `ReadClassInfo` path that drives `getClassInfo`,
and returns rows whose name contains the supplied ASCII case-insensitive
substring. Replaces the historical "click each class in turn" loop the
user reported when hunting a property like `MaxHP` across an assembly
with several-hundred classes.

| Arg | Type | Notes |
| --- | --- | --- |
| `assembly` | string | required; same name string `getClasses` accepts (e.g. `"Assembly-CSharp"`, `"UnityEngine.CoreModule"`). |
| `pattern`  | string | required; non-empty ASCII case-insensitive substring (`'A'`-`'Z'` fold to `'a'`-`'z'`; non-ASCII bytes compare as-is). |
| `kinds`    | string[] | optional; subset of `["field", "method"]`. Default = both. At least one must be present; passing `[]` errors. |
| `limit`    | int    | optional; cap on returned rows. Default 200, hard cap 5000. |

```jsonc
// → {
//   "matches": [
//     {
//       "namespace": "Game",
//       "class":     "Player",
//       "name":      "MaxHP",
//       "kind":      "field",
//       "type":      "System.Int32",   // declared type (field kind only)
//       "offset":    64,               // bytes from instance base
//       "static":    false
//     },
//     {
//       "namespace": "",
//       "class":     "PlayerStats",
//       "name":      "get_MaxHP",
//       "kind":      "method",
//       "token":     100663552,        // metadata token (Mono + IL2CPP)
//       "static":    false,
//       "address":   "0x00007FF876123450"  // IL2CPP only — Mono omits this
//     }
//   ],
//   "scanned_classes": 153,            // distinct classes walked
//   "truncated":       false           // true when capped by `limit`
// }
```

**Backend differences (small)**:

- **IL2CPP method rows carry `address`** (the AOT `methodPointer`).
- **Mono method rows omit `address`** — calling `mono_compile_method`
  per method during a bulk scan would JIT every overload of every
  matching class, which is far too heavy. The user drills into a
  single overload via the Methods picker's "Find overloads" entry
  point, which JITs only that one method.
- **`kind` is always one of `"field"` / `"method"`** — properties
  in C# are surfaced via their `get_*` / `set_*` accessor methods
  (Mono / IL2CPP runtime convention); a `MaxHP` property hits as
  three rows: field `MaxHP` (if there's a backing field), method
  `get_MaxHP`, method `set_MaxHP`.

**Caveats**:

- The pattern is **substring**, not prefix or regex. Searching for
  `"set"` will match every setter in the assembly; narrow with
  prefix (`"set_MaxHP"`) or use the kind filter when the result set
  bloats.
- `truncated: true` means at least one matching row was *not* emitted
  — the user should narrow the pattern or raise the `limit`. The
  truncated set is order-of-iteration (whichever class the scan
  reached first), not "best matches first".
- `ReadClassInfo` walks declared members only — inherited fields /
  methods from parent classes don't surface here. Use `getClassInfo`
  with `include_inherited` for that path.
- The `format` of `address` matches `findMethodInstances` —
  `0x` prefix + uppercase hex, never zero-padded — so the UI can
  hand it to `CreateAAScript` / `whatIsAt` verbatim.

### `findFieldReferences` *(Both — backend-symmetric wire shape)*

Phase 28 — **inverse of [`searchSymbols`](#searchsymbols-both)**. Given a
single Unity field, returns every method that reads or writes it.

* **Mono** walks the IL bytecode of every method declared in the target
  assembly via `mono_method_get_header` + `mono_method_header_get_code`,
  matching `ldfld`(0x7B) / `ldflda`(0x7C) / `stfld`(0x7D) / `ldsfld`(0x7E)
  / `ldsflda`(0x7F) / `stsfld`(0x80) operand tokens against the target
  field's `mono_class_get_field_token`. Exact, no false positives.
* **IL2CPP static** fields: resolve the field's static-storage absolute
  address via `il2cpp_class_get_static_data(klass) + field.offset`, scan
  `GameAssembly.dll`'s `.text` for `mov reg, [rip+disp32]` /
  `lea reg, [rip+disp32]` / `mov [rip+disp32], reg` instructions whose
  absolute target matches (+ a class-pointer chain detector + lazy-init slot
  scanner fallback for runtime-heap `static_fields`), then attribute each hit
  to its enclosing method via binary search against a sorted method-pointer
  index. `confidence: "exact"`.
* **IL2CPP instance** fields: decode the **declaring class's own
  methods** for `[gpReg + fieldOffset]` accesses (base ≠ `rip`/`rsp`/`rbp`,
  no index, `disp == field.offset`), classified read/write by the memory
  operand's action (`lea` → read). `confidence: "heuristic"` — the
  displacement match is not taint-checked against `this`, and the field
  header carries **no** `staticAddress` (instance fields have no single
  storage address). Method end is the next method's AOT entry, clamped to
  start+8 KB / the section end, terminated at `int3` padding.

**Scope (v1)**: same-assembly references only — cross-assembly access
uses MemberRef tokens (Mono) / methods in other images (IL2CPP), both
deferred to v2 if a consumer asks. IL2CPP instance scan is additionally
scoped to the **declaring class** (references from other classes / inlined
accessors are missed; prefer Mono for exactness).

| Arg | Type | Notes |
| --- | --- | --- |
| `assembly`  | string | required; declaring assembly name (e.g. `"Assembly-CSharp"`). |
| `ns`        | string | optional, `""` allowed for top-level types. |
| `class`     | string | required; class simple name. Use `"Outer/Inner"` for nested types. |
| `fieldName` | string | required; field simple name. |
| `kind`      | string | optional; `"any"` (default) / `"read"` / `"write"`. |
| `limit`     | int    | optional; cap on returned rows. Default 200, hard cap 5000. |

```jsonc
// Mono response shape (instance field PLY_WORK.HP)
// → {
//   "ok": true,
//   "field": {
//     "name":     "HP",
//     "type":     "System.Int32",
//     "offset":   16,
//     "isStatic": false,
//     "token":    67108865         // 0x04000001 — Mono-only
//   },
//   "references": [
//     {
//       "assembly":      "Assembly-CSharp",
//       "namespace":     "MainGame",
//       "class":         "PLY_WORK",
//       "method":        "TakeDamage",
//       "methodToken":   100663553,
//       "methodFlags":   134,
//       "isMethodStatic": false,
//       "isStatic":      false,       // mirrors field.isStatic for per-row convenience
//       "kind":          "read",      // "read" / "write"
//       "confidence":    "exact",     // Mono is always "exact"
//       "ilOffset":      35,          // byte offset of the matching IL op (Mono-only)
//       "address":       null         // Mono — JIT addresses don't survive restart
//     }
//   ],
//   "scanned_methods": 1284,
//   "truncated":       false
// }
```

```jsonc
// IL2CPP response shape (static field Game.Player.InstanceCount)
// → {
//   "ok": true,
//   "field": {
//     "name":          "InstanceCount",
//     "type":          "System.Int32",
//     "offset":        0,
//     "isStatic":      true,
//     "staticAddress": "0x00007FF876200000"  // IL2CPP-only — the resolved
//                                            // target the RIP scan walked for
//   },
//   "references": [
//     {
//       "assembly":      "Assembly-CSharp",
//       "namespace":     "Game",
//       "class":         "Player",
//       "method":        "RegisterPlayer",
//       "methodToken":   100663600,
//       "methodFlags":   22,
//       "isMethodStatic": true,
//       "isStatic":      true,
//       "kind":          "read",       // "read" for MOV r,[rip+] / LEA r,[rip+];
//                                      // "write" for MOV [rip+],r
//       "confidence":    "exact",      // RIP-relative encoding is unambiguous
//       "address":       "0x00007FF876123450", // enclosing method's AOT entry
//       "hitAddress":    "0x00007FF876123460"  // exact IP of the matching insn
//     }
//   ],
//   "scanned_methods": 4523,
//   "truncated":       false
// }
```

```jsonc
// IL2CPP response shape (INSTANCE field Game.Player.currentHp)
// → {
//   "ok": true,
//   "field": {
//     "name":     "currentHp",
//     "type":     "System.Int32",
//     "offset":   24,            // offset within Il2CppObject (incl. header)
//     "isStatic": false
//     // NO staticAddress — instance fields have no single storage address
//   },
//   "references": [
//     {
//       "assembly":      "Assembly-CSharp",
//       "namespace":     "Game",
//       "class":         "Player",       // declaring-class scope only
//       "method":        "TakeDamage",
//       "methodToken":   100663700,
//       "methodFlags":   134,
//       "isMethodStatic": false,
//       "isStatic":      false,
//       "kind":          "read",         // "read" / "write" (mem operand action)
//       "confidence":    "heuristic",    // displacement match, no `this` taint
//       "address":       "0x00007FF876123450", // enclosing method's AOT entry
//       "hitAddress":    "0x00007FF876123461"  // exact IP of the matching insn
//     }
//   ],
//   "scanned_methods": 87,
//   "truncated":       false
// }
```

**Backend shape diffs (small)**:

| Field on row | Mono | IL2CPP |
|---|---|---|
| `address`    | always `null` | enclosing method's AOT entry |
| `hitAddress` | absent | exact IP of the matching insn |
| `ilOffset`   | byte offset of the IL op | absent (native scan, no IL boundary) |
| `confidence` | `"exact"` | `"exact"` (static) / `"heuristic"` (instance) |

| Field on header | Mono | IL2CPP |
|---|---|---|
| `token`         | target field's metadata token | absent |
| `staticAddress` | absent | static fields only (absent for instance fields) |

**Caveats**:

- IL2CPP **instance fields** use a heuristic, declaring-class-scoped scan
  (`confidence: "heuristic"`). Known recall gaps: references from **other
  classes** to a public field are missed (scope is the declaring class);
  accessors **inlined** into other-class callers are missed (inherent vs
  Mono's pre-JIT IL walk). `this` spilled to the stack and reloaded is still
  matched (the scan keys on the displacement, not the base's provenance), so
  that path keeps recall. False positives are possible (offset collisions
  with other objects touched in the same method) — prefer the Mono backend
  when exactness matters. Static fields work on both backends.
- IL2CPP **opcode coverage** is `0x8B` (MOV load) / `0x8D` (LEA) / `0x89`
  (MOV store) at both 6-byte (no REX) and 7-byte (REX prefix) forms.
  `MOV r/m, imm32` (0xC7) and SIMD `movss` / `movsd` (0xF3/0xF2 0x0F 0x10)
  are NOT matched in v1 — most static-field reads in AOT Unity IL2CPP
  are scalar MOV / LEA so this catches the vast majority. Float static
  reads via `movss` would miss; rare enough that adding coverage is
  v2.
- Mono **generic methods** are scanned once at the open-generic level.
  Instantiated versions share the same IL body, so a token match in
  the open generic counts for every instantiation — same semantics as
  dnSpy navigation.
- Cross-assembly attribution (IL2CPP) — hits whose enclosing method
  falls outside the target image are silently dropped. Scanning every
  user assembly's method index would be O(NM); v1 scopes to one image
  per call.

### `walkArray` *(Both)*

Reads a managed `T[]` header at `address` and returns up to `limit` per-element
entries. Element addresses are slot-relative (`data_address + i * element_size`);
primitive elements decode inline using the same per-type table as
`walkInstance`.

| Arg | Type | Notes |
| --- | --- | --- |
| `address` | address | required; start of the `MonoArray` / `Il2CppArray` header |
| `elementType` | string | optional managed-type name, e.g. `"System.Int32"`, `"MainGame.CARRYITEM"`. Required for primitive decoding. |
| `limit` | uint | optional, default 256, capped at 4096 |
| `elementSize` | uint | optional width hint for struct arrays the decoder can't introspect |
| `elementAssembly` | string | optional element-class spec; when supplied the active backend resolves the class via `class_from_name` and fills `element_size` / `is_pointer` / `element_class_*` from `class_value_size` + `class_is_valuetype`. Lets the caller skip width hints for struct arrays. |
| `elementNamespace` | string | empty string for top-level types |
| `elementName` | string | element class simple name |

Header layout (identical between Mono-bdwgc and IL2CPP, x64):

```
+0x00 vtable*       +0x10 bounds*        (NULL for SZARRAY)
+0x08 sync          +0x18 max_length     (uintptr_t — total length)
                    +0x20 vector[0]      (first element)
```

`List<T>` handling stays client-side: the caller `readMemory`s the
`List._items` pointer at `+0x10` of the List object first, then `walkArray`s
that pointer.

```jsonc
// → {
//   "address":      "0x...",        // echoed
//   "data_address": "0x...",        // start of vector (header + 0x20)
//   "length":       42,             // total length read from header
//   "limit":        256,
//   "element_type": "MainGame.CARRYITEM",
//   "element_size": 8,              // sizeof(T) for value, 8 for ref
//   "is_pointer":   true,           // each slot is an 8-byte qword
//   "count":        42,             // how many were emitted (≤ length, ≤ limit)
//   "elements": [
//     { "index": 0, "address": "0x...", "ptr": "0x..." },
//     { "index": 1, "address": "0x...", "ptr": "0x..." },
//     ...
//   ],
//   // present only when elementAssembly + elementName were supplied AND
//   // the runtime had class_value_size / class_is_valuetype bound:
//   "element_class_namespace": "MainGame",
//   "element_class_name":      "CARRYITEM",
//   "element_is_value_type":   false
// }
```

For primitive arrays the per-element shape carries `value` (or `string` /
`error` etc., same shape as `walkInstance.fields[]`). Soft errors land in the
top-level `error` (e.g. `"header unreadable"`, `"implausible length — not a
managed array?"` when the qword at `+0x18` exceeds 16 Mi).

---

## Enum decoding

### `getEnumValues`

* **IL2CPP**: combines runtime + metadata. Verifies `parent.name == "Enum"`,
  then for each declared field reads its `FieldDefault` blob from
  `global-metadata.dat` and decodes it as an
  [ECMA-335 §II.23.2 compressed signed integer](https://ecma-international.org/publications-and-standards/standards/ecma-335/).
* **Mono**: stub. Full decode needs `mono_field_get_data` and
  `mono_class_enum_basetype` resolver bindings (planned).

| Arg | Type | Notes |
| --- | --- | --- |
| `assembly`, `namespace`, `name` | class spec | required |

```jsonc
// IL2CPP result:
{
  "namespace": "UnityEngine", "name": "CameraType",
  "count": 5,
  "values": [
    { "name": "Game",       "field_index": <int>, "value": 1,
      "enc_bytes": 1, "raw_hex": "02..." },
    { "name": "SceneView",  "field_index": <int>, "value": 2,
      "enc_bytes": 1, "raw_hex": "04..." },
    ...
  ]
}
```

`enc_bytes` (1 / 2 / 4) tells you how many bytes the compressed-int decoder
consumed. `raw_hex` is the first 8 bytes of the FieldDefault blob — useful
for re-decoding if the underlying type isn't Int32 (the current decoder
doesn't yet honor the underlying enum type; that lands with Phase 5
segment 2d).

Soft errors: `class is not an enum`, `type not found in metadata: ...`.

---

## IL2CPP metadata (raw)

These talk directly to the on-disk `global-metadata.dat` and bypass the
runtime. They're useful when the runtime view differs (e.g. literal fields
that were stripped from `il2cpp_class_get_fields` after JIT).

### `il2cppGetMetadataStrings` *(IL2CPP)*

Dump the string-literal pool.

| Arg | Type | Notes |
| --- | --- | --- |
| `offset` | uint | optional, default 0 |
| `limit` | uint | optional, default 0 = all |

```jsonc
// → { "version": 29, "count": 50000, "offset": 0, "returned": 100,
//      "strings": ["...", "...", null, ...] }
```

`null` entries mean a string failed bounds validation (extremely rare).

---

## Address finder *(Both)*

Phases 17a + 17b — inverse of `findInstances`. Given an arbitrary process
address (typically from CE's *Find what writes / accesses*, pointer scan,
or manual probe), identify which managed instance + class + declared field
it sits inside. Algorithm: backward 8-byte stride scan from the queried
address looking for an object header whose first qword is a valid runtime
class pointer (IL2CPP) or vtable pointer (Mono); then walk the parent
chain and match the queried byte offset against each declared field's
`[offset, offset+size)` range.

### `whatIsAt`

Strategy A backward scan. Builds the class-pointer cache implicitly on first
call; subsequent calls reuse the cache.

| Arg | Type | Notes |
| --- | --- | --- |
| `address` | hex string / int | required — the address to look up |
| `maxBackwardScan` | uint | optional, default 4096; capped at 16384 |

Response — managed instance found:

```jsonc
// → {
//   "found": true,
//   "address":     "0x1FB9D39D8",     // queried address
//   "instance":    "0x1FB9D39D0",     // base of containing managed object
//   "class_ptr":   "0x7FF5...",       // Il2CppClass* / MonoClass*
//   "vtable_ptr":  "0x...",           // Mono only — MonoVTable* in object header
//   "assembly":    "Assembly-CSharp",
//   "namespace":   "MainGame",
//   "class":       "PLY_WORK",
//   "fieldOffset": 8,                 // bytes from instance base to queried address
//   "field": {                        // 17b — field whose [offset, offset+size) contains fieldOffset
//     "name":           "HP",
//     "type":           "System.Int32",
//     "offset":         8,
//     "size":           4,
//     "isStatic":       false,
//     "isValueType":    true,
//     "isEnum":         false,
//     "declaredIn":     "PLY_WORK",   // class name where this field is declared
//     "kind":           "leaf",       // leaf | value-struct | pointer | interior
//     "interiorOffset": 0             // present only when kind == "interior"
//     // "underlying_type": "System.Int32"   // present only when isEnum == true
//   },
//   "nearestField":      null,        // populated only when field == null (no exact range hit)
//   // "nearestFieldDelta": <int>     // present only when nearestField != null
//   "scannedRange": { "from": "0x...", "to": "0x..." },
//   "scannedSteps": <uint>            // number of 8-byte probes taken
// }
```

**Field kinds**:
- `leaf` — primitive type at the field's exact offset (`System.Int32`, `System.Single`, `System.Boolean`, …) or an `enum`.
- `value-struct` — value type at the field's exact offset (`Vector3`, `Color`, user struct, …).
- `pointer` — reference type at the field's exact offset (`System.String`, an array, a class).
- `interior` — queried offset falls inside the field's storage but not at its boundary; `interiorOffset` reports bytes from the field's start.

When no declared field's range contains the queried offset (e.g. inside compiler padding or the unmanaged tail of a class), `field` is `null` and the closest declared field below the offset is returned in `nearestField` along with `nearestFieldDelta` (queriedOffset − field.offset).

Response — no managed instance in the scanned range:

```jsonc
// → {
//   "found":   false,
//   "reason":  "no_klass_in_range",   // or class_range_cache_unavailable / no_il2cpp_domain / ...
//   "address": "0xDEADBEEF",
//   "scannedRange": { "from": "0x...", "to": "0x..." },
//   "scannedSteps": <uint>
// }
```

### `getClassRangeStats`

Diagnostic for the class-pointer cache. Exposed mostly for smoke tests; the
production caller never needs it (the cache builds on demand).

| Arg | Type | Notes |
| --- | --- | --- |
| `rebuild` | bool | optional, default false — drops + rebuilds the cache |

```jsonc
// IL2CPP → { "backend": "IL2CPP", "built": true, "class_count": 10001,
//            "min_klass": "0x7FF...", "max_klass": "0x7FF..." }
// Mono   → { "backend": "Mono",   "built": true, "class_count": 12345,
//            "vtable_count": 11234, "min_klass": "...", "max_klass": "...",
//            "min_vtable": "...", "max_vtable": "..." }
```

---

## Phase 18 — Find method instances + AOB pattern

Two paired commands. `findMethodInstances` enumerates every overload of a
method on a class with native address + decoded signature + a symbol-safe
human-readable suffix; `getMethodAobPattern` reads N bytes at any address
and returns a CE-style AOB pattern with rel8/rel32 immediates and
RIP-relative ModRM disp32 fields wildcarded.

### `findMethodInstances`

| Arg | Type | Notes |
| --- | --- | --- |
| `assembly`, `namespace`, `name` | strings | identify the class — same shape as `getClassInfo` |
| `method` | string | required — method name to enumerate overloads for |
| `signatureMatch` | string | optional — substring filter against the rendered signature (e.g. `"Vector3"` picks only the Vector3-arg overload) |

```jsonc
// → {
//   "method":  "Set",
//   "count":   2,
//   "matches": [
//     {
//       "name":               "Set",
//       "token":              <uint>,
//       "flags":              <uint>,           // MethodAttributes
//       "iflags":             <uint>,           // MethodImplAttributes
//       "isStatic":           false,
//       "isVirtual":          false,
//       "isAbstract":         false,
//       "compiled":           true,             // false for abstract / generic-without-args
//       "address":            "0x7FF6A7773740", // post-JIT (Mono) / AOT (IL2CPP)
//       "paramTypes":         ["System.Single", "System.Single", "System.Single"],
//       "paramNames":         ["newX", "newY", "newZ"],   // empty when get_param_names unbound
//       "returnType":         "System.Void",
//       "declaringClass":     "Vector3",
//       "declaringNamespace": "UnityEngine",
//       "signature":          "(System.Single, System.Single, System.Single)",
//       "humanSuffix":        "3float"           // symbol-safe, used by AA-script generator
//     },
//     { "...second overload..." }
//   ]
// }
```

`humanSuffix` shape (single source of truth: `dll/src/shared/MethodSignatureFormat.h`):
- 0 args → `"noargs"`
- 1 arg → `"<Short>"` (e.g. `"Vector3"`)
- N same → `"<N><Short>"` (e.g. `"3float"`)
- N mixed → `"<S0>_<S1>_..."` (e.g. `"string_Card_Point"`)

C# keywords for primitives: `bool` / `byte` / `sbyte` / `short` / `ushort` /
`int` / `uint` / `long` / `ulong` / `float` / `double` / `char` / `string` /
`object` / `void`. Other types use the last segment after `.` or `/`,
generic backticks (`Foo`1[Bar]`) and `<…>` instantiations stripped, sanitised
to `[A-Za-z0-9_]`.

### `getMethodAobPattern`

Backend-agnostic. Walks N bytes through a minimal x64 length disassembler
with conservative wildcarding: rel8/rel32 immediates (CALL/JMP/Jcc/LOOPxx)
and RIP-relative ModRM disp32 → `??`. Unknown opcode → fall through with
literal bytes (the AOB stays valid, just less version-stable across game
patches). False positives (over-wildcarding) cause symbol ambiguity and are
the failure mode we avoid.

| Arg | Type | Notes |
| --- | --- | --- |
| `address` | hex string / int | required — the method's native entry point |
| `length` | uint | optional, default 32, capped at 128 |

```jsonc
// → {
//   "address":      "0x7FF6A7773740",
//   "length":       32,
//   "pattern":      "F3 0F 11 09 F3 0F 11 51 04 ... 48 89 5C 24 ?? E8 ?? ?? ?? ??",
//   "bytes":        "F30F1109F30F11510...",          // raw hex, no wildcards
//   "moduleName":   "GameAssembly.dll",              // "" if address isn't in any loaded module
//   "moduleBase":   "0x7FF6A7260000",
//   "moduleOffset": "0x00000000005A3740",            // address - moduleBase
//   "moduleSize":   47968256,
//   "stats": {
//     "instructions": 17,
//     "wildcards":    8,
//     "relBranches":  2,                             // CALL/JMP/Jcc immediates wildcarded
//     "ripRel":       0                              // RIP-relative disp32 fields wildcarded
//   },
//   "decoderBailed": false,
//   "bailedAt":      0                               // present only when decoderBailed == true
// }
```

The wire response is everything the UI needs to emit a complete
`aobscanmodule(name, MODULE, AOB)` line — module name + AOB pattern plus
the original address as a sanity-check trailing comment.

---

## Phase 19a / 19b / 19c — Lua-callable Unity methods

### `callMethod` *(IL2CPP + Mono)*

Invokes a managed method via `il2cpp_runtime_invoke` (IL2CPP backend) or
`mono_runtime_invoke` (Mono backend) with marshaled args and a typed
return value. **Static AND instance methods**, primitive / null / address
/ string args, primitive / address / UTF-8-decoded-string returns, plus
managed exception capture. Wire shape is byte-identical between the two
backends; the only differences visible to the caller are the `error`
strings when a backend-specific export is missing
(`il2cpp_runtime_invoke unbound` vs `mono_runtime_invoke unbound`,
similarly for `*_string_new`). Full spec, error-code table, marshaling
rules, GC-root caveats: [lua-invoke.md](lua-invoke.md).

| Arg | Type | Notes |
| --- | --- | --- |
| `assembly`, `namespace`, `name` | strings | identify the class — same shape as `getClassInfo` |
| `method` | string | required — method name |
| `signatureMatch` | string | optional — substring filter against the rendered signature, same semantics as `findMethodInstances` |
| `instance` | hex string / int | managed object pointer for instance dispatch; null/omit for static methods (silently ignored if the resolved overload is static) |
| `args` | array of `{type, value}` | one entry per formal parameter; `type` is one of `bool`/`byte`/`sbyte`/`int16`/`uint16`/`int32`/`uint32`/`int64`/`uint64`/`float`/`double`/`char`/`string`/`address`/`null`/`value-struct` |
| `args[].bytes` | hex string / byte array | **value-struct only** (Vector3/Quaternion/…): the struct's raw little-endian image, in place of `value` |
| `main_thread` | bool | optional — run on the Unity main thread (required for engine methods that touch the scene graph, e.g. `Transform.set_position`) |
| `returnStruct` | bool | optional — decode a value-type-by-value return into `returnStructBytes` (hex) + `returnStructSize`; pass `returnStructSize` as a byte-count fallback |

```jsonc
// → success
// {
//   "ok":          true,
//   "returnType":  "System.Int32",
//   "returnValue": 7,
//   "exception":   null,
//   "elapsedMs":   2,
//   "method": {
//     "address":     "0x7FF6A7773740",
//     "signature":   "(System.Int32)",
//     "humanSuffix": "int"
//   }
// }
//
// → managed exception caught
// {
//   "ok":          false,
//   "returnType":  "System.Int32",
//   "returnValue": null,
//   "exception":   "System.FormatException: Input string was not in a correct format.",
//   "elapsedMs":   3,
//   "method":      { "...": "..." }
// }
//
// → resolution failure
// { "error": "method_not_found" }     // or "ambiguous_method", "method_is_abstract", ...
```

`returnValue` encoding by `returnType`:

| `returnType` | `returnValue` shape |
| --- | --- |
| `System.Void` | `null` |
| `System.Boolean` / unsigned ints / `Char` | unsigned int |
| signed ints | signed int |
| `System.Single` / `Double` | float |
| `System.IntPtr` / `UIntPtr` / any reference type | hex string `"0x..."` |
| `System.String` | UTF-8 string (decoded from the managed UTF-16 buffer) |
| Value type by value (Vector3 etc.) | `returnValue:null`; with `returnStruct:true` in the request, raw little-endian bytes in `returnStructBytes` (hex) + `returnStructSize`. Without the flag, `null` + `note`. |

Switch on `returnType`, not on the JSON type of `returnValue`.

---

### `il2cppGetMetadataTypeFields` *(IL2CPP)*

Per-type field listing including const literals (which don't appear in the
runtime field iterator). Each field reports its raw `FieldDefault` data when
present, with a best-effort string decode.

| Arg | Type | Notes |
| --- | --- | --- |
| `namespace`, `name` | strings | `name` required |

```jsonc
// → {
//   "namespace": "Some.Ns", "name": "Type", "type_index": <int>,
//   "flags": <uint>, "token": <uint>,
//   "field_start": <int>, "field_count": <int>,
//   "fields": [
//     { "name": "...", "index": <int>, "type_index": <int>, "token": <uint>,
//       "default": {                        // only if FieldDefault exists
//         "type_index": <int>, "data_index": <int>,
//         "raw_hex_first16": "...", "raw_size": <int>,
//         "decoded_string": "...",          // only when shape matches a string
//         "value_kind": "normal" | "infinity" | "-infinity" | "nan"
//                                            // R4/R8 only — see note below
//       }
//     },
//     ...
//   ]
// }
```

**R4 / R8 special-value handling**: JSON disallows `Infinity` / `-Infinity`
/ `NaN` as numeric literals; nlohmann/json silently coerces those floats
to `null`. To preserve the semantic, R4 and R8 default values now also
emit a `value_kind` field — `"normal"` for finite values, `"infinity"` /
`"-infinity"` / `"nan"` for the IEEE-754 special cases (with `value`
set to JSON `null` in those three cases). Older clients that ignore
`value_kind` continue to see `value: null` for non-finite floats, same
as before this fix. Added 2026-05-08 closing the cosmetic backlog item
surfaced by the v31 cross-version sweep.

---

### `dumpIl2cppSymbols` *(IL2CPP)*

Bulk dump of every IL2CPP class and/or AOT method native entry across the
supplied assemblies. Drives the UI's "Export Symbols → x64dbg / Ghidra /
IDA" actions — see [export-formats.md](export-formats.md) for the
debugger-side format details.

Mono is unsupported here on purpose: Mono managed methods live in dynamic
JIT pages with no stable module-relative offset, so a session snapshot
can't be reloaded into a debugger after a game restart.

| Arg | Type | Notes |
| --- | --- | --- |
| `assemblies`         | string[] | required; same name shape as `getClasses` (e.g. `"Assembly-CSharp"`). Missing assemblies are reported in `missing_assemblies` rather than failing the whole call. |
| `kinds`              | string[] | optional; subset of `["class", "method"]`. Default = both. |
| `computeHumanSuffix` | bool    | optional, default `true`. When `true`, every method row carries a `humanSuffix` field built from its formal-parameter types — same shape as `findMethodInstances`. Skip only if you don't care about overload collisions and want to shave the per-method param-walk cost on very large dumps. |
| `limit`              | int     | optional; `0` (default) = unlimited up to a 1,000,000-row safety cap. When the cap fires `truncated` is `true`. |

```jsonc
// → {
//   "moduleName":      "GameAssembly.dll",
//   "moduleBase":      "0x00007FF6A7260000",      // absolute, "0x..." hex
//   "moduleSize":      47968256,
//   "rows": [
//     { "kind": "class",  "assembly": "Assembly-CSharp",
//       "namespace": "Game", "class": "Player",
//       "address": "0x00007FF6A7990400" },
//     { "kind": "method", "assembly": "Assembly-CSharp",
//       "namespace": "Game", "class": "Player",
//       "name":     "TakeDamage",
//       "token":    100663552,
//       "static":   false,
//       "address":  "0x00007FF6A7773740",
//       "humanSuffix": "int" },
//     ...
//   ],
//   "count":              42_817,
//   "scanned_classes":    1_503,
//   "truncated":          false,
//   "missing_assemblies": ["Foo.Bar"]              // optional, omitted when empty
// }
```

**Address spaces**:

- Class rows' `address` is the `Il2CppClass*` — a `.data` pointer inside
  `GameAssembly.dll`. Useful as a label when reverse-engineering vtable /
  RGCTX lookups.
- Method rows' `address` is the AOT `methodPointer` — a `.text` entry
  point. Same value `findMethodInstances` returns.
- Both are subtractable from `moduleBase` to produce a stable
  module-relative offset, which is what x64dbg's `.dd64` labels use.

**Suffix collisions**: when `computeHumanSuffix` is enabled, two overloads
of the same method get distinct rows (`HumanSuffix="int"` vs
`HumanSuffix="float"`) — the UI's `SymbolExporter.BuildLabel` appends the
suffix so the resulting debugger labels stay unique.

**Cost guidance**: full dump of a typical Unity 2022 IL2CPP game's
`Assembly-CSharp` + 4-5 user assemblies runs ~1-3 seconds and produces
30-100k rows. With `computeHumanSuffix=false` it's roughly 2x faster but
overloads will collide in the rendered output.

---

## Phase 29a — Snapshot capture (Both)

Three-RPC streaming protocol so the UI can capture a wide
"every-class, every-instance, every-field" managed-state dump without
holding a single 100 MB JSON response in memory. Server caches the
pre-built JSON inside a per-session struct; the UI drains it via
fixed-size chunks then sends `snapshotEnd` to release.

### `snapshotBegin`

Request keys (all optional; sensible defaults map to "全抓 user
assemblies"):

```json
{
  "asmFilter":          "Assembly-CSharp,Mortal.Core",  // CSV; empty=all
  "instanceCap":        512,                            // per-class
  "arrayCap":           256,                            // per-array
  "includeUnchanged":   false,                          // see snapshot store
  "includeSystemAssemblies": false                      // skip mscorlib/Unity*
}
```

Response:

```json
{
  "ok":             true,
  "snapshotId":     "snap-1722340800-7f3a",
  "capturedAt":     "2026-05-29T12:34:56Z",
  "gameBuildId":    "7f3a2b1c89e44d10",  // FNV-1a(modulepath+size+writeTime)
  "gameSessionId":  "5e8c1...",          // per-inject GUID
  "totalClasses":   3647,
  "totalInstances": 119370,
  "plan":           [ { "assembly": "...", "classes": 152 }, ... ],
  "scanStats":      { ... },
  "decodeStats":    { ... },
  "instanceCap":    512,
  "arrayCap":       256,
  "excludedSystemAssemblies": 14,
  "includeSystemAssemblies":  false,
  "gamePointerSize": 4,                  // x86 game → UI auto-clamps caps
  "gameWorkingSet":  1932735283,
  "gamePrivateUsage":2068054016
}
```

On x86 targets with PrivateUsage ≥ 3 GB the server **refuses** the
snapshot with `{ "ok": false, "error": "Game process at X.X GB
private memory — refusing snapshot to avoid OOM crash on this 32-bit
Mono target. Lower InstanceCap (try 128) and ArrayCap (try 32) then
retry, or restart the game to free memory before snapshotting." }`.
This is the 32-bit OOM mitigation from build #645 — see lessons-learned.

### `snapshotChunk`

```json
{
  "snapshotId": "snap-...",
  "cursor":     0,    // client-tracked; resumes from server's nextCursor
  "limit":      32    // max class entries this chunk
}
```

Response is a slice of the pre-built payload:

```json
{
  "ok":         true,
  "snapshotId": "snap-...",
  "cursor":     0,
  "nextCursor": 32,
  "done":       false,
  "classes":    [ /* SnapshotClassEntry[] */ ]
}
```

The UI loops until `done=true`. Per-class payload shape matches
existing `WalkInstanceResult` field structure with array-element
support: each `instances[].fields[]` may carry `arrayElements` /
`arrayError`.

### `snapshotEnd`

```json
{ "snapshotId": "snap-..." }
```

Releases the server-side session struct (frees the cached JSON in
the game process). UI calls this in a `finally` so the slot is
freed even if the chunk loop errored.

### Lifecycle

| Step | Server state |
|---|---|
| `snapshotBegin` | Heap walk → per-class decode → JSON build → cache in session struct. ~5–60 s depending on InstanceCap × class count. |
| `snapshotChunk` (loop) | Zero-Mono-runtime slicing of the cached JSON. ~10–50 ms each. |
| `snapshotEnd` | Drop the session struct + free the cache. |

The DLL allows multiple concurrent snapshot sessions (per
`snapshotId`) so a future workflow can run two captures in parallel.

---

## Quick tour by use case

| Goal | Path |
| --- | --- |
| Verify the DLL is up | `ping` → `handshake` |
| Discover classes | `getAssemblies` → `getClasses` → `getClassInfo` |
| Get a static field's runtime address | `getStaticFieldData` |
| Find live instances of a class | `findInstances` |
| Inspect one instance | `walkInstance` (use an address from `findInstances`) |
| Dereference a pointer chain | `resolvePointer` |
| Read / write raw bytes | `readMemory` / `writeMemory` |
| Stash an address for later | `registerSymbol` / `lookupSymbol` |
| List enum members | `getEnumValues` (IL2CPP today) |
| Read `const` literals stripped by IL2CPP | `il2cppGetMetadataTypeFields` |
| Identify the instance + class at an address (CE *what writes here*) | `whatIsAt` |
| Enumerate every overload of a method with native addresses | `findMethodInstances` |
| Generate a CE AOB pattern from a method's native address | `getMethodAobPattern` |
| Invoke a managed method with marshaled args (IL2CPP + Mono, static + instance) | `callMethod` |
| Dump every class + method as debugger labels (IL2CPP) | `dumpIl2cppSymbols` |
| Capture managed-state snapshot of the game (Phase 29a) | `snapshotBegin` → `snapshotChunk` loop → `snapshotEnd` |

---

## Versioning

* `handshake.protocol` is the integer protocol version. It bumps when the
  envelope shape, framing, or any reply key on a stable command changes in
  a backwards-incompatible way.
* `handshake.build` is the DLL build counter (see `build_number.txt`). Use
  it to detect a stale injection.

The current protocol version is **1**.
