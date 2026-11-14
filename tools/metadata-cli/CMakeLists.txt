# metadata-cli — Phase 13. Standalone IL2CPP global-metadata.dat parser
# binary. Reuses the DLL tree's parser (parser core only — the BackendDetector
# adapter lives in MetadataParserHostPath.cpp which we intentionally don't pull
# in here, since the CLI has no host process to detect).
#
# Phase R1 — two flavours from the same CLI sources:
#   metadata-cli         parser per DISCRETE_RUST_METADATA (Rust by default).
#                        This is the one that ships in dist\.
#   metadata-cli-legacy  always the legacy C++ MetadataParser.cpp. Never
#                        shipped; exists so tools/verify/metadata_rust_diff.py
#                        can diff the two parsers' output byte-for-byte.
#                        Delete together with MetadataParser.cpp once R1 is
#                        live-verified (docs/rust-core.md).

set(METADATA_CLI_SOURCES
    main.cpp
    PathResolver.cpp
    FieldOffsets.cpp
    PEParser.cpp
    RegistrationFinder.cpp
    CodeGenModulesFinder.cpp
    SymbolPipeline.cpp
    ../../dll/src/shared/Logger.cpp
)

function(add_metadata_cli TARGET_NAME PARSER_SOURCE USE_RUST)
    add_executable(${TARGET_NAME} ${METADATA_CLI_SOURCES} ${PARSER_SOURCE})

    # PathResolver reads HKCU/HKLM\Software\Valve\Steam via RegGetValueW.
    target_link_libraries(${TARGET_NAME} PRIVATE Advapi32)
    if(USE_RUST)
        target_compile_definitions(${TARGET_NAME} PRIVATE DISCRETE_METADATA_RUST=1)
        target_link_libraries(${TARGET_NAME} PRIVATE discrete_core)
    endif()

    target_include_directories(${TARGET_NAME} PRIVATE
        ../../dll/src
    )
    # Phase 28b-v2 follow-up: nlohmann + generated/ used to come from root-scope
    # include_directories(SYSTEM). Plumb per-target now to avoid the MSVC
    # /external:I leak into Zydis's vendor subdir compiles.
    target_include_directories(${TARGET_NAME} SYSTEM PRIVATE
        ${DISCRETE_NLOHMANN_INCLUDE_DIR}
    )
    target_include_directories(${TARGET_NAME} PRIVATE
        ${DISCRETE_GENERATED_INCLUDE_DIR}
    )

    # Logger emits Log::Write(...) calls but is a no-op until Log::Init() is
    # called; the CLI never inits, so the log dir under %LOCALAPPDATA% is not
    # touched. Keep the dependency anyway so the parser's existing
    # Discrete::Log::Write call sites link cleanly. (The Rust parser logs
    # through a sink that the CLI never registers — same no-op outcome.)

    set_target_properties(${TARGET_NAME} PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    )
endfunction()

add_metadata_cli(metadata-cli "${DISCRETE_METADATA_PARSER_SOURCE}" ${DISCRETE_RUST_METADATA})
add_metadata_cli(metadata-cli-legacy "${DISCRETE_METADATA_PARSER_LEGACY}" OFF)
