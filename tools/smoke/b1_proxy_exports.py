"""Static probes: CE elevation manifest, proxy DLL identity, winmm export/import diff."""
import os, struct, sys

def read(path):
    with open(path, 'rb') as f:
        return f.read()

class PE:
    def __init__(self, data):
        self.d = data
        e = struct.unpack_from('<I', data, 0x3c)[0]
        assert data[e:e+4] == b'PE\x00\x00', 'not PE'
        self.machine = struct.unpack_from('<H', data, e+4)[0]
        nsec = struct.unpack_from('<H', data, e+6)[0]
        optsz = struct.unpack_from('<H', data, e+20)[0]
        opt = e + 24
        self.magic = struct.unpack_from('<H', data, opt)[0]
        self.pe64 = self.magic == 0x20b
        ddoff = opt + (112 if self.pe64 else 96)
        self.dirs = []
        for i in range(16):
            va, sz = struct.unpack_from('<II', data, ddoff + i*8)
            self.dirs.append((va, sz))
        self.secs = []
        so = opt + optsz
        for i in range(nsec):
            name = data[so+i*40: so+i*40+8].rstrip(b'\x00').decode('latin1')
            vsz, va, rsz, raw = struct.unpack_from('<IIII', data, so+i*40+8)
            self.secs.append((name, va, vsz, raw, rsz))

    def off(self, va):
        for name, sva, vsz, raw, rsz in self.secs:
            if sva <= va < sva + max(vsz, rsz):
                return raw + (va - sva)
        return None

    def cstr(self, va):
        o = self.off(va)
        if o is None:
            return ''
        end = self.d.index(b'\x00', o)
        return self.d[o:end].decode('latin1')

    def exports(self):
        va, sz = self.dirs[0]
        if not va:
            return []
        o = self.off(va)
        nnames = struct.unpack_from('<I', self.d, o+24)[0]
        anames = struct.unpack_from('<I', self.d, o+32)[0]
        out = []
        no = self.off(anames)
        for i in range(nnames):
            nva = struct.unpack_from('<I', self.d, no + i*4)[0]
            out.append(self.cstr(nva))
        return out

    def imports(self):
        va, sz = self.dirs[1]
        if not va:
            return {}
        o = self.off(va)
        res = {}
        i = 0
        while True:
            oft, ts, fc, nameva, fthunk = struct.unpack_from('<IIIII', self.d, o + i*20)
            if nameva == 0 and fthunk == 0:
                break
            dll = self.cstr(nameva).lower()
            fns = []
            tva = oft or fthunk
            to = self.off(tva)
            if to is not None:
                step = 8 if self.pe64 else 4
                fmt = '<Q' if self.pe64 else '<I'
                j = 0
                while True:
                    v = struct.unpack_from(fmt, self.d, to + j*step)[0]
                    if v == 0:
                        break
                    high = (1 << (63 if self.pe64 else 31))
                    if v & high:
                        fns.append('#ord%d' % (v & 0xFFFF))
                    else:
                        fns.append(self.cstr((v & 0x7FFFFFFF) + 2))
                    j += 1
            res.setdefault(dll, []).extend(fns)
            i += 1
        return res


def manifest_level(path):
    d = read(path)
    for key in (b'requireAdministrator', b'highestAvailable', b'asInvoker'):
        if key in d:
            return key.decode()
    return 'no-manifest-found'


def product(path):
    d = read(path)
    def w(s):
        return s.encode('utf-16-le')
    hits = []
    for tag in ('Discrete', 'ReShade', 'Special K', 'ENB'):
        if w(tag) in d:
            hits.append(tag)
    return ','.join(hits) or '(unknown)'


