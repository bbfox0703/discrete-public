// ExportAPI.h — C ABI surface exposed by both backend variants of the DLL.
// These are the entry points used by the proxy DLLs and the CE Lua bridge.
#pragma once

#include <cstdint>

#if defined(UNITYCEDUMPER_BUILDING_DLL)
#  define UNITYCEDUMPER_API extern "C" __declspec(dllexport)
#else
#  define UNITYCEDUMPER_API extern "C" __declspec(dllimport)
#endif

extern "C" {

// Backend identifier returned by GetBackendType().
// Keep in sync with UI's BackendType enum.
enum UnityDumperBackend : std::int32_t {
    UnityDumperBackend_Unknown = 0,
    UnityDumperBackend_Mono    = 1,
    UnityDumperBackend_IL2CPP  = 2,
};

// One-time initialization. Idempotent. Returns 0 on success, non-zero on failure.
UNITYCEDUMPER_API int UnityDumper_Init();

// Tears everything down. Idempotent.
UNITYCEDUMPER_API void UnityDumper_Shutdown();

// Starts the named pipe server thread. Returns 0 on success or already running, non-zero on failure.
UNITYCEDUMPER_API int UnityDumper_StartPipe();

// Stops the named pipe server thread.
UNITYCEDUMPER_API void UnityDumper_StopPipe();

// Returns which backend variant is loaded.
UNITYCEDUMPER_API UnityDumperBackend UnityDumper_GetBackendType();

// Returns the embedded build number (UNITYCEDUMPER_BUILD_NUMBER).
UNITYCEDUMPER_API std::uint32_t UnityDumper_GetBuildNumber();

}  // extern "C"
