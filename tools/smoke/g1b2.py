"""E-4/E-5 with an unambiguous timeline: kill the client 2s into a ~33s scan and
watch CPU every second for 45s. Abort-on-disconnect => drop within a few seconds.
Ran-to-completion => the extra core stays busy until ~33s after the scan started."""
import json, os, subprocess, sys, time
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
sys.path.insert(0, _HERE)
import dh

SC = _HERE
GAME = sys.argv[1] if len(sys.argv) > 1 else 'CoreKeeper'
pid, pname = dh.find_pid(GAME)
T = json.load(open(os.path.join(SC, 'target.json')))[0]
rep = dh.Report('E-4/E-5 timeline on %s (pid %s)' % (pname, pid))

base = [dh.cpu_percent(pid, 1.0) for _ in range(4)]
baseline = sum(base) / len(base)
print('    idle baseline: %s -> %.0f%%' % (['%.0f' % b for b in base], baseline))

flag = os.path.join(SC, 'child_flag.txt')
if os.path.exists(flag):
    os.remove(flag)
child = subprocess.Popen([sys.executable, os.path.join(SC, 'scan_child.py'),
                          str(T['value']), flag, 'beginValueScan'],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
for _ in range(60):
    if os.path.exists(flag):
        break
    time.sleep(0.1)
t_scan = time.time()
print('    scan started (child pid %d)' % child.pid)
time.sleep(2.0)
child.kill()
child.wait(10)
t_kill = time.time()
print('    client force-killed %.1fs into the scan' % (t_kill - t_scan))

timeline = []
for i in range(45):
    c = dh.cpu_percent(pid, 1.0)
    dt = time.time() - t_scan
    timeline.append((dt, c))
    mark = ''
    if c <= baseline + 45:
        mark = '  <-- back to idle'
    print('    t=+%4.1fs since scan start (%4.1fs after kill): %5.0f%%%s'
          % (dt, time.time() - t_kill, c, mark))
    if len([1 for _, v in timeline[-3:] if v <= baseline + 45]) == 3 and len(timeline) >= 3:
        print('    (3 consecutive idle samples - stopping)')
        break

idle_at = None
for dt, c in timeline:
    if c <= baseline + 45:
        idle_at = dt
        break
print('    first idle sample at t=+%.1fs since scan start' % (idle_at if idle_at else -1))
print('    (a completed scan would keep a core busy until t=~33s)')
rep.check('E-4/E-5 scan aborted quickly after the client died',
          idle_at is not None and idle_at < 15,
          'idle at t=+%.1fs (>=25s would mean it ran to completion)'
          % (idle_at if idle_at else -1))
print('    child flag: %s' % open(flag).read().strip().replace('\n', ' | '))
rep.check('pipe healthy afterwards', dh.pipe_alive(5))
sys.exit(rep.summary())
