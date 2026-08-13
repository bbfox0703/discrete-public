"""The ScanCancel::Scope refcount, exercised by two genuinely overlapping scans.

With the old bool the fix for scanFieldValue would have BROKEN concurrent scans
two ways: the second Scope's ctor cleared the first scan's cancel (making scan A
uncancellable), and the second's dtor dropped `active` while A was still running
(closing RequestCancel's gate). Counting scopes fixes both.

Observable: two overlapping scans + one cancelScan -> BOTH abort promptly (the
documented collateral-cancel trade-off), and the scan after them is complete, so
the flag did not leak past the last scope.
"""
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
import sys
import threading
import time
sys.path.insert(0, _HERE)
import dh

GAME = sys.argv[1] if len(sys.argv) > 1 else 'CoreKeeper'
VALUE = int(sys.argv[2]) if len(sys.argv) > 2 else 6278
pid, pname = dh.find_pid(GAME)
rep = dh.Report('overlapping-scan cancellation on %s' % pname)
out = {}


def runner(tag, delay):
    time.sleep(delay)
    try:
        with dh.Rpc(timeout=20) as r:
            t = time.time()
            res = r.call('scanFieldValue', {'dataType': 'int32', 'value': VALUE, 'limit': 200})
            out[tag] = {'dt': time.time() - t, 'cancelled': res.get('cancelled'),
                        'ok': res.get('ok'), 'count': res.get('count'),
                        'error': res.get('error')}
    except Exception as e:
        out[tag] = {'err': str(e)}


a = threading.Thread(target=runner, args=('A', 0.0), daemon=True)
b = threading.Thread(target=runner, args=('B', 1.5), daemon=True)
a.start()
b.start()
time.sleep(4.0)                      # both are now inside their Scopes
t_cancel = time.time()
with dh.Rpc(timeout=5) as rc:
    print('    cancelScan while BOTH are scanning -> %s' % rc.call('cancelScan'))
a.join(150)
b.join(150)
for tag in ('A', 'B'):
    print('    scan %s: %s' % (tag, out.get(tag)))

both = [out.get('A', {}), out.get('B', {})]
rep.check('both overlapping scans returned', all('dt' in x for x in both), str(out))
rep.check('both report cancelled (neither had its cancel stolen)',
          all(x.get('cancelled') is True for x in both),
          'A=%s B=%s' % (both[0].get('cancelled'), both[1].get('cancelled')))
rep.check('both aborted promptly (uncancelled sweep is ~33s)',
          all((x.get('dt') or 99) < 25 for x in both),
          'A=%.1fs B=%.1fs' % (both[0].get('dt', -1), both[1].get('dt', -1)))

# the flag must not survive the last scope
print('    now a plain scan - must be complete')
with dh.Rpc(timeout=20) as r:
    t = time.time()
    res = r.call('scanFieldValue', {'dataType': 'int32', 'value': VALUE, 'limit': 200})
    n = len(res.get('candidates') or [])
    print('    %.1fs count=%s cancelled=%s scanned=%.2f GB'
          % (time.time() - t, res.get('count'), res.get('cancelled'),
             res.get('scannedBytes', 0) / 2 ** 30))
rep.check('the scan after them is complete (flag did not leak past the last scope)',
          n > 0 and not res.get('cancelled'),
          'count=%s cancelled=%s' % (res.get('count'), res.get('cancelled')))
sys.exit(rep.summary())
