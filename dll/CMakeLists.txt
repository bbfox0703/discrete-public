# UnityDumper DLL — builds Mono and/or IL2CPP variants + ProxyDLLs

set(DLL_SHARED_SOURCES
    src/main.cpp
    src/shared/Logger.cpp
    src/shared/Logger.h
    src/shared/MagicStrings.h
    src/shared/Memory.cpp
    src/shared/Memory.h
    src/shared/ModuleHelper.cpp
    src/shared/ModuleHelper.h
    src/shared/PipeServer.cpp
    src/shared/PipeServer.h
    src/shared/ExportAPI.cpp
    src/shared/ExportAPI.h
)

# ---------------------------------------------------------------------------
# Helper: configure a backend DLL target
# ---------------------------------------------------------------------------
function(add_backend_dll TARGET_NAME BACKEND_DEFINE)
    add_library(${TARGET_NAME} SHARED ${DLL_SHARED_SOURCES})
    target_include_directories(${TARGET_NAME} PRIVATE src)
    target_compile_definitions(${TARGET_NAME} PRIVATE ${BACKEND_DEFINE})
    set_target_properties(${TARGET_NAME} PROPERTIES
        OUTPUT_NAME "${TARGET_NAME}"
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    )
endfunction()

if(BACKEND STREQUAL "Mono" OR BACKEND STREQUAL "Both")
    add_backend_dll(UnityDumper-Mono UNITYCEDUMPER_BACKEND_MONO)
endif()

if(BACKEND STREQUAL "IL2CPP" OR BACKEND STREQUAL "Both")
    add_backend_dll(UnityDumper-IL2CPP UNITYCEDUMPER_BACKEND_IL2CPP)
endif()

# ---------------------------------------------------------------------------
# Proxy DLLs (version / winmm / dxgi) — Phase 12
# Placeholders only; real .def forwarders land in Phase 12.
# ---------------------------------------------------------------------------
# add_subdirectory(src/proxy/version)
# add_subdirectory(src/proxy/winmm)
# add_subdirectory(src/proxy/dxgi)
