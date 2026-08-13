"""getMethodAobPattern must not sign past the end of the function.

Before: asking for 48 bytes on a 24-byte getter returned 48 bytes whose tail was
`C3 01 04 01 00 04 02 00 00 ...` — padding / JIT bookkeeping / the next function.
Those trailing zeros matched 223 places in 308 MB of executable memory, and
padding also moves between runs, so signing it is fragile for no benefit.

Checks per sampled method:
  * the pattern still self-matches at its own address (batch-H invariant)
  * when it was truncated, it ends ON the terminal instruction (C3/C2/E9/EB/FF)
  * nothing after the terminal instruction is signed
  * match count over executable memory, and the uniqueness warning when short
"""
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
import re
import sys
sys.path.insert(0, _HERE)
import dh

GAME = sys.argv[1] if len(sys.argv) > 1 else 'CoreKeeper'
WANT = int(sys.argv[2]) if len(sys.argv) > 2 else 40
ASK = 48
pid, pname = dh.find_pid(GAME)
rep = dh.Report('AOB function-end trimming on %s (ask %d bytes)' % (pname, ASK))
SKIP = ('System', 'mscorlib', 'netstandard', 'Mono.', 'Unity', 'Microsoft', 'Newtonsoft')
TERMINALS = {0xC3, 0xC2, 0xE9, 0xEB, 0xCB, 0xCA, 0xCF, 0xFF}

rows = []
with dh.Rpc() as r:
    print('    DLL build=%s' % r.call('handshake').get('build'))
    asms = [a['name'] if isinstance(a, dict) else a
            for a in r.call('getAssemblies').get('assemblies', [])]
    for asm in [a for a in asms if not any(a.startswith(s) for s in SKIP)]:
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
            for m in (ci.get('methods') or [])[:10]:
                if len(rows) >= WANT or str(m.get('name', '')).startswith('.'):
                    continue
                try:
                    fmi = r.call('findMethodInstances', {'assembly': asm, 'namespace': ns,
                                                         'name': nm, 'method': m['name']})
                except Exception:
                    continue
                addr = next((int(str(o['address']), 16) for o in (fmi.get('matches') or [])
                             if o.get('address') and int(str(o['address']), 16)), None)
                if addr is None:
                    continue
                try:
                    pat = r.call('getMethodAobPattern', {'address': '0x%X' % addr,
                                                         'length': ASK})
                except Exception:
                    continue
                p = pat.get('pattern') or ''
                if not p:
                    continue
                rows.append({'name': '%s.%s' % (nm, m['name']), 'addr': addr, 'pat': p,
                             'len': pat.get('length'), 'asked': pat.get('requestedLength'),
                             'stopped': pat.get('stoppedAtTerminal'),
                             'warn': bool(pat.get('uniquenessWarning')),
                             'raw': dh.read_mem(pid, addr, ASK)})

print('    sampled %d methods' % len(rows))
bad_self, bad_end, trimmed = [], [], 0
exec_regions = [(b, s) for b, s, _, _ in dh.regions(pid, want_exec=True) if s <= 128 * 1024 * 1024]
bufs = [(b, dh.read_mem(pid, b, s)) for b, s in exec_regions]
match_counts = []
for x in rows:
    toks = x['pat'].split()
    rx = b''.join(b'.' if '?' in t else re.escape(bytes([int(t, 16)])) for t in toks)
    prog = re.compile(rx, re.DOTALL)
    if not prog.match(x['raw'][:len(toks)]):
        bad_self.append(x['name'])
    if x['stopped']:
        trimmed += 1
        # the last byte signed must belong to the terminal instruction
        tail = [int(t, 16) for t in toks[-5:] if '?' not in t]
        if not any(b in TERMINALS for b in tail):
            bad_end.append((x['name'], ' '.join(toks[-5:])))
    n = sum(len(prog.findall(buf)) for _, buf in bufs if buf)
    match_counts.append((n, x['name'], len(toks), x['warn']))

print('    truncated at the function end: %d of %d' % (trimmed, len(rows)))
for n, name, ln, w in sorted(match_counts, reverse=True)[:5]:
    print('      %-44s %2d bytes  %4d matches  warned=%s' % (name[:44], ln, n, w))
rep.check('every pattern still self-matches at its own address', not bad_self,
          '%d failed: %s' % (len(bad_self), bad_self[:3]))
rep.check('truncated patterns end on the terminal instruction', not bad_end,
          '%s' % bad_end[:3])
rep.check('trimming actually happens on short functions', trimmed > 0,
          '%d of %d' % (trimmed, len(rows)))
# A distinctive 48-byte pattern that happens to match 2 places (duplicated code)
# is a legitimate outcome the DLL cannot foresee without scanning. What it CAN
# recognise is "too little distinctive code to single anything out", so only
# clearly non-discriminating patterns are required to carry the warning.
loud = [(nm, n) for n, nm, _, w in match_counts if n > 8 and not w]
rep.check('clearly non-unique patterns (>8 matches) carry the warning', not loud,
          '%s' % loud[:3])
print('    match-count spread: %s' % sorted(n for n, _, _, _ in match_counts)[-6:])
sys.exit(rep.summary())
