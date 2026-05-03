# UnityCEDumper pipe protocol

The injected DLL exposes a named-pipe JSON-RPC surface for inspecting the host
Unity process. This document is the contract between the DLL and any UI /
script that talks to it.

## Connection

| Property | Value |
| --- | --- |
| Pipe name | `\\.\pipe\UnityDumpBfx` |
| Direction | duplex (`InOut`) |
| Framing | line-delimited UTF-8 JSON; each request and each response is one line terminated with `\n` |
| Concurrency | one client at a time; the server queues additional connect attempts |
| Lifetime | created by `UnityDumper_StartPipe` after `UnityDumper_Init`; torn down by `UnityDumper_StopPipe` or DLL unload |

Open from PowerShell:

```powershell
$client = New-Object System.IO.Pipes.NamedPipeClientStream(
    '.', 'UnityDumpBfx', 'InOut')
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
| `limit` | uint | optional, default 256, capped at 4096 |

```jsonc
// IL2CPP result:
{
  "class_ptr": "0x...",         // the searched-for pointer
  "count": 100, "limit": 256,
  "matches": ["0x...", "0x...", ...]
}
// Mono result:
{
  "vtable": "0x...",
  "count": 100, "limit": 256,
  "matches": ["0x...", "0x...", ...]
}
```

False positives are possible (the same pointer can appear in metadata tables
or in field values that happen to hold a class reference). Filter by
`instance_size` alignment if you need precision.

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
//   ]
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
//         "decoded_string": "..."           // only when shape matches a string
//       }
//     },
//     ...
//   ]
// }
```

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

---

## Versioning

* `handshake.protocol` is the integer protocol version. It bumps when the
  envelope shape, framing, or any reply key on a stable command changes in
  a backwards-incompatible way.
* `handshake.build` is the DLL build counter (see `build_number.txt`). Use
  it to detect a stale injection.

The current protocol version is **1**.
