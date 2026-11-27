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
struct HMIDIIN__    { int unused; }; using HMIDIIN    = HMIDIIN__*;
struct HMIDISTRM__  { int unused; }; using HMIDISTRM  = HMIDISTRM__*;
struct HMIXER__     { int unused; }; using HMIXER     = HMIXER__*;
struct HMIXEROBJ__  { int unused; }; using HMIXEROBJ  = HMIXEROBJ__*;
struct HMMIO__      { int unused; }; using HMMIO      = HMMIO__*;
using MMRESULT    = UINT;
using MCIERROR    = DWORD;
using MCIDEVICEID = UINT;

// Documented failure values for the PRE-CRT REFUSAL path (see PROXY_THUNK).
//
// Verified against the Windows SDK 10.0.26100.0 headers, not from memory:
//   MMSYSERR_ERROR           = MMSYSERR_BASE + 1 = 1    (mmsyscom.h)
//   TIMERR_NOCANDO           = TIMERR_BASE   + 1 = 97   (timeapi.h)
//   JOYERR_NOCANDO           = JOYERR_BASE   + 6 = 166  (joystickapi.h)
//   MCIERR_INVALID_DEVICE_ID = MCIERR_BASE   + 1 = 257  (mmsystem.h)
//
// ⚠ Hard-coded rather than #include'd on purpose: mmsystem.h also declares
// every one of these exports __declspec(dllimport), which is the exact C2375
// clash this whole file exists to route around.
//
// ⚠ The one property that MUST hold is that none of them is the SUCCESS value.
// MMSYSERR_NOERROR, TIMERR_NOERROR and JOYERR_NOERROR are all 0, so a blanket
// "return 0 when unresolved" would report success for every refused call — for
// timeBeginPeriod that means the host believes it got a 1 ms timer tick and
// never finds out otherwise. MMSYSERR_* occupies the shared low range, so
// kMmFail reads correctly in the wave / midi / mmio families alike.
constexpr MMRESULT kMmFail   = 1;
constexpr MMRESULT kTimeFail = 97;
constexpr MMRESULT kJoyFail  = 166;
constexpr MCIERROR kMciFail  = 257;

}  // namespace

// Internal symbol Proxy_<name>; aliased to <name> by winmm.def. The alias
// keeps the public export ABI identical while sidestepping the system
// header's dllimport decl.
//
// `failval` is what the export answers when the PRE-CRT GATE refuses. It has to
// be that export's own DOCUMENTED failure value, because the caller in that
// window is Windows' loader and it will act on the answer.
//
// ⚠ The two null cases are NOT the same, and the macro keeps them apart:
//
//   CRT not up yet   → the resolver was never entered. Answer `failval` and do
//                      NOT forward. Nothing is latched: `fn` stays null and the
//                      host's own next call resolves for real.
//   CRT up, fn null  → the resolver RAN and the name is genuinely absent from
//                      the real DLL (or the real DLL would not load). Fall
//                      through to `fn args` and crash loudly, ON PURPOSE — a
//                      silent stub here would hide a broken proxy behind a
//                      plausible return value.
//
// `static std::atomic<fn_t> fn{nullptr}` stays CONSTANT-initialised (C++20
// constexpr atomic constructor: zero-filled .bss, zero `_Init_thread_header`
// CRT machinery) while eliminating the formal data race across concurrent
// callers on first resolution (A-L10). Never replace with
// `static auto fn = ResolveExport<fn_t>(...)` — a magic static would latch
// null on a pre-CRT call and drag in CRT guard variables.
#define PROXY_THUNK(ret, conv, name, params, args, failval)               \
    extern "C" ret conv Proxy_##name params {                             \
        using fn_t = ret (conv*) params;                                  \
        static std::atomic<fn_t> fn{nullptr};                             \
        fn_t resolvedFn = fn.load(std::memory_order_acquire);             \
        if (resolvedFn == nullptr) {                                      \
            if (!Discrete::Proxy::Proxy_CrtReady()) {                     \
                Discrete::Proxy::Proxy_NotePreCrtCall();                  \
                return failval;                                           \
            }                                                             \
            resolvedFn = ResolveExport<fn_t>(#name);                      \
            fn.store(resolvedFn, std::memory_order_release);              \
        }                                                                 \
        return resolvedFn args;                                           \
    }

