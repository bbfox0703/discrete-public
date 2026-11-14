#!/usr/bin/env python3
"""Phase R1 gate: prove the Rust metadata parser behaves exactly like the C++ one.

Runs the same metadata-cli subcommands through two binaries built from the
same CLI sources -- metadata-cli.exe (Rust parser, dll/rust/src/metadata) and
metadata-cli-legacy.exe (legacy C++ MetadataParser.cpp) -- over real
global-metadata.dat files, and requires the exit code, stdout and stderr of
every run to be byte-identical.

    py -I tools/verify/metadata_rust_diff.py <global-metadata.dat> [...]
    py -I tools/verify/metadata_rust_diff.py            # DISCRETE_METADATA_FIXTURES, else tools/verify/fixtures.local.txt
    py -I tools/verify/metadata_rust_diff.py --cli-dir build/cli-debug/bin <files...>

Build both binaries first with `build.cmd cli` (they land in
build/cli-release/bin/; only metadata-cli.exe is copied to dist/).

Coverage, per file:
  dump-parser          every public Parser method, plus out-of-range probes.
                       Full walks: string literals, type definitions, fields,
                       nested-type parents, methods (+ a getter/enumerator
                       consistency check), parameters, images (+ both edges of
                       each image's method range). Sampled because they are
                       O(n^2) through the legacy parser: FindFieldDefault
                       (all enum types + every 7th type), FindTypeByName
                       (every 97th type + fixed names), FindImageContaining-
                       Method (every 61st method + image edges).
  info / stats / types (with and without GameAssembly enrichment) /
  methods / images     the user-facing JSON commands
  type <ns> <name>     a deterministic sample of types (fields, defaults,
                       nested names via FindTypeByName)
  method <ns> <t> <m>  a deterministic sample of methods (parameters)
  symbols              the D-5 symbol pipeline; its three output files are
                       compared too

WHAT THIS PROVES: for these inputs, the Rust and C++ parsers are
indistinguishable through the whole public API. WHAT IT DOES NOT: behaviour
on inputs not in the corpus (unit tests in dll/rust cover synthetic corrupt /
truncated files), and the live DLL path (Parser::Get() inside a game) -- that
needs one in-game smoke.

Exit code: 0 = all identical, 1 = at least one difference, 2 = setup error.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
import os
import subprocess
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
RUST_EXE = "metadata-cli.exe"
LEGACY_EXE = "metadata-cli-legacy.exe"
# Present only when the Rust parser is linked: the panic-guard label of the
# Rust load entry point (dll/rust/src/metadata/ffi.rs).
RUST_MARKER = b"dc_md_load_path_w"
TYPE_SAMPLES = 150
METHOD_SAMPLES = 60
WELL_KNOWN_TYPES = [
    ("System", "Int32"), ("System", "String"), ("System", "Single"),
    ("UnityEngine", "Vector3"), ("UnityEngine", "GameObject"), ("UnityEngine", "Mathf"),
]


@dataclass
class Run:
    code: int
    out: Path
    err: bytes


@dataclass
class Result:
    fixture: str
    checks: int = 0
    failures: list[str] = field(default_factory=list)


def run_cli(exe: Path, args: list[str], out_file: Path) -> Run:
    with out_file.open("wb") as fh:
        p = subprocess.run([str(exe), *args], stdout=fh, stderr=subprocess.PIPE, check=False)
    return Run(p.returncode, out_file, p.stderr)


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def first_diff(a: Path, b: Path) -> str:
    with a.open("rb") as fa, b.open("rb") as fb:
        for n, (la, lb) in enumerate(zip(fa, fb), start=1):
            if la != lb:
                return f"line {n}:\n      rust:   {la[:300]!r}\n      legacy: {lb[:300]!r}"
        return "one output is a prefix of the other (different length)"


def compare(res: Result, label: str, rust: Run, legacy: Run, must_succeed: bool = False) -> None:
    res.checks += 1
    problems = []
    # Identical failures would otherwise "pass" without exercising anything
    # (e.g. no GameAssembly.dll next to the fixture for `symbols`).
    if must_succeed and legacy.code != 0:
        problems.append(f"expected success, legacy exit code {legacy.code}: {legacy.err[:300]!r}")
    if rust.code != legacy.code:
        problems.append(f"exit code rust={rust.code} legacy={legacy.code}")
    if rust.err != legacy.err:
        problems.append(f"stderr differs:\n      rust:   {rust.err[:300]!r}\n      legacy: {legacy.err[:300]!r}")
    if sha256(rust.out) != sha256(legacy.out):
        problems.append("stdout differs at " + first_diff(rust.out, legacy.out))
    if problems:
        res.failures.append(f"{label}: " + "; ".join(problems))


def sample_evenly(items: list, n: int) -> list:
    if len(items) <= n:
        return items
    step = len(items) / n
    return [items[int(i * step)] for i in range(n)]


def json_lines(path: Path) -> list[dict]:
    rows = []
    with path.open("rb") as fh:
        for line in fh:
            line = line.strip()
            if line:
                try:
                    rows.append(json.loads(line))
                except json.JSONDecodeError:
                    pass  # non-JSON noise is still covered by the byte compare
    return rows


def check_fixture(fixture: Path, rust_exe: Path, legacy_exe: Path, work: Path) -> Result:
    res = Result(str(fixture))
    spec = str(fixture)
    counter = iter(range(1 << 30))

    def both(label: str, args: list[str], must_succeed: bool = False) -> tuple[Run, Run]:
        n = next(counter)
        r = run_cli(rust_exe, args, work / f"{n:04d}.rust.out")
        l = run_cli(legacy_exe, args, work / f"{n:04d}.legacy.out")
        compare(res, label, r, l, must_succeed)
        return r, l

    both("dump-parser", ["dump-parser", spec], must_succeed=True)
    both("info", ["info", spec], must_succeed=True)
    both("stats", ["stats", spec])
    both("types --no-binary", ["--no-binary", "types", spec])
    _, types_run = both("types", ["types", spec])
    _, methods_run = both("methods", ["methods", spec])
    both("images", ["images", spec])

    types = [(t.get("namespace", ""), t.get("name", ""))
             for t in json_lines(types_run.out) if t.get("field_count", 0) > 0]
    for ns, name in WELL_KNOWN_TYPES + sample_evenly(types, TYPE_SAMPLES):
        both(f"type {ns}.{name}", ["--no-binary", "type", spec, ns, name])

    methods = [(m.get("declaring_namespace", ""), m.get("declaring_type", ""), m.get("name", ""))
               for m in json_lines(methods_run.out) if m.get("parameter_count", 0) > 0]
    for ns, ty, mn in sample_evenly(methods, METHOD_SAMPLES):
        both(f"method {ns}.{ty}::{mn}", ["method", spec, ns, ty, mn])

    # symbols writes <prefix>.dd64 / .ghidra.txt / .idc next to the prefix.
    rust_prefix, legacy_prefix = work / "sym-rust", work / "sym-legacy"
    for prefix in (rust_prefix, legacy_prefix):  # no stale files from a --keep rerun
        for ext in (".dd64", ".ghidra.txt", ".idc"):
            Path(str(prefix) + ext).unlink(missing_ok=True)
    r = run_cli(rust_exe, ["symbols", spec, str(rust_prefix)], work / "sym.rust.out")
    l = run_cli(legacy_exe, ["symbols", spec, str(legacy_prefix)], work / "sym.legacy.out")
    # stdout/stderr echo the output prefix, which differs by construction --
    # raw in log lines, JSON-escaped (doubled backslashes) in the summary.
    def mask(data: bytes, prefix: Path) -> bytes:
        raw = str(prefix).encode()
        return data.replace(raw.replace(b"\\", b"\\\\"), b"<prefix>").replace(raw, b"<prefix>")

    for run, prefix in ((r, rust_prefix), (l, legacy_prefix)):
        run.err = mask(run.err, prefix)
        run.out.write_bytes(mask(run.out.read_bytes(), prefix))
    compare(res, "symbols (console)", r, l, must_succeed=True)
    for ext in (".dd64", ".ghidra.txt", ".idc"):
        a, b = Path(str(rust_prefix) + ext), Path(str(legacy_prefix) + ext)
        res.checks += 1
        if not (a.exists() and b.exists()):
            res.failures.append(f"symbols {ext}: missing output, rust={a.exists()} legacy={b.exists()}")
        elif sha256(a) != sha256(b):
            res.failures.append(f"symbols {ext}: differs at " + first_diff(a, b))
    return res



def load_fixtures() -> list[str]:
    """$DISCRETE_METADATA_FIXTURES (';'-separated) if set, else the git-ignored
    tools/verify/fixtures.local.txt (one path per line, '#' comments; template:
    fixtures.example.txt). Inlined in each harness because `py -I` does not put
    the script's directory on sys.path."""
    env = os.environ.get("DISCRETE_METADATA_FIXTURES", "")
    if env:
        return [p for p in env.split(";") if p]
    local = Path(__file__).resolve().parent / "fixtures.local.txt"
    if not local.is_file():
        return []
    out = []
    for line in local.read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if line:
            out.append(line)
    return out

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("fixtures", nargs="*", help="global-metadata.dat paths (default: $DISCRETE_METADATA_FIXTURES)")
    ap.add_argument("--cli-dir", default=str(REPO / "build" / "cli-release" / "bin"),
                    help="directory holding both metadata-cli builds")
    ap.add_argument("--keep", metavar="DIR", help="keep every captured output under DIR")
    ap.add_argument("-j", "--jobs", type=int, default=max(1, min(4, os.cpu_count() or 1)))
    args = ap.parse_args()

    fixtures = args.fixtures or load_fixtures()
    if not fixtures:
        print("no fixtures: pass global-metadata.dat paths, set DISCRETE_METADATA_FIXTURES, "
              "or list them in tools/verify/fixtures.local.txt", file=sys.stderr)
        return 2
    cli_dir = Path(args.cli_dir)
    rust_exe, legacy_exe = cli_dir / RUST_EXE, cli_dir / LEGACY_EXE
    for exe in (rust_exe, legacy_exe):
        if not exe.exists():
            print(f"missing {exe} -- run `build.cmd cli` first", file=sys.stderr)
            return 2
    # Guard against a vacuous PASS: if both binaries carry the same parser
    # (e.g. configured with -DDISCRETE_RUST_METADATA=OFF), every diff is empty.
    if RUST_MARKER not in rust_exe.read_bytes():
        print(f"{rust_exe} does not contain the Rust parser (built with DISCRETE_RUST_METADATA=OFF?)", file=sys.stderr)
        return 2
    if RUST_MARKER in legacy_exe.read_bytes():
        print(f"{legacy_exe} unexpectedly contains the Rust core", file=sys.stderr)
        return 2
    missing = [f for f in fixtures if not Path(f).is_file()]
    if missing:
        print("missing fixture(s):\n  " + "\n  ".join(missing), file=sys.stderr)
        return 2

    root_ctx = tempfile.TemporaryDirectory(prefix="metadata_rust_diff_") if not args.keep else None
    root = Path(args.keep) if args.keep else Path(root_ctx.name)
    root.mkdir(parents=True, exist_ok=True)
    try:
        jobs = {}
        with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as pool:
            for i, f in enumerate(fixtures):
                work = root / f"fixture{i:02d}"
                work.mkdir(parents=True, exist_ok=True)
                jobs[pool.submit(check_fixture, Path(f), rust_exe, legacy_exe, work)] = f
            results = [fut.result() for fut in concurrent.futures.as_completed(jobs)]
    finally:
        if root_ctx:
            root_ctx.cleanup()

    total_checks = total_fail = 0
    for res in sorted(results, key=lambda r: r.fixture):
        total_checks += res.checks
        total_fail += len(res.failures)
        status = "PASS" if not res.failures else f"FAIL ({len(res.failures)})"
        print(f"[{status}] {res.checks:4d} checks  {res.fixture}")
        for f in res.failures[:20]:
            print(f"    - {f}")
        if len(res.failures) > 20:
            print(f"    ... {len(res.failures) - 20} more")
    print(f"\n{len(results)} fixture(s), {total_checks} checks, {total_fail} difference(s)")
    return 0 if total_fail == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
