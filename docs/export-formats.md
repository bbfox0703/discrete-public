# Export Formats — CE XML / CSX / SDK Header / Debugger symbols

> The UI exports the currently-selected class as one of three single-class
> formats, plus a whole-process debugger-symbol dump driven from the
> Tools menu. All exporters live in
> [`ui/UnityDumpUI.Core/Services/Export/`](../ui/UnityDumpUI.Core/Services/Export);
> the single-class ones run client-side off the cached `getClassInfo`
> response (no per-export DLL round-trip), the debugger-symbol path
> takes one `dumpIl2cppSymbols` round-trip for the whole process.

---

## TL;DR — which format when?

| Format | What it's for | File extension |
|---|---|---|
| **CE XML (.CT)** | Drop-in cheat-table address list. Open directly in Cheat Engine. Live mode embeds real instance addresses → cheats work immediately without scanning. | `.CT` |
| **CSX (Structure Dissect)** | CE's "Structure Dissect" feature. Snapshot the class layout for offset-relative reuse across many instances of the same type. | `.csx` |
| **SDK header (.h)** | Plain C/C++ struct definition with `+0xNN` offset comments. For external consumers (mods, native tools, RE notes). | `.h` |
| **x64dbg `.dd64`** | Debugger label database — class metadata pointers + method entry points, module-relative to `GameAssembly.dll`. IL2CPP only. | `.dd64` |
| **Ghidra symbol text** | Same payload as `.dd64` but in `ImportSymbolsScript.py` format (absolute address). IL2CPP only. | `.txt` |
| **IDA `.idc`** | Same payload as `.dd64` but as a `MakeName` / `MakeComm` IDC script. IL2CPP only. | `.idc` |

