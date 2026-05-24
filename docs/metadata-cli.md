# `metadata-cli` — Offline IL2CPP Metadata Parser

> Standalone CLI tool that parses `global-metadata.dat` from any
> IL2CPP Unity game **without injecting into a process**. Useful for
> CI dumping, static analysis of stripped/obfuscated metadata, and
> sanity-checking `MetadataParser` against the live runtime.

Phase 13. Ships as `dist/metadata-cli.exe` (~70 KB).

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
metadata-cli <subcommand> <path-to-global-metadata.dat> [args]
```

Output is **line-oriented JSON** to stdout. Errors and diagnostic
notes go to stderr. Exit code 0 on success, non-zero on parse
failure.

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
