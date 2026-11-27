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

// D-M2: Compile-time C ABI layout checks matching the Rust `const _: ()`
// assertions in `dll/rust/src/lib.rs` for EVERY #[repr(C)] struct in
// `discrete_core.h` on both x64 and x86. Any field drift breaks compilation.
#if INTPTR_MAX == INT64_MAX
static_assert(sizeof(DcBytes) == 16 && alignof(DcBytes) == 8);
static_assert(offsetof(DcBytes, ptr) == 0 && offsetof(DcBytes, len) == 8);

static_assert(sizeof(DcTypeDef) == 72 && alignof(DcTypeDef) == 8);
static_assert(offsetof(DcTypeDef, index) == 0);
static_assert(offsetof(DcTypeDef, ns) == 8);
static_assert(offsetof(DcTypeDef, name) == 24);
static_assert(offsetof(DcTypeDef, declaring_type_index) == 40);
static_assert(offsetof(DcTypeDef, field_start) == 44);
static_assert(offsetof(DcTypeDef, field_count) == 48);
static_assert(offsetof(DcTypeDef, method_start) == 52);
static_assert(offsetof(DcTypeDef, method_count) == 56);
static_assert(offsetof(DcTypeDef, flags) == 60);
static_assert(offsetof(DcTypeDef, bitfield) == 64);
static_assert(offsetof(DcTypeDef, token) == 68);

static_assert(sizeof(DcFieldDef) == 32 && alignof(DcFieldDef) == 8);
static_assert(offsetof(DcFieldDef, index) == 0);
static_assert(offsetof(DcFieldDef, name) == 8);
static_assert(offsetof(DcFieldDef, type_index) == 24);
static_assert(offsetof(DcFieldDef, token) == 28);

static_assert(sizeof(DcMethodDef) == 48 && alignof(DcMethodDef) == 8);
static_assert(offsetof(DcMethodDef, index) == 0);
static_assert(offsetof(DcMethodDef, name) == 8);
static_assert(offsetof(DcMethodDef, declaring_type_index) == 24);
static_assert(offsetof(DcMethodDef, return_type_index) == 28);
static_assert(offsetof(DcMethodDef, parameter_start) == 32);
static_assert(offsetof(DcMethodDef, parameter_count) == 36);
static_assert(offsetof(DcMethodDef, flags) == 40);
static_assert(offsetof(DcMethodDef, token) == 44);

static_assert(sizeof(DcParameterDef) == 32 && alignof(DcParameterDef) == 8);
static_assert(offsetof(DcParameterDef, index) == 0);
static_assert(offsetof(DcParameterDef, name) == 8);
static_assert(offsetof(DcParameterDef, type_index) == 24);
static_assert(offsetof(DcParameterDef, token) == 28);

static_assert(sizeof(DcImageDef) == 48 && alignof(DcImageDef) == 8);
static_assert(offsetof(DcImageDef, index) == 0);
static_assert(offsetof(DcImageDef, name) == 8);
static_assert(offsetof(DcImageDef, type_start) == 24);
static_assert(offsetof(DcImageDef, type_count) == 28);
static_assert(offsetof(DcImageDef, token) == 32);
static_assert(offsetof(DcImageDef, method_start) == 36);
static_assert(offsetof(DcImageDef, method_end) == 40);

static_assert(sizeof(DcFieldDefault) == 32 && alignof(DcFieldDefault) == 8);
static_assert(offsetof(DcFieldDefault, field_index) == 0);
static_assert(offsetof(DcFieldDefault, type_index) == 4);
static_assert(offsetof(DcFieldDefault, data_index) == 8);
static_assert(offsetof(DcFieldDefault, data) == 16);

static_assert(sizeof(DcAobResult) == 64 && alignof(DcAobResult) == 8);
static_assert(offsetof(DcAobResult, length) == 0);
static_assert(offsetof(DcAobResult, instruction_count) == 8);
static_assert(offsetof(DcAobResult, rel_branch_count) == 16);
static_assert(offsetof(DcAobResult, rip_rel_count) == 24);
static_assert(offsetof(DcAobResult, bailed_at) == 32);
static_assert(offsetof(DcAobResult, requested_length) == 40);
static_assert(offsetof(DcAobResult, pattern_len) == 48);
static_assert(offsetof(DcAobResult, decoder_bailed) == 56);
static_assert(offsetof(DcAobResult, stopped_at_terminal) == 57);

