# UnityDumper DLL — builds Mono and/or IL2CPP variants + ProxyDLLs

# ---------------------------------------------------------------------------
# Phase 20-MT — MinHook for inline detouring mono_runtime_invoke /
# il2cpp_runtime_invoke so we can piggyback on Unity's per-frame engine
# tick to drain the main-thread work queue. Vendored at
# dll/vendor/minihook/ with its own CMakeLists.txt — we just add_subdirectory
# (with EXCLUDE_FROM_ALL so its install rules don't pollute our `dist/`).
# Build cost: ~50 KB statically linked into each UnityDumper-{Backend}.dll.
#
# CMake 3.27+ deprecates `cmake_minimum_required(VERSION < 3.10)`. MinHook
# 1.3.3 still ships with `VERSION 3.0...3.5`. Two-part shut-up:
#   (a) CMAKE_POLICY_VERSION_MINIMUM (CMake 3.24+) raises the floor so policy
#       semantics behave like 3.10.
#   (b) CMAKE_WARN_DEPRECATED toggle scopes the deprecation suppression to
#       the vendor add_subdirectory call — our own DLL build still surfaces
#       any deprecation warnings.
# Without this you get one CMake Deprecation Warning per `cmake -B` run on
# CMake 3.27 and later.
set(CMAKE_POLICY_VERSION_MINIMUM 3.10)
set(CMAKE_WARN_DEPRECATED OFF CACHE BOOL "" FORCE)
add_subdirectory(vendor/minihook EXCLUDE_FROM_ALL)
unset(CMAKE_WARN_DEPRECATED CACHE)
set(CMAKE_WARN_DEPRECATED ON CACHE BOOL "Suppress deprecated CMake feature warnings" FORCE)

# Silence the warnings MinHook 1.3.3 emits under MSVC. None of these point
# at real bugs — they're stylistic ABI / bit-packing decisions in vendored
# third-party C code:
#   C4100 — unreferenced formal parameter (x86-only path in buffer.c that
#           takes pOrigin but doesn't use it; x64 build doesn't trip it)
#   C4201 — nameless struct/union (used by MinHook's IO buffer)
#   C4244 — BOOL → UINT8 narrowing in deliberate bit-pack assignments
#   C4310 — cast truncates constant value (intentional opcode constants)
#   C4701 — possibly-uninitialised local in the HDE disassembler's
#           switch fall-through (the variable IS init in every path the
#           runtime takes; MSVC's flow analysis is too conservative)
# Apply via the `minhook` target so the suppressions don't bleed into
# our own source — UnityDumper-* targets keep their warnings as-is.
if(MSVC)
    target_compile_options(minhook PRIVATE /wd4100 /wd4201 /wd4244 /wd4310 /wd4701)
endif()

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
    src/shared/AobPattern.cpp
    src/shared/AobPattern.h
    src/shared/BackendDetector.cpp
    src/shared/BackendDetector.h
    src/shared/Logger.cpp
    src/shared/Logger.h
    src/shared/MagicStrings.h
    src/shared/MainThreadDispatcher.cpp
    src/shared/MainThreadDispatcher.h
    src/shared/Memory.cpp
    src/shared/Memory.h
    src/shared/ModuleHelper.cpp
    src/shared/ModuleHelper.h
    src/shared/PipeServer.cpp
    src/shared/PipeServer.h
    src/shared/ExportAPI.cpp
    src/shared/ExportAPI.h
    src/shared/MethodSignatureFormat.h
    src/shared/ValueScanSession.cpp
    src/shared/ValueScanSession.h
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
    src/mono/MonoAddressFinder.cpp
    src/mono/MonoAddressFinder.h
    src/mono/MonoMethodFinder.cpp
    src/mono/MonoMethodFinder.h
    src/mono/MonoMethodInvoker.cpp
    src/mono/MonoMethodInvoker.h
    src/mono/MonoMainThreadHook.cpp
    src/mono/MonoMainThreadHook.h
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
    src/il2cpp/IL2CPPAddressFinder.cpp
    src/il2cpp/IL2CPPAddressFinder.h
    src/il2cpp/IL2CPPMethodFinder.cpp
    src/il2cpp/IL2CPPMethodFinder.h
    src/il2cpp/IL2CPPMethodInvoker.cpp
    src/il2cpp/IL2CPPMethodInvoker.h
    src/il2cpp/IL2CPPMainThreadHook.cpp
    src/il2cpp/IL2CPPMainThreadHook.h
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
    # Phase 20-MT: link MinHook (vendored as `minhook` target — see
    # add_subdirectory above). Static C-only library, ~50 KB per backend.
    target_link_libraries(${TARGET_NAME} PRIVATE minhook)
    target_include_directories(${TARGET_NAME} PRIVATE vendor/minihook/include)
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
