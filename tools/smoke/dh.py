"""dh.py - headless harness for the Discrete DLL.

Talks to the DiscreteBfx named pipe (line-delimited JSON, PIPE_TYPE_BYTE, max 4
instances) and provides process samplers, so every DLL-side smoke item can be
driven without the UI or Cheat Engine.
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import subprocess
import time

PIPE_PATH = r'\\.\pipe\DiscreteBfx'

k32 = ctypes.WinDLL('kernel32', use_last_error=True)
psapi = ctypes.WinDLL('psapi', use_last_error=True)


# --------------------------------------------------------------- RPC client
class Rpc:
    """One pipe connection. Open several to mimic the UI's cancel channel."""

    def __init__(self, timeout=10.0, label=''):
        self.label = label
        self._f = None
        self._buf = b''
        deadline = time.time() + timeout
        last = None
        while time.time() < deadline:
            try:
                self._f = open(PIPE_PATH, 'r+b', buffering=0)
                break
            except OSError as e:
                last = e
                time.sleep(0.2)
        if self._f is None:
            raise OSError('cannot open %s: %s' % (PIPE_PATH, last))
        self._id = 0

    def send(self, cmd, args=None, rid=None):
        self._id += 1
        req = {'id': rid if rid is not None else self._id, 'cmd': cmd}
        if args:
            req['args'] = args
        self._f.write((json.dumps(req) + '\n').encode('utf-8'))
        return req['id']

    def recv(self, timeout=None):
        """Read one line. timeout=None blocks (pipe is in blocking mode)."""
        while b'\n' not in self._buf:
            chunk = self._f.read(65536)
            if not chunk:
                raise EOFError('pipe closed by server')
            self._buf += chunk
        line, self._buf = self._buf.split(b'\n', 1)
        return json.loads(line.decode('utf-8', 'replace'))

    def call(self, cmd, args=None):
        self.send(cmd, args)
        r = self.recv()
        if 'error' in r:
            raise RuntimeError('%s -> error %s' % (cmd, r['error']))
        return r.get('result', {})

    def try_call(self, cmd, args=None):
        """Like call() but returns (ok, payload) instead of raising."""
        try:
            return True, self.call(cmd, args)
        except Exception as e:
            return False, str(e)

    def close(self):
        if self._f is not None:
            try:
                self._f.close()
            finally:
                self._f = None

    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.close()


def pipe_alive(timeout=1.0):
    try:
        with Rpc(timeout=timeout) as r:
            r.call('ping')
        return True
    except Exception:
        return False


def wait_for_pipe(timeout=90.0, poll=1.0):
    end = time.time() + timeout
    while time.time() < end:
        if pipe_alive(0.5):
            return True
        time.sleep(poll)
    return False


# ------------------------------------------------------------ process utils
TH32CS_SNAPPROCESS = 2


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [('dwSize', wt.DWORD), ('cntUsage', wt.DWORD),
                ('th32ProcessID', wt.DWORD), ('th32DefaultHeapID', ctypes.c_void_p),
                ('th32ModuleID', wt.DWORD), ('cntThreads', wt.DWORD),
                ('th32ParentProcessID', wt.DWORD), ('pcPriClassBase', ctypes.c_long),
                ('dwFlags', wt.DWORD), ('szExeFile', ctypes.c_char * 260)]


def list_processes():
    out = []
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == -1:
        return out
    try:
        pe = PROCESSENTRY32()
        pe.dwSize = ctypes.sizeof(PROCESSENTRY32)
        ok = k32.Process32First(snap, ctypes.byref(pe))
        while ok:
            out.append((pe.th32ProcessID, pe.szExeFile.decode('latin1'), pe.cntThreads))
            ok = k32.Process32Next(snap, ctypes.byref(pe))
    finally:
        k32.CloseHandle(snap)
    return out


def find_pid(substr):
    s = substr.lower()
    for pid, name, _ in list_processes():
        if s in name.lower():
            return pid, name
    return None, None


class PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):
    _fields_ = [('cb', wt.DWORD), ('PageFaultCount', wt.DWORD),
                ('PeakWorkingSetSize', ctypes.c_size_t), ('WorkingSetSize', ctypes.c_size_t),
                ('QuotaPeakPagedPoolUsage', ctypes.c_size_t), ('QuotaPagedPoolUsage', ctypes.c_size_t),
                ('QuotaPeakNonPagedPoolUsage', ctypes.c_size_t), ('QuotaNonPagedPoolUsage', ctypes.c_size_t),
                ('PagefileUsage', ctypes.c_size_t), ('PeakPagefileUsage', ctypes.c_size_t),
                ('PrivateUsage', ctypes.c_size_t)]


PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


def open_proc(pid, access=PROCESS_QUERY_INFORMATION | PROCESS_VM_READ):
    h = k32.OpenProcess(access, False, pid)
    if not h:
        raise OSError('OpenProcess(%d) failed: %d' % (pid, ctypes.get_last_error()))
    return h


def mem_info(pid):
    """(working_set_mb, private_mb)"""
    h = open_proc(pid)
    try:
        c = PROCESS_MEMORY_COUNTERS_EX()
        c.cb = ctypes.sizeof(c)
        if not psapi.GetProcessMemoryInfo(h, ctypes.byref(c), c.cb):
            raise OSError('GetProcessMemoryInfo failed')
        return c.WorkingSetSize / 1048576.0, c.PrivateUsage / 1048576.0
    finally:
        k32.CloseHandle(h)


class FILETIME(ctypes.Structure):
    _fields_ = [('dwLowDateTime', wt.DWORD), ('dwHighDateTime', wt.DWORD)]


