# Contributing to discrete

> Thanks for taking the time to read this. The bar is "fits the
> existing patterns" rather than "perfect" — when in doubt, mirror
> what nearby code does and ask via PR comment.

---

## Quick start

```powershell
# Clone + sync vendor (nlohmann/json, libil2cpp metadata sources)
git clone https://github.com/<you>/discrete.git
cd discrete
.\sync_vendor.ps1

# Point git at the in-tree hooks dir (one-shot per clone — see
# "Git hooks" below for what it enforces).
git config core.hooksPath .githooks

# Full Release publish (DLL + UI AOT + tests + metadata-cli)
.\build.cmd publish clean
```

If `.\build.cmd publish clean` succeeds with no `IL*` warnings, your
toolchain is set up correctly.

### Git hooks

The repo ships one pre-commit hook at [`.githooks/pre-commit`](.githooks/pre-commit)
that rejects any staged `*.lua` whose working-tree state contains
CRLF. The check exists because the UnityDumpUI.Core build embeds the
working-tree lua bytes as `<EmbeddedResource>`s, and the Phase 22a
AOBMaker CE-side `InjectTableFile` verification trips on CRLF (one
byte per CRLF pair lost in CE's `StringStream` normalisation). The
hook fires only on commits that stage a `*.lua`; everything else is
unaffected.

To skip the hook for an emergency one-off, `git commit --no-verify`
— but anything pushed needs to land on `main` clean.

---

## Repo orientation

```
discrete/
├── build.cmd / build.ps1       <- UE5-style wrapper
├── build_number.txt
├── README.md / README.zh-TW.md / CONTRIBUTING.md (this file) / CLAUDE.md
├── dist/                       <- gitignored; built artifacts
├── docs/                       <- canonical specs (plan / STATUS / pipe-protocol / ...)
├── dll/                        <- C++ DLL backends
│   ├── src/main.cpp
│   ├── src/shared/             <- Logger / Memory / Pipe / ExportAPI / AobPattern / ...
│   ├── src/mono/               <- Mono backend (Resolver / Domain / Walker / Invoker / ...)
│   ├── src/il2cpp/             <- IL2CPP backend (mirrors Mono)
│   ├── tests/                  <- C++-side unit tests
│   └── vendor/                 <- nlohmann/json + libil2cpp source dumps
├── ui/
│   ├── UnityDumpUI/            <- Avalonia 12 AOT app (AssemblyName = DiscreteUI)
│   ├── UnityDumpUI.Core/       <- protocol + services (AOT-safe, no reflection)
│   └── UnityDumpUI.Tests/      <- xUnit v3
├── tools/                      <- PowerShell smoke tests + metadata-cli + Install-CEScripts
└── scripts/                    <- CE Lua bridge — deployed to <CE>/discrete/ via build.cmd deploy
```

For deeper structure see [README.md §Architecture](README.md#architecture)
and [docs/dll-spec.md](docs/dll-spec.md) / [docs/ui-spec.md](docs/ui-spec.md).

---

## Code conventions

These come from [CLAUDE.md](CLAUDE.md) (project rules) and
[docs/Archiutecture.md](docs/Archiutecture.md) (invariants). The
short version:

### Universal

- **English only**: code comments and UI strings. Doc files may be
  bilingual (e.g. `README.md` + `README.zh-TW.md`); pick one language
  per file.
- **No half-finished implementations**. If a feature is partial,
  mark it explicitly with a wire-shape error code (DLL) or a status
  message (UI), not a silent no-op.
- **Don't add features beyond what the task requires**. A bug fix
  doesn't need surrounding cleanup; a one-shot doesn't need a helper
  abstraction. Three similar lines is better than a premature
  abstraction.
- **Comments explain WHY, not WHAT**. Default to no comments — the
  identifiers should carry the WHAT. Reserve comments for non-obvious
  invariants, hidden constraints, workarounds for specific bugs, or
  surprising behaviour.

### C# / UI

- **AOT mandatory**. Every UI build must publish under Native AOT
  with **0/0 trim/AOT analyzer warnings**. No reflection-based JSON
  paths — always go through the source-gen `ProtocolJsonContext` (or
  the public `CallArgFactory` seam from outside `UnityDumpUI.Core`).
- **CommunityToolkit.Mvvm only**. `[ObservableProperty]` /
  `[RelayCommand]` source generators — no hand-rolled `INotifyPropertyChanged`
  boilerplate. Compiled bindings on every XAML binding (`x:DataType=...`).
- **Async everywhere**. Pipe/RPC and long-running work are async;
  never block the UI thread on a `.Result` / `.Wait()`. Small local
  settings/state files may use sync `File.*`.
- **No reflection APIs**. `LibraryImport` (not `DllImport` for new
  P/Invokes), `[JsonSerializable]` source-gen, etc.
- **Single instance**. The UI app uses a `Mutex` so only one
  `DiscreteUI.exe` runs at a time. Don't add new app-level state
  outside that mutex's scope.

### C++ / DLL

- **SEH-protected reads**. Every cross-process struct access goes
  through `SafeRead<T>` / `SafeReadBytes` from
  [shared/Memory.h](dll/src/shared/Memory.h). Bare `*(T*)addr`
  reads are forbidden — the game can crash, mutate, or unmap the
  page at any time.
- **Per-RPC SEH leaf for risky calls**. `runtime_invoke`, JIT
  compile entry points, AOB scans — wrap in `__try`/`__except` so a
  hard crash becomes a wire error code, not a host tear-down.
- **No global mutable state across pipe handlers** unless protected
  by a documented invariant (e.g. the per-PID class-pointer cache in
  the address-finder is single-threaded by virtue of the pipe server
  being single-threaded per connection).
- **Match the existing module shape**. Mono and IL2CPP backends
  mirror each other one-for-one — `*Resolver` / `*Domain` /
  `*ClassWalker` / `*MethodInvoker` / `*AddressFinder` /
  `*MethodFinder`. New behaviour goes in both backends.

### Lua bridge

- **Standalone scripts** in `scripts/`. Each `unity_*.lua` script
  must work via a single `dofile` — no cross-file `require` (CE Lua
  doesn't always have `package.path` set up cleanly).
- **Self-documenting headers**. Each script's top-of-file comment
  carries quick-start + full API + caveats so the dofile target is
  itself the documentation.
- **JSON parser per script**. Each script ships its own response-shape-
  specific JSON parser (~50 LOC). Don't try to factor into a shared
  module — the response shapes vary enough that a generic parser
  pays for itself in one debugging session.

---

## Testing

### What we have

- **xUnit v3** for the C# side (`ui/UnityDumpUI.Tests/`). Run via
  `.\build.cmd test`. Covers protocol DTOs, exporters, pipe-client
  lifecycle, a few inspector helpers.
- **PowerShell smoke tests** in `tools/Test-*.ps1`. One-shot pipe RPC
  against the injected DLL — useful for headless verification without
  launching the UI.
- **Manual click-through** for UI / live-runtime workflows. Recorded
  in [docs/plan.md](docs/plan.md) "Recent batches" (recent) or
  [docs/archive/status-history.md](docs/archive/status-history.md)
  (older), under each batch's "live-verified" / "live-smoke" notes.

### When you need to add a test

- **DLL feature touching the wire**: write a `tools/Test-<Feature>.ps1`
  against a known target. Pattern: open the named pipe, write JSON,
  read response, assert on shape.
- **UI feature touching exports / parsing / pure logic**: add an
  xUnit test under `ui/UnityDumpUI.Tests/`. Reach into
  `UnityDumpUI.Core` only (the `UnityDumpUI` project depends on
  Avalonia which can't initialize in xUnit).
- **UI feature touching ViewModels**: add a unit test that drives the
  VM with a mocked `IBackendAdapter` (the `BackendAdapter` interface
  is the seam — no need to spin up a real pipe).
- **CE Lua bridge change**: add the verification recipe as a new entry
  in plan.md "Recent batches" once you've clicked through it (older
  entries flow into archive/status-history.md over time).

### What we don't have (yet)

- **End-to-end MetadataParser fixtures** per IL2CPP version — would
  need a deterministic `global-metadata.dat` fixture per version.
  Drip-feed candidate.
- **CE-XML live-mode rendering against a deterministic fixture**.
  Same drip-feed.
- **AobMakerBridge happy + sad path** with a fake pipe server. Same.

PRs adding any of the above are welcome.

---

## Submitting changes

### Branches

- `main` — production. PRs land here only when the dev work is verified
  on the dev box.
- `dev` — active development. Most PRs land here first.

### Commit messages

Follow the existing style — `git log --oneline -20` for examples:

```
feat: Phase 19c — instance dispatch + string in/out marshaling
fix(ui): wire Skia + HarfBuzz explicitly after dropping Avalonia.Desktop
docs: refresh STATUS / plan / test-matrix / CLAUDE for Phase 19 closure
chore(ui): drop Avalonia.Desktop metapackage in favor of Avalonia.Win32
```

Format: `type(optional-scope): summary`. Types we use:
- `feat` — new feature / phase
- `fix` — bug fix
- `docs` — documentation only
- `chore` — build, dependencies, config
- `test` — test-only changes
- `refactor` — restructure without behaviour change

### Pull requests

Title: short imperative phrase, ideally referencing a phase if
applicable (e.g. "Phase 16 docs — lessons-learned + READMEs").

Body should cover:
1. **What** changed (one-liner).
2. **Why** — context that's not obvious from the diff.
3. **Test plan** — what you clicked / ran to verify. If you couldn't
   verify a UI piece (no game / no CE), say so explicitly.
4. **Anything to watch in review** — known caveats, deferred items.

Skip the body for trivial fixes (typo, dependency bump, etc.).

### Before pushing

```powershell
.\build.cmd test       # xUnit must pass
.\build.cmd publish    # AOT publish must hit 0/0 trim/AOT warnings
```

If you touched the DLL, also rebuild it (`.\build.cmd il2cpp` /
`.\build.cmd mono`). The game must be closed before rebuilding —
the DLL stays mapped after injection.

### Releases

Pushing a `vNNN` tag (NNN = build number, on `main`) runs
[.github/workflows/release.yml](.github/workflows/release.yml): a full
`build.ps1 -Mode Publish` on a GitHub Windows runner, then `dist/` zipped
with a `.sha256` and attached to a **draft** release with a changelog since
the previous tag. Review the draft, then publish it. Manual runs
(*Actions → Release → Run workflow*) build the zip as an artifact only.

The workflow fetches nlohmann/json, MinHook and Zydis at the commits
pinned in [tools/vendor_pins.json](tools/vendor_pins.json), by running
`sync_vendor.ps1 -SkipReference`, exactly as a local sync does. To bump
them: `.\sync_vendor.ps1 -UpdatePins`, then `.\build.cmd dll` and
`.\build.cmd test`, and commit the pins file only if both pass.

---

## Working with Claude Code

This project is built with [Claude Code](https://claude.com/claude-code)
as the primary development environment — see [CLAUDE.md](CLAUDE.md)
for the project-specific rules Claude follows.

If you're contributing without Claude Code, the rules in CLAUDE.md
still apply (they're rules about the code, not about the tool).

---

## Common pitfalls

Already documented in detail in [docs/lessons-learned.md](docs/lessons-learned.md);
the highlights:

1. **`signatureMatch` substring trap**: `"Int32"` is a substring of
   `"UInt32"`. Always use parens-bounded form: `"(System.Int32)"`.
2. **`runtime_invoke` non-uniform marshaling**: value types pass
   `&slot`, reference types pass the pointer **directly** in `params[i]`
   (not `&pointer`). The Mono docs say otherwise — they're wrong.
3. **Avalonia 12 Win-only build needs all three**: `Avalonia.Win32` +
   `Avalonia.Skia` + `Avalonia.HarfBuzz`. Missing `.UseHarfBuzz()`
   exits with `0xC0000409` before logging starts — you'll see
   "EXE doesn't run" with no clue.
4. **`PowerShell ConvertTo-Json` flattens single-element arrays**.
   Splice the array JSON in as raw text for nested arrays.
5. **CMake `-DBACKEND=` cache poisoning**: `build.ps1` wipes the
   per-backend build dir on every configure. Don't optimize this away.

---

## License

TBD — choose before first public release.
