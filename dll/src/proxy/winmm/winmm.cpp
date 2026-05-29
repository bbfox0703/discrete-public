// winmm.cpp — proxy DLL exporting winmm.dll's symbols (subset).
//
// Phase 12a. Internal C functions are named Proxy_<symbol>; winmm.def
// aliases them to the public export name. This avoids the C2375 clash
// between our __declspec(dllexport) and the Windows headers'
// __declspec(dllimport).
//
// We forward only the winmm functions Unity 2017+ statically imports
// from UnityPlayer.exe. If a game imports a winmm function not listed
// here it'll fail to launch with "procedure entry point not located" —
// extending the list is one PROXY_THUNK + one .def line per export.

#include "../loader.h"

#include <windows.h>

namespace {

HMODULE       g_real     = nullptr;
volatile LONG g_initOnce = 0;

void EnsureReal() noexcept {
    if (g_real != nullptr) return;
    if (::InterlockedCompareExchange(&g_initOnce, 1, 0) == 0) {
        g_real = Discrete::Proxy::Proxy_LoadRealDll(L"winmm.dll");
    } else {
        for (int i = 0; i < 1000 && g_real == nullptr; ++i) ::Sleep(0);
    }
}

template <typename Fn>
Fn ResolveExport(const char* name) noexcept {
    EnsureReal();
    return reinterpret_cast<Fn>(
        Discrete::Proxy::Proxy_GetRealExport(g_real, name));
}

// Pointer-width opaque handles. We never deref them; the values just need
// to round-trip the registers same as the real types would.
struct HWAVEOUT__   { int unused; }; using HWAVEOUT   = HWAVEOUT__*;
struct HMIDIOUT__   { int unused; }; using HMIDIOUT   = HMIDIOUT__*;
struct HMMIO__      { int unused; }; using HMMIO      = HMMIO__*;
using MMRESULT    = UINT;
using MCIERROR    = DWORD;
using MCIDEVICEID = UINT;

}  // namespace

#define PROXY_THUNK(ret, conv, name, params, args)                        \
    extern "C" ret conv Proxy_##name params {                             \
        using fn_t = ret (conv*) params;                                  \
        static fn_t fn = nullptr;                                         \
        if (fn == nullptr) fn = ResolveExport<fn_t>(#name);               \
        return fn args;                                                   \
    }

// time* — Unity uses timeGetTime + timeBeginPeriod for frame timing.
PROXY_THUNK(DWORD,    WINAPI, timeGetTime,        (), ())
PROXY_THUNK(MMRESULT, WINAPI, timeBeginPeriod,    (UINT a), (a))
PROXY_THUNK(MMRESULT, WINAPI, timeEndPeriod,      (UINT a), (a))
PROXY_THUNK(MMRESULT, WINAPI, timeGetDevCaps,
    (void* a, UINT b), (a, b))
PROXY_THUNK(MMRESULT, WINAPI, timeGetSystemTime,
    (void* a, UINT b), (a, b))
PROXY_THUNK(MMRESULT, WINAPI, timeKillEvent,      (UINT a), (a))
PROXY_THUNK(MMRESULT, WINAPI, timeSetEvent,
    (UINT a, UINT b, void* c, DWORD_PTR d, UINT e),
    (a, b, c, d, e))

// mci* — MCI command interface.
PROXY_THUNK(MCIERROR, WINAPI, mciSendStringA,
    (LPCSTR a, LPSTR b, UINT c, HWND d), (a, b, c, d))
PROXY_THUNK(MCIERROR, WINAPI, mciSendStringW,
    (LPCWSTR a, LPWSTR b, UINT c, HWND d), (a, b, c, d))
PROXY_THUNK(MCIERROR, WINAPI, mciSendCommandA,
    (MCIDEVICEID a, UINT b, DWORD_PTR c, DWORD_PTR d), (a, b, c, d))
PROXY_THUNK(MCIERROR, WINAPI, mciSendCommandW,
    (MCIDEVICEID a, UINT b, DWORD_PTR c, DWORD_PTR d), (a, b, c, d))
PROXY_THUNK(MCIDEVICEID, WINAPI, mciGetDeviceIDA, (LPCSTR a),  (a))
PROXY_THUNK(MCIDEVICEID, WINAPI, mciGetDeviceIDW, (LPCWSTR a), (a))

// waveOut* — WMM audio backend.
PROXY_THUNK(MMRESULT, WINAPI, waveOutOpen,
    (HWAVEOUT* a, UINT b, const void* c, DWORD_PTR d, DWORD_PTR e, DWORD f),
    (a, b, c, d, e, f))
PROXY_THUNK(MMRESULT, WINAPI, waveOutClose,         (HWAVEOUT a), (a))
PROXY_THUNK(MMRESULT, WINAPI, waveOutPrepareHeader,
    (HWAVEOUT a, void* b, UINT c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, waveOutUnprepareHeader,
    (HWAVEOUT a, void* b, UINT c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, waveOutWrite,
    (HWAVEOUT a, void* b, UINT c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, waveOutReset,         (HWAVEOUT a), (a))
PROXY_THUNK(UINT,     WINAPI, waveOutGetNumDevs,    (), ())

// midiOut* — old MIDI playback path.
PROXY_THUNK(MMRESULT, WINAPI, midiOutOpen,
    (HMIDIOUT* a, UINT b, DWORD_PTR c, DWORD_PTR d, DWORD e),
    (a, b, c, d, e))
PROXY_THUNK(MMRESULT, WINAPI, midiOutClose,         (HMIDIOUT a), (a))
PROXY_THUNK(MMRESULT, WINAPI, midiOutShortMsg,
    (HMIDIOUT a, DWORD b), (a, b))
PROXY_THUNK(MMRESULT, WINAPI, midiOutLongMsg,
    (HMIDIOUT a, void* b, UINT c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, midiOutPrepareHeader,
    (HMIDIOUT a, void* b, UINT c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, midiOutUnprepareHeader,
    (HMIDIOUT a, void* b, UINT c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, midiOutReset,         (HMIDIOUT a), (a))

// joy* — legacy joystick.
PROXY_THUNK(UINT,     WINAPI, joyGetNumDevs,        (), ())
PROXY_THUNK(MMRESULT, WINAPI, joyGetPosEx,
    (UINT a, void* b), (a, b))
PROXY_THUNK(MMRESULT, WINAPI, joyGetDevCapsW,
    (UINT_PTR a, void* b, UINT c), (a, b, c))

// mmio* — multimedia file IO.
PROXY_THUNK(HMMIO,    WINAPI, mmioOpenW,
    (LPWSTR a, void* b, DWORD c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, mmioClose, (HMMIO a, UINT b), (a, b))

// PlaySound — high-level audio.
PROXY_THUNK(BOOL, WINAPI, PlaySoundA, (LPCSTR a, HMODULE b, DWORD c), (a, b, c))
PROXY_THUNK(BOOL, WINAPI, PlaySoundW, (LPCWSTR a, HMODULE b, DWORD c), (a, b, c))

#undef PROXY_THUNK

BOOL APIENTRY DllMain(HMODULE self, DWORD reason, LPVOID /*reserved*/) {
    if (reason == DLL_PROCESS_ATTACH) {
        ::DisableThreadLibraryCalls(self);
        EnsureReal();
        Discrete::Proxy::Proxy_StartBackendLoader(self);
    }
    return TRUE;
}
