"""Rust core R1 + R2 live A/B smoke (docs/rust-core.md "R2 gate").

Run once against a game with the default (Rust) DLLs and once with a legacy
build (-DDISCRETE_RUST_R2=OFF -DDISCRETE_RUST_METADATA=OFF), then diff the two
JSON files with --compare. Everything recorded in the "ab" section is
restart-stable (names, offsets, module-relative addresses), so the two runs
must match exactly. The "live" section holds per-run PASS/FAIL checks of the
value-scan refine comparator and the group-scan matcher, which work on heap
addresses that change between runs.

    python -u r2_live.py run <exe-substring> <out.json> [assembly]
    python -u r2_live.py compare <a.json> <b.json>

What each part exercises:
  metadata (R1)   getClasses / getClassInfo over the first N classes (field
                  names, types, offsets, static flags, method names)
  xref (R2 chain) findFieldReferences on static fields -> IL2CPP chain
                  detector (check the walk log for "FieldXref: v2 scan complete");
                  on Mono this is the IL scan (not Rust), and the metadata-parser
                  RPCs are skipped. Mono JIT method addresses are not
                  restart-stable, so an A/B of Mono AOBs is not meaningful
  aob (R2 aob)    getMethodAobPattern on methods found via findMethodInstances,
                  both stopAtTerminal settings; plus a self-match check
  value (R2)      beginValueScan / refineValueScan on a field we set to a unique
                  value with WriteProcessMemory (restored afterwards)
  group (R2 gm)   beginGroupScan / refineGroupScan on two such fields

The value/group part WRITES two int32 fields of one live object and restores
them. Use a title screen or a throwaway save.
"""
import ctypes
import hashlib
import json
import re
import sys
import time
import os as _os
from ctypes import wintypes

_HERE = _os.path.dirname(_os.path.abspath(__file__))
sys.path.insert(0, _HERE)
import dh  # noqa: E402

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
PROCESS_VM_OPERATION, PROCESS_VM_WRITE = 0x0008, 0x0020
TH32CS_SNAPMODULE, TH32CS_SNAPMODULE32 = 0x8, 0x10

N_CLASSES = 400         # metadata sample
N_XREF_FIELDS = 40      # static fields to xref
N_AOB_METHODS = 150     # methods to pattern
AOB_LEN = 48


class MODULEENTRY32W(ctypes.Structure):
    _fields_ = [('dwSize', wintypes.DWORD), ('th32ModuleID', wintypes.DWORD),
                ('th32ProcessID', wintypes.DWORD), ('GlblcntUsage', wintypes.DWORD),
                ('ProccntUsage', wintypes.DWORD), ('modBaseAddr', ctypes.c_void_p),
                ('modBaseSize', wintypes.DWORD), ('hModule', wintypes.HMODULE),
                ('szModule', ctypes.c_wchar * 256), ('szExePath', ctypes.c_wchar * 260)]


def module_range(pid, name):
    k32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)
    me = MODULEENTRY32W()
    me.dwSize = ctypes.sizeof(me)
    try:
        ok = k32.Module32FirstW(snap, ctypes.byref(me))
        while ok:
            if me.szModule.lower() == name.lower():
                return me.modBaseAddr, me.modBaseSize
            ok = k32.Module32NextW(snap, ctypes.byref(me))
    finally:
        k32.CloseHandle(snap)
    return None, None


def write_i32(pid, addr, value):
    h = k32.OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_WRITE, False, pid)
    if not h:
        raise OSError('OpenProcess(write) failed: %d' % ctypes.get_last_error())
    try:
        buf = ctypes.c_int32(value)
        n = ctypes.c_size_t(0)
        if not k32.WriteProcessMemory(h, ctypes.c_void_p(addr), ctypes.byref(buf), 4, ctypes.byref(n)):
            raise OSError('WriteProcessMemory failed: %d' % ctypes.get_last_error())
    finally:
        k32.CloseHandle(h)


def read_i32(pid, addr):
    b = dh.read_mem(pid, addr, 4)
    return int.from_bytes(b, 'little', signed=True) if len(b) == 4 else None


def hx(v):
    if v is None:
        return None
    return int(str(v), 16) if isinstance(v, str) else int(v)


def is_static(f):
    for k in ('isStatic', 'is_static', 'static'):
        if k in f:
            return bool(f[k])
    return False


