"""A-3 (keep-foreground survives a Discrete_Shutdown -> Discrete_Init cycle),
without touching the desktop.

The fix is one line in ForegroundHook::Shutdown: it disabled the MinHook detours
but left `g_hookInstalled = true`, so the NEXT SetKeepForeground(true) skipped the
whole install block - the lock reported ENABLED while the GetForegroundWindow
illusion stayed dead.

That is directly observable in the game's memory: enabling the hook patches the
first bytes of user32!GetForegroundWindow with a jump, and disabling restores
them. So instead of alt-tabbing and judging by eye, read those bytes at each step:

  1 baseline            -> pristine
  2 setKeepForeground   -> patched
  3 Discrete_Shutdown   -> pristine again (hooks disabled)
  4 Discrete_Init       -> pipe back up
  5 setKeepForeground   -> MUST be patched again   <- the A-3 verdict
"""
import os as _os
_HERE = _os.path.dirname(_os.path.abspath(__file__))
import ctypes
import ctypes.wintypes as wt
import struct
import sys
import time
sys.path.insert(0, _HERE)
import dh
from b1_proxy_exports import PE, read

GAME = sys.argv[1] if len(sys.argv) > 1 else 'CoreKeeper'
DLL = sys.argv[2] if len(sys.argv) > 2 else r'D:\Github\discrete\dist\Discrete-Mono.dll'
pid, pname = dh.find_pid(GAME)
rep = dh.Report('A-3 keep-foreground across Shutdown/Init on %s (pid %s)' % (pname, pid))

psapi = ctypes.WinDLL('psapi', use_last_error=True)
k32 = dh.k32
# HMODULE values exceed a default c_int conversion, so be explicit
psapi.GetModuleFileNameExW.argtypes = [wt.HANDLE, ctypes.c_void_p,
                                       ctypes.c_wchar_p, wt.DWORD]
psapi.EnumProcessModulesEx.argtypes = [wt.HANDLE, ctypes.c_void_p,
                                       wt.DWORD, ctypes.POINTER(wt.DWORD), wt.DWORD]


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
                return int(arr[i]), buf.value
        return None, None
    finally:
        k32.CloseHandle(h)


def export_rvas(path):
    """name -> RVA for every named export."""
    pe = PE(read(path))
    va, _ = pe.dirs[0]
    o = pe.off(va)
    nfuncs = struct.unpack_from('<I', pe.d, o + 20)[0]
    nnames = struct.unpack_from('<I', pe.d, o + 24)[0]
    afuncs = struct.unpack_from('<I', pe.d, o + 28)[0]
    anames = struct.unpack_from('<I', pe.d, o + 32)[0]
    aords = struct.unpack_from('<I', pe.d, o + 36)[0]
    fo, no, oo = pe.off(afuncs), pe.off(anames), pe.off(aords)
    out = {}
    for i in range(nnames):
        nva = struct.unpack_from('<I', pe.d, no + i * 4)[0]
        ordi = struct.unpack_from('<H', pe.d, oo + i * 2)[0]
        frva = struct.unpack_from('<I', pe.d, fo + ordi * 4)[0]
        out[pe.cstr(nva)] = frva
    return out


def call_remote(pid, addr, timeout=20000):
    access = (dh.PROCESS_CREATE_THREAD | dh.PROCESS_QUERY_INFORMATION |
              dh.PROCESS_VM_OPERATION | dh.PROCESS_VM_WRITE | dh.PROCESS_VM_READ)
    h = dh.open_proc(pid, access)
    try:
        th = k32.CreateRemoteThread(h, None, 0, ctypes.c_void_p(addr), None, 0, None)
        if not th:
            raise OSError('CreateRemoteThread failed: %d' % ctypes.get_last_error())
        k32.WaitForSingleObject(th, timeout)
        code = wt.DWORD(0)
        k32.GetExitCodeThread(th, ctypes.byref(code))
        k32.CloseHandle(th)
        return code.value
    finally:
        k32.CloseHandle(h)


# ---- where is user32!GetForegroundWindow, and is it at the same base in both? --
k32.GetModuleHandleW.restype = ctypes.c_void_p
k32.GetProcAddress.restype = ctypes.c_void_p
k32.GetProcAddress.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
u32_local = k32.GetModuleHandleW('user32.dll')
gfw = k32.GetProcAddress(u32_local, b'GetForegroundWindow')
u32_remote, u32_path = remote_module_base(pid, 'user32.dll')
print('    user32 base: local=0x%X remote=0x%X (%s)' % (u32_local, u32_remote or 0, u32_path))
rep.check('user32 is at the same base in both processes (shared image)',
          u32_remote == u32_local, 'local=0x%X remote=0x%X' % (u32_local, u32_remote or 0))
