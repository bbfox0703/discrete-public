# IL2CPP Metadata — Layout Cross-check

`MetadataLayout.h` was originally derived empirically (hex-dumping `global-metadata.dat`
files and locating canonical TypeDef tokens). This document records the results
of cross-checking those layouts against Unity's official IL2CPP source, now
available in `vendor/libil2cpp/`. The script `sync_vendor.ps1` clones a community
mirror of the per-Unity-version `libil2cpp/` source trees pulled from Unity
installer archives.

**Verdict**: every field, byte offset, type width, and struct size in
`MetadataLayout.h` matches the upstream definition for v24, v27, v29, and v31.
No bugs. Two minor documentation-only deltas worth recording (see *Notes* below).

---

## TypeDefinition layouts

| Version | Source file | Stride | byrefTypeIndex? | Aliased to |
|---------|-------------|--------|-----------------|------------|
| v24 (Unity 2018.4 - 2019.x) | [`Unity_2019.4/2019.4.40f1/il2cpp-metadata.h:150`](../vendor/libil2cpp/libil2cpp/Unity_2019.4/2019.4.40f1/il2cpp-metadata.h) | **92 B** | yes (offset +12) | `TypeDefinition_v24` |
| v27 (Unity 2020.x) | [`Unity_2020.3/2020.3.31f1/vm/GlobalMetadataFileInternals.h:58`](../vendor/libil2cpp/libil2cpp/Unity_2020.3/2020.3.31f1/vm/GlobalMetadataFileInternals.h) | **88 B** | no | `using = TypeDefinition_v29` |
| v29 (Unity 2021.x - 2022.1) | [`Unity_2021.3/2021.3.39f1/vm/GlobalMetadataFileInternals.h:66`](../vendor/libil2cpp/libil2cpp/Unity_2021.3/2021.3.39f1/vm/GlobalMetadataFileInternals.h) | **88 B** | no | `TypeDefinition_v29` (canonical) |
| v31 (Unity 2022.2+) | [`Unity_2022.3/2022.3.32f1/vm/GlobalMetadataFileInternals.h:66`](../vendor/libil2cpp/libil2cpp/Unity_2022.3/2022.3.32f1/vm/GlobalMetadataFileInternals.h) | **88 B** | no | `using = TypeDefinition_v29` |
| Unity 6 (still v29) | [`Unity_6000.0/6000.0.5f1/vm/GlobalMetadataFileInternals.h:66`](../vendor/libil2cpp/libil2cpp/Unity_6000.0/6000.0.5f1/vm/GlobalMetadataFileInternals.h) | **88 B** | no | covered by v29 |

The v24 → v27 layout change is exactly what `MetadataLayout.h` documents:
removal of `byrefTypeIndex` (one int32 dropped between `byvalTypeIndex` and
`declaringTypeIndex`).

### Field-by-field check (v29 reference)

| `MetadataLayout.h` (`TypeDefinition_v29`) | Upstream `Il2CppTypeDefinition` | Match |
|------------------------------------------|---------------------------------|-------|
| `int32 nameIndex` | `StringIndex nameIndex` (= int32) | ✓ |
| `int32 namespaceIndex` | `StringIndex namespaceIndex` | ✓ |
| `int32 byvalTypeIndex` | `TypeIndex byvalTypeIndex` | ✓ |
| `int32 declaringTypeIndex` | `TypeIndex declaringTypeIndex` | ✓ |
| `int32 parentIndex` | `TypeIndex parentIndex` | ✓ |
| `int32 elementTypeIndex` | `TypeIndex elementTypeIndex` | ✓ |
| `int32 genericContainerIndex` | `GenericContainerIndex` | ✓ |
| `uint32 flags` | `uint32_t flags` | ✓ |
| `int32 fieldStart` | `FieldIndex fieldStart` | ✓ |
| `int32 methodStart` | `MethodIndex methodStart` | ✓ |
| `int32 eventStart` | `EventIndex eventStart` | ✓ |
| `int32 propertyStart` | `PropertyIndex propertyStart` | ✓ |
| `int32 nestedTypesStart` | `NestedTypeIndex nestedTypesStart` | ✓ |
| `int32 interfacesStart` | `InterfacesIndex interfacesStart` | ✓ |
| `int32 vtableStart` | `VTableIndex vtableStart` | ✓ |
| `int32 interfaceOffsetsStart` | `InterfacesIndex interfaceOffsetsStart` | ✓ |
| 8× `uint16 *_count` | 8× `uint16_t *_count` | ✓ |
| `uint32 bitfield` | `uint32_t bitfield` | ✓ |
| `uint32 token` | `uint32_t token` | ✓ |

All upstream `Index` typedefs collapse to `int32_t`, so the field widths line up
exactly. Total: 16 × 4 + 8 × 2 + 2 × 4 = **88 bytes** matches `static_assert`.

### v24 inserts `byrefTypeIndex`

```c
// Unity 2019.4.40f1, il2cpp-metadata.h:150
typedef struct Il2CppTypeDefinition {
    StringIndex nameIndex;
    StringIndex namespaceIndex;
    TypeIndex   byvalTypeIndex;
    TypeIndex   byrefTypeIndex;     // <-- present in v24, removed v27+
    TypeIndex   declaringTypeIndex;
    ...
};
```

`MetadataLayout.h::TypeDefinition_v24` mirrors this with one extra int32 in the
same position. **17 × 4 + 8 × 2 + 2 × 4 = 92 bytes** matches `static_assert`.

