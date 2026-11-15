// Logger.cpp — 5-category per-PID logger: size rotation + age-based retention.
//
// Policy (shared with the UI's FileLogger.cs, modelled on UE5CEDumper):
//   <pid>\<category>\current.log           ALWAYS the live file
//   <pid>\<category>\YYYYMMDD-HHMMSS.log   an archived 8 MB chunk
//   anything older than LogRetentionDays   deleted by RunRetentionSweep()
//
// The archive name is stamped from the file's OWN last-write time, never
// "now", so the age prune keys on the real date of the content.
//
// Retention is by age, not a file count. The old fixed shuffle
// (current.1 .. current.3, oldest dropped) and the UI's "keep the newest 15
// PID folders" both evicted by count, so a few short game launches in one
// afternoon discarded everything before them regardless of how recent.

#include "Logger.h"
#include "MagicStrings.h"

#include <windows.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <unordered_set>

namespace fs = std::filesystem;

namespace Discrete::Log {

namespace {

std::mutex g_mutex;
fs::path g_logRoot;          // <base>\<pid>
bool g_initialized = false;
std::atomic<bool> g_sweepDone{false};

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

// %LOCALAPPDATA%\discrete\Logs — parent of every per-PID folder and of ui\.
fs::path LogBaseDirectory() {
    wchar_t buf[MAX_PATH];
    DWORD n = ::GetEnvironmentVariableW(MagicStrings::LogRootEnvVar, buf, MAX_PATH);
    fs::path base = (n > 0 && n < MAX_PATH) ? fs::path(buf) : fs::temp_directory_path();
    return base / MagicStrings::LogRootSubpath;
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

// ---------------------------------------------------------------------------
// File-time helpers (Win32 only — no CRT time zone state involved)
// ---------------------------------------------------------------------------

// FILETIME ticks are 100 ns, so one day is 24*60*60*10^7.
constexpr ULONGLONG kTicksPerDay = 864000000000ULL;

ULONGLONG AsU64(const FILETIME& ft) {
    ULARGE_INTEGER u;
    u.LowPart  = ft.dwLowDateTime;
    u.HighPart = ft.dwHighDateTime;
    return u.QuadPart;
}

bool FileWriteTime(const fs::path& p, ULONGLONG& out) {
    WIN32_FILE_ATTRIBUTE_DATA fad{};
    if (!::GetFileAttributesExW(p.c_str(), GetFileExInfoStandard, &fad)) return false;
    out = AsU64(fad.ftLastWriteTime);
    return true;
}

ULONGLONG NowTicks() {
    FILETIME ft{};
    ::GetSystemTimeAsFileTime(&ft);
    return AsU64(ft);
}

// "YYYYMMDD-HHMMSS" in LOCAL time, from a UTC FILETIME.
std::wstring StampFor(ULONGLONG utcTicks) {
    ULARGE_INTEGER u;
    u.QuadPart = utcTicks;
    FILETIME utc{u.LowPart, u.HighPart};
    FILETIME local{};
    SYSTEMTIME st{};
    if (!::FileTimeToLocalFileTime(&utc, &local)) return {};
    if (!::FileTimeToSystemTime(&local, &st)) return {};
    wchar_t buf[32];
    std::swprintf(buf, 32, L"%04u%02u%02u-%02u%02u%02u",
                  st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return buf;
}

// Enumerate `dir` WITHOUT the throwing increment. A range-for over
// directory_iterator advances with the throwing operator++, so a file
// vanishing mid-enumeration would throw out of a logger that must never
// throw. Returns false when enumeration failed at ANY point — callers must
// not read that as "the directory is empty" (one of those is a reason to
// delete things).
template <typename Fn>
bool ForEachDirEntry(const fs::path& dir, Fn&& fn) {
    std::error_code ec;
    fs::directory_iterator it(dir, ec);
    if (ec) return false;
    const fs::directory_iterator last{};
    while (it != last) {
        fn(*it);
        it.increment(ec);
        if (ec) return false;
    }
    return true;
}

// ---------------------------------------------------------------------------
// Rotation
// ---------------------------------------------------------------------------

// Rename `src` to <dir>\<its own mtime>.log ("-N" on a same-second clash).
// On failure the file is LEFT in place (the next write retries) — losing the
// log would be worse than one oversized file.
void ArchiveByWriteTime(const fs::path& src, const fs::path& dir) {
    ULONGLONG t = 0;
    std::wstring stamp;
    if (FileWriteTime(src, t)) stamp = StampFor(t);
    if (stamp.empty()) return;

    std::error_code ec;
    for (int dup = 0; dup < 100; ++dup) {
        std::wstring name = stamp;
        if (dup > 0) name += L"-" + std::to_wstring(dup);
        name += L".log";
        fs::path dst = dir / name;
        if (fs::exists(dst, ec)) continue;
        fs::rename(src, dst, ec);
        return;
    }
}

// Caller holds g_mutex.
void RotateIfNeeded(const fs::path& dir) {
    fs::path current = dir / MagicStrings::LogCurrentFileName;
    std::error_code ec;
    auto size = fs::file_size(current, ec);
    if (ec || size < MagicStrings::LogMaxFileBytes) return;
    ArchiveByWriteTime(current, dir);
}

// ---------------------------------------------------------------------------
// Retention
// ---------------------------------------------------------------------------

bool IsPidFolderName(const std::wstring& name) {
    if (name.empty()) return false;
    for (wchar_t c : name) {
        if (c < L'0' || c > L'9') return false;
    }
    return true;
}

// Newest last-write time of any file up to two levels down (<pid>\<cat>\file).
// Returns false when the folder could not be fully enumerated.
bool NewestFileTime(const fs::path& dir, int depth, ULONGLONG& newest, bool& sawFile) {
    bool ok = true;
    bool enumerated = ForEachDirEntry(dir, [&](const fs::directory_entry& e) {
        std::error_code ec;
        if (e.is_directory(ec)) {
            if (depth > 0 && !NewestFileTime(e.path(), depth - 1, newest, sawFile)) ok = false;
            return;
        }
        ULONGLONG t = 0;
        if (!FileWriteTime(e.path(), t)) return;
        sawFile = true;
        if (t > newest) newest = t;
    });
    return enumerated && ok;
}

struct SweepCounts {
    int folders = 0;
    int files = 0;
};

// Delete archived *.log files older than the window in one category folder.
// current.log is never touched (a live writer may own it).
void PruneAgedArchives(const fs::path& catDir, ULONGLONG now, ULONGLONG maxAge, SweepCounts& n) {
    ForEachDirEntry(catDir, [&](const fs::directory_entry& e) {
        std::error_code ec;
        if (!e.is_regular_file(ec)) return;
        if (e.path().extension() != L".log") return;
        if (_wcsicmp(e.path().filename().c_str(), MagicStrings::LogCurrentFileName) == 0) return;
        ULONGLONG t = 0;
        if (!FileWriteTime(e.path(), t)) return;
        std::error_code rmEc;  // per-file: one locked file must not stop the sweep
        if (now > t && (now - t) > maxAge && fs::remove(e.path(), rmEc)) ++n.files;
    });
}

void SweepLogBase(const fs::path& base, const std::wstring& ownPid, SweepCounts& n) {
    const ULONGLONG now = NowTicks();
    const ULONGLONG maxAge = static_cast<ULONGLONG>(MagicStrings::LogRetentionDays) * kTicksPerDay;

    ForEachDirEntry(base, [&](const fs::directory_entry& entry) {
        std::error_code ec;
        if (!entry.is_directory(ec)) return;
        const std::wstring name = entry.path().filename().wstring();
        // Only DLL per-PID folders. ui\ belongs to the UI's own sweep.
        if (!IsPidFolderName(name) || name == ownPid) return;

        ULONGLONG newest = 0;
        bool sawFile = false;
        // Half-read folder: never guess it is empty or stale.
        if (!NewestFileTime(entry.path(), 1, newest, sawFile)) return;

        std::error_code rmEc;
        // Judged by the newest file INSIDE, not the folder's own mtime (Windows
        // does not bump it when an existing child is written), so a running
        // game's folder — files seconds old — can never be selected.
        if (!sawFile || (now > newest && (now - newest) > maxAge)) {
            if (fs::remove_all(entry.path(), rmEc) != static_cast<std::uintmax_t>(-1) && !rmEc) ++n.folders;
            return;
        }
        ForEachDirEntry(entry.path(), [&](const fs::directory_entry& cat) {
            std::error_code catEc;
            if (cat.is_directory(catEc)) PruneAgedArchives(cat.path(), now, maxAge, n);
        });
    });
}

}  // namespace

void Init() {
    std::lock_guard lock(g_mutex);
    if (g_initialized) return;
    g_logRoot = LogBaseDirectory() / std::to_wstring(::GetCurrentProcessId());
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

    fs::path file = dir / MagicStrings::LogCurrentFileName;
    std::ofstream out(file, std::ios::app | std::ios::binary);
    if (!out) return;
    out << Timestamp() << message << '\n';
}

void Init(std::string_view message) {
    Write(MagicStrings::LogCategoryInit, message);
}

void RunRetentionSweep() {
    // Copy under the lock, then sweep UNLOCKED: holding g_mutex across the
    // file-system walk would stall every logging thread, and Write() below
    // takes the (non-recursive) mutex itself.
    fs::path ownDir;
    {
        std::lock_guard lock(g_mutex);
        if (!g_initialized) return;
        ownDir = g_logRoot;
    }
    // Latch after the readiness check so an early call does not consume it.
    if (g_sweepDone.exchange(true)) return;

    const ULONGLONG t0 = ::GetTickCount64();
    SweepCounts n;
    SweepLogBase(ownDir.parent_path(), ownDir.filename().wstring(), n);

    char msg[192];
    std::snprintf(msg, sizeof(msg),
                  "Log retention sweep: removed %d PID folder(s) and %d archive(s) "
                  "older than %d days in %llu ms",
                  n.folders, n.files, MagicStrings::LogRetentionDays,
                  static_cast<unsigned long long>(::GetTickCount64() - t0));
    Write(MagicStrings::LogCategoryInit, msg);
}

void Shutdown() {
    std::lock_guard lock(g_mutex);
    g_initialized = false;
}

}  // namespace Discrete::Log
