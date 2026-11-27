# Offline C++ DLL self-tests (built and run for both x64 and x86 via build.ps1).
# Never copied into dist\.

add_library(discrete_pin_probe SHARED
    pin_probe_dll.cpp
)

target_include_directories(discrete_pin_probe PRIVATE
    ${CMAKE_SOURCE_DIR}/dll/src/shared
)

if(MSVC)
    target_compile_options(discrete_pin_probe PRIVATE /W4 /WX)
endif()

set_target_properties(discrete_pin_probe PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

add_executable(discrete_dll_test
    dll_core_test.cpp
    ${CMAKE_SOURCE_DIR}/dll/src/shared/GroupScanSession.cpp
    ${CMAKE_SOURCE_DIR}/dll/src/shared/Logger.cpp
    ${CMAKE_SOURCE_DIR}/dll/src/shared/MainThreadDispatcher.cpp
    ${CMAKE_SOURCE_DIR}/dll/src/shared/Memory.cpp
    ${CMAKE_SOURCE_DIR}/dll/src/shared/SnapshotSession.cpp
    ${CMAKE_SOURCE_DIR}/dll/src/shared/ValueScanSession.cpp
)

add_dependencies(discrete_dll_test discrete_pin_probe)

target_link_libraries(discrete_dll_test PRIVATE advapi32)

target_include_directories(discrete_dll_test PRIVATE
    ${CMAKE_SOURCE_DIR}/dll/src/shared
    ${CMAKE_SOURCE_DIR}/dll/src/mono
    ${DISCRETE_NLOHMANN_INCLUDE_DIR}
)

if(MSVC)
    # Match the DLL's /W4 warning level and keep exceptions enabled (/EHsc).
    # Memory.cpp uses SEH (__try/__except) in leaf functions with no C++
    # unwinding inside __try, same compilation model as the DLLs.
    target_compile_options(discrete_dll_test PRIVATE /W4 /WX)
endif()

set_target_properties(discrete_dll_test PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)
