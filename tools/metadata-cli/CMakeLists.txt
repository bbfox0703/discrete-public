# metadata-cli — Phase 13. Standalone IL2CPP global-metadata.dat parser
# binary. Reuses the DLL tree's parser (parser core only — the BackendDetector
# adapter lives in MetadataParserHostPath.cpp which we intentionally don't pull
# in here, since the CLI has no host process to detect).
#
# Phase R1: the parser is the Rust port (MetadataParserRs.cpp over
# discrete_core). Until R1 closed a second flavour, metadata-cli-legacy, linked
# the legacy C++ MetadataParser.cpp for the byte-for-byte differential
# (tools/verify/metadata_rust_diff.py; both last in 5748299).

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

function(add_metadata_cli TARGET_NAME)
    add_executable(${TARGET_NAME} ${METADATA_CLI_SOURCES} ${DISCRETE_METADATA_PARSER_SOURCE})

    # PathResolver reads HKCU/HKLM\Software\Valve\Steam via RegGetValueW.
    target_link_libraries(${TARGET_NAME} PRIVATE Advapi32 discrete_core)

    target_include_directories(${TARGET_NAME} PRIVATE
        ../../dll/src
    )
    # Plumb nlohmann + generated/ per-target instead of root-scope
    # include_directories(SYSTEM) so /external:I doesn't bleed into vendor subdirs.
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

add_metadata_cli(metadata-cli)