def tc(r, cmd, args=None):
    """r.call() that turns an RPC error into {'error': text} (stable for diffing)."""
    ok, payload = r.try_call(cmd, args)
    return payload if ok else {'error': str(payload)[:200]}


# --------------------------------------------------------------------------
def run(game, out_path, asm_want):
    pid, pname = dh.find_pid(game)
    if not pid:
        print('process not found: %s' % game)
        return 2
    if not dh.wait_for_pipe(180):
        print('pipe never came up')
        return 2
    base, size = module_range(pid, 'GameAssembly.dll')
    rel = (lambda a: None if a is None else ('+0x%X' % (a - base) if base and base <= a < base + size
                                             else 'abs'))
    out = {'game': pname, 'ab': {}, 'live': {}, 'timing': {}}
    rep = dh.Report('R1+R2 live smoke on %s (pid %s)' % (pname, pid))

    with dh.Rpc(timeout=600.0, label='r2live') as r:
        hs = r.call('handshake')
        out['handshake'] = hs
        print('handshake:', json.dumps(hs)[:300])
        # Many games pause when unfocused (runInBackground = false), which
        # stalls every main-thread RPC while this script runs in the
        # background. The DLL's keep-foreground hook keeps the game ticking;
        # switched off again at the end.
        kf = tc(r, 'setKeepForeground', {'enable': True})
        print('keep-foreground:', kf)
        asms = sorted(a['name'] if isinstance(a, dict) else a
                      for a in r.call('getAssemblies').get('assemblies', []))
        out['ab']['assemblies'] = asms
        asm = asm_want if asm_want in asms else next((a for a in asms if 'Assembly-CSharp' == a), asms[0])
        out['assembly'] = asm

        # ---- R1: metadata-backed class info --------------------------------
        t0 = time.time()
        classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        classes = sorted(classes, key=lambda c: (c.get('namespace', ''), c.get('name', '')))
        out['ab']['class_count'] = len(classes)
        md, statics, method_sites, enum_sites = [], [], [], []
        for c in classes[:N_CLASSES]:
            ns, nm = c.get('namespace', ''), c.get('name', '')
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception as e:  # noqa: BLE001
                md.append({'c': ns + '.' + nm, 'error': str(e)[:120]})
                continue
            flds = ci.get('fields') or []
            meths = ci.get('methods') or []
            md.append({'c': ns + '.' + nm,
                       'f': [[f.get('name'), f.get('type'), f.get('offset'), is_static(f),
                              f.get('defaultValue', f.get('default'))] for f in flds],
                       'm': sorted(m.get('name') or '' for m in meths)})
            if any(f.get('name') == 'value__' for f in flds):
                enum_sites.append((ns, nm))
            elif '<' not in nm:
                for f in flds:
                    if is_static(f) and len(statics) < N_XREF_FIELDS * 3:
                        statics.append((ns, nm, f.get('name')))
                for m in meths[:6]:
                    mn = m.get('name') or ''
                    if mn and not mn.startswith('.'):
                        method_sites.append((ns, nm, mn))
        out['ab']['metadata'] = md
        out['ab']['metadata_sha'] = hashlib.sha256(json.dumps(md, sort_keys=True).encode()).hexdigest()
        out['timing']['metadata_s'] = round(time.time() - t0, 1)
        rep.check('metadata: getClassInfo answered for %d classes' % len(md),
                  sum(1 for x in md if 'error' not in x) > 0)

        # ---- R1: RPCs backed by the global-metadata.dat parser -------------
        # IL2CPP only: Mono has no global-metadata.dat (and no chain detector;
        # on Mono findFieldReferences is the IL scan, so only the shared AOB /
        # value-refine / group-match code is Rust there).
        il2cpp = hs.get('backend') == 'IL2CPP'
        if il2cpp:
            # (getClassInfo goes through the il2cpp runtime API; these three call
            # Metadata::Parser::Get(), which loads the Rust parser on first use.)
            t0 = time.time()
            enums, tfields = [], []
            for ns, nm in enum_sites[:150]:
                ev = tc(r, 'getEnumValues', {'assembly': asm, 'namespace': ns, 'name': nm})
                enums.append({'c': ns + '.' + nm, 'r': ev})
            for c in classes:
                ns, nm = c.get('namespace', ''), c.get('name', '')
                if len(tfields) < 150 and '<' not in nm:
                    tf = tc(r, 'il2cppGetMetadataTypeFields', {'namespace': ns, 'name': nm})
                    tfields.append({'c': ns + '.' + nm, 'r': tf})
            strs = []
            first = tc(r, 'il2cppGetMetadataStrings', {'offset': 0, 'limit': 2000}) or {}
            total = first.get('count') or 0
            strs.append(first)
            if total > 4000:
                strs.append(tc(r, 'il2cppGetMetadataStrings', {'offset': total // 2, 'limit': 2000}))
                strs.append(tc(r, 'il2cppGetMetadataStrings', {'offset': total - 1000, 'limit': 2000}))
            out['ab']['md_enums'] = enums
            out['ab']['md_type_fields'] = tfields
            out['ab']['md_strings'] = strs
            out['timing']['md_parser_s'] = round(time.time() - t0, 1)
            ok_enum = sum(1 for e in enums if isinstance(e['r'], dict) and not e['r'].get('error'))
            ok_tf = sum(1 for e in tfields if isinstance(e['r'], dict) and not e['r'].get('error'))
            rep.check('metadata parser: %d/%d enums, %d/%d type-field lists, %d string literals (v%s)'
                      % (ok_enum, len(enums), ok_tf, len(tfields), total, first.get('version')),
                      ok_enum > 0 and ok_tf > 0 and total > 0)

        # ---- field xrefs (IL2CPP: R2 chain detector; Mono: IL scan) ---------
        t0 = time.time()
        xr, n_refs = [], 0
        for ns, nm, fn in statics[:N_XREF_FIELDS]:
            try:
                res = r.call('findFieldReferences', {'assembly': asm, 'ns': ns, 'class': nm,
                                                     'fieldName': fn, 'limit': 500})
            except Exception as e:  # noqa: BLE001
                xr.append({'f': '%s.%s::%s' % (ns, nm, fn), 'error': str(e)[:160]})
                continue
            refs = sorted([[x.get('namespace'), x.get('class'), x.get('method'), x.get('kind'),
                            x.get('confidence'), rel(hx(x.get('address'))), rel(hx(x.get('hitAddress')))]
                           for x in res.get('references') or []], key=json.dumps)
            n_refs += len(refs)
            xr.append({'f': '%s.%s::%s' % (ns, nm, fn), 'refs': refs,
                       'scanned': res.get('scanned_methods'), 'truncated': res.get('truncated'),
                       'error': res.get('error')})
        out['ab']['xref'] = xr
        out['timing']['xref_s'] = round(time.time() - t0, 1)
        rep.check('xref: %d static fields scanned, %d references' % (len(xr), n_refs),
                  len(xr) > 0 and not all('error' in x and x['error'] for x in xr))

        # ---- R2 aob ---------------------------------------------------------
        t0 = time.time()
        aob, self_ok, self_bad = [], 0, 0
        seen_addr = set()
        for ns, nm, mn in method_sites:
            if len(aob) >= N_AOB_METHODS:
                break
            try:
                fmi = r.call('findMethodInstances', {'assembly': asm, 'namespace': ns,
                                                     'name': nm, 'method': mn})
            except Exception:  # noqa: BLE001
                continue
            addr = next((hx(o.get('address')) for o in (fmi.get('matches') or [])
                         if o.get('address') and hx(o.get('address'))), None)
            if addr is None or addr in seen_addr:
                continue
            seen_addr.add(addr)
            row = {'m': '%s.%s::%s' % (ns, nm, mn), 'rel': rel(addr)}
            for stop in (True, False):
                try:
                    p = r.call('getMethodAobPattern', {'address': '0x%X' % addr, 'length': AOB_LEN,
                                                       'stopAtTerminal': stop})
                except Exception as e:  # noqa: BLE001
                    row['stop' if stop else 'full'] = {'error': str(e)[:120]}
                    continue
                keep = {k: v for k, v in p.items() if k not in ('address', 'bytes', 'ok')}
                row['stop' if stop else 'full'] = keep
                pat = p.get('pattern') or ''
                raw = dh.read_mem(pid, addr, AOB_LEN)
                toks = pat.split()
                if toks and len(raw) >= len(toks):
                    rx = b''.join(b'.' if '?' in t else re.escape(bytes([int(t, 16)])) for t in toks)
                    if re.compile(rx, re.DOTALL).match(raw):
                        self_ok += 1
                    else:
                        self_bad += 1
            aob.append(row)
        out['ab']['aob'] = aob
        out['timing']['aob_s'] = round(time.time() - t0, 1)
        rep.check('aob: %d methods, patterns self-match %d/%d' % (len(aob), self_ok, self_ok + self_bad),
                  len(aob) > 0 and self_bad == 0)

        # ---- R2 value refine + group match (writes 2 fields, restores) ------
        tgt = None
        for c in classes:
            if tgt:
                break
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or c.get('is_enum') or c.get('is_value_type') or '<' in nm:
                continue
            try:
                fi = r.call('findInstances', {'assembly': asm, 'namespace': ns, 'name': nm,
                                              'limit': 4, 'validate': True, 'sane': True})
            except Exception:  # noqa: BLE001
                continue
            inst = fi.get('instances') or fi.get('matches') or []
            if not inst:
                continue
            ia = hx(inst[0]['address'] if isinstance(inst[0], dict) else inst[0])
            try:
                w = r.call('walkInstance', {'address': '0x%X' % ia, 'assembly': asm,
                                            'namespace': ns, 'name': nm})
            except Exception:  # noqa: BLE001
                continue
            # Reference types only: their offsets include the 0x10 object
            # header. A value type's fields start at +0 and are not
            # addressable from an instance base (tools/smoke/README.md), so
            # also require walkInstance's value to equal what RPM reads at
            # base + offset before we ever write there.
            ints = [f for f in (w.get('fields') or [])
                    if str(f.get('type', '')).endswith('Int32') and not is_static(f)
                    and isinstance(f.get('offset'), int) and f['offset'] >= 0x10
                    and isinstance(f.get('value'), int)
                    and read_i32(pid, ia + f['offset']) == f['value']]
            if len(ints) < 2:
                continue
            # findInstances can return a stale object (seen on Mono: the
            # memory now belongs to a different live object). Before writing
            # anything, ask the DLL's own reverse lookup (read-only scan for
            # the current value) who owns each field address; both must map
            # back to this instance and class.
            owned = True
            for f in ints[:2]:
                chk = tc(r, 'scanFieldValue', {'dataType': 'int32', 'value': f['value'], 'limit': 5000})
                own = next((c for c in chk.get('candidates') or []
                            if hx(c.get('addr')) == ia + f['offset']), None)
                if not own or hx(own.get('instAddr')) != ia or own.get('class') != nm:
                    owned = False
                    break
            if owned:
                tgt = (ns, nm, ia, ints[0], ints[1])
        if not tgt:
            rep.check('value/group: found an object with two int32 fields', False)
        else:
            ns, nm, ia, fa, fb = tgt
            aa, ab_ = ia + fa['offset'], ia + fb['offset']
            orig_a, orig_b = read_i32(pid, aa), read_i32(pid, ab_)
            out['live']['target'] = {'class': ns + '.' + nm, 'fields': [fa.get('name'), fb.get('name')],
                                     'orig': [orig_a, orig_b]}
            print('value/group target: %s.%s @0x%X fields %s(+%d)=%s %s(+%d)=%s'
                  % (ns, nm, ia, fa.get('name'), fa['offset'], orig_a, fb.get('name'), fb['offset'], orig_b))
            U = 0x5A17C3E1 & 0x7FFFFFFF
            try:
                # value scan
                write_i32(pid, aa, U)
                t0 = time.time()
                b0 = r.call('beginValueScan', {'dataType': 'int32', 'scanType': 'exact', 'value': U,
                                               'limit': 5000})
                out['timing']['value_begin_s'] = round(time.time() - t0, 1)
                sid = b0.get('sessionId')
                has = lambda resp: any(hx(c.get('addr')) == aa for c in resp.get('candidates') or [])  # noqa: E731
                rep.check('value: begin exact finds the field (count=%s)' % b0.get('count'), bool(sid) and has(b0))
                steps = [(U + 7, {'scanType': 'increased'}, True),
                         (None, {'scanType': 'unchanged'}, True),
                         (U + 3, {'scanType': 'decreased'}, True),
                         (None, {'scanType': 'exact', 'value': U + 3}, True),
                         (None, {'scanType': 'range', 'value': U, 'valueTo': U + 5}, True),
                         (None, {'scanType': 'changed'}, False)]
                for newv, args, want in steps:
                    if newv is not None:
                        write_i32(pid, aa, newv)
                    a = dict(args, sessionId=sid, limit=5000)
                    rr = r.call('refineValueScan', a)
                    rep.check('value: refine %s -> field %s' % (json.dumps(args), 'kept' if want else 'dropped'),
                              has(rr) == want, 'total=%s removed=%s' % (rr.get('totalCandidates'), rr.get('removed')))
                    if not want:
                        break
                r.try_call('endValueScan', {'sessionId': sid})

                # group scan
                V1, V2 = U + 101, U + 202
                write_i32(pid, aa, V1)
                write_i32(pid, ab_, V2)
                g0 = r.call('beginGroupScan', {'slots': [
                    {'dataType': 'int32', 'scanType': 'exact', 'value': V1},
                    {'dataType': 'int32', 'scanType': 'exact', 'value': V2}], 'limit': 500})
                gsid = g0.get('sessionId')

                def ghas(resp):
                    # Match on the two slot addresses, not instAddr alone.
                    for c in resp.get('candidates') or []:
                        addrs = {hx(s.get('addr')) for s in c.get('slots') or []}
                        if {aa, ab_} <= addrs:
                            return c
                    return None
                c0 = ghas(g0)
                rep.check('group: begin finds the object (count=%s)' % g0.get('count'),
                          c0 is not None and hx(c0.get('instAddr')) == ia)
                if c0:
                    offs = sorted(s.get('fieldOffset') for s in c0.get('slots') or [])
                    rep.check('group: SDR assigns the two fields', offs == sorted([fa['offset'], fb['offset']]),
                              'offsets=%s' % offs)
                write_i32(pid, ab_, V2 + 1)
                g1 = r.call('refineGroupScan', {'sessionId': gsid, 'slots': [
                    {'scanType': 'unchanged'}, {'scanType': 'increased'}], 'limit': 500})
                rep.check('group: refine unchanged/increased keeps it', ghas(g1) is not None,
                          'total=%s' % g1.get('totalCandidates'))
                g2 = r.call('refineGroupScan', {'sessionId': gsid, 'slots': [
                    {'scanType': 'exact', 'value': V1}, {'scanType': 'exact', 'value': V2}], 'limit': 500})
                rep.check('group: refine exact old value drops it', ghas(g2) is None,
                          'total=%s' % g2.get('totalCandidates'))
                r.try_call('endGroupScan', {'sessionId': gsid})
            finally:
                if orig_a is not None:
                    write_i32(pid, aa, orig_a)
                if orig_b is not None:
                    write_i32(pid, ab_, orig_b)
                rep.check('restore: both fields back to original',
                          read_i32(pid, aa) == orig_a and read_i32(pid, ab_) == orig_b)

        tc(r, 'setKeepForeground', {'enable': False})

    out['live']['checks'] = rep.rows if hasattr(rep, 'rows') else None
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(out, f, indent=1)
    print('timing:', out['timing'])
    return rep.summary()


def compare(pa, pb):
    a = json.load(open(pa, encoding='utf-8'))['ab']
    b = json.load(open(pb, encoding='utf-8'))['ab']
    bad = 0
    for k in sorted(set(a) | set(b)):
        if k not in a or k not in b:
            print('[DIFF] %s missing on one side' % k)
            bad += 1
            continue
        if a[k] == b[k]:
            n = len(a[k]) if isinstance(a[k], list) else ''
            print('[SAME] %s %s' % (k, n))
            continue
        bad += 1
        if isinstance(a[k], list) and isinstance(b[k], list):
            for i, (x, y) in enumerate(zip(a[k], b[k])):
                if x != y:
                    print('[DIFF] %s[%d]\n  A: %s\n  B: %s' % (k, i, json.dumps(x)[:600], json.dumps(y)[:600]))
                    break
            if len(a[k]) != len(b[k]):
                print('[DIFF] %s length %d vs %d' % (k, len(a[k]), len(b[k])))
        else:
            print('[DIFF] %s: %s vs %s' % (k, json.dumps(a[k])[:200], json.dumps(b[k])[:200]))
    print('%d section(s) differ' % bad)
    return 1 if bad else 0


if __name__ == '__main__':
    if len(sys.argv) >= 4 and sys.argv[1] == 'run':
        sys.exit(run(sys.argv[2], sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else 'Assembly-CSharp'))
    if len(sys.argv) == 4 and sys.argv[1] == 'compare':
        sys.exit(compare(sys.argv[2], sys.argv[3]))
    print(__doc__)
    sys.exit(1)
