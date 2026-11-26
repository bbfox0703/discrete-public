# GEMINI.md

Guidance for Gemini when working in this repository.

## 1. Read and Follow `CLAUDE.md` First

- **Primary instructions**: Always read and follow [CLAUDE.md](CLAUDE.md) first (and the docs it links, including [docs/plan.md](docs/plan.md) and [docs/Archiutecture.md](docs/Archiutecture.md)). Because development alternates between Claude and Gemini models, `CLAUDE.md` remains the shared baseline so both models obey the exact same rules and architecture invariants.

## 2. Additional Workflow Rules

- **Activity log (`docs/dev-log.md`)**: Record session activities in [docs/dev-log.md](docs/dev-log.md). This file is an **append-only** chronological log — only add new entries, never modify or rewrite existing older entries (at most archive older entries into `docs/archive/` if the file grows too large; see the sibling `UE5CEDumper` repo's `docs/dev-log.md` for reference style).
- **Clean verification build**: Test builds MUST go through `.\build.cmd publish` and complete with **zero errors and zero warnings**.
- **Prefer Python for test tools/scripts**: Avoid PowerShell, JavaScript, or VBScript for ad-hoc test tools or helper scripts because antivirus software frequently blocks built-in OS interpreters. Use **Python** instead, and handle encoding explicitly on Windows (watch out for heredoc / console code-page encoding issues — prefer UTF-8 `.py` files or explicit `encoding="utf-8"`).
- **Vendor repos**: External repositories under `vendor/` are managed and updated via `.\sync_vendor.ps1`; do not hand-edit cloned vendor sources.
- **Sibling repositories are read-only**: `discrete-public` is the main repository to edit. Sibling repos `AOBMaker` (Cheat Engine interface, partially invoked via its DLL) and `UE5CEDumper` (reference for analogous designs such as CE Field Export and `dev-log.md` conventions) are strictly **read-only**.
