"""D-1 / D-2 on 32-bit Mono, with the harness mistakes from the first attempt fixed:
  * reference types only (a value type's metadata offsets are not addressable
    from an instance base - that is what produced 'foreign' field names before)
  * every instance is proven real by matching walkInstance values against
    ReadProcessMemory before it is used as a probe target
  * rejection reasons are printed, so 'found nothing' is diagnosable
  * D-2 requires an IntPtr[] whose header actually looks like a MonoArray
"""
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
import json
import struct
import sys
sys.path.insert(0, _HERE)
import dh

GAME = sys.argv[1] if len(sys.argv) > 1 else 'Mortal'
WANT = int(sys.argv[2]) if len(sys.argv) > 2 else 3
pid, pname = dh.find_pid(GAME)
rep = dh.Report('D-1 / D-2 on %s (pid %s)' % (pname, pid))
FOUR = {'System.Int32': '<i', 'System.UInt32': '<I', 'System.Single': '<f'}
SKIP = ('System', 'mscorlib', 'netstandard', 'Mono.', 'Unity', 'Microsoft',
        'Newtonsoft', 'AK.', 'Ak.', 'DOTween', 'NaughtyAttributes', 'com.', 'I18N')

reasons = {}


def bump(k):
    reasons[k] = reasons.get(k, 0) + 1