print('    GetForegroundWindow = 0x%X' % gfw)

dbase, dpath = remote_module_base(pid, 'Discrete-Mono.dll')
print('    Discrete module in game: 0x%X (%s)' % (dbase or 0, dpath))
rvas = export_rvas(DLL)
need = ('Discrete_Shutdown', 'Discrete_Init', 'Discrete_StartPipe')
print('    export RVAs: %s' % {k: hex(rvas[k]) for k in need if k in rvas})
rep.check('found the exports we need', all(k in rvas for k in need) and dbase,
          'missing=%s' % [k for k in need if k not in rvas])


def prologue():
    b = dh.read_mem(pid, gfw, 16)
    return b


def classify(b):
    if not b:
        return 'unreadable', ''
    hexs = ' '.join('%02X' % x for x in b[:8])
    if b[0] == 0xE9:
        return 'HOOKED (E9 jmp rel32)', hexs
    if b[0] == 0xFF and b[1] == 0x25:
        return 'HOOKED (FF25 jmp [rip])', hexs
    if b[0] == 0x48 and b[1] == 0xB8:
        return 'HOOKED (mov rax,imm64; jmp)', hexs
    return 'pristine', hexs


steps = []
base_b = prologue()
k, h = classify(base_b)
print('\n    1) baseline                    : %-28s %s' % (k, h))
steps.append(('baseline', k))
# Only meaningful on a FRESH process: a previous run's soft-disable leaves the
# trampoline in place on purpose, so a re-run legitimately starts out HOOKED.
if k == 'pristine':
    rep.check('baseline: GetForegroundWindow is not patched yet', True, k)
else:
    print('       . baseline already hooked - a previous run soft-disabled it '
          '(by design); step 5 is still the decisive check')

with dh.Rpc() as r:
    print('    getKeepForeground -> %s' % r.call('getKeepForeground'))
    print('    setKeepForeground(true) -> %s' % r.call('setKeepForeground', {'enable': True}))
time.sleep(0.5)
k2, h2 = classify(prologue())
print('    2) after setKeepForeground(on) : %-28s %s' % (k2, h2))
rep.check('enabling keep-foreground patches GetForegroundWindow', k2.startswith('HOOKED'), k2)

code = call_remote(pid, dbase + rvas['Discrete_Shutdown'])
print('\n    Discrete_Shutdown remote call -> thread exit %s' % code)
time.sleep(1.0)
k3, h3 = classify(prologue())
print('    3) after Discrete_Shutdown     : %-28s %s' % (k3, h3))
rep.check('Shutdown disables the detour (bytes restored)', k3 == 'pristine', k3)

code = call_remote(pid, dbase + rvas['Discrete_Init'])
print('    Discrete_Init remote call -> thread exit %s' % code)
code = call_remote(pid, dbase + rvas['Discrete_StartPipe'])
print('    Discrete_StartPipe remote call -> thread exit %s' % code)
up = dh.wait_for_pipe(30)
print('    4) pipe back up: %s' % up)
rep.check('pipe comes back after Init + StartPipe', up)

if up:
    with dh.Rpc() as r:
        print('    setKeepForeground(true) again -> %s' % r.call('setKeepForeground', {'enable': True}))
        print('    getKeepForeground -> %s' % r.call('getKeepForeground'))
    time.sleep(0.5)
    k5, h5 = classify(prologue())
    print('    5) after re-enable             : %-28s %s' % (k5, h5))
    rep.check('A-3: re-enabling after Shutdown->Init patches the detour AGAIN',
              k5.startswith('HOOKED'),
              '%s  (pre-fix: stays pristine while the flag reports enabled)' % k5)
    # Turn it back off. NOTE: SetKeepForeground(false) is a deliberate SOFT
    # disable - ForegroundHook.cpp keeps the detour + subclass installed because
    # they gate on g_enabled and pass through when off, "to avoid an
    # unhook/unsubclass race with an in-flight call". Only Shutdown() actually
    # MH_DisableHook's. So the correct expectation here is: the FLAG goes to 0
    # and the patch is still in place.
    with dh.Rpc() as r:
        off = r.call('setKeepForeground', {'enable': False})
        flag = r.call('getKeepForeground')
    time.sleep(0.3)
    k6, _ = classify(prologue())
    print('    6) after setKeepForeground(off): flag=%s bytes=%s' % (flag, k6))
    rep.check('disabling clears the flag', flag.get('state') == 0, str(flag))
    rep.check('soft-disable intentionally leaves the detour in place',
              k6.startswith('HOOKED'), k6)

sys.exit(rep.summary())
