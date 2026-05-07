# Mono Backend Internals

> Deep dive into `dll/src/mono/`. Pairs with
> [il2cpp-internals.md](il2cpp-internals.md) — both backends mirror
> each other one-for-one, so reading them side-by-side highlights
> where the runtimes actually diverge (JIT vs AOT, `mono_string_new`
> vs `il2cpp_string_new`, `MonoException +0x18` vs `Il2CppException +0x18`,
> etc. — they barely diverge at all in practice).

---

## Module set

| Module | Role |
|---|---|
| [MonoResolver](../dll/src/mono/MonoResolver.h) / [.cpp](../dll/src/mono/MonoResolver.cpp) | Lazy-bind every Mono runtime export we use from `mono-2.0-bdwgc.dll` (or legacy `mono.dll`). Singleton; missing exports logged but non-fatal. |
| [MonoTypes](../dll/src/mono/MonoTypes.h) | Opaque forward declarations (`MonoDomain*`, `MonoClass*`, `MonoMethod*`…) — no runtime access through these structs, only handed back through the resolver's APIs. |
| [MonoDomain](../dll/src/mono/MonoDomain.h) / [.cpp](../dll/src/mono/MonoDomain.cpp) | Walk the root domain → assemblies → images. Used by `getAssemblies` / `handshake`. |
| [MonoClassWalker](../dll/src/mono/MonoClassWalker.h) / [.cpp](../dll/src/mono/MonoClassWalker.cpp) | Iterate types via `MONO_TABLE_TYPEDEF`, expose field / method iterators, walk nested types. Used by `getClasses`, `getClassInfo`. |
| [MonoTypeParser](../dll/src/mono/MonoTypeParser.h) / [.cpp](../dll/src/mono/MonoTypeParser.cpp) | `MonoType*` → readable string (`int`, `System.Single`, `MyNs.Foo[]`, `List<T>`…). Thin wrapper over `mono_type_get_name`. |
| [MonoAddressFinder](../dll/src/mono/MonoAddressFinder.h) / [.cpp](../dll/src/mono/MonoAddressFinder.cpp) | Phase 17 — backward heap scan to identify which managed instance + class + field a given address belongs to. |
| [MonoMethodFinder](../dll/src/mono/MonoMethodFinder.h) / [.cpp](../dll/src/mono/MonoMethodFinder.cpp) | Phase 18 — enumerate every overload of a named method on a class, return JIT addresses + decoded signatures. |
| [MonoMethodInvoker](../dll/src/mono/MonoMethodInvoker.h) / [.cpp](../dll/src/mono/MonoMethodInvoker.cpp) | Phase 19 — `callMethod` RPC handler. Marshals args, calls `mono_runtime_invoke`, decodes return value + managed exception. |
| [MonoThreadAttach](../dll/src/mono/MonoThreadAttach.h) | RAII guard around `mono_thread_attach` / `mono_thread_detach`. |

---

## Module detection (vs IL2CPP)

[BackendDetector](../dll/src/shared/BackendDetector.h) sniffs loaded
modules:

| Module name | Verdict |
|---|---|
| `mono-2.0-bdwgc.dll` | Mono — Unity 2018.4+ |
| `mono.dll` | Mono — older builds (Unity 2017 and earlier) |
| `GameAssembly.dll` | IL2CPP |

Polled with retries (up to 5 s) because the engine DLL may not be
loaded at our `DllMain` time. Rule of one backend per process — once
one is detected, the other backend's resolver is never touched.

---

## Resolver: lazy export binding

`MonoResolver` declares ~50 function-pointer typedefs and a single
`Resolver` struct holding one slot per typedef. First call to
`GetResolver()`:

1. Find the loaded Mono module's HMODULE.
2. For every export, `GetProcAddress` and store in the corresponding
   slot.
3. Log missing exports to the `init` log channel.
4. Cache the result in a static singleton.

`IsResolved()` returns true iff every load-bearing export is bound.
"Load-bearing" is the minimum needed for assembly enumeration — newer
exports added in later phases (`mono_runtime_invoke` for Phase 19,
etc.) are *optional*: if missing, the corresponding pipe handler
returns a wire error code, but the rest of the dumper keeps working.

### Adding a new export

1. Add `using fn_xxx = ret(*)(args);` typedef at the top of
   `MonoResolver.h`.
2. Add a slot in the `Resolver` struct.
3. In `MonoResolver.cpp`, `BIND_REQUIRED(xxx)` (load-bearing) or
   `BIND_OPTIONAL(xxx)` (used by a specific pipe handler that
   returns an error if missing).
4. Bump any tests that count required exports.

`mono_string_new`, `mono_string_chars`, `mono_string_length`,
`mono_object_unbox`, `mono_object_get_class`, `mono_runtime_invoke`
are all OPTIONAL — Phase 19 handlers gate on them. Older Mono
builds without one of these still get every Phase ≤ 18 feature.

---

## Domain → assemblies → images

`MonoDomain::EnumerateAssemblies()`:

