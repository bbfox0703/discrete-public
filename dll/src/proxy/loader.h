// loader.h — shared helpers for the version.dll / winmm.dll proxy DLLs.
//
// Phase 12a. The proxy DLL gets loaded by the host game during startup
// (because the .exe statically imports it), runs a tiny DllMain shim,
// then hands off to:
//   1. Proxy_LoadRealDll: opens the real <SystemRoot>\System32\<name>
//      and caches the HMODULE so per-export thunks can GetProcAddress.
//   2. Proxy_StartBackendLoader: spawns a worker thread (so we never do
//      LoadLibrary inside DllMain → loader-lock deadlocks) that detects
//      the Unity backend and loads the matching UnityDumper-{Mono|IL2CPP}.dll
//      from the same directory as the proxy DLL.
//
// 32-bit support is NOT yet implemented (Phase 12d). The current build is
// x64 only — same as the rest of the project. Running this proxy under a
// 32-bit Mono game will fail at backend-DLL load (we ship x64 backends only).

#pragma once

#include <windows.h>

namespace UnityCEDumper::Proxy {

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
/// <c>UnityDumper-{Backend}.dll</c>).
/// </summary>
void Proxy_StartBackendLoader(HMODULE proxySelf) noexcept;

}  // namespace UnityCEDumper::Proxy
