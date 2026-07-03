# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> For detailed specs, implementation history, and debugging notes, see the **[docs/](docs/)** directory.
>
> **進度與待辦 (start here)**: [docs/plan.md](docs/plan.md) — 單一窗口:current state / open backlog / phase ledger / recent batches。完成 phase 的長篇寫作在 [docs/archive/](docs/archive/);舊批次摘要在 [docs/archive/status-history.md](docs/archive/status-history.md)。

## Code Changes 
- When asked to rename, refactor, or restructure modules, ALWAYS modify the actual code files (rename files, update imports, change class names) — not just documentation or naming tables.

-----

## Debugging & Fixes
- After implementing a fix, verify it against the actual runtime behavior or data format before declaring it done. For memory layout, address mapping, and binary protocol fixes, double-check byte offsets, pointer sizes, and data types (e.g., 32-bit vs 64-bit int, double vs float).
- For container/nested data structures (arrays, custom structures, types), always trace the full indirection chain before implementing a fix. 

-----

## Cheat Engine
- When working with Cheat Engine Lua APIs, always verify that a function exists in the CE Lua documentation before using it. Do not assume methods like GotoAddressEx exist.

-----

## Communication
- When the user reports a bug or says a fix isn't working, take it at face value and investigate — do not dismiss it as belonging to another project or assume it's already resolved.

## Rules (MUST follow)
- **Keep this file small & clean**: Lesson learnt, notes, others must stored in seperate files under `docs` folder. Keep this file small and clean with key information only
- **Language**: Code comments and UI strings in English
- **Single Instance**: UI app uses Mutex to ensure only one instance runs
- **Async everywhere**: All I/O, pipe operations, and alert actions must be async
- **AOT compatible**: All C# code must be Native AOT / trimming compatible. No reflection-based APIs — use source generators instead (e.g. `[JsonSerializable]` context for `System.Text.Json`, `[ObservableProperty]` for MVVM)
- **DataGrid columns never star-sized**: Read-only result/data grids MUST size columns with `Width="Auto"` (or fixed) + `MinWidth`, NEVER `Width="*"`/`"2*"`. Star columns force the total to fit the window, so widening one column shrinks the others — the user must NOT have to resize neighbours first. Auto + MinWidth lets total width exceed the window and the grid scrolls horizontally (Avalonia default). Star-fill is allowed ONLY in small editable input forms (e.g. MethodCallWindow). See [docs/ui-spec.md](docs/ui-spec.md).
- **Lua output is DEBUG-gated + closes the Lua Engine on success**: Every CE Lua surface (hand-written `scripts/*.lua` AND the `{$lua}` blocks emitted by the C# `*AaScriptGenerator`s) MUST follow [docs/lua-logging.md](docs/lua-logging.md). Informational `print()` only fires when the flag (`DISCRETE_DEBUG = 1`, or `U.DEBUG`) is on; errors ALWAYS print. On a clean, non-debug finish, close CE's Lua Engine so it stops covering CE's main window — `synchronize(function() local e=getLuaEngine(); if e then e.Close() end end)` — but leave it open on any error or debug run. `utils.lua` consumers get this via `U.log/dbg/warn/err` + `U.guard(body)`. Keep `scripts/*.lua` and `dist/*.lua` in sync.

-----

## Project Goal

Develop a **C++ DLL + Cheat Engine Lua Bridge** Unity Dumper.

Backend support targets:
- Unity **Mono** 32-bit and 64-bit (mono-2.0-bdwgc.dll / mono.dll)
- Unity **IL2CPP** 64-bit (GameAssembly.dll, metadata v24/v27/v29/v31)

Headline capabilities (all shipped — see [docs/plan.md](docs/plan.md) for the canonical phase ledger):
- Build the full **Assembly → Class/Struct → Field/Method** hierarchy from the
  live runtime (and offline, via `metadata-cli`, for IL2CPP).
- **Instance walker / exporter** — find live instances of a class, dump fields
  with live values, drill down through pointer chains, export as CE XML / CSX
  / SDK header / live cheat tables.
- **Find method instances** — locate every overload of a method by signature,
  return native addresses (Phase 18).
- **Jump to instance / method disassembly** — hand an address off to CE's
  Memory View via the AOBMaker bridge (`NavigateHexView` / `NavigateDisassembler` /
  `CreateAAScript` over `\\.\pipe\AOBMakerCEBridge`).
- **CE AA inject scaffolding** — given a class + overloaded method, generate
  CE Auto-Assembler boilerplate that targets the correct overload by
  signature, not just by name (Phase 18c). One-click "Create AA in CE"
  pushes the script straight into CE's address list.
- **Lua-callable Unity methods** — invoke managed methods (Mono JIT or
  IL2CPP AOT entry points) from CE Lua with marshaled args / return values
  (Phase 19; spec at [docs/lua-invoke.md](docs/lua-invoke.md)). Three
  surfaces: `callMethod` pipe RPC (DLL), `MethodCallWindow` (UI), and
  `scripts/unity_invoke.lua` (CE Lua bridge).
- **Address finder (reverse lookup)** — given any address from CE's scanner /
  pointer-scan, identify which Unity managed instance, class, and field it
  belongs to. The inverse of `findInstances`. See
  [docs/address-finder.md](docs/address-finder.md).

-----