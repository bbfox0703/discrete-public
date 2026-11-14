# Technical Notes

> Catch-all for engineering notes that don't fit cleanly into a
> phase-specific spec or the [lessons-learned.md](lessons-learned.md)
> war stories. New entries land here as drip-feed; they get promoted
> to a dedicated doc once a topic accumulates enough material.

---

## Build numbering

`build_number.txt` at the repo root holds an integer that
auto-increments on every `build.ps1` invocation. The number flows
into:

- `BuildInfo.h` (DLL) — `DISCRETE_BUILD_NUMBER` macro,
  exposed via `Discrete_GetBuildNumber()`.
- `VersionInfo.cs` (UI) — `BuildNumber` property, surfaced on the
  Connection bar / Handshake panel.
- DLL `version.rc` Win32 file properties — visible in the file's
  Properties dialog under Details.

Bumped in `build.ps1` before any compile happens, so a clean
`build.cmd publish` increments by 1 even if no source changed.
Commit `build_number.txt` along with the work that triggered the
bump — keeps `git blame` honest about which commits actually shipped.

---

## Adding a new sortable DataGrid (Avalonia 12 + Native AOT)

There's a documented three-layer trap that disables Avalonia 12
DataGrid sort under AOT trimming — see
[lessons-learned.md §"Avalonia 12 DataGrid sort is a three-layer
trap"](lessons-learned.md#avalonia-12-datagrid-sort-is-a-three-layer-trap-under-aot--fix-needs-all-three)
for the full mechanism. Short version: column Bindings compile to
`CompiledBinding` (no Path string), DataGrid auto-disables
`CanUserSort`, and default reflection-based sort is trimmed away.

**Three-step recipe for any new DataGrid that should sort:**

1. **In XAML — every sortable column needs both attributes:**

   ```xml
   <DataGridTextColumn Binding="{Binding Foo}"
                       SortMemberPath="Foo"
                       CanUserSort="True" />
   ```

   `SortMemberPath` gives the runtime a real string path to key the
   sort dictionary on; `CanUserSort="True"` is belt-and-braces
   against the CompiledBinding-empty-Path auto-disable.

   Skip these on columns that should NOT sort (e.g. action-button
   columns, the per-row checkbox in ProxyDeploy) — leaving the
   attributes off keeps `CanUserSort` at the default disabled
   state, which is correct for those columns.

2. **Name the DataGrid** so the code-behind can find it:

   ```xml
   <DataGrid x:Name="MyGrid" ... CanUserSortColumns="True">
   ```

3. **In code-behind — wire the comparers at `Loaded`:**

   ```csharp
   using System.Collections;
   using System.Collections.Generic;
   using Avalonia.Controls;
   using UnityDumpUI.Core.Services;

   public MyWindow()
   {
       AvaloniaXamlLoader.Load(this);
       Loaded += (_, _) =>
           this.FindControl<DataGrid>("MyGrid")?.WireSortComparers(SortComparers);
   }

   private static readonly IReadOnlyDictionary<string, IComparer> SortComparers =
       new Dictionary<string, IComparer>
       {
           ["Foo"]  = DataGridSortComparers.Ordinal<MyRow>(r => r.Foo),
           ["Bar"]  = DataGridSortComparers.Hex<MyRow>(r => r.Bar),       // hex addresses
           ["Baz"]  = DataGridSortComparers.Number<MyRow>(r => r.Baz),    // ints
           ["Quux"] = DataGridSortComparers.Bool<MyRow>(r => r.Quux),     // booleans
       };
   ```

   Dictionary key = the XAML's `SortMemberPath`. Factories are in
   [`UnityDumpUI.Core/Services/DataGridSortComparers.cs`](../ui/UnityDumpUI.Core/Services/DataGridSortComparers.cs);
   extension method `WireSortComparers` is in
   [`UnityDumpUI/Views/DataGridSortExtensions.cs`](../ui/UnityDumpUI/Views/DataGridSortExtensions.cs).

**Diagnostic recipe when sort silently doesn't work:** hook
`DataGrid.Sorting` to log, walk `grid.Columns` and dump each
column's `SortMemberPath`, `CanUserSort`, `Binding` runtime type.
The trap-layer-to-symptom mapping in lessons-learned tells you
which gate is closed. Build is always green in the broken cases —
only interactive header click reveals it.

**Working reference:** every DataGrid in the project already
follows this pattern as of 2026-05-19 (build 325). See
[`FieldAcrossInstancesWindow`](../ui/UnityDumpUI/Views/FieldAcrossInstancesWindow.axaml)
+ [its code-behind](../ui/UnityDumpUI/Views/FieldAcrossInstancesWindow.axaml.cs)
for a 3-column minimal example; [`MainWindow`](../ui/UnityDumpUI/Views/MainWindow.axaml.cs)
shows the multi-grid case (3 grids in one window).

---

## Logger channels

DLL writes to `%LOCALAPPDATA%\Discrete_Dumper\Logs\<host-pid>\<channel>\`
across five channels:

| Channel | Used by | Volume (typical) |
|---|---|---|
| `init` | Resolver bind status, module list at DllMain | A few KB once at startup |
| `scan` | AOB scans, address-finder backward walks | Moderate — bursts on Phase 17 / 18 calls |
| `metadata` | `MetadataParser` version dispatch, table parse stats | A few KB at first metadata access |
| `pipe` | Per-RPC dispatch, errors, slow handlers | High — every RPC logs at least one line |
| `walk` | `walkInstance` / `walkArray` field-level decisions | High — bursts on Inspector usage |

UI writes to a separate per-PID directory under the same root, but
to a single `view` channel: VM-level events (find instances, walk
instance, copy XML, etc.).

8 MB rotation with 4-file ring per channel. Old logs are clobbered;
preserve a session by copying out before next launch.

---

## Pipe protocol versioning

Current protocol = `1`. The handshake response carries:

```json
{
  "protocol": 1,
  "build":    234,
  ...
}
```

The UI's `BackendAdapter` doesn't currently gate features on
`protocol` because we've never had a breaking wire change — every
new command (Phase 17, 18, 19) just adds, never modifies existing
shapes. When we do bump it, the UI should refuse to connect to a
mismatched DLL with a clear error message.

---

## CMake gotchas

### `-DBACKEND=` cache poisoning

CMake caches the `BACKEND` variable. Switching `Mono` → `IL2CPP` →
`Both` in the same build dir produces inconsistent linker errors
that look like "missing source files".

`build.ps1` wipes the per-backend build dir on every configure pass.
The cost is one extra cmake invocation per backend build (~5 s);
the win is never debugging this again.

### x64-only enforcement

```cmake
if(NOT (CMAKE_SIZEOF_VOID_P EQUAL 8))
    message(FATAL_ERROR "32-bit builds are not yet supported (Phase 12d)")
endif()
```

Removing this is part of the Phase 12d 32-bit-Mono unblock — would
cascade through proxy DLL compile, x86 backend build, x86 install
flow.

---

## CJK Windows console + em-dashes

A CJK Windows console (Traditional / Simplified Chinese, Japanese,
Korean) munges em-dashes (`—`) and other Unicode punctuation printed
by `build.cmd` status banners. Routing through `Start-Transcript`
fixes the encoding. `build.cmd` already does this so users see clean
output regardless of console code page.

If you're adding new banner text in `build.ps1`, prefer ASCII —
em-dashes are nice but not worth the failure mode of "build script
output looks broken on developer's PC".

---

## Antivirus

Freshly-built `DiscreteUI.exe` triggers Windows Defender / some
third-party AV due to the combination of:
- Many `LibraryImport` native calls (looks like P/Invoke shellcode
  loader to a heuristic).
- `CreateRemoteThread` injection P/Invokes.
- Self-contained AOT executable (no Microsoft signature).
- Output path under `dist\`.

Dev workflow: exclude the `dist\` folder from real-time AV scanning.
Mention this if a fresh dev hits a quarantine error after
`build.cmd publish`.

For end-user distribution: consider Authenticode signing (out of
scope for this repo today).

---

## Missing test coverage

Documented in [CONTRIBUTING.md §"Testing"](../CONTRIBUTING.md#testing).
Big known gaps:

- **Inspector classifier kind-routing** — the logic that decides
  whether a field is scalar / pointer / array / value-struct /
  string. Pure function; cheap test win when someone gets to it.
- **End-to-end `MetadataParser` round-trips per version** — would
  need a deterministic `global-metadata.dat` fixture per supported
  version (v24, v27, v29, v31). Big test asset; medium-effort.
- **CE-XML live-mode rendering** against a deterministic walked-
  instance tree. Same scaffolding need as above.
- **AobMakerBridge happy + sad path** with a fake pipe server. The
  bridge is fail-soft so most paths are silent — a fake server
  catches regressions where we'd start throwing on a benign
  no-AOBMaker scenario.

PRs adding any of the above are welcome.

---

## Out-of-scope features (drip-feed)

Tracked in [plan.md](plan.md) §"Open Backlog / TODO", but worth
calling out here for visibility:

- **Phase 5 segment 2d** — full `Il2CppMetadataRegistration` AOB
  scan in `GameAssembly.dll` so non-enum const literals can be
  type-aware-decoded. ~½ day of work; non-blocking.
- **Phase 12d** — 32-bit Mono support. Multi-day project; no user
  has asked yet (no 32-bit Unity game in the test matrix).
- **Phase 17e** — Strategy B address finder (cached heap walk +
  interval tree). Skip until requested — Strategy A (backward scan +
  class-pointer cache) is fast enough for interactive use.
- **CSX live mode** — design dead-end as scoped (CSX is offset-
  relative; "live" only makes sense as snapshot-derived layout).
  CE XML already provides true absolute-address live mode.
- ~~**Highlight matching substring**~~ ✅ shipped 2026-05-08 in
  Fields / Methods / Inspector filter results via
  `HighlightedTextBlock` + `HighlightSegmenter`.
- ~~**Phase 19d-3 smart-typed editors**~~ ✅ shipped 2026-05-19 —
  `MethodCallWindow` picks NumericUpDown / ComboBox / TextBox per
  row via `MethodCallEditorKind`; sized-int range clamps at the
  editor; enum params probe `getEnumValues` on window-open.
  `System.Boolean` rows get an `AutoCompleteBox` suggesting
  true/false but accepting any text the existing `bool.TryParse`
  consumes.
- **CE `{$CCODE}` / `{$LUACODE}` blocks** — evaluated and rejected, on
  architectural grounds rather than performance. What our AA generators
  emit is `{$lua}`, which runs in *CE's* process at assemble time; it is
  **not** `{$LUACODE}`, which runs in the *game's* process on every hook
  hit. Both `{$LUACODE}` and `{$CCODE}` need an injection hook site to
  attach to, and discrete emits none — its generated AA is
  symbol-registration plus a data `alloc()` only, never a code cave
  (`newmem` / `originalcode` / `fullaccess` all appear zero times across
  `Services/Export/` and `scripts/`). A CCODE block would be dead text.
  Full decision record:
  `D:\Github\UE5CEDumper\docs\ce-ccode-eval.md`
  ([public mirror](https://github.com/bbfox0703/UE5CEDumper/blob/main/docs/ce-ccode-eval.md)).

---

## Vendor sync (`sync_vendor.ps1`)

`dll/vendor/` and `vendor/` are gitignored. The script handles two kinds
of tree differently:

- **Build deps** (`dll/vendor/`: nlohmann/json, MinHook; Zydis + Zycore
  were here until the Rust core took over x64 decoding — `tools/zydis-oracle`
  now fetches its own pinned copy)
  are compiled into the DLL, so they are **pinned** to the commits in
  `tools/vendor_pins.json`. A normal run restores those commits and only
  *reports* whether upstream moved (with a compare link). `-UpdatePins`
  moves them to upstream tip and rewrites the pins file; rebuild and run
  the tests before committing it. The release workflow uses the same file.
- **Reference clones** (`vendor/`: libil2cpp, Il2CppDumper, mono, ...) are
  read-only oracles and follow upstream tip (see
  [il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md)).
  `-SkipReference` leaves them alone.

Re-run after a fresh checkout. A failed fetch is reported in red and makes
the script exit 1, never a silent "up to date".

---

## Source map

This file doesn't have a "source" — it's pure prose. New entries go
at the bottom; promote to a dedicated doc once a topic gets > ~1
page of material.