// time* — Unity uses timeGetTime + timeBeginPeriod for frame timing.
//
// ⚠ Read the failure values here before changing any of them. timeGetTime and
// timeSetEvent answer 0, everything else answers kTimeFail, and that asymmetry
// is not an oversight: TIMERR_NOERROR IS 0, so 0 means SUCCESS for the middle
// five, while timeSetEvent returns a timer ID whose documented failure answer
// IS 0 — the convention inverts inside one family.
PROXY_THUNK(DWORD, WINAPI, timeGetTime, (), (), 0)
PROXY_THUNK(MMRESULT, WINAPI, timeBeginPeriod, (UINT a), (a), kTimeFail)
PROXY_THUNK(MMRESULT, WINAPI, timeEndPeriod, (UINT a), (a), kTimeFail)
PROXY_THUNK(MMRESULT, WINAPI, timeGetDevCaps, (void* a, UINT b), (a, b), kTimeFail)
PROXY_THUNK(MMRESULT, WINAPI, timeGetSystemTime, (void* a, UINT b), (a, b), kTimeFail)
PROXY_THUNK(MMRESULT, WINAPI, timeKillEvent, (UINT a), (a), kTimeFail)
// ⚠ 0, not kTimeFail — see the block comment above. This one returns an ID.
PROXY_THUNK(MMRESULT, WINAPI, timeSetEvent,
    (UINT a, UINT b, void* c, DWORD_PTR d, UINT e), (a, b, c, d, e), 0)

// mci* — MCI command interface.
PROXY_THUNK(MCIERROR, WINAPI, mciSendStringA,
    (LPCSTR a, LPSTR b, UINT c, HWND d), (a, b, c, d), kMciFail)
PROXY_THUNK(MCIERROR, WINAPI, mciSendStringW,
    (LPCWSTR a, LPWSTR b, UINT c, HWND d), (a, b, c, d), kMciFail)
PROXY_THUNK(MCIERROR, WINAPI, mciSendCommandA,
    (MCIDEVICEID a, UINT b, DWORD_PTR c, DWORD_PTR d), (a, b, c, d), kMciFail)
PROXY_THUNK(MCIERROR, WINAPI, mciSendCommandW,
    (MCIDEVICEID a, UINT b, DWORD_PTR c, DWORD_PTR d), (a, b, c, d), kMciFail)
// 0 is what these two document for "no such device", so it is the honest
// refusal answer — same for every *GetNumDevs below, where 0 means "none".
PROXY_THUNK(MCIDEVICEID, WINAPI, mciGetDeviceIDA, (LPCSTR a), (a), 0)
PROXY_THUNK(MCIDEVICEID, WINAPI, mciGetDeviceIDW, (LPCWSTR a), (a), 0)

