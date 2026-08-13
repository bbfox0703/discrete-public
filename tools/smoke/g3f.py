"""D-1, assumption-light. Earlier runs compared whatIsAt's answer against field
offsets from getClassInfo, and on some classes those two disagree by a constant
8 bytes (x86 object header), which looks like mass misattribution but is a frame
difference, not the bug D-1 is about.

So: don't use metadata offsets to build the probe addresses at all. Sweep the
object 4 bytes at a time and judge whatIsAt only against ITSELF:

  invariant A  reportedOffset + interiorOffset - probeOffset is CONSTANT across
               the sweep. A constant 0 means whatIsAt and we share a frame; a
               constant 8 means only that its frame starts 8 bytes earlier. What
               would NOT be constant is genuine misattribution.
  invariant B  the D-1 bug signature: two consecutive 4-byte probes attributed to
               the SAME field, i.e. a field whose size was overestimated (the
               unresolved-size fallback was hard-coded 8, so on x86 every 4-byte
               field swallowed the next one). Any run of length > 1 over 4-byte
               steps is the failure.
"""
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
import json
import sys
sys.path.insert(0, _HERE)
import dh

GAME = sys.argv[1] if len(sys.argv) > 1 else 'Mortal'
pid, pname = dh.find_pid(GAME)
rep = dh.Report('D-1 attribution sweep on %s (pid %s)' % (pname, pid))
SKIP = ('System', 'mscorlib', 'netstandard', 'Mono.', 'Unity', 'Microsoft',
        'Newtonsoft', 'AK.', 'Ak.', 'DOTween', 'NaughtyAttributes', 'com.', 'I18N')
FOUR = ('System.Int32', 'System.UInt32', 'System.Single', 'System.Boolean')


def nf_of(w):
    nf = w.get('nearestField')
    if isinstance(nf, dict) and nf.get('name'):
        return nf
    f = w.get('field')
    if isinstance(f, dict):
        return f
    return None


with dh.Rpc() as r:
    hs = r.call('handshake')
    print('    build=%s pointer_size=%s' % (hs.get('build'), hs['game_pointer_size']))
    asms = [a['name'] if isinstance(a, dict) else a
            for a in r.call('getAssemblies').get('assemblies', [])]
    game = [a for a in asms if not any(a.startswith(s) for s in SKIP)]

    targets = []
    for asm in game:
        if len(targets) >= 3:
            break
        try:
            classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        except Exception:
            continue
        for c in classes:
            if len(targets) >= 3:
                break
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or '<' in nm or '/' in nm:
                continue
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception:
                continue
            small = [f for f in (ci.get('fields') or [])
                     if not f.get('is_static') and str(f.get('type')) in FOUR]
            if len(small) < 3:
                continue
            try:
                fi = r.call('findInstances', {'assembly': asm, 'namespace': ns, 'name': nm,
                                              'limit': 6, 'validate': True})
            except Exception:
                continue
            for i in (fi.get('instances') or fi.get('matches') or []):
                base = int(str(i['address'] if isinstance(i, dict) else i), 16)
                try:
                    w0 = r.call('whatIsAt', {'address': '0x%X' % base})
                except Exception:
                    continue
                if w0.get('class') != nm or w0.get('fieldOffset') not in (0, None):
                    continue
                targets.append((asm, nm, base, {f['name']: f for f in small}))
                print('      target %-40s base=0x%X (%d small fields)'
                      % (('%s.%s' % (asm, nm))[:40], base, len(small)))
                break

    if not targets:
        rep.check('found an identity-confirmed instance to sweep', False)
        sys.exit(rep.summary())

    all_ok = True
    for asm, nm, base, byname in targets:
        print('\n      --- %s.%s @ 0x%X' % (asm, nm, base))
        rows = []
        for off in range(0, 68, 4):
            try:
                w = r.call('whatIsAt', {'address': '0x%X' % (base + off)})
            except Exception as e:
                rows.append((off, 'RPC-ERROR', None, None, None))
                continue
            nf = nf_of(w)
            if not nf:
                rows.append((off, None, None, None, w.get('fieldOffset')))
                continue
            rows.append((off, nf.get('name'), nf.get('offset'),
                         nf.get('interiorOffset') or 0, w.get('fieldOffset')))
        deltas, runs, prev = set(), [], None
        for off, name, foff, interior, finstoff in rows:
            if name and isinstance(foff, int):
                deltas.add(foff + (interior or 0) - off)
            mark = ''
            if name and name == prev:
                mark = '  <-- same field as the previous 4-byte step'
                runs.append((off, name))
            prev = name
            print('        +%-3d %-34s fieldOffset=%-5s interior=%-4s instOff=%-5s%s'
                  % (off, str(name)[:34], foff, interior, finstoff, mark))
        print('        distinct (reportedOffset+interior - probeOffset) values: %s' % sorted(deltas))
        ok_a = len(deltas) <= 1
        # A repeat is only the D-1 bug when the repeated field is 4 bytes wide.
        bad_runs = [(o, n) for o, n in runs if n in byname]
        ok_b = not bad_runs
        all_ok = all_ok and ok_a and ok_b
        rep.check('%s: attribution frame is consistent across the sweep' % nm, ok_a,
                  'deltas=%s' % sorted(deltas))
        rep.check('%s: no 4-byte field swallows the next one' % nm, ok_b,
                  'repeats=%s' % bad_runs[:4])

sys.exit(rep.summary())
