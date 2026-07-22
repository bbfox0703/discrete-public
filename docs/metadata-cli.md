# `metadata-cli` — Offline IL2CPP Metadata Parser

> Standalone CLI tool that parses `global-metadata.dat` from any
> IL2CPP Unity game **without injecting into a process**. Useful for
> CI dumping, static analysis of stripped/obfuscated metadata, and
> sanity-checking `MetadataParser` against the live runtime.

Phase 13. Ships as `dist/metadata-cli.exe` (~250 KB — grown well past
the original ~70 KB as Phase D's PE parser / registration finder /
symbol pipeline landed).

---

## Why offline parsing

The DLL backend's `MetadataParser` runs against a live process —
locating `global-metadata.dat` in memory, dispatching by version,
walking type tables. Useful, but:

- **You may not want to launch the game** (especially for malware
  analysis, anti-cheat-protected titles, or just diff-checking
  metadata across versions).
- **CI / batch dumping** wants a one-shot tool that takes a file
  path, emits JSON, and exits.
- **Sanity checks**: when adding a new metadata version (v32, v38…)
  the CLI lets you verify `MetadataParser` produces the same view as
  the live runtime would, without reproducing the whole inject + RPC
  flow.

`metadata-cli` is a thin wrapper around the same `MetadataParser`
the DLL uses. Identical version dispatch, identical type-table
parsing, identical strip-ratio heuristic — so live and offline
results are directly comparable.

## Install

Built as part of `.\build.cmd publish`. Lands in
`dist\metadata-cli.exe`. Standalone — no DLL dependencies beyond the
standard MSVC runtime.

To build just the CLI:

```powershell
.\build.cmd cli
```

## Usage

```
metadata-cli <subcommand> <spec> [args]
```

Output is **line-oriented JSON** to stdout. Errors and diagnostic
notes go to stderr. Exit code 0 on success, non-zero on parse
failure.

Global option: **`--ascii`** — escape non-ASCII as `\uXXXX` so output
survives a shell that mangles UTF-8. See
[Non-ASCII names](#non-ascii-names-chcp-is-not-the-thing-to-check).

`metadata-cli --help` (also `-h` / `help` / `/?`) prints the full usage,
including the PowerShell encoding guidance. The help text is
deliberately **pure 7-bit ASCII** — it's what you read *while* your
shell is mangling UTF-8, so it must stay legible under any code page.

### `<spec>` — you don't have to type the full path

Both artefacts the CLI reads (`global-metadata.dat`, `GameAssembly.dll`)
live at fixed offsets from a Unity game's install root, so the tool
finds them itself. Every subcommand's first argument accepts three
shapes, resolved in this order:

| `<spec>` | Behaviour |
|---|---|
| **An existing file** — `…\Metadata\global-metadata.dat` | Used verbatim. |
| **A game install root** — `D:\SteamLibrary\steamapps\common\My Game` | The artefact is located by Unity's layout rules. |
| **A Steam game name** — `"FINAL FANTASY VI"` | Install path looked up via the registry + `libraryfolders.vdf`, then resolved as a game root. |

Two hard rules, so the tool never quietly parses a different file than
the one you asked for:

- **A path-shaped spec that doesn't exist is a hard failure.** Anything
  containing a separator, a drive letter, a leading `.`, or a
  `.dat`/`.dll`/`.exe` suffix is a path — a missing one fails, it is
  never reinterpreted as a Steam name.
- **Auto-detection failing is a hard failure.** No fallback guessing;
  stderr lists every location that was probed.

```powershell
# All three are equivalent:
metadata-cli stats "D:\SteamLibrary\steamapps\common\SaGa Frontier Remastered\SaGa Frontier Remastered_Data\il2cpp_data\Metadata\global-metadata.dat"
metadata-cli stats "D:\SteamLibrary\steamapps\common\SaGa Frontier Remastered"
metadata-cli stats "SaGa Frontier Remastered"
```

When the spec wasn't a literal file, the resolved path is echoed on
stderr so stdout stays pure JSON:

```
metadata-cli: using D:\...\global-metadata.dat  [steam 'SaGa Frontier Remastered' -> D:\SteamLibrary\steamapps\common\SaGa Frontier Remastered]
```

**Layouts probed** from a directory spec — the spec may be the install
root or any of its Unity sub-directories (`<Game>_Data`, `il2cpp_data`,
`Metadata`), which are climbed out of first. One extra level below the
root is also searched, for installs that nest the payload:

- `<root>\<Game>_Data\il2cpp_data\Metadata\global-metadata.dat`
- `<root>\il2cpp_data\Metadata\global-metadata.dat`
- `<root>\Metadata\global-metadata.dat`
- `<root>\global-metadata.dat`
- `<root>\GameAssembly.dll`

**Steam name matching** is case- and punctuation-insensitive, and works
against both the store name from `appmanifest_*.acf` and the install
folder name — which frequently differ (`Persona 3 Reload` → `P3R`,
`FINAL FANTASY VI` → `FINAL FANTASY VI PR`). Exact match beats
normalised match beats substring; an ambiguous substring lists the
candidates and fails rather than picking one:

```powershell
metadata-cli info "Kiseki"
# → metadata-cli: 'Kiseki' is ambiguous — 3 Steam games match:
#       (run `metadata-cli steam-vdf` to see every name -> folder mapping)
#       The Legend of Heroes: Zero no Kiseki Kai  ->  D:\SteamLibrary\steamapps\common\...
#       Sora no Kiseki the 1st                    ->  D:\SteamLibrary\steamapps\common\...
#       The Legend of Heroes: Ao no Kiseki KAI    ->  E:\SteamLibrary\steamapps\common\...
#     Pass the install directory explicitly.
```

Non-ASCII store names work — the CLI entry point is `wmain`, so a
CJK-named game (`活俠傳`) survives argv intact rather than being
mangled by the ANSI code page.

### Subcommands

#### `info` — version + table sizes

Prints one JSON object describing the metadata header:

```powershell
metadata-cli info "C:\Path\To\game_Data\il2cpp_data\Metadata\global-metadata.dat"
```

```json
{
  "version": 31,
  "loaded": true,
  "type_tables_parsed": true,
  "type_definition_count": 45123,
  "string_literal_count": 89342
}
```

If `type_tables_parsed` is `false`, the version is recognized but
the type-table layout for that version isn't shipped yet (currently
v24/v27/v29/v31 are dispatched; older / newer versions only get the
string-literal path).

