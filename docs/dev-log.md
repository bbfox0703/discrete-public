# Dev Log

Append-only activity history, newest first. Each entry references a build number
from `build_number.txt` so commits can be cross-referenced. Existing entries are
never modified or rewritten — archive older entries into `docs/archive/` only if
this file grows too large.

**Reading tip:** grep `^## ` for the index, then read from the top (newest-first).

> **Looking for current state or open backlog?** See [plan.md](plan.md) for the
> current phase, open TODO list, and phase ledger.

-----

## 2026-11-26 (build 1528) — Fix E-H1 (x86 `List<T>._items` pointer read in live CE XML export), add `GEMINI.md` & badge, rename audit docs

- **Bug fix (`E-H1`):** `CeXmlExporter.Live.cs` (`EmitCollectionLiveAsync`) previously read a hardcoded 8 bytes for `List<T>._items` and parsed them with `ParseLeQword` (which required 16 hex chars). On 32-bit Mono targets (`PointerSize == 4`), that swept the adjacent `_size` field at `+0xC` into the upper 32 bits (`_items | (_size << 32)`), causing `WalkArrayAsync` to fail on every non-empty `List<T>`.
  - Changed the `ReadMemoryAsync` size to `ptr` (4 on x86, 8 on x64) and replaced `ParseLeQword` in `CeXmlExporter.cs` with `ParseLePointer(string leHex, int byteCount)`.
  - Added two x86 regression tests in `ui/UnityDumpUI.Tests/CeXmlExporterLiveTests.cs` (`RenderLive_X86_NonEmptyList_ReadsFourByteItemsPointer` and `RenderLive_X86_NonEmptyList_ParsesExactFourByteHexResponse`), and updated `FakeLive.ReadMemoryAsync` to slice returned hex to `size * 2` chars and record `LastReadMemorySize`.
- **Build warning cleanup:** Fixed a pre-existing x86 MSVC `warning C4244` (`uint64_t` → `uintptr_t`) in `dll/src/shared/ExportAPI.cpp:4622` with `static_cast<std::uintptr_t>`.
- **Docs & metadata:**
  - Added [GEMINI.md](../GEMINI.md) with `CLAUDE.md` as its first rule, plus workflow rules (append-only `docs/dev-log.md`, zero-error/zero-warning `.\build.cmd publish`, Python-first tooling with UTF-8 care, `vendor/` via `.\sync_vendor.ps1`, read-only sibling repos).
  - Added the Gemini badge to [README.md](../README.md) and [README.zh-TW.md](../README.zh-TW.md).
  - Renamed `docs/AUDIT_PLAN.md` → [audit-2026-11-23-plan.md](audit-2026-11-23-plan.md) and `docs/BUG_REPORT.md` → [audit-2026-11-23-findings.md](audit-2026-11-23-findings.md), updated internal cross-references, and marked `E-H1` fixed.
  - Created this append-only [dev-log.md](dev-log.md).
- **Tests & build:** `.\build.cmd publish` (build #1528) passed with 0 errors and 0 warnings across x64 + x86 DLLs, Native AOT UI (`DiscreteUI.exe`), and `metadata-cli.exe`; identity guard passed; C# xUnit v3 tests 1149 passed (0 failed, 0 skipped); Rust core tests 104 passed (x64) / 105 passed (i686).
