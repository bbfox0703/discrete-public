# zydis-oracle

Differential dev tool: proves the Rust core's x86-64 decoder adapter still
answers every question exactly the way **Zydis** did for the legacy C++ code
it replaced. Never shipped; not part of `build.cmd`.

The legacy IL2CPP chain detector and instance-field xref scan asked Zydis 5.0
(commit `17bc6fb`, decoder `LONG_64` / `STACK_WIDTH_64`, default modes) a
handful of questions per instruction. `dll/rust/src/chain/decode.rs` answers
them with iced-x86 instead, in Zydis' terms, plus two tables generated from
Zydis for the APX encodings iced cannot decode. This crate compiles the
**real** `dll/rust` modules (`ffi`, `chain`, `xref` via `#[path]`, no copies)
and compares them with C++ code that calls the pinned Zydis directly.

## What it proves

| Test | Compares | Against |
|---|---|---|
| `oracle_chain::fuzz_random` | chain projection of `InsnDecoder::decode` (length, mnemonic class, operands, category, written registers — the facts the trace consults) | `native/oracle_chain.cpp` `oracle_project` |
| `oracle_chain::golden_vectors` | every `("hex", "projection")` vector in `dll/rust/src/chain/tests.rs` | Zydis |
| `oracle_chain::e2e_matches_legacy` | `chain::detect` on generated PE modules: match list + all stats | the legacy `DetectFieldAccessChains` (Zydis), transcribed in `oracle_chain.cpp` |
| `oracle_chain::msvc_sort_matches_std_sort` | `chain/msvc_sort.rs` | MSVC `std::sort` |
| `oracle_xref::fuzz_random` | xref projection of `InsnDecoder::decode_mem` (per-instruction hit list of the instance-field scan) | `native/oracle_xref.cpp` `oxr_project` |
| `oracle_sweep::{chain,xref}_*` | both projections over exhaustive / structured encoding spaces (table in `src/oracle_sweep.rs`) | Zydis |
| `oracle_tables::apx_map4_regenerates` | `dll/rust/src/chain/apx_map4.rs` regenerated from Zydis | the repo file, byte for byte |
| `oracle_tables::apx_mem_regenerates` | `dll/rust/src/xref/apx_mem.rs` regenerated from Zydis | the repo file, byte for byte |
| `oracle_chain::real_game_assembly_images` | `chain::detect` on real `GameAssembly.dll` files | legacy detector |

The scan-loop behaviour of the xref kernels around the decoder (windows,
stop rules, ordering) is covered by `tools/verify/r2_golden.py`, not here.

## When to run it

Before committing any change to:

- `dll/rust/src/chain/decode.rs` (or anything else under `chain/`),
- the xref memory-operand facts (`decode_mem`, `MemFacts`) or `dll/rust/src/xref/`,
- `chain/apx_map4.rs` / `xref/apx_mem.rs`,
- the `iced-x86` pin (keep `Cargo.toml` here identical to `dll/rust/Cargo.toml`),
- the Rust toolchain (`rust-toolchain.toml`).

`quick` for every such change; `full` before merging a decoder change or an
iced-x86 / toolchain bump.

## Prerequisites

- Windows x64, Visual Studio or Build Tools with the C++ desktop workload (found via `vswhere`). The lib is rebuilt whenever the MSVC toolset version changes (e.g. after a Visual Studio update).
- rustup (the repo-root `rust-toolchain.toml` pins the toolchain; no separate one here).
- git with network access on the first run (`fetch_zydis.ps1` clones Zydis at
  the pin into `vendor/zydis`, shallow, with its Zycore submodule). Zydis is
  no longer a DLL dependency, so this tool keeps its own copy.

## Commands

From the repo root (any PowerShell):

