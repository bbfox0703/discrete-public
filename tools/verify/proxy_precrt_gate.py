#!/usr/bin/env python3
"""Prove a proxy DLL survives having an export called BEFORE its DllMain has run.

This is the offline stand-in for the failure documented in
docs/lessons-learned.md "Proxy DLLs - the loader can call our exports before we
exist".  There, Windows' AppCompat shim engine (apphelp!SE_DllLoaded ->
AcGenral) calls a proxy export after our module is MAPPED but before
_DllMainCRTStartup has run.  A lazy thunk then enters a resolver that allocates,
__acrt_heap is still NULL, and the process dies in ntdll!RtlAllocateHeap before
the EXE entry point.

LoadLibraryExW(..., DONT_RESOLVE_DLL_REFERENCES) reproduces that state without a
game, without a compat layer and without the shim engine: the image is mapped,
its exports are callable, and DllMain is NOT called.  Calling a forwarding thunk
in that state is exactly the window the pre-CRT gate closes.

    py tools/verify/proxy_precrt_gate.py --all
    py tools/verify/proxy_precrt_gate.py --all --baseline out/proxy-baseline
    py tools/verify/proxy_precrt_gate.py dist/winmm.dll timeBeginPeriod
    py tools/verify/proxy_precrt_gate.py --compare OLD.dll NEW.dll timeBeginPeriod
    py tools/verify/proxy_precrt_gate.py --forward dist/winmm.dll timeGetTime

WHAT THIS PROVES AND WHAT IT DOES NOT.

  PROVES: the thunk, entered before DllMain, RETURNS instead of transferring
  control into the resolver.  That is the property the gate adds, and it is
  checked here against a real mapped image and real machine code - not against
  source text.

  DOES NOT PROVE: that the *allocation* would have been the thing to fault.
  Under DONT_RESOLVE_DLL_REFERENCES the IAT is not snapped either, so an ungated
  binary faults on its first imported call (AcquireSRWLockShared, inside
  Proxy_LoadRealDll) rather than on HeapAlloc(NULL, ...).  Same place in OUR
  code, earlier instruction.  The negative control therefore shows "the ungated
  binary dies inside the resolver, the gated one never enters it" - which is the
  fix - and NOT a byte-for-byte replay of the game crash.  Only a game proves
  that.

  The gated path is import-free by construction: Proxy_CrtReady() reads a
  module global and Proxy_NotePreCrtCall() is an InterlockedIncrement intrinsic.
  So the rig genuinely discriminates for both of our flavours.

One export gives a POSITIVE signal on top of "did not fault": winmm's
timeBeginPeriod answers kTimeFail (97, TIMERR_NOCANDO) when the gate refuses.
The real function never answers 97 for a period of 1, and TIMERR_NOERROR is 0,
so 97 coming back is unambiguous proof that the gate fired rather than that
something happened to return a plausible zero.

Each load+call runs in a CHILD PROCESS so a fault is a reportable exit code
rather than the death of this script.  That is the whole reason for the --child
plumbing.

-- `--forward`: the complement --------------------------------------------

A NORMAL LoadLibraryW, so DllMain runs and the CRT comes up, and the export must
then actually FORWARD to the real System32 DLL.  This is what covers the risk
the gate introduces: if a thunk latched null on a refused first call, the export
would be permanently dead, and from the outside that is indistinguishable from
success because the forwarders answer a *documented failure value* rather than
crashing.  --forward calls with genuine arguments and checks the answer is the
real DLL's, not the refusal value.
"""

from __future__ import annotations

import argparse
import ctypes
import os
import struct
import subprocess
import sys
from ctypes import wintypes

DONT_RESOLVE_DLL_REFERENCES = 0x00000001

# Exit codes the child uses to report back to the parent.
EXIT_OK = 0
EXIT_LOAD_FAILED = 10
EXIT_NO_EXPORT = 11
EXIT_BAD_ARGS = 12


# --------------------------------------------------------------------------
# Probe table
# --------------------------------------------------------------------------
#
# `refusal` is what the gate makes the export answer.  `forward` describes how
# to call the export for real once DllMain has run: (args, predicate, label).
#
# Pre-CRT args are never dereferenced by our code in either the gated or the
# ungated build - the gate returns before touching them, and the ungated build
# faults inside the resolver before touching them - so zeros are safe there.
# --forward args must be genuine, because the REAL function receives them.