---

## FieldDefinition / FieldDefaultValue

Identical across v24, v27, v29, v31 — verified at v24 (`il2cpp-metadata.h:198, 205`)
and v31 (`Unity_2022.3/2022.3.32f1/vm/GlobalMetadataFileInternals.h:113, 120`).

```c
struct Il2CppFieldDefinition {
    StringIndex   nameIndex;
    TypeIndex     typeIndex;
    uint32_t      token;
};                                  // 12 bytes

struct Il2CppFieldDefaultValue {
    FieldIndex            fieldIndex;
    TypeIndex             typeIndex;
    DefaultValueDataIndex dataIndex;
};                                  // 12 bytes
```

`MetadataLayout.h` aliases v24/v31 onto the v29 definitions. Correct — the
single source of truth in our header is fine.

---

## Header prefix

Our `HeaderV29` covers the leading **42 int32s = 168 bytes** of
`Il2CppGlobalMetadataHeader`, terminating at `typeDefinitions{Offset,Size}`.
That prefix is identical in v27, v29, v31, and Unity 6 (verified in each
upstream source). The trailing fields (`imagesOffset`, `assembliesOffset`,
`fieldRefsOffset`, `attributeDataOffset`, `unresolvedVirtualCallParameter*`,
`windowsRuntime*`, `exportedTypeDefinitions*`) diverge between versions, but
the metadata-cli code path doesn't read them — so there's no exposure.

The v24 header walks the same 42 int32 prefix in the same order with one
naming delta, see **Notes** below.

---

## Notes — documentation-only deltas

### 1. v24 uses `XxxCount` where v29 uses `XxxSize`

In `Unity_2019.4/2019.4.40f1/il2cpp-metadata.h:375`, the header pairs are named
`stringLiteralOffset/stringLiteralCount`, `stringLiteralDataOffset/stringLiteralDataCount`,
`stringOffset/stringCount`, etc. From v27 onwards these became `*Size`.

The byte layout is identical — both are int32 — and our `HeaderV29` reading
v24 data interprets the `Size` field as it always has. The only places our code
could be wrong are:

- Comments in user-facing JSON output mentioning "size" when the value is
  actually a "count" (entry count, not byte count). Looking at metadata-cli's
  `info` subcommand output, we already use `string_literal_count` —
  matches the v24 semantic. No correction needed.
- Internal field names in `HeaderV29`. Renaming would be cosmetic; the
  reading code doesn't care. Leaving as-is.

Action: none. Documented here so a future investigator doesn't get confused.

### 2. Unity 6 (6000.0.x) ships IL2CPP v29 — not v31

Per `vendor/libil2cpp/IL2CPP_Version.md` and the upstream README: *"Latest
version: Unity 6000.0.5f1 with IL2CPP 29"*. Confirmed by reading
`Unity_6000.0/6000.0.5f1/vm/GlobalMetadataFileInternals.h` — header and
TypeDefinition layout identical to v29.

So the version boundaries we currently document need a small clarification:

| Version | Unity range covered |
|---------|---------------------|
| v24 | 2018.4 - 2019.x |
| v27 | 2020.x |
| v29 | **2021.x - 2022.1, AND Unity 6 (6000.0.x)** |
| v31 | 2022.2 - 2023.x |

Unity 6 reverted to v29 (likely a metadata-format stability decision).
metadata-cli already handles v29 correctly, so this is informational — but it
means an Il2CPP-based Unity 6 game would be exercising the v29 dispatch, not a
new v32 path. The note in `MetadataLayout.h` claiming v31 covers "Unity 2022.2+"
is technically true for 2022.2/2022.3 but doesn't account for 6000.0 falling
back to v29.

Action: comment update in `MetadataLayout.h` (header block) and a sentence in
the next iteration of `il2cpp-internals.md` if it gets updated.

---

## How this was verified

1. `vendor/libil2cpp/` populated by `sync_vendor.ps1` (already wired).
2. Picked the latest patch in each major Unity version that maps to a
   distinct metadata version:
   - Unity 2019.4.40f1 (latest v24 patch we care about — SaGa F1R / FFVI PR territory)
   - Unity 2020.3.31f1 (v27)
   - Unity 2021.3.39f1 (v29 — exact version of SaGa F2R)
   - Unity 2022.3.32f1 (v31)
   - Unity 6000.0.5f1 (still v29 — confirms our dispatch covers Unity 6)
3. Located `Il2CppTypeDefinition` in either `il2cpp-metadata.h` (v24) or
   `vm/GlobalMetadataFileInternals.h` (v27+) for each.
4. Field-by-field compare against `dll/src/il2cpp/MetadataLayout.h`.
5. Cross-checked `Il2CppFieldDefinition`, `Il2CppFieldDefaultValue`, and the
   `Il2CppGlobalMetadataHeader` prefix.
6. No mismatches found.

The whole pass took ~10 minutes once the layout was clear. To re-verify after
a new Unity release: bump `sync_vendor.ps1`, pull the new
`Unity_<version>/<patch>/vm/GlobalMetadataFileInternals.h`, eyeball the
`Il2CppTypeDefinition` against `MetadataLayout.h`. If field count / order /
widths still match, no code changes needed.

---

**Outcome**: `MetadataLayout.h` is byte-for-byte canonical. The empirical
hex-dumping methodology used originally produced exactly the right answer.
Future v32+ targets can start from this same diffing approach without needing
to re-dump anything.
