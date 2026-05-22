cmake_minimum_required(VERSION 3.25)

project(UnityCEDumper
    VERSION 0.1.0
    DESCRIPTION "Unity Engine Dumper (Mono + IL2CPP) for Cheat Engine"
    LANGUAGES CXX
)

# ---------------------------------------------------------------------------
# Global toolchain settings
# ---------------------------------------------------------------------------
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Architecture: x64 is the default; Phase 12d added x86 for 32-bit Mono
# targets (Legend of Mortal, older Unity games). The build.ps1 -Arch flag
# selects which toolchain DevShell loads — we just report what cmake sees
# here, and stamp a compile-time define so backend code can branch on
# pointer width when struct layouts shift (Mono MonoObject prefix is
# 0x10 on x64 but 0x08 on x86, for example).
if(CMAKE_SIZEOF_VOID_P EQUAL 8)
    set(UNITYCEDUMPER_ARCH "x64")
    add_compile_definitions(UNITYCEDUMPER_ARCH_X64=1)
    message(STATUS "Architecture: x64 (pointer size 8)")
elseif(CMAKE_SIZEOF_VOID_P EQUAL 4)
    set(UNITYCEDUMPER_ARCH "x86")
    add_compile_definitions(UNITYCEDUMPER_ARCH_X86=1)
    message(STATUS "Architecture: x86 (pointer size 4)")
else()
    message(FATAL_ERROR "UnityCEDumper requires a 32-bit or 64-bit Windows toolchain.")
endif()

if(MSVC)
    add_compile_options(/W4 /permissive- /Zc:__cplusplus /utf-8 /MP)
    add_compile_definitions(
        WIN32_LEAN_AND_MEAN
        NOMINMAX
        _CRT_SECURE_NO_WARNINGS
        UNICODE _UNICODE
    )
endif()

# Static CRT (Publish mode) — drops the runtime dependency on the VC++
# Redistributable (vcruntime140.dll / msvcp140.dll / ucrtbase.dll). build.ps1
# passes -DSTATIC_CRT=ON for `build.cmd publish` so the shipped DLLs and
# tools.exe drop into a bare Windows install without needing VCRedist.
# Requires CMP0091=NEW (default for our cmake_minimum_required 3.25).
option(STATIC_CRT "Statically link the MSVC runtime (/MT, /MTd)" OFF)
if(MSVC AND STATIC_CRT)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    message(STATUS "MSVC runtime: static (/MT)")
else()
    if(MSVC)
        message(STATUS "MSVC runtime: dynamic (/MD) — set -DSTATIC_CRT=ON for static")
    endif()
endif()

# Backend selection (set by build.ps1 via -DBACKEND=Mono|IL2CPP|Both)
set(BACKEND "Both" CACHE STRING "DLL backend: Mono | IL2CPP | Both")
set_property(CACHE BACKEND PROPERTY STRINGS Mono IL2CPP Both)

# Read build number
file(STRINGS "${CMAKE_SOURCE_DIR}/build_number.txt" BUILD_NUMBER LIMIT_COUNT 1)
add_compile_definitions(UNITYCEDUMPER_BUILD_NUMBER=${BUILD_NUMBER})

# ---------------------------------------------------------------------------
# Version metadata — populates BuildInfo.h + every PE's VERSIONINFO resource.
# Major/Minor/Patch come from the project() VERSION clause above; Build is
# the auto-incremented build_number.txt value. Git commit + dirty flag +
# build timestamp embed at configure time so the binary itself can identify
# its provenance — File Explorer's Properties pane and `dumpbin /headers`
# will both show this, and the DLL/UI log banners print the same string.
# ---------------------------------------------------------------------------
set(VER_MAJOR ${PROJECT_VERSION_MAJOR})
set(VER_MINOR ${PROJECT_VERSION_MINOR})
set(VER_PATCH ${PROJECT_VERSION_PATCH})

find_package(Git QUIET)
if(GIT_FOUND)
    execute_process(
        COMMAND ${GIT_EXECUTABLE} rev-parse HEAD
        WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
        OUTPUT_VARIABLE GIT_COMMIT_HASH
        OUTPUT_STRIP_TRAILING_WHITESPACE
        ERROR_QUIET)
    execute_process(
        COMMAND ${GIT_EXECUTABLE} rev-parse --short HEAD
        WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
        OUTPUT_VARIABLE GIT_COMMIT_SHORT
        OUTPUT_STRIP_TRAILING_WHITESPACE
        ERROR_QUIET)
    execute_process(
        COMMAND ${GIT_EXECUTABLE} status --porcelain
        WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
        OUTPUT_VARIABLE GIT_DIRTY_OUTPUT
        OUTPUT_STRIP_TRAILING_WHITESPACE
        ERROR_QUIET)
    if(GIT_DIRTY_OUTPUT)
        set(GIT_IS_DIRTY 1)
    else()
        set(GIT_IS_DIRTY 0)
    endif()
endif()
if(NOT GIT_COMMIT_HASH)
    set(GIT_COMMIT_HASH  "unknown")
    set(GIT_COMMIT_SHORT "unknown")
    set(GIT_IS_DIRTY 0)
endif()

string(TIMESTAMP BUILD_TIMESTAMP "%Y-%m-%dT%H:%M:%SZ" UTC)

# Generate BuildInfo.h once into a shared dir every C++ target picks up.
configure_file(
    ${CMAKE_SOURCE_DIR}/dll/src/shared/BuildInfo.h.in
    ${CMAKE_BINARY_DIR}/generated/BuildInfo.h
    @ONLY)

message(STATUS "Version: ${VER_MAJOR}.${VER_MINOR}.${VER_PATCH}.${BUILD_NUMBER} ${GIT_COMMIT_SHORT} (dirty=${GIT_IS_DIRTY})")

# Vendored headers — cloned by `sync_vendor.ps1` (see .gitmodules note).
# Path was `dll/vendor/nlohmann_json/` until the submodule was retired in
# favour of the sync-script approach.
include_directories(SYSTEM
    ${CMAKE_SOURCE_DIR}/dll/vendor/nlohmann/single_include
)
include_directories(${CMAKE_BINARY_DIR}/generated)

add_subdirectory(dll)

# Phase 13 — offline metadata-cli. Built only when -DBUILD_CLI=ON; gated
# so the regular DLL build doesn't grow an extra target by default.
option(BUILD_CLI "Build the offline metadata-cli (Phase 13)" OFF)
if(BUILD_CLI)
    add_subdirectory(tools/metadata-cli)
endif()

# Phase 12d-5 — x86 inject helper. Only meaningful when building x86;
# DiscreteUI x64 shells out to it for Pick & Inject on a 32-bit target.
# Auto-included in the x86 build so users always have it alongside the
# x86 backend DLL + proxies. Silently skipped under x64 (the in-process
# .NET injector handles x64 targets directly).
if(UNITYCEDUMPER_ARCH STREQUAL "x86")
    add_subdirectory(tools/inject-x86)
endif()
