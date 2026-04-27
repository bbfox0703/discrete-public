// Logger.h — Phase 0 minimal stub. Full 5-category rotation logger lands in Phase 1.
#pragma once

#include <string>
#include <string_view>

namespace UnityCEDumper::Log {

// Initialize logging. Creates %LOCALAPPDATA%\UnityCEDumper\Logs\<pid>\<category>\.
void Init();

// Write a single line to the named category log file.
void Write(std::wstring_view category, std::string_view message);

// Convenience: write to "init" category.
void Init(std::string_view message);

void Shutdown();

}  // namespace UnityCEDumper::Log