static_assert(sizeof(DcChainMatch) == 24 && alignof(DcChainMatch) == 8);
static_assert(offsetof(DcChainMatch, hit_ip) == 0);
static_assert(offsetof(DcChainMatch, hop_one_ip) == 8);
static_assert(offsetof(DcChainMatch, is_write) == 16);

static_assert(sizeof(DcChainStats) == 48 && alignof(DcChainStats) == 8);
static_assert(offsetof(DcChainStats, hop_one_candidates) == 0);
static_assert(offsetof(DcChainStats, chain_broken) == 8);
static_assert(offsetof(DcChainStats, chains_emitted) == 16);
static_assert(offsetof(DcChainStats, bytes_scanned) == 24);
static_assert(offsetof(DcChainStats, failed_reads) == 32);
static_assert(offsetof(DcChainStats, valid) == 40);

static_assert(sizeof(DcXrefSection) == 16 && alignof(DcXrefSection) == 8);
static_assert(offsetof(DcXrefSection, va) == 0 && offsetof(DcXrefSection, size) == 8);

static_assert(sizeof(DcXrefRipHit) == 16 && alignof(DcXrefRipHit) == 8);
static_assert(offsetof(DcXrefRipHit, ip) == 0 && offsetof(DcXrefRipHit, opcode) == 8);

static_assert(sizeof(DcXrefRange) == 16 && alignof(DcXrefRange) == 8);
static_assert(offsetof(DcXrefRange, start) == 0 && offsetof(DcXrefRange, len) == 8);

static_assert(sizeof(DcXrefInstHit) == 24 && alignof(DcXrefInstHit) == 8);
static_assert(offsetof(DcXrefInstHit, method) == 0);
static_assert(offsetof(DcXrefInstHit, ip) == 8);
static_assert(offsetof(DcXrefInstHit, write) == 16);
#elif INTPTR_MAX == INT32_MAX
static_assert(sizeof(DcBytes) == 8 && alignof(DcBytes) == 4);
static_assert(offsetof(DcBytes, ptr) == 0 && offsetof(DcBytes, len) == 4);

static_assert(sizeof(DcTypeDef) == 52 && alignof(DcTypeDef) == 4);
static_assert(offsetof(DcTypeDef, index) == 0);
static_assert(offsetof(DcTypeDef, ns) == 4);
static_assert(offsetof(DcTypeDef, name) == 12);
static_assert(offsetof(DcTypeDef, declaring_type_index) == 20);
static_assert(offsetof(DcTypeDef, field_start) == 24);
static_assert(offsetof(DcTypeDef, field_count) == 28);
static_assert(offsetof(DcTypeDef, method_start) == 32);
static_assert(offsetof(DcTypeDef, method_count) == 36);
static_assert(offsetof(DcTypeDef, flags) == 40);
static_assert(offsetof(DcTypeDef, bitfield) == 44);
static_assert(offsetof(DcTypeDef, token) == 48);

static_assert(sizeof(DcFieldDef) == 20 && alignof(DcFieldDef) == 4);
static_assert(offsetof(DcFieldDef, index) == 0);
static_assert(offsetof(DcFieldDef, name) == 4);
static_assert(offsetof(DcFieldDef, type_index) == 12);
static_assert(offsetof(DcFieldDef, token) == 16);

static_assert(sizeof(DcMethodDef) == 36 && alignof(DcMethodDef) == 4);
static_assert(offsetof(DcMethodDef, index) == 0);
static_assert(offsetof(DcMethodDef, name) == 4);
static_assert(offsetof(DcMethodDef, declaring_type_index) == 12);
static_assert(offsetof(DcMethodDef, return_type_index) == 16);
static_assert(offsetof(DcMethodDef, parameter_start) == 20);
static_assert(offsetof(DcMethodDef, parameter_count) == 24);
static_assert(offsetof(DcMethodDef, flags) == 28);
static_assert(offsetof(DcMethodDef, token) == 32);

