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

# Architecture: x64-only per project decision (docs/plan.md)
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "UnityCEDumper is x64-only. Configure with a 64-bit toolchain.")
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

# Backend selection (set by build.ps1 via -DBACKEND=Mono|IL2CPP|Both)
set(BACKEND "Both" CACHE STRING "DLL backend: Mono | IL2CPP | Both")
set_property(CACHE BACKEND PROPERTY STRINGS Mono IL2CPP Both)

# Read build number
file(STRINGS "${CMAKE_SOURCE_DIR}/build_number.txt" BUILD_NUMBER LIMIT_COUNT 1)
add_compile_definitions(UNITYCEDUMPER_BUILD_NUMBER=${BUILD_NUMBER})

# Vendored headers (nlohmann/json submodule)
include_directories(SYSTEM
    ${CMAKE_SOURCE_DIR}/dll/vendor/nlohmann_json/single_include
)

add_subdirectory(dll)

# Phase 13 — offline metadata-cli. Built only when -DBUILD_CLI=ON; gated
# so the regular DLL build doesn't grow an extra target by default.
option(BUILD_CLI "Build the offline metadata-cli (Phase 13)" OFF)
if(BUILD_CLI)
    add_subdirectory(tools/metadata-cli)
endif()
