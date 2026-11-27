# Lua-callable Unity methods

> Phase 19 design spec. Companion to [plan.md](plan.md#phase-19--lua-callable-unity-methods-lua--managed-invocation).
> Implementation status: **19a (IL2CPP)** + **19b (Mono port)** + **19c
> (instance dispatch + string in/out marshaling)** all landed end-to-end on
> both backends. 19d adds the UI MethodCallDialog, 19e ships the
> `unity_invoke.lua` bridge.

The Phase 19 wire surface is a single new pipe RPC: `callMethod`. It takes a
`(class, method, args)` triple, marshals the typed args into the Mono /
IL2CPP runtime's invoke entry point, captures any managed exception, and
returns the unboxed primitive (or raw managed pointer) the callee produced.

The original goal in [CLAUDE.md](../CLAUDE.md) is "invoke managed methods
from CE Lua with marshaled args / return values." This doc pins down what
"marshaled" means in the wire protocol so each phase ships the same shape.

## Why a custom RPC when CE has built-in support?

**Optional alternative — not the recommended path.** Cheat Engine 7.5+ ships
its own Mono client (`mono.dll` Lua module, exposed when "Enable Mono
features" is ticked in Settings). It works on **both Mono and IL2CPP**
backed Unity games. Public API includes:

```lua
LaunchMonoDataCollector()
local img    = mono_findImage("Assembly-CSharp")
local cls    = mono_image_findClass(img, "MyNamespace", "MyClass")
local method = mono_class_findMethodWithSignature(cls,
                  "MyMethod", "Int32,String")
local result = mono_invoke_method(domain, method, thisPtr, {42, "hello"})
```

So in principle, **CE alone can call Unity managed methods on either
backend** — no DLL injection needed.

In practice, getting to a working `mono_invoke_method` call from CE Lua
is painful:

| Pain point | CE built-in path | Our `callMethod` |
|---|---|---|
| Class / method discovery | `mono_findImage` → `mono_image_findClass` → `mono_class_findMethod` (+ `_findMethodWithSignature` for overloads); each call needs a fresh `LaunchMonoDataCollector` if the previous session was torn down | One `callMethod` RPC with `(assembly, namespace, name, method, signatureMatch)`; resolution is server-side and stateless |
| Overload disambiguation | Build the comma-separated CLR signature by hand (`"Int32,String"`) and hope your spelling matches what Mono reports | Substring filter against the formatted signature; matches the exact same shape `findMethodInstances` uses (Phase 18) |
| Argument marshaling | Caller picks how to pass each arg — primitives directly, strings via `mono_string_new`, objects as `MonoObject*` etc. — and gets it wrong silently when the convention differs across CE versions | Wire `type` tags resolve the convention per arg; the DLL handles the IL2CPP/Mono non-uniform pointer indirection that bit our own first impl |
| String return decode | Manual: `mono_string_toCSharpString(returnValue)` — assumes the return is in fact a managed string | Auto: `returnType == "System.String"` triggers UTF-16 → UTF-8 decode in `DecodeManagedString` |
| Exception capture | Faults bubble up as Lua errors; type + message extraction is on the user | `Type: message` populated automatically from `Il2CppException + 0x18` / `MonoException + 0x18` |
| Works while CE is detached | No (CE must be attached to use mono_*) | Yes — pipe is hosted by the injected DLL, any client can drive it (UI, PowerShell, Lua, custom tools) |
| Phase 18 symbol naming integration | Manual | `humanSuffix` already in `findMethodInstances` response — same shape as registerSymbol AA scripts |

**Recommendation:** for prototyping or one-off shots, CE's built-in
`mono_invoke_method` is fine. For shipping a cheat table with consistent
behavior across Mono + IL2CPP games — or any workflow that wants the same
API surface from PowerShell / the UI / Lua at once — `callMethod` is the
lower-friction path.

## Scope by sub-phase

|        | static | instance | primitives | string args | string return | exception | runtime |
|--------|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| **19a**✅ | ✓  |    | ✓  |    | (as address) | ✓ | IL2CPP |
| **19b**✅ | ✓  |    | ✓  |    | (as address) | ✓ | + Mono |
| **19c**✅ | ✓  | ✓  | ✓  | ✓  | ✓  | ✓ | both   |
| 19d (UI)    | UI dialog only — no protocol change |
| 19e (Lua)   | Lua bridge only — no protocol change |

Out of scope until v2:
- `ref` / `out` parameters
- Open-generic methods (the IL2CPP runtime registers concrete instantiations;
  we'd expose a way to pick one)
- Boxed value-type args (`Vector3`, `Color`, user struct) by-value — pass
  by pre-allocated address only
- `async` / `Task<T>` return values — call the synchronous entry point
- Marshaling back to the game's main thread

## Wire request

```jsonc
{
  "id": 1,
  "cmd": "callMethod",
  "args": {
    "assembly":       "Assembly-CSharp",
    "namespace":      "MainGame",
    "name":           "Player",
    "method":         "TakeDamage",
    "signatureMatch": "Int32, String",   // optional; same semantics as Phase 18
    "instance":       "0x16F8CEA0BE0",   // null/omit for static methods (19c)
    "args": [
      { "type": "int32",  "value": 10 },
      { "type": "string", "value": "fire" }   // 19c
    ]
  }
}
```

Identical class-resolution shape as `getClassInfo` and `findMethodInstances`
— `assembly` + `namespace` + `name` triple. Method overload resolution is
the same as Phase 18: `signatureMatch` is a substring filter against the
rendered signature `(System.Int32, System.String)`. Pass it tight enough
that exactly one overload matches; otherwise the response is
`error: "ambiguous_method"`.

### Wire `type` tags for args

| Tag (preferred) | Aliases | Encoding | Notes |
|---|---|---|---|
| `bool`    | `boolean` | JSON `true`/`false` (or 0/1) | Marshaled as 1-byte 0/1 |
| `byte`    | `uint8`   | JSON uint                    | 1 byte |
| `sbyte`   | `int8`    | JSON int                     | 1 byte |
| `int16`   | `short`   | JSON int                     | 2 bytes |
| `uint16`  | `ushort`  | JSON uint                    | 2 bytes |
| `int32`   | `int`     | JSON int                     | 4 bytes |
| `uint32`  | `uint`    | JSON uint                    | 4 bytes |
| `int64`   | `long`    | JSON int                     | 8 bytes |
| `uint64`  | `ulong`   | JSON uint                    | 8 bytes |
| `float`   | `single`  | JSON number                  | 4 bytes |
| `double`  |           | JSON number                  | 8 bytes |
| `char`    |           | JSON uint or 1-char string   | 2 bytes (UTF-16 code unit) |
| `string`  |           | JSON string (UTF-8)          | **19c only**; rejected before 19c |
| `address` | `intptr`, `uintptr`, `ptr` | JSON hex string `"0x..."` or uint | Pass a pre-resolved managed pointer |
| `null`    |           | (value ignored)              | Reference null. Slot holds NULL ptr. |
| `value-struct` | `valuestruct`, `struct` | `bytes` (NOT `value`): hex string or JSON byte array | **Teleport wave 1.** Value type by value (Vector3/Quaternion/user struct). `bytes` = the struct's raw little-endian memory image; the DLL points `params[i]` at a padded backing buffer (128 B cap). Lua builders: `argVector3` / `argQuaternion` / `argValueStruct`. |

The wire `type` tag binds JSON encoding only; the marshaler then casts to
the formal parameter's CLR type. Out-of-range values are truncated to the
formal width (e.g. passing `int32: 70000` to a `byte` parameter writes
`0x70` — the low 8 bits — same as `(byte)70000` in C#).

## Wire response

Successful call:

```jsonc
{
  "id": 1,
  "result": {
    "ok":          true,
    "returnType":  "System.Int32",
    "returnValue": 7,
    "exception":   null,
    "elapsedMs":   2,
    "method": {
      "address":     "0x7FF6A7773740",
      "signature":   "(System.Int32)",
      "humanSuffix": "int"
    }
  }
}
```

Managed exception caught (the host process stays alive — no log-scrubbing
required):

```jsonc
{
  "id": 1,
  "result": {
    "ok":          false,
    "returnType":  "System.Int32",
    "returnValue": null,
    "exception":   "System.FormatException: Input string was not in a correct format.",
    "elapsedMs":   3,
    "method":      { /* ... */ }
  }
}
```

Resolution-level failure (no method found, ambiguous overloads, abstract
method, etc.) — note no `ok` / `returnType` keys, only `error`:

```jsonc
{ "id": 1, "result": { "error": "method_not_found" } }
```

### `returnValue` encoding by `returnType`

| Return type | `returnValue` JSON shape |
|---|---|
| `System.Void` | `null` |
| `System.Boolean` | `true` / `false` |
| `System.Byte` / `UInt16` / `UInt32` / `UInt64` / `Char` | unsigned int |
| `System.SByte` / `Int16` / `Int32` / `Int64` | signed int |
| Enum types | underlying signed/unsigned int |
| `System.Single` / `Double` | float |
| `System.IntPtr` / `UIntPtr` | hex string `"0x..."` |
| `System.String` | UTF-8 string (decoded from the managed UTF-16 buffer at `+0x14`) |
| Other reference type | hex string `"0x..."` — caller chains `whatIsAt` / `walkInstance` |
| Value type by value (Vector3, Color, ...) | `returnValue:null`. When the request set `returnStruct:true`, the raw little-endian struct bytes come back in `returnStructBytes` (hex) + `returnStructSize` (byte count). Without the flag, `returnValue:null` + a `note`. |

The discriminator is `returnType`; UI / Lua callers should switch on that
when consuming the response, not on the JSON type of `returnValue`.

### Value-struct return decode (opt-in)

Add `"returnStruct": true` to the request to receive a value-type-by-value
return (Vector3/Quaternion/…) as `returnStructBytes` (a hex string of the
struct's raw little-endian bytes) plus `returnStructSize`. The DLL unboxes it
using the runtime's `class_value_size`; supply `"returnStructSize": <bytes>`
as a fallback when that export is unbound (12 for Vector3, 16 for Quaternion).
Off by default — the legacy `note` shape is unchanged for callers that don't
ask. Lua: `callUnityMethod(..., { returnStruct = true, returnStructSize = 12 })`
then `decodeStructFloats(result, 3)`.

### Error codes (in `result.error` or `result.ok=false` paths)

| Code | Cause | Recoverable by |
|---|---|---|
| `il2cpp runtime not resolved` / `mono runtime not resolved` | DLL injected but resolver not bound | re-inject; check `handshake` |
| `il2cpp_runtime_invoke unbound` / `mono_runtime_invoke unbound` | Unity build missing the export | none — pre-Unity-2018? |
| `il2cpp_string_new unbound` / `mono_string_new unbound` | runtime missing the string-alloc export, but a wire `type: string` arg was sent | drop the string arg, or pass an `address` you allocated yourself |
| `il2cpp_string_new returned null` / `mono_string_new returned null` | string allocation failed inside the runtime | usually OOM or a corrupt domain; rare |
| `class not found: …` | bad assembly / namespace / name | fix the triple |
| `method_not_found` | no overload matches name + signature | drop the `signatureMatch` filter or fix it |
| `ambiguous_method` | >1 overload matches | tighten `signatureMatch` (use parens-bounded `(System.Int32)` form to dodge `Int32` ⊂ `UInt32` substring matches) |
| `method_is_abstract` | method has no body to invoke | pick a concrete override; instance dispatch routes through the overload's actual native entry |
| `instance_required_for_non_static` | matched method is non-static, but `instance == null` | pass the managed object pointer as `instance` (typically from `findInstances` / `whatIsAt`) |
| `arg_count_mismatch` | wire `args.length != formal param count` | match the parameter count |
| `arg_type_mismatch` | wire arg `type` (or struct byte size) does not match formal parameter type | match the method signature's formal parameter types |
| `native_crash_in_runtime_invoke` | SEH caught a hard crash inside the JIT/AOT entry point | check the log file and the method's actual signature |

## Caller responsibilities

### GC roots
A managed pointer obtained from `findInstances` / `walkInstance` /
`callMethod` is valid for *the next call you make right now*. A GC that
runs between the address read and the next invoke can move the object
(Mono uses a moving GC; IL2CPP largely doesn't but isn't guaranteed for
all generations). Best practice: do `findInstances` → `callMethod` in a
single Lua block when correctness matters, instead of stashing the address.

### Threading
The invoke runs on the pipe worker thread inside one `ThreadAttachGuard`.
Methods that assume the Unity main thread (most engine APIs touching the
scene graph) will misbehave. v1 doesn't marshal back to the main thread;
target methods that don't touch Unity engine state. v2 follow-up could add
a "queue on next Update" path via a hook — out of scope.

### Overload disambiguation
`signatureMatch` is a substring filter against the rendered signature
`(System.Type1, System.Type2, ...)`. Phase 18 callers already do this for
`findMethodInstances`. Same semantics here:

- `Vector3.Set` with `signatureMatch="Single"` → picks the `Set(float, float, float)` overload
- `Vector3.Set` with `signatureMatch="Vector3"` → picks the `Set(Vector3)` overload
- `Math.Abs` with `signatureMatch="Int32"` → picks `Abs(int)`
- `Math.Abs` with `signatureMatch=""` → ambiguous; protocol returns `ambiguous_method`

### `instance` arg (19c)
Hex address of an Il2CppObject* / MonoObject*. The runtime invoke uses
this as the `this` pointer. For 19a/19b the field is parsed but rejected
with `instance_methods_not_supported_in_19a` if non-null.

## Acceptance gates

Per [plan.md](plan.md#phase-19--lua-callable-unity-methods-lua--managed-invocation):

- **IL2CPP positive (19a)** — `callMethod("UnityEngine.CoreModule",
  "UnityEngine", "Mathf", "Abs", [{int32:-7}])` returns
  `{ok:true, returnValue:7, returnType:"System.Int32"}` on SaGa F2R.
- **Mono positive (19b)** — same shape against a Mono game's
  `System.Math.Abs(int)`.
- **Instance call (19c)** — `Vector3.ToString()` against a live instance
  returns a formatted string (e.g. `"(1.0, 2.0, 3.0)"`).
- **String round-trip (19c)** — any method that accepts `string` and
  returns `string` echoes the input correctly across the wire.
- **Exception path** — calling `Convert.ToInt32` on a non-numeric input
  (e.g. address `0x0` for the string overload, or any malformed input
  once strings land) returns `ok:false` plus the exception's
  `Type: message` shape — host process stays alive.
- **Overload disambiguation** — `signatureMatch` picks
  `Vector3.Set(float, float, float)` over `Vector3.Set(Vector3)`.
- **Tooling** — `tools/Test-CallMethod.ps1` covers the canonical
  positive (return value), negative (managed exception), and
  overload-disambiguation cases.
- **AOT publish stays 0/0 warnings** under all four trim/AOT analyzers
  (UI side, lands in 19d).

## v1 design notes

### IL2CPP runtime exports used (all `bindOpt`)

The minimum set 19a needs:

| Export | Used for |
|---|---|
| `il2cpp_runtime_invoke` | the call itself |
| `il2cpp_object_unbox`   | extract bytes from boxed primitive returns |
| `il2cpp_object_get_class` | resolve exception type name |

19c will additionally use:

| Export | Used for |
|---|---|
| `il2cpp_string_new`   | alloc Il2CppString* for string args |
| `il2cpp_string_chars` | UTF-16 base of a string return |
| `il2cpp_string_length`| length of a string return |

### Mono runtime exports used (all `optBind`)

19b mirrors the IL2CPP set:

| Export | Used for |
|---|---|
| `mono_runtime_invoke` | the call itself |
| `mono_object_unbox`   | extract bytes from boxed primitive returns |
| `mono_object_get_class` | resolve exception type name |
| `mono_compile_method` *(already bound for `findMethodInstances`)* | populate the response's `address` field with the JIT entry point |

19c additions:

| Export | Used for |
|---|---|
| `mono_string_new`     | alloc MonoString* for string args (note: takes a `MonoDomain*` first arg, unlike IL2CPP) |
| `mono_string_chars`   | UTF-16 base of a string return |
| `mono_string_length`  | length of a string return |

All bound as **optional** — pre-19 features (handshake, class walk,
`findMethodInstances`) keep working on minimal Unity builds where these
are missing. The `callMethod` handler refuses cleanly with
`il2cpp_runtime_invoke unbound` / `mono_runtime_invoke unbound` when the
load-bearing export isn't there.

### Mono vs IL2CPP — what's the same, what's different

| | IL2CPP | Mono |
|---|---|---|
| Runtime invoke entry | `il2cpp_runtime_invoke` | `mono_runtime_invoke` |
| Returns a boxed `MonoObject*` | ✓ | ✓ |
| Exception out-param | `Il2CppException**` | `MonoObject**` (effectively the same — managed System.Exception under both runtimes) |
| `void**` params marshaling | low-N-byte writes per slot | identical |
| Address field of response | `MethodInfo->methodPointer` (AOT) | `mono_compile_method(method)` (JIT) |
| Exception layout | `Il2CppException + 0x18 = _message` | `MonoException + 0x18 = _message` (Mono and IL2CPP share the corlib layout) |
| String layout | `+0x10 length, +0x14 chars` | identical |
| `string_new` signature | `(const char*)` | `(MonoDomain*, const char*)` |

### Marshaling layout

Every arg gets one 8-byte slot, even types narrower than 8 bytes (we write
to the low N bytes; the upper bits stay zero). `void** params` then points
at each slot. This matches IL2CPP's marshaling convention in
`libil2cpp/vm/Runtime.cpp::Invoke` and avoids per-type slot sizing.

For null / address args the slot holds the pointer value directly. For
value-type args narrower than 8 bytes (every primitive in v1) we write the
value at the low end of the slot, and rely on the formal-type cast inside
the JIT/AOT trampoline to pick up the correct width.

### Exception decoding

`Il2CppException` mirrors managed `System.Exception`. On x64 the offsets:

```
+0x00  Il2CppObject (klass*, monitor)         16 bytes
+0x10  _className   (Il2CppString*)           legacy, often null
+0x18  _message     (Il2CppString*)           ← read this
+0x20  _data        (IDictionary*)
+0x28  _innerException
...
```

The message offset has been stable across Mono bdwgc + IL2CPP for the
entire Unity 2018+ lifecycle. If the message decode fails we still emit
the exception type alone (decoded via `il2cpp_object_get_class` +
`il2cpp_class_get_name`) so the caller knows the call faulted.

## Future surface (informational — not implemented)

These would slot in without breaking the v1 shape:
- `args[i].byRef = true` flag for `ref`/`out` params; response gets an
  `outValues[]` array.
- ✅ **IMPLEMENTED (teleport wave 1)** — `args[i].type = "value-struct"` with
  `bytes` (hex string / byte array) = the struct's raw little-endian image, and
  opt-in `returnStruct:true` (+ `returnStructSize`) for a value-struct RETURN in
  `returnStructBytes`. Lua builders `argVector3` / `argQuaternion` /
  `argValueStruct` + teleport helpers (`unityTeleportTo`, `unityGetPosition`,
  `unityResolveTransform`) in unity_invoke.lua. Backs the Teleport tab —
  see [teleport.md](teleport.md).
- `args[i].type = "delegate"` with `{methodSymbol: "..."}` to wire a CE
  registered symbol as a managed delegate target.
- `instantiate` arg for open-generic methods, listing concrete type
  arguments by name.

## Phase 21b — field write-back (`setField` / `setInstanceField`)

Companion to `callUnityMethod`. Where Phase 19 lets you call a managed
method, Phase 21b lets you write a primitive instance field's bytes
through the same `\\.\pipe\DiscreteBfx` channel — no method invocation
required, no managed code runs.

Wire spec lives in [pipe-protocol.md §"setInstanceField"](pipe-protocol.md);
this section just covers the Lua wrappers shipped in `unity_invoke.lua`.

```lua
-- Single-shot from a field's storage address (most common — the
-- inspector / address-finder hand you this directly):
local r = setField("0x1F8CEA0BE8", "int32", 999)
if r.ok then log("wrote %d bytes at %s", r.written, r.fieldAddress)
else      err("write failed: %s", r.error or "?") end

-- Two-step from instance + offset (e.g. when the offset comes from
-- walkInstance):
local r = setInstanceField("0x1F8CEA0BE0", 0x28, "float", 3.14)

-- Boolean: 0/1, true/false, "true"/"false" all accepted:
setField("0x1234", "bool", true)

-- Address (raw 8-byte pointer write):
setField("0x1234", "address", "0x00007FFE12345678")
```

**API**

| Function | Signature | Notes |
| --- | --- | --- |
| `setField` | `(fieldAddr, wireType, value) -> result` | Field-address shorthand. Equivalent to `setInstanceField(fieldAddr, 0, wireType, value)`. |
| `setInstanceField` | `(instanceAddr, fieldOffset, wireType, value) -> result` | Full form mirroring the wire RPC shape. |

**Wire type tags** (same taxonomy as Phase 19 arg builders): `bool`,
`int8` / `sbyte`, `uint8` / `byte`, `int16`, `uint16`, `int32` / `int`,
`uint32` / `uint`, `int64` / `long`, `uint64` / `ulong`, `float` /
`single`, `double`, `char`, `address` / `intptr` / `uintptr`. CLR-style
names (`System.Int32`, `System.Boolean`, …) are also accepted — the
walkInstance row's `type` string plugs straight in.

**Result table** (mirrors the wire response):

| Field | Notes |
| --- | --- |
| `ok` | `true` on a successful write; `false` (with `error`) otherwise |
| `address` | echoed instance address |
| `fieldOffset` | echoed |
| `fieldAddress` | computed `address + fieldOffset` |
| `type` | echoed wire type |
| `written` | bytes actually written on success |
| `error` | soft-error message (`"memory not writable"`, `"'value' out of range for int8"`, `"unsupported type '...'"`) |
| `hint` | populated on unsupported-type errors with the v2-deferred-to story |

**Out of scope for v1** (mirrors the DLL — see
[pipe-protocol.md §"setInstanceField"](pipe-protocol.md)): reference-type
writes (`System.String`, object pointer), value-type-by-value
(`Vector3`, `Color`), and atomic batch writes. Pipe-level errors still
throw via the underlying `rpc()` call — wrap calls in `pcall` if you
need a non-throwing variant (no `pcallSetField` shipped — the soft
errors land in `result.error`, which is the more useful signal).
