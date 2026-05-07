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

### `DataGridCheckBoxColumn` requires double-click to enter edit mode

Single-click on a `DataGridCheckBoxColumn`'s checkbox does nothing
until you double-click the cell to enter edit mode. For UX-critical
single-click toggles (e.g. multi-select grids), use a
`DataGridTemplateColumn` with an inline
`<CheckBox Focusable="False" />` instead. Pattern in
[MethodOverloadsWindow.axaml:103-112](../ui/UnityDumpUI/Views/MethodOverloadsWindow.axaml).

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
