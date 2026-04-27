// Logger.cpp — Phase 0 stub: write to per-PID category subfolder.
// Full rotation + 5-category enforcement is Phase 1.

#include "Logger.h"
#include "MagicStrings.h"

#include <windows.h>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>

namespace fs = std::filesystem;

namespace UnityCEDumper::Log {

namespace {

std::mutex g_mutex;
fs::path g_logRoot;
bool g_initialized = false;

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
    fs::path dir = g_logRoot / std::wstring(category);
    std::error_code ec;
    fs::create_directories(dir, ec);
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

}  // namespace UnityCEDumper::Log
