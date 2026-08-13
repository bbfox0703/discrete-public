// winmm.cpp — proxy DLL exporting winmm.dll's symbols (subset).
//
// Phase 12a. Internal C functions are named Proxy_<symbol>; winmm.def
// aliases them to the public export name. This avoids the C2375 clash
// between our __declspec(dllexport) and the Windows headers'
// __declspec(dllimport).
//
// We forward only the winmm functions Unity statically imports. If a game
// imports a winmm function not listed here it'll fail to launch with
// "procedure entry point not located" — extending the list is one
// PROXY_THUNK + one .def line per export.
//
// The list MUST be derived from UnityPlayer.DLL, not UnityPlayer.exe. That
// mistake shipped a proxy missing 13 of the 23 winmm symbols UnityPlayer.dll
// statically imports (the whole waveIn* family plus waveOutGetDevCapsA/W and
// waveOutGetPosition), which made every Unity game refuse to start with the
// winmm proxy installed. Re-check against a real UnityPlayer.dll import table
// before trimming anything here.
//
// NOT fixable with PE export forwarders ("waveInOpen = winmm.waveInOpen"):
// the game directory precedes System32 in the search order, so the forward
// would resolve back to THIS dll and recurse. Thunks are required.
//
// On x86 these are stdcall — the CALLEE pops the arguments — so every thunk's
// parameter list must match the real export's arity exactly or the stack is
// corrupted on return. Verify arity, not just names, when adding entries.

#include "../loader.h"

#include <windows.h>

#include <atomic>

namespace {

std::atomic<HMODULE> g_real{nullptr};

// MUST be non-blocking and re-entrant. winmm's own timer exports mean a thunk
// can be entered from a multimedia-timer callback (timeSetEvent runs its
// callback on a high-priority timer thread that is documented as forbidden to
// block), and timeKillEvent is routinely called FROM that callback.
//
// The previous version was neither. It did an InterlockedCompareExchange and
// made the losing thread spin `for (i < 1000) Sleep(0)` waiting for the winner
// — blocking, inside a callback that must not block. Worse, the spin was
// BOUNDED: if it expired, g_real stayed null, ResolveExport returned null, and
// the thunk executed `return fn args;` through a null pointer. Nothing checked.
// g_real was also a plain HMODULE read across threads with no barrier.
//
// None of that machinery bought anything: Proxy_LoadRealDll is already
// serialised by its own SRWLOCK, caches by name, and FreeLibrary's the loser's
// duplicate handle, and LoadLibraryW is refcounted. Racing callers therefore
// converge on the same handle on their own. So just call it — no CAS, no spin,
// no expiry window, nothing to re-enter.
void EnsureReal() noexcept {
    if (g_real.load(std::memory_order_acquire) != nullptr) return;
    HMODULE m = Discrete::Proxy::Proxy_LoadRealDll(L"winmm.dll");
    g_real.store(m, std::memory_order_release);
}

template <typename Fn>
Fn ResolveExport(const char* name) noexcept {
    EnsureReal();
    return reinterpret_cast<Fn>(Discrete::Proxy::Proxy_GetRealExport(
        g_real.load(std::memory_order_acquire), name));
}

// Pointer-width opaque handles. We never deref them; the values just need
// to round-trip the registers same as the real types would.
struct HWAVEOUT__   { int unused; }; using HWAVEOUT   = HWAVEOUT__*;
struct HWAVEIN__    { int unused; }; using HWAVEIN    = HWAVEIN__*;
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
PROXY_THUNK(MMRESULT, WINAPI, waveOutGetDevCapsA,
    (UINT_PTR a, void* b, UINT c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, waveOutGetDevCapsW,
    (UINT_PTR a, void* b, UINT c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, waveOutGetPosition,
    (HWAVEOUT a, void* b, UINT c), (a, b, c))
// SDL2.dll (bundled by many Unity games) imports this one.
PROXY_THUNK(MMRESULT, WINAPI, waveOutGetErrorTextW,
    (MMRESULT a, LPWSTR b, UINT c), (a, b, c))

// waveIn* — microphone capture. UnityPlayer.dll statically imports this whole
// family (FMOD's recording path) even in games that never record, so omitting
// any one of them blocks the process from starting.
PROXY_THUNK(UINT,     WINAPI, waveInGetNumDevs,     (), ())
PROXY_THUNK(MMRESULT, WINAPI, waveInGetDevCapsA,
    (UINT_PTR a, void* b, UINT c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, waveInGetDevCapsW,
    (UINT_PTR a, void* b, UINT c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, waveInOpen,
    (HWAVEIN* a, UINT b, const void* c, DWORD_PTR d, DWORD_PTR e, DWORD f),
    (a, b, c, d, e, f))
PROXY_THUNK(MMRESULT, WINAPI, waveInClose,          (HWAVEIN a), (a))
PROXY_THUNK(MMRESULT, WINAPI, waveInPrepareHeader,
    (HWAVEIN a, void* b, UINT c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, waveInUnprepareHeader,
    (HWAVEIN a, void* b, UINT c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, waveInAddBuffer,
    (HWAVEIN a, void* b, UINT c), (a, b, c))
PROXY_THUNK(MMRESULT, WINAPI, waveInStart,          (HWAVEIN a), (a))
PROXY_THUNK(MMRESULT, WINAPI, waveInReset,          (HWAVEIN a), (a))

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
        // Deliberately NOT calling EnsureReal() here. That would LoadLibraryW
        // under the loader lock while the host .exe's own static imports are
        // still being initialised. UE5CEDumper hit exactly that and it crashed
        // Octopath Traveler through its dxgi proxy (see its Lugner_Dxgi.asm
        // header) — which is why its generated proxies resolve lazily too.
        // Each thunk resolves on its first call instead, which happens on a
        // game thread after DllMain has returned: the safe place.
        Discrete::Proxy::Proxy_StartBackendLoader(self);
    }
    return TRUE;
}
