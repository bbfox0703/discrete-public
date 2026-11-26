#!/usr/bin/env python3
"""Regression tests for tools/check_no_local_paths.py (A-M12 ReDoS fix)."""

import importlib.util
import pathlib
import sys
import time


def load_checker():
    repo_root = pathlib.Path(__file__).resolve().parents[2]
    checker_path = repo_root / "tools" / "check_no_local_paths.py"
    spec = importlib.util.spec_from_file_location("check_no_local_paths", checker_path)
    assert spec is not None and spec.loader is not None
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def main() -> int:
    mod = load_checker()
    pat = mod.SESSION_DIR_PATTERN

    # Build synthetic path fixtures dynamically so this test file itself does
    # not trip check_no_local_paths.py's tracked-file scan.
    seg_dir = "." + "claude"
    seg_proj = "projects"

    positives = [
        f"C:/Users/user/{seg_dir}/{seg_proj}/my-proj/memory/note.md",
        f"D:\\Users\\someone\\{seg_dir}\\{seg_proj}\\proj\\file.md",
        f"C:\\{seg_dir}\\{seg_proj}\\root",
        f"e:/nested/a/b/c/{seg_dir}/{seg_proj}/x",
    ]
    for sample in positives:
        assert pat.search(sample) is not None, f"expected match on: {sample!r}"

    negatives = [
        f"%USERPROFILE%\\{seg_dir}\\{seg_proj}\\my-proj",
        f"$HOME/{seg_dir}/{seg_proj}/my-proj",
        "docs/plan.md",
    ]
    for sample in negatives:
        assert pat.search(sample) is None, f"unexpected match on: {sample!r}"

    # A-M12 ReDoS regression: long non-matching paths with many separators must
    # complete in linear time (< 0.1s), not exponential backtracking.
    evil_samples = [
        "C:/" + "a/" * 50 + "x",
        "D:\\" + "dir\\" * 50 + "file.txt",
        "C:/" + ("seg/" * 40) + f"{seg_dir}/notprojects",
    ]
    for sample in evil_samples:
        t0 = time.perf_counter()
        m = pat.search(sample)
        elapsed = time.perf_counter() - t0
        assert m is None, f"unexpected match on evil sample: {sample!r}"
        assert elapsed < 0.1, f"ReDoS regression: {elapsed:.3f}s on {sample[:30]!r}..."

    print(f"PASS: {len(positives)} positive, {len(negatives)} negative, "
          f"{len(evil_samples)} ReDoS checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
