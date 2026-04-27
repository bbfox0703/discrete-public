// BackendDetector.h — sniff Unity backend (Mono / IL2CPP) and extract engine
// metadata (Unity version, IL2CPP global-metadata.dat version) from the host
// process. Used to populate the `handshake` pipe response.
#pragma once

#include <cstdint>
#include <optional>
#include <string>

namespace UnityCEDumper::BackendDetector {

enum class Backend : int {
    Unknown = 0,
    Mono    = 1,
    IL2CPP  = 2,
};

struct DetectionResult {
    Backend                       backend = Backend::Unknown;
    std::wstring                  monoModuleName;   // populated when backend == Mono
    std::wstring                  gameModule;       // base name of host exe
    std::wstring                  gameDataDir;      // <gameModule without .exe>_Data
    std::optional<std::string>    unityVersion;     // e.g. "2020.3.30f1"
    std::optional<std::uint32_t>  metadataVersion;  // IL2CPP global-metadata.dat version
};

// Performs a fresh scan of the host process. Cheap; safe to call repeatedly.
DetectionResult Detect();

const char* BackendName(Backend b) noexcept;

}  // namespace UnityCEDumper::BackendDetector
