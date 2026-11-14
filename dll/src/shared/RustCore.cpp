// RustCore.cpp — see RustCore.h.

#include "RustCore.h"

#include "Logger.h"
#include "MagicStrings.h"
#include "Memory.h"

#include "BuildInfo.h"  // generated: DISCRETE_BUILD_BANNER

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

extern "C" std::int32_t SafeReadShim(void* /*ctx*/, std::uintptr_t addr,
                                     std::uint8_t* dst, std::size_t len) noexcept {
    if (dst == nullptr) return 0;
    if (len == 0) return 1;
    return Discrete::Memory::SafeReadBytes(reinterpret_cast<const void*>(addr), dst, len) ? 1 : 0;
}

void Init() {
    dc_core_set_log_sink(&RustLogSink);
    const std::string banner = dc_core_version();
    Discrete::Log::Init("Rust core: " + banner);
    // Both banners come from the same CMake configure (RustCore.cmake passes
    // BuildInfo.h's values to cargo), so a mismatch means a stale
    // discrete_core.lib got linked.
    constexpr std::string_view expected = "discrete_core " DISCRETE_BUILD_BANNER;
    if (banner != expected) {
        Discrete::Log::Init("WARN: Rust core banner does not match this DLL build (" +
                            std::string(expected) + ") - stale discrete_core.lib?");
    }
}

}  // namespace Discrete::RustCore
