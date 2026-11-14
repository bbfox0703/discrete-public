// RustCore.h — C++ side of the Rust core (dll/rust, Phase R0).
//
// Init() wires the Rust log sink into Discrete::Log and writes the Rust core
// banner to the init log. Call it once, after Log::Init(), from the DLL's
// attach path. The proxy DLLs never call it (they don't link the Rust core).
#pragma once

#include <cstddef>
#include <cstdint>

namespace Discrete::RustCore {

void Init();

// Phase R2 — the SEH read shim handed to Rust as a `dc_read_fn`
// (discrete_core.h). Copies `len` bytes from `addr` with
// Memory::SafeReadBytes; returns 1 on success, 0 on any fault. `ctx` unused.
// Rust code never dereferences foreign memory itself — an access violation
// unwinding through Rust frames would be undefined behaviour.
extern "C" std::int32_t SafeReadShim(void* ctx, std::uintptr_t addr,
                                     std::uint8_t* dst, std::size_t len) noexcept;

}  // namespace Discrete::RustCore
