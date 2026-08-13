"""With the game's Update genuinely dead (unfocused pause), what actually blocks?

Two paths behave completely differently, which is the whole answer to "if the game
is paused, surely the call cannot return":

  plain export / plain RPC   does NOT need Update -> returns in ms
  callMethod main_thread:true  IS drained by Update -> must hit the dispatcher's
                               finite timeout (default 5000 ms) instead of hanging

Then the interesting combination: while a main-thread call is stuck waiting, time
Discrete_Shutdown - the export CE's callDLL invokes - to see whether teardown has
to wait for it. If that exceeds 10 s, CE's finite ceiling is what stands between
the user and a permanently frozen Cheat Engine.
"""
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
import ctypes
import ctypes.wintypes as wt
import struct
import sys
import threading
import time
sys.path.insert(0, _HERE)
import dh
from b1_proxy_exports import PE, read

GAME = sys.argv[1] if len(sys.argv) > 1 else 'Mortal'
DLL = sys.argv[2] if len(sys.argv) > 2 else r'D:\Github\discrete\dist\x86\Discrete-Mono.dll'
pid, pname = dh.find_pid(GAME)
rep = dh.Report('main-thread stall behaviour on %s (pid %s)' % (pname, pid))

psapi = ctypes.WinDLL('psapi', use_last_error=True)
k32 = dh.k32
psapi.GetModuleFileNameExW.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_wchar_p, wt.DWORD]
psapi.EnumProcessModulesEx.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD,
                                       ctypes.POINTER(wt.DWORD), wt.DWORD]