def _nonzero(v):
    return v != 0


def _is_zero(v):
    return v == 0


PROBES = {
    "version.dll": [
        {
            "name": "GetFileVersionInfoSizeW",
            "argtypes": [wintypes.LPCWSTR, ctypes.POINTER(wintypes.DWORD)],
            "restype": wintypes.DWORD,
            "precrt_args": (None, None),
            "refusal": 0,
            "refusal_is_distinctive": False,
            "forward": (
                lambda: (os.path.join(os.environ["SystemRoot"], "System32", "kernel32.dll"),
                         ctypes.byref(wintypes.DWORD(0))),
                _nonzero,
                "non-zero version-info size for kernel32.dll",
            ),
        },
        {
            "name": "VerLanguageNameW",
            "argtypes": [wintypes.DWORD, wintypes.LPWSTR, wintypes.DWORD],
            "restype": wintypes.DWORD,
            "precrt_args": (0, None, 0),
            "refusal": 0,
            "refusal_is_distinctive": False,
            "forward": (
                lambda: (0x0409, ctypes.create_unicode_buffer(128), 128),
                _nonzero,
                "non-zero char count for LANG 0x0409",
            ),
        },
    ],
    "winmm.dll": [
        {
            # THE discriminating probe.  kTimeFail == TIMERR_NOCANDO == 97, and
            # TIMERR_NOERROR == 0, so 97 can only have come from the gate.
            "name": "timeBeginPeriod",
            "argtypes": [wintypes.UINT],
            "restype": wintypes.UINT,
            "precrt_args": (1,),
            "refusal": 97,
            "refusal_is_distinctive": True,
            "forward": None,  # would change the system timer resolution; skipped
        },
        {
            "name": "timeGetTime",
            "argtypes": [],
            "restype": wintypes.DWORD,
            "precrt_args": (),
            "refusal": 0,
            "refusal_is_distinctive": False,
            "forward": (lambda: (), _nonzero, "a real tick count"),
        },
        {
            "name": "waveOutGetNumDevs",
            "argtypes": [],
            "restype": wintypes.UINT,
            "precrt_args": (),
            "refusal": 0,
            "refusal_is_distinctive": False,
            # A machine with no audio device genuinely answers 0, so this one
            # cannot assert non-zero.  It is here to exercise a second thunk
            # family, and its verdict is "did not fault".
            "forward": (lambda: (), lambda v: True, "a device count (0 is legal)"),
        },
    ],
}


def probes_for(dll_path: str):
    return PROBES.get(os.path.basename(dll_path).lower(), [])


def probe_by_name(dll_path: str, export: str):
    for p in probes_for(dll_path):
        if p["name"] == export:
            return p
    return None


# --------------------------------------------------------------------------
# PE bitness - refuse to pretend we tested something we could not load
# --------------------------------------------------------------------------

def pe_machine(path: str) -> int:
    with open(path, "rb") as f:
        data = f.read(0x400)
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    return struct.unpack_from("<H", data, e_lfanew + 4)[0]


IMAGE_FILE_MACHINE_I386 = 0x014C
IMAGE_FILE_MACHINE_AMD64 = 0x8664


def bitness_ok(path: str) -> tuple[bool, str]:
    machine = pe_machine(path)
    host64 = struct.calcsize("P") == 8
    if machine == IMAGE_FILE_MACHINE_AMD64 and not host64:
        return False, "x64 DLL, 32-bit Python"
    if machine == IMAGE_FILE_MACHINE_I386 and host64:
        return False, "x86 DLL, 64-bit Python - run this under a 32-bit interpreter"
    return True, ""


# --------------------------------------------------------------------------
# Child: map + call, in a process of its own
# --------------------------------------------------------------------------

