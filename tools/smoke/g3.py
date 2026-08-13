"""G3 (32-bit Mono) headless:
   D-1  whatIsAt must attribute an address to the field that owns it, not the
        previous one (pre-fix: unresolved field size fell back to 8 on x86)
   D-2  IntPtr[]/UIntPtr[] element stride must be 4 on x86 (pre-fix: 8 -> half
        the elements, values wrong, reads past the end)
   31-5 bonus: whatIsAt inside a value-struct field should name the component
"""
import json, os, re, sys, struct
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
sys.path.insert(0, _HERE)
import dh

SC = _HERE
GAME = sys.argv[1] if len(sys.argv) > 1 else 'Mortal'
pid, pname = dh.find_pid(GAME)
rep = dh.Report('G3 (32-bit Mono) on %s (pid %s)' % (pname, pid))

SKIP = ('System', 'mscorlib', 'netstandard', 'Mono.', 'Unity', 'Microsoft',
        'Newtonsoft', 'AK.', 'Ak.', 'DOTween', 'NaughtyAttributes', 'com.')
FOUR = ('System.Int32', 'System.UInt32', 'System.Single', 'System.Boolean',
        'System.Byte', 'System.Int16', 'System.UInt16')

with dh.Rpc() as r:
    hs = r.call('handshake')
    print('    build=%s pointer_size=%s unity=%s'
          % (hs.get('build'), hs.get('game_pointer_size'), hs.get('unity_version')))
    rep.check('target really is 32-bit', hs.get('game_pointer_size') == 4,
              'pointer_size=%s' % hs.get('game_pointer_size'))

    asms = [a['name'] if isinstance(a, dict) else a
            for a in r.call('getAssemblies').get('assemblies', [])]
    game = [a for a in asms if not any(a.startswith(s) for s in SKIP)]
    print('    game-ish assemblies: %s' % game[:12])

    # ------------------------------------------------------------- D-1
    print('\n[D-1] whatIsAt attribution across adjacent 4-byte fields')
    probes, wrong, examples = 0, [], []
    for asm in game:
        if probes >= 40:
            break
        try:
            classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        except Exception:
            continue
        for c in classes:
            if probes >= 40:
                break
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or '<' in nm:
                continue
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception:
                continue
            flds = [f for f in (ci.get('fields') or [])
                    if not f.get('is_static') and isinstance(f.get('offset'), int)]
            flds.sort(key=lambda f: f['offset'])
            pairs = [(flds[i], flds[i + 1]) for i in range(len(flds) - 1)
                     if flds[i + 1]['offset'] - flds[i]['offset'] == 4
                     and str(flds[i].get('type')) in FOUR]
            if not pairs:
                continue
            try:
                fi = r.call('findInstances', {'assembly': asm, 'namespace': ns, 'name': nm,
                                              'limit': 3, 'validate': True, 'sane': True})
            except Exception:
                continue
            inst = fi.get('instances') or fi.get('matches') or []
            if not inst:
                continue
            base = int(str(inst[0]['address'] if isinstance(inst[0], dict) else inst[0]), 16)
            for f1, f2 in pairs[:3]:
                if probes >= 40:
                    break
                probes += 1
                try:
                    w = r.call('whatIsAt', {'address': '0x%X' % (base + f2['offset'])})
                except Exception as e:
                    continue
                got = ((w.get('field') or {}).get('name') if isinstance(w.get('field'), dict)
                       else w.get('field')) or w.get('fieldName') or ''
                gotbase = str(got).split('.')[0]
                ok = gotbase == f2['name']
                if not ok:
                    wrong.append((nm, f1['name'], f2['name'], got))
                if len(examples) < 6:
                    examples.append('%s: +%d %s(%s) then +%d %s -> whatIsAt says %r %s'
                                    % (nm[:22], f1['offset'], f1['name'], f1['type'].split('.')[-1],
                                       f2['offset'], f2['name'], got, 'OK' if ok else 'WRONG'))
    for e in examples:
        print('      ' + e)
    print('      probed %d adjacent-field boundaries, %d misattributed' % (probes, len(wrong)))
    for x in wrong[:8]:
        print('      WRONG %s: asked for %s, got %r (prev field %s)' % (x[0], x[2], x[3], x[1]))
    rep.check('D-1 no address attributed to the previous field', probes > 0 and not wrong,
              '%d/%d wrong' % (len(wrong), probes))

    # ------------------------------------------------------------- D-2
    print('\n[D-2] IntPtr[] / UIntPtr[] element stride on x86')
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
            ptrarr = [f for f in (ci.get('fields') or [])
                      if 'IntPtr[]' in str(f.get('type', '')) and not f.get('is_static')]
            if not ptrarr:
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
                    if 'IntPtr[]' in str(f.get('type', '')) and f.get('ptr') \
                            and int(str(f['ptr']), 16) != 0:
                        found = (asm, ns, nm, f)
                        break
                if found:
                    break
    if not found:
        print('      no live non-null IntPtr[] field found in this game')
        rep.check('D-2 found a live IntPtr[] to verify', False, 'none present')
    else:
        asm, ns, nm, f = found
        arr = int(str(f['ptr']), 16)
        print('      %s.%s.%s -> array at 0x%X' % (asm, nm, f.get('name'), arr))
        wa = r.call('walkArray', {'address': '0x%X' % arr, 'elementType': 'System.IntPtr',
                                  'limit': 64})
        els = wa.get('elements') or []
        print('      walkArray: length=%s returned=%d element_size=%s'
              % (wa.get('length'), len(els), wa.get('element_size')))
        # MonoArray on x86: [vtable][sync][bounds][max_length] then data
        hdr = dh.read_mem(pid, arr, 32)
        maxlen = struct.unpack_from('<I', hdr, 12)[0] if len(hdr) >= 16 else -1
        data = dh.read_mem(pid, arr + 16, max(4, 4 * min(64, max(0, maxlen))))
        mine = [struct.unpack_from('<I', data, 4 * i)[0]
                for i in range(min(len(data) // 4, min(64, maxlen)))]
        theirs = [int(str(e.get('ptr') or e.get('value') or 0), 16)
                  if isinstance(e.get('ptr') or e.get('value'), str)
                  else (e.get('ptr') or e.get('value') or 0) for e in els]
        print('      header max_length=%d' % maxlen)
        print('      mine  : %s' % ['0x%X' % v for v in mine[:8]])
        print('      theirs: %s' % ['0x%X' % v if isinstance(v, int) else v for v in theirs[:8]])
        rep.check('D-2 element count matches the array header',
                  wa.get('length') == maxlen, 'walk=%s header=%s' % (wa.get('length'), maxlen))
        rep.check('D-2 element values match a 4-byte-stride read',
                  mine[:len(theirs)] == theirs[:len(mine)],
                  'first mismatch at %s' % next((i for i, (a2, b2) in enumerate(zip(mine, theirs))
                                                 if a2 != b2), None))

    # ------------------------------------------------------------ 31-5
    print('\n[31-5 bonus] whatIsAt inside a value-struct field names the component')
    named = []
    for asm in game[:6]:
        if named:
            break
        try:
            classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        except Exception:
            continue
        for c in classes:
            if named:
                break
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or '<' in nm:
                continue
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception:
                continue
            v3 = [f for f in (ci.get('fields') or [])
                  if str(f.get('type', '')).endswith(('Vector3', 'Vector2', 'Quaternion'))
                  and not f.get('is_static')]
            if not v3:
                continue
            try:
                fi = r.call('findInstances', {'assembly': asm, 'namespace': ns, 'name': nm,
                                              'limit': 2, 'validate': True, 'sane': True})
            except Exception:
                continue
            inst = fi.get('instances') or fi.get('matches') or []
            if not inst:
                continue
            base = int(str(inst[0]['address'] if isinstance(inst[0], dict) else inst[0]), 16)
            f = v3[0]
            try:
                w = r.call('whatIsAt', {'address': '0x%X' % (base + f['offset'] + 4)})
            except Exception:
                continue
            got = ((w.get('field') or {}).get('name') if isinstance(w.get('field'), dict)
                   else w.get('field')) or ''
            print('      %s.%s (%s) at +%d+4 -> %r' % (nm, f['name'], f['type'].split('.')[-1],
                                                       f['offset'], got))
            named.append(('.' in str(got), got))
    if named:
        rep.check('31-5 struct component is named (e.g. field.y)', named[0][0],
                  'got %r' % named[0][1])
    else:
        print('      no suitable Vector3/Quaternion field with instances found')

sys.exit(rep.summary())
