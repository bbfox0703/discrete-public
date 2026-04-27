// BackendDetector.cpp — implementation.

#include "BackendDetector.h"

#include "Logger.h"
#include "MagicStrings.h"
#include "ModuleHelper.h"

#include <windows.h>

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>
#include <regex>
#include <string>
#include <vector>

namespace UnityCEDumper::BackendDetector {

namespace {

std::wstring ToLower(std::wstring s) {
    std::transform(s.begin(), s.end(), s.begin(),
                   [](wchar_t c) { return static_cast<wchar_t>(::towlower(c)); });
    return s;
}

std::wstring HostExePath() {
    wchar_t buf[MAX_PATH * 2] = {};
    DWORD n = ::GetModuleFileNameW(nullptr, buf, static_cast<DWORD>(std::size(buf)));
    if (n == 0) return {};
    return std::wstring(buf, buf + n);
}

std::wstring BaseName(const std::wstring& full) {
    auto pos = full.find_last_of(L"\\/");
    return (pos == std::wstring::npos) ? full : full.substr(pos + 1);
}

std::wstring StripExtension(std::wstring s) {
    auto pos = s.find_last_of(L'.');
    if (pos != std::wstring::npos) s.erase(pos);
    return s;
}

std::wstring DirOf(const std::wstring& full) {
    auto pos = full.find_last_of(L"\\/");
    return (pos == std::wstring::npos) ? std::wstring() : full.substr(0, pos);
}

bool FileExists(const std::wstring& path) {
    DWORD attrs = ::GetFileAttributesW(path.c_str());
    return (attrs != INVALID_FILE_ATTRIBUTES) && !(attrs & FILE_ATTRIBUTE_DIRECTORY);
}

// Reads up to maxBytes from the head of a file.
bool ReadFileHead(const std::wstring& path, std::vector<std::uint8_t>& out, std::size_t maxBytes) {
    HANDLE h = ::CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                             nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    out.resize(maxBytes);
    DWORD got = 0;
    BOOL ok = ::ReadFile(h, out.data(), static_cast<DWORD>(maxBytes), &got, nullptr);
    ::CloseHandle(h);
    if (!ok) { out.clear(); return false; }
    out.resize(got);
    return got > 0;
}

// Extracts a Unity version like "2020.3.30f1" / "5.6.7p4" from a byte buffer.
// Searches for an ASCII run terminated by NUL/non-printable matching the pattern.
std::optional<std::string> ScanUnityVersionPattern(const std::vector<std::uint8_t>& buf) {
    if (buf.empty()) return std::nullopt;
    static const std::regex kPattern(R"((\d+)\.(\d+)\.(\d+)([abfp])(\d+))");

    // Build a printable-only view so we don't choke std::regex with embedded NULs.
    std::string view;
    view.reserve(buf.size());
    for (auto b : buf) {
        view.push_back((b >= 0x20 && b < 0x7F) ? static_cast<char>(b) : ' ');
    }
    std::smatch m;
    if (std::regex_search(view, m, kPattern)) {
        return m.str(0);
    }
    return std::nullopt;
}

// data.unity3d UnityFS bundle: "UnityFS\0", uint32 BE format, then
// asciiz unityWebBundleVersion, then asciiz unityRevision (e.g. "2020.3.30f1").
std::optional<std::string> ReadVersionFromUnityFs(const std::wstring& path) {
    std::vector<std::uint8_t> head;
    if (!ReadFileHead(path, head, 256)) return std::nullopt;
    if (head.size() < 16) return std::nullopt;
    if (std::memcmp(head.data(), "UnityFS\0", 8) != 0) return std::nullopt;

    // After the 8-byte signature + 4-byte format version, two NUL-terminated strings.
    std::size_t p = 12;
    auto readAsciiz = [&](std::string& s) -> bool {
        s.clear();
        while (p < head.size() && head[p] != 0) {
            s.push_back(static_cast<char>(head[p++]));
        }
        if (p >= head.size()) return false;
        ++p;  // skip NUL
        return true;
    };
    std::string bundleVer, revision;
    if (!readAsciiz(bundleVer)) return std::nullopt;
    if (!readAsciiz(revision))  return std::nullopt;
    if (revision.empty()) return std::nullopt;
    return revision;
}

std::optional<std::string> ExtractUnityVersion(const std::wstring& dataDir) {
    if (dataDir.empty()) return std::nullopt;

    // Preferred: data.unity3d bundle (newer Unity layouts).
    std::wstring dataUnity3d = dataDir + L"\\data.unity3d";
    if (FileExists(dataUnity3d)) {
        if (auto v = ReadVersionFromUnityFs(dataUnity3d); v) return v;
        // Fallback: scan first 4 KB anyway (some bundles vary).
        std::vector<std::uint8_t> buf;
        if (ReadFileHead(dataUnity3d, buf, 4096)) {
            if (auto v = ScanUnityVersionPattern(buf); v) return v;
        }
    }

    // Fallback: globalgamemanagers serialized file.
    std::wstring ggm = dataDir + L"\\globalgamemanagers";
    if (FileExists(ggm)) {
        std::vector<std::uint8_t> buf;
        if (ReadFileHead(ggm, buf, 256)) {
            if (auto v = ScanUnityVersionPattern(buf); v) return v;
        }
    }

    return std::nullopt;
}

// IL2CPP global-metadata.dat: first 4 bytes magic 0xFAB11BAF (LE), next 4 bytes version.
std::optional<std::uint32_t> ReadMetadataVersion(const std::wstring& dataDir) {
    if (dataDir.empty()) return std::nullopt;
    std::wstring path = dataDir + L"\\il2cpp_data\\Metadata\\global-metadata.dat";
    if (!FileExists(path)) return std::nullopt;

    std::vector<std::uint8_t> head;
    if (!ReadFileHead(path, head, 8)) return std::nullopt;
    if (head.size() < 8) return std::nullopt;

    std::uint32_t magic = 0;
    std::uint32_t version = 0;
    std::memcpy(&magic,   head.data(),     4);
    std::memcpy(&version, head.data() + 4, 4);
    if (magic != 0xFAB11BAFu) {
        UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata,
            "global-metadata.dat magic mismatch (corrupt or encrypted)");
        return std::nullopt;
    }
    return version;
}

}  // namespace

