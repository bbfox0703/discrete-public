#!/usr/bin/env python3
"""Phase R2 regression gate: r2-dump / xref-dump output must match the recorded golden hashes.

Until R2 was closed, tools/verify/r2_rust_diff.py ran every r2-dump subcommand
through two binaries -- the Rust ports and the legacy C++ (AobPattern.cpp,
GroupMatch.h, ValueRefine.cpp, IL2CPPChainDetector.cpp + Zydis) -- and
required byte-identical output (19/19 identical on Release and Debug, then a
live A/B; docs/rust-core.md "R2 gate"). The legacy sources were then deleted,
so this script compares the Rust build against SHA-256 hashes of that proven
output instead, stored in r2_golden.json next to this file. The IL2CPPFieldXref
scan kernels followed the same path: xref-dump (tools/r2-diff/xref_dump.cpp)
was proven byte-identical to the legacy Zydis kernels by
tools/verify/xref_rust_diff.py before those were deleted (both last in 987b20d).

  * r2-dump aob-synthetic / gm / refine and xref-dump rip-synthetic /
    inst-synthetic use fixed seeds and no input file, so their hashes hold on
    every machine.
  * r2-dump aob / chain and xref-dump rip / inst over a GameAssembly.dll are
    keyed by the SHA-256 of the image file itself: the same game build checks
    anywhere; an image without a recorded entry is reported as SKIP (not a
    failure).

    py -I tools/verify/r2_golden.py                    # images from DISCRETE_METADATA_FIXTURES
    py -I tools/verify/r2_golden.py <GameAssembly.dll> [...]
    py -I tools/verify/r2_golden.py --no-images        # generated cases only
    py -I tools/verify/r2_golden.py --cli-dir build/cli-debug/bin -j 7
    py -I tools/verify/r2_golden.py --record           # rewrite entries (see below)

--record overwrites the entries for everything it runs. Only record from a
build whose output you have independently proven correct -- a changed hash
means the behaviour changed, and recording it silently blesses the change.
The legacy implementations are in git history (R2 modules last present in
aae48d3; the xref kernels, IL2CPPFieldXrefScan.cpp, in 987b20d)
if a behaviour change ever needs re-proving against them. Decoder-level
changes are re-proven against Zydis with tools/zydis-oracle.

Build both tools first with `build.cmd cli` (build/cli-release/bin/). Images
default to <root>/GameAssembly.dll for every <root>/<X>_Data/il2cpp_data/
Metadata/global-metadata.dat in DISCRETE_METADATA_FIXTURES (';'-separated) or,
when that is unset, the git-ignored tools/verify/fixtures.local.txt (one path
per line; template fixtures.example.txt) -- the same list the R1 harness uses. Every run must exit 0 and end with the
{"rec":"end"} record; the usage error and an unmappable image must fail with
exit codes 1 and 2. Subcommand coverage is described in tools/r2-diff/r2_dump.cpp
and tools/r2-diff/xref_dump.cpp.

Exit code: 0 = all checked runs match, 1 = mismatch, 2 = setup error or
nothing was actually compared.
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

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
GOLDEN = HERE / "r2_golden.json"


@dataclass(frozen=True)
class Tool:
    exe: str
    # Panic-guard label of one of the tool's Rust C ABI entry points: proves
    # the binary carries the Rust ports.
    marker: bytes
    generated: tuple[str, ...]
    image: tuple[str, ...]


TOOLS = (
    Tool("r2-dump.exe", b"dc_chain_detect", ("aob-synthetic", "gm", "refine"), ("aob", "chain")),
    Tool("xref-dump.exe", b"dc_xref_inst_scan", ("rip-synthetic", "inst-synthetic"), ("rip", "inst")),
)
GENERATED_CMDS = [c for t in TOOLS for c in t.generated]
IMAGE_CMDS = [c for t in TOOLS for c in t.image]
TOOL_OF = {c: t for t in TOOLS for c in (*t.generated, *t.image)}
END_RECORD = b'{"rec":"end"}\n'


@dataclass
class Task:
    label: str
    tool: Tool
    args: list[str]
    key: str | None = None           # golden key; None for the error paths
    hint: str = ""                   # human hint stored next to an image key
    expect_code: int = 0


@dataclass
class Result:
    task: Task
    status: str = "PASS"             # PASS / FAIL / SKIP / REC
    notes: list[str] = field(default_factory=list)
    digest: str = ""
    size: int = 0
    lines: int = 0
    seconds: float = 0.0


def file_sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def tail(path: Path) -> bytes:
    size = path.stat().st_size
    with path.open("rb") as fh:
        fh.seek(max(0, size - 256))
        t = fh.read()
    nl = t.rfind(b"\n", 0, len(t) - 1)
    return t[nl + 1:]


def run_task(task: Task, cli_dir: Path, work: Path, golden: dict, record: bool) -> Result:
    res = Result(task)
    exe = cli_dir / task.tool.exe
    out = work / "out.bin"
    t0 = time.perf_counter()
    with out.open("wb") as fh:
        p = subprocess.run([str(exe), *task.args], stdout=fh, stderr=subprocess.PIPE, check=False)
    res.seconds = time.perf_counter() - t0
    res.size = out.stat().st_size
    if p.returncode != task.expect_code:
        res.status = "FAIL"
        res.notes.append(f"exit code {p.returncode}, expected {task.expect_code}: {p.stderr[:300]!r}")
        return res
    if task.key is None:
        return res  # error-path check: the exit code is the assertion
    if p.stderr:
        res.notes.append(f"unexpected stderr: {p.stderr[:300]!r}")
        res.status = "FAIL"
        return res
    if tail(out) != END_RECORD:
        res.status = "FAIL"
        res.notes.append("stdout does not end with the end record (truncated run?)")
        return res
    res.digest = file_sha256(out)
    with out.open("rb") as fh:
        res.lines = sum(chunk.count(b"\n") for chunk in iter(lambda: fh.read(1 << 20), b""))
    if record:
        res.status = "REC"
        return res
    want = golden.get(task.key)
    if want is None:
        if "@" not in task.key:
            # Generated checks use fixed seeds and no input file: their entry
            # must exist on every machine, so a missing one is a broken
            # r2_golden.json, never a legitimate skip.
            res.status = "FAIL"
            res.notes.append("golden entry missing for a generated check -- r2_golden.json is "
                             "incomplete (re-record only from a proven build)")
        else:
            res.status = "SKIP"
            res.notes.append("no golden entry for this input (different game build?)")
    elif want["sha256"] != res.digest:
        res.status = "FAIL"
        res.notes.append(f"sha256 {res.digest[:16]}.. != golden {want['sha256'][:16]}.. "
                         f"(size {res.size} vs {want.get('size')}, lines {res.lines} vs {want.get('lines')})")
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
                    help="directory holding r2-dump.exe and xref-dump.exe")
    ap.add_argument("-j", "--jobs", type=int, default=max(1, min(4, os.cpu_count() or 1)))
    ap.add_argument("--no-images", action="store_true", help="run only the generated subcommands")
    ap.add_argument("--only", nargs="+", choices=GENERATED_CMDS + IMAGE_CMDS, metavar="CMD",
                    help=f"restrict to these subcommands ({', '.join(GENERATED_CMDS + IMAGE_CMDS)})")
    ap.add_argument("--record", action="store_true",
                    help="add missing entries into r2_golden.json (refuses to overwrite changed entries without --rebaseline)")
    ap.add_argument("--rebaseline", action="store_true",
                    help="with --record: explicitly allow overwriting existing changed golden entries and print the diff")
    args = ap.parse_args()

    if args.rebaseline and not args.record:
        print("--rebaseline requires --record", file=sys.stderr)
        return 2

    cli_dir = Path(args.cli_dir)
    selected = set(args.only or GENERATED_CMDS + IMAGE_CMDS)
    for tool in TOOLS:
        if not selected & {*tool.generated, *tool.image}:
            continue
        exe = cli_dir / tool.exe
        if not exe.exists():
            print(f"missing {exe} -- run `build.cmd cli` first", file=sys.stderr)
            return 2
        if tool.marker not in exe.read_bytes():
            print(f"{exe} does not contain the Rust ports (marker {tool.marker!r} missing)", file=sys.stderr)
            return 2
    golden_doc = json.loads(GOLDEN.read_text(encoding="utf-8")) if GOLDEN.exists() else {"entries": {}}
    golden = golden_doc.setdefault("entries", {})
    if not golden and not args.record:
        print(f"{GOLDEN} has no entries -- nothing to compare against", file=sys.stderr)
        return 2

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
        # r2-dump finds the mapped module by GetModuleHandleW(L"GameAssembly.dll");
        # xref-dump takes any image, but both run over the same list.
        bad = [str(p) for p in images if p.name.lower() != "gameassembly.dll"]
        if bad:
            print("image(s) must be named GameAssembly.dll:\n  " + "\n  ".join(bad), file=sys.stderr)
            return 2
        seen, unique = set(), []
        for p in images:
            k = os.path.normcase(str(p.resolve()))
            if k not in seen:
                seen.add(k)
                unique.append(p.resolve())
        images = unique
        if not images:
            print("no GameAssembly.dll: pass paths, set DISCRETE_METADATA_FIXTURES or "
                  "tools/verify/fixtures.local.txt, "
                  "or use --no-images for the generated cases only", file=sys.stderr)
            return 2

    tasks: list[Task] = [Task(c, TOOL_OF[c], [c], key=c) for c in GENERATED_CMDS if c in selected]
    for img in images:
        img_sha = file_sha256(img)
        for c in IMAGE_CMDS:
            if c in selected:
                tasks.append(Task(f"{c} {img}", TOOL_OF[c], [c, str(img)], key=f"{c}@{img_sha}",
                                  hint=img.parent.name))
    if not tasks:
        print("nothing to run: the selected subcommands need a GameAssembly.dll "
              "but --no-images was given", file=sys.stderr)
        return 2
    for tool in TOOLS:
        if not selected & {*tool.generated, *tool.image}:
            continue
        name = tool.exe.removesuffix(".exe")
        tasks.append(Task(f"error: {name} usage", tool, ["no-such-subcommand"], expect_code=1))
        if images and selected & set(tool.image):
            tasks.append(Task(f"error: {name} unmappable image", tool,
                              [tool.image[-1], str(images[0].parent / "no-such-dir" / "GameAssembly.dll")],
                              expect_code=2))

    t0 = time.perf_counter()
    with tempfile.TemporaryDirectory(prefix="r2_golden_") as root:
        jobs = {}
        with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
            for i, task in enumerate(tasks):
                work = Path(root) / f"task{i:03d}"
                work.mkdir()
                jobs[pool.submit(run_task, task, cli_dir, work, golden, args.record)] = task
            results = [f.result() for f in concurrent.futures.as_completed(jobs)]

    order = {t.label: i for i, t in enumerate(tasks)}
    results.sort(key=lambda r: order[r.task.label])
    fails = skips = compared = 0
    for r in results:
        print(f"[{r.status}] {r.lines:8d} lines {r.size / 1e6:8.1f} MB {r.seconds:7.1f}s  {r.task.label}")
        for n in r.notes:
            print(f"    - {n}")
        fails += r.status == "FAIL"
        skips += r.status == "SKIP"
        compared += r.status == "PASS" and r.task.key is not None

    if args.record and not fails:
        added, rebaselined, unchanged = 0, 0, 0
        blocked: list[str] = []
        for r in results:
            if r.status != "REC":
                continue
            new_entry = {"sha256": r.digest, "size": r.size, "lines": r.lines,
                         **({"image": r.task.hint} if r.task.hint else {})}
            old_entry = golden.get(r.task.key)
            if old_entry is None:
                golden[r.task.key] = new_entry
                added += 1
                continue
            if old_entry.get("sha256") == r.digest:
                unchanged += 1
                continue
            diff_line = (f"  ~ {r.task.key} ({r.task.label}): "
                         f"{str(old_entry.get('sha256', ''))[:16]}.. -> {r.digest[:16]}.. "
                         f"(size {old_entry.get('size')} -> {r.size}, lines {old_entry.get('lines')} -> {r.lines})")
            if not args.rebaseline:
                blocked.append(diff_line)
                continue
            print(f"[REBASELINE]\n{diff_line}")
            golden[r.task.key] = new_entry
            rebaselined += 1
        if blocked:
            print("\nrefusing to overwrite existing golden entries without --rebaseline:", file=sys.stderr)
            for b in blocked:
                print(b, file=sys.stderr)
            return 1
        golden_doc["entries"] = dict(sorted(golden.items()))
        GOLDEN.write_text(json.dumps(golden_doc, indent=1) + "\n", encoding="utf-8", newline="\n")
        print(f"\nrecorded into {GOLDEN}: {added} added, {rebaselined} rebaselined, {unchanged} unchanged")
        return 0

    print(f"\n{len(results)} run(s): {compared} matched golden, {skips} skipped, {fails} failed, "
          f"{time.perf_counter() - t0:.1f}s")
    if fails:
        return 1
    if compared == 0:
        print("nothing was compared against a golden hash", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
