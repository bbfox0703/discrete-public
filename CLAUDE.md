# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> For detailed specs, implementation history, and debugging notes, see the **[docs/](docs/)** directory.
>
> **進度與待辦 (start here)**: [docs/plan.md](docs/plan.md) — 單一窗口:current state / open backlog / phase ledger / recent batches。完成 phase 的長篇寫作在 [docs/archive/](docs/archive/);舊批次摘要在 [docs/archive/status-history.md](docs/archive/status-history.md)。

## Code Changes
- A rename, refactor, or restructure request means changing the code itself (files, imports, class names), not only the docs that describe it.

-----

## Debugging & Fixes
- In memory layout, address mapping, and binary protocol code, the usual mistakes are byte offsets, pointer sizes, and data types (32- vs 64-bit int, double vs float). The Mono DLL ships for both x64 and x86, so take these values from the real runtime data or format, not from a neighbouring case.
- For container/nested data structures (arrays, custom structures, types), always trace the full indirection chain before implementing a fix.

-----

## Cheat Engine
- Before calling a Cheat Engine Lua API, look up its name, signature, and return convention in CE's own reference: `celua.txt` for the core API and `autorun\monoscript.lua` for the `mono_*` layer (default install: `C:\Program Files\Cheat Engine\`). Calls that look plausible do not always exist (`GotoAddressEx`, `is32bitProcess`), and return conventions vary (`injectDLL` returns `false` on failure, not `nil`).

-----

## Communication
- When the user reports a bug or says a fix isn't working, take it at face value and investigate — do not dismiss it as belonging to another project or assume it's already resolved.

## Rules (MUST follow)

> Canonical invariant list — platform abstraction, log layout, magic strings, Unity offsets, backend-agnostic UI, plus the SEH-leaf, pre-CRT proxy, and Mono thread-attach rules that have broken builds or crashed games before: [docs/Archiutecture.md](docs/Archiutecture.md) §Rules. Below are this file's own rule plus the ones most often broken.

- **Keep this file small & clean**: lessons learnt, notes, and other long-form material must live in separate files under `docs/`. Keep this file to key information only.
- **Language**: Code comments and UI strings in English
- **Releases stay drafts with rewritten notes**: a `v<build>` tag makes `release.yml` build a DRAFT whose body is a raw commit list; replace it with user-readable notes and leave publishing to the maintainer. Procedure and note rules: [CONTRIBUTING.md → Releases](CONTRIBUTING.md#releases). Pipeline test runs are exempt.
- **Build / test only via `.\build.cmd`** (targets `dll` / `mono` / `il2cpp` / `ui` / `proxy` / `cli` / `test` / `deploy` / `deploy-scripts`; modes `debug` / `release` / `publish`; `clean` to wipe first; `.\build.cmd /?` is the authoritative list) — bare `cmake` / `dotnet` calls fail without the VS Developer Shell. Output lands in `dist\` (x86 in `dist\x86\`).
- **Rust core (`dll/rust`)**: every `extern "C"` body runs inside `ffi::guard` (panics never unwind into C++), returned pointers borrow handle-owned memory, bytes cross as `(ptr,len)` without UTF-8 validation, game memory is read only through the `dc_read_fn` SEH shim (never dereferenced in Rust), zero crate deps unless recorded, never linked into the proxy DLLs. Keep `include/discrete_core.h` in sync by hand. Full rules + phase gates: [docs/rust-core.md](docs/rust-core.md).
- **Single Instance**: UI app uses Mutex to ensure only one instance runs
- **Async everywhere**: Pipe/RPC and any long-running work must be async and must never block the UI thread. Small local settings/state files may use sync `File.*`.
- **AOT compatible**: All C# code must be Native AOT / trimming compatible. No reflection-based APIs — use source generators instead (e.g. `[JsonSerializable]` context for `System.Text.Json`, `[ObservableProperty]` for MVVM). The UI ships as a self-contained trimmed binary. See [docs/aot-pitfalls.md](docs/aot-pitfalls.md) (§0 = day-one checklist) before touching `ui/` build config.
- **DataGrid columns never star-sized**: Read-only result/data grids MUST size columns with `Width="Auto"` (or fixed) + `MinWidth`, NEVER `Width="*"`/`"2*"`. Star columns force the total to fit the window, so widening one column shrinks the others — the user must NOT have to resize neighbours first. Auto + MinWidth lets total width exceed the window and the grid scrolls horizontally (Avalonia default). Star-fill is allowed ONLY for the exceptions listed in [docs/ui-spec.md](docs/ui-spec.md) — record any new one there rather than here.
- **Lua output is DEBUG-gated + closes the Lua Engine on success**: Every CE Lua surface (hand-written `scripts/*.lua` AND the `{$lua}` blocks emitted by the C# `*AaScriptGenerator`s) MUST follow [docs/lua-logging.md](docs/lua-logging.md). Informational `print()` only fires when the flag (`DISCRETE_DEBUG = 1`, or `U.DEBUG`) is on; errors ALWAYS print. On a clean, non-debug finish, close CE's Lua Engine so it stops covering CE's main window — `synchronize(function() local e=getLuaEngine(); if e then e.Close() end end)` — but leave it open on any error or debug run. `utils.lua` consumers get this via `U.log/dbg/warn/err` + `U.guard(body)`. Keep `scripts/*.lua` and `dist/*.lua` in sync.

-----

## Project Goal

A **C++ DLL + Cheat Engine Lua Bridge** Unity dumper: build the live
Assembly → Class/Struct → Field/Method hierarchy, walk instances, and hand the
results (addresses, AA scripts, cheat tables) to CE.

- **Backends**: Unity **Mono** x86/x64 (`mono-2.0-bdwgc.dll` / `mono.dll`) and
  **IL2CPP** x64 (`GameAssembly.dll`; `global-metadata.dat` v16–v39 accepted,
  v24/27/29/31/35–39 verified — `dll/src/il2cpp/MetadataParser.cpp`).
- **Shape**: injected C++ DLL ⇄ Named-Pipe JSON-RPC ⇄ Avalonia UI, plus CE Lua
  bridges in `scripts/`. The UI is backend-agnostic — every Mono / IL2CPP
  difference lives behind the pipe protocol, never in the UI.

The shipped feature set is large. **Check what exists before building anything
new**, or you will rebuild it: [docs/plan.md](docs/plan.md) (phase ledger) ·
`dll/src/shared/MagicStrings.h` (every wire command) ·
[docs/pipe-protocol.md](docs/pipe-protocol.md) (wire contract) ·
[README.md](README.md) (feature list + documentation map).
