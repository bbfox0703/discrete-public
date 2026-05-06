# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> For detailed specs, implementation history, and debugging notes, see the **[docs/](docs/)** directory.

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
Target: Support Mono 32/64 bits, 64 bites IL2CPP games, build complete class/object/struct/method hierarchy, integrate with CE. Instance walker/export and find method instances. Jump to instance/method disassembler code in CE. Helping tool to create CE AA inject code to map to right overload method. Also lua call Unity methods. Address finder to find from CE searched address is beloging to which Unity instance and class.

-----