UI entry points: single-class exports live on the Browser tab's class
context menu (**Export .h…**, **Export .csx…**, **Export .CT…**, plus
**Copy CE XML** / **Copy CE field**). Whole-process debugger exports
live on the connection bar's **Tools** flyout (**Export Symbols →
x64dbg / Ghidra / IDA**).

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
| `Reserve a minimum string length` + `Min string length (chars)` | off / 32 | Floor every CE `String` `<Length>` to N characters. A currently-short string still reserves enough UTF-16 buffer to display a longer value under a different save; a string already ≥ N is left as-is. `ZeroTerminate=1` keeps CE stopping at the real NUL. Threaded through the single `EmitStringDerefGroup` choke point, so it covers instance-field, flattened-struct-member, and array-element string surfaces. |
| `Fill empty array slots` | off | Assume every array slot holds data even when a slot is a null pointer or the whole collection is empty/unallocated (the game hasn't loaded that record yet). See below. |

#### `Fill empty array slots` (live mode)

Different save files populate different slots, so a live array is often
heterogeneous — some slots point at real instances, some are null (`0x0`), and
the whole list may not be allocated yet. Off, the exporter emits only what is
live now (null slots → bare `[i]` leaf, empty list → one watchable pointer). On,
every slot is templated so the CE table carries the full per-element structure
for any save:

- **Null pointer slots** in a populated array are filled from the **element
  class's metadata** (`GetClassInfoAsync` — no live object needed), addressed
  with the same relative deref chain (`+dataOff+i*stride`, `Offsets=[0]`) a
  populated sibling uses. Because no live pointer value ever enters the table,
  the slot re-resolves the moment the game writes an instance into it.
- **Polymorphic collections** — a collection declared as base type `T` that
  actually holds subclass instances (e.g. `LibraryItemData[]` holding `Book`
  entries with more fields). Walking each populated slot by its object header
  shows each slot's *real* runtime type, so a `Book` slot (many fields) sits
  next to a base slot (few) as structurally different rows. When type variance
  is detected across the populated slots, **every** slot is instead templated
  from the **richest** concrete type found (the one with the most fields), so
  the whole array is uniform and any slot resolves for a fully-completed save
  where it might hold the richest subclass. A **monomorphic** array (every slot
  exactly the declared type) keeps its faithful per-slot live walk (which also
  preserves live values and resolves generic instantiations metadata can't).
- **Empty / unallocated collections** (null handle or `List._items == null`) are
  filled with `Array element limit` metadata slots so the table is ready before
  the game builds the list.
- **String slots** reserve the **max** length observed across the array (floored
  by `Min string length` when that option is on), so the array reads uniformly.
- **Framework / engine reference** element types (`List<Transform>`,
  `SpriteRenderer[]`, `Unity.Entities.*`, `System.*` …) are **never** filled or
  fanned out — the framework skip always wins.

Metadata-filled bodies walk the base-class chain (inherited fields first, like
the live walk). Because a metadata `FieldEntry` carries no per-field
enum/value-type flag, an ambiguous game-type member is classified from the
member class's **base type** (`getClassInfo`'s `parent`): `System.Enum` → inline
4-byte signed leaf, `System.ValueType` → inline group (no deref), otherwise a
reference → pointer-deref group; each recurses within `Drilldown depth`. Nested
collections inside a metadata body are emitted as watchable leaves (a metadata
layout lacks the live SZARRAY data offset, so a nested-array chain is never
fabricated). Only **reference** element classes are filled for the empty/
unallocated-collection case (exact pointer stride); a value-struct element array
whose handle is null falls back to a single leaf rather than risk a mis-strided
inline fill. Filled slots are emitted collapsed when `Collapse pointer/array
nodes` is on, so slots that stay null don't visually dominate the table.

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

### Whole-game variant — `il2cpp.h` batch export

Phase C of [il2cppdumper-comparison.md](il2cppdumper-comparison.md).
The single-class flow above scales poorly when you want every class
at once — call it 5K-200K times per game. The Tools-menu **Export
il2cpp.h (whole game)…** entry walks every user assembly in one go
and concatenates an Il2CppDumper-equivalent `il2cpp.h`, with one
big difference: method **AOT addresses** are inlined as trailing
comments per class. Il2CppDumper's own `il2cpp.h` only has field
layouts (it puts method addresses in `dump.cs` / `script.json`).

[Il2cppHeaderBatchExporter.cs](../ui/UnityDumpUI.Core/Services/Export/Il2cppHeaderBatchExporter.cs)

Shape:

```cpp
// auto-generated by Discrete — whole-game IL2CPP header
#pragma once
#include <cstdint>

// ============================================================
// Assembly: Assembly-CSharp  (3128 classes)
// ============================================================

// Class: Game.Player
// inherits UnityEngine.MonoBehaviour
#pragma pack(push, 1)
struct Game_Player {
    int32_t HP;          // +0x10  System.Int32
    int32_t MAX_HP;      // +0x14  System.Int32
    void*   weapon;      // +0x18  Game.Item
};
#pragma pack(pop)
// Methods (AOT entry points, GameAssembly.dll absolute):
//            .ctor    @ 0x00007FF600001000    token=0x06000001
//            TakeDamage    @ 0x00007FF600001100    token=0x06000002
// [static]   ResetCache    @ 0x00007FF600001200    token=0x06000003
```

Key differences from the single-class `.h`:

- **Fully-qualified struct names**: `Game_Player`, not `Player` —
  collision-safe across all namespaces. Matches the debugger-symbol
  label naming so you can cross-reference between the `.h` and the
  `.dd64` / `.idc` / `.py` files.
- **No flattened inheritance**: each struct declares only its own
  declared fields, with a `// inherits <Parent>` banner so the
  reader walks the chain manually. Avoids the depth-first explosion
  on the ~200K-class IL2CPP games in our corpus (single-class
  exporter still does flatten when called with explicit ancestors).
- **Soft-fail per class**: a `getClassInfo` error on one class emits
  `// SKIPPED <class> — getClassInfo failed` instead of tearing the
  whole dump down. The status bar surfaces the skip count.
- **Mono support**: the menu greys out (same `CanExportSymbols`
  gate as the debugger-symbol exports) — Mono JIT addresses don't
  survive a restart, so the method-address trailer would be lies.

### Manual live-verification steps

After any change to `Il2cppHeaderBatchExporter.cs` or the wiring,
run this against a real IL2CPP game. The unit tests in
`UnityDumpUI.Tests/Il2cppHeaderBatchExporterTests.cs` cover rendering
shape but can't catch live-RPC regressions.

1. Launch any IL2CPP game from the test matrix (e.g. Heroes of Might
   and Magic: Olden Era — v31 IL2CPP x64).
2. Inject `Discrete-IL2CPP.dll` via DiscreteUI's Inject & Connect.
3. Wait for the connection bar to show `Connected · IL2CPP · …`.
4. Click the Tools menu → **Export il2cpp.h (whole game)…**.
5. Save somewhere; wait — large games take ~20-40 seconds. The
   View log surfaces `export il2cpp.h: <assembly> — N classes
   emitted so far` every 200 classes.
6. Open the file. Spot-check:
   - Assembly banners present, one per user assembly.
   - Struct names are FQN (e.g. `UnityEngine_GameObject`).
   - At least one well-known class has plausible field offsets
     (`UnityEngine_Transform.m_CachedPtr` at `+0x10` on x64).
   - Method-address trailer lines look like real `GameAssembly.dll`
     absolute addresses (typically `0x00007FF6...`).
   - No `// SKIPPED` markers on common BCL types — those mean the
     pipe layer regressed on `getClassInfo`.

---

## 4. Debugger symbols — x64dbg `.dd64` / Ghidra / IDA `.idc`

[SymbolExporter.cs](../ui/UnityDumpUI.Core/Services/Export/SymbolExporter.cs)

Whole-process symbol dump targeted at static-RE workflows: load the
output into a disassembler so every Unity class metadata pointer
(`Il2CppClass*` in `.data`) and every AOT method entry (`.text`) gets a
human-readable label. Triggered from the Tools menu while connected
to an IL2CPP game.

### How it differs from the single-class exporters

- **Source**: one `dumpIl2cppSymbols` round-trip across every user
  assembly, not a per-class `getClassInfo`. The DLL emits flat
  `SymbolDumpRow`s (`kind: "class"|"method"` + address + label inputs);
  the UI renders them.
- **Default assembly filter**: passes the assembly list through
  `SystemAssemblyFilter.SelectUserAssemblies` so system /
  `UnityEngine.*` / `Unity.*` / BCL DLLs don't bloat the output.
- **IL2CPP only**: the Mono backend leaves the menu greyed out. Mono
  managed methods JIT into dynamic pages with no module-relative
  offset, so a snapshot can't survive a game restart in a debugger.

### Label naming

Single source of truth: `SymbolExporter.BuildLabel`. Every label is
sanitised to `[A-Za-z0-9_]` (spaces / `.` / `/` / `<` / `>` / `` ` ``
collapse to `_`).

| Row kind | Label shape | Example |
|---|---|---|
| class  | `Class_<Namespace>_<Class>` | `Class_Game_Player` |
| method | `<Namespace>_<Class>__<Method>[_<HumanSuffix>]` | `Game_Player__TakeDamage_int` |

The `HumanSuffix` comes from the backend's
[`MethodSignatureFormat.h::MakeHumanSuffix`](../dll/src/shared/MethodSignatureFormat.h)
and matches `findMethodInstances` byte-for-byte:

- 0 args → `noargs`
- 1 arg → C# keyword (`int`, `float`, `string`) or last-segment of the
  type name
- N same-typed → `<N><Short>` (e.g. `3float`)
- N mixed → `<S0>_<S1>_...` (e.g. `string_Card_Point`)

Without the suffix, every `Update`/`Start`/`OnEnable` (Unity message
methods) would collide across hundreds of classes. The suffix is what
keeps x64dbg from silently dropping all-but-one of every overload set.

### Per-format details

#### x64dbg `.dd64`

```json
{
  "labels": [
    { "module": "GameAssembly.dll",
      "address": "5A3740",
      "manual": true,
      "text": "Game_Player__Update_noargs" }
  ],
  "comments": [
    { "module": "GameAssembly.dll",
      "address": "5A3740",
      "manual": true,
      "text": "Assembly-CSharp:Game.Player" }
  ]
}
```

- `address` is **module-relative HEX without** `0x` prefix —
  x64dbg's loader treats the field as raw hex against the named module
  and resolves at load time.
- Method rows always get a paired comment (`{Assembly}:{Namespace}.{Class}`)
  so the disassembly user can see the declaring class without expanding
  the label.
- When the backend can't locate `GameAssembly.dll` (`moduleBase` = 0)
  the renderer falls back to absolute hex so the labels still load,
  just without rebase support.

Import via: Tools → Database management → Import.

#### Ghidra symbols (`.txt`)

```
// Generated by Discrete — Ghidra Symbol Import
// Import via: ImportSymbolsScript.py
Class_Game_Player 0x00007FF6CAFEBABE l
Game_Player__Move_Vector3 0x00007FF6DEADBEEF f
```

- Absolute hex with `0x` prefix (Ghidra wants it that way).
- Type letter: `f` for method rows (Ghidra marks them as function
  symbols), `l` for class rows (plain label on a data pointer).
- Import via the bundled `ImportSymbolsScript.py` once the
  `GameAssembly.dll` image is loaded into the project.

#### IDA `.idc`

```c
#include <idc.idc>

// Generated by Discrete — IDA Symbol Import
static main() {
    MakeName(0x00007FF600009999, "Game_Player__TakeDamage_int");
    MakeComm(0x00007FF600009999, "Assembly-CSharp:Game.Player");
    ...
}
```

- Absolute hex (IDA's IDC accepts that shape natively).
- Method rows pair `MakeName` + `MakeComm` (declaring class
  qualifier). Quotes inside the qualifier are escaped — though
  sanitised labels can never produce them, the comment payload
  isn't sanitised.
- Run from File → Script file… after loading `GameAssembly.dll`.

### Cost guidance

A typical Unity 2022 IL2CPP game with `Assembly-CSharp` + 4-5 user
assemblies produces 30k-100k rows in 1-3 seconds. The 1,000,000-row
safety cap (`limit: 0` request → unlimited up to cap) hasn't been
reached in any test game so far; when it does, the response carries
`truncated: true` and the UI surfaces a banner.

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
| [SymbolExporter.cs](../ui/UnityDumpUI.Core/Services/Export/SymbolExporter.cs) | x64dbg `.dd64` / Ghidra label / IDA `.idc` emitter (IL2CPP only) |
| [ExportOptions.cs](../ui/UnityDumpUI.Core/Services/Export/ExportOptions.cs) | Shared option DTO (depth, array limit, collapse, live, base address, min string length, fill empty array slots) |
| [UnityTypeMap.cs](../ui/UnityDumpUI.Core/Services/Export/UnityTypeMap.cs) | Type-name → CE / C / size mapping |
| [IClassInfoLookup.cs](../ui/UnityDumpUI.Core/Services/Export/IClassInfoLookup.cs) | Drilldown peer lookup interface |
| [AobAaScriptGenerator.cs](../ui/UnityDumpUI.Core/Services/Export/AobAaScriptGenerator.cs) | Phase 18c AOB AA-script emitter |
