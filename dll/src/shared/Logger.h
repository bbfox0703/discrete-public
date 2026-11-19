// Logger.h — 5-category, per-PID file logger with age-based retention.
// Categories: init / scan / metadata / pipe / walk (see MagicStrings.h).
// Each category writes to %LOCALAPPDATA%\discrete\Logs\<pid>\<category>\current.log.
//
// Rotation: on reaching MagicStrings::LogMaxFileBytes, current.log is archived
// to <YYYYMMDD-HHMMSS>.log (stamped from its own last-write time) in the same
// folder and a fresh current.log starts. A PID folder is not always one run:
// Windows recycles PIDs, so Init() archives (the same way) any current.log
// last written before this process was created — an earlier process that had
// the same PID. Logs written by other copies in this process are kept, so
// there is otherwise no per-start rotation.
//
// Retention: RunRetentionSweep() deletes other processes' per-PID folders whose
// newest file is older than MagicStrings::LogRetentionDays (or that are empty),
// and archived *.log files past that age inside the folders it keeps. The UI
// runs the same sweep at its startup (FileLogger.RunRetentionSweep).
//
// Linked into the proxy DLLs too — they only call Init/Write, and only from
// their worker thread (post-CRT, off the loader lock). Nothing here may be
// called from a proxy before Proxy_CrtReady().
#pragma once

#include <string>
#include <string_view>

namespace Discrete::Log {

void Init();
void Shutdown();

void Write(std::wstring_view category, std::string_view message);

// Convenience: write to "init" category.
void Init(std::string_view message);

// One-shot age-based retention sweep of the log root (see header comment).
// Does file-system work proportional to the number of old PID folders, so it
// MUST run off the loader lock — main.cpp calls it from a short-lived worker
// thread. No-op before Init() or after the first successful call.
void RunRetentionSweep();

}  // namespace Discrete::Log
