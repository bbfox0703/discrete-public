# UnityDumper DLL — builds Mono and/or IL2CPP variants + ProxyDLLs

set(DLL_SHARED_SOURCES
    src/main.cpp
    src/shared/BackendDetector.cpp
    src/shared/BackendDetector.h
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

# Mono-specific sources: only compiled into UnityDumper-Mono.dll.
set(DLL_MONO_SOURCES
    src/mono/MonoTypes.h
    src/mono/MonoResolver.cpp
    src/mono/MonoResolver.h
    src/mono/MonoThreadAttach.h
    src/mono/MonoDomain.cpp
    src/mono/MonoDomain.h
    src/mono/MonoTypeParser.cpp
    src/mono/MonoTypeParser.h
    src/mono/MonoClassWalker.cpp
    src/mono/MonoClassWalker.h
)

# IL2CPP-specific sources: only compiled into UnityDumper-IL2CPP.dll.
set(DLL_IL2CPP_SOURCES
    src/il2cpp/IL2CPPTypes.h
    src/il2cpp/IL2CPPResolver.cpp
    src/il2cpp/IL2CPPResolver.h
    src/il2cpp/IL2CPPThreadAttach.h
    src/il2cpp/IL2CPPDomain.cpp
    src/il2cpp/IL2CPPDomain.h
    src/il2cpp/IL2CPPTypeParser.cpp
    src/il2cpp/IL2CPPTypeParser.h
    src/il2cpp/IL2CPPClassWalker.cpp
    src/il2cpp/IL2CPPClassWalker.h
    src/il2cpp/MetadataLayout.h
    src/il2cpp/MetadataParser.cpp
    src/il2cpp/MetadataParser.h
    src/il2cpp/MetadataParserHostPath.cpp  # DLL-only: BackendDetector adapter for Parser::Load()
    src/il2cpp/IL2CPPMetadataRegistration.cpp
    src/il2cpp/IL2CPPMetadataRegistration.h
)

# ---------------------------------------------------------------------------
# Helper: configure a backend DLL target
# ---------------------------------------------------------------------------
function(add_backend_dll TARGET_NAME BACKEND_DEFINE)
    set(SOURCES ${DLL_SHARED_SOURCES})
    if(BACKEND_DEFINE STREQUAL "UNITYCEDUMPER_BACKEND_MONO")
        list(APPEND SOURCES ${DLL_MONO_SOURCES})
    elseif(BACKEND_DEFINE STREQUAL "UNITYCEDUMPER_BACKEND_IL2CPP")
        list(APPEND SOURCES ${DLL_IL2CPP_SOURCES})
    endif()
    add_library(${TARGET_NAME} SHARED ${SOURCES})
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
