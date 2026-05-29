// Logger.cpp — 5-category rotating logger implementation.

#include "Logger.h"
#include "MagicStrings.h"

#include <windows.h>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <unordered_set>

namespace fs = std::filesystem;

namespace Discrete::Log {

namespace {

std::mutex g_mutex;
fs::path g_logRoot;
bool g_initialized = false;

const std::unordered_set<std::wstring>& KnownCategories() {
    static const std::unordered_set<std::wstring> s = {
        MagicStrings::LogCategoryInit,
        MagicStrings::LogCategoryScan,
        MagicStrings::LogCategoryMetadata,
        MagicStrings::LogCategoryPipe,
        MagicStrings::LogCategoryWalk,
    };
    return s;
}

fs::path LogRootForCurrentProcess() {
    wchar_t buf[MAX_PATH];
    DWORD n = ::GetEnvironmentVariableW(MagicStrings::LogRootEnvVar, buf, MAX_PATH);
    fs::path base = (n > 0 && n < MAX_PATH) ? fs::path(buf) : fs::temp_directory_path();
    return base / MagicStrings::LogRootSubpath / std::to_wstring(::GetCurrentProcessId());
}

std::string Timestamp() {
    using namespace std::chrono;
    auto now = system_clock::now();
    auto t = system_clock::to_time_t(now);
    auto ms = duration_cast<milliseconds>(now.time_since_epoch()) % 1000;
    std::tm tm{};
    ::localtime_s(&tm, &t);
    char buf[64];
    std::snprintf(buf, sizeof(buf), "%04d-%02d-%02d %02d:%02d:%02d.%03lld ",
                  tm.tm_year + 1900, tm.tm_mon + 1, tm.tm_mday,
                  tm.tm_hour, tm.tm_min, tm.tm_sec,
                  static_cast<long long>(ms.count()));
    return buf;
}

// Rotate <dir>/current.log → current.1.log → current.2.log → ...
// Drop the oldest beyond LogRotationFileCount. Caller holds g_mutex.
void RotateIfNeeded(const fs::path& dir) {
    fs::path current = dir / L"current.log";
    std::error_code ec;
    if (!fs::exists(current, ec)) return;
    auto size = fs::file_size(current, ec);
    if (ec || size < MagicStrings::LogMaxFileBytes) return;

    fs::path oldest = dir / (L"current." + std::to_wstring(MagicStrings::LogRotationFileCount - 1) + L".log");
    if (fs::exists(oldest, ec)) fs::remove(oldest, ec);

    for (int i = MagicStrings::LogRotationFileCount - 2; i >= 1; --i) {
        fs::path src = dir / (L"current." + std::to_wstring(i) + L".log");
        fs::path dst = dir / (L"current." + std::to_wstring(i + 1) + L".log");
        if (fs::exists(src, ec)) fs::rename(src, dst, ec);
    }
    fs::rename(current, dir / L"current.1.log", ec);
}

}  // namespace

void Init() {
    std::lock_guard lock(g_mutex);
    if (g_initialized) return;
    g_logRoot = LogRootForCurrentProcess();
    std::error_code ec;
    fs::create_directories(g_logRoot, ec);
    g_initialized = true;
}

void Write(std::wstring_view category, std::string_view message) {
    std::lock_guard lock(g_mutex);
    if (!g_initialized) return;

    std::wstring cat(category);
    if (!KnownCategories().contains(cat)) {
        cat = MagicStrings::LogCategoryInit;
    }

    fs::path dir = g_logRoot / cat;
    std::error_code ec;
    fs::create_directories(dir, ec);

    RotateIfNeeded(dir);

    fs::path file = dir / L"current.log";
    std::ofstream out(file, std::ios::app | std::ios::binary);
    if (!out) return;
    out << Timestamp() << message << '\n';
}

void Init(std::string_view message) {
    Write(MagicStrings::LogCategoryInit, message);
}

void Shutdown() {
    std::lock_guard lock(g_mutex);
    g_initialized = false;
}

}  // namespace Discrete::Log
