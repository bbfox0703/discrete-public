# RustCore.cmake — Phase R0. Builds the discrete_core Rust static library with
# cargo and exposes it as the IMPORTED target `discrete_core`.
#
# Included from the root CMakeLists.txt BEFORE add_subdirectory(dll) so both
# the backend DLLs and metadata-cli can link it. The proxy DLLs deliberately
# do NOT link it (pre-CRT rule — see docs/Archiutecture.md §Rules).
#
# The three knobs map 1:1 from the C++ build so the two halves never disagree:
#   DISCRETE_ARCH     x64 | x86        -> --target x86_64- / i686-pc-windows-msvc
#   CMAKE_BUILD_TYPE  Debug | Release  -> cargo profile dev | release
#   STATIC_CRT        ON | OFF         -> -Ctarget-feature=+crt-static (/MT) or not (/MD)
# Mixing CRT flavours inside one image fails at link time (LNK4098 / duplicate
# CRT symbols), which is why the Rust side follows STATIC_CRT instead of
# picking its own.

find_program(CARGO_EXECUTABLE cargo
    HINTS "$ENV{CARGO_HOME}/bin" "$ENV{USERPROFILE}/.cargo/bin")
if(NOT CARGO_EXECUTABLE)
    message(FATAL_ERROR
        "cargo not found. Install Rust via rustup (https://rustup.rs) and add "
        "both targets: rustup target add x86_64-pc-windows-msvc i686-pc-windows-msvc")
endif()

if(DISCRETE_ARCH STREQUAL "x64")
    set(DISCRETE_RUST_TRIPLE "x86_64-pc-windows-msvc")
else()
    set(DISCRETE_RUST_TRIPLE "i686-pc-windows-msvc")
endif()

if(CMAKE_BUILD_TYPE STREQUAL "Debug")
    set(_dc_profile     "dev")
    set(_dc_profile_dir "debug")
else()
    set(_dc_profile     "release")
    set(_dc_profile_dir "release")
endif()

# CARGO_ENCODED_RUSTFLAGS takes precedence over RUSTFLAGS,
# CARGO_BUILD_RUSTFLAGS and every `rustflags` in ~/.cargo/config.toml, so a
# developer's own settings (a global +crt-static, -Ctarget-cpu=native, ...)
# can never leak into a shipping build. It is ALWAYS non-empty: the /MD case
# states -crt-static explicitly rather than relying on an empty value, whose
# set-vs-unset handling through `cmake -E env` differs between platforms.
if(STATIC_CRT)
    set(_dc_rustflags "-Ctarget-feature=+crt-static")
    set(_dc_rust_crt  "libcmt.lib")
else()
    set(_dc_rustflags "-Ctarget-feature=-crt-static")
    set(_dc_rust_crt  "msvcrt.lib")
endif()

set(_dc_manifest   "${CMAKE_SOURCE_DIR}/dll/rust/Cargo.toml")
set(_dc_target_dir "${CMAKE_BINARY_DIR}/rust-target")
set(DISCRETE_CORE_LIB
    "${_dc_target_dir}/${DISCRETE_RUST_TRIPLE}/${_dc_profile_dir}/discrete_core.lib"
    CACHE INTERNAL "")

# Always runs; cargo's own fingerprinting makes the no-change case ~0.1 s.
# --locked: Cargo.lock is committed and must not be rewritten by a build.
add_custom_target(discrete_core_cargo
    COMMAND ${CMAKE_COMMAND} -E env
            --unset=RUSTFLAGS
            "CARGO_ENCODED_RUSTFLAGS=${_dc_rustflags}"
            "CARGO_TARGET_DIR=${_dc_target_dir}"
            ${CARGO_EXECUTABLE} build --locked --lib
                --manifest-path "${_dc_manifest}"
                --target ${DISCRETE_RUST_TRIPLE}
                --profile ${_dc_profile}
    BYPRODUCTS "${DISCRETE_CORE_LIB}"
    WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}/dll/rust"
    COMMENT "cargo build discrete_core (${DISCRETE_RUST_TRIPLE}, ${_dc_profile}, crt=${_dc_rust_crt})"
    VERBATIM)

add_library(discrete_core STATIC IMPORTED GLOBAL)
# Native libs from `cargo rustc -- --print native-static-libs` (Rust std on
# windows-msvc). Re-check that list when bumping the toolchain.
set(_dc_native_libs kernel32 ntdll userenv ws2_32 dbghelp synchronization)
set_target_properties(discrete_core PROPERTIES
    IMPORTED_LOCATION "${DISCRETE_CORE_LIB}"
    INTERFACE_INCLUDE_DIRECTORIES "${CMAKE_SOURCE_DIR}/dll/rust/include"
    INTERFACE_LINK_LIBRARIES "${_dc_native_libs}")
# Rust std always names the RELEASE CRT (msvcrt / libcmt). A C++ Debug build
# links the debug CRT (msvcrtd / libcmtd), so drop Rust's default-lib request
# there; the debug CRT exports every symbol Rust std needs.
target_link_options(discrete_core INTERFACE
    "$<$<CONFIG:Debug>:/NODEFAULTLIB:${_dc_rust_crt}>")
add_dependencies(discrete_core discrete_core_cargo)
