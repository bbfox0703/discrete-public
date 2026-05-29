# metadata-cli — Phase 13. Standalone IL2CPP global-metadata.dat parser
# binary. Reuses MetadataParser.cpp from the DLL tree (parser core only —
# the BackendDetector adapter lives in MetadataParserHostPath.cpp which we
# intentionally don't pull in here, since the CLI has no host process to
# detect).

add_executable(metadata-cli
    main.cpp
    PEParser.cpp
    RegistrationFinder.cpp
    CodeGenModulesFinder.cpp
    SymbolPipeline.cpp
    ../../dll/src/il2cpp/MetadataParser.cpp
    ../../dll/src/shared/Logger.cpp
)

target_include_directories(metadata-cli PRIVATE
    ../../dll/src
)
# Phase 28b-v2 follow-up: nlohmann + generated/ used to come from root-scope
# include_directories(SYSTEM). Plumb per-target now to avoid the MSVC
# /external:I leak into Zydis's vendor subdir compiles.
target_include_directories(metadata-cli SYSTEM PRIVATE
    ${DISCRETE_NLOHMANN_INCLUDE_DIR}
)
target_include_directories(metadata-cli PRIVATE
    ${DISCRETE_GENERATED_INCLUDE_DIR}
)

# Logger emits Log::Write(...) calls but is a no-op until Log::Init() is
# called; the CLI never inits, so the log dir under %LOCALAPPDATA% is not
# touched. Keep the dependency anyway so the parser's existing
# Discrete::Log::Write call sites link cleanly.

set_target_properties(metadata-cli PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)
