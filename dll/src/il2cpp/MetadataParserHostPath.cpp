// MetadataParserHostPath.cpp — DLL-only adapter that resolves the host
// process's <gameDir>/il2cpp_data/Metadata/global-metadata.dat via
// BackendDetector and feeds it into MetadataParser::LoadFromPath.
//
// Split out from MetadataParser.cpp so the offline metadata-cli (Phase 13)
// can link the parser core without dragging BackendDetector / module
// helpers into a CLI executable that has no host process to detect.

#include "MetadataParser.h"

#include "../shared/BackendDetector.h"
#include "../shared/Logger.h"
#include "../shared/MagicStrings.h"

namespace Discrete::IL2CPP::Metadata {

namespace {

std::wstring MetadataPathForCurrentProcess() {
    auto d = BackendDetector::Detect();
    if (d.gameDataDir.empty()) return {};
    return d.gameDataDir + L"\\il2cpp_data\\Metadata\\global-metadata.dat";
}

}  // namespace

const Parser& Parser::Get() {
    static Parser instance = [] {
        Parser p;
        p.Load();
        return p;
    }();
    return instance;
}

void Parser::Load() {
    std::wstring path = MetadataPathForCurrentProcess();
    if (path.empty()) {
        Discrete::Log::Write(MagicStrings::LogCategoryMetadata,
            "MetadataParser: no game data dir resolved");
        return;
    }
    (void)LoadFromPath(path);
}

}  // namespace Discrete::IL2CPP::Metadata