#### `stats` — strip / obfuscation heuristic

Same heuristic as the DLL's `getMetadataStats` RPC. Counts single-
char and two-char user-type names (excluding `<...>` compiler-
generated types) and emits a verdict.

```powershell
metadata-cli stats "C:\...\global-metadata.dat"
```

```json
{
  "total_classes": 45123,
  "with_namespace": 32104,
  "single_char_names": 124,
  "short_names_2": 893,
  "compiler_generated": 5421,
  "numeric_lead": 12,
  "min_name_length": 1,
  "max_name_length": 87,
  "avg_name_length": 18.42,
  "strip_ratio": 0.0256,
  "verdict": "clean"
}
```

Verdict mapping:

| `strip_ratio` | Verdict | Meaning |
|---|---|---|
| > 0.30 | `heavy_strip` | Most class names are 1–2 chars — usable but UI shows scary banner |
| > 0.10 | `partial_strip` | Some stripping detected — most names readable, some single-char clutter |
| ≤ 0.10 | `clean` | Production build with debug symbols intact (typical for early-access games / mods) |

#### `types` — JSON-line per type

One JSON object per line for every TypeDefinition. Useful for `jq` /
text-tool diffing.

```powershell
metadata-cli types "C:\...\global-metadata.dat" > types.jsonl
```

Each line:

```json
{"index":0,"namespace":"","name":"<Module>","field_count":0,"method_count":0,"token":33554432}
{"index":1,"namespace":"System","name":"Object","field_count":0,"method_count":7,"token":33554433}
{"index":2,"namespace":"System","name":"ValueType","field_count":0,"method_count":3,"token":33554434}
...
```

Filter with `jq` for ad-hoc queries:

```powershell
# Count types per namespace
metadata-cli types game-metadata.dat | jq -s 'group_by(.namespace) | map({ns: .[0].namespace, n: length}) | sort_by(-.n)'

# Find types with > 100 fields (interesting candidates for inspection)
metadata-cli types game-metadata.dat | jq 'select(.field_count > 100)'
```