const char* BackendName(Backend b) noexcept {
    switch (b) {
        case Backend::Mono:   return "Mono";
        case Backend::IL2CPP: return "IL2CPP";
        default:              return "Unknown";
    }
}

DetectionResult Detect() {
    DetectionResult r;

    std::wstring exePath = HostExePath();
    r.gameModule = BaseName(exePath);

    // <exe>_Data lives next to the host exe.
    std::wstring exeDir = DirOf(exePath);
    std::wstring exeStem = StripExtension(r.gameModule);
    if (!exeDir.empty() && !exeStem.empty()) {
        r.gameDataDir = exeDir + L"\\" + exeStem + L"_Data";
        DWORD attrs = ::GetFileAttributesW(r.gameDataDir.c_str());
        if (attrs == INVALID_FILE_ATTRIBUTES || !(attrs & FILE_ATTRIBUTE_DIRECTORY)) {
            r.gameDataDir.clear();
        }
    }

    // Backend sniff. IL2CPP wins if both somehow appear (shouldn't happen).
    if (ModuleHelper::GetModuleBase(MagicStrings::ModuleIL2CPP) != nullptr) {
        r.backend = Backend::IL2CPP;
    } else if (ModuleHelper::GetModuleBase(MagicStrings::ModuleMonoBdwgc) != nullptr) {
        r.backend = Backend::Mono;
        r.monoModuleName = MagicStrings::ModuleMonoBdwgc;
    } else if (ModuleHelper::GetModuleBase(MagicStrings::ModuleMonoLegacy) != nullptr) {
        r.backend = Backend::Mono;
        r.monoModuleName = MagicStrings::ModuleMonoLegacy;
    }

    // Engine metadata (cheap file reads; safe even if backend == Unknown).
    r.unityVersion    = ExtractUnityVersion(r.gameDataDir);
    if (r.backend == Backend::IL2CPP) {
        r.metadataVersion = ReadMetadataVersion(r.gameDataDir);
    }

    // Log a one-shot summary into the metadata category.
    char line[512];
    std::string uv = r.unityVersion.value_or("unknown");
    std::string mv = r.metadataVersion ? std::to_string(*r.metadataVersion) : std::string("n/a");
    std::snprintf(line, sizeof(line),
                  "Detect: backend=%s unity=%s metadata=%s dataDir=%s",
                  BackendName(r.backend), uv.c_str(), mv.c_str(),
                  r.gameDataDir.empty() ? "<none>" : "<set>");
    UnityCEDumper::Log::Write(MagicStrings::LogCategoryMetadata, line);

    return r;
}

}  // namespace UnityCEDumper::BackendDetector
