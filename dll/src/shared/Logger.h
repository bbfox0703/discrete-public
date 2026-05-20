// Logger.h — 5-category, per-PID, rotating file logger.
// Categories: init / scan / metadata / pipe / walk (see MagicStrings.h).
// Each category writes to %LOCALAPPDATA%\Discrete_UEDumper\Logs\<pid>\<category>\current.log.
// On reaching LogMaxFileBytes the file is rotated; up to LogRotationFileCount files kept.
#pragma once

#include <string>
#include <string_view>

namespace UnityCEDumper::Log {

void Init();
void Shutdown();

void Write(std::wstring_view category, std::string_view message);

// Convenience: write to "init" category.
void Init(std::string_view message);

}  // namespace UnityCEDumper::Log
