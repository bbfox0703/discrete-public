# IL2CPP Backend Internals

> Deep dive into `dll/src/il2cpp/`. Mirror of
> [mono-internals.md](mono-internals.md) — both backends share the
> same module shape and same Phase 17/18/19 features. The IL2CPP-
> specific bits are: AOT (no JIT), `global-metadata.dat` parsing,
> the metadata-version dispatcher (v24/v27/v29/v31), and the
> `MethodInfo.methodPointer` direct read.

---

## Module set

| Module | Role |
|---|---|
| [IL2CPPResolver](../dll/src/il2cpp/IL2CPPResolver.h) / [.cpp](../dll/src/il2cpp/IL2CPPResolver.cpp) | Lazy-bind every IL2CPP runtime export from `GameAssembly.dll`. Singleton; missing exports logged but non-fatal. |
| [IL2CPPTypes](../dll/src/il2cpp/IL2CPPTypes.h) | Opaque forward declarations (`Il2CppDomain*`, `Il2CppClass*`, `MethodInfo*`…). |
| [IL2CPPDomain](../dll/src/il2cpp/IL2CPPDomain.h) / [.cpp](../dll/src/il2cpp/IL2CPPDomain.cpp) | Walk the active domain → assemblies → images via `il2cpp_domain_get_assemblies`. |
| [IL2CPPClassWalker](../dll/src/il2cpp/IL2CPPClassWalker.h) / [.cpp](../dll/src/il2cpp/IL2CPPClassWalker.cpp) | `il2cpp_image_get_class_count` + `il2cpp_image_get_class(idx)`; field / method iterators. |
| [IL2CPPTypeParser](../dll/src/il2cpp/IL2CPPTypeParser.h) / [.cpp](../dll/src/il2cpp/IL2CPPTypeParser.cpp) | `Il2CppType*` → string. Thin wrapper. |
| [IL2CPPAddressFinder](../dll/src/il2cpp/IL2CPPAddressFinder.h) / [.cpp](../dll/src/il2cpp/IL2CPPAddressFinder.cpp) | Phase 17 — backward heap scan + class-pointer cache. |
| [IL2CPPMethodFinder](../dll/src/il2cpp/IL2CPPMethodFinder.h) / [.cpp](../dll/src/il2cpp/IL2CPPMethodFinder.cpp) | Phase 18 — overload enumeration + AOT pointers + signature decode. |
| [IL2CPPMethodInvoker](../dll/src/il2cpp/IL2CPPMethodInvoker.h) / [.cpp](../dll/src/il2cpp/IL2CPPMethodInvoker.cpp) | Phase 19 — `callMethod` RPC handler. |
| [IL2CPPThreadAttach](../dll/src/il2cpp/IL2CPPThreadAttach.h) | RAII guard around `il2cpp_thread_attach` / `il2cpp_thread_detach`. |
| [MetadataParser](../dll/src/il2cpp/MetadataParser.h) / [.cpp](../dll/src/il2cpp/MetadataParser.cpp) | `global-metadata.dat` parser — version dispatcher (v24/v27/v29/v31), type tables, string heap, field defaults. |
| [MetadataLayout](../dll/src/il2cpp/MetadataLayout.h) | Per-version struct layouts (cross-checked vs upstream Unity in `vendor/libil2cpp/`). |
| [IL2CPPMetadataRegistration](../dll/src/il2cpp/IL2CPPMetadataRegistration.h) / [.cpp](../dll/src/il2cpp/IL2CPPMetadataRegistration.cpp) | Phase 5 segment 2d (partial) — `Il2CppMetadataRegistration` AOB scan for type-aware const literal decode. |

---

## Module detection

`GameAssembly.dll` loaded → IL2CPP. Polled with retries up to 5 s
because the engine module may not be present at our `DllMain` time.
See [BackendDetector](../dll/src/shared/BackendDetector.h).

---