with dh.Rpc() as r:
    hs = r.call('handshake')
    ptr = hs['game_pointer_size']
    print('    build=%s pointer_size=%s unity=%s' % (hs.get('build'), ptr, hs.get('unity_version')))
    rep.check('target is 32-bit', ptr == 4, 'pointer_size=%s' % ptr)

    asms = [a['name'] if isinstance(a, dict) else a
            for a in r.call('getAssemblies').get('assemblies', [])]
    game = [a for a in asms if not any(a.startswith(s) for s in SKIP)]
    print('    game assemblies: %d -> %s' % (len(game), game[:14]))

    good = []
    for asm in game:
        if len(good) >= WANT:
            break
        try:
            classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        except Exception:
            bump('getClasses failed')
            continue
        for c in classes:
            if len(good) >= WANT:
                break
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or '<' in nm or '/' in nm:
                bump('name filtered')
                continue
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception:
                bump('getClassInfo failed')
                continue
            if ci.get('is_value_type') or ci.get('isValueType') or ci.get('value_type'):
                bump('value type')
                continue
            flds = [f for f in (ci.get('fields') or [])
                    if not f.get('is_static') and isinstance(f.get('offset'), int)
                    and str(f.get('type')) in FOUR and f['offset'] >= 2 * ptr]
            flds.sort(key=lambda f: f['offset'])
            pairs = [i for i in range(len(flds) - 1)
                     if flds[i + 1]['offset'] - flds[i]['offset'] == 4]
            if not pairs:
                bump('no adjacent 4-byte pair')
                continue
            try:
                fi = r.call('findInstances', {'assembly': asm, 'namespace': ns, 'name': nm,
                                              'limit': 4, 'validate': True})
            except Exception:
                bump('findInstances failed')
                continue
            inst = fi.get('instances') or fi.get('matches') or []
            if not inst:
                bump('no instances')
                continue
            picked = None
            for i in inst:
                base = int(str(i['address'] if isinstance(i, dict) else i), 16)
                try:
                    w = r.call('walkInstance', {'address': '0x%X' % base, 'assembly': asm,
                                                'namespace': ns, 'name': nm})
                except Exception:
                    continue
                wf = {f.get('name'): f.get('value') for f in (w.get('fields') or [])}
                proven = 0
                for f in flds:
                    raw = dh.read_mem(pid, base + f['offset'], 4)
                    if len(raw) != 4:
                        continue
                    rv = struct.unpack(FOUR[f['type']], raw)[0]
                    wv = wf.get(f['name'])
                    if isinstance(wv, float) or isinstance(rv, float):
                        if wv is not None and abs(float(wv) - float(rv)) < 1e-4:
                            proven += 1
                    elif wv == rv:
                        proven += 1
                if proven >= 2:
                    picked = (base, proven)
                    break
            if picked is None:
                bump('no instance whose values match raw memory')
                continue
            print('      candidate %-40s base=0x%X value-verified=%d fields=%d'
                  % (('%s.%s' % (asm, nm))[:40], picked[0], picked[1], len(flds)))
            good.append((asm, ns, nm, picked[0], flds))

    print('    rejection reasons: %s' % reasons)

    if not good:
        rep.check('D-1 found a verified reference instance', False, str(reasons))
    else:
        probes, wrong = 0, []
        for asm, ns, nm, base, flds in good:
            for i in range(len(flds) - 1):
                f1, f2 = flds[i], flds[i + 1]
                if f2['offset'] - f1['offset'] != 4:
                    continue
                probes += 1
                try:
                    w = r.call('whatIsAt', {'address': '0x%X' % (base + f2['offset'])})
                except Exception as e:
                    wrong.append((nm, f2['name'], 'RPC error %s' % e))
                    continue
                fld = w.get('field')
                got = fld.get('name') if isinstance(fld, dict) else (fld or '')
                ok = str(got).split('.')[0] == f2['name']
                if not ok:
                    wrong.append((nm, f2['name'], got))
                print('      %-24s +%-4d want %-22s got %-22s %s'
                      % (nm[:24], f2['offset'], f2['name'], repr(got),
                         'OK' if ok else 'MISMATCH'))
                if probes >= 15:
                    break
            if probes >= 15:
                break
        rep.check('D-1 boundary address resolves to the owning field',
                  probes > 0 and not wrong, '%d/%d wrong %s' % (len(wrong), probes, wrong[:3]))

    # ------------------------------------------------------------------ D-2
    print('\n    [D-2] looking for a populated IntPtr[] / UIntPtr[]')
    hit = None
    for asm in asms:
        if hit:
            break
        try:
            classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        except Exception:
            continue
        for c in classes:
            if hit:
                break
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or '<' in nm:
                continue
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception:
                continue
            pa = [f for f in (ci.get('fields') or [])
                  if 'IntPtr[]' in str(f.get('type', '')) and not f.get('is_static')]
            if not pa:
                continue
            try:
                fi = r.call('findInstances', {'assembly': asm, 'namespace': ns, 'name': nm,
                                              'limit': 20, 'validate': True})
            except Exception:
                continue
            for i in (fi.get('instances') or fi.get('matches') or []):
                a = int(str(i['address'] if isinstance(i, dict) else i), 16)
                try:
                    w = r.call('walkInstance', {'address': '0x%X' % a, 'assembly': asm,
                                                'namespace': ns, 'name': nm})
                except Exception:
                    continue
                for f in (w.get('fields') or []):
                    if 'IntPtr[]' not in str(f.get('type', '')):
                        continue
                    p = f.get('ptr')
                    if not p or int(str(p), 16) == 0:
                        continue
                    arr = int(str(p), 16)
                    hdr = dh.read_mem(pid, arr, 4 * ptr)
                    if len(hdr) < 4 * ptr:
                        continue
                    # MonoArray x86: [vtable][sync][bounds][max_length]
                    maxlen = struct.unpack_from('<I', hdr, 3 * 4)[0]
                    if 0 < maxlen <= 4096:
                        hit = (asm, ns, nm, f, arr, maxlen)
                        break
                if hit:
                    break
    if not hit:
        print('      none found with a sane max_length (all null or garbage)')
        rep.check('D-2 found a populated IntPtr[]', False, 'no live candidate in this game')
    else:
        asm, ns, nm, f, arr, maxlen = hit
        print('      %s.%s.%s array=0x%X header max_length=%d'
              % (asm, nm, f.get('name'), arr, maxlen))
        wa = r.call('walkArray', {'address': '0x%X' % arr, 'elementType': 'System.IntPtr',
                                  'limit': 64})
        els = wa.get('elements') or []
        got = []
        for e in els:
            v = e.get('ptr') if isinstance(e, dict) else None
            if v is None and isinstance(e, dict):
                v = e.get('value')
            got.append(int(str(v), 16) if isinstance(v, str) else (v or 0))
        n = min(len(got), maxlen, 64)
        data = dh.read_mem(pid, arr + 4 * ptr, 4 * n)
        mine = [struct.unpack_from('<I', data, 4 * i)[0] for i in range(len(data) // 4)]
        print('      walkArray length=%s returned=%d element_size=%s'
              % (wa.get('length'), len(els), wa.get('element_size')))
        print('      mine  : %s' % ['0x%X' % v for v in mine[:8]])
        print('      theirs: %s' % ['0x%X' % v for v in got[:8]])
        rep.check('D-2 element count matches the header', wa.get('length') == maxlen,
                  'walk=%s header=%s' % (wa.get('length'), maxlen))
        rep.check('D-2 values match a 4-byte-stride read', n > 0 and mine[:n] == got[:n],
                  'first differing index %s' % next((i for i in range(n) if mine[i] != got[i]), None))

sys.exit(rep.summary())
