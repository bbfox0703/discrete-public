# Export Formats — CE XML / CSX / SDK Header

> The UI exports the currently-selected class as one of three formats.
> All three live in [`ui/UnityDumpUI.Core/Services/Export/`](../ui/UnityDumpUI.Core/Services/Export)
> and run client-side off the cached `getClassInfo` response — no DLL
> round-trip per export.

---

## TL;DR — which format when?

| Format | What it's for | File extension |
|---|---|---|
| **CE XML (.CT)** | Drop-in cheat-table address list. Open directly in Cheat Engine. Live mode embeds real instance addresses → cheats work immediately without scanning. | `.CT` |
| **CSX (Structure Dissect)** | CE's "Structure Dissect" feature. Snapshot the class layout for offset-relative reuse across many instances of the same type. | `.csx` |
| **SDK header (.h)** | Plain C/C++ struct definition with `+0xNN` offset comments. For external consumers (mods, native tools, RE notes). | `.h` |

UI entry points (Browser tab → class context menu): **Export .h…**,
**Export .csx…**, **Export .CT…**, plus **Copy CE XML** /
**Copy CE field** for clipboard variants.

---

## 1. CE XML (.CT)

[CeXmlExporter.cs](../ui/UnityDumpUI.Core/Services/Export/CeXmlExporter.cs)

### Shape

```xml
<CheatTable>
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>"MainGame.PLY_WORK @ 0x..."</Description>
      <ShowAsHex>1</ShowAsHex>
      <GroupHeader>1</GroupHeader>
      <Address>00007FF6...</Address>
      <CheatEntries>
        <CheatEntry>
          <Description>"+0x10  HP : Int32"</Description>
          <VariableType>4 Bytes</VariableType>
          <Address>00007FF6...+10</Address>
        </CheatEntry>
        <CheatEntry GroupHeader="1">
          <Description>"+0x18  weapon : Item"</Description>
          <VariableType>4 Bytes</VariableType>
          <Address>00007FF6...+18</Address>
          <Offsets><Offset>0</Offset></Offsets>
          <CheatEntries>
            <!-- pointer drilldown — Item's fields rooted at *(addr+0x18) -->
          </CheatEntries>
        </CheatEntry>
        ...
      </CheatEntries>
    </CheatEntry>
  </CheatEntries>
</CheatTable>
```

Matches a CE "Save selected" round-trip exactly — opens cleanly in
CE 7.5+ without warnings.

### Live mode vs structure mode

- **Structure mode** (default for **Export .CT…**): root `<Address>`
  is the user-supplied **base** (e.g. `[GameAssembly.dll+12345678]`),
  fields are offsets from that base. Reusable across instances.
- **Live mode** (toggle "Use live data in CE XML" — Phase 10f): root
  `<Address>` is the **actual instance pointer** captured at export
  time. Pointer fields use the dereferenced child address; array
  slots get their slot data address. Cheats work the moment the .CT
  loads — no scan required. Good for one-shot debugging.

The right-click "Copy CE XML rooted here" / "Copy CE field" actions
on the Inspector grid (Phase 11.5) always operate in live mode rooted
at the right-clicked node — same code path, just scoped to a subtree.

### Pointer / array / value-struct routing

Each field is classified by `WalkedFieldKind` and rendered:

- **Scalar** (`int`, `float`, `bool`, `enum`): `<VariableType>`
  matching CE's primitive types (`4 Bytes`, `Float`, `Byte`, etc.).
  Enum fields use the underlying integer type with `<Description>`
  carrying the enum-symbol annotation.
- **Pointer** (managed reference type): wrapped in a `GroupHeader`
  entry with `<Offsets><Offset>0</Offset></Offsets>` for the deref.
  If `Drilldown depth > 0`, a child `<CheatEntries>` block walks the
  pointed-to class.
