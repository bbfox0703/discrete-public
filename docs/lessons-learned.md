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