```powershell
.\tools\zydis-oracle\build.ps1                     # quick (default)
.\tools\zydis-oracle\build.ps1 -Mode full          # the original proofs
.\tools\zydis-oracle\build.ps1 -Mode tables        # regenerate + diff both tables
.\tools\zydis-oracle\build.ps1 -Mode probe -Hex "62 F4 7C 08 01 43 11"
.\tools\zydis-oracle\build.ps1 -Mode real -Dlls "D:\Games\X\GameAssembly.dll;D:\Games\Y\GameAssembly.dll"
.\tools\zydis-oracle\build.ps1 -Mode full -Filter oracle_sweep::chain_legacy   # one test
.\tools\zydis-oracle\build.ps1 -Rebuild            # recompile the C++ side
```

`build.ps1` fetches Zydis (no-op once present), loads the MSVC environment,
compiles Zydis + `native/*.cpp` into `build/zydis_oracle.lib` (when out of
date), then runs `cargo test --locked` with the mode's settings. Outputs stay
inside this folder (`vendor/`, `build/`, `target/`, all git-ignored).

## Run modes

Measured on a 32-thread dev box; cargo runs tests in parallel and the long
tests spread over all cores.

| Mode | Cases | Time |
|---|---|---|
| `quick` | chain fuzz 2 M (8 generator modes x 250k), xref fuzz 1.8 M (9 x 200k), 53 golden vectors, 3k generated modules e2e, 20k sorts, both tables | ~35 s incl. compile |
| `full` | chain fuzz 160 M (8 x 20 M), e2e 60k modules, xref fuzz 99 M (9 x 5.5 M x seeds 1, 2), all sweeps for both projections (293 M each), both tables | ~3 min (e2e is the long pole) |
| `tables` | both generators only (~1.2 M Zydis decodes) | seconds |

Knobs (environment, read by the tests): `CHAIN_FUZZ`, `CHAIN_GEN`, `CHAIN_SEEDS`,
`XF_ITERS`, `XF_MODES`, `XF_SEEDS`, `E2E_ITERS`, `E2E_SEED`.

## Reading a mismatch

A failing comparison prints one line per mismatch class (Zydis mnemonic or
`X` = Zydis rejects; `meOK` / `meX` = the Rust side accepts / rejects; for the
chain projection also the iced `Code`), up to four examples with the bytes,
both projections and diagnostics, then `N mismatches / M cases`.

- Projection format — chain: `L<len>`, `RD<base>@<disp>` / `WR...` (hop-3
  read / write shapes), `H2...>reg` (hop-2 shape), `T` (ret / jmp), `C`
  (call), `B` (conditional branch), `MV<dst><<src>` (reg-reg mov), `W:<regs>`
  (written trackable registers). xref: `L<len>` then ` R<disp>` / ` W<disp>`
  per candidate memory operand. `X` = decode failure. Zydis' text after ` |`
  is diagnostics only (mnemonic id, category, ISA set; operand details).
- Reproduce one case: `-Mode probe -Hex "<bytes>"`.
- Decide **harness vs decoder**: if the Rust projection changed for an
  encoding the legacy consumer can see, `decode.rs` has to reproduce Zydis
  again (see its module docs for every known divergence and how it is
  handled). A difference only in what the harness formats (e.g. a new
  projection field) is a harness bug.
- A table mismatch names the first differing line; diff
  `target/oracle-tables/<file>` against the repo file.

## Regenerating a table

The generators never write into `dll/rust`. After a deliberate change (e.g. a
new Zydis pin, which is a decision of its own), copy
`target/oracle-tables/apx_map4.rs` / `apx_mem.rs` over the repo files, then run
`full` and `tools/verify/r2_golden.py`. The emitted header text is part of the
byte-identical check; change it in `src/oracle_tables.rs` and the repo file
together.

## Layout

```
fetch_zydis.ps1     pinned Zydis -> vendor/zydis (idempotent, verifies the commit)
build.ps1           MSVC env + native lib + cargo test (run modes)
native/             Zydis side: legacy chain detector + chain projection; xref projection + raw memory facts
src/lib.rs          #[path] includes of dll/rust/src/{ffi.rs, chain/, xref/}
src/zydis.rs        FFI, projections, random generator, mismatch collector
src/oracle_*.rs     the tests above
```
