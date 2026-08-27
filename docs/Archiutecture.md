---

## Project Goal

Develop a **C++ DLL + Cheat Engine Lua Bridge** Unity Engine Dumper.
Target backends: **Unity Mono** 32-bit + 64-bit (mono-2.0-bdwgc.dll) and
**Unity IL2CPP** 64-bit (GameAssembly.dll; `global-metadata.dat` v16-v39
accepted, v24/27/29/31/35-39 verified).
Find runtime metadata entry points, build complete Assembly → Class →
Field/Method hierarchy, integrate with CE for symbol registration, table
building, and live structure inspection.

Headline capabilities (single source of truth: [README.md](../README.md#what-it-does)
for the user-facing list, [plan.md](plan.md) for the phase ledger):
- Full hierarchy walk (live + offline `metadata-cli`).
- Instance walker + exporter (CE XML / CSX / SDK header / live tables).
- **Find method instances by signature** — closed as Phase 18
  (`findMethodInstances` + `getMethodAobPattern` wire commands +
  UI overload picker + AOB AA-script generator + AOBMaker
  `CreateAAScript` direct push). See [plan.md §Phase 18](plan.md#phase-18--find-method-instances).
- Jump-to-disassembly via the AOBMaker bridge.
- CE Auto-Assembler scaffolding for overload-correct injection
  (Phase 18 `aobscanmodule(name, MODULE, AOB)` script generator).
- **Lua-callable Unity methods** (Mono JIT / IL2CPP AOT) — closed as
  Phase 19 (all five sub-phases) with marshaled primitive + string args
  + boxed return values + managed exception capture. Plan in [plan.md §Phase 19](plan.md#phase-19--lua-callable-unity-methods-lua--managed-invocation).
- **Address finder (reverse lookup)** — closed as Phase 17. Given any
  CE-scanned address, identifies the containing managed instance +
  class + field. Inverse of `findInstances`. Design spec:
  [address-finder.md](address-finder.md).

The architecture mirrors **UE5CEDumper**: injected C++ DLL communicates via Named Pipe JSON-RPC with an external Avalonia UI. The UI is **engine-agnostic** — a single binary handles both Mono and IL2CPP backends, auto-detected at connection time.

---

## Build & Deploy

* After making code changes, always do a full rebuild and verify the build output is actually updated before testing. Never assume a build succeeded without checking.
* Use `build.ps1` exclusively — bare `cmake` / `dotnet build` commands will fail without the VS DevShell environment.

### Unified Build Script (PREFERRED — always use this)

```powershell
# Build everything (DLL + UI + Tests) — Release
powershell -NoProfile -ExecutionPolicy Bypass -File "build.ps1"

# Build DLL only (builds both Mono + IL2CPP targets)
powershell -NoProfile -ExecutionPolicy Bypass -File "build.ps1" -Target DLL

# Build a specific DLL backend
powershell -NoProfile -ExecutionPolicy Bypass -File "build.ps1" -Target DLL -Backend Mono
powershell -NoProfile -ExecutionPolicy Bypass -File "build.ps1" -Target DLL -Backend IL2CPP

# Build UI only
powershell -NoProfile -ExecutionPolicy Bypass -File "build.ps1" -Target UI

# Build Proxy DLL (version.dll)
powershell -NoProfile -ExecutionPolicy Bypass -File "build.ps1" -Target ProxyDLL

# Run tests only
powershell -NoProfile -ExecutionPolicy Bypass -File "build.ps1" -Target Test

# Debug build
powershell -NoProfile -ExecutionPolicy Bypass -File "build.ps1" -Mode Debug

# Clean + publish (distribution)
powershell -NoProfile -ExecutionPolicy Bypass -File "build.ps1" -Mode Publish -Clean
```

### Build Requirements

| Tool | Version |
|------|---------|
| Visual Studio / MSVC | 2026 v18+ (MSVC v145) |
| CMake | 3.25+ |
| Ninja | any recent |
| .NET SDK | 10.0 |

### Runtime Requirements

* Windows 10/11 x64
* Proxy DLL (no CE required) *or* Cheat Engine 7.6+ (for CE injection method) 
* A running Unity game process (x64)

---

## Code Changes

* When asked to refactor or rename modules/files, make actual code changes (move files, update imports, rename classes) — not just documentation updates. Confirm structural changes before proceeding to docs.

---

## Debugging

* When fixing bugs, verify the fix against the actual memory layout or data structure rather than assuming. If the first fix doesn't work, re-examine fundamental assumptions about the data format before iterating.
* Unity version differences can change struct layouts. Always verify field offsets against the actual runtime before hardcoding.

---

## Git Operations

* When creating PRs, check for branch divergence and resolve merge conflicts before attempting `gh pr create`. Run `git status` and `git log --oneline -5` first.

---

## Cheat Engine

* When working with CE Lua APIs, verify that functions/methods actually exist in the Cheat Engine Lua API before using them. Do not invent API calls.

---

## Rules (MUST follow)

* **Language**: Code comments and UI strings in English
* **Single Instance**: UI app uses Mutex to ensure only one instance runs
* **Async everywhere**: Pipe/RPC and any long-running work must be async and must never block the UI thread. Small local settings/state files may use sync `File.*`
* **Platform Abstraction**: Any system/OS-dependent call (P/Invoke, Registry, OS commands) MUST go through an interface in `Core` project. `Core` must NEVER contain direct platform-specific code
* **Log output**: All logs go to `%LOCALAPPDATA%\Discrete_Dumper\Logs`, split by category into per-process subfolders. DLL: 5 files (init, scan, metadata, pipe, walk) under `<pid>\<category>\`. UI: 3 files (init, pipe, view) under `ui\<category>\`. UI rotates 5 files on every UI startup; DLL rotates 4 files on 8 MB size threshold per category. Per-PID DLL directories older than 15 days are pruned at UI startup. Root `Logs/` has no loose files — only subfolders
* **Magic words management**: All magic strings kept in one file per project with proper comments
* **UI Strings**: English only. All UI strings in `Resources/Strings/en.axaml`, referenced via `StaticResource` bindings
* **Unity offsets**: All internal struct offsets (MonoClass, Il2CppClass, etc.) must be dynamically verified via OffsetFinder or API probing, never hardcoded for a single Unity version
* **AOT compatible**: All C# code must be Native AOT / trimming compatible. No reflection-based APIs — use source generators instead (e.g. `[JsonSerializable]` context for `System.Text.Json`, `[ObservableProperty]` for MVVM). The UI is published as a self-contained trimmed binary
* **Backend agnostic UI**: The UI must never contain Mono-specific or IL2CPP-specific logic. All engine differences are abstracted behind the pipe protocol. The `engine` field in handshake determines which panels/features are available
* **SEH-leaf rule (DLL)**: Every `__try { ... } __except (EXCEPTION_EXECUTE_HANDLER)` body must be **leaf-shape** — exactly one expression: a function-pointer call OR a single memory access (`memcpy`, dereference, `*reinterpret_cast<T*>`). No C++ destructors, no virtual dispatch, no `throw`, no nested exception handlers. SEH does not unwind C++ destructors — composing them with RAII corrupts state. Wrap-and-call helpers go in an anonymous namespace named `Seh<Action>` (e.g. `SehClassFromName`); each compilation unit using SEH disables warning C4509 (`#pragma warning(disable: 4509)`). Mono-thread-attach pairs with this rule: see `dll/src/mono/MonoThreadAttach.h` for the policy
* **Pre-CRT rule (proxy DLLs)**: An export of `version.dll` / `winmm.dll` (and any future `dxgi.dll`) **can be entered before `_DllMainCRTStartup` has run** — Windows' AppCompat shim engine calls into a MAPPED-but-uninitialised module. The shipped proxies are `/MT`, so `__acrt_heap` is still NULL there and the first `malloc` faults. Therefore: (1) `Discrete::Proxy::Proxy_MarkCrtReady()` MUST be the first statement of every proxy's `DLL_PROCESS_ATTACH`; (2) every `PROXY_THUNK` and `Proxy_LoadRealDll` MUST gate on `Proxy_CrtReady()` before allocating or calling `LoadLibraryW`, and answer that export's **documented failure value** — never a blanket `0`, because `MMSYSERR_NOERROR` / `TIMERR_NOERROR` / `JOYERR_NOERROR` are all `0`; (3) the latch variables MUST stay plain `volatile LONG` with constant initialisers (no `std::atomic`, no dynamic initialiser); (4) **never hold a non-recursive lock across `LoadLibraryW` in a proxy** — the load can re-enter our own exports on the same thread. Rationale, evidence, and the checklist to read before adding a `dxgi.dll` flavour: [lessons-learned.md §Proxy DLLs](lessons-learned.md#proxy-dlls--the-loader-can-call-our-exports-before-we-exist). Regression rig: `tools/verify/proxy_precrt_gate.py --all`
* **Thread-attach rule (Mono DLL)**: Direct calls to Mono runtime exports (`r.class_get_*`, `r.field_*`, …) from the pipe-worker thread must run inside a `Mono::ThreadAttachGuard` scope. Helper functions in `dll/src/mono/` already attach internally — do **not** wrap them in an outer guard (nested attach/detach broke build #54). New code that touches the runtime directly must open its own guard around the batch of bare resolver calls, after `Mono::Find*` / `Enumerate*` returns

---

## Architecture Overview

```
+------------------------------------------------------+
|  Game Process                                        |
|                                                      |
|  CE Lua                                              |
|    unity_dump.lua     <- DLL inject + pipe start     |
|    unity_invoke.lua   <- call managed methods        |
|      |  loadLibrary / callFunction (C ABI exports)   |
|      v                                               |
|  Discrete.dll  [injected]                         |
|      +-- ExportAPI       <- C ABI for CE Lua         |
|      +-- PipeServer      <- Named Pipe IPC (JSON-RPC)|
|      +-- BackendDetector <- Mono vs IL2CPP auto-detect|
|      +-- Signatures      <- AOB patterns + exports   |
|      |                                               |
|      +-- [Mono Backend]                              |
|      |   +-- MonoResolver    <- mono API call bridge |
|      |   +-- MonoDomain      <- Domain/Assembly walk |
|      |   +-- MonoClassWalker <- Field/Method/Prop    |
|      |   +-- MonoTypeParser  <- Type string resolver |
|      |                                               |
|      +-- [IL2CPP Backend]                            |
|      |   +-- IL2CPPResolver  <- il2cpp API call bridge|
|      |   +-- MetadataParser  <- global-metadata.dat  |
|      |   +-- IL2CPPDomain    <- Domain/Assembly walk  |
|      |   +-- IL2CPPClassWalker <- Field/Method/Prop  |
|      |   +-- IL2CPPTypeParser <- TypeDefinition walk  |
|      |                                               |
|      +-- [Shared]                                    |
|      |   +-- Memory          <- AOBScan + SEH r/w    |
|      |   +-- Logger          <- 5-category per-proc  |
|      |   +-- FieldExporter   <- CE symbol / XML / CSX|
|      |                                               |
+----------------------+-------------------------------+
                       | Named Pipe: \\.\pipe\DiscreteBfx
                       | JSON over pipe (newline-delimited)
+----------------------v-------------------------------+
|  UnityDumpUI (Avalonia + ReactiveUI, standalone exe) |
|      +-- PipeClient          <- async pipe mgmt     |
|      +-- AssemblyTreePanel   <- Assembly hierarchy   |
|      +-- ClassBrowserPanel   <- Namespace/Class tree |
|      +-- FieldStructPanel    <- Field/Property Grid  |
|      +-- MethodListPanel     <- Method signatures    |
|      +-- InstanceFinderPanel <- Class instance search|
|      +-- LiveWalkerPanel     <- Instance drill-down  |
|      +-- StaticFieldPanel    <- Static field viewer  |
|      +-- ProxyDeployPanel    <- Proxy DLL management |
|      +-- CeXmlExport        <- CE XML pointer chains |
|      +-- CsxExport          <- CE Structure Dissect  |
+------------------------------------------------------+
```

---

## Backend Detection Flow

The DLL auto-detects the Unity backend at injection time:

```
1. Check loaded modules:
   - "mono-2.0-bdwgc.dll" or "mono.dll" present → Mono backend
   - "GameAssembly.dll" present → IL2CPP backend
   - Both absent → error (not a Unity game)

2. Report backend type in handshake response:
   {
     "engine": "unity",
     "backend": "mono" | "il2cpp",
     "unity_version": "2021.3.16f1",  // from globalgamemanagers or data.unity3d
     "il2cpp_version": 29             // IL2CPP metadata version (IL2CPP only)
   }

3. UI adapts panels based on backend:
   - Mono: shows MonoImage-based tree, JIT method addresses
   - IL2CPP: shows metadata-based tree, AOT method addresses
   - Both: share Field/Instance/LiveWalker/Export panels
```

---

## Mono Backend Details

### Entry Point Resolution

Mono exports its API from `mono-2.0-bdwgc.dll` (or `mono.dll` in older Unity). Key exports to resolve:

| Export Function | Purpose |
|----------------|---------|
| `mono_get_root_domain` | Get the active MonoDomain |
| `mono_domain_get_assemblies` | Enumerate loaded assemblies |
| `mono_assembly_get_image` | Get MonoImage from assembly |
| `mono_image_get_table_rows` | Get number of types in image |
| `mono_class_from_name` | Lookup class by namespace + name |
| `mono_class_get_name` | Get class name string |
| `mono_class_get_namespace` | Get namespace string |
| `mono_class_get_fields` | Iterate fields (MonoClassField*) |
| `mono_class_get_methods` | Iterate methods (MonoMethod*) |
| `mono_class_get_properties` | Iterate properties |
| `mono_class_get_parent` | Get parent class (inheritance) |
| `mono_field_get_name` | Field name |
| `mono_field_get_type` | Field type (MonoType*) |
| `mono_field_get_offset` | Field offset in instance |
| `mono_method_get_name` | Method name |
| `mono_compile_method` | Force JIT compile, get native addr |
| `mono_class_vtable` | Get vtable for static field access |
| `mono_vtable_get_static_field_data` | Pointer to static fields block |
| `mono_type_get_name` | Type name as string |
| `mono_type_get_type` | MonoTypeEnum (int, float, object...) |

### Walk Strategy

```
mono_get_root_domain()
  → mono_domain_get_assemblies(domain, &count)
    → for each assembly:
        mono_assembly_get_image(assembly)
          → mono_image_get_table_rows(image, MONO_TABLE_TYPEDEF)
            → for each type index:
                mono_class_get(image, MONO_TOKEN_TYPE_DEF | (i+1))
                  → mono_class_get_name(klass)
                  → mono_class_get_namespace(klass)
                  → mono_class_get_fields(klass, &iter)
                    → mono_field_get_name / get_offset / get_type
                  → mono_class_get_methods(klass, &iter)
                    → mono_method_get_name
                    → mono_compile_method → native address
```

### Mono-Specific Concerns

* **Iterator pattern**: `mono_class_get_fields` uses a `void* iter = NULL` pattern. Call repeatedly until returns NULL.
* **GC safety**: Calling mono API from an injected thread is generally safe for read-only metadata queries, but `mono_compile_method` can trigger GC. Consider `mono_thread_attach(domain)` before heavy API use.
* **Older Unity (< 2017)**: Uses `mono.dll` instead of `mono-2.0-bdwgc.dll`. Export names are the same.
* **MonoClass internal layout varies**: Don't walk MonoClass fields manually when API functions exist. The struct layout changes between Mono versions (embedded in Unity).

---

## IL2CPP Backend Details

### Entry Point Resolution

`GameAssembly.dll` exports the il2cpp API:

| Export Function | Purpose |
|----------------|---------|
| `il2cpp_init` | (already called by Unity runtime) |
| `il2cpp_domain_get` | Get the Il2CppDomain |
| `il2cpp_domain_get_assemblies` | Enumerate assemblies |
| `il2cpp_assembly_get_image` | Get Il2CppImage from assembly |
| `il2cpp_image_get_class_count` | Number of types in image |
| `il2cpp_image_get_class` | Get class by index |
| `il2cpp_class_get_name` | Class name |
| `il2cpp_class_get_namespace` | Namespace |
| `il2cpp_class_get_fields` | Iterate fields |
| `il2cpp_class_get_methods` | Iterate methods |
| `il2cpp_class_get_parent` | Parent class |
| `il2cpp_class_get_type` | Get Il2CppType* |
| `il2cpp_field_get_name` | Field name |
| `il2cpp_field_get_type` | Field type |
| `il2cpp_field_get_offset` | Field offset |
| `il2cpp_field_is_literal` | Is const/enum value |
| `il2cpp_method_get_name` | Method name |
| `il2cpp_method_get_param_count` | Parameter count |
| `il2cpp_method_get_param` | Get param type by index |
| `il2cpp_method_get_return_type` | Return type |
| `il2cpp_type_get_name` | Type name string |
| `il2cpp_class_get_static_field_data` | Pointer to static fields |
| `il2cpp_string_chars` | Get Il2CppString wchar_t* |
| `il2cpp_array_length` | Get Il2CppArray length |

### Walk Strategy

```
il2cpp_domain_get()
  → il2cpp_domain_get_assemblies(domain, &count)
    → for each assembly:
        il2cpp_assembly_get_image(assembly)
          → il2cpp_image_get_class_count(image)
            → for each index:
                il2cpp_image_get_class(image, index)
                  → il2cpp_class_get_name(klass)
                  → il2cpp_class_get_namespace(klass)
                  → il2cpp_class_get_fields(klass, &iter)
                    → il2cpp_field_get_name / get_offset / get_type
                  → il2cpp_class_get_methods(klass, &iter)
                    → method pointer is the native AOT address
```

### global-metadata.dat (Optional Deep Parse)

For deeper analysis (string literals, enum values, default values), parse the metadata file:

```
Header (first 4 bytes): 0xFAB11BAF (magic)
  → sanity + version (field at offset 4)
  → offset tables for: StringLiteral, String, Events, Properties,
     Methods, ParameterDefault, FieldDefault, GenericContainers,
     GenericParameters, TypeDefinitions, Images, Assemblies, ...

Key tables:
  - Il2CppTypeDefinition[] → class metadata (nameIndex, fieldStart, fieldCount, methodStart, methodCount)
  - Il2CppFieldDefinition[] → field metadata (nameIndex, typeIndex)
  - Il2CppMethodDefinition[] → method metadata (nameIndex, returnType, parameterStart)
  - String heap → indexed by nameIndex, null-terminated UTF-8

The metadata version (24, 27, 29, 31...) determines struct sizes.
```

### IL2CPP-Specific Concerns

* **Static fields**: `il2cpp_class_get_static_field_data(klass)` returns a pointer to a separate memory block. Field offsets from `il2cpp_field_get_offset` are relative to this block for static fields.
* **Generic types**: `List<T>`, `Dictionary<K,V>` are instantiated as separate Il2CppClass entries. The generic class and its instances are different objects.
* **Method pointers**: Unlike Mono JIT, IL2CPP methods are AOT compiled. The method pointer in `Il2CppMethodInfo` directly points to native code in `GameAssembly.dll`.
* **Metadata version varies**: Unity 2020 uses v24, Unity 2021 uses v27/29, Unity 2022+ uses v29/31. The `Il2CppGlobalMetadataHeader` struct size and field positions change.
* **Obfuscation**: Some games strip metadata or use tools like `Il2CppObfuscator`. Detect by checking if class/field names are garbled (e.g., all single-letter names).

---

## Pipe Protocol (JSON-RPC)

Pipe name: `\\.\pipe\DiscreteBfx`

### Handshake

```json
// Request
{ "cmd": "handshake", "id": 1 }

// Response
{
  "id": 1,
  "engine": "unity",
  "backend": "mono" | "il2cpp",
  "unity_version": "2021.3.16f1",
  "metadata_version": 29,
  "game_module": "MyGame.exe",
  "assemblies_count": 42,
  "status": "ready"
}
```

### Core Commands (both backends)

| Command | Description |
|---------|-------------|
| `handshake` | Get engine info + backend type |
| `scan` | Trigger AOB scan / API resolution |
| `getAssemblies` | List all loaded assemblies |
| `getClasses` | List classes in an assembly (with namespace grouping) |
| `getClassInfo` | Get full class detail: parent, fields, methods, properties, interfaces |
| `getFieldInfo` | Get single field detail with type, offset, attributes |
| `getMethodInfo` | Get method detail: params, return type, native address |
| `getStaticFieldData` | Read static field values for a class |
| `findInstances` | Scan for live instances of a class (GC heap walk or pointer scan) |
| `readMemory` | Read bytes at address (for LiveWalker) |
| `writeMemory` | Write bytes at address |
| `registerSymbol` | Register a CE symbol (name → address mapping) |
| `exportCeXml` | Generate CE XML for a class/struct |
| `exportCsx` | Generate CE Structure Dissect for a class |
| `exportSdkHeader` | Generate C/C++ header for a class |
| `getEnumValues` | Get enum member names and values |
| `resolvePointer` | Follow pointer chain, return final address + type info |
| `walkInstance` | Read instance fields with live values (for LiveWalker) |

### Mono-Only Commands

| Command | Description |
|---------|-------------|
| `monoCompileMethod` | Force JIT compile a method, return native address |
| `monoGetVTable` | Get vtable for a class |

### IL2CPP-Only Commands

| Command | Description |
|---------|-------------|
| `il2cppGetMetadataStrings` | Read string literals from metadata |
| `il2cppGetGenericInstances` | List generic instantiations of a class |

---

## CE Integration

### Symbol Registration Pattern

For each class dumped, register CE symbols following this naming convention:

```
// Instance fields:
Unity:{AssemblyName}:{Namespace}.{ClassName}.{FieldName}       → offset value

// Static fields:
Unity:{AssemblyName}:{Namespace}.{ClassName}._static            → pointer to static block
Unity:{AssemblyName}:{Namespace}.{ClassName}.s_{FieldName}      → offset within static block

// Methods:
Unity:{AssemblyName}:{Namespace}.{ClassName}.m_{MethodName}     → native address

// Class metadata pointer:
Unity:{AssemblyName}:{Namespace}.{ClassName}._klass             → MonoClass* or Il2CppClass*

// NOTE: methods previously used `::` as the separator. CE's symbol resolver
// treats `::` as ambiguous and falls through to a slow Mono-runtime
// enumeration that hangs the UI for minutes when called against a large
// game (Hollow Knight Silksong's Mono runtime, ~half a million symbols).
// The `.m_` form mirrors the `.s_` static-field convention and resolves
// through the fast local-symbol path. unity_symbols.lua emits `.m_` since
// build #159+; older cheat tables hardcoding `::` will need updating.
```

### CE Table Structure

```
[Unity Game Name]
├── [Engine Info]
│   ├── Backend: Mono / IL2CPP
│   ├── Unity Version: 2021.3.x
│   └── GameAssembly.dll / mono-2.0-bdwgc.dll base
├── [Assembly-CSharp]
│   ├── [PlayerController]
│   │   ├── health (offset: +0x48, type: Single)
│   │   ├── maxHealth (offset: +0x4C, type: Single)
│   │   ├── position (offset: +0x50, type: Vector3)
│   │   └── [Static Fields]
│   │       └── instance (offset: +0x00 from static base)
│   ├── [EnemyManager]
│   │   └── ...
│   └── ...
├── [UnityEngine.CoreModule]
│   └── ...
└── [Symbols] (auto-registered)
```

---

## Export Formats

### CE XML (pointer chain address records)

Same format as UE5CEDumper — generates `<CheatEntries>` with address + offset chains. For Unity, the root is typically the static instance pointer → field offsets.

### CSX (CE Structure Dissect)

Generates structure definition importable into CE's Structure Dissect view. Maps Unity types to CE types:

| Unity Type | CE Type |
|-----------|---------|
| System.Boolean | Byte |
| System.Byte | Byte |
| System.Int16 | 2 Bytes |
| System.Int32 | 4 Bytes |
| System.Int64 | 8 Bytes |
| System.Single | Float |
| System.Double | Double |
| System.String | Pointer (→ Il2CppString / MonoString) |
| UnityEngine.Vector2 | 8 Bytes (2× Float) |
| UnityEngine.Vector3 | 12 Bytes (3× Float) |
| UnityEngine.Vector4 | 16 Bytes (4× Float) |
| UnityEngine.Quaternion | 16 Bytes (4× Float) |
| UnityEngine.Color | 16 Bytes (4× Float) |
| System.Collections.Generic.List\`1 | Pointer (→ items array) |
| T[] | Pointer (→ Il2CppArray / MonoArray) |
| Any class/interface | Pointer (8 Bytes on x64) |

### SDK Header (.h)

```c
// Assembly: Assembly-CSharp
// Class: GameNamespace.PlayerController
// Parent: UnityEngine.MonoBehaviour
struct PlayerController {
    // MonoBehaviour base: 0x00 - 0x18
    float health;          // +0x48
    float maxHealth;       // +0x4C
    Vector3 position;      // +0x50
    int level;             // +0x5C
    // ...
};
```

---

## Unity Type System Reference

### Instance Memory Layout (Mono)

```
[MonoObject header]     +0x00: vtable pointer (8 bytes)
                        +0x08: synchronisation (8 bytes)
[Parent fields]         +0x10: MonoBehaviour fields...
[This class fields]     +0xNN: declared fields start here
```

### Instance Memory Layout (IL2CPP)

```
[Il2CppObject header]   +0x00: Il2CppClass* klass (8 bytes)
                         +0x08: MonitorData* monitor (8 bytes)
[Parent fields]          +0x10: MonoBehaviour fields...
[This class fields]      +0xNN: declared fields start here
```

### String Layout

```
// Both Mono and IL2CPP (Il2CppString / MonoString):
+0x00: object header (16 bytes)
+0x10: int length (4 bytes)
+0x14: wchar_t[] chars (UTF-16LE, not null-terminated by length)
```

### Array Layout

```
// Il2CppArray / MonoArray:
+0x00: object header (16 bytes)
+0x10: Il2CppArrayBounds* bounds (8 bytes, NULL for single-dim)
+0x18: il2cpp_array_size_t max_length (8 bytes)
+0x20: elements start here (element[0])
```

### List<T> Layout

```
// System.Collections.Generic.List`1:
+0x10: T[] _items      → pointer to Il2CppArray / MonoArray
+0x18: int _size        → actual element count (not capacity)
+0x1C: int _version     → modification counter
```

---

## Directory Structure

```
Discrete/
├── CLAUDE.md                    ← this file
├── CMakeLists.txt               ← C++ DLL build (Mono + IL2CPP targets)
├── build.ps1                    ← Unified build script
├── build.cmd                    ← CMD wrapper for build.ps1
├── build_number.txt             ← Auto-incremented build number
├── .gitignore
├── .gitmodules
├── LICENSE
├── Readme.MD
├── Readme_zh-TW.MD
├── CONTRIBUTING.md
│
├── dll/                         ← C++ DLL source
│   ├── src/
│   │   ├── main.cpp             ← DllMain + init
│   │   ├── ExportAPI.cpp/.h     ← C ABI exports for CE Lua
│   │   ├── PipeServer.cpp/.h    ← Named Pipe JSON-RPC server
│   │   ├── BackendDetector.cpp/.h ← Mono vs IL2CPP detection
│   │   │
│   │   ├── shared/              ← Engine-agnostic modules
│   │   │   ├── Memory.cpp/.h    ← AOBScan, SEH read/write
│   │   │   ├── Logger.cpp/.h    ← 5-category logging
│   │   │   ├── ModuleHelper.cpp/.h ← GetModuleHandle, export resolver
│   │   │   └── FieldExporter.cpp/.h ← CE XML / CSX / SDK header gen
│   │   │
│   │   ├── mono/                ← Mono backend
│   │   │   ├── MonoResolver.cpp/.h    ← Resolve mono_* exports
│   │   │   ├── MonoDomain.cpp/.h      ← Domain → Assembly → Image walk
│   │   │   ├── MonoClassWalker.cpp/.h ← Class → Field/Method iteration
│   │   │   └── MonoTypeParser.cpp/.h  ← MonoType → string conversion
│   │   │
│   │   ├── il2cpp/              ← IL2CPP backend
│   │   │   ├── IL2CPPResolver.cpp/.h    ← Resolve il2cpp_* exports
│   │   │   ├── MetadataParser.cpp/.h    ← global-metadata.dat reader
│   │   │   ├── IL2CPPDomain.cpp/.h      ← Domain → Assembly → Image walk
│   │   │   ├── IL2CPPClassWalker.cpp/.h ← Class → Field/Method iteration
│   │   │   └── IL2CPPTypeParser.cpp/.h  ← Il2CppType → string conversion
│   │   │
│   │   └── proxy/               ← Proxy DLL (version.dll)
│   │       ├── ProxyMain.cpp
│   │       └── version.def
│   │
│   └── vendor/                  ← Third-party (nlohmann/json, etc.)
│
├── scripts/                     ← CE Lua scripts
│   ├── unity_dump.lua           ← DLL inject + pipe start
│   ├── unity_invoke.lua         ← Call managed methods
│   └── unity_symbols.lua        ← Batch symbol registration
│
├── ui/                          ← C# Avalonia UI
│   ├── UnityDumpUI/             ← Main UI project
│   │   ├── App.axaml
│   │   ├── ViewModels/
│   │   ├── Views/
│   │   ├── Models/
│   │   ├── Services/
│   │   │   ├── PipeClient.cs
│   │   │   ├── ProtocolHandler.cs
│   │   │   └── BackendAdapter.cs  ← Abstracts Mono/IL2CPP differences
│   │   └── Resources/
│   │       └── Strings/en.axaml
│   └── UnityDumpUI.Tests/       ← Unit tests
│
├── docs/                        ← Documentation
│   ├── architecture.md
│   ├── dll-spec.md
│   ├── pipe-protocol.md
│   ├── ui-spec.md
│   ├── export-formats.md
│   ├── mono-internals.md        ← Mono struct layouts, version diffs
│   ├── il2cpp-internals.md      ← IL2CPP struct layouts, metadata versions
│   ├── technical-notes.md
│   ├── lessons-learned.md
│   └── test-games.md
│
├── tools/
│   └── ghidra/                  ← Ghidra helper scripts
│
└── img/                         ← Screenshots, diagrams
```

---

## Known Challenges & Pitfalls

### Mono

1. **Thread safety**: Mono API calls from injected threads need `mono_thread_attach()` first. Forgetting this causes random crashes.
2. **Domain reloads**: In Unity Editor (dev builds), domain reload can invalidate all cached pointers. Detect via `mono_domain_get()` changes.
3. **Generics**: `mono_class_get_fields` on open generic types (e.g., `List<T>`) returns fields with unresolved types. Need `mono_class_inflate_generic_method` for concrete instances.
4. **Value types (structs)**: `mono_field_get_offset` for fields inside a struct returns offset from struct start, not from the containing object.

### IL2CPP

1. **Metadata version matrix**: Major layout changes at v24 (Unity 2019), v27 (Unity 2021), v29 (Unity 2022), v31 (Unity 6). Must handle each.
2. **Stripped assemblies**: Some builds strip unused classes/methods from metadata. `il2cpp_image_get_class_count` may return fewer types than expected.
3. **Static field initialization**: Static fields may not be initialized until the class is first accessed. `il2cpp_runtime_class_init(klass)` forces init but can trigger game logic.
4. **il2cpp_class_get_methods iterator**: Same `void* iter = NULL` pattern as Mono. Must reset iter for each class.
5. **Pointer-sized int**: `il2cpp_array_length` returns `il2cpp_array_size_t` which is pointer-sized (8 bytes on x64), not `int`.

### Both Backends

1. **UnityEngine types**: Core types like `Transform`, `GameObject`, `Component` are in native C++ land. Their managed fields are wrappers. The actual data lives in native `UnityEngine.dll` (not `GameAssembly.dll`). Accessing `Transform.position` through the managed field gives you a cached copy.
2. **Inheritance chain**: Always walk `mono_class_get_parent` / `il2cpp_class_get_parent` to get the full field layout. Fields from parent classes come first in memory.
3. **Alignment**: Unity uses standard C# alignment rules. `bool` fields are 1 byte but may have padding. Verify with actual offset values from the API.

---

## Documentation Index

| Document | Contents |
|----------|----------|
| [docs/architecture.md](docs/architecture.md) | Directory structure, git submodules, build environment |
| [docs/dll-spec.md](docs/dll-spec.md) | C++ DLL interface: C ABI exports, CE Lua bridge |
| [docs/pipe-protocol.md](docs/pipe-protocol.md) | Named Pipe JSON-RPC protocol (all commands, Request/Response/Event) |
| [docs/ui-spec.md](docs/ui-spec.md) | Avalonia UI tech stack, AOT compatibility, component specs |
| [docs/export-formats.md](docs/export-formats.md) | CE XML, CSX, SDK Header export rules, type mappings |
| [docs/mono-internals.md](docs/mono-internals.md) | Mono runtime internals, API reference, struct layouts by version |
| [docs/il2cpp-internals.md](docs/il2cpp-internals.md) | IL2CPP internals, metadata format, version differences |
| [docs/technical-notes.md](docs/technical-notes.md) | Unity version differences, backend detection, edge cases |
| [docs/lessons-learned.md](docs/lessons-learned.md) | Hard-won lessons from cross-game debugging |
| [docs/test-games.md](docs/test-games.md) | Test games with Unity versions, backend type, status |

---

## References & Credits

| Project | Use | alt loc |
|---------|-----|---------|
| [UE5CEDumper](https://github.com/bbfox0703/UE5CEDumper) | Architecture reference — pipe protocol, UI framework, CE integration | D:\Github\UE5CEDumper |
| [Zolt-Dump](https://github.com/bbfox0703/Zolt-Dump) | Architecture reference — pipe protocol, UI framework, CE integration | D:\Github\Zolt-Dump |
| RE-UNION | Architecture reference — pipe protocol, UI framework, CE integration | D:\Github\RE-UNION |
| AOBMaker | CE integration, lua and script creation into CE | D:\Github\AOBMaker |
| [Perfare/Il2CppDumper](https://github.com/Perfare/Il2CppDumper) | IL2CPP metadata format reference, global-metadata.dat parser | |
| [sinai-dev/UnityExplorer](https://github.com/sinai-dev/UnityExplorer) | Runtime Unity inspector reference, Mono/IL2CPP API usage patterns | |
| [knah/Il2CppAssemblyUnhollower](https://github.com/knah/Il2CppAssemblyUnhollower) | IL2CPP type system mapping reference | |
| [BepInEx/BepInEx](https://github.com/BepInEx/BepInEx) | Mono hook injection patterns, doorstop proxy technique | |
| [mono/mono](https://github.com/mono/mono) | Mono runtime source — struct definitions, API documentation | |
| [libil2cpp source (Unity)](https://github.com/nicknacknow/Il2CppVersions) | Il2CppClass/Il2CppType struct definitions per Unity version | |
| [nlohmann/json](https://github.com/nlohmann/json) | Header-only JSON library used in DLL | |
| [cheat-engine/cheat-engine](https://github.com/cheat-engine/cheat-engine) | CE Lua scripting API reference | D:\Github\cheat-engine |

---

**License**: [GPLv3](LICENSE) © 2026 bbfox0703
