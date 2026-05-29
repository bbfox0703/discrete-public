// ExportAPI.h — C ABI surface exposed by both backend variants of the DLL.
// These are the entry points used by the proxy DLLs and the CE Lua bridge.
#pragma once

#include <cstdint>

#if defined(DISCRETE_BUILDING_DLL)
#  define DISCRETE_API extern "C" __declspec(dllexport)
#else
#  define DISCRETE_API extern "C" __declspec(dllimport)
#endif

extern "C" {

// Backend identifier returned by GetBackendType().
// Keep in sync with UI's BackendType enum.
enum DiscreteBackend : std::int32_t {
    DiscreteBackend_Unknown = 0,
    DiscreteBackend_Mono    = 1,
    DiscreteBackend_IL2CPP  = 2,
};

// One-time initialization. Idempotent. Returns 0 on success, non-zero on failure.
DISCRETE_API int Discrete_Init();

// Tears everything down. Idempotent.
DISCRETE_API void Discrete_Shutdown();

// Starts the named pipe server thread. Returns 0 on success or already running, non-zero on failure.
DISCRETE_API int Discrete_StartPipe();

// Stops the named pipe server thread.
DISCRETE_API void Discrete_StopPipe();

// Returns which backend variant is loaded.
DISCRETE_API DiscreteBackend Discrete_GetBackendType();

// Returns the embedded build number (DISCRETE_BUILD_NUMBER).
DISCRETE_API std::uint32_t Discrete_GetBuildNumber();

}  // extern "C"
