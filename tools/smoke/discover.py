import json, sys, time
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
sys.path.insert(0, _HERE)
import dh

ASM = sys.argv[1] if len(sys.argv) > 1 else 'Assembly-CSharp'

with dh.Rpc() as r:
    asms = r.call('getAssemblies').get('assemblies', [])
    names = [a['name'] if isinstance(a, dict) else a for a in asms]
    cand_asm = [n for n in names if 'Assembly-CSharp' in n or n in ('Elin', 'MainGame')]
    print('game-ish assemblies:', cand_asm[:10])
    asm = ASM if ASM in names else (cand_asm[0] if cand_asm else names[0])
    print('using assembly:', asm)

    cls = r.call('getClasses', {'assembly': asm})
    lst = cls.get('classes', [])
    print('classes: %d' % len(lst))
    print('sample keys:', list(lst[0].keys()) if lst else None)

    # find classes with live instances and an int32 field holding a distinctive value
    tried = 0
    hits = []
    for c in lst:
        if tried >= 60 or len(hits) >= 3:
            break
        ns = c.get('namespace', '')
        nm = c.get('name', '')
        if not nm or c.get('is_enum') or c.get('is_value_type'):
            continue
        tried += 1
        try:
            fi = r.call('findInstances', {'assembly': asm, 'namespace': ns, 'name': nm,
                                          'limit': 6, 'validate': True, 'sane': True})
        except Exception as e:
            continue
        inst = fi.get('instances') or fi.get('matches') or []
        if not inst:
            continue
        addr = inst[0]['address'] if isinstance(inst[0], dict) else inst[0]
        try:
            w = r.call('walkInstance', {'address': addr, 'assembly': asm,
                                        'namespace': ns, 'name': nm})
        except Exception:
            continue
        flds = w.get('fields') or []
        ints = [f for f in flds
                if str(f.get('type', '')).endswith('Int32')
                and isinstance(f.get('value'), int)
                and 1000 <= abs(f.get('value')) < 2_000_000_000]
        print('  %-46s instances=%d int32-fields=%d' % ((ns + '.' + nm)[:46], len(inst), len(ints)))
        if ints:
            hits.append({'assembly': asm, 'namespace': ns, 'name': nm,
                         'address': addr, 'instances': len(inst),
                         'field': ints[0].get('name'), 'offset': ints[0].get('offset'),
                         'value': ints[0].get('value'),
                         'all_ints': [(f.get('name'), f.get('offset'), f.get('value')) for f in ints[:6]]})
    print()
    print(json.dumps(hits, indent=2)[:3000])
    with open(_HERE + r'\target.json', 'w') as f:
        json.dump(hits, f, indent=2)