#### `type` — fields + defaults of one type

Drill into a single type by namespace + name:

```powershell
metadata-cli type "C:\...\global-metadata.dat" "MainGame" "PLY_WORK"
```

```json
{
  "namespace": "MainGame",
  "name": "PLY_WORK",
  "index": 12345,
  "token": 33567778,
  "fields": [
    {"index": 0, "name": "HP", "type_index": 4321, "offset": 16, "is_static": false},
    {"index": 1, "name": "MAX_HP", "type_index": 4321, "offset": 20, "is_static": false},
    {"index": 2, "name": "LV", "type_index": 4322, "offset": 24, "is_static": false},
    ...
  ]
}
```

For enums, fields with `FieldDefaultValue` entries also include the
typed default value.

#### `methods` — JSON line per method

One JSON object per line for every `MethodDefinition` in the file.
Includes the declaring type's FQN so consumers don't need a second
lookup. Useful for `jq` / text-tool slicing on large method tables
(Heroes of Olden Era is ~200K methods; Songs of Silence ~250K).

```powershell
metadata-cli methods "C:\...\global-metadata.dat" > methods.jsonl
```

Each line:

```json
{"index":4,"declaring_namespace":"System","declaring_type":"Object","name":"GetHashCode","return_type_index":7,"parameter_count":0,"token":100663301,"flags":454}
```

`return_type_index` references the runtime `Il2CppType` table which
lives in `GameAssembly.dll`, not in `global-metadata.dat`. The CLI
emits it as a raw index — full resolution lands in the planned
[il2cppdumper-comparison.md](il2cppdumper-comparison.md) **Phase D**
(offline PE parser). Same caveat applies to `parameters[].type_index`.

#### `method` — full decode of one method (with parameters)

```powershell
metadata-cli method "C:\...\global-metadata.dat" "System" "String" "Concat"
```

Returns all overloads as a JSON array — IL2CPP supports overloading,
so a name lookup may match multiple methods:

```json
{
  "namespace": "System",
  "type": "String",
  "method_name": "Concat",
  "overloads": [
    {
      "index": 73390,
      "token": 100663971,
      "return_type_index": 48643,
      "flags": 150,
      "parameters": [
        {"name": "str0", "index": 55305, "type_index": 48643, "token": 134218896},
        {"name": "str1", "index": 55306, "type_index": 48643, "token": 134218897}
      ]
    },
    ...
  ]
}
```

Verified across the test corpus (v24.0 / v24.5 / v29 / v31 / v39):
the same call returns 6–7 overloads for `String.Concat` with
parameter types consistent across overloads (e.g. all string-only
overloads share the same `type_index`). Phase B docs at
[il2cppdumper-comparison.md](il2cppdumper-comparison.md).

#### `pe-info` — inspect GameAssembly.dll headers

Phase D step 1+2 — minimal PE parser shipped as part of the CLI.
Reads `GameAssembly.dll`'s DOS / NT / section headers and dumps a
JSON description. Useful on its own for verifying a binary looks
like an IL2CPP runtime before chaining into the planned `symbols`
subcommand (Phase D step 5 — see
[il2cppdumper-comparison.md](il2cppdumper-comparison.md)).

```powershell
metadata-cli pe-info "C:\Path\To\game_Data\..\GameAssembly.dll"
```

```json
{
  "machine": "x64",
  "is64Bit": true,
  "imageBase": "0x0000000180000000",
  "entryPointRva": "0x00520214",
  "sizeOfImage": 98496512,
  "sectionCount": 7,
  "hasIl2CppMarker": true,
  "sections": [
    {"name": ".text",  "rva": "0x00001000", "virtualSize": 5603812, "fileOffset": "0x00000400", "rawSize": 5603840,  "characteristics": "0x60000020"},
    {"name": "il2cpp", "rva": "0x0055A000", "virtualSize": 65290316,"fileOffset": "0x00558600", "rawSize": 65290752, "characteristics": "0x60000020"},
    {"name": ".rdata", "rva": "0x0439F000", "virtualSize": 14808108,"fileOffset": "0x0439C800", "rawSize": 14808576, "characteristics": "0x40000040"},
    ...
  ]
}
```

