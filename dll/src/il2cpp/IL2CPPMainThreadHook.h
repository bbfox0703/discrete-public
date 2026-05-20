// IL2CPPMainThreadHook.h — Phase 20-MT (IL2CPP backend).
//
// Mirror of MonoMainThreadHook for the IL2CPP backend. Detours
// `il2cpp_runtime_invoke` so we can piggyback our main-thread queue
// drain on Unity's per-frame MonoBehaviour Update dispatch. See
// `MonoMainThreadHook.h` for the architecture; everything is the same
// except the runtime function symbol + IL2CPP-typed signature.

#pragma once

namespace UnityCEDumper::IL2CPP::MainThreadHook {

bool Install();
void Uninstall();
bool IsInstalled();

}  // namespace UnityCEDumper::IL2CPP::MainThreadHook
