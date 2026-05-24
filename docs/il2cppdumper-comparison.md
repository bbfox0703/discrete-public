# Il2CppDumper feature parity — delta + roadmap

> **TL;DR**: We're a CE-integration / live-injection tool;
> [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) is a static
> offline dumper. Different niches, overlapping core. We already have
> most of the metadata-parser plumbing; the deltas worth closing
> (small, high-leverage) are tracked as phases B–D below.
>
> **Do NOT clone the whole tool.** Il2CppDumper is mature and lives in
> `vendor/Il2CppDumper/` — anyone who wants `dump.cs` or `DummyDll/`
> should run upstream. We only build what fits our CE / live-injection
> niche.

Phase started: 2026-05-24 (build #480 baseline). Owner: whoever picks
this up across PCs.

---

## Feature matrix (state at build #480, 2026-05-24)

| Il2CppDumper feature | Status | Notes |
|---|---|---|
| Parse `global-metadata.dat` | ✅ Done | v24.0 / v24.1 / v24.2-v24.5 / v27 / v29 / v31 / v32-v34 / v35-v37 / v38-v39 — full coverage via [MetadataParser](../dll/src/il2cpp/MetadataParser.cpp). v24 sub-version auto-detected by `Parser::DetectV24Variant()`. |
| Parse `GameAssembly.dll` (PE) offline | ❌ **Not done** | We read live runtime memory instead. Gap target for **Phase D**. |
| Parse ELF / Mach-O / NSO / WebAssembly | ❌ Not planned | Android/iOS/Switch dump support — out of scope, no demand. |
| TypeDefinition + Field tables | ✅ Done | `metadata-cli types` / `type` |
| MethodDefinition + Parameter tables | ✅ **Done build #482** | `metadata-cli methods` / `method` — covers v24.0 / v24.1 / v24.2-v24.5 / v27-v30 / v31-v37 / v38 / v39 strides with the same auto-dispatch as TypeDefinition. Overloads supported (the `method` subcommand returns all matches as a JSON array). Parameter `type_index` stays opaque (resolves through the runtime types[] table, owned by Phase D). |
| ParameterDefaultValue | ❌ Not done | Deferred — same ECMA-335 blob walker as field defaults. |
| GenericContainer / GenericParameter | ❌ Not done | Not in roadmap — Il2CppDumper's `dump.cs` cares; we don't. |
| CustomAttribute decode | ❌ Not done | Not in roadmap — same reason. |
| `dump.cs` (whole-game C# pseudo-source) | ❌ Not planned | Use upstream Il2CppDumper. |
| `DummyDll/` (.NET stubs for ILSpy/dnSpy) | ❌ Not planned | Use upstream Il2CppDumper. |
| `il2cpp.h` (whole-game C struct header) | ✅ **Done build #486** | Tools menu → "Export il2cpp.h (whole game)…" walks every user assembly via existing `getClasses` + `getClassInfo` RPCs (no DLL changes); concatenates one big `.h` with FQN-based struct names, offset comments, and trailing **method AOT addresses inline** (Il2CppDumper only emits RVAs in `dump.cs`). [Il2cppHeaderBatchExporter.cs](../ui/UnityDumpUI.Core/Services/Export/Il2cppHeaderBatchExporter.cs) — see [export-formats.md](export-formats.md). |
| `script.json` / Ghidra / IDA symbol scripts | ✅ Done (build #477) | `.dd64` (x64dbg) + `ImportSymbolsScript.py` (Ghidra) + `.idc` (IDA) — live-DLL driven. Offline equivalent depends on Phase D. |
| Detect "dumped" metadata (process-dump replay) | ❌ Not done | Not in roadmap. |

### What we have that Il2CppDumper doesn't

These are the reasons our project exists; **don't lose sight of them
while chasing parity**.

| Feature | Where |
|---|---|
| Live process injection + IL2CPP runtime RPC pipe | [`dll/src/il2cpp/`](../dll/src/il2cpp/) + [`scripts/`](../scripts/) |
| Phase 18 AOB pattern + CE AA-script generation | [`docs/aob.md`](aob.md) |
| Phase 19 `callMethod` (Lua + PowerShell + UI) | [`docs/lua-invoke.md`](lua-invoke.md) |
| CE XML / CSX live-mode export (real instance addresses) | [`docs/export-formats.md`](export-formats.md) |
| Mono backend (bdwgc + legacy `mono.dll`) | [`dll/src/mono/`](../dll/src/mono/) |
| AOBMaker bridge + UI inspector | [`docs/ce-bridge-integration.md`](ce-bridge-integration.md) |
| `MetadataParser::ValidateTypeTable()` "fail loud" guard | build #479 — Il2CppDumper happily emits garbage on stride-mismatched metadata |

---

## Phase plan

Three phases, prioritized by ROI for our niche. Each phase is
self-contained — ship one and the project is strictly better even
if the next one slips.

### Phase B — Method + Parameter parsing in metadata-cli  ✏️ **in flight**

**Why first**: foundational. Without method enumeration, neither the
batch `il2cpp.h` (Phase C) nor the offline PE symbol resolution
(Phase D) has anything to walk. Also closes a noticeable CLI feature
gap on its own — CI consumers asking "how many methods does this
game have?" hit a wall today.

**Scope**:

1. `MetadataLayout.h` — add `MethodDefinition_v24_0` / `_v24_1` /
   `_v24` / `_v29` / `_v31` / `_v35` / `_v38+` structs with
   `static_assert`s on stride. Match upstream Il2CppDumper's
   `[Version(...)]` attribute field-drop sequence
   ([MetadataClass.cs:225-270](../vendor/Il2CppDumper/Il2CppDumper/Il2Cpp/MetadataClass.cs)).
   Same for `ParameterDefinition_v24_0` / `_v29` (parameter has its
   own `[Version(Max = 24)] customAttributeIndex`).
2. `MetadataParser.h/cpp` — new `MethodDefView` /
   `ParameterDefView` structs + APIs:
   - `MethodDefSizeof()` / `ParameterDefSizeof()`
   - `GetMethodDefinition(idx)` / `EnumerateTypeMethods(type)`
   - `EnumerateMethodParameters(method)`
   - All version-dispatched the same way TypeDefinition is today.
3. `metadata-cli` — two new subcommands:
   - `metadata-cli methods <path>` — JSON line per method
     (name, type-FQN, return-type-index, param-count, token).
   - `metadata-cli method <path> <ns> <type> <method>` — full
     decode with parameters (name + type-FQN per param).
4. Verification across v24.0 / v24.5 / v29 / v31 / v39.

**Out of scope**: parameter *default* value decoding (needs the same
ECMA-335 blob walker as field defaults; defer until a consumer
actually asks). Generic method-instantiation table is also deferred.

**Estimated effort**: ~half a day.

### Phase C — Whole-game `il2cpp.h` batch export  📋 **pending**

**Why second**: ~4 hours of glue, leverages Phase B's method
enumeration. Bridges the gap between our single-class
`.h` exporter (already shipped in
[`ui/UnityDumpUI.Core/Services/Export/SdkHeaderExporter.cs`](../ui/UnityDumpUI.Core/Services/Export/SdkHeaderExporter.cs))
and Il2CppDumper's `il2cpp.h`.

**Open design question** (decide when starting):

- **Path A** — Add `metadata-cli sdk-headers <path> <out-dir>`.
  Offline, no game running. Reuses Phase B method enumeration. Only
  emits field layouts (no method bodies, because no PE parse yet).
- **Path B** — Add a UI Tools menu entry "Export il2cpp.h…". Live
  DLL drives, so we can include resolved method native addresses
  inline as `// 0x7FF6...` comments — better than Il2CppDumper's
  RVA-only output. Depends on having the game running.

Path A ships sooner; Path B is more valuable. Likely build both —
share the formatting code, vary the input source.

### Phase D — Offline PE parser  🏗️ **deferred (2-3 days)**

**Why last**: biggest investment, biggest payoff. Lets the CLI
produce native method addresses without launching the game —
matches Il2CppDumper's headline `script.json` use case but feeds
our existing `.dd64` / Ghidra / IDA exporters.

**Scope** (in implementation order — each step is independent
enough to demo):

1. Minimal PE parser: walk `IMAGE_DOS_HEADER` → `IMAGE_NT_HEADERS` →
   sections. Identify `.text`, image base, entry point.
2. Find `Il2CppCodeRegistration` + `Il2CppMetadataRegistration`:
   - PE has an exported symbol `il2cpp_init` (sometimes); fall back
     to AOB scan inside `.text` for the registration pointer load.
   - Same `Il2CppMetadataRegistration` structural-shape scan we
     already do at runtime (Phase 5 segment 2d) — port to file
     bytes instead of process memory.
3. Resolve each `MethodDefinition` → native RVA by indexing into
   `Il2CppCodeRegistration::methodPointers[]`.
4. Wire to existing exporter formatters → `metadata-cli symbols
   <path-to-GameAssembly.dll> <path-to-global-metadata.dat>
   [--format=dd64|ghidra|ida]`.

**Why bother when we have live-DLL symbol export already**: lets
people analyze games they don't want to launch (anti-cheat,
malware, broken installs), enables CI dumping, makes Linux/macOS
dev usable (no need to run the game).

---

## Rejected (on purpose)

| Idea | Why no |
|---|---|
| Full `dump.cs` generator | 2-3 days of work for something Il2CppDumper does perfectly. Tell users to run upstream. |
| `DummyDll/` generator | 3-5 days of `System.Reflection.Emit` / dnlib work for something Il2CppDumper does perfectly. |
| ELF / Mach-O / NSO binary parsers | Android/iOS/Switch IL2CPP is rare in our test corpus; no user demand. |
| Custom-attribute blob decoder | Niche. ECMA-335 §II.23.3 is non-trivial. Wait for an actual need. |
| Generic instantiation table walker | Same — niche; only matters for `dump.cs` accuracy. |

---

## Progress log

| Build | Date | Phase | Status |
|---|---|---|---|
| #480 | 2026-05-24 | A (doc) | This file landed. |
| #482 | 2026-05-24 | **B (Method/Param)** | ✅ **Done.** `metadata-cli methods` + `method` subcommands ship; corpus-verified on R-Type Dimensions (v24.0, 23.9K methods), SaGa Frontier Remastered (v24.5, 56.2K), SaGa F2R (v29, 69.6K), Heroes (v31, 203K), Songs of Silence (v39, 248.8K). Overload semantics validated against `System.String.Concat` (6 overloads on Heroes / 7 on R-Type) and `System.Math.Max` (11 primitive overloads on Songs of Silence). |
| #486 | 2026-05-24 | **C (il2cpp.h batch)** | ✅ **Done (path B — UI live-driven).** Tools menu entry "Export il2cpp.h (whole game)…" ships. Walks every user assembly via existing `getClasses` + `getClassInfo` RPCs (no DLL changes). FQN-based struct names (`Namespace_Class`) avoid collisions; method AOT addresses inline per class as `// Methods (AOT entry points…)` trailing comments — beats Il2CppDumper's RVA-only `dump.cs`. Mono backend greys out (JIT addresses don't survive restart). 4 unit tests in `Il2cppHeaderBatchExporterTests` pin the rendering contract; manual live-verification steps in [export-formats.md](export-formats.md). |
| #487 | 2026-05-24 | C live-verify (SaGa F2R, v29) | ✅ **3,763 / 3,765 classes (99.95%)** — `MainGame/PLY_WORK` fields + method AOT addresses match the matrix-recorded layout. 2 `// SKIPPED` markers (both MessagePack edge cases — `MessagePackSerializer` generic-only static class and `LZ4Codec/HashTablePool` nested type) — design-as-intended fail-loud behaviour. ⚠️ Surfaced a slow-quit (game took >60s to terminate after the export) caused by 3,765 per-RPC `ThreadAttachGuard` cycles — the **IL2CPP analog** of the documented Mono Phase 27 follow-up issue. Recorded in [lessons-learned.md](lessons-learned.md) §"Phase C follow-up — IL2CPP equivalent". Proper fix is a **server-side bulk `dumpClassInfo` RPC** — landed at build #496. |
| #496 | 2026-05-24 | **C follow-up (bulk RPC)** | ✅ **Shipped.** New `dumpClassInfo` RPC registered on **both backends** (Mono mirror is forward-symmetry — no current Mono UI consumer, but prevents future foot-gun if the il2cpp.h flow is ever extended). `Il2cppHeaderBatchExporter.RenderFromBulk()` consumes the bulk payload via the same `EmitOneClass` projection the per-class path uses — output shape unchanged, tests still green (353/353). MainWindowViewModel switched from N×getClassInfo loop to one `DumpClassInfoAsync` call. Also did a **symmetry audit** of every UI batch site (table in lessons-learned.md): the il2cpp.h export was the only hotspot; CE XML / CSX live-mode drilldown is bounded by user-action depth and stays inline. |
| TBD | TBD | D (PE parser) | Deferred. |

Update this table when each phase ships.

---

## Related docs

- [docs/metadata-cli.md](metadata-cli.md) — CLI usage
- [docs/test-matrix.md](test-matrix.md) — verification corpus per metadata version
- [docs/export-formats.md](export-formats.md) — debugger-symbol export formats
- [docs/il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md) — layout cross-check audit