if __name__ == '__main__':
    REPO = r'D:\Github\discrete'
    CE = r'C:\Program Files\Cheat Engine'

    print('== CE elevation manifest ==')
    for exe in ('cheatengine-x86_64.exe', 'Cheat Engine.exe', 'cheatengine-i386.exe'):
        p = os.path.join(CE, exe)
        if os.path.exists(p):
            print('  %-32s %s' % (exe, manifest_level(p)))

    print('\n== proxy DLL identity ==')
    cands = [
        os.path.join(REPO, 'dist', 'version.dll'),
        os.path.join(REPO, 'dist', 'winmm.dll'),
        os.path.join(REPO, 'dist', 'x86', 'version.dll'),
        r'D:\SteamLibrary\steamapps\common\Core Keeper\version.dll',
        r'D:\SteamLibrary\steamapps\common\LegendOfMortal\version.dll',
        r'D:\SteamLibrary\steamapps\common\Utawarerumono Past and Present Rediscovered\version.dll',
    ]
    for p in cands:
        if os.path.exists(p):
            pe = PE(read(p))
            arch = {0x8664: 'x64', 0x14c: 'x86'}.get(pe.machine, hex(pe.machine))
            print('  %-70s %s %s  exports=%d' % (p[-70:], arch, product(p), len(pe.exports())))

    print('\n== B-1: winmm proxy exports vs Unity imports ==')
    ours = {}
    for arch, p in (('x64', os.path.join(REPO, 'dist', 'winmm.dll')),
                    ('x86', os.path.join(REPO, 'dist', 'x86', 'winmm.dll'))):
        if os.path.exists(p):
            ours[arch] = set(PE(read(p)).exports())
            print('  our winmm %s exports: %d' % (arch, len(ours[arch])))

    libs = [r'C:\Program Files (x86)\Steam\steamapps\common', r'D:\SteamLibrary\steamapps\common']
    checked = 0
    for lib in libs:
        if not os.path.isdir(lib):
            continue
        for game in sorted(os.listdir(lib)):
            gdir = os.path.join(lib, game)
            up = os.path.join(gdir, 'UnityPlayer.dll')
            if not os.path.exists(up):
                continue
            pe = PE(read(up))
            arch = 'x64' if pe.machine == 0x8664 else 'x86'
            imps = pe.imports()
            need = set()
            for dll, fns in imps.items():
                if dll.startswith('winmm'):
                    need |= set(fns)
            if not need:
                continue
            checked += 1
            have = ours.get(arch, set())
            missing = sorted(need - have)
            status = 'OK' if not missing else 'MISSING %d' % len(missing)
            print('  %-46s %s  winmm imports=%2d  %s' % (game[:46], arch, len(need), status))
            if missing:
                print('        ' + ', '.join(missing))
    print('  (%d UnityPlayer.dll checked)' % checked)

    print('\n== also: game exe direct winmm imports ==')
    for lib in libs:
        if not os.path.isdir(lib):
            continue
        for game in sorted(os.listdir(lib)):
            gdir = os.path.join(lib, game)
            if not os.path.isdir(gdir):
                continue
            try:
                entries = os.listdir(gdir)
            except Exception:
                continue
            if not any(d.endswith('_Data') for d in entries):
                continue
            for f in entries:
                if not f.lower().endswith('.exe'):
                    continue
                try:
                    pe = PE(read(os.path.join(gdir, f)))
                except Exception:
                    continue
                arch = 'x64' if pe.machine == 0x8664 else 'x86'
                need = set()
                for dll, fns in pe.imports().items():
                    if dll.startswith('winmm') or dll.startswith('version'):
                        need |= set('%s!%s' % (dll, x) for x in fns)
                if need:
                    have = set()
                    for tgt, exp in (('winmm', ours.get(arch, set())),):
                        have |= set('%s.dll!%s' % (tgt, x) for x in exp)
                    vp = os.path.join(REPO, 'dist' if arch == 'x64' else 'dist' + os.sep + 'x86', 'version.dll')
                    if os.path.exists(vp):
                        have |= set('version.dll!%s' % x for x in PE(read(vp)).exports())
                    miss = sorted(need - have)
                    print('  %-46s %s %s %s' % (game[:46], f[:28], arch, ('OK' if not miss else 'MISSING: ' + ', '.join(miss))))
