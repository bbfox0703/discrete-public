"""D-1 / D-2 on 32-bit Mono, third attempt - with the two harness mistakes fixed.

D-1 identity gate
  Earlier attempts picked "instances" that were really inline value-type structs
  living inside some other object, so whatIsAt correctly named the ENCLOSING
  object's fields and the harness scored it as a miss. Worse, the check used to
  validate a candidate (walkInstance values == ReadProcessMemory at base+offset)
  is circular: both sides read the same base, so an inline struct passes.
  Fix: an address is only accepted as an instance base if whatIsAt(base) itself
  reports the class we asked for AT OFFSET 0. That is independent of anything we
  computed, and it is exactly the claim D-1 tests.

D-2 without needing a real IntPtr[]
  The bug was a hard-coded 8 for IntPtr/UIntPtr element size, so on x86 walkArray
  would stride 8 through a 4-byte array: half the elements, wrong values, reads
  past the end. On x86 sizeof(IntPtr) == sizeof(Int32) == 4, so walking a REAL
  4-byte array twice - once declared System.Int32 (control) and once declared
  System.IntPtr (the fixed path) - must agree on count and on every value, and
  both must agree with our own 4-byte-stride ReadProcessMemory. A surviving
  stride-8 bug makes the IntPtr walk disagree with both.
"""
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
import json
import struct
import sys
sys.path.insert(0, _HERE)
import dh

GAME = sys.argv[1] if len(sys.argv) > 1 else 'Mortal'
pid, pname = dh.find_pid(GAME)
rep = dh.Report('D-1 / D-2 (identity-gated) on %s (pid %s)' % (pname, pid))
FOUR = {'System.Int32': '<i', 'System.UInt32': '<I', 'System.Single': '<f'}
ARR4 = ('System.Int32[]', 'System.UInt32[]', 'System.Single[]')
SKIP = ('System', 'mscorlib', 'netstandard', 'Mono.', 'Unity', 'Microsoft',
        'Newtonsoft', 'AK.', 'Ak.', 'DOTween', 'NaughtyAttributes', 'com.', 'I18N')


def field_of(w):
    """whatIsAt puts the answer in `nearestField` (with kind/interiorOffset) and
    leaves `field` null for an interior hit - reading only `field` scored every
    correct answer as a miss in the first two attempts."""
    nf = w.get('nearestField')
    if isinstance(nf, dict) and nf.get('name'):
        return nf['name'], nf.get('offset'), nf.get('interiorOffset'), nf.get('kind')
    f = w.get('field')
    if isinstance(f, dict):
        return f.get('name') or '', f.get('offset'), None, 'field'
    return (f or ''), w.get('fieldOffset'), None, 'field'


def class_of(w):
    for k in ('class', 'className', 'name'):
        v = w.get(k)
        if isinstance(v, str) and v:
            return v
    inst = w.get('instance')
    if isinstance(inst, dict):
        for k in ('class', 'className', 'name'):
            if isinstance(inst.get(k), str) and inst[k]:
                return inst[k]
    return ''


