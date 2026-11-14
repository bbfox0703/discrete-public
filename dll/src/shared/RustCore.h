// RustCore.h — C++ side of the Rust core (dll/rust, Phase R0).
//
// Init() wires the Rust log sink into Discrete::Log and writes the Rust core
// banner to the init log. Call it once, after Log::Init(), from the DLL's
// attach path. The proxy DLLs never call it (they don't link the Rust core).
#pragma once

namespace Discrete::RustCore {

void Init();

}  // namespace Discrete::RustCore
