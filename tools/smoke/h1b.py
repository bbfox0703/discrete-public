"""H-1 at scale: the batch-H fix is about the AOB decoder keeping instruction
boundaries (group 3 / F6-F7 immediates were skipped, desyncing everything after).

Signal: for every sampled method, the emitted pattern must still MATCH the bytes
at that method's own address. A desynced decoder puts wildcards on the wrong
bytes, so the pattern stops matching itself. Reported separately for the subset
whose window actually contains an F6/F7 opcode.
"""
import json, os, re, sys
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
sys.path.insert(0, _HERE)
import dh

GAME = sys.argv[1] if len(sys.argv) > 1 else 'CoreKeeper'
WANT = int(sys.argv[2]) if len(sys.argv) > 2 else 120
LEN = 32
pid, pname = dh.find_pid(GAME)
rep = dh.Report('H-1 decoder boundaries on %s (pid %s), %d methods x %d bytes'
                % (pname, pid, WANT, LEN))

SKIP = ('System', 'mscorlib', 'netstandard', 'Mono.', 'Unity.', 'UnityEngine',
        'Microsoft', 'Newtonsoft', 'Sentry', 'nunit', 'JetBrains')

rows = []
with dh.Rpc() as r:
    asms = [a['name'] if isinstance(a, dict) else a
            for a in r.call('getAssemblies').get('assemblies', [])]
    asms = [a for a in asms if not any(a.startswith(s) for s in SKIP)]
    for asm in asms:
        if len(rows) >= WANT:
            break
        try:
            classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        except Exception:
            continue
        for c in classes:
            if len(rows) >= WANT:
                break
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or '<' in nm:
                continue
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception:
                continue
            for m in (ci.get('methods') or [])[:12]:
                if len(rows) >= WANT:
                    break
                mn = m.get('name') or ''
                if mn.startswith('.'):
                    continue
                try:
                    fmi = r.call('findMethodInstances', {'assembly': asm, 'namespace': ns,
                                                         'name': nm, 'method': mn})
                except Exception:
                    continue
                addr = None
                for o in (fmi.get('matches') or []):
                    a = o.get('address')
                    if a and int(str(a), 16):
                        addr = int(str(a), 16)
                        break
                if addr is None:
                    continue
                try:
                    pat = r.call('getMethodAobPattern', {'address': '0x%X' % addr, 'length': LEN})
                except Exception:
                    continue
                p = pat.get('pattern') or ''
                if not p:
                    continue
                raw = dh.read_mem(pid, addr, LEN)
                if len(raw) < LEN:
                    continue
                toks = p.split()
                rx = b''.join(b'.' if '?' in t else re.escape(bytes([int(t, 16)])) for t in toks)
                self_match = re.compile(rx, re.DOTALL).match(raw) is not None
                # literal (non-wildcard) bytes must equal the real bytes one-for-one
                literal_ok = all('?' in t or raw[i] == int(t, 16)
                                 for i, t in enumerate(toks) if i < len(raw))
                rows.append({
                    'name': '%s.%s.%s' % (asm, nm, mn), 'addr': addr,
                    'wild': sum(1 for t in toks if '?' in t), 'n': len(toks),
                    'self_match': self_match, 'literal_ok': literal_ok,
                    'has_f6f7': any(b in (0xF6, 0xF7) for b in raw),
                    'bailed': pat.get('decoderBailed'),
                    'stats': pat.get('stats'),
                })

print('    sampled %d methods' % len(rows))
bad = [x for x in rows if not x['self_match']]
badlit = [x for x in rows if not x['literal_ok']]
f6 = [x for x in rows if x['has_f6f7']]
f6bad = [x for x in f6 if not x['self_match']]
bailed = [x for x in rows if x['bailed']]
wilds = [x['wild'] for x in rows]
print('    wildcards per 32-byte pattern: min=%d max=%d avg=%.1f  (0 everywhere would be suspicious)'
      % (min(wilds), max(wilds), sum(wilds) / len(wilds)))
print('    decoderBailed: %d' % len(bailed))
print('    windows containing an F6/F7 opcode: %d' % len(f6))
for x in f6[:6]:
    print('      %-64s wild=%d self_match=%s' % (x['name'][:64], x['wild'], x['self_match']))
if bad:
    for x in bad[:8]:
        print('      MISMATCH %s (wild=%d)' % (x['name'], x['wild']))

rep.check('every emitted pattern matches its own method bytes', not bad,
          '%d/%d mismatched' % (len(bad), len(rows)))
rep.check('literal bytes are byte-exact (no boundary desync)', not badlit,
          '%d/%d wrong' % (len(badlit), len(rows)))
rep.check('F6/F7 subset also self-matches (the batch-H fix)',
          len(f6) > 0 and not f6bad,
          'subset=%d mismatched=%d' % (len(f6), len(f6bad)))
json.dump(rows, open(os.path.join(
    _HERE,
    'h1_rows.json'), 'w'), indent=1)
sys.exit(rep.summary())
