"""G4 (IL2CPP) headless:
   G-3  a static field at OFFSET 0 must still produce chain-detection hits.
        x86 encodes no displacement for offset 0, so the old `disp.size == 0`
        guard threw those candidates away before the comparison ever ran - the
        symptom is zero hits for a class's FIRST static field while its other
        static fields resolve fine. Reported as a rate: offset-0 fields vs
        non-zero-offset controls.
   G-4  findFieldReferences result hygiene: no duplicate rows. The old REX
        de-dup guessed from buf[p-1]; a preceding byte in 0x40..0x4F is not
        necessarily this instruction's prefix, so ~1/16 of legitimate hits were
        dropped and pairs could survive. The fix de-dups on (target, hitIP), so
        the observable is: zero duplicate (address, hitAddress) pairs.
"""
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
import json
import sys
sys.path.insert(0, _HERE)
import dh

GAME = sys.argv[1] if len(sys.argv) > 1 else 'Utawarerumono'
BUDGET = int(sys.argv[2]) if len(sys.argv) > 2 else 14
pid, pname = dh.find_pid(GAME)
rep = dh.Report('G4 (IL2CPP) on %s (pid %s)' % (pname, pid))
SKIP = ('System', 'mscorlib', 'netstandard', 'Mono.', 'Unity', 'Microsoft',
        'Newtonsoft', 'I18N', 'Il2Cpp')


def refs(r, asm, ns, cls, field, limit=300):
    return r.call('findFieldReferences', {'assembly': asm, 'ns': ns, 'class': cls,
                                          'fieldName': field, 'kind': 'any',
                                          'limit': limit})


with dh.Rpc() as r:
    hs = r.call('handshake')
    print('    backend=%s build=%s metadata_v=%s unity=%s ptr=%s'
          % (hs.get('backend'), hs.get('build'), hs.get('metadata_version'),
             hs.get('unity_version'), hs.get('game_pointer_size')))
    rep.check('target is IL2CPP', hs.get('backend') == 'IL2CPP',
              'backend=%s' % hs.get('backend'))

    asms = [a['name'] if isinstance(a, dict) else a
            for a in r.call('getAssemblies').get('assemblies', [])]
    game = [a for a in asms if not any(a.startswith(s) for s in SKIP)]
    print('    game assemblies: %s' % game[:10])

    zero, nonzero = [], []          # (asm, ns, cls, fieldname, offset)
    for asm in game:
        if len(zero) >= BUDGET and len(nonzero) >= BUDGET:
            break
        try:
            classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        except Exception:
            continue
        for c in classes:
            if len(zero) >= BUDGET and len(nonzero) >= BUDGET:
                break
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or '<' in nm:
                continue
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception:
                continue
            # IL2CPP wire key is `static` (not `is_static`), and enum members are
            # LITERAL constants (FieldAttributes.Literal = 0x40) with no storage -
            # they are 'static' but are not what chain detection can find.
            stat = [f for f in (ci.get('fields') or [])
                    if (f.get('static') or f.get('is_static'))
                    and isinstance(f.get('offset'), int)
                    and not (int(f.get('flags') or 0) & 0x40)]
            if not stat:
                continue
            stat.sort(key=lambda f: f['offset'])
            if stat[0]['offset'] == 0 and len(zero) < BUDGET:
                zero.append((asm, ns, nm, stat[0]['name'], 0))
            for f in stat[1:]:
                if f['offset'] > 0 and len(nonzero) < BUDGET:
                    nonzero.append((asm, ns, nm, f['name'], f['offset']))
                    break

    print('    sampling %d offset-0 static fields and %d non-zero controls'
          % (len(zero), len(nonzero)))

    all_rows, dupes, shown = [], [], False

    def run(group, label):
        hitcount = 0
        for asm, ns, cls, fname, off in group:
            try:
                res = refs(r, asm, ns, cls, fname)
            except Exception as e:
                print('      %-52s ERROR %s' % (('%s.%s' % (cls, fname))[:52], e))
                continue
            rows = res.get('references') or res.get('rows') or res.get('matches') or []
            global shown
            if not shown:
                shown = True
                print('      RAW findFieldReferences keys: %s' % list(res.keys()))
                if rows:
                    print('        row: %s' % json.dumps(rows[0])[:300])
            pairs = [(str(x.get('address')), str(x.get('hitAddress'))) for x in rows]
            seen, d = set(), 0
            for pr in pairs:
                if pr in seen:
                    d += 1
                seen.add(pr)
            if d:
                dupes.append((cls, fname, d, len(pairs)))
            all_rows.extend(pairs)
            if rows:
                hitcount += 1
            print('      %-14s %-46s off=%-6s hits=%-4s dup=%d'
                  % (label, ('%s.%s' % (cls, fname))[:46], off, len(rows), d))
        return hitcount

    print('\n    [G-3] static fields at offset 0')
    z_hits = run(zero, 'offset-0')
    print('\n    [G-3 control] static fields at non-zero offsets')
    n_hits = run(nonzero, 'control')

    zr = (z_hits / len(zero)) if zero else 0
    nr = (n_hits / len(nonzero)) if nonzero else 0
    print('\n    offset-0 fields with >=1 hit: %d/%d (%.0f%%)   control: %d/%d (%.0f%%)'
          % (z_hits, len(zero), 100 * zr, n_hits, len(nonzero), 100 * nr))
    rep.check('G-3 offset-0 static fields do produce hits', z_hits > 0,
              '%d/%d had hits (pre-fix symptom: 0 while controls work)' % (z_hits, len(zero)))
    rep.check('G-3 offset-0 hit rate is not collapsed vs the controls',
              len(zero) == 0 or len(nonzero) == 0 or zr >= 0.5 * nr,
              'offset-0 %.0f%% vs control %.0f%%' % (100 * zr, 100 * nr))
    rep.check('G-4 no duplicate (address, hitAddress) rows', not dupes,
              'dupes=%s' % dupes[:4])
    print('    total reference rows collected: %d' % len(all_rows))

sys.exit(rep.summary())