```
mono_get_root_domain() → MonoDomain*
   ├─ mono_thread_attach(domain) → guarded by MonoThreadAttach RAII
   └─ mono_assembly_foreach(callback, &out)
          └─ for each MonoAssembly*:
               • mono_assembly_get_image(asm) → MonoImage*
               • mono_assembly_get_name(asm) → MonoAssemblyName*
               • mono_assembly_name_get_name(name) → const char*
               • mono_image_get_filename(image) → const char* (full path)
```

Output is the list returned by `getAssemblies` and the assemblies
count in `handshake`.

### Thread attach is mandatory

Every Mono call from our injected thread MUST be inside a
`MonoThreadAttach` guard. Mono uses thread-local state for the
current domain and GC root tracking; calling `mono_class_*` from an
unattached thread is undefined (commonly: silent corruption of the
GC root list, eventual heap walker crash).

The pipe server attaches once per RPC dispatch, not per Mono call —
nested attaches are no-ops via the resolver's reference count.

---

## Class walking

[MonoClassWalker](../dll/src/mono/MonoClassWalker.h) is the core of
`getClasses` / `getClassInfo`:

```
mono_image_get_table_info(image, MONO_TABLE_TYPEDEF) → MonoTableInfo*
mono_table_info_get_rows(table) → row count
   ├─ for each row in [0, rowCount):
   │     token = MonoTokenTypeDef + row             // 0x02000000 + idx
   │     class = mono_class_get(image, token)
   │     ↓
   │     • mono_class_get_namespace(class) → const char*
   │     • mono_class_get_name(class) → const char*
   │     • mono_class_get_parent(class) → MonoClass* (inheritance)
   │     • mono_class_get_nesting_type(class) → outer class for nested
   │     • mono_class_get_nested_types(class, &iter) → enumerate inner
   │
   └─ For getClassInfo (one specific class):
         • mono_class_get_fields(class, &iter) → MonoClassField*
         • mono_class_get_methods(class, &iter) → MonoMethod*
```

### Field iterator

```cpp
MonoClassField* field = nullptr;
void* iter = nullptr;
while ((field = res.class_get_fields(klass, &iter)) != nullptr) {
    name   = res.field_get_name(field);
    type   = res.field_get_type(field);
    offset = res.field_get_offset(field);     // bytes from start of MonoObject
    flags  = res.field_get_flags(field);       // FIELD_ATTRIBUTE_*
    typeName = res.type_get_name(type);        // free with res.free()
}
```

`mono_field_get_offset` returns the **storage offset within the
managed object** — directly usable as the field's address relative
to an instance pointer. Static fields' offsets are within the
class's static field block, accessed via `mono_class_vtable` →
`mono_vtable_get_static_field_data`.

### Method iterator

```cpp
MonoMethod* method = nullptr;
void* iter = nullptr;
while ((method = res.class_get_methods(klass, &iter)) != nullptr) {
    name   = res.method_get_name(method);
    token  = res.method_get_token(method);     // 0x06000000 + row
    flags  = res.method_get_flags(method, &iflags);
    addr   = res.compile_method(method);       // JIT compile → native address
}
```

`mono_compile_method` JIT-compiles on demand and returns the native
function pointer. **Wrapped in SEH** — JIT compile can fault on
abstract / generic-not-yet-instantiated methods, and we don't want
that to tear down the host. On fault, `addr = 0` and we mark the
method as `compiled = false` in the response.

---

## Method signature decoding (Phase 18)

Two-tier resolution for every `MonoMethod*`:

```cpp
MonoMethodSignature* sig = res.method_signature(method);
uint32_t paramCount      = res.signature_get_param_count(sig);

void* iter = nullptr;
MonoType* paramType = nullptr;
while ((paramType = res.signature_get_params(sig, &iter)) != nullptr) {
    paramTypeName = res.type_get_name(paramType);    // owned, free after use
}

MonoType* returnType = res.signature_get_return_type(sig);
returnTypeName       = res.type_get_name(returnType);

// Optional — older Mono builds may not export this
if (res.method_get_param_names != nullptr) {
    std::vector<const char*> names(paramCount);
    res.method_get_param_names(method, names.data());
}
```

The decoded `(System.Int32, System.String)` signature feeds:
- The Phase 18 overload-disambiguation `signatureMatch` substring
  filter (parens-bounded form is recommended — see
  [lessons-learned.md](lessons-learned.md) §"signatureMatch substring
  trap").
- The Phase 19 `MethodCallWindow`'s param row layout.
- The AOB AA-script's `humanSuffix` (e.g. `_2int`, `_string` for
  unique symbol names across overloads).

---

## `callMethod` (Phase 19) — runtime invocation

[MonoMethodInvoker](../dll/src/mono/MonoMethodInvoker.cpp) is the
RPC handler. Mirror of `IL2CPPMethodInvoker` (and meant to stay that
way). The shape:

