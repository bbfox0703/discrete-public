"""G3 D-1 done properly:
  * only REFERENCE classes (a value type's metadata offsets are not addressable
    from an instance base - that was what produced 'foreign' field names before)
  * raw whatIsAt output so the wire keys are not guessed
  * cross-check every field value against ReadProcessMemory first, so we only
    probe instances we have proven are real
"""
import json, struct, sys, time
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
sys.path.insert(0, _HERE)
import dh

GAME = sys.argv[1] if len(sys.argv) > 1 else 'Mortal'
pid, pname = dh.find_pid(GAME)
rep = dh.Report('G3 D-1 on %s (pid %s)' % (pname, pid))
FOUR = ('System.Int32', 'System.UInt32', 'System.Single')
ASMS = ['Mortal.Core', 'FlatKit.Runtime', 'Mortal.Battle', 'OBB.Framework']


def field_name_of(w):
    """Pull the field name out of a whatIsAt response, whatever shape it uses."""
    for k in ('field', 'fieldName'):
        v = w.get(k)
        if isinstance(v, str) and v:
            return v
        if isinstance(v, dict) and v.get('name'):
            return v['name']
    for k in ('resolved', 'instance', 'match'):
        v = w.get(k)
        if isinstance(v, dict):
            n = field_name_of(v)
            if n:
                return n
    return ''


with dh.Rpc() as r:
    good = []           # (asm, ns, nm, base, fields)
    for asm in ASMS:
        if len(good) >= 3:
            break
        try:
            classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        except Exception:
            continue
        for c in classes:
            if len(good) >= 3:
                break
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or '<' in nm:
                continue
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception:
                continue
            # REFERENCE types only. Field offsets of a value type are relative to
            # a boxed layout, so base+offset means nothing for them.
            if ci.get('is_value_type') or ci.get('isValueType'):
                continue
            flds = [f for f in (ci.get('fields') or [])
                    if not f.get('is_static') and isinstance(f.get('offset'), int)
                    and str(f.get('type')) in FOUR]
            flds.sort(key=lambda f: f['offset'])
            if len(flds) < 2 or flds[0]['offset'] < 8:
                continue          # a real x86 object starts its fields past the 8-byte header
            try:
                fi = r.call('findInstances', {'assembly': asm, 'namespace': ns, 'name': nm,
                                              'limit': 3, 'validate': True, 'sane': True})
            except Exception:
                continue
            inst = fi.get('instances') or fi.get('matches') or []
            if not inst:
                continue
            base = int(str(inst[0]['address'] if isinstance(inst[0], dict) else inst[0]), 16)
            w = r.call('walkInstance', {'address': '0x%X' % base, 'assembly': asm,
                                        'namespace': ns, 'name': nm})
            wf = {f.get('name'): f.get('value') for f in (w.get('fields') or [])}
            proven = 0
            for f in flds:
                raw = dh.read_mem(pid, base + f['offset'], 4)
                if len(raw) != 4:
                    continue
                rv = struct.unpack('<i', raw)[0] if f['type'] != 'System.Single' \
                    else round(struct.unpack('<f', raw)[0], 4)
                wv = wf.get(f['name'])
                if isinstance(wv, float):
                    wv = round(wv, 4)
                if wv == rv:
                    proven += 1
            print('    %-42s fields=%d value-verified=%d base=0x%X'
                  % (('%s.%s' % (asm, nm))[:42], len(flds), proven, base))
            if proven >= 2:
                good.append((asm, ns, nm, base, flds))

    if not good:
        rep.check('found a verified reference instance to probe', False)
        sys.exit(rep.summary())

    asm, ns, nm, base, flds = good[0]
    print('\n    RAW whatIsAt at the instance base (0x%X):' % base)
    w0 = r.call('whatIsAt', {'address': '0x%X' % base})
    print('      %s' % json.dumps(w0)[:700])
    print('    keys: %s' % list(w0.keys()))

    probes, wrong = 0, []
    for asm, ns, nm, base, flds in good:
        for i in range(len(flds) - 1):
            f1, f2 = flds[i], flds[i + 1]
            if f2['offset'] - f1['offset'] != 4:
                continue
            probes += 1
            w = r.call('whatIsAt', {'address': '0x%X' % (base + f2['offset'])})
            got = field_name_of(w)
            want = f2['name']
            ok = str(got).split('.')[0] == want
            if not ok:
                wrong.append((nm, f1['name'], want, got, json.dumps(w)[:200]))
            print('    %-26s +%-4d %-24s -> whatIsAt %-26s %s'
                  % (nm[:26], f2['offset'], want, repr(got), 'OK' if ok else 'MISMATCH'))
            if probes >= 12:
                break
        if probes >= 12:
            break
    for x in wrong[:5]:
        print('      MISMATCH detail %s: %s' % (x[0], x[4]))
    rep.check('D-1 boundary addresses resolve to the owning field',
              probes > 0 and not wrong, '%d/%d wrong' % (len(wrong), probes))
sys.exit(rep.summary())
