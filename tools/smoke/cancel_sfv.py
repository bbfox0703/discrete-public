"""Is scanFieldValue cancellable now?

Before the fix it owned no ScanCancel::Scope, so g_scanActive stayed false, so
RequestCancel() was a no-op: neither the Cancel button nor a dead client could
stop it. Measured symptom on a 4 GB heap - the client was force-killed 2 s into
the scan and the sweep still ran its full 33 s, pinning a core inside the game.

Three things have to hold now:
  1 an explicit cancelScan aborts it promptly, and it reports cancelled rather
    than handing back the truncated candidate list as a complete result
  2 a client that simply DIES mid-scan also stops it (the disconnect monitor)
  3 the next scan is still complete and correct - i.e. the cancel flag did not
    leak into it (that regression is P2)
"""
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
import json
import subprocess
import sys
import threading
import time
sys.path.insert(0, _HERE)
import dh

GAME = sys.argv[1] if len(sys.argv) > 1 else 'CoreKeeper'
VALUE = int(sys.argv[2]) if len(sys.argv) > 2 else 6278
# How long to let the scan run before cancelling. Must be well inside the sweep:
# a 4 GB Mono heap takes ~33 s, but IL2CPP's SIMD path can do 4.3 GB in ~1.5 s,
# and a cancel that lands after the scan finished proves nothing.
CANCEL_AFTER = float(sys.argv[3]) if len(sys.argv) > 3 else 2.0
pid, pname = dh.find_pid(GAME)
rep = dh.Report('scanFieldValue cancellation on %s (pid %s)' % (pname, pid))

with dh.Rpc() as r:
    print('    DLL build=%s' % r.call('handshake').get('build'))

    # ---------------------------------------------------------------- baseline
    t0 = time.time()
    base = r.call('scanFieldValue', {'dataType': 'int32', 'value': VALUE, 'limit': 200})
    base_dt = time.time() - t0
    base_n = len(base.get('candidates') or [])
    print('\n    [baseline] uncancelled scan: %.1fs count=%s scanned=%.2f GB'
          % (base_dt, base.get('count'), base.get('scannedBytes', 0) / 2 ** 30))
    rep.check('baseline scan completes and returns hits', base_n > 0 and not base.get('cancelled'),
              'count=%s cancelled=%s' % (base.get('count'), base.get('cancelled')))

    # ------------------------------------------------- 1: explicit cancelScan
    print('\n    [1] explicit cancelScan %.2fs in (the sweep takes %.1fs)'
          % (CANCEL_AFTER, base_dt))
    if CANCEL_AFTER >= base_dt * 0.8:
        print('        !! that delay is NOT inside the sweep — the cancel would land')
        print('           after it finished and prove nothing. Pass a smaller argv[3].')
    out = {}

    def scan():
        try:
            t = time.time()
            out['res'] = r.call('scanFieldValue',
                                {'dataType': 'int32', 'value': VALUE, 'limit': 200})
            out['dt'] = time.time() - t
        except Exception as e:
            out['err'] = str(e)

    th = threading.Thread(target=scan, daemon=True)
    th.start()
    time.sleep(CANCEL_AFTER)
    t_cancel = time.time()
    with dh.Rpc(timeout=5) as rc:
        print('        cancelScan -> %s' % rc.call('cancelScan'))
    th.join(120)
    res = out.get('res') or {}
    after = time.time() - t_cancel
    print('        scan returned %.2fs after the cancel (total %.2fs): ok=%s cancelled=%s '
          'error=%s count=%s'
          % (after, out.get('dt', -1), res.get('ok'), res.get('cancelled'),
             res.get('error'), res.get('count')))
    rep.check('cancelScan actually stops it (was: ran the full sweep regardless)',
              after < max(8.0, base_dt * 0.5),
              '%.2fs after cancel, uncancelled baseline is %.1fs' % (after, base_dt))
    rep.check('it reports cancelled instead of a truncated result',
              res.get('cancelled') is True and res.get('ok') is False,
              'cancelled=%s ok=%s' % (res.get('cancelled'), res.get('ok')))
    rep.check('no partial candidate list is handed back',
              not (res.get('candidates') or []),
              '%d candidates in a cancelled reply' % len(res.get('candidates') or []))

    # ------------------------------------------- 3: the NEXT scan is unaffected
    print('\n    [3] the next scan must be complete (P2 regression guard)')
    t0 = time.time()
    again = r.call('scanFieldValue', {'dataType': 'int32', 'value': VALUE, 'limit': 200})
    print('        %.1fs count=%s scanned=%.2f GB'
          % (time.time() - t0, again.get('count'), again.get('scannedBytes', 0) / 2 ** 30))
    sig_a = sorted(c.get('addr', '') for c in (base.get('candidates') or []))
    sig_b = sorted(c.get('addr', '') for c in (again.get('candidates') or []))
    overlap = len(set(sig_a) & set(sig_b))
    rep.check('post-cancel scan is complete, not truncated',
              len(sig_b) > 0 and again.get('scannedBytes', 0) >= 0.9 * base.get('scannedBytes', 1),
              'count=%s scanned=%s vs baseline %s'
              % (again.get('count'), again.get('scannedBytes'), base.get('scannedBytes')))
    rep.check('post-cancel candidates match the baseline',
              overlap >= max(1, int(0.8 * min(len(sig_a), len(sig_b)))),
              'overlap %d of %d/%d' % (overlap, len(sig_a), len(sig_b)))

# --------------------------------------- 2: a client that dies mid-scan
print('\n    [2] client force-killed mid-scan (disconnect monitor)')
if base_dt < 6:
    # On a fast sweep (IL2CPP's SIMD path does 4.3 GB in ~1.8s) a CPU-settling
    # measurement cannot tell an abort from normal completion — it would pass
    # vacuously. Check [1] already proves the cancel path works on this target.
    print('        SKIPPED: the sweep is only %.1fs, so CPU settling cannot' % base_dt)
    print('        distinguish an abort from the scan simply finishing.')
    sys.exit(rep.summary())
base_cpu = sum(dh.cpu_percent(pid, 1.0) for _ in range(2)) / 2
print('        idle CPU baseline %.0f%%' % base_cpu)
flag = _os.path.join(_HERE, 'child_flag.txt')
if _os.path.exists(flag):
    _os.remove(flag)
child = subprocess.Popen([sys.executable, _os.path.join(_HERE, 'scan_child.py'),
                          str(VALUE), flag, 'scanFieldValue'],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
for _ in range(80):
    if _os.path.exists(flag):
        break
    time.sleep(0.1)
t_scan = time.time()
time.sleep(2.0)
child.kill()
child.wait(10)
t_kill = time.time()
print('        client killed %.1fs into the scan' % (t_kill - t_scan))
idle_at = None
for i in range(20):
    c = dh.cpu_percent(pid, 1.0)
    dt = time.time() - t_scan
    if c <= base_cpu + 45 and idle_at is None:
        idle_at = dt
        print('        t=+%4.1fs cpu=%3.0f%%  <- back to idle' % (dt, c))
        break
    print('        t=+%4.1fs cpu=%3.0f%%' % (dt, c))
rep.check('a dead client also stops the sweep',
          idle_at is not None and idle_at < max(10.0, base_dt * 0.6),
          'idle at t=+%s (uncancelled sweep runs %.1fs)'
          % ('%.1fs' % idle_at if idle_at else 'never', base_dt))
rep.check('pipe still healthy afterwards', dh.pipe_alive(6))
sys.exit(rep.summary())
