// loader.h — shared helpers for the version.dll / winmm.dll proxy DLLs.
//
// Phase 12a. The proxy DLL gets loaded by the host game during startup
// (because the .exe statically imports it), runs a tiny DllMain shim,
// then hands off to:
//   1. Proxy_LoadRealDll: opens the real <SystemRoot>\System32\<name>
//      and caches the HMODULE so per-export thunks can GetProcAddress.
//      Called LAZILY, from the first thunk the host actually invokes —
//      never from DllMain, and never before the CRT is up (see the
//      pre-CRT latch below).
//   2. Proxy_StartBackendLoader: spawns a worker thread (so we never do
//      LoadLibrary inside DllMain → loader-lock deadlocks) that detects
//      the Unity backend and loads the matching Discrete-{Mono|IL2CPP}.dll
//      from the same directory as the proxy DLL.
//
// 32-bit support is NOT yet implemented (Phase 12d). The current build is
// x64 only — same as the rest of the project. Running this proxy under a
// 32-bit Mono game will fail at backend-DLL load (we ship x64 backends only).

#pragma once

#include <windows.h>

namespace Discrete::Proxy {

// ---------------------------------------------------------------------------
// The pre-CRT latch
// ---------------------------------------------------------------------------
//
// An export of ours CAN be called before `_DllMainCRTStartup` has run. When it
// is, our CRT does not exist yet: the shipped proxies are built /MT (static
// UCRT — `build.cmd publish` sets STATIC_CRT=ON, and the shipped binaries
// import KERNEL32.dll and nothing else), so `__acrt_heap` is a global of OUR
// module that is still NULL. The first `malloc` on that path becomes
// `HeapAlloc(NULL, ...)` and faults in `ntdll!RtlAllocateHeap`.
//
// Proxy_LoadRealDll allocates (std::wstring) and calls LoadLibraryW, so it is
// exactly that path.
//
// The caller is Windows' own AppCompat shim engine. On a game with a compat
// layer applied, `apphelp.dll` + `AcGenral.dll` load ahead of the CRT, and
// `apphelp!SE_DllLoaded` fires when a module is MAPPED — before its init
// routine runs. AcGenral then does GetModuleHandleW / GetProcAddress / call
// into whatever it finds. For `dxgi.dll` that is a live crash; UE5CEDumper hit
// it on OCTOPATH TRAVELER and dumped it at instruction level.
//
// Our two flavours are not that trigger — AcGenral names dxgi.dll / ninput.dll
// / d3d9.dll, not winmm.dll or version.dll. But `version.dll` is one step away:
// AcGenral STATICALLY IMPORTS it (GetFileVersionInfoSizeW / GetFileVersionInfoW
// / VerQueryValueW) and version.dll is not a KnownDLL, so on a shimmed game the
// copy it binds to is OURS. That survives today only because a static
// dependency is initialised before its dependent, i.e. our DllMain happens to
// have run first. This latch is what stops that accident from being
// load-bearing. See docs/proxy-dlls.md §Pre-CRT.
//
// The gate REFUSES rather than doing a kernel32-only, allocation-free resolve:
// the pre-CRT caller IS the loader, so a LoadLibraryW there is a recursive load
// under the loader lock. Refusing outright is what ReShade does and is strictly
// safer. Nothing is latched — the host's own next call resolves normally.

/// <summary>
/// Record that the CRT is up. MUST be the first statement of each proxy's
/// <c>DLL_PROCESS_ATTACH</c>: reaching DllMain means <c>__acrt_initialize</c>
/// has already run.
/// </summary>
void Proxy_MarkCrtReady() noexcept;

/// <summary>
/// True once <see cref="Proxy_MarkCrtReady"/> has been called. Safe to call
/// before the CRT exists — it reads a plain <c>volatile LONG</c> that lives in
/// zero-filled <c>.data</c> with no dynamic initialiser.
/// </summary>
bool Proxy_CrtReady() noexcept;

/// <summary>
/// Count one forwarded call that arrived before the CRT was up. Allocation-free
/// and CRT-free by construction (a single <c>InterlockedIncrement</c>) because
/// it runs on the path this whole mechanism exists to survive.
/// </summary>
void Proxy_NotePreCrtCall() noexcept;

/// <summary>
/// How many calls <see cref="Proxy_NotePreCrtCall"/> counted. Reported once
/// from the backend-loader thread — without it the refusal is completely
/// invisible: no log, no crash, and nothing for the next person to go on.
/// </summary>
LONG Proxy_PreCrtCallCount() noexcept;


/// <summary>
/// Resolve the absolute path to the system copy of <paramref name="dllName"/>
/// (e.g. <c>L"version.dll"</c>) and load it via <c>LoadLibraryW</c>. Caching
/// is per-name; second calls return the cached HMODULE. Caller must NOT call
/// <c>FreeLibrary</c> on the returned handle — the proxy holds the reference
/// for the lifetime of the host process.
/// </summary>
HMODULE Proxy_LoadRealDll(const wchar_t* dllName) noexcept;

/// <summary>
/// Resolve <paramref name="exportName"/> on the real DLL handle returned by
/// <see cref="Proxy_LoadRealDll"/>. Per-export thunks call this once and
/// cache the result in a static — keeps the hot path to a single indirect
/// branch.
/// </summary>
FARPROC Proxy_GetRealExport(HMODULE realDll, const char* exportName) noexcept;

/// <summary>
/// Kick off the backend-DLL load on a worker thread. Safe to call from
/// DllMain — does not touch the loader lock from this thread.
/// <paramref name="proxySelf"/> is the proxy's own HMODULE, used to resolve
/// the proxy's on-disk directory (so we know where to find the sibling
/// <c>Discrete-{Backend}.dll</c>).
/// </summary>
void Proxy_StartBackendLoader(HMODULE proxySelf) noexcept;

}  // namespace Discrete::Proxy
