"""G1-A follow-up: P2 candidate-set comparison with the right wire keys ('addr'),
and a peak-memory measurement of the whole-heap walk for the I-2/I-3 A/B."""
import json, sys, threading, time
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
sys.path.insert(0, _HERE)
import dh

GAME = sys.argv[1] if len(sys.argv) > 1 else 'CoreKeeper'
TAG = sys.argv[2] if len(sys.argv) > 2 else 'run'
pid, pname = dh.find_pid(GAME)
with open(_HERE + r'\target.json') as f:
    T = json.load(f)[0]
VALUE = T['value']

samples = []
stop = False


def sampler():
    while not stop:
        try:
            samples.append(dh.mem_info(pid)[1])
        except Exception:
            pass
        time.sleep(0.3)


def sig(res):
    return sorted(c.get('addr', '') for c in (res.get('candidates') or []))


def described(res):
    out = []
    for c in (res.get('candidates') or [])[:5]:
        out.append('%s %s.%s+%s=%s' % (c.get('addr'), c.get('class'), c.get('field'),
                                       c.get('fieldOffset'), c.get('currentValue')))
    return out


th = threading.Thread(target=sampler, daemon=True)
th.start()
time.sleep(1)
idle = samples[-1]

with dh.Rpc() as r:
    hs = r.call('handshake')
    print('[%s] DLL build=%s  idle private=%.0f MB' % (TAG, hs.get('build'), idle))

    t0 = time.time()
    a = r.call('scanFieldValue', {'dataType': 'int32', 'value': VALUE, 'limit': 200})
    ta = time.time() - t0
    peak_a = max(samples)
    print('[%s] scan#1 %.1fs count=%s scanned=%.2f GB  peak private=%.0f MB (+%.0f)'
          % (TAG, ta, a.get('count'), a.get('scannedBytes', 0) / 2 ** 30, peak_a, peak_a - idle))
    for line in described(a):
        print('        ' + line)

    # settle, then a second identical scan: a leak keeps climbing, transient
    # working memory returns to a similar peak.
    time.sleep(6)
    mid = samples[-1]
    base2 = len(samples)
    t0 = time.time()
    b = r.call('scanFieldValue', {'dataType': 'int32', 'value': VALUE, 'limit': 200})
    tb = time.time() - t0
    peak_b = max(samples[base2:])
    print('[%s] settled to %.0f MB' % (TAG, mid))
    print('[%s] scan#2 %.1fs count=%s scanned=%.2f GB  peak private=%.0f MB (+%.0f over settle)'
          % (TAG, tb, b.get('count'), b.get('scannedBytes', 0) / 2 ** 30, peak_b, peak_b - mid))

    sa, sb = sig(a), sig(b)
    ov = len(set(sa) & set(sb))
    print('[%s] candidate overlap between two identical scans: %d of %d/%d'
          % (TAG, ov, len(sa), len(sb)))

    # cancel, then scan again and compare to scan#2
    out = {}

    def long_scan():
        try:
            out['r'] = r.call('beginValueScan', {'dataType': 'int32', 'scanType': 'exact',
                                                 'value': VALUE, 'limit': 100})
        except Exception as e:
            out['e'] = str(e)

    t = threading.Thread(target=long_scan, daemon=True)
    t.start()
    time.sleep(1.5)
    with dh.Rpc(timeout=5) as rc:
        rc.call('cancelScan')
    t.join(120)
    print('[%s] cancelled scan -> %s' % (TAG, {k: v for k, v in (out.get('r') or {}).items()
                                               if k in ('ok', 'cancelled', 'error', 'count')}))
    c = r.call('scanFieldValue', {'dataType': 'int32', 'value': VALUE, 'limit': 200})
    sc = sig(c)
    ov2 = len(set(sb) & set(sc))
    print('[%s] post-cancel scan: count=%s scanned=%.2f GB  overlap with scan#2: %d of %d/%d'
          % (TAG, c.get('count'), c.get('scannedBytes', 0) / 2 ** 30, ov2, len(sb), len(sc)))

stop = True
json.dump({'tag': TAG, 'build': hs.get('build'), 'idle': idle,
           'peak_scan1': peak_a, 'peak_scan2': peak_b, 'settle': mid,
           'count1': a.get('count'), 'count2': b.get('count'),
           'scanned1': a.get('scannedBytes'), 'overlap_ab': ov,
           'overlap_post_cancel': ov2, 'count_post_cancel': c.get('count'),
           'scanned_post_cancel': c.get('scannedBytes')},
          open(_HERE + r'\mem_%s.json' % TAG, 'w'), indent=2)
print('[%s] wrote mem_%s.json' % (TAG, TAG))
