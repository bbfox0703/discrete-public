"""Measure whether Cheat Engine's GUI thread keeps pumping messages, from OUTSIDE
the process — the objective version of "CE 視窗全程可操作".

SendMessageTimeout(WM_NULL) is answered by the target's message loop, so a probe
that times out means the GUI thread is blocked. Reports the longest continuous
unresponsive stretch, which is what separates "pumps every ~200 ms between
sleeps" (PASS) from "frozen for the whole 3 s retry budget" (FAIL).

usage: python ce_watchdog.py [seconds] [probe_timeout_ms]
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time

u32 = ctypes.WinDLL('user32', use_last_error=True)
k32 = ctypes.WinDLL('kernel32', use_last_error=True)

WM_NULL = 0x0000
SMTO_ABORTIFHUNG = 0x0002
SMTO_BLOCK = 0x0001

WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def ce_windows():
    """Every visible top-level window owned by a cheatengine process."""
    import os
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import dh
    pids = {p for p, n, _ in dh.list_processes() if 'cheatengine' in n.lower()}
    found = []

    def cb(hwnd, _):
        pid = wt.DWORD(0)
        u32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value in pids and u32.IsWindowVisible(hwnd):
            n = u32.GetWindowTextLengthW(hwnd)
            buf = ctypes.create_unicode_buffer(n + 1)
            u32.GetWindowTextW(hwnd, buf, n + 1)
            found.append((hwnd, buf.value, pid.value))
        return True

    u32.EnumWindows(WNDENUMPROC(cb), 0)
    return found


def responsive(hwnd, timeout_ms):
    res = ctypes.c_ulong(0)
    r = u32.SendMessageTimeoutW(wt.HWND(hwnd), WM_NULL, 0, 0,
                                SMTO_ABORTIFHUNG | SMTO_BLOCK,
                                ctypes.c_uint(timeout_ms), ctypes.byref(res))
    return r != 0


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 15.0
    probe_ms = int(sys.argv[2]) if len(sys.argv) > 2 else 400

    wins = ce_windows()
    if not wins:
        print('no Cheat Engine window found')
        return 2
    hwnd, title, pid = wins[0]
    print('watching hwnd=0x%X pid=%d title=%r  (probe timeout %d ms, %.0fs)'
          % (hwnd, pid, title, probe_ms, secs))
    for h, t, _ in wins[1:]:
        print('  (also open: 0x%X %r)' % (h, t))

    t0 = time.time()
    worst, worst_at, streak, streak_start = 0.0, 0.0, 0.0, None
    fails = 0
    samples = 0
    while time.time() - t0 < secs:
        t = time.time()
        ok = responsive(hwnd, probe_ms)
        samples += 1
        if ok:
            if streak_start is not None:
                dur = t - streak_start
                if dur > worst:
                    worst, worst_at = dur, streak_start - t0
                streak_start = None
        else:
            fails += 1
            if streak_start is None:
                streak_start = t
            print('    +%5.2fs UNRESPONSIVE' % (t - t0))
        time.sleep(0.1)
    if streak_start is not None:
        dur = time.time() - streak_start
        if dur > worst:
            worst, worst_at = dur, streak_start - t0

    print('probes=%d unresponsive=%d  longest continuous block=%.2fs (at +%.2fs)'
          % (samples, fails, worst, worst_at))
    print('VERDICT: %s' % ('PASS - GUI kept pumping' if worst < 1.0
                           else 'FAIL - GUI thread blocked for %.2fs' % worst))
    return 0


if __name__ == '__main__':
    sys.exit(main())