Cross-corpus verified (build #499) — all 4 test-matrix IL2CPP
targets parse cleanly: R-Type Dimensions (Unity 2018.1, 12 MB,
5 sections), SaGa F2R (Unity 2021.3, 45 MB, 7 sections incl.
separate `il2cpp` exec section), Heroes (Unity 6.0 LTS, 95 MB,
7 sections), Songs of Silence (Unity 6.1, 170 MB, 7 sections).
`hasIl2CppMarker` correctly distinguishes IL2CPP from generic
Windows DLLs (`kernel32.dll` parses but reports `false`).

Negative cases fail loud with one-line diagnostics:

```powershell
metadata-cli pe-info docs\metadata-cli.md
# → "metadata-cli: pe-info failed: bad DOS magic (not a PE)", exit 1

metadata-cli pe-info D:\nonexistent\file.dll
# → "metadata-cli: pe-info failed: CreateFileW failed", exit 1
```

#### `registration` — offline Il2CppMetadataRegistration finder

Phase D step 3. Builds on `pe-info` — locates the runtime
`Il2CppMetadataRegistration` struct inside `GameAssembly.dll` by
porting the existing in-process AOB scanner to file bytes (see
[RegistrationFinder.cpp](../tools/metadata-cli/RegistrationFinder.cpp)).
No injection, no LoadLibraryEx; pure file-byte parse with
VA → RVA → file-offset translation via the PE section table.

```powershell
metadata-cli registration "C:\Path\To\game_Data\..\GameAssembly.dll"
```

```json
{
  "found": true,
  "image_base": "0x0000000180000000",
  "registration_rva": "0x023F4970",
  "registration_va": "0x00000001823F4970",
  "types_count": 29456,
  "types_array_rva": "0x023FD840",
  "types_array_va": "0x00000001823FD840",
  "section": ".rdata",
  "near_misses": 8
}
```

Cross-corpus verified (build #500) — same structural-shape scanner
finds the registration **version-agnostically** across v24.0 / v29
/ v31 / v39 (the metadata.dat format changes don't touch the
runtime registration struct shape):

| Game | Metadata | Section | types_count | Notes |
|---|---|---|---|---|
| R-Type Dimensions | v24.0 | `.data` | **19,874** | Matches the runtime scan reported in the test matrix EXACTLY |
| SaGa F2R | v29 | `.rdata` | 29,456 | |
| Heroes Olden Era | v31 | `.rdata` | 77,466 | |
| Songs of Silence | v39 | `.rdata` | 145,455 | Within 0.4% of the live runtime scan (144,837) |

`kernel32.dll` correctly fails with "no candidate matched the
structural shape" — no false positives on generic Win32 DLLs.

Foundation for the planned Phase D-4 (method-pointer resolution)
and D-5 (`symbols` subcommand → `.dd64` / Ghidra / IDA outputs).

#### `code-modules` — offline `Il2CppCodeRegistration::codeGenModules` walker

Phase D step 4. Locates the codeGenModules array directly (instead
of trying to validate the larger `Il2CppCodeRegistration` struct
that varies considerably across versions), and emits the per-module
breakdown the upcoming `symbols` subcommand will use to cross-link
metadata MethodDefinitions to native AOT addresses.

```powershell
metadata-cli code-modules "C:\Path\To\game_Data\..\GameAssembly.dll"
```

```json
{
  "found": true,
  "image_base": "0x0000000180000000",
  "code_gen_modules_array_rva": "0x05241EF0",
  "code_gen_modules_array_va":  "0x00000001852419F0",
  "module_count": 57,
  "section": ".data",
  "modules": [
    {
      "name": "Assembly-CSharp.dll",
      "module_rva": "0x05241EF0",
      "method_count": 51,
      "method_pointers_rva": "0x05242090",
      "sample_methods": ["0x0055BCA0", "0x0055BC00", "0x0055BC70"]
    },
    ...
  ]
}
```

Cross-corpus verification (build #501):

| Game | Metadata | Modules | Example | Notes |
|---|---|---|---|---|
| R-Type Dimensions | **v24.0** | n/a | — | Correctly rejected: pre-v24.2 IL2CPP has no `codeGenModules` — uses the flat `methodPointers` on `Il2CppCodeRegistration` directly. Old-format support TBD when there's a consumer. |
| SaGa F2R | v29 | 20 | Assembly-CSharp.dll = 20,161 methods | Sample shape: assemblies with mostly abstract / generic-only / inlined methods report `0x00000000` for the first slots (this is IL2CPP doing what it always does — `methodPointers[]` slots are null for methods that don't have a standalone AOT body) |
| Heroes Olden Era | v31 | 57 | Cysharp.Net.Http.YetAnotherHttpHandler.dll, Discord.dll, Epic.dll, Facepunch.Steamworks.Win64.dll | All sample methods are real non-zero addresses (e.g. `0x0055ECF0`) |
| Songs of Silence | v39 | 87 | Many Chimera.* internal modules | Confirms the same scan works on v38+ section-header metadata games |

The strong-signal heuristic: scan for runs of 8-byte-aligned
pointers where every entry dereferences to a struct whose **first
field is a pointer to a string ending in `.dll`**. That's the
unique signature of the codeGenModules array — Unity's IL2CPP
toolchain always names modules after their source assembly file,
and the moduleName slot has been the first struct field
unchanged from v24.2 through v39.

Foundation for Phase D-5 (`symbols` subcommand) — once we know
each module's `methodPointers[]` base + count, the remaining
work is to walk metadata.dat's Image table to compute each
method's (image, image-local index) pair and look up the address.

#### `symbols` — offline debugger-symbol export (Phase D-5, closes Phase D)

End-to-end pipeline: PE parse + offline `Il2CppMetadataRegistration`
finder + codeGenModules walker + metadata Image table walk + cross-
link to native AOT addresses + emit `.dd64` (x64dbg) + `.ghidra.txt`
+ `.idc` in one go. Equivalent of running the UI's Tools menu
**Export Symbols → x64dbg / Ghidra / IDA** triple without launching
the game.

`symbols` needs *both* artefacts, so it has a short form that resolves
the pair from one `<spec>`:

```powershell
metadata-cli symbols "SaGa Frontier Remastered" "D:\out\mygame"
# → produces D:\out\mygame.dd64
#            D:\out\mygame.ghidra.txt
#            D:\out\mygame.idc
```

The three-positional form still works, for scripts that already pass
both paths and for the rare install where the two artefacts don't share
a root. Each path goes through the resolver independently, so a game
root works in either slot:

```powershell
metadata-cli symbols `
    "C:\Path\To\GameAssembly.dll" `
    "C:\Path\To\game_Data\il2cpp_data\Metadata\global-metadata.dat" `
    "D:\out\mygame"
```

Naming one of the two files in the short form pins it and auto-locates
the other from the derived install root — so
`metadata-cli symbols "…\global-metadata.dat" out` finds the sibling
`GameAssembly.dll` on its own.

JSON stats summary to stdout:

```json
{
  "ok": true,
  "image_base": "0x0000000180000000",
  "images_total": 108,
  "images_matched": 57,
  "classes_total": 24682,
  "methods_total": 131353,
  "methods_resolved": 124460,
  "methods_null_ptr": 6893,
  "resolve_ratio": 0.9475,
  "output_prefix": "D:\\out\\mygame"
}
```

Cross-corpus verification (v0.2, build #511):

| Game | Images matched | Methods resolved | Resolve ratio |
|---|---|---|---|
| SaGa F2R (v29) | **54 / 57** | **49,743 / 54,134** | **91.89%** |
| Heroes (v31) | **106 / 108** | **176,658 / 190,021** | **92.97%** |
| Songs of Silence (v39) | **158 / 253** | **103,340 / 120,966** | **85.43%** |

v0.1 → v0.2 improvements: image coverage 35-65% → 62-98%; method
count up 37-69% per game. Remaining unmatched images (e.g. 95 on
Songs of Silence) are metadata-only references — assemblies whose
code lives outside this `GameAssembly.dll` (system libraries
referenced via reflection only, etc.). The codeGenModules scanner
now allows null entries within the run (v0.2.1) and accepts
modules with `methodCount==0` (interface-only / attribute-only
DLLs that were rejected as not-a-module in v0.1).

Cross-validated against the test-matrix-recorded live runtime data:
SaGa F2R's `PLY_WORK.GetWazNat` (token `0x060017D5`) resolves to
RVA `0x86C6D0`, which back-computes to the exact same RVA the live
`dumpIl2cppSymbols` path records (different absolute addresses
because of runtime ASLR base, but file-relative offsets match
bit-for-bit).

**Label naming** (matches the live `SymbolExporter.cs` convention):

- Class labels: `Class_<Namespace>_<Class>` — sanitised to `[A-Za-z0-9_]`.
- Method labels: `<Namespace>_<Class>__<Method>_t<TokenHex>` — token
  suffix disambiguates overloads. (The live path also computes a
  human-readable `humanSuffix` from parameter types; the offline path
  uses tokens because parameter-type resolution requires the
  `Il2CppType` table — deferred. Token suffix still guarantees
  uniqueness, just less human-friendly.)
- **Nested types** (v0.2.2) — `<NS>_<Outer>_<Inner>__<Method>` via a
  parent-walk through `nestedTypeIndices[]`. SaGa F2R's
  `PLY_WORK.GetWazNat` (nested in `MainGame`) now emits as
  `MainGame_PLY_WORK__GetWazNat_t060017D5`, matching what the live
  UI export produces (the UI walks `il2cpp_class_get_declaring_type`
  for the same result).

**Limitations** (v0.2):

- **Pre-v24.2 IL2CPP not supported** — those games lack
  `codeGenModules` and use a flat `methodPointers[]` directly on
  `Il2CppCodeRegistration`. R-Type Dimensions (v24.0) returns a
  clean "image table empty" error.
- ~~Nested-type names drop the parent prefix~~ — **fixed v0.2.2**
  via `nestedTypeIndices[]` walk; labels now match the live UI
  shape (`MainGame_PLY_WORK__GetWazNat_t...`).
- **~5-15% of methods report null pointers** — these are abstract /
  generic-only / inlined methods that legitimately have no AOT
  body. The live path skips them too.
- **Some assemblies unmatched per game** — fixed bulk of this in
  v0.2.1 (35-65% miss → 2-37% miss; coverage 62-98%). Remaining
  unmatched are metadata-only references — assemblies whose code
  lives outside this `GameAssembly.dll` (cross-DLL system
  references, reflection-only loads). The live `Tools → Export
  Symbols` path reaches everything by enumerating from the runtime
  side.

#### `resolve` — show what a `<spec>` maps to

Diagnostic surface for the path rules. Resolves without parsing
anything, so you can check auto-detection before kicking off a long
dump. Exits 1 only when *neither* artefact is found (a Mono game
legitimately has neither).

```powershell
metadata-cli resolve "FINAL FANTASY VI"
```

```json
{
  "resolved": true,
  "spec": "FINAL FANTASY VI",
  "origin": "steam-name",
  "game_root": "E:\\SteamLibrary\\steamapps\\common\\FINAL FANTASY VI PR",
  "global_metadata": "E:\\SteamLibrary\\steamapps\\common\\FINAL FANTASY VI PR\\FINAL FANTASY VI_Data\\il2cpp_data\\Metadata\\global-metadata.dat",
  "game_assembly": "E:\\SteamLibrary\\steamapps\\common\\FINAL FANTASY VI PR\\GameAssembly.dll",
  "note": "steam 'FINAL FANTASY VI' -> E:\\SteamLibrary\\steamapps\\common\\FINAL FANTASY VI PR"
}
```

`origin` is one of `explicit-file` / `game-root` / `steam-name`. When
the spec names one of the two files directly, that file is reported in
its own slot and the *other* is located from the derived install root —
unlike the parsing subcommands, which use an explicitly-named file
verbatim whatever they were asked for.

#### `steam-vdf` — list VDF name → install folder

The discoverability answer for "what name do I type?". Dumps what
Steam's VDF files declare: every library folder from
`libraryfolders.vdf`, and under each, the `appmanifest_*.acf`
name → install target folder mapping.

```powershell
metadata-cli steam-vdf
```

```
Steam libraries: 4   (apps: 125)

[D:\SteamLibrary]  25 apps
  APPID    NAME                                     INSTALL FOLDER
  1295270  SaGa Frontier Remastered                 D:\SteamLibrary\steamapps\common\SaGa Frontier Remastered
  2161700  Persona 3 Reload                         D:\SteamLibrary\steamapps\common\P3R
  2288150  ROMANCE OF THE THREE KINGDOMS 8  REMAKE  D:\SteamLibrary\steamapps\common\SAN8R
  -        3DMark                                   D:\SteamLibrary\steamapps\common\3DMark

[E:\SteamLibrary]  20 apps
  APPID    NAME                         INSTALL FOLDER
  1859910  活俠傳                       E:\SteamLibrary\steamapps\common\LegendOfMortal
  524220   NieR:Automata™               E:\SteamLibrary\steamapps\common\NieRAutomata
```

Columns are sized to the content — names are never truncated, since
the whole point is to show the string you'd pass as a `<spec>`.
Alignment is East-Asian-width aware, so CJK store names don't skew the
table.

- `APPID` is `-` for folders with no readable `appmanifest_*.acf`
  (manual copies, partial uninstalls). Those are still listed and still
  matchable, by folder name.
- Rows prefixed `!` have a manifest but **no install folder** —
  uninstalled, or moved outside Steam. They appear because they're
  genuinely in the VDF, but they are deliberately *not* matchable by
  name: a stale manifest must never win a lookup.

Exit 1 when Steam isn't installed.

##### Non-ASCII names: `chcp` is not the thing to check

All output is UTF-8. If a CJK game name comes out as mojibake —

```
{"app_id":"1859910","name":"瘣颱???,"install_dir":...     ← closing quote eaten
```

— that is not merely a display glitch. A legacy double-byte lead byte
swallows the following ASCII character, so the JSON loses its closing
quote and **stops parsing** (`ConvertFrom-Json` → *"必須有 ':' 或 '}'"*).

**The cause is the shell, not the console.** PowerShell never lets a
native program write to the console directly: it captures stdout into
a pipe and re-decodes the bytes using `[Console]::OutputEncoding`.
That value is cached when the shell starts, so a session launched
under a legacy code page keeps decoding as Big5 **even after
`chcp 65001` reports UTF-8**:

```
chcp = 65001   [Console]::OutputEncoding = big5     ← the actual trap
```

So `chcp` is the wrong diagnostic. Check the real one:

```powershell
[Console]::OutputEncoding
```

**Fix (recommended — helps every CLI tool, not just this one):**

```powershell
[Console]::OutputEncoding = [Text.Encoding]::UTF8
```

Verified to fix both `steam-games` and `steam-vdf` mid-session, on
Windows PowerShell 5.1 and PowerShell 7. Put it in your `$PROFILE` to
make it stick.

**Fix without touching the shell — `--ascii`:**

```powershell
metadata-cli steam-games --ascii
```

Escapes every non-ASCII character as `\uXXXX`, so the output is pure
7-bit ASCII and no decoding step can corrupt it. JSON parsers decode
the escapes natively, so the value you get back is the real name:

```json
{"app_id":"1859910","name":"活俠傳","install_dir":"E:\\SteamLibrary\\steamapps\\common\\LegendOfMortal", ...}
```
```powershell
(metadata-cli steam-games --ascii | ConvertFrom-Json).name   # → 活俠傳
```

`--ascii` may appear anywhere on the command line and applies to
`steam-vdf`'s table too (column alignment is preserved — the escaped
form is ASCII, so its width is its length).

For a CJK-named game there's also a shell-proof workaround: pass the
**install folder name** as the `<spec>` instead of the store name —
matching accepts either, and the folder name is usually ASCII
(`LegendOfMortal` for 活俠傳).

Separately, the tool switches the *console* to UTF-8 for the duration
of a run and restores the previous code page on exit. That covers
running under `cmd.exe`, whose console is typically cp950/cp932 — but
it cannot help the PowerShell case above, where stdout is a pipe
rather than a console. It also can't help `metadata-cli … > CON`,
because cmd opens `CON` write-only and `GetConsoleMode` needs read
access to identify a console handle.

#### `steam-games` — the same data as JSON

Machine-readable sibling of `steam-vdf`, for scripting. One JSON line
per install; excludes the `!` stale-manifest rows.

```powershell
metadata-cli steam-games
```

```json
{"app_id":"2161700","name":"Persona 3 Reload","install_dir":"D:\\SteamLibrary\\steamapps\\common\\P3R","library":"D:\\SteamLibrary"}
{"app_id":"1859910","name":"活俠傳","install_dir":"E:\\SteamLibrary\\steamapps\\common\\LegendOfMortal","library":"E:\\SteamLibrary"}
{"app_id":null,"name":"3DMark","install_dir":"D:\\SteamLibrary\\steamapps\\common\\3DMark","library":"D:\\SteamLibrary"}
```

```powershell
# Which installed Steam games are IL2CPP?
metadata-cli steam-games | jq -r .name | ForEach-Object {
    if (metadata-cli resolve $_ 2>$null) { $_ }
}
```

## Comparing CLI output to live RPC

Quick sanity check: the CLI's `info` output should match the DLL's
`handshake` (for `metadata_version`) and `getMetadataStats` (for the
verdict).

```powershell
# Offline:
metadata-cli stats "$gamedir\game_Data\il2cpp_data\Metadata\global-metadata.dat"

# Live (with the game running + DLL injected):
.\tools\Test-Handshake.ps1                    # gets metadata_version
.\tools\Test-Send.ps1 -Cmd getMetadataStats   # gets verdict
```

Mismatch indicates one of:

- The live runtime sees a remapped / decrypted metadata.dat (some
  protectors decrypt at runtime).
- The version dispatcher in `MetadataParser` is reading offset
  layouts wrong for a new version.
- The on-disk file was extracted from a different build than what's
  running.

## Caveats

- **Versions supported for type-table parsing**: v24, v27, v29, v31.
  Other versions in the [16, 31] range load (string heap works) but
  return `type_definition_count: 0`. v32+ uses a section-based header
  format documented in
  [il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md);
  not yet shipped.
- **Encrypted metadata** (some commercial protectors): the CLI parses
  the on-disk bytes literally. If they're encrypted, parse fails
  with `sanity_mismatch`. Use the live DLL path (which sees the
  decrypted in-memory copy).
- **No method bodies**. The CLI parses TypeDefinitions, FieldDefinitions,
  StringLiterals, FieldDefaults. MethodDefinitions are deferred (no
  current consumer). For method information, use the live DLL with
  `findMethodInstances` — it reads from the runtime `Il2CppClass*`
  tables, not metadata.dat.
- **No console truncation**. The `types` subcommand can emit tens of
  thousands of lines for large games. Pipe to a file or `jq`.

## Source

- [tools/metadata-cli/main.cpp](../tools/metadata-cli/main.cpp) —
  CLI entry point + JSON formatting.
- [tools/metadata-cli/PathResolver.h](../tools/metadata-cli/PathResolver.h)
  / [.cpp](../tools/metadata-cli/PathResolver.cpp) — `<spec>` resolution:
  Unity layout probing + Steam discovery (registry → `libraryfolders.vdf`
  → `appmanifest_*.acf`). The VDF tokeniser mirrors
  [ui/UnityDumpUI/Platform/SteamGameScanner.cs](../ui/UnityDumpUI/Platform/SteamGameScanner.cs).
- [tools/metadata-cli/CMakeLists.txt](../tools/metadata-cli/CMakeLists.txt)
  — build target (links against the same `MetadataParser` source the
  DLL uses).
- [dll/src/il2cpp/MetadataParser.h](../dll/src/il2cpp/MetadataParser.h)
  / [.cpp](../dll/src/il2cpp/MetadataParser.cpp) — version dispatcher
  + per-version type-table parsing.
- [dll/src/il2cpp/MetadataLayout.h](../dll/src/il2cpp/MetadataLayout.h)
  — per-version struct layouts (cross-checked vs upstream Unity in
  `vendor/libil2cpp/`).

## Related

- [il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md) —
  audit of our metadata layouts against upstream Unity sources.
- [il2cpp-internals.md](il2cpp-internals.md) — IL2CPP backend deep
  dive (live runtime path, complementary to the offline path).
