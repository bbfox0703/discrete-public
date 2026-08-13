"""Find a live UInt64 field for smoke item I-1 (write a value above long.MaxValue
through the UI and read it back). Prints the exact class + field + instance so the
UI session can navigate straight to it, and cross-checks the current value against
ReadProcessMemory so we know the address is real before touching it."""
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
import struct
import sys
sys.path.insert(0, _HERE)
import dh

GAME = sys.argv[1] if len(sys.argv) > 1 else 'CoreKeeper'
WANT = int(sys.argv[2]) if len(sys.argv) > 2 else 5
pid, pname = dh.find_pid(GAME)
print('target %s pid %s' % (pname, pid))
SKIP = ('mscorlib', 'netstandard', 'Mono.', 'Microsoft', 'Newtonsoft', 'nunit',
        'JetBrains', 'Sentry', 'I18N')

found = []
with dh.Rpc() as r:
    ptr = r.call('handshake')['game_pointer_size']
    asms = [a['name'] if isinstance(a, dict) else a
            for a in r.call('getAssemblies').get('assemblies', [])]
    asms = [a for a in asms if not any(a.startswith(s) for s in SKIP)]
    for asm in asms:
        if len(found) >= WANT:
            break
        try:
            classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        except Exception:
            continue
        for c in classes:
            if len(found) >= WANT:
                break
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or '<' in nm:
                continue
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception:
                continue
            u64 = [f for f in (ci.get('fields') or [])
                   if str(f.get('type')) in ('System.UInt64', 'System.Int64')
                   and not (f.get('static') or f.get('is_static'))
                   and isinstance(f.get('offset'), int)]
            if not u64:
                continue
            try:
                fi = r.call('findInstances', {'assembly': asm, 'namespace': ns, 'name': nm,
                                              'limit': 4, 'validate': True, 'sane': True})
            except Exception:
                continue
            for i in (fi.get('instances') or fi.get('matches') or []):
                base = int(str(i['address'] if isinstance(i, dict) else i), 16)
                # identity gate: whatIsAt must agree this is that class, at offset 0
                try:
                    w = r.call('whatIsAt', {'address': '0x%X' % base})
                except Exception:
                    continue
                if w.get('class') != nm or w.get('fieldOffset') not in (0, None):
                    continue
                for f in u64:
                    raw = dh.read_mem(pid, base + f['offset'], 8)
                    if len(raw) != 8:
                        continue
                    cur = struct.unpack('<Q', raw)[0]
                    found.append({'assembly': asm, 'namespace': ns, 'class': nm,
                                  'field': f['name'], 'type': f['type'],
                                  'offset': f['offset'], 'instance': '0x%X' % base,
                                  'current': cur})
                    print('  %-22s.%-26s %-14s +%-4d inst=0x%X current=%d'
                          % (nm[:22], f['name'][:26], f['type'].split('.')[-1],
                             f['offset'], base, cur))
                    break
                break

print()
if not found:
    print('no live UInt64/Int64 instance field found')
    sys.exit(1)
t = found[0]
print('USE THIS for I-1:')
print('  assembly = %s' % t['assembly'])
print('  class    = %s%s' % ((t['namespace'] + '.') if t['namespace'] else '', t['class']))
print('  field    = %s (%s) at +%d' % (t['field'], t['type'], t['offset']))
print('  instance = %s   current value = %d' % (t['instance'], t['current']))
print('  write    = 18446744073709551615   (0xFFFFFFFFFFFFFFFF, > long.MaxValue)')