def _ft(f):
    return ((f.dwHighDateTime << 32) | f.dwLowDateTime) / 1e7   # seconds


def cpu_times(pid):
    """(kernel_s, user_s) of total CPU consumed since process start."""
    h = open_proc(pid, PROCESS_QUERY_LIMITED_INFORMATION)
    try:
        c, e, kt, ut = FILETIME(), FILETIME(), FILETIME(), FILETIME()
        if not k32.GetProcessTimes(h, ctypes.byref(c), ctypes.byref(e),
                                   ctypes.byref(kt), ctypes.byref(ut)):
            raise OSError('GetProcessTimes failed')
        return _ft(kt), _ft(ut)
    finally:
        k32.CloseHandle(h)


def cpu_percent(pid, interval=1.0):
    """Whole-process CPU over `interval`, in units of one core = 100%."""
    k0, u0 = cpu_times(pid)
    t0 = time.time()
    time.sleep(interval)
    k1, u1 = cpu_times(pid)
    dt = time.time() - t0
    return ((k1 - k0) + (u1 - u0)) / dt * 100.0


def thread_count(pid):
    for p, _, n in list_processes():
        if p == pid:
            return n
    return -1


# ---------------------------------------------------------------- injection
PROCESS_CREATE_THREAD = 0x0002
PROCESS_VM_OPERATION = 0x0008
PROCESS_VM_WRITE = 0x0020
MEM_COMMIT_RESERVE = 0x3000
PAGE_READWRITE = 4


def inject(pid, dll_path):
    """LoadLibraryW injection (x64 host -> x64 target)."""
    dll_path = os.path.abspath(dll_path)
    if not os.path.exists(dll_path):
        raise FileNotFoundError(dll_path)
    access = (PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
              PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ)
    h = open_proc(pid, access)
    try:
        buf = (dll_path + '\0').encode('utf-16-le')
        k32.VirtualAllocEx.restype = ctypes.c_void_p
        remote = k32.VirtualAllocEx(h, None, len(buf), MEM_COMMIT_RESERVE, PAGE_READWRITE)
        if not remote:
            raise OSError('VirtualAllocEx failed: %d' % ctypes.get_last_error())
        written = ctypes.c_size_t(0)
        if not k32.WriteProcessMemory(h, ctypes.c_void_p(remote), buf, len(buf),
                                      ctypes.byref(written)):
            raise OSError('WriteProcessMemory failed: %d' % ctypes.get_last_error())
        k32.GetModuleHandleW.restype = ctypes.c_void_p
        k32.GetProcAddress.restype = ctypes.c_void_p
        k32.GetProcAddress.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        hk = k32.GetModuleHandleW('kernel32.dll')
        fn = k32.GetProcAddress(hk, b'LoadLibraryW')
        th = k32.CreateRemoteThread(h, None, 0, ctypes.c_void_p(fn),
                                    ctypes.c_void_p(remote), 0, None)
        if not th:
            raise OSError('CreateRemoteThread failed: %d' % ctypes.get_last_error())
        k32.WaitForSingleObject(th, 15000)
        code = wt.DWORD(0)
        k32.GetExitCodeThread(th, ctypes.byref(code))
        k32.CloseHandle(th)
        return code.value
    finally:
        k32.CloseHandle(h)


# ------------------------------------------------------------------ logging
class Report:
    def __init__(self, title):
        self.title = title
        self.rows = []
        print('=' * 72)
        print(title)
        print('=' * 72)

    def check(self, name, ok, detail=''):
        self.rows.append((name, bool(ok), detail))
        print('  %s  %s%s' % ('PASS' if ok else 'FAIL', name,
                              ('   ' + str(detail)) if detail else ''))
        return ok

    def note(self, msg):
        print('       . ' + str(msg))

    def summary(self):
        p = sum(1 for _, ok, _ in self.rows if ok)
        f = len(self.rows) - p
        print('-' * 72)
        print('%s: %d passed, %d failed' % (self.title, p, f))
        return f


# ------------------------------------------------- remote read + region walk
MEM_COMMIT = 0x1000
PAGE_EXEC_ANY = 0x10 | 0x20 | 0x40 | 0x80   # EXECUTE, _READ, _READWRITE, _WRITECOPY


class MEMORY_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [('BaseAddress', ctypes.c_void_p), ('AllocationBase', ctypes.c_void_p),
                ('AllocationProtect', wt.DWORD), ('__align', wt.DWORD),
                ('RegionSize', ctypes.c_size_t), ('State', wt.DWORD),
                ('Protect', wt.DWORD), ('Type', wt.DWORD), ('__align2', wt.DWORD)]


def regions(pid, want_exec=False):
    out = []
    h = open_proc(pid)
    try:
        addr = 0
        mbi = MEMORY_BASIC_INFORMATION()
        k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p,
                                       ctypes.POINTER(MEMORY_BASIC_INFORMATION), ctypes.c_size_t]
        while addr < 0x7FFFFFFFFFFF:
            n = k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi))
            if not n:
                break
            base = mbi.BaseAddress or 0
            size = mbi.RegionSize
            if mbi.State == MEM_COMMIT and (not want_exec or (mbi.Protect & PAGE_EXEC_ANY)):
                out.append((base, size, mbi.Protect, mbi.Type))
            addr = base + size
            if size == 0:
                break
        return out
    finally:
        k32.CloseHandle(h)


def read_mem(pid, addr, size):
    h = open_proc(pid)
    try:
        buf = (ctypes.c_char * size)()
        got = ctypes.c_size_t(0)
        ok = k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, size, ctypes.byref(got))
        return bytes(buf[:got.value]) if ok or got.value else b''
    finally:
        k32.CloseHandle(h)
