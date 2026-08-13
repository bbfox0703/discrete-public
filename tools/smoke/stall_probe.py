"""Does a stalled game main thread make a callDLL export hang? (the CE-Lua
finite-timeout item)

The checklist's suggested repro - suspend the game process - does NOT work:
CreateRemoteThread's new thread runs even when every existing thread is
suspended (measured: Discrete_GetBuildNumber returned in 0 ms on a fully
suspended target).

A game loading a save is a much better candidate: the main thread sits in a long
synchronous load, so Update never runs AND the Mono loader/GC locks are held, so
anything entering the runtime from another thread blocks behind it.

This script finds such a window without driving the game's UI: the handshake
reports main_thread.tick_count, so a stall is simply "tick_count is not
advancing". When one is detected it times, inside the stall:

  ping            pure pipe round-trip, touches no runtime
  getAssemblies   enters the Mono runtime (would block on a loader lock)
  GetBuildNumber  a real callDLL export, via CreateRemoteThread
  Discrete_Shutdown (optional, last) the export that has to drain clients
"""
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
import ctypes
import ctypes.wintypes as wt
import json
import struct
import sys
import time
sys.path.insert(0, _HERE)
import dh
from b1_proxy_exports import PE, read

GAME = sys.argv[1] if len(sys.argv) > 1 else 'Mortal'
DLL = sys.argv[2] if len(sys.argv) > 2 else r'D:\Github\discrete\dist\x86\Discrete-Mono.dll'
WATCH = float(sys.argv[3]) if len(sys.argv) > 3 else 180.0
DO_SHUTDOWN = '--shutdown' in sys.argv

psapi = ctypes.WinDLL('psapi', use_last_error=True)
k32 = dh.k32
psapi.GetModuleFileNameExW.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_wchar_p, wt.DWORD]
psapi.EnumProcessModulesEx.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD,
                                       ctypes.POINTER(wt.DWORD), wt.DWORD]


def remote_module_base(pid, needle):
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
        return None
    finally:
        k32.CloseHandle(h)


def export_rvas(path):
    pe = PE(read(path))
    va, _ = pe.dirs[0]
    o = pe.off(va)
    nnames = struct.unpack_from('<I', pe.d, o + 24)[0]
    afuncs = struct.unpack_from('<I', pe.d, o + 28)[0]
    anames = struct.unpack_from('<I', pe.d, o + 32)[0]
    aords = struct.unpack_from('<I', pe.d, o + 36)[0]
    fo, no, oo = pe.off(afuncs), pe.off(anames), pe.off(aords)
    out = {}
    for i in range(nnames):
        nva = struct.unpack_from('<I', pe.d, no + i * 4)[0]
        ordi = struct.unpack_from('<H', pe.d, oo + i * 2)[0]
        out[pe.cstr(nva)] = struct.unpack_from('<I', pe.d, fo + ordi * 4)[0]
    return out


def call_remote(pid, addr, timeout=30000):
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
        return time.time() - t0, code.value, ('TIMEOUT' if rc == 0x102 else 'ok')
    finally:
        k32.CloseHandle(h)


pid, pname = dh.find_pid(GAME)
print('target %s pid %s' % (pname, pid))
if not dh.wait_for_pipe(180):
    print('pipe never came up')
    sys.exit(1)
dbase = remote_module_base(pid, 'Discrete-Mono.dll')
rvas = export_rvas(DLL)
print('module base 0x%X  GetBuildNumber rva 0x%X' % (dbase or 0, rvas['Discrete_GetBuildNumber']))

r = dh.Rpc(timeout=20)
last_tick, last_change, stalls_probed = None, time.time(), 0
t_start = time.time()
print('\nwatching main_thread.tick_count for stalls (%.0fs)...' % WATCH)
while time.time() - t_start < WATCH:
    try:
        mt = r.call('handshake').get('main_thread', {})
    except Exception as e:
        print('  handshake failed (%s) - reconnecting' % e)
        try:
            r.close()
        except Exception:
            pass
        time.sleep(1)
        try:
            r = dh.Rpc(timeout=20)
        except Exception:
            break
        continue
    tick, age = mt.get('tick_count'), mt.get('last_tick_age_ms')
    now = time.time()
    if tick != last_tick:
        last_tick, last_change = tick, now
    stalled_for = now - last_change
    if stalled_for >= 1.5:
        # Two very different stalls look identical in tick_count:
        #   idle - unfocused pause; the main thread waits and holds no lock
        #   BUSY - a synchronous load (save / scene); the main thread is inside
        #          Mono holding loader/GC locks, which is the only case that can
        #          actually block another thread's runtime call
        cpu = dh.cpu_percent(pid, 0.6)
        busy = cpu > 60
        if not busy and stalls_probed >= 2:
            last_change = time.time()      # already characterised idle stalls
            time.sleep(0.15)
            continue
        stalls_probed += 1
        print('\n  *** STALL: tick stuck at %s for %.1fs (age=%sms) cpu=%.0f%% -> %s ***'
              % (tick, stalled_for, age, cpu,
                 'BUSY (synchronous load?)' if busy else 'idle (unfocused pause)'))
        t = time.time()
        try:
            r.call('ping')
            print('      ping            %.3fs  (pure pipe, no runtime)' % (time.time() - t))
        except Exception as e:
            print('      ping            FAILED %s' % e)
        t = time.time()
        try:
            n = len(r.call('getAssemblies').get('assemblies', []))
            print('      getAssemblies   %.3fs  (%d assemblies - enters the Mono runtime)'
                  % (time.time() - t, n))
        except Exception as e:
            print('      getAssemblies   FAILED %s' % e)
        dt, code, how = call_remote(pid, dbase + rvas['Discrete_GetBuildNumber'])
        print('      GetBuildNumber  %.3fs  exit=%s %s  <- what callDLL does'
              % (dt, code, how))
        # keep watching for further stalls
        last_change = time.time()
    time.sleep(0.15)

print('\nstalls probed: %d' % stalls_probed)
if DO_SHUTDOWN and dbase:
    print('timing Discrete_Shutdown (the export that must drain clients)...')
    dt, code, how = call_remote(pid, dbase + rvas['Discrete_Shutdown'], timeout=40000)
    print('  Discrete_Shutdown %.3fs exit=%s %s' % (dt, code, how))
    print('  > 10s means CE would have hit its ceiling (and hung forever pre-fix): %s'
          % (dt > 10))
