#!/usr/bin/env python3
"""Phase R1 regression gate: metadata-cli output must match the recorded golden hashes.

Until R1 was closed, tools/verify/metadata_rust_diff.py ran every check below
through two binaries -- metadata-cli.exe (Rust parser, dll/rust/src/metadata)
and metadata-cli-legacy.exe (the legacy C++ MetadataParser.cpp) -- and required
byte-identical exit code, stdout and stderr (7 fixtures, 1589 checks, 0
differences; docs/rust-core.md "R1 gate"). The legacy parser was then deleted
(git history: last in 5748299), so this script
compares the Rust build against SHA-256 hashes of that proven output instead,
stored in metadata_golden.json next to this file.

    py -I tools/verify/metadata_golden.py                    # fixtures from the list below
    py -I tools/verify/metadata_golden.py <global-metadata.dat> [...]
    py -I tools/verify/metadata_golden.py --cli-dir build/cli-debug/bin -j 7
    py -I tools/verify/metadata_golden.py --record           # rewrite entries (see below)

Fixtures: DISCRETE_METADATA_FIXTURES (';'-separated global-metadata.dat paths)
or, when that is unset, the git-ignored tools/verify/fixtures.local.txt (one
path per line; template fixtures.example.txt). Build metadata-cli first with
`build.cmd cli` (build/cli-release/bin/).

Entries are keyed by the SHA-256 of the global-metadata.dat file plus the
GameAssembly.dll next to it (types / symbols read the binary too), and every
path the CLI echoes (metadata file, GameAssembly.dll, install root, output
prefix) is masked before hashing, so the same game build checks from any
install location and other builds report SKIP. Checks inside a fixture are
keyed by position ("type[017]"), never by a game's class / method / field
names; each entry carries only the game's folder name as a hint (as
r2_golden.json does). A mismatch prints the real label from the local run.

Coverage, per fixture (as the differential had it):
  dump-parser          every public Parser method, plus out-of-range probes
  info / stats / types (with and without GameAssembly enrichment) /
  methods / images     the user-facing JSON commands
  type <ns> <name>     well-known types + a deterministic sample of types
  method <ns> <t> <m>  a deterministic sample of methods (parameters)
  symbols              the D-5 symbol pipeline: console output (output prefix
                       masked) and its three output files

--record overwrites the entries of every fixture it runs. Only record from a
build whose output you have independently proven correct -- a changed hash
means the behaviour changed, and recording it silently blesses the change.
--exe checks another binary against the recorded hashes (e.g. a checkout's
legacy build) and skips the Rust-marker guard.

Exit code: 0 = every checked fixture matches, 1 = mismatch, 2 = setup error or
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
GOLDEN = HERE / "metadata_golden.json"
EXE = "metadata-cli.exe"
# Present only when the Rust parser is linked: the panic-guard label of the
# Rust load entry point (dll/rust/src/metadata/ffi.rs).
RUST_MARKER = b"dc_md_load_path_w"
TYPE_SAMPLES = 150
METHOD_SAMPLES = 60
WELL_KNOWN_TYPES = [
    ("System", "Int32"), ("System", "String"), ("System", "Single"),
    ("UnityEngine", "Vector3"), ("UnityEngine", "GameObject"), ("UnityEngine", "Mathf"),
]
SYMBOL_EXTS = (".dd64", ".ghidra.txt", ".idc")


@dataclass
class Check:
    key: str           # stored in the JSON: position-based, no game names
    label: str         # printed locally on a mismatch
    digest: str


@dataclass
class Result:
    fixture: str
    key: str = ""
    hint: str = ""
    checks: list[Check] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)
    status: str = "PASS"            # PASS / FAIL / SKIP / REC
    notes: list[str] = field(default_factory=list)
    seconds: float = 0.0


def file_sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def run_digest(code: int, out: bytes, err: bytes) -> str:
    """One hash over exit code, stdout and stderr (the differential compared all three)."""
    h = hashlib.sha256()
    h.update(b"code=%d\n" % code)
    h.update(b"out=%d\n" % len(out))
    h.update(out)
    h.update(b"err=%d\n" % len(err))
    h.update(err)
    return h.hexdigest()


def sample_evenly(items: list, n: int) -> list:
    if len(items) <= n:
        return items
    step = len(items) / n
    return [items[int(i * step)] for i in range(n)]


def json_lines(data: bytes) -> list[dict]:
    rows = []
    for line in data.splitlines():
        line = line.strip()
        if line:
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                pass  # non-JSON noise is still covered by the hash
    return rows


def fixture_key(fixture: Path) -> tuple[str, str]:
    """(key, hint): metadata hash + GameAssembly.dll hash ('none' if absent)."""
    md = file_sha256(fixture)
    ga = "none"
    parts = [x.lower() for x in fixture.parts]
    if (len(parts) >= 5 and parts[-1] == "global-metadata.dat" and parts[-2] == "metadata"
            and parts[-3] == "il2cpp_data" and parts[-4].endswith("_data")):
        dll = fixture.parents[3] / "GameAssembly.dll"
        if dll.is_file():
            ga = file_sha256(dll)
        hint = fixture.parents[3].name
    else:
        hint = fixture.parent.name
    return f"{md}+{ga}", hint


def path_masks(fixture: Path, prefix: Path) -> list[tuple[bytes, bytes]]:
    """Byte patterns -> placeholder for every machine-specific path the CLI can
    echo: `types` prints the GameAssembly.dll it read, `symbols` the binary,
    the metadata file, the install root and its output prefix. Without this the
    hashes would bind the golden to one install location. Longest first, each
    raw, with forward slashes and JSON-escaped, in UTF-8 and the ANSI code page."""
    paths = [(str(prefix), b"<prefix>"), (str(fixture), b"<metadata>")]
    if len(fixture.parents) > 3:
        root = fixture.parents[3]
        paths += [(str(root / "GameAssembly.dll"), b"<gameassembly>"), (str(root), b"<root>")]
    # The CLI canonicalises what it opens (a junction or symlink prints its
    # target), so mask the real paths too.
    paths += [(os.path.realpath(text), token) for text, token in paths]
    masks: list[tuple[bytes, bytes]] = []
    for text, token in paths:
        for form in (text.replace("\\", "\\\\"), text, text.replace("\\", "/")):
            for enc in ("utf-8", "mbcs"):
                try:
                    b = form.encode(enc)
                except (UnicodeEncodeError, LookupError):
                    continue
                if (b, token) not in masks:
                    masks.append((b, token))
    masks.sort(key=lambda m: len(m[0]), reverse=True)
    return masks


def apply_masks(data: bytes, masks: list[tuple[bytes, bytes]]) -> bytes:
    for pat, token in masks:
        data = data.replace(pat, token)
    return data


def run_fixture(fixture: Path, exe: Path, work: Path) -> Result:
    res = Result(str(fixture))
    t0 = time.perf_counter()
    res.key, res.hint = fixture_key(fixture)
    spec = str(fixture)
    prefix = work / "sym"
    masks = path_masks(fixture, prefix)

    def run(key: str, label: str, args: list[str], must_succeed: bool = False) -> bytes:
        p = subprocess.run([str(exe), *args], stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        # Identical failures would otherwise "pass" without exercising anything.
        if must_succeed and p.returncode != 0:
            res.errors.append(f"{label}: expected success, exit code {p.returncode}: {p.stderr[:300]!r}")
        res.checks.append(Check(key, label, run_digest(p.returncode, apply_masks(p.stdout, masks),
                                                       apply_masks(p.stderr, masks))))
        return p.stdout

    run("dump-parser", "dump-parser", ["dump-parser", spec], must_succeed=True)
    run("info", "info", ["info", spec], must_succeed=True)
    run("stats", "stats", ["stats", spec])
    run("types-no-binary", "types --no-binary", ["--no-binary", "types", spec])
    types_out = run("types", "types", ["types", spec])
    methods_out = run("methods", "methods", ["methods", spec])
    run("images", "images", ["images", spec])

    types = [(t.get("namespace", ""), t.get("name", ""))
             for t in json_lines(types_out) if t.get("field_count", 0) > 0]
    for i, (ns, name) in enumerate(WELL_KNOWN_TYPES + sample_evenly(types, TYPE_SAMPLES)):
        run(f"type[{i:03d}]", f"type {ns}.{name}", ["--no-binary", "type", spec, ns, name])

    methods = [(m.get("declaring_namespace", ""), m.get("declaring_type", ""), m.get("name", ""))
               for m in json_lines(methods_out) if m.get("parameter_count", 0) > 0]
    for i, (ns, ty, mn) in enumerate(sample_evenly(methods, METHOD_SAMPLES)):
        run(f"method[{i:03d}]", f"method {ns}.{ty}::{mn}", ["method", spec, ns, ty, mn])

    # symbols writes <prefix>.dd64 / .ghidra.txt / .idc; its console output
    # echoes the prefix (a temp path) and the game paths, masked like the rest.
    for ext in SYMBOL_EXTS:
        Path(str(prefix) + ext).unlink(missing_ok=True)
    p = subprocess.run([str(exe), "symbols", spec, str(prefix)],
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    if p.returncode != 0:
        res.errors.append(f"symbols: expected success, exit code {p.returncode}: {p.stderr[:300]!r}")
    res.checks.append(Check("symbols-console", "symbols (console)",
                            run_digest(p.returncode, apply_masks(p.stdout, masks),
                                       apply_masks(p.stderr, masks))))
    for ext in SYMBOL_EXTS:
        out = Path(str(prefix) + ext)
        if not out.exists():
            res.errors.append(f"symbols {ext}: missing output")
            continue
        res.checks.append(Check(f"symbols{ext}", f"symbols {ext}", file_sha256(out)))
    res.seconds = time.perf_counter() - t0
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


def judge(res: Result, golden: dict) -> None:
    if res.errors:
        res.status = "FAIL"
        res.notes.extend(res.errors)
        return
    want = golden.get(res.key)
    if want is None:
        res.status = "SKIP"
        res.notes.append("no golden entry for this fixture (different game build?)")
        return
    have = {c.key: c for c in res.checks}
    want_checks = want.get("checks", {})
    for k in sorted(set(have) | set(want_checks)):
        if k not in want_checks:
            res.notes.append(f"{k} ({have[k].label}): not in the golden entry")
        elif k not in have:
            res.notes.append(f"{k}: in the golden entry but not produced by this run")
        elif have[k].digest != want_checks[k]:
            res.notes.append(f"{k} ({have[k].label}): sha256 {have[k].digest[:16]}.. != golden "
                             f"{want_checks[k][:16]}..")
    if res.notes:
        res.status = "FAIL"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("fixtures", nargs="*", help="global-metadata.dat paths (default: the fixture list)")
    ap.add_argument("--cli-dir", default=str(REPO / "build" / "cli-release" / "bin"),
                    help="directory holding metadata-cli.exe")
    ap.add_argument("--exe", help="check this binary instead (skips the Rust-marker guard; not with --record)")
    ap.add_argument("-j", "--jobs", type=int, default=max(1, min(4, os.cpu_count() or 1)))
    ap.add_argument("--record", action="store_true",
                    help="add missing fixture entries into metadata_golden.json (refuses to overwrite changed entries without --rebaseline)")
    ap.add_argument("--rebaseline", action="store_true",
                    help="with --record: explicitly allow overwriting existing changed golden entries and print the diff")
    args = ap.parse_args()

    if args.rebaseline and not args.record:
        print("--rebaseline requires --record", file=sys.stderr)
        return 2
    if args.exe and args.record:
        print("--exe cannot be combined with --record", file=sys.stderr)
        return 2
    exe = Path(args.exe) if args.exe else Path(args.cli_dir) / EXE
    if not exe.exists():
        print(f"missing {exe} -- run `build.cmd cli` first", file=sys.stderr)
        return 2
    if not args.exe and RUST_MARKER not in exe.read_bytes():
        print(f"{exe} does not contain the Rust parser (marker {RUST_MARKER!r} missing)", file=sys.stderr)
        return 2
    fixtures = args.fixtures or load_fixtures()
    if not fixtures:
        print("no fixtures: pass global-metadata.dat paths, set DISCRETE_METADATA_FIXTURES, "
              "or list them in tools/verify/fixtures.local.txt", file=sys.stderr)
        return 2
    missing = [f for f in fixtures if not Path(f).is_file()]
    if missing:
        print("missing fixture(s):\n  " + "\n  ".join(missing), file=sys.stderr)
        return 2
    seen, unique = set(), []
    for f in fixtures:
        k = os.path.normcase(str(Path(f).absolute()))
        if k not in seen:
            seen.add(k)
            unique.append(Path(f).absolute())

    golden_doc = json.loads(GOLDEN.read_text(encoding="utf-8")) if GOLDEN.exists() else {"entries": {}}
    golden_doc.setdefault("about", (
        "SHA-256 per metadata-cli check, recorded from the Rust parser after metadata_rust_diff.py "
        "proved it byte-identical to the legacy C++ MetadataParser.cpp (legacy last in 5748299). "
        "Entries are keyed by sha256(global-metadata.dat)+sha256(GameAssembly.dll); checks by "
        "position, never by game names. Maintained by tools/verify/metadata_golden.py --record."))
    golden = golden_doc.setdefault("entries", {})
    if not golden and not args.record:
        print(f"{GOLDEN} has no entries -- nothing to compare against", file=sys.stderr)
        return 2

    t0 = time.perf_counter()
    with tempfile.TemporaryDirectory(prefix="metadata_golden_") as root:
        jobs = {}
        with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
            for i, f in enumerate(unique):
                work = Path(root) / f"fixture{i:02d}"
                work.mkdir()
                jobs[pool.submit(run_fixture, f, exe, work)] = f
            results = [fut.result() for fut in concurrent.futures.as_completed(jobs)]
    results.sort(key=lambda r: r.fixture)

    for r in results:
        if args.record:
            r.status = "FAIL" if r.errors else "REC"
            r.notes.extend(r.errors)
        else:
            judge(r, golden)
    fails = sum(r.status == "FAIL" for r in results)
    for r in results:
        print(f"[{r.status}] {len(r.checks):4d} checks {r.seconds:6.1f}s  {r.fixture}")
        for n in r.notes[:20]:
            print(f"    - {n}")
        if len(r.notes) > 20:
            print(f"    ... {len(r.notes) - 20} more")

    if args.record:
        if fails:
            print("\nnot recorded: fix the failing fixture(s) first", file=sys.stderr)
            return 1
        added, rebaselined, unchanged = 0, 0, 0
        blocked: list[str] = []
        for r in results:
            new_checks = {c.key: c.digest for c in r.checks}
            old_entry = golden.get(r.key)
            if old_entry is None:
                golden[r.key] = {"fixture": r.hint, "checks": new_checks}
                added += 1
                continue
            old_checks = old_entry.get("checks", {})
            diffs: list[str] = []
            label_by_key = {c.key: c.label for c in r.checks}
            for k in sorted(set(old_checks) | set(new_checks)):
                if k not in old_checks:
                    diffs.append(f"  + {k} ({label_by_key.get(k, k)}): added {new_checks[k][:16]}..")
                elif k not in new_checks:
                    diffs.append(f"  - {k}: removed (was {old_checks[k][:16]}..)")
                elif old_checks[k] != new_checks[k]:
                    diffs.append(f"  ~ {k} ({label_by_key.get(k, k)}): {old_checks[k][:16]}.. -> {new_checks[k][:16]}..")
            if not diffs:
                unchanged += 1
                continue
            if not args.rebaseline:
                blocked.append(f"{r.fixture} ({r.key[:16]}..): {len(diffs)} check(s) changed:\n" + "\n".join(diffs[:20]))
                continue
            print(f"\n[REBASELINE] {r.fixture} ({len(diffs)} check(s) changed):")
            for d in diffs[:20]:
                print(d)
            if len(diffs) > 20:
                print(f"  ... {len(diffs) - 20} more")
            golden[r.key] = {"fixture": r.hint, "checks": new_checks}
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

    compared = sum(r.status == "PASS" for r in results)
    skips = sum(r.status == "SKIP" for r in results)
    checks = sum(len(r.checks) for r in results if r.status == "PASS")
    print(f"\n{len(results)} fixture(s): {compared} matched golden ({checks} checks), {skips} skipped, "
          f"{fails} failed, {time.perf_counter() - t0:.1f}s")
    if fails:
        return 1
    if compared == 0:
        print("nothing was compared against a golden hash", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