with dh.Rpc() as r:
    hs = r.call('handshake')
    ptr = hs['game_pointer_size']
    print('    build=%s pointer_size=%s unity=%s'
          % (hs.get('build'), ptr, hs.get('unity_version')))
    rep.check('target is 32-bit', ptr == 4, 'pointer_size=%s' % ptr)

    asms = [a['name'] if isinstance(a, dict) else a
            for a in r.call('getAssemblies').get('assemblies', [])]
    game = [a for a in asms if not any(a.startswith(s) for s in SKIP)]
    print('    game assemblies: %s' % game[:14])

    # ---------------------------------------------------------------- D-1
    print('\n    [D-1] finding instances whose identity whatIsAt itself confirms')
    verified, shown_raw = [], False
    for asm in game:
        if len(verified) >= 3:
            break
        try:
            classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        except Exception:
            continue
        for c in classes:
            if len(verified) >= 3:
                break
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or '<' in nm or '/' in nm:
                continue
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception:
                continue
            flds = [f for f in (ci.get('fields') or [])
                    if not f.get('is_static') and isinstance(f.get('offset'), int)
                    and str(f.get('type')) in FOUR and f['offset'] >= 2 * ptr]
            flds.sort(key=lambda f: f['offset'])
            if not any(flds[i + 1]['offset'] - flds[i]['offset'] == 4
                       for i in range(len(flds) - 1)):
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
                if not shown_raw:
                    shown_raw = True
                    print('      RAW whatIsAt at a candidate base: %s' % json.dumps(w0)[:420])
                cls = class_of(w0)
                # Identity: the reverse lookup must land on THIS class AND at the
                # very start of the object. `fieldOffset` is the offset of the
                # probed address inside the instance whatIsAt resolved - anything
                # non-zero means findInstances handed us an address INSIDE some
                # object, which is what produced the bogus 'misattribution' runs.
                if cls != nm or w0.get('fieldOffset') not in (0, None):
                    continue
                print('      accepted %-38s base=0x%X (whatIsAt: class=%s fieldOffset=%s)'
                      % (('%s.%s' % (asm, nm))[:38], base, cls, w0.get('fieldOffset')))
                verified.append((asm, ns, nm, base, flds))
                break

    if not verified:
        rep.check('D-1 found an instance whose identity whatIsAt confirms', False,
                  'no candidate passed the gate')
    else:
        probes, wrong = 0, []
        for asm, ns, nm, base, flds in verified:
            for i in range(len(flds) - 1):
                f1, f2 = flds[i], flds[i + 1]
                if f2['offset'] - f1['offset'] != 4:
                    continue
                probes += 1
                w = r.call('whatIsAt', {'address': '0x%X' % (base + f2['offset'])})
                got, goff, interior, kind = field_of(w)
                base_name = str(got).split('.')[0]
                ok = base_name == f2['name']
                if not ok:
                    wrong.append((nm, f2['name'], got))
                print('      %-22s +%-4d prev=%-16s want %-18s got %-18s off=%s int=%s %-8s %s'
                      % (nm[:22], f2['offset'], f1['name'][:16], f2['name'], str(got)[:18],
                         goff, interior, kind, 'OK' if ok else 'MISMATCH'))
                if probes >= 12:
                    break
            if probes >= 12:
                break
        rep.check('D-1 boundary address resolves to the owning field, not the previous one',
                  probes > 0 and not wrong, '%d/%d wrong %s' % (len(wrong), probes, wrong[:3]))

    # ---------------------------------------------------------------- D-2
    print('\n    [D-2] 4-byte array walked as Int32 (control) vs IntPtr (fixed path)')
    found = None
    for asm in asms:
        if found:
            break
        try:
            classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        except Exception:
            continue
        for c in classes:
            if found:
                break
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or '<' in nm:
                continue
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception:
                continue
            cand = [f for f in (ci.get('fields') or [])
                    if str(f.get('type')) in ARR4 and not f.get('is_static')]
            if not cand:
                continue
            try:
                fi = r.call('findInstances', {'assembly': asm, 'namespace': ns, 'name': nm,
                                              'limit': 12, 'validate': True})
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
                    if str(f.get('type')) not in ARR4:
                        continue
                    p = f.get('ptr')
                    if not p or int(str(p), 16) == 0:
                        continue
                    arr = int(str(p), 16)
                    hdr = dh.read_mem(pid, arr, 4 * ptr)
                    if len(hdr) < 4 * ptr:
                        continue
                    maxlen = struct.unpack_from('<I', hdr, 3 * 4)[0]
                    if 2 <= maxlen <= 4096:
                        found = (asm, nm, f, arr, maxlen)
                        break
                if found:
                    break
    if not found:
        rep.check('D-2 found a populated 4-byte array', False, 'none in this game')
    else:
        asm, nm, f, arr, maxlen = found
        print('      %s.%s.%s (%s) array=0x%X header max_length=%d'
              % (asm, nm, f.get('name'), f.get('type'), arr, maxlen))

        def walk(et):
            wa = r.call('walkArray', {'address': '0x%X' % arr, 'elementType': et,
                                      'limit': 64})
            vals = []
            for e in (wa.get('elements') or []):
                v = e.get('value') if 'value' in e else e.get('ptr')
                if isinstance(v, str):
                    v = int(v, 16)
                vals.append(v if isinstance(v, int) else None)
            return wa, vals

        wa_i, v_i = walk('System.Int32')
        wa_p, v_p = walk('System.IntPtr')
        n = min(maxlen, 64)
        data = dh.read_mem(pid, arr + 4 * ptr, 4 * n)
        mine = [struct.unpack_from('<I', data, 4 * i)[0] for i in range(len(data) // 4)]
        mine_signed = [struct.unpack_from('<i', data, 4 * i)[0] for i in range(len(data) // 4)]
        print('      Int32  walk: length=%s returned=%d element_size=%s'
              % (wa_i.get('length'), len(v_i), wa_i.get('element_size')))
        print('      IntPtr walk: length=%s returned=%d element_size=%s'
              % (wa_p.get('length'), len(v_p), wa_p.get('element_size')))
        print('      raw   : %s' % ['0x%X' % x for x in mine[:8]])
        print('      Int32 : %s' % v_i[:8])
        print('      IntPtr: %s' % ['0x%X' % x if isinstance(x, int) else x for x in v_p[:8]])

        rep.check('D-2 IntPtr element_size is the pointer size (4), not 8',
                  wa_p.get('element_size') in (4, None),
                  'element_size=%s' % wa_p.get('element_size'))
        rep.check('D-2 IntPtr walk returns as many elements as the Int32 walk',
                  len(v_p) == len(v_i) and wa_p.get('length') == wa_i.get('length'),
                  'IntPtr %d/%s vs Int32 %d/%s (stride 8 would halve it)'
                  % (len(v_p), wa_p.get('length'), len(v_i), wa_i.get('length')))
        k = min(len(v_p), len(mine))
        rep.check('D-2 IntPtr values match a 4-byte-stride read of the same array',
                  k > 0 and all(v_p[j] == mine[j] for j in range(k)),
                  'first differing index %s'
                  % next((j for j in range(k) if v_p[j] != mine[j]), None))
        k2 = min(len(v_i), len(mine_signed))
        rep.check('D-2 control: Int32 values match too', k2 > 0 and
                  all(v_i[j] == mine_signed[j] for j in range(k2)),
                  'first differing index %s'
                  % next((j for j in range(k2) if v_i[j] != mine_signed[j]), None))

sys.exit(rep.summary())
