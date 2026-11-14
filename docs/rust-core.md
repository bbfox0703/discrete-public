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
| **R0** | Toolchain: cargo inside `build.cmd`/CMake, x64 + x86, /MD + /MT, Debug + Release; FFI conventions; log sink; panic guard | All arch × CRT combinations build and link; `cargo test` passes on x64 and i686; Rust banner in the init log | ✅ offline (see below); banner in a live game pending |
| **R1** | IL2CPP `global-metadata.dat` parser (`dll/rust/src/metadata`) behind `MetadataParserRs.cpp` | `tools/verify/metadata_rust_diff.py`: Rust and legacy C++ parsers byte-identical over the local fixture corpus | see [R1 gate](#r1-gate) |
| R2 | Pure algorithms: `AobPattern`, `GroupMatch`, value decoders, `IL2CPPChainDetector` (Zydis → `iced-x86`), plus a C SEH shim for the reads they need | per-module byte-fixture diff | not started |
| R3 (opt.) | `PipeServer` framing; handlers stay C++ | wire-level replay | not started |
| R4+ | Backend walkers / `ExportAPI` | only if R1–R3 show a real maintenance win | not planned |

## Layout

```
rust-toolchain.toml            pinned rustc + both targets (repo root)
dll/rust/
  Cargo.toml / Cargo.lock      crate `discrete_core`, ZERO dependencies
  RustCore.cmake               cargo -> IMPORTED target `discrete_core`
  include/discrete_core.h      the C ABI (hand-maintained, no cbindgen)
  src/lib.rs                   FFI rules (read this first)
  src/ffi.rs                   log sink, panic guard, version banner
  src/metadata/                R1 parser (layout.rs, parser.rs, ffi.rs, tests)
dll/src/shared/RustCore.cpp    C++ log sink + banner (called from DllMain attach)
dll/src/il2cpp/MetadataParserRs.cpp   Parser class -> dc_md_* wrapper
tools/verify/metadata_rust_diff.py    R1 differential harness
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
- Native libs Rust std needs (from `--print native-static-libs`): `kernel32
  ntdll userenv ws2_32 dbghelp synchronization`. Re-check on toolchain bumps.
- `build.cmd test` also runs `cargo test` for **both** triples — the i686 test
  exe runs under WOW64, which exercises 32-bit codegen and panic unwinding
  without a 32-bit game. Set `DISCRETE_METADATA_FIXTURES` (semicolon-separated
  `global-metadata.dat` paths) to include the real-file parser test.
- `build.ps1` requires rustup, runs `rustup toolchain install` (installs the
  pinned toolchain on a fresh clone / after a bump, tolerating rustup's stderr
  progress under Windows PowerShell 5.1), then checks both targets and prints
  the `rustup` command to fix a missing one.

Toolchain bump: edit `rust-toolchain.toml`, rebuild all four combinations
(`build.cmd dll`, `build.cmd dll publish`), rerun `build.cmd test` and the R1
harness.

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

Adding a crate dependency is a decision, not a convenience: record why here
first (supply chain, vendoring, licence — the repo is GPL-3.0).

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

Not covered offline: the `Rust core: discrete_core …` line in a live game's
`init` log (first in-game run of any backend proves it).

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

Live-DLL check still pending: one IL2CPP game with the new
`Discrete-IL2CPP.dll` — `init` log shows `Rust core: discrete_core 0.1.0`,
`metadata` log shows the usual `MetadataParser: loaded version=…` line, and a
metadata-backed RPC (e.g. a class walk with field defaults) answers as before.

## Fallback / removal

- `-DDISCRETE_RUST_METADATA=OFF` rebuilds the DLL and metadata-cli on the
  legacy C++ parser (`MetadataParser.cpp`). It is an **emergency switch, not a
  `build.cmd` option**: add it to the `cmake -S … -B …` line of
  `Build-DllForArch` in `build.ps1` for that build (the script wipes the CMake
  cache on every run). Keep it until R1 has passed one in-game smoke on an
  IL2CPP target. The harness refuses to run (exit 2) if `metadata-cli.exe`
  does not contain the Rust parser, so an OFF build cannot fake a PASS.
- `metadata-cli-legacy.exe` is built next to `metadata-cli.exe` by
  `build.cmd cli` and never shipped; it exists only for the harness.
- After the in-game smoke: delete `MetadataParser.cpp`, the legacy half of
  `MetadataParser.h`, the CMake option and `metadata-cli-legacy`. The harness
  then becomes a Rust-vs-recorded-baseline check (record `dump-parser`
  output first).
