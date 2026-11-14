# Rust core (`dll/rust`) — strangler migration of the DLL

The injected DLL is being moved to Rust **piece by piece**, not rewritten. The
C++ DLL stays the host; self-contained pieces move into the `discrete_core`
static library behind a C ABI, and the C++ classes they replace become thin
wrappers so no consumer changes. Each phase has a go/no-go gate.

Why not a full rewrite: most bugs this DLL has hit (BDWGC attach churn, x86
calling conventions, TEB offsets, metadata sub-version strides, the proxy
pre-CRT window — see [lessons-learned.md](lessons-learned.md)) are runtime
*semantics*, which Rust does not prevent; and reading another runtime's memory
through raw pointers is `unsafe` in Rust too. Rust pays off where we parse
untrusted input or hold concurrent state, and where an FFI boundary can turn a
bug into an error return instead of a game crash.

## Phases

| Phase | Scope | Gate | Status |
|---|---|---|---|
| **R0** | Toolchain: cargo inside `build.cmd`/CMake, x64 + x86, /MD + /MT, Debug + Release; FFI conventions; log sink; panic guard | All arch × CRT combinations build and link; `cargo test` passes on x64 and i686; Rust banner in the init log | ✅ offline + IL2CPP x64 and Mono x64 live; x86 live pending |
| **R1** | IL2CPP `global-metadata.dat` parser (`dll/rust/src/metadata`) behind `MetadataParserRs.cpp` | `tools/verify/metadata_rust_diff.py`: Rust and legacy C++ parsers byte-identical over the local fixture corpus | see [R1 gate](#r1-gate) |
| **R2** | Pure algorithms: `AobPattern` (`aob.rs`), `GroupMatch` (`group_match.rs`), the value-scan refine comparator (`value.rs`), `IL2CPPChainDetector` (`chain/`, Zydis → `iced-x86`); host-memory reads only through the SEH read shim | `tools/verify/r2_rust_diff.py`: legacy vs Rust byte-identical over generated cases + every local `GameAssembly.dll` | see [R2 gate](#r2-gate) |
| R3 (opt.) | `PipeServer` framing; handlers stay C++ | wire-level replay | not started |
| R4+ | Backend walkers / `ExportAPI` | only if R1–R3 show a real maintenance win | not planned |

## Layout

```
rust-toolchain.toml            pinned rustc + both targets (repo root)
dll/rust/
  Cargo.toml / Cargo.lock      crate `discrete_core`; deps only as recorded below
  build.rs                     version banner from the CMake build (C++ format)
  RustCore.cmake               cargo -> IMPORTED target `discrete_core`
  include/discrete_core.h      the C ABI (hand-maintained, no cbindgen)
  src/lib.rs                   FFI rules (read this first)
  src/ffi.rs                   log sink, panic guard, version banner
  src/metadata/                R1 parser (layout.rs, parser.rs, ffi.rs, tests)
  src/aob.rs                   R2 AOB pattern builder          (dc_aob_*)
  src/group_match.rs           R2 group-scan SDR matcher       (dc_gm_*)
  src/value.rs                 R2 value-scan refine comparator (dc_value_*)
  src/chain/                   R2 IL2CPP chain detector        (dc_chain_*)
    mod.rs                       the scan (legacy transcribed) + C ABI
    decode.rs                    iced-x86 -> "what Zydis would have said" adapter
    apx_map4.rs                  GENERATED: APX/EVEX map-4 table from vendored Zydis
    msvc_sort.rs                 MSVC std::sort transcription (dedup order parity)
dll/src/shared/RustCore.cpp    C++ log sink + banner + SafeReadShim (R2 read shim)
dll/src/il2cpp/MetadataParserRs.cpp   Parser class -> dc_md_* wrapper
dll/src/shared/{AobPattern,GroupMatch,ValueRefine}Rs.cpp,
dll/src/il2cpp/IL2CPPChainDetectorRs.cpp   R2 wrappers (same C++ API)
tools/verify/metadata_rust_diff.py    R1 differential harness
tools/r2-diff/ + tools/verify/r2_golden.py   R2 regression gate (golden hashes)
```

Linked into `Discrete-Mono.dll`, `Discrete-IL2CPP.dll` (x64 + x86) and
`metadata-cli.exe`. **Never into the proxy DLLs** — their exports can run
before the CRT is initialised (pre-CRT rule, [Archiutecture.md](Archiutecture.md) §Rules),
and nothing in them needs Rust.

## Build

`build.cmd` drives everything; `RustCore.cmake` maps the C++ configuration
onto cargo so the two halves can never disagree:

| C++ build | cargo |
|---|---|
| `DISCRETE_ARCH` x64 / x86 | `--target x86_64-pc-windows-msvc` / `i686-pc-windows-msvc` |
| `CMAKE_BUILD_TYPE` Debug / Release | `--profile dev` / `release` |
| `STATIC_CRT` ON (publish, `/MT`) / OFF (`/MD`) | `-Ctarget-feature=+crt-static` / none |

- Flags go through `CARGO_ENCODED_RUSTFLAGS`, which outranks `RUSTFLAGS`,
  `CARGO_BUILD_RUSTFLAGS` and every config-file `rustflags`, so a developer's
  own settings never leak into a shipping build. It is always non-empty (`/MD`
  passes `-crt-static` explicitly) and `RUSTFLAGS` is unset for the call.
  `--locked` keeps `Cargo.lock` fixed.
- The cargo target dir is `build/<cmake-dir>/rust-target/`, one per CMake
  build dir, so switching CRT/arch never thrashes a shared cache.
- Rust std always names the *release* CRT. C++ Debug links the debug CRT, so
  Debug adds `/NODEFAULTLIB:msvcrt.lib` (or `libcmt.lib`); the debug CRT
  exports everything Rust std needs.
- **Version = the C++ DLL / UI version.** `RustCore.cmake` passes the
  values `BuildInfo.h` is generated from (`project(VERSION)`,
  `build_number.txt`, git short hash, config, configure timestamp) to cargo
  as `DISCRETE_FILE_VERSION` / `DISCRETE_GIT_SHORT` / `DISCRETE_BUILD_CONFIG`
  / `DISCRETE_BUILD_TIMESTAMP`; `build.rs` formats them like
  `DISCRETE_BUILD_BANNER`, so `dc_core_version()` returns `discrete_core
  0.1.0.1469 9636b00 (Release, …)`. `RustCore::Init` logs it and writes a
  `WARN` line if it differs from the C++ banner (a stale
  `discrete_core.lib`). `build.rs` fails the build when `Cargo.toml`'s
  version is not the `project(VERSION)` major.minor.patch — bump both
  together. Plain `cargo build` / `cargo test` reports `0.1.0 (cargo)`.
- Native libs Rust std needs (from `--print native-static-libs`): `kernel32
  ntdll userenv ws2_32 dbghelp synchronization`. Re-check on toolchain bumps.
- `build.cmd test` also runs `cargo test` for **both** triples — the i686 test
  exe runs under WOW64, which exercises 32-bit codegen and panic unwinding
  without a 32-bit game. The real-file parser test (and every harness under
  `tools/verify/`) takes its games from `DISCRETE_METADATA_FIXTURES`
  (semicolon-separated `global-metadata.dat` paths) or, when that is unset,
  from the git-ignored `tools/verify/fixtures.local.txt` (one path per line;
  template `fixtures.example.txt`). Game files are never copied into the
  repo — they stay in the game's install folder.
- `build.ps1` requires rustup, runs `rustup toolchain install` (installs the
  pinned toolchain on a fresh clone / after a bump, tolerating rustup's stderr
  progress under Windows PowerShell 5.1), then checks both targets and prints
  the `rustup` command to fix a missing one.

Toolchain bump: edit `rust-toolchain.toml`, rebuild all four combinations
(`build.cmd dll`, `build.cmd dll publish`), rerun `build.cmd test`, the R1
harness and `r2_golden.py`.

## FFI rules (every `extern "C"` function)

1. Body inside `ffi::guard(name, fallback, || ...)`: a panic is logged and
   becomes the documented failure value. Panics must never unwind into C++
   (that aborts the host game). `panic = "unwind"` in every profile for this
   reason — `abort` would kill the game on any bounds-check bug.
2. Returned pointers point into memory owned by a handle and live until the
   handle is freed. Rust never hands heap ownership to C++.
3. Byte strings cross as `(ptr, len)`, **not** validated as UTF-8; C++ copies
   them byte-for-byte. (Metadata names are not guaranteed UTF-8.)
4. A NULL handle is always accepted and answers "not loaded".
5. Handles are `Send + Sync` (checked at compile time) so the pipe's worker
   threads can share them. Interior mutability is limited to synchronised,
   append-only caches whose entries are never freed before the handle: the
   metadata parser's nested-type map (`OnceLock`) and its decoded-string
   cache (`Mutex<HashMap<i32, Box<[u8]>>>` — boxed, so returned pointers stay
   valid as the map grows).
6. Rust logs through `ffi::log(LogCategory::X, ..)`; the C++ sink maps the
   category onto `MagicStrings::LogCategory*`, so category names stay in the
   C++ magic-strings file. Without a registered sink (metadata-cli) lines are
   dropped — the same contract as `Log::Write` before `Log::Init()`.
7. A C++ sink must not throw into Rust frames (`RustLogSink` is `noexcept`
   with a catch-all).
8. **Rust never dereferences game memory.** An access violation unwinding
   through Rust frames is UB, and `catch_unwind` cannot catch it. Reads of
   host-process memory go through a `dc_read_fn` callback (`ffi::Reader`),
   implemented by C++ `RustCore::SafeReadShim` on top of
   `Memory::SafeReadBytes` (SEH, copies, reports failure). Buffers the C++
   caller owns (its own stack, a vector it filled) may be passed as plain
   `(ptr, len)` slices.

## Dependencies

Each crate dependency is a recorded decision: why, licence (the repo is
GPL-3.0), exact pin in `Cargo.toml` (`=x.y.z`) and `Cargo.lock`. Cargo fetches
them from crates.io at build time (the CI runner has network; offline builds
need a warm `~/.cargo/registry`), the same model as `sync_vendor.ps1` for the
C++ deps.

| Crate | Pin | Licence | Why | Scope |
|---|---|---|---|---|
| `iced-x86` | `=1.22.0`, features `std` + `decoder` + `instr_info` only | MIT | R2 chain detector needs a full x86-64 decoder with per-operand read/write access; writing one is out of scope and Zydis is a C library the Rust side cannot call without a second FFI layer | `chain/` only (IL2CPP backend) |
| `lazy_static` | via `iced-x86/std` | MIT / Apache-2.0 | transitive | — |

Zydis stays vendored for now: `IL2CPPFieldXref.cpp`'s instance-field scan
still uses it. Retiring Zydis entirely would mean porting that scan too.

## R0 gate

Verified 2026-11-14 (build #1440–1443):

| Combination | Result |
|---|---|
| x64 Release `/MD` (Mono + IL2CPP) | builds, links; banner string present |
| x86 Release `/MD` (Mono) | builds, links; banner string present |
| x64 Publish `/MT` | builds; no `VCRUNTIME140` / `api-ms-win-crt` imports |
| x86 Publish `/MT` | builds; no dynamic CRT imports |
| x64 + x86 Debug (Mono), x64 Debug (IL2CPP, after `ff2e3fc`) | builds, links, no LNK4098 |
| `cargo test` x64, i686, i686 `+crt-static` | 3/3 pass, incl. panic → fallback |
| proxy DLLs | do **not** contain the Rust core (checked) |

Live: the `Rust core: discrete_core …` banner line appeared in an IL2CPP x64
game's `init` log and a Mono x64 game's (2026-11-14). x86 not yet run live.

Unrelated finding while testing: x64 **Debug** `Discrete-IL2CPP.dll` failed with
`C2712` in `IL2CPPAddressFinder.cpp` — broken since before the Rust work.
Fixed in `ff2e3fc` (SEH-leaf refactor); after the merge, x64 Debug IL2CPP with
the Rust core builds and links cleanly too.

## R1 gate

Verified 2026-11-14: `py -I tools/verify/metadata_rust_diff.py` over every
`global-metadata.dat` on the dev box — **7 fixtures, 1589 checks, 0
differences** (≈80 s with `-j 7`). Each check compares exit code, stdout and
stderr byte-for-byte; `dump-parser` alone is 33–121 MB of output per file.
The harness refuses to run unless `metadata-cli.exe` really contains the Rust
parser and `metadata-cli-legacy.exe` does not, and `dump-parser` / `info` /
`symbols` must also *succeed* (identical failures do not count as a pass) —
both added after the integration review; the 1589/1589 result above is from
the hardened harness.

| Fixture | Metadata | Types | String literals |
|---|---|---|---|
| FINAL FANTASY VI PR | v24 (v24.2–5 stride) | 11,438 | 11,537 |
| SaGa Frontier Remastered | v24 (v24.2–5 stride) | 7,514 | 15,182 |
| SaGa Frontier 2 Remastered | v29 | 10,001 | 11,392 |
| Heroes of Might and Magic: Olden Era | v31 | 25,551 | 15,895 |
| Utawarerumono: Past and Present Rediscovered | v31 | 22,160 | 19,327 |
| Iron Nest: Heavy Turret Simulator | v39 | 19,504 | 21,692 |
| Songs of Silence | v39 | 40,507 | 44,048 |

**Not in the corpus** (covered only by the synthetic-blob unit tests in
`dll/rust/src/metadata/tests.rs`): v24.0 / v24.1 strides (R-Type Dimensions
EX), v27 (Townscaper / Tianding), v35–v37. Re-run the harness when one of
those games is installed.

How the port was checked before the harness ran: one writer, then five
area reviewers (header, type definitions, fields/strings, methods/images,
C ABI) each compared Rust and C++ line by line; every finding went to two
independent refuters. Two minor findings survived, both in file reading, and
were fixed: a failed 256 MiB allocation used to abort (now a read failure,
via `try_reserve_exact`), and `std::fs` rewrote ≥248-char paths to `\\?\`
form (now plain `CreateFileW`, same arguments as C++).

Intentional differences from the C++ parser (none observable in the
harness):
- **UB hardening.** Where C++ `int32` arithmetic could overflow, Rust uses
  wrapping/checked arithmetic that gives the same result for every
  non-overflowing input.
- **Thread-safe lazy map.** The nested-type map is a `OnceLock`; the C++
  `mutable` cache was an unsynchronised write from `const` methods.
- **Allocation failure.** A failed allocation logs the read-failure line and
  reports "not loaded"; C++ threw `bad_alloc` (caught by the pipe handler).
- **Log lines with non-UTF-8 bytes** (only the `ValidateTypeTable FAILED`
  line quotes raw heap bytes) go through `from_utf8_lossy`; C++ wrote the raw
  bytes. Log text only.
- `TryDecodeStringDefault` returns a sanitised copy (bad UTF-8 → U+FFFD), as
  C++ does. When sanitising changed bytes, the copy is cached in the handle
  (keyed by data index) so the returned pointer stays handle-owned.

Speed: the Rust parser is markedly faster through the same API — the
legacy `dump-parser` on Songs of Silence ran for >15 min before it was
stopped (string copies in `GetTypeDefinition` × `FindImageContainingMethod`),
the Rust one finished the identical walk in ~8 min; `dump-parser` now samples
that one O(methods × types) call.

Live-DLL check: ✅ passed 2026-11-14 together with R2 — see
[Live A/B](#live-ab-r1--r2).

## R2 gate

Verified 2026-11-14 (build #1456–1468). Four modules moved; the C++ APIs
(`Aob::BuildPattern`, `GroupMatch::HasDistinctAssignment` /
`ResolveAssignment`, `ValueRefine::CompareCandidateForRefine`,
`DetectFieldAccessChains`) are unchanged, so no caller changed.

| Module | Rust | Notes |
|---|---|---|
| AOB pattern builder | `aob.rs` | Pure; input is a buffer C++ already read with `SafeReadBytes`. Legacy quirks kept on purpose (≤5 legacy prefixes, one REX, `A0..A3` always moffs64, disp8 never wildcarded) |
| Group-scan SDR matcher | `group_match.rs` | Same Kuhn search order; the recursion is now an explicit stack (legacy could overflow the stack on a very deep augmenting path) |
| Refine comparator | `value.rs` | Only the per-candidate comparator. The value-scan hot loop (`Memory.cpp` `ScanChunk`) stays C++: it touches game memory under SEH on every byte |
| IL2CPP chain detector | `chain/` | Zydis → `iced-x86` through `decode.rs`, which reduces each instruction to exactly the facts the legacy code read from Zydis. Every read of `GameAssembly.dll` goes through `SafeReadShim` (FFI rule 8). `msvc_sort.rs` transcribes MSVC `std::sort` because legacy dedup after an unstable sort depends on its exact element moves |

**Differential harness** (at the time: `build.cmd cli` built `r2-dump` + the
unshipped `r2-dump-legacy`; `tools/verify/r2_rust_diff.py`): **19 checks, 0
differences**, both on the **Release** and the **Debug** builds (the Debug
run proves no `overflow-checks` panic in the dev profile turns into a
fallback). Each check compares exit code, stdout and stderr byte for byte
and must succeed. Coverage: 50k synthetic AOB windows, 200k GM instances,
500k refine cases, and per local `GameAssembly.dll` (the same 7 games as the
R1 table) 10k real AOB windows plus 34 chain-detector runs (limits, empty /
bogus / reversed slots, `sfo = 0`, NULL stats). The harness refused to run if
`r2-dump.exe` lacked the Rust marker or the legacy binary had it, or if the
flags left nothing to compare. Since the legacy removal (below) the same
outputs are guarded by golden hashes.

Before the harness, each port was checked by its author against a private
oracle (legacy C++ compiled verbatim): AOB 600k random cases × x64/x86 ×
pure-Rust/wrapper, MD5-identical; chain decoder 160M random instructions plus
exhaustive legacy maps against the vendored Zydis, and 60k generated modules
end to end; GM 220k cases against a recursive copy of the legacy code.

Review: five area reviewers (ABI, AOB, chain, GM + value, build + harness),
then a second round with three lenses the output diff cannot see (side effects
such as logging or cancellation, in-process hazards, call sites); every
finding went to an independent refuter. One survived — `--no-images --only
chain` passed after comparing nothing — and is fixed. The second round found
nothing.

Also verified: x64 + x86 Release, x64 + x86 Debug, x64 Publish (`/MT`), and a
full legacy build (`DISCRETE_RUST_R2=OFF DISCRETE_RUST_METADATA=OFF`) all
build warning-free in the R2 code; `cargo test` 88 (x64) / 89 (i686) and
`cargo clippy --all-targets` clean on both; 1130 UI tests green.

Intentional differences (none observable in the harness):
- **Allocation failure.** The chain detector's 1 MiB chunk buffer and every
  GM buffer use `try_reserve`: failure means "no matches" / "no assignment"
  instead of `bad_alloc`. Small hash-set / result-vector growth in the chain
  detector still aborts on OOM like any Rust allocation — bounded by the
  number of matches, so not a realistic risk.
- **Defensive inputs.** NULL pointers that legacy would dereference (only
  reachable through the C ABI, never from the wrappers) return the failure
  value instead.
- **Speed.** The Rust chain scan is ~25–35 % slower than Zydis on images with
  few candidates and faster with many; seconds either way.

Live-DLL check: ✅ passed 2026-11-14 — see [Live A/B](#live-ab-r1--r2).

## Live A/B (R1 + R2)

2026-11-14, Utawarerumono: Past and Present Rediscovered (IL2CPP, Unity
6000.0.67f1, metadata v31), title screen, proxy auto-start.
`tools/smoke/r2_live.py` ran against the shipping DLL (build #1466, `/MT`,
Rust R1 + R2), then the game was restarted on a legacy build (#1467,
`DISCRETE_RUST_R2=OFF DISCRETE_RUST_METADATA=OFF`) and the same script ran
again; `r2_live.py compare` diffs the restart-stable parts.

| Check | Rust | Legacy |
|---|---|---|
| `init` log `Rust core: discrete_core …` banner | ✅ | ✅ (R0 banner stays) |
| `metadata` log `MetadataParser: loaded version=31 …` | ✅ (Rust parser) | ✅ |
| 400 × `getClassInfo`; 41 × `getEnumValues`, 150 × `il2cppGetMetadataTypeFields`, 3 × `il2cppGetMetadataStrings` (19,327 literals) | identical | identical |
| 150 methods × 2 × `getMethodAobPattern` (module-relative), each pattern matches its own bytes | identical, 300/300 | identical, 300/300 |
| 40 static fields × `findFieldReferences` (chain detector; per-field `FieldXref: v2 scan complete — hopOneCandidates/chainBroken/chainsEmitted/bytesScanned/failedReads` log lines also identical) | identical | identical |
| value scan: begin exact, refine increased / unchanged / decreased / exact / range keep, changed drops (field written with `WriteProcessMemory`, restored) | 7/7 | 7/7 |
| group scan: begin finds the object, SDR assigns both fields, refine keeps / drops | 4/4 | 4/4 |

`compare`: **0 sections differ** — once both games were in the same state.
The first legacy pass ran on a freshly started game and missed the chain
matches for 3 fields: `ClassPointerScanner` (C++, not ported) found 0 slots
because IL2CPP fills a class-pointer slot lazily, when the class is first
used, and the Rust pass had run on a game that had already been probed five
times. A second legacy pass matched exactly. Lesson for future A/B runs:
warm both games the same way (run the script once and discard it) before
comparing xrefs.

Side observations (not R2 regressions): both builds grow the game's private
memory to ~6.4 GB over a run (whole-heap value-scan / walk machinery, same
on legacy); the xref section's time varied between ~2 s and ~21 s across runs of
the same build (warm-state dependent, e.g. lazy-init slot discovery), so it
says nothing about Rust vs legacy.

Follow-up the same day: after the Rust banner switched to the C++ version
scheme (build #1469), the game's `init` log showed `Discrete DLL attached
0.1.0.1469 9636b00 (Release, …)` and `Rust core: discrete_core 0.1.0.1469
9636b00 (Release, …)` — identical, no mismatch `WARN`.

**Mono x64** (same day, 活俠傳 / Legend of Mortal, Unity Mono x64 since its
2026-10 update, build #1469, assembly `Mortal.Core`): `init` log banner pair
identical, no `WARN`; 150 methods × 2 `getMethodAobPattern` self-match
300/300; value scan 7/7; group scan 4/4 (SDR assigns the two fields). Rust
run only — Mono has no metadata parser or chain detector, and JIT addresses
are not restart-stable, so there is nothing restart-stable to A/B beyond
what the offline harness already proves for the shared code.

Two harness lessons from the Mono run (both fixed in `r2_live.py`): the game
pauses when unfocused, so the script turns on `setKeepForeground` for the
run; and `findInstances` can return a stale object whose memory now belongs
to another live object, so the script confirms field ownership with a
read-only `scanFieldValue` reverse lookup before writing anything.

Not covered live: x86 (no 32-bit Mono game on the dev box now — 活俠傳 moved
to x64).

## Fallback / removal

- `-DDISCRETE_RUST_METADATA=OFF` rebuilds the DLL and metadata-cli on the
  legacy C++ parser (`MetadataParser.cpp`). It is an **emergency switch, not a
  `build.cmd` option**: add it to the `cmake -S … -B …` line of
  `Build-DllForArch` in `build.ps1` for that build (the script wipes the CMake
  cache on every run). Keep it until R1 has passed one in-game smoke on an
  IL2CPP target (passed 2026-11-14 — removal is now unblocked). The harness refuses to run (exit 2) if `metadata-cli.exe`
  does not contain the Rust parser, so an OFF build cannot fake a PASS.
- `metadata-cli-legacy.exe` is built next to `metadata-cli.exe` by
  `build.cmd cli` and never shipped; it exists only for the harness.
- **R2 legacy removed (2026-11-14, after the live A/B).** `AobPattern.cpp`,
  `ValueRefine.cpp`, the inline half of `GroupMatch.h`,
  `IL2CPPChainDetector.cpp`, the `DISCRETE_RUST_R2` option, `r2-dump-legacy`
  and `r2_rust_diff.py` are gone; all of it is in git history (last in
  `aae48d3` — check that commit out to rebuild the legacy flavour). There
  is no R2 fallback switch any more.
  The regression gate is now `py -I tools/verify/r2_golden.py` (`build.cmd
  cli` first): it runs `r2-dump` and compares each output's SHA-256 with
  `tools/verify/r2_golden.json`, recorded right after the Rust build was
  re-proven byte-identical to legacy (19/19). The synthetic checks hold on
  any machine; image checks are keyed by the SHA-256 of the
  `GameAssembly.dll` file, so the same game build checks anywhere and other
  builds report SKIP. `--record` rewrites entries — only after proving a
  behaviour change correct. Checked 17/17 on Release and Debug, and a
  tampered hash reports FAIL.
  Zydis stays while `IL2CPPFieldXref.cpp`'s instance-field scan uses it.
- After the in-game smoke: delete `MetadataParser.cpp`, the legacy half of
  `MetadataParser.h`, the CMake option and `metadata-cli-legacy`. The harness
  then becomes a Rust-vs-recorded-baseline check (record `dump-parser`
  output first).