```
1. Resolve the class triple via MonoClassWalker.
2. Single-pass overload resolver: walk methods, match name + (optional)
   signatureMatch substring. Returns MonoMethod* directly — no double
   walk.
3. Marshal args:
     - Value types: write into 8-byte slot, push &slot into params[i].
     - Reference types (string / address / null): push the pointer
       directly into params[i] (NOT &slot).
4. Wrap mono_runtime_invoke in __try / __except (all of it, including
   the trampoline) so a JIT crash becomes
   error: native_crash_in_runtime_invoke.
5. Decode return value:
     - void → no returnValue
     - bool / int* / uint* / float / double → mono_object_unbox + read
     - System.String → DecodeManagedString (UTF-16 → UTF-8)
     - reference types → returnValue: "0x..." (hex address)
6. Decode managed exception (when *exc != null):
     - mono_object_get_class(exc) → MonoClass* → namespace + name
     - exc + 0x18 → MonoString* _message → UTF-16 → UTF-8
     - exception: "Type: message"
```

### Marshaling rule (load-bearing)

```cpp
// Value type: write into a slot, pass &slot
int32_t slot = 42;
params[0] = &slot;

// Reference type: pass the pointer DIRECTLY (NOT &slot)
MonoString* str = mono_string_new(domain, "hello");
params[0] = str;     // NOT &str
```

This is the **non-uniform** convention used by BepInEx /
MelonLoader / Il2CppInterop. The Mono docs describe a "uniform"
convention (`&slot` for both) — that's wrong (or at minimum mismatched
with the actual x64 trampoline). See
[lessons-learned.md §"non-uniform marshaling"](lessons-learned.md#runtime_invoke-uses-non-uniform-parameter-marshaling-not-the-documented-uniform-convention)
for the war story.

### String marshaling

- **String arg in**: `mono_string_new(domain, utf8)` allocates a
  `MonoString*`; pointer goes directly into `params[i]`.
- **String return**: a `MonoObject*` with internal layout
  `[ vtable ptr | length(4) | char buffer ]`. We read length at
  `+0x10` and copy `length` UTF-16 chars from `+0x14`, then convert
  to UTF-8 via `WideCharToMultiByte`.

### Exception capture

Every managed exception inherits `System.Exception`. The `_message`
field is at offset `+0x18` (stable since Unity 2018+). When
`mono_runtime_invoke` populates the `MonoObject** exc` out-param:

```cpp
MonoClass* excClass = res.object_get_class(exc);
const char* ns      = res.class_get_namespace(excClass);
const char* name    = res.class_get_name(excClass);
MonoString* msg     = *(MonoString**)((uint8_t*)exc + 0x18);
std::string utf8    = DecodeManagedString(msg);
return "exception: \"" + std::string(ns) + "." + name + ": " + utf8 + "\"";
```

All of the above is SEH-protected — a corrupt exception object
becomes `error: native_crash_decoding_exception` instead of a host
crash.

---

## Mono-specific quirks

### `mono_string_new` takes a `MonoDomain*` (IL2CPP doesn't)

Unlike IL2CPP's `il2cpp_string_new(utf8)`, Mono's
`mono_string_new(domain, utf8)` requires the current domain. We
fetch it via the cached `mono_get_root_domain()` per call.

### Static field block

`mono_class_vtable(domain, class)` returns a `MonoVTable*`. The
block of static fields starts at
`mono_vtable_get_static_field_data(vtable)`. Each static field's
storage address is `staticData + field_offset` where `field_offset`
is `mono_field_get_offset(field)`.

For a class never instantiated (e.g. a pure utility class), the
vtable might not exist yet. `mono_class_vtable` triggers vtable
creation on first call, so we don't have to do anything special.

### `mono_compile_method` is the JIT entry

Mono is JIT — there's no AOT-`methodPointer` shortcut like IL2CPP
has. Every `findMethodInstances` call routes through
`mono_compile_method`, which can take a few ms on first call and
zero on subsequent calls (cached by the runtime).

### Generic methods

For generic methods that haven't been instantiated with concrete
type arguments, `mono_compile_method` either returns 0 or returns a
non-functional placeholder. We mark these as `compiled: false` in
the response and let the caller decide. Typical workflow: find a
non-generic overload of the same method, or wait for the game to
instantiate the generic before calling `findMethodInstances` again.

---

## Integration test surface

- **PowerShell smoke**: [tools/Test-CallMethod.ps1](../tools/Test-CallMethod.ps1)
  runs against any injected Mono target. Verified on BongoCat
  (Unity bdwgc) for every Phase 19 scenario.
- **CE Lua**: [scripts/unity_invoke.lua](../scripts/unity_invoke.lua)
  + dofile + run scenarios from CE's Lua console. Verified on
  BongoCat 2026-05-07 (4/4 green — see
  [STATUS.md §Live verifications](STATUS.md#live-verifications-carried-forward--still-green)).

## Related

- [il2cpp-internals.md](il2cpp-internals.md) — IL2CPP backend mirror.
- [lua-invoke.md](lua-invoke.md) — `callMethod` wire spec.
- [pipe-protocol.md](pipe-protocol.md) — every RPC the Mono backend
  exposes.
- [lessons-learned.md](lessons-learned.md) — non-uniform marshaling,
  signatureMatch trap, GC-root caveat.