def run_child(mode: str, dll: str, export: str) -> int:
    probe = probe_by_name(dll, export)
    if probe is None:
        sys.stderr.write("no probe entry for %s!%s\n" % (os.path.basename(dll), export))
        return EXIT_BAD_ARGS

    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.LoadLibraryExW.restype = wintypes.HMODULE
    k32.LoadLibraryExW.argtypes = [wintypes.LPCWSTR, wintypes.HANDLE, wintypes.DWORD]
    k32.LoadLibraryW.restype = wintypes.HMODULE
    k32.LoadLibraryW.argtypes = [wintypes.LPCWSTR]
    k32.GetProcAddress.restype = ctypes.c_void_p
    k32.GetProcAddress.argtypes = [wintypes.HMODULE, wintypes.LPCSTR]

    if mode == "precrt":
        h = k32.LoadLibraryExW(dll, None, DONT_RESOLVE_DLL_REFERENCES)
    else:
        h = k32.LoadLibraryW(dll)
    if not h:
        sys.stderr.write("load failed, GetLastError=%d\n" % ctypes.get_last_error())
        return EXIT_LOAD_FAILED

    addr = k32.GetProcAddress(wintypes.HMODULE(h), export.encode("ascii"))
    if not addr:
        sys.stderr.write("export not found\n")
        return EXIT_NO_EXPORT

    # WINFUNCTYPE, not CFUNCTYPE: these are stdcall on x86, where the CALLEE
    # pops.  Getting this wrong corrupts the stack on return rather than
    # failing loudly, which would make the rig lie.
    proto = ctypes.WINFUNCTYPE(probe["restype"], *probe["argtypes"])
    fn = proto(addr)

    if mode == "precrt":
        args = probe["precrt_args"]
    else:
        args = probe["forward"][0]()

    rc = fn(*args)
    # Report the return value through stdout; the exit code carries "did it
    # fault", which is the primary verdict.
    print("RET=%d" % (rc & 0xFFFFFFFF))
    return EXIT_OK


def call_in_child(mode: str, dll: str, export: str):
    """Returns (exit_code, ret_value_or_None, stderr_text)."""
    proc = subprocess.run(
        [sys.executable, os.path.abspath(__file__), "--child", mode, dll, export],
        capture_output=True, text=True,
    )
    ret = None
    for line in proc.stdout.splitlines():
        if line.startswith("RET="):
            ret = int(line[4:])
    return proc.returncode, ret, proc.stderr.strip()


def describe_exit(code: int) -> str:
    if code == EXIT_OK:
        return "RETURNED CLEANLY"
    if code == EXIT_LOAD_FAILED:
        return "could not map the image"
    if code == EXIT_NO_EXPORT:
        return "export not present"
    if code == EXIT_BAD_ARGS:
        return "harness error"
    # Windows surfaces an unhandled SEH exception as the NTSTATUS itself, which
    # Python reports as a signed 32-bit value.
    return "FAULTED 0x%08X" % (code & 0xFFFFFFFF)


def faulted(code: int) -> bool:
    return code not in (EXIT_OK, EXIT_LOAD_FAILED, EXIT_NO_EXPORT, EXIT_BAD_ARGS)


# --------------------------------------------------------------------------
# Modes
# --------------------------------------------------------------------------

def check_precrt(dll: str, export: str, indent: str = "  ") -> bool:
    probe = probe_by_name(dll, export)
    code, ret, err = call_in_child("precrt", dll, export)
    verdict = describe_exit(code)
    detail = "" if ret is None else " (returned %d)" % ret
    print("%s%-26s => %s%s" % (indent, export, verdict, detail))
    if err:
        print("%s%-26s    %s" % (indent, "", err))
    if code != EXIT_OK:
        return False
    if probe and probe["refusal_is_distinctive"]:
        if ret != probe["refusal"]:
            print("%s%-26s    !! expected the gate's refusal value %d, got %d"
                  % (indent, "", probe["refusal"], ret))
            return False
        print("%s%-26s    ++ %d is the gate's own refusal value - positive proof it fired"
              % (indent, "", ret))
    return True


def check_forward(dll: str, export: str, indent: str = "  ") -> bool:
    probe = probe_by_name(dll, export)
    if probe is None or probe["forward"] is None:
        print("%s%-26s => skipped (no safe forward call)" % (indent, export))
        return True
    _, predicate, label = probe["forward"]
    code, ret, err = call_in_child("forward", dll, export)
    if code != EXIT_OK:
        print("%s%-26s => %s" % (indent, export, describe_exit(code)))
        if err:
            print("%s%-26s    %s" % (indent, "", err))
        return False
    ok = predicate(ret)
    print("%s%-26s => returned %d  [%s] %s"
          % (indent, export, ret, label, "OK" if ok else "!! NOT the real DLL's answer"))
    return ok