// waveOut* — WMM audio backend.
PROXY_THUNK(MMRESULT, WINAPI, waveOutOpen,
    (HWAVEOUT* a, UINT b, const void* c, DWORD_PTR d, DWORD_PTR e, DWORD f),
    (a, b, c, d, e, f), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutClose, (HWAVEOUT a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutPrepareHeader,
    (HWAVEOUT a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutUnprepareHeader,
    (HWAVEOUT a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutWrite,
    (HWAVEOUT a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutReset, (HWAVEOUT a), (a), kMmFail)
PROXY_THUNK(UINT, WINAPI, waveOutGetNumDevs, (), (), 0)
PROXY_THUNK(MMRESULT, WINAPI, waveOutGetDevCapsA,
    (UINT_PTR a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutGetDevCapsW,
    (UINT_PTR a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutGetPosition,
    (HWAVEOUT a, void* b, UINT c), (a, b, c), kMmFail)
// SDL2.dll + common audio middleware (CRI / Wwise / FMOD / OpenAL / NAudio)
// imports (A-L10). Arities verified against Windows SDK 10.0.26100.0 headers
// for exact x86 __stdcall stack cleanup.
PROXY_THUNK(MMRESULT, WINAPI, waveOutGetErrorTextA,
    (MMRESULT a, LPSTR b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutGetErrorTextW,
    (MMRESULT a, LPWSTR b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutPause, (HWAVEOUT a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutRestart, (HWAVEOUT a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutGetVolume, (HWAVEOUT a, LPDWORD b), (a, b), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutSetVolume, (HWAVEOUT a, DWORD b), (a, b), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutGetID, (HWAVEOUT a, LPUINT b), (a, b), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveOutMessage,
    (HWAVEOUT a, UINT b, DWORD_PTR c, DWORD_PTR d), (a, b, c, d), kMmFail)

// waveIn* — microphone capture. UnityPlayer.dll statically imports this whole
// family (FMOD's recording path) even in games that never record, so omitting
// any one of them blocks the process from starting.
PROXY_THUNK(UINT, WINAPI, waveInGetNumDevs, (), (), 0)
PROXY_THUNK(MMRESULT, WINAPI, waveInGetDevCapsA,
    (UINT_PTR a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveInGetDevCapsW,
    (UINT_PTR a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveInOpen,
    (HWAVEIN* a, UINT b, const void* c, DWORD_PTR d, DWORD_PTR e, DWORD f),
    (a, b, c, d, e, f), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveInClose, (HWAVEIN a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveInPrepareHeader,
    (HWAVEIN a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveInUnprepareHeader,
    (HWAVEIN a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveInAddBuffer,
    (HWAVEIN a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveInStart, (HWAVEIN a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveInStop, (HWAVEIN a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveInReset, (HWAVEIN a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveInGetPosition,
    (HWAVEIN a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveInGetErrorTextA,
    (MMRESULT a, LPSTR b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveInGetErrorTextW,
    (MMRESULT a, LPWSTR b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, waveInMessage,
    (HWAVEIN a, UINT b, DWORD_PTR c, DWORD_PTR d), (a, b, c, d), kMmFail)

// midiOut* / midiIn* / midiStream* — MIDI playback & capture.
PROXY_THUNK(UINT, WINAPI, midiOutGetNumDevs, (), (), 0)
PROXY_THUNK(MMRESULT, WINAPI, midiOutGetDevCapsA,
    (UINT_PTR a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiOutGetDevCapsW,
    (UINT_PTR a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiOutOpen,
    (HMIDIOUT* a, UINT b, DWORD_PTR c, DWORD_PTR d, DWORD e), (a, b, c, d, e), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiOutClose, (HMIDIOUT a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiOutShortMsg, (HMIDIOUT a, DWORD b), (a, b), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiOutLongMsg,
    (HMIDIOUT a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiOutPrepareHeader,
    (HMIDIOUT a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiOutUnprepareHeader,
    (HMIDIOUT a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiOutReset, (HMIDIOUT a), (a), kMmFail)

PROXY_THUNK(UINT, WINAPI, midiInGetNumDevs, (), (), 0)
PROXY_THUNK(MMRESULT, WINAPI, midiInGetDevCapsA,
    (UINT_PTR a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiInGetDevCapsW,
    (UINT_PTR a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiInOpen,
    (HMIDIIN* a, UINT b, DWORD_PTR c, DWORD_PTR d, DWORD e), (a, b, c, d, e), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiInClose, (HMIDIIN a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiInStart, (HMIDIIN a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiInStop, (HMIDIIN a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiInReset, (HMIDIIN a), (a), kMmFail)

PROXY_THUNK(MMRESULT, WINAPI, midiStreamOpen,
    (HMIDISTRM* a, LPUINT b, DWORD c, DWORD_PTR d, DWORD_PTR e, DWORD f),
    (a, b, c, d, e, f), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiStreamClose, (HMIDISTRM a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiStreamOut,
    (HMIDISTRM a, void* b, UINT c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiStreamPause, (HMIDISTRM a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiStreamRestart, (HMIDISTRM a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiStreamStop, (HMIDISTRM a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiStreamProperty,
    (HMIDISTRM a, LPBYTE b, DWORD c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, midiStreamPosition,
    (HMIDISTRM a, void* b, UINT c), (a, b, c), kMmFail)

// joy* — legacy joystick.
PROXY_THUNK(UINT, WINAPI, joyGetNumDevs, (), (), 0)
PROXY_THUNK(MMRESULT, WINAPI, joyGetPos, (UINT a, void* b), (a, b), kJoyFail)
PROXY_THUNK(MMRESULT, WINAPI, joyGetPosEx, (UINT a, void* b), (a, b), kJoyFail)
PROXY_THUNK(MMRESULT, WINAPI, joyGetDevCapsA,
    (UINT_PTR a, void* b, UINT c), (a, b, c), kJoyFail)
PROXY_THUNK(MMRESULT, WINAPI, joyGetDevCapsW,
    (UINT_PTR a, void* b, UINT c), (a, b, c), kJoyFail)

// mixer* — legacy audio mixer API (imported by older FMOD / DirectShow / middleware).
PROXY_THUNK(UINT, WINAPI, mixerGetNumDevs, (), (), 0)
PROXY_THUNK(MMRESULT, WINAPI, mixerOpen,
    (HMIXER* a, UINT b, DWORD_PTR c, DWORD_PTR d, DWORD e), (a, b, c, d, e), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, mixerClose, (HMIXER a), (a), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, mixerGetLineInfoA,
    (HMIXEROBJ a, void* b, DWORD c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, mixerGetLineInfoW,
    (HMIXEROBJ a, void* b, DWORD c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, mixerGetLineControlsA,
    (HMIXEROBJ a, void* b, DWORD c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, mixerGetLineControlsW,
    (HMIXEROBJ a, void* b, DWORD c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, mixerGetControlDetailsA,
    (HMIXEROBJ a, void* b, DWORD c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, mixerGetControlDetailsW,
    (HMIXEROBJ a, void* b, DWORD c), (a, b, c), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, mixerSetControlDetails,
    (HMIXEROBJ a, void* b, DWORD c), (a, b, c), kMmFail)

// mmio* — multimedia file IO.
PROXY_THUNK(HMMIO, WINAPI, mmioOpenA, (LPSTR a, void* b, DWORD c), (a, b, c), nullptr)
PROXY_THUNK(HMMIO, WINAPI, mmioOpenW, (LPWSTR a, void* b, DWORD c), (a, b, c), nullptr)
PROXY_THUNK(MMRESULT, WINAPI, mmioClose, (HMMIO a, UINT b), (a, b), kMmFail)
PROXY_THUNK(LONG, WINAPI, mmioRead, (HMMIO a, char* b, LONG c), (a, b, c), -1)
PROXY_THUNK(LONG, WINAPI, mmioWrite, (HMMIO a, const char* b, LONG c), (a, b, c), -1)
PROXY_THUNK(LONG, WINAPI, mmioSeek, (HMMIO a, LONG b, int c), (a, b, c), -1)
PROXY_THUNK(MMRESULT, WINAPI, mmioDescend,
    (HMMIO a, void* b, const void* c, UINT d), (a, b, c, d), kMmFail)
PROXY_THUNK(MMRESULT, WINAPI, mmioAscend, (HMMIO a, void* b, UINT c), (a, b, c), kMmFail)

// PlaySound — high-level audio.
PROXY_THUNK(BOOL, WINAPI, PlaySoundA, (LPCSTR a, HMODULE b, DWORD c), (a, b, c), FALSE)
PROXY_THUNK(BOOL, WINAPI, PlaySoundW, (LPCWSTR a, HMODULE b, DWORD c), (a, b, c), FALSE)

#undef PROXY_THUNK

BOOL APIENTRY DllMain(HMODULE self, DWORD reason, LPVOID /*reserved*/) {
    if (reason == DLL_PROCESS_ATTACH) {
        // FIRST statement, before anything here can allocate. Reaching DllMain
        // means _DllMainCRTStartup has already run __acrt_initialize, so the
        // heap is real from this point on — which is exactly what every thunk's
        // pre-CRT gate above is asking about. See loader.h.
        Discrete::Proxy::Proxy_MarkCrtReady();
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
