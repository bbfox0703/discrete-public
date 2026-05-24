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
    ../../dll/src/il2cpp/MetadataParser.cpp
    ../../dll/src/shared/Logger.cpp
)

target_include_directories(metadata-cli PRIVATE
    ../../dll/src
)

# Logger emits Log::Write(...) calls but is a no-op until Log::Init() is
# called; the CLI never inits, so the log dir under %LOCALAPPDATA% is not
# touched. Keep the dependency anyway so the parser's existing
# UnityCEDumper::Log::Write call sites link cleanly.

set_target_properties(metadata-cli PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)
