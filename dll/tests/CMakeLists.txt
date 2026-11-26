# Offline C++ DLL self-tests (built and run for both x64 and x86 via build.ps1).
# Never copied into dist\.

add_executable(discrete_dll_test
    dll_core_test.cpp
    ${CMAKE_SOURCE_DIR}/dll/src/shared/Memory.cpp
)

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
