// RustCore.cpp — see RustCore.h.

#include "RustCore.h"

#include "Logger.h"
#include "MagicStrings.h"

#include <discrete_core.h>

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

namespace Discrete::RustCore {

namespace {

// DC_LOG_* -> the C++ category names, so the names stay in MagicStrings.h.
std::wstring_view CategoryFor(std::uint32_t category) noexcept {
    switch (category) {
        case DC_LOG_SCAN:     return MagicStrings::LogCategoryScan;
        case DC_LOG_METADATA: return MagicStrings::LogCategoryMetadata;
        case DC_LOG_PIPE:     return MagicStrings::LogCategoryPipe;
        case DC_LOG_WALK:     return MagicStrings::LogCategoryWalk;
        case DC_LOG_INIT:
        default:              return MagicStrings::LogCategoryInit;
    }
}

// Called by Rust on any thread. Log::Write is thread-safe and must not let a
// C++ exception escape into Rust frames (that would be UB), hence the catch.
void RustLogSink(std::uint32_t category, const char* msg, std::size_t len) noexcept {
    try {
        Discrete::Log::Write(CategoryFor(category), std::string_view(msg, len));
    } catch (...) {
        // Dropping a log line beats crashing the host.
    }
}

}  // namespace

void Init() {
    dc_core_set_log_sink(&RustLogSink);
    Discrete::Log::Init(std::string("Rust core: ") + dc_core_version());
}

}  // namespace Discrete::RustCore