def remote_base(needle):
    h = dh.open_proc(pid)
    try:
        arr = (wt.HMODULE * 3000)()
        need = wt.DWORD(0)
        psapi.EnumProcessModulesEx(h, arr, ctypes.sizeof(arr), ctypes.byref(need), 0x03)
        n = min(need.value // ctypes.sizeof(wt.HMODULE), 3000)
        buf = ctypes.create_unicode_buffer(600)
        for i in range(n):
            psapi.GetModuleFileNameExW(h, ctypes.c_void_p(arr[i]), buf, 600)
            if needle.lower() in buf.value.lower():
                return int(arr[i])
    finally:
        k32.CloseHandle(h)
    return None


def rvas(path):
    pe = PE(read(path))
    va, _ = pe.dirs[0]
    o = pe.off(va)
    nn = struct.unpack_from('<I', pe.d, o + 24)[0]
    af = struct.unpack_from('<I', pe.d, o + 28)[0]
    an = struct.unpack_from('<I', pe.d, o + 32)[0]
    ao = struct.unpack_from('<I', pe.d, o + 36)[0]
    fo, no, oo = pe.off(af), pe.off(an), pe.off(ao)
    out = {}
    for i in range(nn):
        nva = struct.unpack_from('<I', pe.d, no + i * 4)[0]
        ordi = struct.unpack_from('<H', pe.d, oo + i * 2)[0]
        out[pe.cstr(nva)] = struct.unpack_from('<I', pe.d, fo + ordi * 4)[0]
    return out


def call_remote(addr, timeout=40000):
    access = (dh.PROCESS_CREATE_THREAD | dh.PROCESS_QUERY_INFORMATION |
              dh.PROCESS_VM_OPERATION | dh.PROCESS_VM_WRITE | dh.PROCESS_VM_READ)
    h = dh.open_proc(pid, access)
    try:
        t0 = time.time()
        th = k32.CreateRemoteThread(h, None, 0, ctypes.c_void_p(addr), None, 0, None)
        if not th:
            raise OSError('CreateRemoteThread failed: %d' % ctypes.get_last_error())
        rc = k32.WaitForSingleObject(th, timeout)
        code = wt.DWORD(0)
        k32.GetExitCodeThread(th, ctypes.byref(code))
        k32.CloseHandle(th)
        return time.time() - t0, code.value, ('TIMED-OUT-WAITING' if rc == 0x102 else 'ok')
    finally:
        k32.CloseHandle(h)


base = remote_base('Discrete-Mono.dll')
ex = rvas(DLL)

with dh.Rpc(timeout=20) as r:
    mt = r.call('handshake').get('main_thread', {})
    print('    Update state: tick=%s age=%sms healthy=%s'
          % (mt.get('tick_count'), mt.get('last_tick_age_ms'), mt.get('is_healthy')))
    rep.check('the game main thread really is stalled (age > 5s)',
              (mt.get('last_tick_age_ms') or 0) > 5000,
              'age=%sms' % mt.get('last_tick_age_ms'))

    # pick any method to invoke; we only care about the marshaling path, and a
    # main-thread submit cannot even reach the invoke while Update is dead
    asms = [a['name'] if isinstance(a, dict) else a
            for a in r.call('getAssemblies').get('assemblies', [])]
    target = None
    for asm in asms:
        if target:
            break
        try:
            classes = r.call('getClasses', {'assembly': asm}).get('classes', [])
        except Exception:
            continue
        for c in classes[:60]:
            ns, nm = c.get('namespace', ''), c.get('name', '')
            if not nm or '<' in nm:
                continue
            try:
                ci = r.call('getClassInfo', {'assembly': asm, 'namespace': ns, 'name': nm})
            except Exception:
                continue
            for m in (ci.get('methods') or []):
                if (m.get('param_count') in (0, None)) and not str(m.get('name', '')).startswith('.') \
                        and m.get('static'):
                    target = (asm, ns, nm, m['name'])
                    break
            if target:
                break
    print('    callMethod target: %s' % (target,))

    if target:
        asm, ns, nm, meth = target
        args = {'assembly': asm, 'namespace': ns, 'name': nm, 'method': meth}
        t = time.time()
        ok, res = r.try_call('callMethod', dict(args, main_thread=False))
        print('    callMethod main_thread=false -> %.2fs ok=%s %s'
              % (time.time() - t, ok, str(res)[:120]))
        rep.check('plain callMethod does not need Update', (time.time() - t) < 3,
                  '%.2fs' % (time.time() - t))

        t = time.time()
        ok2, res2 = r.try_call('callMethod', dict(args, main_thread=True,
                                                  main_thread_timeout_ms=5000))
        dt = time.time() - t
        print('    callMethod main_thread=TRUE  -> %.2fs ok=%s %s' % (dt, ok2, str(res2)[:160]))
        rep.check('main-thread submit returns via its finite timeout, not a hang',
                  3.5 < dt < 12, '%.2fs (dispatcher timeout is 5000 ms)' % dt)
        st = r.call('getDispatcherStats')
        print('    dispatcher: submit=%s drain=%s timeout=%s reject=%s'
              % (st.get('submit_count'), st.get('drain_count'),
                 st.get('timeout_count'), st.get('reject_count')))
        rep.check('the dispatcher counted a timeout', (st.get('timeout_count') or 0) > 0,
                  'timeout_count=%s' % st.get('timeout_count'))

# Now: does Shutdown have to wait for a stuck main-thread call? Fire a
# main_thread:true call from one connection and, while it is stuck, time
# Discrete_Shutdown from a remote thread.
print('\n    [combination] Shutdown while a main-thread call is stuck')
hold = {}


def stuck_call():
    try:
        with dh.Rpc(timeout=20) as r2:
            asms = [a['name'] if isinstance(a, dict) else a
                    for a in r2.call('getAssemblies').get('assemblies', [])]
            if target:
                asm, ns, nm, meth = target
                t = time.time()
                ok, res = r2.try_call('callMethod', {'assembly': asm, 'namespace': ns,
                                                     'name': nm, 'method': meth,
                                                     'main_thread': True,
                                                     'main_thread_timeout_ms': 30000})
                hold['dt'] = time.time() - t
                hold['res'] = str(res)[:100]
    except Exception as e:
        hold['err'] = str(e)


th = threading.Thread(target=stuck_call, daemon=True)
th.start()
time.sleep(2.0)          # let it get stuck in SubmitAndWait
dt, code, how = call_remote(base + ex['Discrete_Shutdown'])
print('    Discrete_Shutdown -> %.2fs exit=%s %s' % (dt, code, how))
print('    (CE would freeze for exactly this long; pre-fix, with an infinite')
print('     timeout, anything that never returned froze CE permanently)')
rep.check('Shutdown returns within CE\'s 10 s ceiling', dt < 10,
          '%.2fs — over 10s would mean CE hits the ceiling and reports an error' % dt)
th.join(35)
print('    the stuck main-thread call: %s' % hold)
sys.exit(rep.summary())
