import sys
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
import time
sys.path.insert(0, _HERE)
import dh

pid, name = dh.find_pid(sys.argv[1] if len(sys.argv) > 1 else 'CoreKeeper')
print('target: pid=%s name=%s' % (pid, name))
print('waiting for pipe...')
if not dh.wait_for_pipe(180):
    print('PIPE NEVER CAME UP')
    sys.exit(1)
with dh.Rpc(label='probe') as r:
    hs = r.call('handshake')
    print('handshake:', hs)
    asm = r.call('getAssemblies')
    names = [a.get('name', a) if isinstance(a, dict) else a
             for a in (asm.get('assemblies') or [])]
    print('assemblies: %d' % len(names))
    print('  first 15:', names[:15])
    if pid:
        ws, priv = dh.mem_info(pid)
        print('game mem: ws=%.0f MB private=%.0f MB threads=%d'
              % (ws, priv, dh.thread_count(pid)))
