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

- `BuildInfo.h` (DLL) — `UNITYCEDUMPER_BUILD_NUMBER` macro,
  exposed via `UnityDumper_GetBuildNumber()`.
- `VersionInfo.cs` (UI) — `BuildNumber` property, surfaced on the
  Connection bar / Handshake panel.
- DLL `version.rc` Win32 file properties — visible in the file's
  Properties dialog under Details.

Bumped in `build.ps1` before any compile happens, so a clean
`build.cmd publish` increments by 1 even if no source changed.
Commit `build_number.txt` along with the work that triggered the
bump — keeps `git blame` honest about which commits actually shipped.

---

## Logger channels

DLL writes to `%LOCALAPPDATA%\UnityCEDumper\Logs\<host-pid>\<channel>\`
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

Tracked in [STATUS.md](STATUS.md) §"Open follow-ups", but worth
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
- **Highlight matching substring** in Fields / Methods / Inspector
  filter results — pure UI polish.
- **Phase 19d-3 smart-typed editors** — NumericUpDown for sized
  ints, ComboBox for enums via `getEnumValues`. Current TextBox +
  culture-invariant parsing works correctly but is plain.

---

## Vendor sync (`tools/sync_vendor.ps1`)

`dll/vendor/` is gitignored. The vendor sync script clones / refreshes:

- `nlohmann/json` (DLL-side JSON) — single header.
- `vendor/libil2cpp/<version>/il2cpp-metadata.h` — upstream Unity
  metadata header sources, kept side-by-side for cross-checking
  (see [il2cpp-metadata-crosscheck.md](il2cpp-metadata-crosscheck.md)).

Re-run after pulling a fresh checkout, or when adding a new metadata
version.

---

## Source map

This file doesn't have a "source" — it's pure prose. New entries go
at the bottom; promote to a dedicated doc once a topic gets > ~1
page of material.
