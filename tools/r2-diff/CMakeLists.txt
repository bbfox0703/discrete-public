# tools/r2-diff — Phase R2 differential harness binaries.
#
# Two flavours built from ONE driver source (r2_dump.cpp), which calls only
# the public C++ APIs of the R2 modules:
#   r2-dump          the Rust ports (dll/rust/src/{aob,group_match,value,chain}.rs)
#                    behind the *Rs.cpp wrappers, linked against discrete_core.
#   r2-dump-legacy   the legacy C++ implementations (AobPattern.cpp,
#                    ValueRefine.cpp, header-only GroupMatch.h,
#                    IL2CPPChainDetector.cpp + Zydis).
# Neither is shipped. tools/verify/r2_rust_diff.py runs both and requires
# byte-identical output. Unlike the backend DLLs, the flavour here does NOT
# follow DISCRETE_RUST_R2 — the harness always needs one of each.
#
# x64 only: the chain detector (both implementations) is x64-only, and the
# CLI build (`build.cmd cli`) is x64-only anyway.

set(_r2_dll_src ${CMAKE_SOURCE_DIR}/dll/src)

# Shared C++ both flavours need:
#   Memory.cpp        Memory::SafeRead / SafeReadBytes — the legacy chain
#                     detector reads the module through them, and the Rust
#                     flavour's SEH read shim (RustCore::SafeReadShim) wraps
#                     SafeReadBytes. Pulls in ScanCancel.h (header-only).
#   ModuleHelper.cpp  ModuleHelper::GetModuleBase(L"GameAssembly.dll"), which
#                     both chain implementations use to find the module.
#   Logger.cpp        Log::Write call sites (legacy chain detector includes
#                     Logger.h; RustCore.cpp's log sink). Never initialised by
#                     the harness, so it is a no-op and touches no files.
# MagicStrings.h is header-only.
set(R2_DUMP_COMMON_SOURCES
    r2_dump.cpp
    ${_r2_dll_src}/shared/Memory.cpp
    ${_r2_dll_src}/shared/ModuleHelper.cpp
    ${_r2_dll_src}/shared/Logger.cpp
)

function(add_r2_dump TARGET_NAME USE_RUST)
    if(USE_RUST)
        set(_impl
            ${DISCRETE_R2_SHARED_RS}
            ${DISCRETE_R2_IL2CPP_RS}
            # SafeReadShim (dc_read_fn handed to dc_chain_detect) + log sink.
            ${_r2_dll_src}/shared/RustCore.cpp
        )
    else()
        set(_impl
            ${DISCRETE_R2_SHARED_LEGACY}
            ${DISCRETE_R2_IL2CPP_LEGACY}
        )
    endif()

    add_executable(${TARGET_NAME} ${R2_DUMP_COMMON_SOURCES} ${_impl})

    target_include_directories(${TARGET_NAME} PRIVATE ${_r2_dll_src})
    # Same per-target plumbing as metadata-cli (no root-scope SYSTEM includes
    # — see the root CMakeLists.txt Phase 28b-v2 note).
    target_include_directories(${TARGET_NAME} SYSTEM PRIVATE
        ${DISCRETE_NLOHMANN_INCLUDE_DIR}
    )
    target_include_directories(${TARGET_NAME} PRIVATE
        ${DISCRETE_GENERATED_INCLUDE_DIR}
    )

    # ModuleHelper.cpp uses PSAPI (GetModuleInformation / EnumProcessModules).
    target_link_libraries(${TARGET_NAME} PRIVATE psapi)

    if(USE_RUST)
        # GroupMatch.h switches from its inline legacy body to the
        # Rust-backed declarations on this define; every TU must agree.
        target_compile_definitions(${TARGET_NAME} PRIVATE DISCRETE_RUST_R2=1)
        target_link_libraries(${TARGET_NAME} PRIVATE discrete_core)
    else()
        target_link_libraries(${TARGET_NAME} PRIVATE Zydis)
        target_include_directories(${TARGET_NAME} PRIVATE
            ${CMAKE_SOURCE_DIR}/dll/vendor/zydis/include
            ${CMAKE_SOURCE_DIR}/dll/vendor/zydis/dependencies/zycore/include
            ${CMAKE_BINARY_DIR}/dll/vendor/zydis  # generated ZydisExportConfig.h
        )
    endif()

    set_target_properties(${TARGET_NAME} PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    )
endfunction()

if(DISCRETE_ARCH STREQUAL "x64")
    add_r2_dump(r2-dump        ON)
    add_r2_dump(r2-dump-legacy OFF)
else()
    message(STATUS "tools/r2-diff: skipped (x64 only; DISCRETE_ARCH=${DISCRETE_ARCH})")
endif()
