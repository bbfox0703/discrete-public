#!/usr/bin/env python3
"""Phase R2 gate: prove the Rust ports behave exactly like the legacy C++.

Runs the same r2-dump subcommands through two binaries built from one driver
source (tools/r2-diff/r2_dump.cpp) -- r2-dump.exe (Rust ports behind the
*Rs.cpp wrappers) and r2-dump-legacy.exe (legacy AobPattern.cpp /
GroupMatch.h / ValueRefine.cpp / IL2CPPChainDetector.cpp + Zydis) -- and
requires the exit code, stdout and stderr of every run to be byte-identical,
every run to succeed, and every stdout to end with the {"rec":"end"} record.

    py -I tools/verify/r2_rust_diff.py <GameAssembly.dll> [...]
    py -I tools/verify/r2_rust_diff.py          # images from DISCRETE_METADATA_FIXTURES
    py -I tools/verify/r2_rust_diff.py --no-images      # generated cases only
    py -I tools/verify/r2_rust_diff.py --cli-dir build/cli-debug/bin -j 8 <dlls...>

Build both binaries first with `build.cmd cli` (they land in
build/cli-release/bin/ and are never copied to dist/). Images default to
<root>/GameAssembly.dll for every <root>/<X>_Data/il2cpp_data/Metadata/
global-metadata.dat listed in DISCRETE_METADATA_FIXTURES (';'-separated, the
same variable the R1 harness uses); fixtures without a GameAssembly.dll next
to them are skipped with a note.

Coverage (fixed seeds, so every run sees the same cases):
  aob-synthetic   Aob::BuildPattern over 50k generated buffers (0..200 bytes,
                  biased toward real x64 encodings: prefixes, REX, 0F maps,
                  rel8/rel32 branches, RIP / SIB / disp ModRM forms, imm sizes,
                  VEX) x stopAtTerminal {false,true}, plus NULL/len-0 and
                  non-NULL/len-0. Every PatternResult field is compared.
  gm              GroupMatch::HasDistinctAssignment + ResolveAssignment over
                  200k instances (0..6 slots, nLeaves -2..10, empty lists,
                  duplicates, negative / out-of-range / INT_MIN / INT_MAX
                  leaves, a stale pre-filled `out`).
  refine          ValueRefine::CompareCandidateForRefine over 500k cases: dt
                  -1..10 (incl. invalid), real + near-miss scan types ("",
                  "EXACT", "exact ", "exact\\0", garbage bytes), payloads with
                  +-0 / +-inf / quiet + signalling NaN / denormals / max / min
                  and correlated values (equal, +-1, +-tolerance), tolerances
                  {0, 1e-4, 0.5, 1e30, -1, NaN, inf}; keep + prev-after.
  aob <image>     BuildPattern over real code: 5000 random offsets + 5000
                  function-like starts (after 0xCC/0x90 padding) x lengths
                  {8..128} x stopAtTerminal.
  chain <image>   IL2CPP::DetectFieldAccessChains over 128 class-pointer-like
                  slots the driver finds itself (RIP-relative mov/lea targets
                  in data sections): staticFieldsOffset {0xB8,0x8,0x18} x
                  field offset {0..0x48}, match limits {0,1,5}, empty slots,
                  staticFieldsOffset 0, NULL stats, reversed + duplicated
                  slots, bogus slots. Matches (order, RVAs, read/write) and
                  every stats field -- including "left untouched" on the
                  early-return paths -- are compared.
  error paths     usage error and an unmappable image fail identically.

The driver maps each GameAssembly.dll with DONT_RESOLVE_DLL_REFERENCES (no
DllMain, no imports) and overwrites its executable sections with the on-disk
bytes, so the two processes scan identical code wherever the loader placed
the module; all printed addresses are module-relative.

WHAT THIS PROVES: for these inputs, the four R2 modules are indistinguishable
through their public C++ API (wrapper + Rust port vs legacy), including edge
cases the generators deliberately aim at. WHAT IT DOES NOT: behaviour on
inputs outside the corpus; the read-fault paths of the chain detector (mapped
images are fully readable, so failedReads stays 0 and the SEH shim never
faults); x86 builds (this harness is x64 only -- the i686 Rust code is
covered by `cargo test --target i686-pc-windows-msvc`); the Rust panic
fallbacks; the callers in ExportAPI.cpp; and live in-game behaviour (code
patched at runtime, the real class-pointer slots of a running IL2CPP
runtime) -- that needs one in-game smoke per backend.

Exit code: 0 = all identical, 1 = at least one difference, 2 = setup error.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import os
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
RUST_EXE = "r2-dump.exe"
LEGACY_EXE = "r2-dump-legacy.exe"
# Present only when the Rust chain detector is linked: the panic-guard label
# of its C ABI entry point (dll/rust/src/chain.rs). The chain module is the
# last one a build would drop, so its marker implies the full Rust set.
RUST_MARKER = b"dc_chain_detect"
GENERATED_CMDS = ["aob-synthetic", "gm", "refine"]
IMAGE_CMDS = ["aob", "chain"]
END_RECORD = b'{"rec":"end"}\n'


@dataclass
class Run:
    code: int
    out: Path
    err: bytes
    seconds: float


@dataclass
class Task:
    label: str
    args: list[str]
    must_succeed: bool = True
    expect_code: int | None = None  # for the error-path checks


@dataclass
class Result:
    label: str
    failures: list[str] = field(default_factory=list)
    lines: int = 0
    size: int = 0
    seconds: float = 0.0


def run_cli(exe: Path, args: list[str], out_file: Path) -> Run:
    t0 = time.perf_counter()
    with out_file.open("wb") as fh:
        p = subprocess.run([str(exe), *args], stdout=fh, stderr=subprocess.PIPE, check=False)
    return Run(p.returncode, out_file, p.stderr, time.perf_counter() - t0)


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def count_lines(path: Path) -> int:
    n = 0
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            n += chunk.count(b"\n")
    return n


def last_line(path: Path) -> bytes:
    size = path.stat().st_size
    with path.open("rb") as fh:
        fh.seek(max(0, size - 256))
        tail = fh.read()
    nl = tail.rfind(b"\n", 0, len(tail) - 1)
    return tail[nl + 1:]


def first_diff(a: Path, b: Path) -> str:
    """First differing line, with a window around the first differing byte --
    aob lines run to several KB, so the line head alone is rarely enough."""
    with a.open("rb") as fa, b.open("rb") as fb:
        n = 0
        while True:
            la, lb = fa.readline(), fb.readline()
            n += 1
            if la == lb:
                if not la:
                    return "outputs are identical"  # cannot happen after a hash mismatch
                continue
            if not la or not lb:
                return f"line {n}: {'rust' if not la else 'legacy'} output ends early"
            col = next((i for i, (x, y) in enumerate(zip(la, lb)) if x != y), min(len(la), len(lb)))
            lo = max(0, col - 100)
            return (f"line {n}, byte {col}:\n"
                    f"      line head: {la[:120]!r}\n"
                    f"      rust:   ...{la[lo:col + 200]!r}\n"
                    f"      legacy: ...{lb[lo:col + 200]!r}")


def check_task(task: Task, rust_exe: Path, legacy_exe: Path, work: Path) -> Result:
    res = Result(task.label)
    r = run_cli(rust_exe, task.args, work / "rust.out")
    l = run_cli(legacy_exe, task.args, work / "legacy.out")
    res.seconds = r.seconds + l.seconds
    problems = []
    if task.must_succeed:
        # Identical failures would otherwise "pass" without exercising anything
        # (e.g. an image the driver could not map).
        for name, run in (("rust", r), ("legacy", l)):
            if run.code != 0:
                problems.append(f"expected success, {name} exit code {run.code}: {run.err[:300]!r}")
        if l.code == 0 and last_line(l.out) != END_RECORD:
            problems.append("legacy stdout does not end with the end record (truncated run?)")
    elif task.expect_code is not None and l.code != task.expect_code:
        problems.append(f"expected exit code {task.expect_code}, legacy gave {l.code}")
    if r.code != l.code:
        problems.append(f"exit code rust={r.code} legacy={l.code}")
    if r.err != l.err:
        problems.append(f"stderr differs:\n      rust:   {r.err[:300]!r}\n      legacy: {l.err[:300]!r}")
    if sha256(r.out) != sha256(l.out):
        problems.append("stdout differs at " + first_diff(r.out, l.out))
    res.failures = problems
    res.size = l.out.stat().st_size
    res.lines = count_lines(l.out)
    return res


def derive_images(fixtures: list[str]) -> tuple[list[Path], list[str]]:
    """<root>/<X>_Data/il2cpp_data/Metadata/global-metadata.dat -> <root>/GameAssembly.dll"""
    images, notes = [], []
    for f in fixtures:
        p = Path(f)
        parts = [x.lower() for x in p.parts]
        if (len(parts) >= 5 and parts[-1] == "global-metadata.dat" and parts[-2] == "metadata"
                and parts[-3] == "il2cpp_data" and parts[-4].endswith("_data")):
            dll = p.parents[3] / "GameAssembly.dll"
            if dll.is_file():
                images.append(dll)
            else:
                notes.append(f"no GameAssembly.dll next to {f} -- skipped")
        else:
            notes.append(f"not a <X>_Data/il2cpp_data/Metadata/global-metadata.dat path: {f} -- skipped")
    return images, notes


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("images", nargs="*",
                    help="GameAssembly.dll paths (default: derived from $DISCRETE_METADATA_FIXTURES)")
    ap.add_argument("--cli-dir", default=str(REPO / "build" / "cli-release" / "bin"),
                    help="directory holding r2-dump.exe and r2-dump-legacy.exe")
    ap.add_argument("--keep", metavar="DIR", help="keep every captured output under DIR")
    ap.add_argument("-j", "--jobs", type=int, default=max(1, min(4, os.cpu_count() or 1)))
    ap.add_argument("--no-images", action="store_true",
                    help="run only the generated subcommands (no GameAssembly.dll needed)")
    ap.add_argument("--only", nargs="+", choices=GENERATED_CMDS + IMAGE_CMDS, metavar="CMD",
                    help=f"restrict to these subcommands ({', '.join(GENERATED_CMDS + IMAGE_CMDS)})")
    args = ap.parse_args()

    cli_dir = Path(args.cli_dir)
    rust_exe, legacy_exe = cli_dir / RUST_EXE, cli_dir / LEGACY_EXE
    for exe in (rust_exe, legacy_exe):
        if not exe.exists():
            print(f"missing {exe} -- run `build.cmd cli` first", file=sys.stderr)
            return 2
    # Guard against a vacuous PASS: if both binaries carried the same
    # implementation, every diff would be empty.
    if RUST_MARKER not in rust_exe.read_bytes():
        print(f"{rust_exe} does not contain the Rust R2 ports (marker {RUST_MARKER!r} missing)", file=sys.stderr)
        return 2
    if RUST_MARKER in legacy_exe.read_bytes():
        print(f"{legacy_exe} unexpectedly contains the Rust core", file=sys.stderr)
        return 2

    selected = set(args.only or GENERATED_CMDS + IMAGE_CMDS)
    images: list[Path] = []
    if args.no_images:
        if args.images:
            print("--no-images conflicts with explicit image paths", file=sys.stderr)
            return 2
    elif selected & set(IMAGE_CMDS):
        if args.images:
            images = [Path(p) for p in args.images]
            missing = [str(p) for p in images if not p.is_file()]
            if missing:
                print("missing image(s):\n  " + "\n  ".join(missing), file=sys.stderr)
                return 2
        else:
            fixtures = [p for p in os.environ.get("DISCRETE_METADATA_FIXTURES", "").split(";") if p]
            images, notes = derive_images(fixtures)
            for n in notes:
                print(f"note: {n}")
        # The driver finds the mapped module with GetModuleHandleW(L"GameAssembly.dll"),
        # exactly like both chain implementations, so the file name is load-bearing.
        bad = [str(p) for p in images if p.name.lower() != "gameassembly.dll"]
        if bad:
            print("image(s) must be named GameAssembly.dll:\n  " + "\n  ".join(bad), file=sys.stderr)
            return 2
        seen, unique = set(), []
        for p in images:
            key = os.path.normcase(str(p.resolve()))
            if key not in seen:
                seen.add(key)
                unique.append(p.resolve())
        images = unique
        if not images:
            print("no GameAssembly.dll: pass paths, set DISCRETE_METADATA_FIXTURES, "
                  "or use --no-images for the generated cases only", file=sys.stderr)
            return 2

    tasks: list[Task] = [Task(c, [c]) for c in GENERATED_CMDS if c in selected]
    for img in images:
        for c in IMAGE_CMDS:
            if c in selected:
                tasks.append(Task(f"{c} {img}", [c, str(img)]))
    # Guard against a vacuous PASS from contradictory flags (e.g. --no-images
    # --only chain): the error-path checks below exercise no R2 code.
    if not tasks:
        print("nothing to compare: the selected subcommands need a GameAssembly.dll "
              "but --no-images was given", file=sys.stderr)
        return 2
    # Error paths: the driver itself must fail identically in both flavours.
    tasks.append(Task("error: usage", ["no-such-subcommand"], must_succeed=False, expect_code=1))
    if images:
        tasks.append(Task("error: unmappable image",
                          ["chain", str(images[0].parent / "no-such-dir" / "GameAssembly.dll")],
                          must_succeed=False, expect_code=2))

    root_ctx = tempfile.TemporaryDirectory(prefix="r2_rust_diff_") if not args.keep else None
    root = Path(args.keep) if args.keep else Path(root_ctx.name)
    root.mkdir(parents=True, exist_ok=True)
    t0 = time.perf_counter()
    try:
        jobs = {}
        with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
            for i, task in enumerate(tasks):
                work = root / f"task{i:03d}"
                work.mkdir(parents=True, exist_ok=True)
                (work / "args.txt").write_text(" ".join(task.args) + "\n", encoding="utf-8")
                jobs[pool.submit(check_task, task, rust_exe, legacy_exe, work)] = task
            results = [fut.result() for fut in concurrent.futures.as_completed(jobs)]
    finally:
        if root_ctx:
            root_ctx.cleanup()

    order = {t.label: i for i, t in enumerate(tasks)}
    total_fail = 0
    for res in sorted(results, key=lambda r: order[r.label]):
        total_fail += len(res.failures)
        status = "PASS" if not res.failures else "FAIL"
        print(f"[{status}] {res.lines:8d} lines {res.size / 1e6:8.1f} MB {res.seconds:7.1f}s  {res.label}")
        for f in res.failures:
            print(f"    - {f}")
    print(f"\n{len(results)} check(s), {total_fail} problem(s), {time.perf_counter() - t0:.1f}s"
          + (f"; outputs kept in {root}" if args.keep else ""))
    return 0 if total_fail == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