- **Array / List<T>**: indexed children at slot offsets (data-start
  varies — array header, List<T>'s `_items` array). `Array element
  limit` caps the number of slots emitted (default 16).
- **Value struct**: inline child fields with offsets relative to the
  parent (no pointer indirection).
- **String** (`System.String`): UTF-16 char buffer at `+0x14` after
  the standard string header.

### Options panel

| Option | Default | Effect |
|---|---|---|
| `Drilldown depth` | 1 | How many pointer dereferences to follow. 0 = pointer fields stop at the address. |
| `Array element limit` | 16 | Max array slots per array field. |
| `Collapse pointer/array nodes` | off | Render pointer / array as a single line instead of expanding children. |
| `Use live data` | off | Switch from structure to live mode (real addresses + walked array elements). |
| `CE XML base address` | inferred | Override for structure mode's root `<Address>`. |

---

## 2. CSX (Structure Dissect)

[CsxExporter.cs](../ui/UnityDumpUI.Core/Services/Export/CsxExporter.cs)

### Shape

```xml
<Structures>
  <Structure Name="MainGame.PLY_WORK"
             AutoFill="0" AutoCreate="1" DefaultHex="0"
             AutoDestroy="0" DoNotSaveLocal="0" RLECompression="1"
             AutoCreateStructsize="1024">
    <Elements>
      <Element Offset="0"  Vartype="Pointer"  Bytesize="8"
               OffsetHex="00000000" Description="vtable" DisplayMethod="unsigned integer"/>
      <Element Offset="16" Vartype="4 Bytes"  Bytesize="4"
               OffsetHex="00000010" Description="HP : Int32"/>
      <Element Offset="20" Vartype="4 Bytes"  Bytesize="4"
               OffsetHex="00000014" Description="MAX_HP : Int32"/>
      <Element Offset="24" Vartype="Pointer"  Bytesize="8"
               OffsetHex="00000018" Description="weapon : Item">
        <Structure Name="Item">
          <Elements>
            <!-- Item's own fields, rooted at offset 0 within the pointed-to allocation -->
          </Elements>
        </Structure>
      </Element>
    </Elements>
  </Structure>
</Structures>
```

Matches a CE "Save structure" round-trip. Open in **Memory View →
Tools → Dissect data/structures…** then **File → Open dissect
data**.

### When to use

CSX represents the **schema** of a class, with offsets relative to
the start of the allocation. Once loaded into CE, you point Dissect
Data at any instance address and CE renders the same field layout.

CE XML (.CT) is the right choice when you want a cheat-table that
just opens and works. CSX is the right choice when you want CE's
"explore this struct over many instances" workflow.

### Live mode caveat

CSX is structurally an **offset-relative** format. There's no
"absolute address" notion to embed — so live mode doesn't apply
here. The "Use live data" toggle on the Browser only affects CE XML.

---

## 3. SDK Header (.h)

[SdkHeaderExporter.cs](../ui/UnityDumpUI.Core/Services/Export/SdkHeaderExporter.cs)

### Shape

```cpp
// auto-generated by discrete — do not edit by hand
#pragma once

#include <cstdint>

/* inherited from System.Object */
// (no instance fields)

/* inherited from MonoBehaviour */
// (no instance fields)

struct PLY_WORK {
    void* __vtable;          // +0x00
    int32_t HP;              // +0x10
    int32_t MAX_HP;          // +0x14
    void*   weapon;          // +0x18  // managed pointer to Item
    int32_t LV;              // +0x20
    // ... etc
};
static_assert(sizeof(PLY_WORK) >= 0x80, "PLY_WORK size mismatch");
```

### Field rendering

- **Primitives**: `int32_t`, `uint64_t`, `float`, etc. via
  [UnityTypeMap.cs](../ui/UnityDumpUI.Core/Services/Export/UnityTypeMap.cs).
- **Reference types**: `void*` with `// managed pointer to <Type>`
  comment.
- **Value structs**: `<TypeName> field` (assumes the consumer has the
  struct definition elsewhere — standalone .h files don't recursively
  emit nested structs).
- **Static fields**: skipped (don't sit in the instance).
- **Literal fields** (`const`): skipped.

### Inheritance

When `GetClassInfoResult.Parent` is populated, parent fields are
emitted first under a `/* inherited from <ParentNamespace>.<ParentName> */`
banner, oldest base first. Caller (the Browser VM) is responsible
for fetching parent class info via `BackendAdapter.GetClassInfoAsync`
and threading it through as the `ancestors` list (oldest → newest).

This matches what dnSpy / IDA's class-explorer would emit for the
same class.

### When to use

- **External RE tools** (IDA, Ghidra, x64dbg) that consume C headers.
- **Native mod authors** building patches that need to know the layout.
- **Documentation** for a game's class hierarchy.
- **Diff-checking** layouts across game versions (text-diffable).

---

## Common helpers

### `UnityTypeMap.cs`

Maps managed type names (`System.Int32`, `UnityEngine.Vector3`) to:

- CE primitive `<VariableType>` strings (`4 Bytes`, `Pointer`, `Float`).
- C/C++ scalar types (`int32_t`, `void*`, `float`).
- Byte sizes for `<Bytesize>` / `static_assert`.

Fallback for unknown types: `Pointer` / `void*` / 8 bytes (assumes
managed reference). Conservative — better than guessing wrong on a
real value type.

### `IClassInfoLookup.cs`

Async lookup interface for fetching peer class info during drilldown
(CE XML pointer expansion, CSX nested structures). Backed by
`BackendAdapter` in production, mockable for tests.

### `AobAaScriptGenerator.cs`

Phase 18c — emits the AOB AA script consumed by
`MethodOverloadsViewModel.CopyAobScriptAsync` /
`CreateAaScriptInCeAsync`. Not a class-export format; included here
because it lives in the same folder.

---

## Wire format vs export format

The UI exporters consume **already-walked** data from
`getClassInfo` / `walkInstance` / `walkArray` (cached in the
ViewModels). The DLL itself does NOT have an exporter — DLL-side
`FieldExporter` + `exportCe*` pipe commands were deliberately
skipped (Phase 10): the UI does it locally, which is cheaper, more
flexible, and lets the user adjust drilldown depth / array limit /
live mode without round-tripping the DLL.

The three formats are pure functions of the cached class info +
options + (for CE XML live mode) the optional walked-instance tree.

## Source map

| File | What |
|---|---|
| [CeXmlExporter.cs](../ui/UnityDumpUI.Core/Services/Export/CeXmlExporter.cs) | `.CT` cheat-table emitter — structure & live modes |
| [CsxExporter.cs](../ui/UnityDumpUI.Core/Services/Export/CsxExporter.cs) | `.csx` Structure Dissect emitter |
| [SdkHeaderExporter.cs](../ui/UnityDumpUI.Core/Services/Export/SdkHeaderExporter.cs) | `.h` C/C++ struct emitter |
| [ExportOptions.cs](../ui/UnityDumpUI.Core/Services/Export/ExportOptions.cs) | Shared option DTO (depth, array limit, collapse, live, base address) |
| [UnityTypeMap.cs](../ui/UnityDumpUI.Core/Services/Export/UnityTypeMap.cs) | Type-name → CE / C / size mapping |
| [IClassInfoLookup.cs](../ui/UnityDumpUI.Core/Services/Export/IClassInfoLookup.cs) | Drilldown peer lookup interface |
| [AobAaScriptGenerator.cs](../ui/UnityDumpUI.Core/Services/Export/AobAaScriptGenerator.cs) | Phase 18c AOB AA-script emitter |