def run_all(dist: str, baseline: str | None) -> int:
    failures = 0
    for dll_name in ("version.dll", "winmm.dll"):
        path = os.path.join(dist, dll_name)
        if not os.path.isfile(path):
            print("[SKIP] %s - not built" % path)
            continue
        ok, why = bitness_ok(path)
        print()
        print("=== %s ===" % path)
        if not ok:
            print("  SKIPPED - %s.  NOT tested; this is not a pass." % why)
            failures += 1
            continue

        print("  [pre-CRT] mapped with DONT_RESOLVE_DLL_REFERENCES, DllMain never runs")
        for probe in PROBES[dll_name]:
            if not check_precrt(path, probe["name"], indent="    "):
                failures += 1

        print("  [forward] normal LoadLibraryW, DllMain runs, must reach the real DLL")
        for probe in PROBES[dll_name]:
            if not check_forward(path, probe["name"], indent="    "):
                failures += 1

        if baseline:
            base = os.path.join(baseline, dll_name)
            if not os.path.isfile(base):
                print("  [control] no baseline at %s - the rig was NOT shown able to fail"
                      % base)
                failures += 1
            else:
                print("  [control] the SAME probes against the pre-gate binary")
                any_faulted = False
                for probe in PROBES[dll_name]:
                    code, ret, _ = call_in_child("precrt", base, probe["name"])
                    print("    %-26s => %s" % (probe["name"], describe_exit(code)))
                    any_faulted = any_faulted or faulted(code)
                if not any_faulted:
                    print("    !! the baseline never faulted, so this comparison proves "
                          "nothing for this flavour")
                    failures += 1
    print()
    if failures:
        print("FAILED - %d check(s) did not pass" % failures)
    else:
        print("PASS - every gated probe returned cleanly and every forward reached the real DLL")
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--child", nargs=3, metavar=("MODE", "DLL", "EXPORT"),
                    help=argparse.SUPPRESS)
    ap.add_argument("--all", action="store_true",
                    help="run every probe against both shipped proxies")
    ap.add_argument("--dist", default="dist",
                    help="directory holding version.dll / winmm.dll (default: dist)")
    ap.add_argument("--baseline", default=None,
                    help="directory holding PRE-GATE proxies, used as the negative control")
    ap.add_argument("--forward", action="store_true",
                    help="normal LoadLibraryW; the export must reach the real System32 DLL")
    ap.add_argument("--compare", nargs=3, metavar=("OLD", "NEW", "EXPORT"),
                    help="pre-CRT probe the same export in two binaries")
    ap.add_argument("dll", nargs="?")
    ap.add_argument("export", nargs="?")
    args = ap.parse_args()

    if os.name != "nt":
        print("Windows only - this maps a PE and calls into it.")
        return 2

    if args.child:
        return run_child(*args.child)

    if args.compare:
        old, new, export = args.compare
        print("[NEGATIVE CONTROL] %s" % old)
        old_code, _, _ = call_in_child("precrt", old, export)
        print("    %-26s => %s" % (export, describe_exit(old_code)))
        print("[GATED]            %s" % new)
        ok = check_precrt(new, export, indent="    ")
        if not faulted(old_code):
            print("\n!! The control did not fault, so this comparison does not "
                  "discriminate for this export.")
            return 1
        return 0 if ok else 1

    if args.all:
        return run_all(args.dist, args.baseline)

    if not args.dll or not args.export:
        ap.error("give --all, --compare, or a DLL and an export name")

    ok, why = bitness_ok(args.dll)
    if not ok:
        print("SKIPPED - %s.  NOT tested; this is not a pass." % why)
        return 2

    if args.forward:
        return 0 if check_forward(args.dll, args.export) else 1
    return 0 if check_precrt(args.dll, args.export) else 1


if __name__ == "__main__":
    sys.exit(main())
