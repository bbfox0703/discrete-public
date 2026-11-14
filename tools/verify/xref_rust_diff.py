#!/usr/bin/env python3
"""R2 follow-up gate: prove the Rust IL2CPPFieldXref scan kernels behave
exactly like the legacy C++ (Zydis) ones.

Runs the same xref-dump subcommands through two binaries built from one
driver source (tools/r2-diff/xref_dump.cpp) -- xref-dump.exe (Rust port behind
IL2CPPFieldXrefScanRs.cpp) and xref-dump-legacy.exe (IL2CPPFieldXrefScan.cpp +
Zydis) -- and requires the exit code, stdout and stderr of every run to be
byte-identical, every run to succeed, and every stdout to end with the
{"rec":"end"} record.

    py -I tools/verify/xref_rust_diff.py <GameAssembly.dll> [...]
    py -I tools/verify/xref_rust_diff.py          # images from the fixture list
    py -I tools/verify/xref_rust_diff.py --no-images      # synthetic cases only
    py -I tools/verify/xref_rust_diff.py --cli-dir build/cli-debug/bin -j 8 <dlls...>

Build both binaries first with `build.cmd cli` (they land in
build/cli-release/bin/ and are never copied to dist/). Images default to
<root>/GameAssembly.dll for every <root>/<X>_Data/il2cpp_data/Metadata/
global-metadata.dat listed in DISCRETE_METADATA_FIXTURES (';'-separated) or,
when that is unset, the git-ignored tools/verify/fixtures.local.txt -- the same
list the R1 harness uses; fixtures without a GameAssembly.dll next
to them are skipped with a note.

Coverage (fixed seeds, so every run sees the same cases):
  rip-synthetic   ScanRipRelative over crafted sections in one VirtualAlloc'd
                  region: REX / no-REX / REX REX / prefixed forms and near
                  misses planted back to back across both 1 MiB chunk
                  boundaries, inside the 16-byte overlap and at the section
                  start / end; sections shifted 1..17 bytes (and 4 KiB, 1 MiB)
                  and trimmed 1..17 bytes so every plant crosses every
                  boundary position (incl. the REX-at-chunk-end duplicate);
                  sections of 0..24 bytes, adjacent sections, a NOACCESS page
                  inside a chunk, a NOACCESS page under only the overlap bytes,
                  a GUARD page, reserved-only memory (4 KiB skip loop),
                  reversed / duplicated / empty / random section lists.
  inst-synthetic  ScanInstanceAccesses x 23 field offsets (0, small, disp8 /
                  disp32 / disp8*N values, 0x7FFFFFFF, 0x80000000, negative
                  as size_t, SIZE_MAX) over 24k ranges (1 B .. 8 KiB) of an
                  8 MiB generated stream -- legacy map 0 / 0F / 0F38 / 0F3A
                  with prefixes, REX and LOCK, VEX2 / VEX3, EVEX maps 1-3 /
                  5 / 6 (masking, broadcast, VSIB), APX EVEX map 4 (ND / NF)
                  and REX2, moffs, string ops, 0xCC, junk -- plus NOACCESS /
                  GUARD / decommitted pages and the reserved tail; and a
                  hand-written table of ~110 edge encodings (RSP / RBP / R12 /
                  R13 / EIP / RIP bases, segment overrides, index, explicit
                  zero displacements, prefetch / clflush / nop / BND* / AGEN
                  actions, conditional writes, x87, FXSAVE family), each
                  exactly and one byte short.
  rip <image>     ScanRipRelative over the real executable sections for ~40
                  harness-picked targets: most-referenced data and code
                  targets, single- and few-reference ones, targets of
                  instructions near 1 MiB chunk boundaries, unreferenced data
                  addresses, the module base / end, 0 and UINTPTR_MAX; plus
                  reversed, first-only and 3-byte-shifted section lists.
  inst <image>    ScanInstanceAccesses over the .pdata function ranges
                  (clamped like IL2CPPFieldXref.cpp: next function, 8 KiB,
                  section end): every range x {0x10, 0x18, 0} (decoder coverage
                  of the whole image), every 16th range x 89 field offsets
                  (0x10..0x200 step 8, 24 odd ones 0x11..0x3F, 0, 0x10001),
                  and every 16th range started 1..3 bytes late (misaligned
                  decode) x 4 offsets. (The full grid over every range would
                  cost ~89 whole-image decodes per binary.) Hits are
                  printed per block of 1024 ranges as count + FNV-1a hash; on a
                  difference this script re-runs `inst-hits` on the first
                  differing block in both binaries and shows the first
                  differing hit.
  error paths     usage error and an unmappable image fail identically.

The driver maps each GameAssembly.dll with DONT_RESOLVE_DLL_REFERENCES (no
DllMain, no imports) and overwrites its executable sections with the on-disk
bytes, so the two processes scan identical code wherever the loader placed
the module; all printed addresses are module- or region-relative.

WHAT THIS PROVES: for these inputs, the two kernels are indistinguishable
through IL2CPPFieldXrefScan.h (wrapper + Rust port vs legacy), including the
read-failure paths (synthetic unreadable pages). WHAT IT DOES NOT: inputs
outside the corpus; read failures inside real images (mapped images are fully
readable); x86 (the kernels are x64-only; i686 is covered by `cargo test
--target i686-pc-windows-msvc`); the Rust panic fallbacks; the caller
IL2CPPFieldXref.cpp (kind filter, method attribution, limit); and live
in-game behaviour -- that needs an in-game smoke.

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
import time
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
RUST_EXE = "xref-dump.exe"
LEGACY_EXE = "xref-dump-legacy.exe"
# Present only when the Rust kernels are linked: the panic-guard label of the
# instance-scan C ABI entry point (dll/rust/src). Override with --rust-marker
# if the port names it differently.
DEFAULT_RUST_MARKER = "dc_xref_inst_scan"
GENERATED_CMDS = ["rip-synthetic", "inst-synthetic"]
IMAGE_CMDS = ["rip", "inst"]
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


def first_diff_lines(a: Path, b: Path) -> tuple[int, bytes, bytes] | None:
    with a.open("rb") as fa, b.open("rb") as fb:
        n = 0
        while True:
            la, lb = fa.readline(), fb.readline()
            n += 1
            if la == lb:
                if not la:
                    return None
                continue
            return n, la, lb


def describe_diff(n: int, la: bytes, lb: bytes) -> str:
    """First differing line, with a window around the first differing byte --
    rip lines carry whole candidate lists and run to hundreds of KB."""
    if not la or not lb:
        return f"line {n}: {'rust' if not la else 'legacy'} output ends early"
    col = next((i for i, (x, y) in enumerate(zip(la, lb)) if x != y), min(len(la), len(lb)))
    lo = max(0, col - 100)
    return (f"line {n}, byte {col}:\n"
            f"      line head: {la[:160]!r}\n"
            f"      rust:   ...{la[lo:col + 200]!r}\n"
            f"      legacy: ...{lb[lo:col + 200]!r}")


def drill_inst(la: bytes, lb: bytes, image: str, rust_exe: Path, legacy_exe: Path, work: Path) -> str:
    """For a differing `inst` block record, re-run `inst-hits` on that block in
    both binaries and report the first differing hit line."""
    try:
        ra, rb = json.loads(la), json.loads(lb)
    except ValueError:
        return ""
    rec = ra if ra.get("rec") == "ib" else rb if rb.get("rec") == "ib" else None
    if rec is None:
        return ""
    blk = rec["blk"]
    if ra.get("rec") == "ib" and rb.get("rec") == "ib":
        blk = min(ra["blk"], rb["blk"])
    args = ["inst-hits", image, rec["run"], rec["fo"], str(blk), "1"]
    r = run_cli(rust_exe, args, work / "rust.drill")
    l = run_cli(legacy_exe, args, work / "legacy.drill")
    head = f"\n    drill-down `{' '.join(args[:1] + ['<image>'] + args[2:])}`:"
    # Skip the totals line (printed before the hit lines): show the first hit
    # group that differs.
    ra_lines = r.out.read_bytes().split(b"\n")
    rb_lines = l.out.read_bytes().split(b"\n")
    summary = b'{"rec":"inst",'
    for n in range(max(len(ra_lines), len(rb_lines))):
        x = ra_lines[n] if n < len(ra_lines) else b""
        y = rb_lines[n] if n < len(rb_lines) else b""
        if x != y and not (x.startswith(summary) and y.startswith(summary)):
            return head + "\n    " + describe_diff(n + 1, x + b"\n" if x else b"", y + b"\n" if y else b"")
    if ra_lines != rb_lines:
        return head + " only the totals line differs"
    return head + f" outputs identical (exit rust={r.code} legacy={l.code})"


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
        d = first_diff_lines(r.out, l.out)
        msg = "stdout differs at " + (describe_diff(*d) if d else "<unknown>")
        if d and task.args[0] == "inst" and len(task.args) == 2:
            msg += drill_inst(d[1], d[2], task.args[1], rust_exe, legacy_exe, work)
        problems.append(msg)
    res.failures = problems
    res.size = l.out.stat().st_size
    res.lines = count_lines(l.out)
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
                    help="directory holding xref-dump.exe and xref-dump-legacy.exe")
    ap.add_argument("--keep", metavar="DIR", help="keep every captured output under DIR")
    ap.add_argument("-j", "--jobs", type=int, default=max(1, min(4, os.cpu_count() or 1)))
    ap.add_argument("--no-images", action="store_true",
                    help="run only the synthetic subcommands (no GameAssembly.dll needed)")
    ap.add_argument("--only", nargs="+", choices=GENERATED_CMDS + IMAGE_CMDS, metavar="CMD",
                    help=f"restrict to these subcommands ({', '.join(GENERATED_CMDS + IMAGE_CMDS)})")
    ap.add_argument("--rust-marker", default=DEFAULT_RUST_MARKER,
                    help="string only the Rust flavour contains (default: %(default)s)")
    args = ap.parse_args()

    cli_dir = Path(args.cli_dir)
    rust_exe, legacy_exe = cli_dir / RUST_EXE, cli_dir / LEGACY_EXE
    for exe in (rust_exe, legacy_exe):
        if not exe.exists():
            print(f"missing {exe} -- run `build.cmd cli` first", file=sys.stderr)
            return 2
    # Guard against a vacuous PASS: if both binaries carried the same
    # implementation, every diff would be empty.
    marker = args.rust_marker.encode("ascii")
    if not marker:
        print("--rust-marker must not be empty", file=sys.stderr)
        return 2
    if marker not in rust_exe.read_bytes():
        print(f"{rust_exe} does not contain the Rust xref kernels (marker {marker!r} missing)", file=sys.stderr)
        return 2
    if marker in legacy_exe.read_bytes():
        print(f"{legacy_exe} unexpectedly contains the Rust xref kernels (marker {marker!r})", file=sys.stderr)
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
            fixtures = load_fixtures()
            images, notes = derive_images(fixtures)
            for n in notes:
                print(f"note: {n}")
        seen, unique = set(), []
        for p in images:
            key = os.path.normcase(str(p.resolve()))
            if key not in seen:
                seen.add(key)
                unique.append(p.resolve())
        images = unique
        if not images:
            print("no GameAssembly.dll: pass paths, set DISCRETE_METADATA_FIXTURES or "
                  "tools/verify/fixtures.local.txt, "
                  "or use --no-images for the synthetic cases only", file=sys.stderr)
            return 2

    tasks: list[Task] = [Task(c, [c]) for c in GENERATED_CMDS if c in selected]
    for img in images:
        for c in IMAGE_CMDS:
            if c in selected:
                tasks.append(Task(f"{c} {img}", [c, str(img)]))
    # Guard against a vacuous PASS from contradictory flags (e.g. --no-images
    # --only inst): the error-path checks below exercise no kernel code.
    if not tasks:
        print("nothing to compare: the selected subcommands need a GameAssembly.dll "
              "but --no-images was given", file=sys.stderr)
        return 2
    # Error paths: the driver itself must fail identically in both flavours.
    tasks.append(Task("error: usage", ["no-such-subcommand"], must_succeed=False, expect_code=1))
    tasks.append(Task("error: inst-hits bad fo", ["inst-hits", "x.dll", "all", "zz", "0", "1"],
                      must_succeed=False, expect_code=1))
    if images:
        tasks.append(Task("error: unmappable image",
                          ["rip", str(images[0].parent / "no-such-dir" / "GameAssembly.dll")],
                          must_succeed=False, expect_code=2))

    root_ctx = tempfile.TemporaryDirectory(prefix="xref_rust_diff_") if not args.keep else None
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