## Resolver: same shape as Mono

`IL2CPPResolver` mirrors `MonoResolver` one-for-one:

- ~50 function-pointer typedefs.
- `Resolver` struct holding one slot per typedef.
- Singleton accessor; missing exports logged but non-fatal.
- `IsResolved()` reports load-bearing-export coverage; optional
  exports (Phase 17/18/19 additions) gate per-handler.

The IL2CPP-specific exports we lean on:

- `il2cpp_image_get_class_count(image) → size_t` —
  IL2CPP exposes a count + index-based accessor (Mono uses table
  iteration). The class array is contiguous and indexable in O(1).
- `il2cpp_method_get_pointer(MethodInfo*) → void*` — **NOT exported
  on Unity 2021.3+**. We fall back to `*(void**)methodInfo` (the
  `methodPointer` field at offset 0) via SEH-protected struct read.
  See `ReadMethodPointer` in [IL2CPPClassWalker.cpp](../dll/src/il2cpp/IL2CPPClassWalker.cpp)
  and [lessons-learned.md](lessons-learned.md#il2cpp-method_get_pointer-is-not-exported-in-unity-20213).

---

## Domain → assemblies → images

```
il2cpp_domain_get() → Il2CppDomain*
   ├─ il2cpp_thread_attach(domain) → guarded by IL2CPPThreadAttach RAII
   └─ il2cpp_domain_get_assemblies(domain, &count) → const Il2CppAssembly**
          └─ for each:
               • il2cpp_assembly_get_image(asm) → const Il2CppImage*
               • il2cpp_image_get_name(image) → const char*
               • il2cpp_image_get_filename(image) → const char* (e.g. "Assembly-CSharp.dll")
```

Output drives `getAssemblies` and the assemblies count in
`handshake`.

---

## Class walking — runtime path

```
il2cpp_image_get_class_count(image) → count
   for index in [0, count):
       il2cpp_image_get_class(image, index) → Il2CppClass*
       ↓
       • il2cpp_class_get_namespace(class) → const char*
       • il2cpp_class_get_name(class) → const char*
       • il2cpp_class_get_parent(class) → Il2CppClass* (inheritance chain)
       • il2cpp_class_get_declaring(class) → outer class for nested types
       • il2cpp_class_get_nested_types(class, &iter) → enumerate inner

For getClassInfo (one specific class):
   • il2cpp_class_get_fields(class, &iter) → FieldInfo*
   • il2cpp_class_get_methods(class, &iter) → const MethodInfo*
```

### Field iterator

```cpp
FieldInfo* field = nullptr;
void* iter       = nullptr;
while ((field = res.class_get_fields(klass, &iter)) != nullptr) {
    name      = res.field_get_name(field);
    type      = res.field_get_type(field);
    offset    = res.field_get_offset(field);
    flags     = res.field_get_flags(field);
    typeName  = res.type_get_name(type);   // free with res.free()
}
```

`il2cpp_field_get_offset` returns the byte offset within the
`Il2CppObject` instance — directly usable, same as Mono.

### Method iterator

```cpp
const MethodInfo* method = nullptr;
void* iter               = nullptr;
while ((method = res.class_get_methods(klass, &iter)) != nullptr) {
    name  = res.method_get_name(method);
    token = res.method_get_token(method);
    addr  = ReadMethodPointer(method);     // export-or-fallback
}
```

`ReadMethodPointer`:
1. If `il2cpp_method_get_pointer` is bound, call it.
2. Otherwise SEH-protected read of `*(void**)method` (offset 0 in
   `MethodInfo` is the AOT entry pointer).

This is **not a JIT compile** — IL2CPP is AOT, so the entry point
already exists for every shipped method. Cost: a single pointer
read.

---

## Method signature decoding (Phase 18)

```cpp
uint32_t paramCount = res.method_get_param_count(method);
for (uint32_t i = 0; i < paramCount; ++i) {
    paramType  = res.method_get_param(method, i);
    paramName  = res.method_get_param_name(method, i);    // optional
    paramTypeName = res.type_get_name(paramType);
}
returnType = res.method_get_return_type(method);
```

All four signature exports are OPTIONAL — if missing, the response
ships with no signature breakdown but the address still resolves.
The Phase 19 `MethodCallWindow` requires them, so its handler
returns an error code if any is unbound.

Same `signatureMatch` substring trap applies as Mono — use parens-
bounded form (`"(System.Int32)"`) to disambiguate from longer types
like `UInt32` / `Int32[]`.

---

## `callMethod` (Phase 19) — runtime invocation

[IL2CPPMethodInvoker](../dll/src/il2cpp/IL2CPPMethodInvoker.cpp).
Identical shape to `MonoMethodInvoker` (and meant to stay that way):

```
1. Resolve the class triple via IL2CPPClassWalker.
2. Single-pass overload resolver: walk methods, match name +
   (optional) signatureMatch substring. Returns MethodInfo* directly.
3. Marshal args (non-uniform — same as Mono):
     - Value types: write into 8-byte slot, push &slot.
     - Reference types: push pointer directly.
4. Wrap il2cpp_runtime_invoke in __try / __except so a JIT/AOT
   crash becomes error: native_crash_in_runtime_invoke.
5. Decode return value:
     - void → no returnValue
     - bool / int* / uint* / float / double → il2cpp_object_unbox + read
     - System.String → DecodeManagedString (UTF-16 → UTF-8)
     - reference types → returnValue: "0x..." (hex address)
6. Decode managed exception (when *exc != null):
     - il2cpp_object_get_class(exc) → Il2CppClass* → namespace + name
     - exc + 0x18 → Il2CppString* _message → UTF-16 → UTF-8
     - exception: "Type: message"
```

Difference from Mono:

- `il2cpp_string_new(utf8)` takes only the UTF-8 string — no domain
  param.
- `runtime_invoke` is the AOT trampoline; no JIT compile path.
  Address field of the response is populated via the pre-resolved
  `MethodInfo.methodPointer` (no `compile_method` analog).
- Exception object's `_message` lives at `Il2CppException + 0x18` —
  same offset as Mono because both inherit `System.Exception` with
  the same managed layout.

See [lua-invoke.md](lua-invoke.md) for the wire spec.

---

## Metadata parser (`global-metadata.dat`)

Phase 5 + Phase 14c. Parses the metadata blob in memory or from
disk (the latter via the [`metadata-cli`](metadata-cli.md) standalone
tool).

### Magic + version dispatch

```
Header[0..3]   = 0xFAB11BAF  (sanity magic)
Header[4..7]   = uint32 version
```

Versions we ship: **v24** (Unity 2018), **v27** (Unity 2020), **v29**
(Unity 2021), **v31** (Unity 2022/2023).

The dispatcher in [MetadataParser.cpp](../dll/src/il2cpp/MetadataParser.cpp)
selects a per-version layout from
[MetadataLayout.h](../dll/src/il2cpp/MetadataLayout.h). Each layout
is a struct of byte offsets / field sizes for that version's
`Il2CppGlobalMetadataHeader` + `Il2CppTypeDefinition` /
`Il2CppFieldDefinition` records.

### Cross-check vs upstream Unity

Each layout is verified against the upstream Unity source in
`vendor/libil2cpp/<version>/` — a side-by-side comparison kept for
exactly this purpose. See [il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md)
for the full audit.

When adding a new version (v32, v38, …), the workflow is:
1. Drop the upstream `il2cpp-metadata.h` for the new version into
   `vendor/libil2cpp/`.
2. Diff against the closest existing version in `MetadataLayout.h`.
3. Write the new struct layout, gated by an else-if branch in the
   version dispatcher.
4. Verify with `metadata-cli info <new-version-game>.dat` — should
   print non-zero `type_definition_count`.
5. Verify with the live runtime — `getMetadataStats` should match
   the CLI's `stats` output for the same file.

### Tables we parse

| Table | Used by | Status |
|---|---|---|
| `TypeDefinitions` | `getClasses`, `getClassInfo` | ✅ v24/v27/v29/v31 |
| `FieldDefinitions` | `getClassInfo` field list | ✅ v24/v27/v29/v31 |
| `StringLiterals` | `walkInstance` for literal-string fields | ✅ all versions (string heap path is version-agnostic) |
| `FieldDefaults` | Enum decode + `metadata-cli type` | ✅ v24/v27/v29/v31 |
| `MethodDefinitions` | (none yet — `findMethodInstances` reads runtime tables) | ⏳ Deferred |

### `Il2CppMetadataRegistration` AOB scan

Phase 5 segment 2d (partial / not-yet-completed). Goal: cross-link
the on-disk metadata index ↔ runtime `Il2CppClass*` so non-enum
const literals (`const int kFoo = 42`) in
`il2cppGetMetadataTypeFields` can be type-aware-decoded. Today, only
enum defaults are type-aware — non-enum constants get the catch-all
`int32` decode. Half-day of work to finish; non-blocking.

See [STATUS.md "Open follow-ups"](STATUS.md#open-follow-ups-not-blocking-drip-feed)
for context.

---

## IL2CPP-specific quirks

### `il2cpp_method_get_pointer` not exported on Unity 2021.3+

Already mentioned above. The fallback is a SEH-protected
`*(void**)methodInfo` read at offset 0, which is `methodPointer` in
every IL2CPP version we've seen. Don't reintroduce the export-only
path — it would silently regress Unity 2021.3+.

### Stripped / obfuscated metadata

Some games (especially mobile ports + protected commercial titles)
ship with stripped class names — `<Module>.A`, `<Module>.B`, etc.
The dumper still works (offsets and AOT pointers are preserved), but
the class browser becomes painful.

`getMetadataStats` (live RPC) and `metadata-cli stats` (offline) both
emit a verdict (`clean` / `partial_strip` / `heavy_strip`) with the
underlying single-char / two-char ratio. UI surfaces a banner above
the class browser when verdict is `heavy_strip`.

### Metadata version 38 (Unity 2024+)

Uses a section-based header instead of fixed field offsets. Not
shipped yet — `metadata-cli info` reports `type_definition_count: 0`
on v38 files and falls back to the strings-only path. Adding it is a
medium-effort task: write the section table parser, then per-version
layouts as before.

---

## Integration test surface

- **PowerShell smoke**: [tools/Test-CallMethod.ps1](../tools/Test-CallMethod.ps1)
  / [Test-FindMethodInstances.ps1](../tools/Test-FindMethodInstances.ps1)
  / [Test-WhatIsAt.ps1](../tools/Test-WhatIsAt.ps1) all run against
  any injected IL2CPP target. Most regressions are caught by the
  Phase 19 `Test-CallMethod.ps1` — its 6 scenarios exercise the full
  marshaling path.
- **CE Lua**: same `unity_*.lua` scripts as Mono. The `whatIsAt` /
  `findMethodInstances` / `callMethod` paths are byte-identical at
  the wire layer between backends.
- **Test matrix**: 6+ IL2CPP games covering all four metadata
  versions — see [test-matrix.md](test-matrix.md).

## Related

- [mono-internals.md](mono-internals.md) — Mono backend mirror.
- [il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md) —
  per-version layout audit.
- [metadata-cli.md](metadata-cli.md) — offline tool that uses the
  same parser.
- [lua-invoke.md](lua-invoke.md) — `callMethod` wire spec.
- [lessons-learned.md](lessons-learned.md) — `method_get_pointer`
  deprecation, non-uniform marshaling, signatureMatch trap.
