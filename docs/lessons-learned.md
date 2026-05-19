# Lessons Learned

> Engineering gotchas accumulated across the project — things that took
> a debugging session to figure out the first time and that we never
> want to relearn from scratch. Each entry leads with the symptom, then
> the root cause, then the rule. Cross-references to source live as
> [file.ext:line](../path/file.ext) links.

---

## Mono / IL2CPP runtime invocation

### `runtime_invoke` uses **non-uniform** parameter marshaling, not the documented "uniform" convention

**Symptom**: a managed `Parse("42")` call returns `FormatException`
even though every byte on the wire looks correct. Numeric primitive
calls work fine; anything that takes a managed reference type
(`System.String`, user objects, arrays) misbehaves the same way.

**Root cause**: Mono's official documentation describes a *uniform*
convention where `params[i]` always holds a pointer to the slot for
arg *i* — `&intSlot` for value types, `&monoStringPtr` for reference
types. The actual x64 trampoline does not honour that for reference
types: it expects the object pointer **directly** in `params[i]`, not
the address of a stack slot containing the pointer. Every battle-
tested injector in the wild (BepInEx, MelonLoader, Il2CppInterop) uses
the non-uniform form. The Mono docs are wrong (or at minimum mismatched
with the actual trampoline shipped in Unity-vendored Mono).

**Rule**:
- Value types (`int`, `float`, `bool`, etc.): pass `&slot` where
  `slot` holds the value.
- Reference types (`MonoString*`, `Il2CppString*`, any object/array
  pointer): pass the pointer **directly** in `params[i]`.
