# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> For detailed specs, implementation history, and debugging notes, see the **[docs/](docs/)** directory.
>
> **進度與待辦 (start here)**: [docs/plan.md](docs/plan.md) — 正典 phase ledger + 集中 **Open Backlog / TODO**。目前狀態速覽見 [docs/STATUS.md](docs/STATUS.md);完成 phase 的長篇寫作在 [docs/archive/](docs/archive/)。

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