static_assert(sizeof(DcParameterDef) == 20 && alignof(DcParameterDef) == 4);
static_assert(offsetof(DcParameterDef, index) == 0);
static_assert(offsetof(DcParameterDef, name) == 4);
static_assert(offsetof(DcParameterDef, type_index) == 12);
static_assert(offsetof(DcParameterDef, token) == 16);

static_assert(sizeof(DcImageDef) == 32 && alignof(DcImageDef) == 4);
static_assert(offsetof(DcImageDef, index) == 0);
static_assert(offsetof(DcImageDef, name) == 4);
static_assert(offsetof(DcImageDef, type_start) == 12);
static_assert(offsetof(DcImageDef, type_count) == 16);
static_assert(offsetof(DcImageDef, token) == 20);
static_assert(offsetof(DcImageDef, method_start) == 24);
static_assert(offsetof(DcImageDef, method_end) == 28);

static_assert(sizeof(DcFieldDefault) == 20 && alignof(DcFieldDefault) == 4);
static_assert(offsetof(DcFieldDefault, field_index) == 0);
static_assert(offsetof(DcFieldDefault, type_index) == 4);
static_assert(offsetof(DcFieldDefault, data_index) == 8);
static_assert(offsetof(DcFieldDefault, data) == 12);

static_assert(sizeof(DcAobResult) == 32 && alignof(DcAobResult) == 4);
static_assert(offsetof(DcAobResult, length) == 0);
static_assert(offsetof(DcAobResult, instruction_count) == 4);
static_assert(offsetof(DcAobResult, rel_branch_count) == 8);
static_assert(offsetof(DcAobResult, rip_rel_count) == 12);
static_assert(offsetof(DcAobResult, bailed_at) == 16);
static_assert(offsetof(DcAobResult, requested_length) == 20);
static_assert(offsetof(DcAobResult, pattern_len) == 24);
static_assert(offsetof(DcAobResult, decoder_bailed) == 28);
static_assert(offsetof(DcAobResult, stopped_at_terminal) == 29);

static_assert(sizeof(DcChainMatch) == 12 && alignof(DcChainMatch) == 4);
static_assert(offsetof(DcChainMatch, hit_ip) == 0);
static_assert(offsetof(DcChainMatch, hop_one_ip) == 4);
static_assert(offsetof(DcChainMatch, is_write) == 8);

static_assert(sizeof(DcChainStats) == 24 && alignof(DcChainStats) == 4);
static_assert(offsetof(DcChainStats, hop_one_candidates) == 0);
static_assert(offsetof(DcChainStats, chain_broken) == 4);
static_assert(offsetof(DcChainStats, chains_emitted) == 8);
static_assert(offsetof(DcChainStats, bytes_scanned) == 12);
static_assert(offsetof(DcChainStats, failed_reads) == 16);
static_assert(offsetof(DcChainStats, valid) == 20);

static_assert(sizeof(DcXrefSection) == 8 && alignof(DcXrefSection) == 4);
static_assert(offsetof(DcXrefSection, va) == 0 && offsetof(DcXrefSection, size) == 4);

static_assert(sizeof(DcXrefRipHit) == 8 && alignof(DcXrefRipHit) == 4);
static_assert(offsetof(DcXrefRipHit, ip) == 0 && offsetof(DcXrefRipHit, opcode) == 4);

static_assert(sizeof(DcXrefRange) == 8 && alignof(DcXrefRange) == 4);
static_assert(offsetof(DcXrefRange, start) == 0 && offsetof(DcXrefRange, len) == 4);

static_assert(sizeof(DcXrefInstHit) == 12 && alignof(DcXrefInstHit) == 4);
static_assert(offsetof(DcXrefInstHit, method) == 0);
static_assert(offsetof(DcXrefInstHit, ip) == 4);
static_assert(offsetof(DcXrefInstHit, write) == 8);
#else
#error "Unsupported pointer width for discrete_core ABI checks"
#endif

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
