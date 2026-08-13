"""G-3 paired test. The audit's FAIL signature is specific: a class's FIRST static
field (offset 0) gets ZERO hits *while its other static fields resolve fine*.
Comparing two independent samples cannot show that - many static fields genuinely
have no references (my first run's offset-0 sample was mostly unused asset-pack
scripts: ME_/RFX1_/RFX4_), so the rate difference was sampling bias.

So pair them inside the same class: for every class that has an offset-0 static
field AND at least one non-zero-offset static field, compare hits. The bug is
present iff some class has hits>0 on a non-zero-offset field and 0 on its
offset-0 field.
"""
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
import json
import sys
sys.path.insert(0, _HERE)
import dh

GAME = sys.argv[1] if len(sys.argv) > 1 else 'Utawarerumono'
WANT = int(sys.argv[2]) if len(sys.argv) > 2 else 14
pid, pname = dh.find_pid(GAME)
rep = dh.Report('G-3 paired (same class, offset-0 vs sibling) on %s' % pname)
SKIP = ('System', 'mscorlib', 'netstandard', 'Mono.', 'Unity', 'Microsoft',
        'Newtonsoft', 'I18N', 'Il2Cpp')
# asset packs that ship unused in this title - they make both columns zero and
# tell us nothing, so skip them to spend the budget on live game code
NOISE = ('ME_', 'RFX1_', 'RFX4_', 'MB3_', 'Voxelize', 'AudioSpectrum')


def refs(r, asm, ns, cls, field):
    res = r.call('findFieldReferences', {'assembly': asm, 'ns': ns, 'class': cls,
                                         'fieldName': field, 'kind': 'any', 'limit': 300})
    rows = res.get('references') or []
    pairs = [(str(x.get('address')), str(x.get('hitAddress'))) for x in rows]
    dup = len(pairs) - len(set(pairs))
    return len(rows), dup, res.get('scanned_methods'), res.get('truncated'), res.get('note')


with dh.Rpc() as r:
    hs = r.call('handshake')
    print('    backend=%s build=%s metadata_v=%s' %
          (hs.get('backend'), hs.get('build'), hs.get('metadata_version')))
    asms = [a['name'] if isinstance(a, dict) else a
            for a in r.call('getAssemblies').get('assemblies', [])]
    game = [a for a in asms if not any(a.startswith(s) for s in SKIP)]

    pairs_found, total_dup = [], 0
    for asm in game:
        if len(pairs_found) >= WANT:
            break
        try:
            classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        except Exception:
            continue
        for c in classes:
            if len(pairs_found) >= WANT:
                break
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or '<' in nm or any(nm.startswith(x) for x in NOISE):
                continue
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception:
                continue
            stat = [f for f in (ci.get('fields') or [])
                    if (f.get('static') or f.get('is_static'))
                    and isinstance(f.get('offset'), int)
                    and not (int(f.get('flags') or 0) & 0x40)]
            if len(stat) < 2:
                continue
            stat.sort(key=lambda f: f['offset'])
            if stat[0]['offset'] != 0:
                continue
            sib = next((f for f in stat[1:] if f['offset'] > 0), None)
            if sib is None:
                continue
            try:
                h0, d0, sm0, t0, n0 = refs(r, asm, ns, nm, stat[0]['name'])
                hs_, ds, sms, ts, ns_ = refs(r, asm, ns, nm, sib['name'])
            except Exception as e:
                print('      %-40s ERROR %s' % (nm[:40], e))
                continue
            total_dup += d0 + ds
            verdict = ('BUG SHAPE' if (hs_ > 0 and h0 == 0)
                       else ('both zero' if (h0 == 0 and hs_ == 0) else 'ok'))
            pairs_found.append((nm, h0, hs_, sib['offset'], verdict))
            print('      %-34s off0 %-18s hits=%-4d | sibling +%-4s %-18s hits=%-4d  %s'
                  % (nm[:34], stat[0]['name'][:18], h0, sib['offset'],
                     sib['name'][:18], hs_, verdict))
            if n0:
                print('        note(off0): %s' % str(n0)[:160])

    informative = [p for p in pairs_found if p[4] != 'both zero']
    bug = [p for p in pairs_found if p[4] == 'BUG SHAPE']
    print('\n    pairs=%d informative=%d (sibling had hits) bug-shaped=%d'
          % (len(pairs_found), len(informative), len(bug)))
    for p in bug:
        print('      BUG SHAPE %s: off0=%d sibling=%d' % (p[0], p[1], p[2]))
    rep.check('G-3 got at least one informative pair', len(informative) > 0,
              '%d pairs, %d informative' % (len(pairs_found), len(informative)))
    rep.check('G-3 no class has hits on a sibling but zero on its offset-0 field',
              not bug, 'bug-shaped pairs: %s' % [(p[0], p[1], p[2]) for p in bug[:4]])
    rep.check('G-4 still zero duplicate rows across this batch', total_dup == 0,
              'dupes=%d' % total_dup)

sys.exit(rep.summary())
