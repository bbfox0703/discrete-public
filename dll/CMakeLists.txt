# UnityDumper DLL — builds Mono and/or IL2CPP variants + ProxyDLLs

# ---------------------------------------------------------------------------
# Helper: configure_file the shared version.rc.in template per target with
# the right InternalName / OriginalFilename / FileDescription, then add the
# generated .rc to the target's sources. Each DLL gets its own VERSIONINFO
# resource embedded at build time — File Explorer / Get-ItemProperty / log
# banners all see the same version string.
# ---------------------------------------------------------------------------
function(add_versioned_rc TARGET_NAME INTERNAL_NAME ORIGINAL_FILENAME DESCRIPTION)
    set(RC_INTERNAL_NAME     "${INTERNAL_NAME}")
    set(RC_ORIGINAL_FILENAME "${ORIGINAL_FILENAME}")
    set(RC_DESCRIPTION       "${DESCRIPTION}")
    set(RC_COMPANY           "UnityCEDumper")
    set(GEN_RC ${CMAKE_BINARY_DIR}/generated/${TARGET_NAME}_version.rc)
    configure_file(
        ${CMAKE_SOURCE_DIR}/dll/src/shared/version.rc.in
        ${GEN_RC}
        @ONLY)
    target_sources(${TARGET_NAME} PRIVATE ${GEN_RC})
endfunction()

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
    add_versioned_rc(UnityDumper-Mono
        "UnityDumper-Mono"
        "UnityDumper-Mono.dll"
        "Unity Mono Engine Dumper DLL")
endif()

if(BACKEND STREQUAL "IL2CPP" OR BACKEND STREQUAL "Both")
    add_backend_dll(UnityDumper-IL2CPP UNITYCEDUMPER_BACKEND_IL2CPP)
    add_versioned_rc(UnityDumper-IL2CPP
        "UnityDumper-IL2CPP"
        "UnityDumper-IL2CPP.dll"
        "Unity IL2CPP Engine Dumper DLL")
endif()

# ---------------------------------------------------------------------------
# Proxy DLLs (Phase 12a — x64). The proxy gets implicitly loaded by the
# host game (because the .exe statically imports version.dll / winmm.dll),
# runs a tiny DllMain shim, then loads the matching UnityDumper-{Backend}.dll
# from the same directory on a worker thread (DllMain holds the loader
# lock, so the actual LoadLibraryW happens off-thread).
#
# Each proxy is a separate shared-library target; both link the shared
# loader.cpp + a minimal slice of the main DLL tree (BackendDetector +
# ModuleHelper + Logger + MagicStrings) so we avoid rebuilding those
# files multiple times.
#
# 32-bit (x86) variants are NOT yet built — Phase 12d.
# ---------------------------------------------------------------------------
set(PROXY_SHARED_SOURCES
    src/proxy/loader.cpp
    src/proxy/loader.h
    src/shared/BackendDetector.cpp
    src/shared/BackendDetector.h
    src/shared/Logger.cpp
    src/shared/Logger.h
    src/shared/MagicStrings.h
    src/shared/ModuleHelper.cpp
    src/shared/ModuleHelper.h
)

function(add_proxy_dll TARGET_NAME OUTPUT_NAME EXTRA_SOURCE DEF_FILE)
    add_library(${TARGET_NAME} SHARED
        ${PROXY_SHARED_SOURCES}
        ${EXTRA_SOURCE}
    )
    target_include_directories(${TARGET_NAME} PRIVATE src)
    set_target_properties(${TARGET_NAME} PROPERTIES
        OUTPUT_NAME              "${OUTPUT_NAME}"
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
        WINDOWS_EXPORT_ALL_SYMBOLS OFF
    )
    # The .def file aliases internal Proxy_<symbol> functions to the public
    # winmm.dll / version.dll export names. Internal names sidestep the
    # C2375 clash with the system header's __declspec(dllimport) decls.
    target_link_options(${TARGET_NAME} PRIVATE "/DEF:${CMAKE_CURRENT_SOURCE_DIR}/${DEF_FILE}")
    # IMPORTANT: do NOT link version.lib / winmm.lib — every export the
    # proxy needs is resolved at runtime via GetProcAddress. Linking the
    # import library would put the system DLL's name into our proxy's
    # import table, which the loader would try to resolve. Since our
    # proxy IS named version.dll / winmm.dll in the game directory, that
    # resolution recurses to ourselves → load loop.
endfunction()

option(BUILD_PROXIES "Build the version.dll / winmm.dll proxies (Phase 12a)" ON)
if(BUILD_PROXIES)
    add_proxy_dll(UnityDumperProxy-version version
        src/proxy/version/version.cpp
        src/proxy/version/version.def)
    add_versioned_rc(UnityDumperProxy-version
        "UnityDumper-version-proxy"
        "version.dll"
        "UnityCEDumper version.dll proxy (auto-loads UnityDumper-{Backend}.dll)")

    add_proxy_dll(UnityDumperProxy-winmm   winmm
        src/proxy/winmm/winmm.cpp
        src/proxy/winmm/winmm.def)
    add_versioned_rc(UnityDumperProxy-winmm
        "UnityDumper-winmm-proxy"
        "winmm.dll"
        "UnityCEDumper winmm.dll proxy (auto-loads UnityDumper-{Backend}.dll)")
endif()
