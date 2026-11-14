# tools/r2-diff — Phase R2 regression drivers.
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
# xref-dump (xref_dump.cpp) calls only the kernel API, implemented by
# IL2CPPFieldXrefScanRs.cpp over discrete_core; tools/verify/r2_golden.py
# compares its output with recorded hashes. Until the port was proven, a
# second flavour (xref-dump-legacy) linked the legacy Zydis kernels for a
# byte-for-byte differential (tools/verify/xref_rust_diff.py; last in 987b20d).
#   Memory.cpp   Memory::SafeReadBytes, which RustCore::SafeReadShim (the
#                kernels' dc_read_fn) wraps. Pulls in ScanCancel.h.
#   Logger.cpp   Log::Write behind RustCore.cpp's log sink. Never initialised
#                here, so it is a no-op and touches no files.
add_executable(xref-dump
    xref_dump.cpp
    ${_r2_dll_src}/shared/Memory.cpp
    ${_r2_dll_src}/shared/Logger.cpp
    ${DISCRETE_XREF_SCAN_SOURCE}
    # SafeReadShim + log sink.
    ${_r2_dll_src}/shared/RustCore.cpp
)
target_include_directories(xref-dump PRIVATE ${_r2_dll_src})
target_include_directories(xref-dump SYSTEM PRIVATE ${DISCRETE_NLOHMANN_INCLUDE_DIR})
target_include_directories(xref-dump PRIVATE ${DISCRETE_GENERATED_INCLUDE_DIR})
target_link_libraries(xref-dump PRIVATE discrete_core)
set_target_properties(xref-dump PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
