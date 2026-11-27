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

#include <atomic>
#include <memory>
#include <mutex>

namespace Discrete::IL2CPP::Metadata {

namespace {

std::wstring MetadataPathForCurrentProcess() {
    auto d = BackendDetector::Detect();
    if (d.gameDataDir.empty()) return {};
    return d.gameDataDir + L"\\il2cpp_data\\Metadata\\global-metadata.dat";
}

}  // namespace

const Parser& Parser::Get() {
    // D-L4: Allow retries when an earlier call failed (e.g. before
    // BackendDetector could resolve gameDataDir or while the file was locked).
    // Once loaded, publish the immutable Parser pointer atomically so
    // steady-state callers stay lock-free and never race with retry attempts.
    static const Parser unloadedFallback{};
    static std::mutex mu;
    static std::unique_ptr<Parser> loadedInstance;
    static std::atomic<const Parser*> published{nullptr};

    if (const Parser* p = published.load(std::memory_order_acquire)) {
        return *p;
    }

    std::lock_guard<std::mutex> lock(mu);
    if (const Parser* p = published.load(std::memory_order_relaxed)) {
        return *p;
    }
    auto candidate = std::make_unique<Parser>();
    candidate->Load();
    if (candidate->loaded()) {
        const Parser* raw = candidate.get();
        loadedInstance = std::move(candidate);
        published.store(raw, std::memory_order_release);
        return *raw;
    }
    return unloadedFallback;
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
