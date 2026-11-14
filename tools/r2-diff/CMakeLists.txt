# tools/r2-diff — Phase R2 regression driver.
#
# r2-dump (r2_dump.cpp) calls only the public C++ APIs of the R2 modules,
# which are thin wrappers (*Rs.cpp) over the Rust ports in dll/rust/src
# (aob.rs, group_match.rs, value.rs, chain/). Never shipped;
# tools/verify/r2_golden.py runs it and compares the output with recorded
# hashes. Until R2 closed a second flavour, r2-dump-legacy, linked the legacy
# C++ for a byte-for-byte differential (git history, last in aae48d3).
#
# x64 only: the chain detector is x64-only, and the CLI build
# (`build.cmd cli`) is x64-only anyway.

if(NOT DISCRETE_ARCH STREQUAL "x64")
    message(STATUS "tools/r2-diff: skipped (x64 only; DISCRETE_ARCH=${DISCRETE_ARCH})")
    return()
endif()

set(_r2_dll_src ${CMAKE_SOURCE_DIR}/dll/src)

add_executable(r2-dump
    r2_dump.cpp
    # Memory::SafeReadBytes, which RustCore::SafeReadShim (the dc_read_fn the
    # chain detector reads through) wraps. Pulls in ScanCancel.h (header-only).
    ${_r2_dll_src}/shared/Memory.cpp
    # ModuleHelper::GetModuleBase(L"GameAssembly.dll"): how the chain wrapper
    # finds the module.
    ${_r2_dll_src}/shared/ModuleHelper.cpp
    # Log::Write behind RustCore.cpp's log sink. Never initialised here, so it
    # is a no-op and touches no files. MagicStrings.h is header-only.
    ${_r2_dll_src}/shared/Logger.cpp
    ${DISCRETE_R2_SHARED_SOURCES}
    ${DISCRETE_R2_IL2CPP_SOURCES}
    # SafeReadShim + log sink.
    ${_r2_dll_src}/shared/RustCore.cpp
)

target_include_directories(r2-dump PRIVATE ${_r2_dll_src})
# Same per-target plumbing as metadata-cli (no root-scope SYSTEM includes —
# see the root CMakeLists.txt Phase 28b-v2 note).
target_include_directories(r2-dump SYSTEM PRIVATE ${DISCRETE_NLOHMANN_INCLUDE_DIR})
target_include_directories(r2-dump PRIVATE ${DISCRETE_GENERATED_INCLUDE_DIR})
# ModuleHelper.cpp uses PSAPI (GetModuleInformation / EnumProcessModules).
target_link_libraries(r2-dump PRIVATE psapi discrete_core)
set_target_properties(r2-dump PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

# R2 follow-up — IL2CPPFieldXref scan kernels (IL2CPPFieldXrefScan.h).
# Two flavours of ONE driver source (xref_dump.cpp), which calls only the
# kernel API:
#   xref-dump         the Rust port (IL2CPPFieldXrefScanRs.cpp -> discrete_core)
#   xref-dump-legacy  the legacy C++ (IL2CPPFieldXrefScan.cpp + Zydis)
# Neither is shipped. tools/verify/xref_rust_diff.py runs both and requires
# byte-identical output. Unlike the backend DLL, the flavour here does NOT
# follow DISCRETE_XREF_SCAN_SOURCE — the harness always needs one of each.
# Drop xref-dump-legacy (and this note) together with the legacy kernel and
# the vendored Zydis.
#
# Shared C++ both flavours need:
#   Memory.cpp   Memory::SafeReadBytes — the legacy kernels read through it,
#                and RustCore::SafeReadShim (the Rust port's dc_read_fn) wraps
#                it. Pulls in ScanCancel.h (header-only).
#   Logger.cpp   Log::Write behind RustCore.cpp's log sink. Never initialised
#                here, so it is a no-op and touches no files.
function(add_xref_dump TARGET_NAME USE_RUST)
    if(USE_RUST)
        set(_impl
            ${DISCRETE_XREF_SCAN_RS}
            # SafeReadShim + log sink.
            ${_r2_dll_src}/shared/RustCore.cpp
        )
    else()
        set(_impl ${DISCRETE_XREF_SCAN_LEGACY})
    endif()

    add_executable(${TARGET_NAME}
        xref_dump.cpp
        ${_r2_dll_src}/shared/Memory.cpp
        ${_r2_dll_src}/shared/Logger.cpp
        ${_impl}
    )
    target_include_directories(${TARGET_NAME} PRIVATE ${_r2_dll_src})
    target_include_directories(${TARGET_NAME} SYSTEM PRIVATE ${DISCRETE_NLOHMANN_INCLUDE_DIR})
    target_include_directories(${TARGET_NAME} PRIVATE ${DISCRETE_GENERATED_INCLUDE_DIR})

    if(USE_RUST)
        target_link_libraries(${TARGET_NAME} PRIVATE discrete_core)
    else()
        target_link_libraries(${TARGET_NAME} PRIVATE Zydis)
        target_include_directories(${TARGET_NAME} PRIVATE
            ${CMAKE_SOURCE_DIR}/dll/vendor/zydis/include
            ${CMAKE_SOURCE_DIR}/dll/vendor/zydis/dependencies/zycore/include
            ${CMAKE_BINARY_DIR}/dll/vendor/zydis  # generated ZydisExportConfig.h
        )
    endif()
    set_target_properties(${TARGET_NAME} PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
endfunction()

add_xref_dump(xref-dump        ON)
add_xref_dump(xref-dump-legacy OFF)