- `null`: pass `nullptr` directly (don't wrap in a slot).

See `MarshalArg` in [IL2CPPMethodInvoker.cpp](../dll/src/il2cpp/IL2CPPMethodInvoker.cpp)
and [MonoMethodInvoker.cpp](../dll/src/mono/MonoMethodInvoker.cpp) for
the canonical implementation. Both backends share the same convention.

### Managed exception message lives at `Il2CppException + 0x18` / `MonoException + 0x18`

Mono and IL2CPP both inherit `System.Exception` which lays out
`_message` at the same offset. We read it via SEH-protected struct
read and decode the UTF-16 chars into UTF-8 with the same helper the
Inspector uses for `System.String` fields. Stable across Unity 2018+.

### IL2CPP `method_get_pointer` is not exported in Unity 2021.3+

**Symptom**: `il2cpp_method_get_pointer` returns null or the symbol is
missing entirely from a recent IL2CPP game.

**Rule**: read `MethodInfo.methodPointer` directly via SEH-protected
struct read at offset 0. The export-only path is a dead end on modern
Unity.

---

## Overload disambiguation

### IL2CPP code stripping removes "unused" Unity APIs — verify with `findMethodInstances`

**Symptom**: `callMethod` against a Unity engine API (e.g.
`Resources.FindObjectsOfTypeAll`, `SceneManager.get_sceneCount`,
`GameObject.FindWithTag`) returns `error: method_not_found` even though
the method is right there in Unity's official documentation and works
in any Editor / non-IL2CPP build.

**Root cause**: IL2CPP build option **Managed Stripping Level**. At
"Low" (default for many shipping titles) and above, the IL2CPP linker
removes any managed method not transitively referenced by user code or
the [Preserve] attribute. Whole categories of Unity engine
"convenience" APIs are typical casualties — `FindObjectsOfTypeAll`,
`FindObjects*Tag*`, `get_sceneCount` / `GetSceneAt`, `Camera.get_allCameras`,
etc. The runtime can still walk the surviving overloads' MethodInfo
table, so `findMethodInstances` returns `count: 0` for the stripped
ones (instead of an error) — they're literally not in the metadata
anymore.

**Verified live on SaGa F2R 2026-05-08 (Unity 2021.3.39f1, IL2CPP, v29
metadata)**:

```
✅ available  Object.get_name()                              compiled
✅ available  Object.FindObjectsOfType(Type, bool)           compiled
✅ available  Object.FindObjectOfType(Type) / (Type, bool)   compiled
✅ available  GameObject.Find(string)                        compiled
✅ available  SceneManager.GetActiveScene()                  compiled
✅ available  Scene.GetRootGameObjects()                     compiled
✅ available  Camera.get_main() / get_allCamerasCount()      compiled

❌ stripped   Resources.FindObjectsOfTypeAll(Type)           count=0
❌ stripped   Object.FindObjectsOfTypeAll(Type)              count=0 (only generic;
                                                              also stripped)
❌ stripped   Object.FindObjectsOfType(Type) (1-arg)         not in metadata
                                                              (2-arg survived)
❌ stripped   GameObject.FindWithTag(string)                 count=0
❌ stripped   SceneManager.get_sceneCount                    count=0
❌ stripped   SceneManager.GetSceneAt(int)                   count=0
❌ stripped   Camera.get_allCameras                          count=0
```

**Rule**: before designing a Unity-engine-API recipe in a bridge
script, probe with `findMethodInstances` first. Recipes that survive
Editor/Mono testing can fail outright on shipped IL2CPP builds. Build
fallback chains around the methods that consistently survive
(`Object.FindObjectsOfType(Type, bool)`, `GameObject.Find(string)`,
`Camera.get_main()`, `SceneManager.GetActiveScene()` →
`Scene.GetRootGameObjects()` if Scene value-type marshaling lands).

The proper long-term fix is a **DLL-side managed-heap walker** that
doesn't depend on Unity's managed APIs at all — walks libgc's heap
sections directly via `GC_get_heap_section_count` /
`GC_get_heap_section_at`. Backlog'd as a follow-up to Phase 20-MT.

**Update 2026-05-18 (Phase 20a-2 closure)** — Unity 2022+ adds another
layer: `Object.FindObjectsOfType` is **removed outright**, not just
deprecated. On a BongoCat-style Unity 2022+ build, `findMethodInstances`
returns 0 overloads for `FindObjectsOfType`. The replacement is
`Object.FindObjectsByType(Type, FindObjectsInactive, FindObjectsSortMode)` —
the 3-arg compiled overload (generic `FindObjectsByType<T>(...)`
overloads exist but have address `0x0` until JIT-instantiated). Updated
`unity_scene.lua` `getAllGameObjects` tries[] now has three entries:
2-arg OfType → 1-arg OfType → 3-arg ByType. First match wins,
production calls land on the per-version preferred path automatically.

### Enums marshal as their underlying integer type — no `enum` wire tag needed

**Surfaced**: Phase 20a-2 verification on BongoCat (Unity 2022+) had to
call `Object.FindObjectsByType(Type, FindObjectsInactive,
FindObjectsSortMode)`. Both enum params have no `enum` tag in the
callMethod wire protocol.

**Resolution**: pass `int32` directly with the underlying integer value:
`Include = 1`, `None = 0`. Worked on Mono first try, no DLL changes.

**Why it works**: CLR enum-typed parameters are ABI-identical to their
underlying integer type. The JIT/AOT prologue reads
`*(int32_t*)params[i]` for an enum param the same way it does for an
int32 param. Our Phase 19c non-uniform marshaling (value types pass
`&value`) gives `runtime_invoke` exactly that — a 4-byte int32 slot
pointer. No type-checking happens between the DLL and the JIT
trampoline.

**Caveat**: this only works for enums whose underlying type is `int32`
(the C# default). For `enum X : long` or `enum X : byte` you'd need to
pass the matching width (`int64` / `byte`). Almost all Unity public
enums use the default — but check `findMethodInstances` output if in
doubt; `paramTypes` shows the enum type name and the rendered signature
matches the underlying width via the CLR's normal type-rendering rules.

**Rule**: when calling a method with enum params, pass `int32` and look
up the enum's integer values from Unity docs or the metadata-cli
(`getEnumValues` RPC also exists for live enumeration). Don't wait for
a DLL-side `enum` wire type — there's nothing it could do that
`int32` doesn't already.

### `signatureMatch` substring substring is whitespace-and-comma-aware — close-paren matters

**Symptom**: a `callMethod` call with a precise-looking
`signatureMatch="(System.Type)"` returns `method_not_found` even though
`findMethodInstances` confirms a method named that exists with
signature `"(System.Type, System.Boolean)"`.

**Root cause**: the substring matcher checks if `signatureMatch` is a
substring of `paramTypes`-derived signature string — including
punctuation. `"(System.Type)"` (close-paren after "Type") is NOT a
substring of `"(System.Type, System.Boolean)"` (comma after "Type") —
because in the haystack the character after "Type" is a comma + space,
not a close paren.

**Rule**: when the target method has multiple overloads, copy the
exact `signature` field from `findMethodInstances` output rather than
hand-typing it. If you need to disambiguate to a 2+-arg overload, the
full string is the only way:

```lua
sig = "(System.Type, System.Boolean)"   -- not "(System.Type)"
```

Mirror trap to the existing "`Int32` matches `UInt32`" gotcha — both
stem from the same substring semantics. The existing
parens-bounding-for-uniqueness trick (`(System.Int32)` vs `Int32`)
still helps but doesn't paper over multi-arg overloads.

### `signatureMatch` substring trap — `"Int32"` matches `"UInt32"`

**Symptom**: a method with both `Mathf.Abs(System.Int32)` and
`Mathf.Abs(System.UInt32)` overloads dispatched with
`signatureMatch="Int32"` resolves ambiguously, often picking the wrong
one. Same trap with `"Int16"` ⊂ `"UInt16"`, `"Int64"` ⊂ `"UInt64"`,
`"Char"` ⊂ `"System.Char"` ⊂ `"Char[]"`, etc.

**Rule**: default to **parens-bounded** signature strings —
`signatureMatch="(System.Int32)"`. The opening parenthesis disambiguates
from any preceding character (`U`, etc.); the closing parenthesis or
comma after the type rules out longer types like `Int32[]` or
`Int32, ...`.

The smoke scripts (`tools/Test-CallMethod.ps1`,
`tools/Test-FindMethodInstances.ps1`), the `unity_invoke.lua` API
header, and the `MethodOverloadsWindow.OpenCallMethodDialogAsync` all
use the parens form for this reason. The DLL doesn't enforce it (the
substring match is intentional so users can supply partial sigs like
`"Vector3"` to find any overload taking one), so the burden lives at
the caller.

### Multi-overload AA-script `[ENABLE]` blocks should be idempotent

**Symptom**: re-executing an AA script while the previous symbols are
still registered fails noisily.

**Rule**: in the `[ENABLE]` block, register each overload as its own
distinct symbol (`Class_Method_2uint`, `Class_Method_ItemBundle`),
**not** a shared `Class_Method` that gets re-registered per overload.
Mirror the per-symbol `unregistersymbol` calls in the `[DISABLE]`
block. The Phase 18c [AobAaScriptGenerator](../ui/UnityDumpUI.Core/Services/Export/AobAaScriptGenerator.cs)
emits unique `MakeSymbolName` collision-resolved names automatically.

---

## GC-root lifetime for `callMethod` returns

A managed pointer returned by `callMethod` is valid **only for the
next call you make right now**. A GC that ran between the address read
and the next invoke can have moved the object — Mono runs a moving
GC, IL2CPP is largely non-moving but not guaranteed.

**Best practice**: chain `findInstances` → `callMethod` (or
`callMethod` → `callMethod` on the returned address) inside one Lua
block, with no intervening user input. For the UI path (Inspector
cross-tab Phase 19d-2), the pre-fill happens at modal-open time and
the user clicks Call within the same dispatcher tick — no exposure.

---

## Avalonia 12 Windows-only build

### All three of `Avalonia.Win32` + `Avalonia.Skia` + `Avalonia.HarfBuzz` must be wired

When dropping `Avalonia.Desktop` (the metapackage that wires every
backend behind `UsePlatformDetect`), each Windows-side dependency must
be added explicitly:

- `Avalonia.Win32` package + `.UseWin32()` — without this, no window.
- `Avalonia.Skia` package + `.UseSkia()` — without this, **silent
  black window**, no error, no log.
- `Avalonia.HarfBuzz` package + `.UseHarfBuzz()` — without this,
  `InvalidOperationException: No text shaping system configured` from
  `AppBuilder.Setup()` BEFORE any logging starts → process exits with
  `0xC0000409`, no log file, no window. Looks identical to "EXE
  doesn't run at all".

`Program.cs` reads
`AppBuilder.Configure<App>().UseWin32().UseSkia().UseHarfBuzz().WithInterFont()`.

Documented in the [Avalonia 12 Win-only memory entry](../C:\Users\%USERNAME%\.claude\projects\D--Github-discrete\memory\feedback_avalonia12_winonly.md);
the symptom-to-cause map is the load-bearing part for diagnosis.

### AOT analyzers are strict — no reflection JSON

Any new JSON serialization path must go through the source-gen
`ProtocolJsonContext`. Reflection-based
`JsonSerializer.SerializeToElement<T>(value)` trips IL2026 + IL3050
under all four trim/AOT analyzers.

### `x:DataType` on the `DataGrid` element silently kills column sort under AOT

With `AvaloniaUseCompiledBindingsByDefault=true`, setting an
`x:DataType="vm:SomeViewModel"` annotation on the `DataGrid`
**element itself** —

```xml
<!-- BROKEN: header click does nothing -->
<DataGrid ItemsSource="{Binding Rows}"
          CanUserSortColumns="True"
          x:DataType="vm:ListViewModel">
  <DataGrid.Columns>
    <DataGridTextColumn Binding="{Binding Foo}" />
  </DataGrid.Columns>
</DataGrid>
```

— anchors the compile-time DataContext type at the DataGrid scope.
Column cell bindings (`{Binding Foo}`) then get compiled against
that scope, but `Foo` doesn't live on `ListViewModel` — it lives on
each row item. Avalonia silently falls back to runtime resolution
for the cell value (so the column still renders correctly), but the
DataGrid's sort comparer reads the column binding's path through
the *compiled* metadata and can't find a comparable accessor.
Result: header clicks become silent no-ops in published (AOT)
builds, while the data displays fine.

Fix is to drop the `x:DataType` attribute from the `DataGrid`
element. The Window-level `x:DataType` already establishes the
parent VM type for `ItemsSource="{Binding Rows}"`; the column
bindings then resolve against each row item via runtime path lookup
(which DataGrid's `TrimmerRootAssembly` preserves under AOT):

```xml
<!-- WORKS: header click sorts as expected -->
<DataGrid ItemsSource="{Binding Rows}"
          CanUserSortColumns="True">
  <DataGrid.Columns>
    <DataGridTextColumn Binding="{Binding Foo}" />
  </DataGrid.Columns>
</DataGrid>
```

Cross-checked against sibling project `D:\Github\CrimsonAtomtic`
(same Avalonia 12 + Native AOT setup, all DataGrids sort correctly).
The diff is exactly this: their DataGrid elements never carry
`x:DataType`. `SortMemberPath` / `CustomSortComparer` are not
needed in either project once this attribute is gone.

The same trap also applies — to a lesser degree — to `x:DataType`
*inside the column Binding itself* (`Binding="{Binding Foo,
x:DataType=vm:RowType}"`); strip that too for safety.

The trap is that the IDE / build pipeline never warns about this:
build is green, AOT publish is green, the column renders, and only
manual interactive testing of header click reveals it. If a new
DataGrid is added, omit `x:DataType` on both the DataGrid element
and column Bindings — let Avalonia infer from `ItemsSource`.

- **In-Core code** can reach `ProtocolJsonContext` directly. Pattern:
  `Str(string)` / `Int(int)` helpers in
  [BackendAdapter.cs](../ui/UnityDumpUI.Core/Services/BackendAdapter.cs).
- **UI ViewModels** can't reach the internal context. Go through
  [CallArgFactory.cs](../ui/UnityDumpUI.Core/Services/CallArgFactory.cs)
  (Phase 19d pattern) — public seam over the internal source-gen
  context.

### `LibraryImport` does NOT auto-resolve A/W variants

Unlike classic `DllImport` with `CharSet`, every entry-point name in
`[LibraryImport(...)]` is taken literally. Use explicit
`EntryPoint = "FooW"` (or `"FooA"`) for any function that ships A/W
variants — no `EntryPoint` means the linker looks for the bare name,
which may not exist.

### Avalonia 12 clipboard API churn

The legacy `IDataObject` / `DataFormats` / `SetDataObjectAsync`
surface is gone. Only supported writer is
`IClipboard.SetDataAsync(IAsyncDataTransfer)` — wrap a `DataFormat.Text`
item in a `DataTransferItem`, add to a `DataTransfer`, hand to
`Clipboard.SetDataAsync`. Pattern in
[MainWindow.axaml.cs:108](../ui/UnityDumpUI/Views/MainWindow.axaml.cs).

### `avares://` keys off `AssemblyName`, not `RootNamespace`

If the .csproj `<AssemblyName>` is renamed (we did this from
`UnityDumpUI` → `DiscreteUI` for branding), every `avares://` URI in
XAML must update too: `App.axaml`'s `ResourceInclude Source=` and any
`Window.Icon` references. Forget any one of them and the build
succeeds but resources fail to load at runtime with cryptic null refs.

### Avalonia 12 `TextBlock.Text` and `Inlines` are mutually-exclusive sources

**Symptom**: setting both `Text` and `Inlines` on the same `TextBlock`
produces undefined rendering — sometimes the plain text appears,
sometimes the inline runs appear, sometimes the text gets duplicated
(plain string + concatenated inline runs). Not deterministic across
property-set order or Avalonia minor versions.

**Rule**: pick exactly one source per render. To toggle between plain
text and inline runs (e.g. for the filter-substring highlight UX in
`HighlightedTextBlock`), the pattern is **clear the other source first**:

```csharp
if (noHighlight) {
    Inlines = null;           // clear before setting Text
    Text = source;
} else {
    Text = null;              // clear before setting Inlines
    Inlines = BuildRuns(source, filter);
}
```

Also — don't bind `Text` from XAML when the control internally manages
`Inlines`. Add a `SourceText` styled property that the consumer binds
instead, and let the control decide which of `Text` / `Inlines` to set.

Pattern in
[ui/UnityDumpUI/Controls/HighlightedTextBlock.cs](../ui/UnityDumpUI/Controls/HighlightedTextBlock.cs)
+ pure splitting in
[ui/UnityDumpUI.Core/Services/HighlightSegmenter.cs](../ui/UnityDumpUI.Core/Services/HighlightSegmenter.cs).

### `DataGridCheckBoxColumn` requires double-click to enter edit mode

Single-click on a `DataGridCheckBoxColumn`'s checkbox does nothing
until you double-click the cell to enter edit mode. For UX-critical
single-click toggles (e.g. multi-select grids), use a
`DataGridTemplateColumn` with an inline
`<CheckBox Focusable="False" />` instead. Pattern in
[MethodOverloadsWindow.axaml:103-112](../ui/UnityDumpUI/Views/MethodOverloadsWindow.axaml).

### `DataGridTextColumn` cannot host custom controls — use `DataGridTemplateColumn` + `CellTemplate`

Need a custom-painted cell inside a `DataGrid` (e.g. the filter
substring highlight rendered by `HighlightedTextBlock`)? `DataGridTextColumn`
can't do it — it only renders plain text from its `Binding`. The
replacement is a `DataGridTemplateColumn` with a `CellTemplate` that
holds whatever control tree you want:

```xml
<DataGridTemplateColumn Header="Name" Width="*" SortMemberPath="Name">
  <DataGridTemplateColumn.CellTemplate>
    <DataTemplate x:DataType="vm:FieldRow">
      <controls:HighlightedTextBlock SourceText="{Binding Name}"
                                     Highlight="{Binding #FieldFilterBox.Text}"
                                     VerticalAlignment="Center"
                                     TextTrimming="CharacterEllipsis"
                                     Margin="4,0" />
    </DataTemplate>
  </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

Two gotchas worth remembering:
1. **Preserve `SortMemberPath`** explicitly — without it, `DataGridTemplateColumn` sorts by the bound DataTemplate's first text node (often empty / wrong). `SortMemberPath="Name"` keeps the click-the-header sort working.
2. **Element-name binding works inside the cell template** — `{Binding #FilterBox.Text}` reaches a named control elsewhere in the same XAML file, even though the cell's `DataContext` is the row item.

Both points exercised by the Browser filter-highlight wiring in
[MainWindow.axaml](../ui/UnityDumpUI/Views/MainWindow.axaml) (Fields
Name + Type columns, Methods Name column).

### `CommunityToolkit.Mvvm` quirks

- **Property name collision with field name**: a property named
  `Backend` collides with the source-generated field for `_backend2`.
  Workaround: name the property `Backend2` (literally with the suffix).
- **Child-collection CanExecute is NOT auto-re-evaluated**: when an
  item inside an `ObservableCollection<T>` raises `PropertyChanged`,
  any `[RelayCommand(CanExecute = ...)]` on the parent VM whose
  predicate reads child state will NOT re-evaluate. Subscribe to each
  row's `PropertyChanged` and call `Notify*Changed()` manually.
  Pattern: `MethodOverloadsViewModel.OnRowPropertyChanged` in
  [MethodOverloadsViewModel.cs:188](../ui/UnityDumpUI/ViewModels/MethodOverloadsViewModel.cs).

---

## PowerShell smoke testing

### `ConvertTo-Json` flattens single-element arrays

**Symptom**: a `Test-*.ps1` script builds a JSON request with
`@{ args = @($oneArg) }`. The resulting wire payload has `"args":
{...}` (a bare object) instead of `"args": [{...}]` (a single-element
array). The DLL rejects with "args must be an array".

**Rule**: PowerShell unwraps single-element arrays implicitly through
the pipeline and `ConvertTo-Json` follows. For nested arrays where
the outer container is a property value, **splice the array JSON in
as raw text**:

```powershell
$argsJson = '[' + (ConvertTo-Json $oneArg -Compress) + ']'
$body     = "{`"args`": $argsJson}"
```

See [tools/Test-CallMethod.ps1](../tools/Test-CallMethod.ps1) for
the pattern in a real script.

### CJK console + em-dashes need `Start-Transcript`

A CJK Windows console (Traditional / Simplified Chinese, Japanese,
Korean) munges em-dashes (`—`) and other Unicode punctuation printed
by `build.cmd`'s status banners. Routing through `Start-Transcript`
fixes the encoding; `build.cmd` already does this so the user sees
clean output regardless of console code page.

---

## Build / DLL development

### CMake `-DBACKEND=` cache poisoning

CMake caches the `BACKEND` variable. Switching `Mono` → `IL2CPP` →
`Both` in the same build directory produces inconsistent linker
errors that look like "missing source files". Solution:
[build.ps1](../build.ps1) wipes the per-backend build dir on every
configure pass. Don't re-introduce a "smart re-use" optimization
without re-confirming it survives a backend switch.

### Game must be closed before rebuilding the DLL

`UnityDumper-Mono.dll` / `UnityDumper-IL2CPP.dll` stay mapped in the
target process after injection. Trying to rebuild while the game is
running fails with `LNK1104: cannot open file ...`. Close the game,
then rebuild.

### `Inject-Dll.ps1` reads from `build/dll-both-release/bin/`, NOT `dist/`

**Symptom**: a freshly-committed DLL fix appears to fail every live-
verification run. The wire response shows the pre-fix behaviour even
though the source code clearly contains the new branch.

**Root cause**: `Inject-Dll.ps1` (called by `Test-Handshake.ps1` and
DiscreteUI's "Pick & Inject" alike) reads the DLL from
`build\dll-both-release\bin\UnityDumper-IL2CPP.dll` (or `…-Mono.dll`).
`dist/` is the *shippable* tree but is **not** the inject source —
`dist/` only gets repopulated when `build.ps1` runs to completion.
Locally-edited code that you forgot to rebuild stays out of `build/`,
out of `dist/`, and out of every injected process. **Worse**, partial
or aborted builds can leave `dist/` slightly newer than
`build/dll-both-release/bin/` (e.g. the IL2CPP variant got copied but
Mono got stuck on `LNK1104`), so a "is dist/ recent?" eyeball check
gives a false-positive.

**Rule**: before any live-verification round after a commit,
unconditionally rebuild — even if `dist/` *looks* recent:

```powershell
.\build.ps1 -Target DLL -Backend Both   # ~20 s warm
Get-ChildItem build\dll-both-release\bin\UnityDumper-*.dll | Select Name,LastWriteTime
```

The build_number bump in `build_number.txt` is the cheapest signal —
if the UI's status bar reads a build # lower than `cat build_number.txt`,
**the running DLL is out of date no matter what `dist/` says**. Caught
2026-05-08 burning ~15 minutes on a 4-target verification run where
all four "failures" turned out to be stale DLLs from before the fix
commits.

### AOB decoder bailout is by design

Phase 18a-3's hand-rolled x64 length disassembler in
[AobPattern.cpp](../dll/src/shared/AobPattern.cpp) bails (falls
through with literal bytes, no wildcards) on any opcode it doesn't
fully model. The bailout is reported in the wire response as
`decoderBailed: true, bailedAt: N`. **Do not try to "fix" this by
over-wildcarding** — false positives cause symbol ambiguity, which is
the failure mode we cannot accept. A literal-bytes pattern is less
version-stable but always points at the right address.

---

## Pipe client / connection

### `PipeClient` write+read is intentionally uncancellable past commit

[PipeClient.cs](../ui/UnityDumpUI.Core/Services/PipeClient.cs)
deliberately stops honouring the caller's `CancellationToken` once
the request is on the wire. Don't "fix" this back to passing `ct`
everywhere — the race it solves (id-mismatch when caller cancels
mid-request and the next caller reads back the orphaned response) is
not theoretical. Rapid Find Instances clicks reproduce it within 10
seconds without the guard.

---

## Antivirus

Freshly-built `DiscreteUI.exe` is flagged by some AV vendors due to
the combination of many `LibraryImport` native calls + remote-thread
injection P/Invokes. Dev workflow: exclude the `dist\` folder. Mention
this if a fresh dev hits a quarantine error after `build.cmd publish`.

---

## Cheat Engine Lua API

### Verify CE Lua functions exist before using them

When working with Cheat Engine Lua APIs, always verify that a function
exists in the CE Lua documentation before using it. Don't assume
methods like `GotoAddressEx` exist — CE has churned its Lua surface
across 7.x versions and many "obvious" function names don't.

### Bridge scripts ship via glob, not a hand-coded list

[tools/Install-CEScripts.ps1](../tools/Install-CEScripts.ps1) used to
hand-list each `*.lua` to copy. New scripts kept getting forgotten
(`unity_addrfind.lua` and `unity_invoke.lua` both shipped one phase
late). Now uses `Get-ChildItem -Filter '*.lua'` glob; future scripts
ship automatically.

### `findInstances` heap scan picks up thread-stack false positives — validate before invoking

**Symptom**: `findInstances UnityEngine.GameObject` returns a list whose
first 1–2 entries have visibly suspect addresses (high-bit ranges like
`0x67_ACEF_EED0`, `0xB5_F7F5_EEA0`, `0xF5_AC39_F290`). Calling
`Object.get_name` (or any other icall) on them returns
`error: native_crash_in_runtime_invoke` from the SEH leaf. Same scene,
different process launch, same heap-scan can return 0/N or 30/32 valid
instances depending on luck.

**Root cause**: `findInstances` heuristic is "find any aligned qword in
the process's RW memory regions whose first 8 bytes equal the target
class's `Il2CppClass*`". It doesn't restrict the search to the IL2CPP
GC heap range — it sweeps thread stacks too. Stack frames frequently
hold local variables of type `GameObject*`, and even after the function
returns the bytes can sit in stack memory until overwritten. Each such
stale local satisfies the heuristic but isn't actually a live managed
object — calling runtime_invoke on it crashes inside the JIT/AOT entry
because the rest of the "object" is uninitialised stack bytes.

The DLL's SEH leaf catches the crash and returns
`{"error":"native_crash_in_runtime_invoke","ok":false}` cleanly so the
host doesn't die — that part of the design is correct. But every false
positive still costs 50–200 ms (the SEH leaf is expensive on the
trapping path) and contributes to main-thread pressure (lessons-learned
entry above). Both add up fast on large limits.

**Two false-positive categories** the heuristic surfaces:

1. **Thread-stack false positives** — local variables of type
   `GameObject*` left on stack frames after function return. Address
   ranges look like `0x67_xxxx`, `0xB5_xxxx`, `0xF5_xxxx` (high-bit
   thread-stack regions). First qword matches by coincidence;
   `runtime_invoke` on them crashes inside the JIT/AOT entry.
2. **IL2CPP metadata structures** — RGCTX entries, `Il2CppType`
   records, vtable pieces. These STORE class pointers at offset 0 by
   design (legitimate metadata bookkeeping). Address ranges look
   normal (heap-y), `runtime_invoke` returns `ok:true,returnValue:null`
   or similar non-error empty-ish results because the icall reads
   garbage at later offsets. Identifying signature: addresses come in
   pairs separated by exactly 0x18 bytes (paired metadata records).

**Rule**: any consumer of `findInstances` that's about to call
`runtime_invoke` on the matches needs a **two-layer** validator. Layer
1 is necessary but insufficient on its own:

```lua
local function _isValidInstance(addr, classPtr)
    if classPtr == 0 then return true end
    if readPointer == nil then return true end

    -- Layer 1: klass at offset 0 must equal classPtr.
    -- Kills stack false positives.
    local ok1, typePtr = pcall(readPointer, addr)
    if not ok1 or typePtr ~= classPtr then return false end

    -- Layer 2: m_CachedPtr at offset 0x10 must be a non-null, plausibly
    -- user-space pointer. UnityEngine.Object's layout is
    -- {klass@0, monitor@8, m_CachedPtr@0x10, ...}; live GameObjects
    -- always have the native pointer set. Metadata structures' +0x10
    -- slot is some unrelated field — usually 0, a small enum, or a
    -- stale value — and fails this check.
    local ok2, cachedPtr = pcall(readPointer, addr + 0x10)
    if not ok2 or cachedPtr == nil then return false end
    if cachedPtr < 0x10000 or cachedPtr > 0x7FFFFFFFFFFF then return false end
    return true
end
```

Both layers are local memory reads (zero pipe traffic, zero callMethod).
Layer 1 alone left 62/62 metadata false positives surviving in SaGa
F2R's equipment screen on 2026-05-08; Layer 2 dropped them. This is
specific to `UnityEngine.Object`-derived classes (which always have
m_CachedPtr at +0x10). For other class hierarchies the same idea
applies but with a class-specific "definitely-non-null managed field"
to validate.

Surfaced in two passes during Phase 20a's live tests on SaGa F2R:
- Pass 1 (PID 38144): `getAllGameObjects({limit=64, decodeNames=true})`
  returned 0/64 named because the first match was stack-y and
  runtime_invoke trapped → wrote Layer 1.
- Pass 2 (PID 41748, equipment screen): with Layer 1, 62/64 survived
  the filter but ALL 62 still get_name'd to nil — these were paired
  IL2CPP metadata structures (24-byte stride). Wrote Layer 2.

The DLL-side proper fix (restrict `findInstances` to the IL2CPP
managed heap range only — queryable via libgc / `il2cpp_gc_get_heap_section_count`)
is backlog'd.

**Resolved 2026-05-18 (Phase 23 / build #301)** — `ScanForPointer` now
filters out `MEM_IMAGE` regions (all DLL `.data` / `.bss` sections,
including IL2CPP RGCTX cache and Mono class-table memory) and every
live thread's stack range (collected via `NtQueryInformationThread` +
TEB walk). The "managed heap range" framing turned out to be a misdiagnosis
of the API surface: `il2cpp_gc_get_heap_section_count` doesn't exist
in any Unity version we surveyed (2017.1 → 6000.0); the closest public
API is `il2cpp_gc_foreach_heap` (2019.3+, IL2CPP only). The negative
filter (exclude module + stack) is runtime-agnostic, works on Mono +
all IL2CPP versions, and eliminates both documented false-positive
sources (RGCTX entries are in `MEM_IMAGE`, stack residue is in stack
ranges). Live smoke on BongoCat (Mono): 1194 regions → 571 scanned;
`UnityEngine.Mathf` (static class, expected 0 instances) found 3
matches (down from order-of-magnitude higher pre-fix counts; remaining
3 are reflection records, not stack/module residue). Per-call telemetry
exposed via `scan_stats` in the wire response + `scan` log category
so future regressions surface immediately. Deprecated Lua-side
`_isValidInstance` removed from `scripts/unity_scene.lua`.

**Even the two-layer Lua-side filter is structurally insufficient for
hot Unity classes.** Pass 3 of the SaGa F2R live test (PID 41748,
equipment screen, 2026-05-08) confirmed: 622 of 1024 candidates
survived both layers, but ALL 622 returned `native_crash_in_runtime_invoke`
on get_name. Forensic on 4 sample addresses showed every single
survivor had this metadata-structure layout:

```
+0x00: <classPtr>      ← passes Layer 1
+0x08: <flags / 0x12>
+0x10: 0x41BC0600      ← always identical across samples; passes Layer 2
                          loosely (>= 0x10000) but is clearly garbage,
                          not a real native pointer
+0x18: <classPtr>      ← classPtr stored AGAIN — definitive metadata
                          fingerprint
```

This is an IL2CPP RGCTX cache / generic class instantiation record —
legitimately stores the class pointer twice (once for resolution lookup,
once for the cache key). Real `Il2CppObject<UnityEngine.Object>`
instances never have classPtr at +0x18 (that slot would be a
managed-field offset).

**`whatIsAt` (Phase 17) shares the same heuristic** — it identifies the
4 metadata records as `class: "GameObject"` because it also just reads
the first qword. This isn't a Phase 17 bug per se (the heuristic is
correct for the typical "user has an address from a CE pointer scan"
use case), but it means our reverse lookup won't help us distinguish
real instances from metadata records either.

**Action**: for `UnityEngine.*`-derived classes, prefer the engine's
own enumeration APIs over `findInstances` — but verify they survived
IL2CPP code stripping first (see "IL2CPP code stripping..." below):
- `Object.FindObjectsOfTypeAll(Type)` — returns ALL objects of a type
  including hidden/inactive/HideAndDontSave. Authoritative,
  zero-false-positive. Now the implementation backing
  `unity_scene.lua` `getAllGameObjects`.
- `Resources.FindObjectsOfTypeAll(Type)` — older API, similar effect
  in older Unity versions.
- `SceneManager.GetActiveScene().GetRootGameObjects()` — narrower
  (only the active scene's roots) but bypasses the whole
  Type-marshaling step. Needs `Scene` value-type marshaling (Phase 19
  v2 candidate).

Use `findInstances` for **user-defined classes** where Unity has no
equivalent enumeration API — those classes don't have the dense IL2CPP
metadata cross-references that `UnityEngine.Object` accumulates, so
the heap scan is much cleaner. Phase 17 / 18 / 19's verified test
matrix (which all use user classes like `MainGame.PLY_WORK`) confirms
the heuristic is fine in those cases.

### `callMethod` on Unity icalls is main-thread-sensitive — sustained bursts freeze the game

**Symptom**: a Lua loop that calls `callMethod` against a Unity scene-graph
property (like `Object.get_name`, `Transform.get_parent`,
`Transform.GetChild`, `Component.gameObject`) thirty-two times: works.
The same loop continued for ~1024 iterations: the game becomes
unresponsive within seconds and has to be killed from Task Manager.

**Root cause**: `callMethod` invokes managed methods on the **DLL's pipe
worker thread**, NOT Unity's main thread. Phase 19's spec called this
out as a v1 limitation. Unity's internal locks / engine state assume
main-thread-only access for scene-graph icalls. Bursts up to a few
dozen calls slip past unscathed because the windows between game-thread
critical sections are short and we win the race; sustained streams of
hundreds-to-thousands of icalls have monotonically increasing odds of
hitting a critical section, and once one stalls the game thread's
cooperative scheduling falls apart and the whole process freezes.

This is NOT a deadlock in the strict CS sense (no cycle of mutex
ownership) — it's lock contention + missed game-loop deadlines until
the OS thinks the window is hung. `pcall` doesn't help; SEH leaves on
the DLL side don't help. The only real fix is main-thread marshaling.

**Rule** (until the proper fix lands):

- Stateless / pure managed methods (Mathf.*, Convert.*, String.*,
  Math.*, etc.) are safe at any call rate. Phase 19's verified test
  matrix is all of these — that's why we never noticed.
- Unity engine icalls (anything on `UnityEngine.Object` /
  `GameObject` / `Transform` / `Component` / `Camera` / scene-graph
  classes) must be **rate-limited and small-N**. Empirical safe band:
  ≤ 256 calls in one stretch, with a 1 ms `sleep()` between RPCs.
  Past that, the game starts to chug; past ~1024 it usually freezes.
- For batch operations on N>256 GameObjects, **don't iterate
  callMethods** — read managed fields via `walkInstance` / `walkArray`
  instead, where the heap is read-only memory access (no icall).
- The proper fix is a future phase: hook the DLL into Unity's main
  thread (e.g. patch `PlayerLoop` or `Update` to drain a queue of
  pending invocations). Until that lands, scripts/UI must enforce
  the call-rate budget themselves.

Surfaced 2026-05-08 during Phase 20a's first live test on SaGa F2R:
`getActiveSceneRoots({limit=1024})` issued ~2048 sustained icalls and
locked up the game. Fixes in `unity_scene.lua`: per-RPC `sleep(1)`
throttle, default limits dropped (256 for icall-bearing functions,
128 for tree-walk experimentals), explicit caveat block at file head.

### Wire envelope is `{id, result}` — bridge scripts must unwrap

**Symptom**: a freshly-written bridge script's high-level API returns
empty arrays / nil values for everything, even though a hand-paste
PowerShell smoke or raw `rpc()` against the same wire command shows
the correct `count` / `matches` / `returnValue` payload.

**Root cause**: the DLL pipe server wraps every reply in
`{"id": N, "result": { ... }}` (or `{"id": N, "error": {code, message}}`
on failure). A bridge script's local `rpc()` helper that does
`return parseJson(resp)` returns the **outer envelope**, not the
result payload. Top-level field reads like `r.matches` / `r.elements` /
`r.returnValue` then all see nil because the real fields live one
level down at `r.result.matches` etc.

**Rule**: every bridge-script `rpc()` must unwrap. Canonical pattern
copied from [unity_invoke.lua](../scripts/unity_invoke.lua) (lines
around 357–362):

```lua
local r = parseJson(resp)
if r.error then
    local err = r.error
    local msg = (type(err) == "table" and (err.message or err.code))
                or tostring(err)
    error(TAG .. " " .. cmd .. " failed: " .. tostring(msg))
end
return r.result or {}
```

The throw on error is optional — `unity_invoke.lua` returns the raw
envelope and lets `callUnityMethod` decide; `unity_scene.lua` throws
because every public function expects a non-error response. Pick one
and stick to it within a single bridge.

Surfaced 2026-05-08 during Phase 20a's first live test against SaGa
F2R: `getAllGameObjects()` returned 0 even though the same
`findInstances` call from a hand-built `rpc()` showed 4+ matches. Fix
in `unity_scene.lua`'s `rpc()`.

---

## AOBMaker CE plugin

### `InjectTableFile` is CRLF-sensitive — Stream.Size verification fires "wrote N, stream has N-K"

**Symptom**: AOBMaker plugin's `InjectTableFile` reply for one
specific lua file is
`failure — Stream size mismatch: wrote 37326, stream has 36466`,
while every other file in the same batch succeeds. The byte gap is
suspiciously close to the file's line count.

**Root cause**: CE's `createStringStream(content)` +
`Stream.copyFrom(ss, 0)` path in the plugin's
`HandleInjectTableFile` (see `D:\Github\AOBMaker\plugins\CEPlugin\src\pipe_server.cpp`
line ~1354) **silently normalises CRLF → LF** when writing into the
TableFile's underlying store. The plugin computes `expectedSize` from
the pre-normalisation byte count and verifies post-write
`Stream.Size` against it; any CRLF in the input mismatches by exactly
one byte per pair.

In Phase 22 live smoke (2026-05-12) this surfaced on
`scripts/unity_scene.lua` — 860 CRLF pairs in the working copy of
that one file (the other 6 bridge files were LF only), 860-byte
deficit, plugin verification fired and the entire bridge-inject
pipeline aborted at step 7/8. **Production DiscreteUI's "Inject
Discrete Bridge" would have hit the same failure on first user run** —
a silent P1 that only the live smoke caught.

**Rule**: every text file embedded as `<EmbeddedResource>` in a
project that ships through AOBMaker `InjectTableFile` must be LF-only
in the index AND the working tree. Enforce via:

1. `.gitattributes` at repo root — `* text=auto eol=lf` plus explicit
   `*.lua text eol=lf` belt-and-braces. Survives any contributor's
   `core.autocrlf=true` machine config.
2. A unit test that asserts `BridgeLuaResources.Read(name)` contains
   neither `\r\n` nor bare `\r`. See
   [BridgeLuaResourcesTests.cs `Read_EmbeddedContent_IsLfOnly_NoCrlf`](../ui/UnityDumpUI.Tests/BridgeLuaResourcesTests.cs).
3. A byte-for-byte round-trip test (NOT a 5%-tolerance length check —
   that was the original test that missed this). See same file's
   `Read_RoundTripsPhysicalFile_ByteForByte`.

The fix to a CRLF-contaminated file is one line of PowerShell:

```powershell
$src = 'path\to\file.lua'
$bytes = [System.IO.File]::ReadAllBytes($src)
$lf = ([System.Text.Encoding]::UTF8.GetString($bytes)) -replace "`r`n", "`n"
[System.IO.File]::WriteAllBytes($src, [System.Text.Encoding]::UTF8.GetBytes($lf))
```

`git ls-files --eol scripts/*.lua` is the diagnostic of choice — it
shows `i/<eol>` (index) and `w/<eol>` (working tree) per file. Both
must be `lf` for `*.lua` patterns.

A future `Phase 22.x` could push the fix upstream to the AOBMaker
plugin (compute `expectedSize` *after* the same normalisation lua
applies on the way in), but the source-side fix is sufficient and
doesn't depend on the plugin shipping a patch.

### Generated Lua AA scripts: never write `''` for an apostrophe inside a single-quoted string

**Symptom**: an AA script generated by Discrete (or any C#-side
template) hits a CE Lua parser error like
`')' expected near "s symbolhandler to finish scanning, then"` and
the *entire chunk fails to parse* — the [ENABLE] block silently does
nothing despite CE reporting the toggle succeeded.

**Root cause**: writing `print('... CE''s symbolhandler ...')` works
in SQL, VB, Pascal — but Lua interprets two adjacent single quotes
inside a single-quoted string as "close string, immediately open
new string". The following content (`s symbolhandler...`) is parsed
as a new string with no preceding `..` concat operator → syntax
error. Surfaced 2026-05-12 during Phase 22f live smoke on the
bootstrap AA's soft-fail print.

**Rule**: in Lua single-quoted strings, escape apostrophes as `\'`.
In C# verbatim strings (`@"..."`), that's literally `\'` (two
characters — `\` and `'`, both pass through). Or use Lua's
double-quoted string `"..."` which doesn't need apostrophe escaping
at all (but in a C# verbatim string then needs `""` for each `"`).

**Regression bar**: every AA-script generator's test should include
a `\w''\w` regex check (`Assert.DoesNotMatch(@"\w''\w", script)`).
The word-character bracket avoids false-positiving on legitimate
empty Lua strings between commas (e.g. `, '', ` from an empty
namespace arg in `callUnityMethod`). See
[BootstrapAaScriptGeneratorTests.cs `Build_HasNo_AdjacentSingleQuotes_LuaSyntaxTrap`](../ui/UnityDumpUI.Tests/BootstrapAaScriptGeneratorTests.cs)
+ matching tests in CallMethod / AddressLookup generators.

### Mono JIT AA scripts cannot use AOB scan (multi-instance + no module)

**Symptom**: AA script generated against a Mono game's method
either:
(a) emits `aobscanmodule(name, , pattern)` with empty MODULE and CE
silently fails every scan; or
(b) emits `aobscan(name, pattern)` (whole-process) and CE finds
N hits where only 1 is the real one — picks the first which is
rarely correct.

**Root cause (a)**: Mono JIT-compiles each method on first use and
the resulting code page is `VirtualAlloc`'d — doesn't live in any
.dll module. `getMethodAobPattern`'s ModuleName comes back empty.

**Root cause (b)**: Mono routinely emits MULTIPLE instantiations of
the same source method (different generic param combinations,
optimization tiers, IL → x64 retries). Their prologues are
byte-identical, so any AOB long enough to be unique within one
instance is also a substring of every other instance. Whole-process
AOB scan finds them all.

**Rule**: for Mono backends, AA scripts MUST resolve method
addresses through the DLL pipe (the `findMethodInstances` RPC calls
`mono_compile_method` server-side and is overload-precise).
Discrete's `unity_symbols.lua` exposes `registerMethodSymbol(symName,
asm, ns, cls, method, sigFilter)` as the single-overload bridge call
the AA generator emits. AA `[ENABLE]` form on Mono:

```lua
[ENABLE]
{$lua}
if not _G.discrete_bridgeLoaded then
  print('[Discrete] Enable "Load Discrete Bridge" first')
  return
end
registerMethodSymbol('Mathf_Abs_int32', 'UnityEngine.CoreModule',
  'UnityEngine', 'Mathf', 'Abs', '(System.Int32)')

[DISABLE]
{$lua}
if syntaxcheck then return end
pcall(unregisterSymbol, 'Mathf_Abs_int32')
```

IL2CPP backends are different — AOT-compiled methods live inside
`GameAssembly.dll` with a stable module offset, so `aobscanmodule`
form is fine. The generator branches per-overload on `ModuleName`
emptiness. See `AobAaScriptGenerator.cs` for the dispatcher.

### DLL pipe must support multiple concurrent clients in Phase 22

**Symptom**: bridge `rpc()` from CE Lua AA script fails with
`pipe open: \\.\pipe\UnityDumpBfx: Invalid argument` *while*
DiscreteUI is connected to the same DLL.

**Root cause**: the DLL's `CreateNamedPipeW` was called with
`nMaxInstances=1` (Phase 11 design — single-client at a time was the
expected case + avoided ghost-instance accumulation across game
crashes). Phase 22's workflow has DiscreteUI persistently connected
AND the CE Lua bridge issuing bursts → contention on the only slot
→ second client gets `ERROR_PIPE_BUSY` (translated as "Invalid
argument" by Lua's CRT `fopen`).

**Rule**: when designing the DLL's pipe server, plan for N
concurrent clients from day one if any "UI + scripted helper" mode
is on the roadmap. Implementation: bump `nMaxInstances` to a small
constant (Discrete uses 4) AND detach a worker thread per accepted
client so the main accept loop doesn't block in `ServeClient` for
the duration of a long-lived UI session. Handlers must be
thread-safe — most read-only walks over Mono / IL2CPP runtime
structures are inherently re-entrant; mutable caches need locks.
See [`PipeServer.cpp` `WorkerThread`](../dll/src/shared/PipeServer.cpp)
for the canonical pattern.

Trade-off: ghost-instance pile-up across game crashes can fill up
to `nMaxInstances` slots before the kernel cleans them. With N=4
this is rarely a problem; clients' existing connect-retry logic
handles the transient `ERROR_PIPE_BUSY` window.

### CE's `executeCodeEx` is unreliable for DLL function calls; prefer pipe RPC

**Symptom**: CE Lua's
`executeCodeEx(callConv, getAddress('UnityDumper_GetBuildNumber'))`
returns `nil` even when the DLL is fully loaded, the export resolves
(non-zero address from `getAddress`), and every pipe-based RPC to
the same DLL works fine.

**Root cause**: CE's `executeCodeEx` runs the target function via a
remote thread + thunk in the target process. The mechanism is
finicky on Windows x64 — varies across CE versions, sometimes loses
return values for `__cdecl` exports with simple integer returns.
Doesn't crash, doesn't log, just returns nil. Surfaced 2026-05-18
during Phase 22f live smoke as the bootstrap AA's "DLL build
unknown" false-negative.

**Rule**: when a DLL exposes BOTH C exports AND a named-pipe RPC
surface, prefer the pipe for ALL Lua-side calls. The pipe path is:
- more reliable (no thread / thunk fragility)
- structured (JSON wire — easy to add fields without breaking
  callers)
- testable (you can hit it from PowerShell / Python / curl-like
  tools without a CE instance)

`executeCodeEx` is fine as an escape hatch for one-off pokes (e.g.
calling a known-stable global init), but anything in the user-
visible smoke / startup path should go through the pipe. See
[`BootstrapAaScriptGenerator.cs`](../ui/UnityDumpUI.Core/Services/Export/BootstrapAaScriptGenerator.cs)
for the canonical inline-handshake pattern (12 lines of Lua;
open pipe, write `{"cmd":"handshake"}`, read response, regex-extract
two fields).
