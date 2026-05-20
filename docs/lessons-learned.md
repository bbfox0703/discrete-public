# Lessons Learned

> Engineering gotchas accumulated across the project — things that took
> a debugging session to figure out the first time and that we never
> want to relearn from scratch. Each entry leads with the symptom, then
> the root cause, then the rule. Cross-references to source live as
> [file.ext:line](../path/file.ext) links.

## Contents

- [Mono / IL2CPP runtime invocation](#mono--il2cpp-runtime-invocation)
  — `runtime_invoke` non-uniform marshaling, exception address at
  `+0x18`, missing `method_get_pointer` export on Unity 2021.3+.
- [Overload disambiguation](#overload-disambiguation) — IL2CPP code
  stripping, enum-marshal-as-int32, `signatureMatch` substring traps,
  AA-script idempotency.
- [GC-root lifetime for `callMethod` returns](#gc-root-lifetime-for-callmethod-returns)
  — managed return values can be collected before the caller reads them.
- [Avalonia 12 Windows-only build](#avalonia-12-windows-only-build) —
  Skia + HarfBuzz + Win32 wiring, AOT analyzer strictness, the
  **DataGrid AOT sort three-layer trap**.
- [Native AOT + integrity-stamping (Phase 26)](#native-aot--integrity-stamping-phase-26) —
  `InternalsVisibleTo` matches AssemblyName, AOT preserves byte
  literals but single-file deflate-compresses them, anti-trim idiom
  for padding arrays.
- [PowerShell smoke testing](#powershell-smoke-testing) — pipe
  enveloping, single-element JSON array unwrap, Boolean parameter
  passing.
- [Build / DLL development](#build--dll-development) — SEH-leaf
  helpers, mutex-based single-instance, linker LNK1104 with cached
  DLL handles, AOT trim warnings.
- [Pipe client / connection](#pipe-client--connection) — overlapped
  I/O on `\\.\pipe\*`, concurrent-clients pattern.
- [Antivirus](#antivirus) — Bitdefender / Defender flagging injected DLLs.
- [Cheat Engine Lua API](#cheat-engine-lua-api) — `executeCodeEx`
  string-arg handling, missing `GotoAddressEx`, AA-script `[ENABLE]`
  idempotency, autorun script wire encoding.
- [AOBMaker CE plugin](#aobmaker-ce-plugin) — pipe protocol,
  concurrent-client constraint pre-Phase 22f, AOB extraction surface.

---

## Mono / IL2CPP runtime invocation

### `runtime_invoke` uses **non-uniform** parameter marshaling, not the documented "uniform" convention

**Symptom**: a managed `Parse("42")` call returns `FormatException`
even though every byte on the wire looks correct. Numeric primitive
calls work fine; anything that takes a managed reference type
(`System.String`, user objects, arrays) misbehaves the same way.

**Root cause**: Mono's official documentation describes a *uniform*
convention where `params[i]` always holds a pointer to the slot for
arg *i* — `&intSlot` for value types, `&monoStringPtr` for reference
types. The actual x64 trampoline does not honour that for reference
types: it expects the object pointer **directly** in `params[i]`, not
the address of a stack slot containing the pointer. Every battle-
tested injector in the wild (BepInEx, MelonLoader, Il2CppInterop) uses
the non-uniform form. The Mono docs are wrong (or at minimum mismatched
with the actual trampoline shipped in Unity-vendored Mono).

**Rule**:
- Value types (`int`, `float`, `bool`, etc.): pass `&slot` where
  `slot` holds the value.
- Reference types (`MonoString*`, `Il2CppString*`, any object/array
  pointer): pass the pointer **directly** in `params[i]`.
- `null`: pass `nullptr` directly (don't wrap in a slot).

See `MarshalArg` in [IL2CPPMethodInvoker.cpp](../dll/src/il2cpp/IL2CPPMethodInvoker.cpp)
and [MonoMethodInvoker.cpp](../dll/src/mono/MonoMethodInvoker.cpp) for
the canonical implementation. Both backends share the same convention.

### Managed exception message lives at `Il2CppException + 0x18` / `MonoException + 0x18`

Mono and IL2CPP both inherit `System.Exception` which lays out
`_message` at the same offset. We read it via SEH-protected struct
read and decode the UTF-16 chars into UTF-8 with the same helper the
Inspector uses for `System.String` fields. Stable across Unity 2018+.

### IL2CPP `method_get_pointer` is not exported in Unity 2021.3+

**Symptom**: `il2cpp_method_get_pointer` returns null or the symbol is
missing entirely from a recent IL2CPP game.

**Rule**: read `MethodInfo.methodPointer` directly via SEH-protected
struct read at offset 0. The export-only path is a dead end on modern
Unity.

### Phase 20-MT main-thread marshaling — never `mono_thread_attach` from the Unity main thread

**Symptom**: first-ever `callMethod main_thread=true` against BongoCat
(Mono / Unity 6000.2) instantly crashed the game. The dispatcher's hook
on `mono_runtime_invoke` fired (`tick_count` rose from 16943 → 24041),
the work item reached `Drain`, but the worker thread saw a 5-second
`main-thread queue drain timeout` followed by an empty pipe response =
process dead. **Identical failure mode to the historical build #54
"nested attach destroyed BongoCat" forensic.**

**Root cause**: `Mono::CallMethod` opens a `ThreadAttachGuard` internally
(see `MonoMethodInvoker.cpp:573`). When `Drain` runs that closure from
inside our `mono_runtime_invoke` detour, we're **already on the Unity
main thread**, which is itself attached to the Mono runtime — it's the
thread that initialized Mono in the first place. The guard's
constructor calls `mono_thread_attach(domain)`, which on the main
thread returns the existing handle (apparently a no-op). But the
destructor's `mono_thread_detach(thread_)` unbinds **Unity's own**
attached-thread record. Next time Unity itself calls into the runtime
(within microseconds, since Update() is mid-flight), it segfaults
because its attached-thread structure has been torn out.

**Rule**: any RAII attach/detach guard MUST check
`MainThreadDispatcher::IsCurrentlyDrainingOnMainThread()` and become a
no-op when true. The flag is a `thread_local bool` set in `Drain` just
before invoking each work item's closure (see
`MainThreadDispatcher.cpp` — search `t_inside_work_item`). Same fix
applied to `IL2CPP::ThreadAttachGuard`; the failure mode reproduces
identically on `il2cpp_thread_attach` / `il2cpp_thread_detach`.

**Why the recursion guard alone wasn't enough**: `t_drain_in_progress`
protects against re-entering `Drain` from inside a draining work
item's runtime_invoke call. But that doesn't help with attach/detach
— the issue isn't recursion, it's that **the main thread is already
attached and we shouldn't be touching its attachment state at all**.

**Verified live 2026-05-20**: BongoCat (Mono Unity 6000.2), SaGa F2R
(IL2CPP v29 / Unity 2021.3), Songs of Silence (IL2CPP v39 / Unity
6000.3) — 1000-iteration `Mathf.Abs(-42)` burst on main-thread path,
zero crashes, zero timeouts, drain_count == submit_count == 1000 on
all three.

### Phase 20-MT — x86 inline detour must match the target's calling convention (cdecl, not stdcall)

**Symptom**: first-ever `Test-Handshake.ps1` against LEAVES (32-bit
Mono / Unity 5.4.3f1) succeeded — handshake returned valid data,
`main_thread.hook_installed: True`, `is_healthy: True`. But
`main_thread.tick_count` only ever read **1** across multiple
handshake / scan cycles. The next non-hook Mono call from the game
itself crashed the process. Repeats identically on Legend of Mortal
(Unity 2020.3) until fixed.

**Root cause — calling convention mismatch on x86**. My initial detour
declaration was:

    MonoObject* WINAPI HookedRuntimeInvoke(MonoMethod*, void*, void**, MonoObject**)

On x86 Windows, `WINAPI` expands to `__stdcall`. But Mono is a C
library — its `mono_runtime_invoke` export uses `__cdecl` (Microsoft
compiler's default for C functions not marked otherwise). When MinHook
redirects a cdecl-caller's call into a stdcall-callee, the caller
pushes args cdecl-style and expects to pop them itself; the callee's
`ret <N>` already popped them on exit. The double-pop corrupts the
stack pointer, so the immediate detour run *appears* to work (we
return cleanly into Mono, the very first hook entry didn't fault), but
the very next call into Mono on the corrupted stack faults.

**Why it didn't surface on x64**: Windows x64 has exactly one calling
convention (Microsoft x64 ABI). `WINAPI` / `__stdcall` / `__cdecl` /
default all generate identical code, so the keyword is invisible.
Phase 20-MT's first three live-PASS targets (BongoCat / SaGa F2R /
Songs of Silence) were all x64 — the bug was completely silent.

**Rule**: any MinHook detour function MUST be declared with the same
calling convention as the target export. For Mono / IL2CPP runtime
functions (and most third-party C libraries), that means **drop the
`WINAPI` keyword** — let it default to cdecl. Even on x64 where the
keyword is a no-op, omitting it removes the x86 footgun and makes
the cross-bitness behaviour obvious from the signature. See
`HookedRuntimeInvoke` in
[`MonoMainThreadHook.cpp`](../dll/src/mono/MonoMainThreadHook.cpp)
and [`IL2CPPMainThreadHook.cpp`](../dll/src/il2cpp/IL2CPPMainThreadHook.cpp)
for the canonical shape.

**Diagnostic tell**: `tick_count` stays at 1 right after install + an
immediate game crash means the calling convention is wrong. A tick of
1 (and no growth) means the hook fired once, returned, and the
process died before the second Unity frame. Compare to a working hook
where `tick_count` grows fast (BongoCat: 42 → 56 between handshake
and scan; LEAVES with the fix: 59403 → 96097 across a burst).

**Verified live 2026-05-20**: after the fix, LEAVES (x86 Mono Unity
5.4) and Legend of Mortal (x86 Mono Unity 2020.3) both pass the 1000-
iteration burst smoke cleanly. Full x86 hook drain count = 1000 on
both.

### Phase 20-MT — assembly enumeration name varies across Unity versions (monolithic vs split)

**Symptom**: `Test-MainThreadBurst.ps1` (or any tool that hardcodes
`assembly = "UnityEngine.CoreModule"`) returns
`assembly not found: UnityEngine.CoreModule` against LEAVES (Unity
5.4) even though the handshake reports `assemblies_count: 11`.

**Root cause**: Unity 2017.2 was the first version to split the
monolithic `UnityEngine.dll` into per-module DLLs
(`UnityEngine.CoreModule.dll` + `UnityEngine.UIModule.dll` +
`UnityEngine.PhysicsModule.dll` + ~30 others). Anything older — Unity
5.x, Unity 2017.1 — still ships **one** `UnityEngine.dll` with `Mathf`
/ `Transform` / `Object` / `GameObject` all inside. The split was a
size-on-disk optimisation; the namespace `UnityEngine.*` is
unchanged.

**Rule for tools**: probe `getAssemblies` first and pick the first
match in the priority list
`["UnityEngine.CoreModule", "UnityEngine"]`. The new-name comes first
because every Unity 2017.2+ build has it; old Unity falls through to
the monolithic name. Same pattern any future split would need — keep
the priority list ordered newest-first.

**Verified live 2026-05-20**: LEAVES (Unity 5.4) auto-picks
`UnityEngine`; Legend of Mortal (Unity 2020.3) auto-picks
`UnityEngine.CoreModule`. Both run identical Mathf.Abs(int) burst
through the same code path.

### Phase 20-MT — never clean up MinHook / pipe-worker `join()` from DllMain on process exit

**Symptom**: closing SaGa Frontier 2 Remastered (with our IL2CPP DLL
injected) hung for ~10 seconds between the user clicking the window's
close button and the process actually vanishing from Task Manager.
The per-PID init log (`Discrete_UEDumper\Logs\<pid>\init\current.log`)
stopped at the post-init "hook armed on il2cpp_runtime_invoke" line
with no shutdown banner — meaning `UnityDumper_Shutdown` either never
ran or was blocked before reaching its log line.

**Root cause**: classic Windows DllMain anti-pattern hit by two
independent cleanup paths simultaneously when the process is dying:

  1. `PipeServer::Stop()` calls `std::thread::join()` on the pipe
     worker thread. Under DllMain's loader lock (held by every DLL
     entry-point call), `join()` blocks waiting for the thread to
     finish — but the thread might need the very same loader lock to
     unwind through any Windows API call it's currently in. Classic
     deadlock; only resolved when the thread eventually exits via
     `TerminateProcess` after the OS gives up.

  2. `MH_RemoveHook` (in `MonoMainThreadHook::Uninstall` /
     `IL2CPPMainThreadHook::Uninstall`) calls `SuspendThread` on
     every other thread in the process to verify nobody's currently
     executing the trampoline. Unity has dozens of threads; on a
     process that's mid-termination, some of those threads may already
     be in weird states (TerminateThread, kernel-side cleanup) that
     SuspendThread either blocks on or races against. Adds seconds of
     latency at best, deadlocks at worst.

**Rule**: read `lpReserved` in DllMain. When `DLL_PROCESS_DETACH` fires:

  * `lpReserved == NULL` → `FreeLibrary` was called. Process keeps
    running; we MUST clean up properly because hook trampolines into
    soon-to-be-unmapped code would crash the host.
  * `lpReserved != NULL` → process is terminating. **Skip all
    cleanup.** The OS reclaims handles, threads, virtual memory,
    everything. Cleanup at this point is at best wasted work, at
    worst the deadlock-trap above.

This is the documented Microsoft best practice, not a hack. The MSDN
page on `DllMain` says verbatim:

> When the system calls the DllMain function with the
> DLL_PROCESS_DETACH value, lpReserved is NULL if FreeLibrary has been
> called or the DLL load failed and non-NULL if the process is
> terminating.

**Verified live 2026-05-20**: post-fix, SaGa F2R closes instantly
(< 1 s) regardless of how long the DLL has been injected or how many
ticks the dispatcher has counted. `MainThreadDispatcher` + MinHook
hooks remain in process memory until OS reaps, but nothing references
them again — the runtime is shutting down.

See `dll/src/main.cpp:DllMain` for the branch + the long comment.

### Phase 27 follow-up — 4000+ `thread_attach`/`detach` cycles in a batch leave BDWGC finalizers that stall shutdown for 30-60s

**Symptom**: 活俠傳 (Legend of Mortal, Mono x86, Unity 2020.3.49) hung
for ~44 seconds during shutdown after a single value scan. Test
matrix:
* Open UI → inject → close UI (no scan) → close game = **1 second**
* Open UI → inject → run value scan → close UI → close game = **44 seconds**

Confirmed scan-only — the longer the scan (more whatIsAt calls), the
worse the shutdown.

**Root cause**: each `Mono::WhatIsAt` / `IL2CPP::WhatIsAt` call opens its
own `ThreadAttachGuard` for the duration of the backward scan. A global
value scan that hits 4000+ candidates triggers 4000+
`mono_thread_attach`/`mono_thread_detach` (or il2cpp equivalent) cycles
on the pipe-worker thread within a couple of seconds.

Each attach/detach pair appears to leave a finalizable thread-handle
ref in BDWGC's finalizer queue (the runtime doesn't free thread state
synchronously — it defers to the GC). When the game shuts down
(`Application.Quit` → Mono teardown), BDWGC runs all pending finalizers
before unwinding the domain. 4000+ thread refs × non-trivial cleanup
each = the missing 40+ seconds.

This **isn't** the same issue as the BDWGC class-vtable hang from the
earlier Phase 27a fix. Different mechanism — same family (mass batch
of cheap runtime calls leaving cleanup work for shutdown).

**Fix**: per-thread reentrancy counter in both `ThreadAttachGuard`
variants. Outer guard does the ONE real attach + bumps `depth=1`.
Inner guards see `depth>0` and skip the runtime call entirely (just
balance the counter on destruction). Scan handlers (Mono + IL2CPP ×
scanFieldValue + beginValueScan) now open ONE outer guard before the
WhatIsAt batch. All 4000 inner guards become counter-only no-ops.

Net cost: **1 attach/detach per scan instead of 4000**. Shutdown back
to baseline (~1 s).

**Generalization**: any batch loop that calls Mono / IL2CPP runtime
APIs in a tight loop should open ONE outer `ThreadAttachGuard` at the
batch level. The reentrancy counter makes this safe (inner guards
inside helper functions become no-ops). This also makes the existing
"pipe handlers must hold their own guard around direct resolver calls"
policy (MonoThreadAttach.h header) safer to follow — nested guards
are no longer a footgun for the OUR-OWN-CODE case (Unity main-thread
nested attach is still a footgun and still has its own skip via
`IsCurrentlyDrainingOnMainThread`).

**How to spot this in the future**: if shutting down the host process
takes noticeably longer when a feature has been used, suspect either
(a) accumulated runtime-handle refs queued for BDWGC finalization,
or (b) C++ static-storage container destructors freeing many small
allocations. The fix for (a) is reentrant attach (this lesson); the
fix for (b) is heap-leaked singletons (the prior lesson above).

---

### Phase 27a — mass-batch Mono runtime API calls wedge legacy 32-bit Mono when main thread is focus-paused

**Symptom**: 活俠傳 (Legend of Mortal, Unity 2020.3.49f1, x86 Mono) hangs
indefinitely after a global `scanFieldValue` RPC completes. Audio
keeps playing (separate thread), but the main thread never recovers.
The pipe-worker thread is idle by the time the hang shows — the script
already finished, disconnected, and exited.

**Root cause**: the pre-Phase-27 `Mono::BuildClassRangeCache` eagerly
called `mono_class_vtable(domain, klass)` for **every** class in the
domain (17,341 calls on this target) inside a single `ThreadAttachGuard`
scope on the pipe worker thread. Each call:
* Touches BDWGC's internal GC lock.
* Allocates a vtable slot if the class has never had one before.
* Records the vtable in Mono's domain bookkeeping.

When the game's main thread is concurrently focus-paused (alt-tab to a
debug console; `Application.runInBackground = false` is still the
default for many 2018-2021-era Unity games), the runtime never gets
a chance to acknowledge the bookkeeping changes. Once focus returns
and the main thread tries to resume, it finds the runtime in an
inconsistent state and hangs on the first managed allocation.

Yielding with `Sleep(0)` every 256 classes did NOT help — the issue
isn't CPU starvation, it's that the runtime expects the main thread
to participate in its bookkeeping handshake but the main thread can't.

**Fix**: defer the heavy runtime touch to runtime. New design:
* `BuildClassRangeCache` caches **only `MonoClass*`** (cheap pointer
  enumeration via `EnumerateAssemblies` → `EnumerateClasses`, ~500 ms,
  zero `mono_class_vtable` calls, no outer `ThreadAttachGuard`).
* `WhatIsAt`'s backward-scan reads each candidate qword (assumed to be
  a `MonoVTable*`) and does one SEH-protected `SafeReadBytes` to
  fetch `vtable[0] = MonoClass*`. That's a pure process-memory read —
  no runtime call, no GC lock.
* Repeat hits skip the deref via a lazily-populated `g_vtableToKlass`
  memo that grows as scans encounter new vtables.

**Verified**: build #423 ran twice on 活俠傳 with the test script — no
hang, ~1.5 s elapsed (vs ~3 s before AND no longer fragile), correctly
identified `Mortal.Core.GameStat._value = 10160` both times.

**Generalization**: any pipe-handler code path that calls a Mono /
IL2CPP runtime API in a tight loop over thousands of items is a hang
risk on focus-pausing targets. Prefer lazy / on-demand resolution over
eager batches. Legacy Mono is the most fragile; IL2CPP and modern
Mono (Unity 6+) tolerate the pattern but still pay the cost.

---

### Phase 20-MT — Unity 2018 pauses the main loop when window loses focus

**Symptom**: handshake against R-Type Dimensions EX (Unity 2018.1.9f2)
returns `main_thread.is_healthy: false, tick_count: 0,
last_tick_age_ms: -1`. The hook is installed (`hook_installed: true`,
main thread id captured), but it has never fired. Burst smoke aborts
with "Dispatcher NOT healthy".

**Root cause**: Unity 2018 ships with `Application.runInBackground =
false` by default. When the game window doesn't have foreground focus,
Unity pauses the entire main loop — no Update() callbacks, no
`runtime_invoke`, no rendering. Our hook only fires from inside
`runtime_invoke`, so it correctly reports no recent ticks.

Unity 2019+ flipped the default to `true`, so this is exclusively a
Unity 2018-era problem. The runtime detection itself is correct —
we're not misidentifying the main thread; the main thread is simply
sleeping.

**Rule for users**: keep the Unity 2018 game window in foreground
focus when running main-thread-routing RPCs. Alt-tab to PowerShell is
fine for typing the command, but once `Test-MainThreadBurst.ps1` is
running you need the game window foreground OR the game's own
"Allow background play" setting flipped on (if it exposes one in its
Settings menu).

**Rule for the codebase**: `MainThreadDispatcher::IsHealthy()` correctly
returns false in this state and `SubmitAndWait` would all time out.
Callers should check `IsHealthy` (exposed via `getDispatcherStats` /
the `main_thread.is_healthy` handshake field) before routing through
the queue, and fall back to the worker-thread path when stale.

---

## Overload disambiguation

### IL2CPP code stripping removes "unused" Unity APIs — verify with `findMethodInstances`

**Symptom**: `callMethod` against a Unity engine API (e.g.
`Resources.FindObjectsOfTypeAll`, `SceneManager.get_sceneCount`,
`GameObject.FindWithTag`) returns `error: method_not_found` even though
the method is right there in Unity's official documentation and works
in any Editor / non-IL2CPP build.

**Root cause**: IL2CPP build option **Managed Stripping Level**. At
"Low" (default for many shipping titles) and above, the IL2CPP linker
removes any managed method not transitively referenced by user code or
the [Preserve] attribute. Whole categories of Unity engine
"convenience" APIs are typical casualties — `FindObjectsOfTypeAll`,
`FindObjects*Tag*`, `get_sceneCount` / `GetSceneAt`, `Camera.get_allCameras`,
etc. The runtime can still walk the surviving overloads' MethodInfo
table, so `findMethodInstances` returns `count: 0` for the stripped
ones (instead of an error) — they're literally not in the metadata
anymore.

**Verified live on SaGa F2R 2026-05-08 (Unity 2021.3.39f1, IL2CPP, v29
metadata)**:

```
✅ available  Object.get_name()                              compiled
✅ available  Object.FindObjectsOfType(Type, bool)           compiled
✅ available  Object.FindObjectOfType(Type) / (Type, bool)   compiled
✅ available  GameObject.Find(string)                        compiled
✅ available  SceneManager.GetActiveScene()                  compiled
✅ available  Scene.GetRootGameObjects()                     compiled
✅ available  Camera.get_main() / get_allCamerasCount()      compiled

❌ stripped   Resources.FindObjectsOfTypeAll(Type)           count=0
❌ stripped   Object.FindObjectsOfTypeAll(Type)              count=0 (only generic;
                                                              also stripped)
❌ stripped   Object.FindObjectsOfType(Type) (1-arg)         not in metadata
                                                              (2-arg survived)
❌ stripped   GameObject.FindWithTag(string)                 count=0
❌ stripped   SceneManager.get_sceneCount                    count=0
❌ stripped   SceneManager.GetSceneAt(int)                   count=0
❌ stripped   Camera.get_allCameras                          count=0
```

**Rule**: before designing a Unity-engine-API recipe in a bridge
script, probe with `findMethodInstances` first. Recipes that survive
Editor/Mono testing can fail outright on shipped IL2CPP builds. Build
fallback chains around the methods that consistently survive
(`Object.FindObjectsOfType(Type, bool)`, `GameObject.Find(string)`,
`Camera.get_main()`, `SceneManager.GetActiveScene()` →
`Scene.GetRootGameObjects()` if Scene value-type marshaling lands).

The proper long-term fix is a **DLL-side managed-heap walker** that
doesn't depend on Unity's managed APIs at all — walks libgc's heap
sections directly via `GC_get_heap_section_count` /
`GC_get_heap_section_at`. Backlog'd as a follow-up to Phase 20-MT.

**Update 2026-05-18 (Phase 20a-2 closure)** — Unity 2022+ adds another
layer: `Object.FindObjectsOfType` is **removed outright**, not just
deprecated. On a BongoCat-style Unity 2022+ build, `findMethodInstances`
returns 0 overloads for `FindObjectsOfType`. The replacement is
`Object.FindObjectsByType(Type, FindObjectsInactive, FindObjectsSortMode)` —
the 3-arg compiled overload (generic `FindObjectsByType<T>(...)`
overloads exist but have address `0x0` until JIT-instantiated). Updated
`unity_scene.lua` `getAllGameObjects` tries[] now has three entries:
2-arg OfType → 1-arg OfType → 3-arg ByType. First match wins,
production calls land on the per-version preferred path automatically.

### Enums marshal as their underlying integer type — no `enum` wire tag needed

**Surfaced**: Phase 20a-2 verification on BongoCat (Unity 2022+) had to
call `Object.FindObjectsByType(Type, FindObjectsInactive,
FindObjectsSortMode)`. Both enum params have no `enum` tag in the
callMethod wire protocol.

**Resolution**: pass `int32` directly with the underlying integer value:
`Include = 1`, `None = 0`. Worked on Mono first try, no DLL changes.

**Why it works**: CLR enum-typed parameters are ABI-identical to their
underlying integer type. The JIT/AOT prologue reads
`*(int32_t*)params[i]` for an enum param the same way it does for an
int32 param. Our Phase 19c non-uniform marshaling (value types pass
`&value`) gives `runtime_invoke` exactly that — a 4-byte int32 slot
pointer. No type-checking happens between the DLL and the JIT
trampoline.

**Caveat**: this only works for enums whose underlying type is `int32`
(the C# default). For `enum X : long` or `enum X : byte` you'd need to
pass the matching width (`int64` / `byte`). Almost all Unity public
enums use the default — but check `findMethodInstances` output if in
doubt; `paramTypes` shows the enum type name and the rendered signature
matches the underlying width via the CLR's normal type-rendering rules.

**Rule**: when calling a method with enum params, pass `int32` and look
up the enum's integer values from Unity docs or the metadata-cli
(`getEnumValues` RPC also exists for live enumeration). Don't wait for
a DLL-side `enum` wire type — there's nothing it could do that
`int32` doesn't already.

### `signatureMatch` substring substring is whitespace-and-comma-aware — close-paren matters

**Symptom**: a `callMethod` call with a precise-looking
`signatureMatch="(System.Type)"` returns `method_not_found` even though
`findMethodInstances` confirms a method named that exists with
signature `"(System.Type, System.Boolean)"`.

**Root cause**: the substring matcher checks if `signatureMatch` is a
substring of `paramTypes`-derived signature string — including
punctuation. `"(System.Type)"` (close-paren after "Type") is NOT a
substring of `"(System.Type, System.Boolean)"` (comma after "Type") —
because in the haystack the character after "Type" is a comma + space,
not a close paren.

**Rule**: when the target method has multiple overloads, copy the
exact `signature` field from `findMethodInstances` output rather than
hand-typing it. If you need to disambiguate to a 2+-arg overload, the
full string is the only way:

```lua
sig = "(System.Type, System.Boolean)"   -- not "(System.Type)"
```

Mirror trap to the existing "`Int32` matches `UInt32`" gotcha — both
stem from the same substring semantics. The existing
parens-bounding-for-uniqueness trick (`(System.Int32)` vs `Int32`)
still helps but doesn't paper over multi-arg overloads.

### `signatureMatch` substring trap — `"Int32"` matches `"UInt32"`

**Symptom**: a method with both `Mathf.Abs(System.Int32)` and
`Mathf.Abs(System.UInt32)` overloads dispatched with
`signatureMatch="Int32"` resolves ambiguously, often picking the wrong
one. Same trap with `"Int16"` ⊂ `"UInt16"`, `"Int64"` ⊂ `"UInt64"`,
`"Char"` ⊂ `"System.Char"` ⊂ `"Char[]"`, etc.

**Rule**: default to **parens-bounded** signature strings —
`signatureMatch="(System.Int32)"`. The opening parenthesis disambiguates
from any preceding character (`U`, etc.); the closing parenthesis or
comma after the type rules out longer types like `Int32[]` or
`Int32, ...`.

The smoke scripts (`tools/Test-CallMethod.ps1`,
`tools/Test-FindMethodInstances.ps1`), the `unity_invoke.lua` API
header, and the `MethodOverloadsWindow.OpenCallMethodDialogAsync` all
use the parens form for this reason. The DLL doesn't enforce it (the
substring match is intentional so users can supply partial sigs like
`"Vector3"` to find any overload taking one), so the burden lives at
the caller.

### Multi-overload AA-script `[ENABLE]` blocks should be idempotent

**Symptom**: re-executing an AA script while the previous symbols are
still registered fails noisily.

**Rule**: in the `[ENABLE]` block, register each overload as its own
distinct symbol (`Class_Method_2uint`, `Class_Method_ItemBundle`),
**not** a shared `Class_Method` that gets re-registered per overload.
Mirror the per-symbol `unregistersymbol` calls in the `[DISABLE]`
block. The Phase 18c [AobAaScriptGenerator](../ui/UnityDumpUI.Core/Services/Export/AobAaScriptGenerator.cs)
emits unique `MakeSymbolName` collision-resolved names automatically.

---

## GC-root lifetime for `callMethod` returns

A managed pointer returned by `callMethod` is valid **only for the
next call you make right now**. A GC that ran between the address read
and the next invoke can have moved the object — Mono runs a moving
GC, IL2CPP is largely non-moving but not guaranteed.

**Best practice**: chain `findInstances` → `callMethod` (or
`callMethod` → `callMethod` on the returned address) inside one Lua
block, with no intervening user input. For the UI path (Inspector
cross-tab Phase 19d-2), the pre-fill happens at modal-open time and
the user clicks Call within the same dispatcher tick — no exposure.

---

## Avalonia 12 Windows-only build

### All three of `Avalonia.Win32` + `Avalonia.Skia` + `Avalonia.HarfBuzz` must be wired

When dropping `Avalonia.Desktop` (the metapackage that wires every
backend behind `UsePlatformDetect`), each Windows-side dependency must
be added explicitly:

- `Avalonia.Win32` package + `.UseWin32()` — without this, no window.
- `Avalonia.Skia` package + `.UseSkia()` — without this, **silent
  black window**, no error, no log.
- `Avalonia.HarfBuzz` package + `.UseHarfBuzz()` — without this,
  `InvalidOperationException: No text shaping system configured` from
  `AppBuilder.Setup()` BEFORE any logging starts → process exits with
  `0xC0000409`, no log file, no window. Looks identical to "EXE
  doesn't run at all".

`Program.cs` reads
`AppBuilder.Configure<App>().UseWin32().UseSkia().UseHarfBuzz().WithInterFont()`.

Documented in the [Avalonia 12 Win-only memory entry](../C:\Users\%USERNAME%\.claude\projects\D--Github-discrete\memory\feedback_avalonia12_winonly.md);
the symptom-to-cause map is the load-bearing part for diagnosis.

### AOT analyzers are strict — no reflection JSON

Any new JSON serialization path must go through the source-gen
`ProtocolJsonContext`. Reflection-based
`JsonSerializer.SerializeToElement<T>(value)` trips IL2026 + IL3050
under all four trim/AOT analyzers.

### Avalonia 12 DataGrid sort is a three-layer trap under AOT — fix needs all three

Diagnosed live 2026-05-19 via logging the DataGrid's column state +
Sorting event firing. The earlier "strip `x:DataType` and you're
done" version of this entry was wrong; the real picture has three
distinct gates that all have to be open:

```
┌─────────────────────────────────────────────────────────────────────┐
│ Layer 1 — column binding compiles to CompiledBinding                │
│           (Window-level x:DataType inherits scope down to the      │
│            DataGrid, so column Bindings compile to a typed         │
│            delegate; CompiledBinding has no readable Path string   │
│            at runtime)                                              │
│                                                                     │
│ Layer 2 — empty Path → column.CanUserSort auto-disabled to False    │
│           (CanUserSortColumns=True on the grid does NOT override    │
│            per-column auto-disable; header click is a silent       │
│            no-op — the Sorting event never even fires)              │
│                                                                     │
│ Layer 3 — even with CanUserSort restored, the default sort uses     │
│           reflection on SortMemberPath to read row property values  │
│           — AOT trim drops that reflection, comparer reads          │
│           nothing, "sorted" data appears in the same order while    │
│           the column header glyph (▲/▼) animates correctly          │
└─────────────────────────────────────────────────────────────────────┘
```

**Symptoms by layer reached:**

| Sort state | Layer | Cause |
|---|---|---|
| Header click does nothing, no glyph change, Sorting event never fires | 1+2 | Default config — column auto-disabled |
| Glyph flips ▲↔▼ but rows don't reorder | 1+3 | CanUserSort restored, but default reflection-sort silently fails |
| Rows actually reorder | all three open | Working config |

**Fix that lands all three for every grid in the project:**

```xml
<!-- XAML — open layer 2 -->
<DataGridTextColumn Binding="{Binding Foo}"
                    SortMemberPath="Foo"        ← gives sort a real string path,
                                                  defeats CompiledBinding-empty-Path
                                                  auto-disable
                    CanUserSort="True"          ← belt-and-braces override
                    ... />
```

```csharp
// Code-behind — open layer 3
Loaded += (_, _) =>
    this.FindControl<DataGrid>("GridName")?.WireSortComparers(_sortComparers);

private static readonly IReadOnlyDictionary<string, IComparer> _sortComparers =
    new Dictionary<string, IComparer>
    {
        ["Foo"] = DataGridSortComparers.Ordinal<MyRow>(r => r.Foo),
        // Hex / Number / Bool also available — see DataGridSortComparers.cs
    };
```

`CustomSortComparer` is Avalonia's documented escape hatch — when
set, the DataGrid uses our typed delegate-based comparer and skips
the reflection path. The shared
[`DataGridSortComparers`](../ui/UnityDumpUI.Core/Services/DataGridSortComparers.cs)
provides factories for the common shapes (Hex address columns get
numeric compare so `0x100000000` doesn't sort before
`0x80000000`); the
[`WireSortComparers`](../ui/UnityDumpUI/Views/DataGridSortExtensions.cs)
extension keys lookup by `SortMemberPath` so the XAML and the
code-behind stay in sync via a single column-name string per
binding.

**Why `D:\Github\CrimsonAtomtic` doesn't need this:** its column
Bindings compile to plain `Binding` (not `CompiledBinding`) because
its XAML scope shape doesn't put a Window-level `x:DataType` in
inheritance reach of the DataGrid columns. Plain `Binding` exposes
the path string, so layer 1 never triggers and layers 2/3 never
matter. Our windows do put `x:DataType` on the root `<Window>`, so
we hit all three layers — the explicit `SortMemberPath` +
`CanUserSort=True` + per-column `CustomSortComparer` route is the
strict-superset fix that works in both scope shapes.

**Old false leads that didn't fix it (kept here so they aren't
re-tried):**

- Stripping `x:DataType=vm:RowType` from inside the column Binding
  (`Binding="{Binding Foo, x:DataType=vm:RowType}"`). Doesn't change
  the binding kind to plain `Binding` — Window-level x:DataType
  still inherits the typed scope.
- Stripping `x:DataType="vm:SomeVM"` from the DataGrid element
  itself. Same reason — Window scope still applies.
- Adding `CustomSortComparer` alone, without `SortMemberPath` +
  `CanUserSort=True`. The comparer is wired, but the column is
  still auto-disabled at layer 2 so click never reaches it.

**Diagnostic recipe** if a new DataGrid's sort doesn't work: hook
`DataGrid.Sorting` to a logger, print each column's
`SortMemberPath`, `CanUserSort`, `Binding` runtime type. If
`canUserSort=False` you're stuck at layer 2 — add
`SortMemberPath="<PropertyName>" CanUserSort="True"` in XAML. If
`Sorting event fired` lines appear but rows don't reorder, you're
at layer 3 — wire `CustomSortComparer` per column. Build is always
green in both cases; only interactive header click reveals which
layer is closed.

- **In-Core code** can reach `ProtocolJsonContext` directly. Pattern:
  `Str(string)` / `Int(int)` helpers in
  [BackendAdapter.cs](../ui/UnityDumpUI.Core/Services/BackendAdapter.cs).
- **UI ViewModels** can't reach the internal context. Go through
  [CallArgFactory.cs](../ui/UnityDumpUI.Core/Services/CallArgFactory.cs)
  (Phase 19d pattern) — public seam over the internal source-gen
  context.

### `LibraryImport` does NOT auto-resolve A/W variants

Unlike classic `DllImport` with `CharSet`, every entry-point name in
`[LibraryImport(...)]` is taken literally. Use explicit
`EntryPoint = "FooW"` (or `"FooA"`) for any function that ships A/W
variants — no `EntryPoint` means the linker looks for the bare name,
which may not exist.

### Avalonia 12 clipboard API churn

The legacy `IDataObject` / `DataFormats` / `SetDataObjectAsync`
surface is gone. Only supported writer is
`IClipboard.SetDataAsync(IAsyncDataTransfer)` — wrap a `DataFormat.Text`
item in a `DataTransferItem`, add to a `DataTransfer`, hand to
`Clipboard.SetDataAsync`. Pattern in
[MainWindow.axaml.cs:108](../ui/UnityDumpUI/Views/MainWindow.axaml.cs).

### `avares://` keys off `AssemblyName`, not `RootNamespace`

If the .csproj `<AssemblyName>` is renamed (we did this from
`UnityDumpUI` → `DiscreteUI` for branding), every `avares://` URI in
XAML must update too: `App.axaml`'s `ResourceInclude Source=` and any
`Window.Icon` references. Forget any one of them and the build
succeeds but resources fail to load at runtime with cryptic null refs.

### Avalonia 12 `TextBlock.Text` and `Inlines` are mutually-exclusive sources

**Symptom**: setting both `Text` and `Inlines` on the same `TextBlock`
produces undefined rendering — sometimes the plain text appears,
sometimes the inline runs appear, sometimes the text gets duplicated
(plain string + concatenated inline runs). Not deterministic across
property-set order or Avalonia minor versions.

**Rule**: pick exactly one source per render. To toggle between plain
text and inline runs (e.g. for the filter-substring highlight UX in
`HighlightedTextBlock`), the pattern is **clear the other source first**:

```csharp
if (noHighlight) {
    Inlines = null;           // clear before setting Text
    Text = source;
} else {
    Text = null;              // clear before setting Inlines
    Inlines = BuildRuns(source, filter);
}
```

Also — don't bind `Text` from XAML when the control internally manages
`Inlines`. Add a `SourceText` styled property that the consumer binds
instead, and let the control decide which of `Text` / `Inlines` to set.

Pattern in
[ui/UnityDumpUI/Controls/HighlightedTextBlock.cs](../ui/UnityDumpUI/Controls/HighlightedTextBlock.cs)
+ pure splitting in
[ui/UnityDumpUI.Core/Services/HighlightSegmenter.cs](../ui/UnityDumpUI.Core/Services/HighlightSegmenter.cs).

### `DataGridCheckBoxColumn` requires double-click to enter edit mode

Single-click on a `DataGridCheckBoxColumn`'s checkbox does nothing
until you double-click the cell to enter edit mode. For UX-critical
single-click toggles (e.g. multi-select grids), use a
`DataGridTemplateColumn` with an inline
`<CheckBox Focusable="False" />` instead. Pattern in
[MethodOverloadsWindow.axaml:103-112](../ui/UnityDumpUI/Views/MethodOverloadsWindow.axaml).

### `DataGridTextColumn` cannot host custom controls — use `DataGridTemplateColumn` + `CellTemplate`

Need a custom-painted cell inside a `DataGrid` (e.g. the filter
substring highlight rendered by `HighlightedTextBlock`)? `DataGridTextColumn`
can't do it — it only renders plain text from its `Binding`. The
replacement is a `DataGridTemplateColumn` with a `CellTemplate` that
holds whatever control tree you want:

```xml
<DataGridTemplateColumn Header="Name" Width="*" SortMemberPath="Name">
  <DataGridTemplateColumn.CellTemplate>
    <DataTemplate x:DataType="vm:FieldRow">
      <controls:HighlightedTextBlock SourceText="{Binding Name}"
                                     Highlight="{Binding #FieldFilterBox.Text}"
                                     VerticalAlignment="Center"
                                     TextTrimming="CharacterEllipsis"
                                     Margin="4,0" />
    </DataTemplate>
  </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

Two gotchas worth remembering:
1. **Preserve `SortMemberPath`** explicitly — without it, `DataGridTemplateColumn` sorts by the bound DataTemplate's first text node (often empty / wrong). `SortMemberPath="Name"` keeps the click-the-header sort working.
2. **Element-name binding works inside the cell template** — `{Binding #FilterBox.Text}` reaches a named control elsewhere in the same XAML file, even though the cell's `DataContext` is the row item.

Both points exercised by the Browser filter-highlight wiring in
[MainWindow.axaml](../ui/UnityDumpUI/Views/MainWindow.axaml) (Fields
Name + Type columns, Methods Name column).

### `CommunityToolkit.Mvvm` quirks

- **Property name collision with field name**: a property named
  `Backend` collides with the source-generated field for `_backend2`.
  Workaround: name the property `Backend2` (literally with the suffix).
- **Child-collection CanExecute is NOT auto-re-evaluated**: when an
  item inside an `ObservableCollection<T>` raises `PropertyChanged`,
  any `[RelayCommand(CanExecute = ...)]` on the parent VM whose
  predicate reads child state will NOT re-evaluate. Subscribe to each
  row's `PropertyChanged` and call `Notify*Changed()` manually.
  Pattern: `MethodOverloadsViewModel.OnRowPropertyChanged` in
  [MethodOverloadsViewModel.cs:188](../ui/UnityDumpUI/ViewModels/MethodOverloadsViewModel.cs).

---

## Native AOT + integrity-stamping (Phase 26)

### `InternalsVisibleTo` matches AssemblyName, not project name

Phase 26 puts a third token guardian (`WindowsRuntimeMetadata`) in the
UI assembly so its sentinel + 32-byte hash slot lands in a different
`.rdata` region than the two slots in Core.dll. The UI class reads
`SchemaConstants` from Core, which is marked `internal`. So Core needs
`<InternalsVisibleTo Include="..." />` naming the UI assembly.

The UI's csproj has:

```xml
<RootNamespace>UnityDumpUI</RootNamespace>
<AssemblyName>DiscreteUI</AssemblyName>
```

— the assembly **name** is `DiscreteUI` (since the repo is called
"discrete"); the **project** + namespace is `UnityDumpUI` (kept for
backward compatibility with the existing source tree).

`InternalsVisibleTo` is matched against the consuming **assembly
identity**, not the project name. Wrong:

```xml
<InternalsVisibleTo Include="UnityDumpUI" />  <!-- silently no-op -->
```

Right:

```xml
<InternalsVisibleTo Include="DiscreteUI" />
```

The "wrong" version compiles silently and triggers
`CS0122 'SchemaConstants' is inaccessible due to its protection
level` at the call site. Easy to misdiagnose as a build-cache issue
the first time you hit it (we wasted a `rm -rf obj bin` cycle on
this).

### AOT preserves static byte-array literals in `.rdata`; single-file deflate-compresses them

Phase 26's hash slot is a `private static readonly byte[]` literal
with a 16-byte sentinel + 32 0xCC placeholder bytes. The publish
step (`tools/inject-hash.ps1`) scans the binary for sentinel +
0xCC tail and rewrites the trailing 32 bytes with the real SHA256.

This works for AOT publish (`-p:PublishAot=true`):

* ILC compiles each `byte[] = [...]` literal into a contiguous
  block in the `.rdata` section of the native binary.
* The byte sequence is searchable verbatim — sentinel + 0xCC tail
  shows up as a 48-byte run.
* AOT-verified empirically: 3 sentinels found at distinct offsets
  in the published exe (each followed by 32×0xCC), inject-hash
  patches successfully, runtime guardian re-hashes and compares
  cleanly.

It does **NOT** work for `-p:PublishSingleFile=true` (Debug/Release):

* The bundled `UnityDumpUI.Core.dll` lives inside `DiscreteUI.exe`
  as a deflate-compressed blob.
* The 32-byte 0xCC run is extremely compressible — DEFLATE
  collapses it to a few bytes in the bundle.
* `inject-hash.ps1` scanning the .exe finds the sentinel bytes
  but the tail isn't 32×0xCC anymore (we verified this on the
  shipped Release `dist/DiscreteUI.exe`).

**Rule**: integrity-stamping must run only on AOT publishes.
`build.ps1`'s `Build-Ui` already gates the `inject-hash.ps1` call
on `$Mode -eq 'Publish'`; Debug/Release builds keep the 0xCC
placeholder, which `IsDevMode` short-circuits to factor=0 so all
poison is disabled.

### `_pad` arrays need an observable read so the trimmer doesn't drop them

Every guardian carries a `byte[] _pad` (96–192 bytes of random-
looking filler) to take up space in `.rdata` around the sentinel
slot — visual obfuscation against "diff binary against a clean
copy and look for the 48-byte block that changed".

If `_pad` is never read, AOT trim drops it. The "anti-trim" idiom
is a single bogus read inside the dev-mode early return:

```csharp
if (SchemaConstants.IsDevMode(HashSlot)) {
    _ = _pad[0];  // anti-trim
    return 0;
}
```

`SchemaConstants._gap` uses the same trick inside `IsDevMode`. The
read has no semantic effect but it makes the array observably
live, so the linker preserves the literal bytes.

Mirror of AOBMaker's pattern (their `IntegrityGuardian.cs` line 70
does the identical `_ = _pad[0]`).

---

## PowerShell smoke testing

### `ConvertTo-Json` flattens single-element arrays

**Symptom**: a `Test-*.ps1` script builds a JSON request with
`@{ args = @($oneArg) }`. The resulting wire payload has `"args":
{...}` (a bare object) instead of `"args": [{...}]` (a single-element
array). The DLL rejects with "args must be an array".

**Rule**: PowerShell unwraps single-element arrays implicitly through
the pipeline and `ConvertTo-Json` follows. For nested arrays where
the outer container is a property value, **splice the array JSON in
as raw text**:

```powershell
$argsJson = '[' + (ConvertTo-Json $oneArg -Compress) + ']'
$body     = "{`"args`": $argsJson}"
```

See [tools/Test-CallMethod.ps1](../tools/Test-CallMethod.ps1) for
the pattern in a real script.

### CJK console + em-dashes need `Start-Transcript`

A CJK Windows console (Traditional / Simplified Chinese, Japanese,
Korean) munges em-dashes (`—`) and other Unicode punctuation printed
by `build.cmd`'s status banners. Routing through `Start-Transcript`
fixes the encoding; `build.cmd` already does this so the user sees
clean output regardless of console code page.

---

## Build / DLL development

### CMake `-DBACKEND=` cache poisoning

CMake caches the `BACKEND` variable. Switching `Mono` → `IL2CPP` →
`Both` in the same build directory produces inconsistent linker
errors that look like "missing source files". Solution:
[build.ps1](../build.ps1) wipes the per-backend build dir on every
configure pass. Don't re-introduce a "smart re-use" optimization
without re-confirming it survives a backend switch.

### Game must be closed before rebuilding the DLL

`UnityDumper-Mono.dll` / `UnityDumper-IL2CPP.dll` stay mapped in the
target process after injection. Trying to rebuild while the game is
running fails with `LNK1104: cannot open file ...`. Close the game,
then rebuild.

### `Inject-Dll.ps1` reads from `build/dll-both-release/bin/`, NOT `dist/`

**Symptom**: a freshly-committed DLL fix appears to fail every live-
verification run. The wire response shows the pre-fix behaviour even
though the source code clearly contains the new branch.

**Root cause**: `Inject-Dll.ps1` (called by `Test-Handshake.ps1` and
DiscreteUI's "Pick & Inject" alike) reads the DLL from
`build\dll-both-release\bin\UnityDumper-IL2CPP.dll` (or `…-Mono.dll`).
`dist/` is the *shippable* tree but is **not** the inject source —
`dist/` only gets repopulated when `build.ps1` runs to completion.
Locally-edited code that you forgot to rebuild stays out of `build/`,
out of `dist/`, and out of every injected process. **Worse**, partial
or aborted builds can leave `dist/` slightly newer than
`build/dll-both-release/bin/` (e.g. the IL2CPP variant got copied but
Mono got stuck on `LNK1104`), so a "is dist/ recent?" eyeball check
gives a false-positive.

**Rule**: before any live-verification round after a commit,
unconditionally rebuild — even if `dist/` *looks* recent:

```powershell
.\build.ps1 -Target DLL -Backend Both   # ~20 s warm
Get-ChildItem build\dll-both-release\bin\UnityDumper-*.dll | Select Name,LastWriteTime
```

The build_number bump in `build_number.txt` is the cheapest signal —
if the UI's status bar reads a build # lower than `cat build_number.txt`,
**the running DLL is out of date no matter what `dist/` says**. Caught
2026-05-08 burning ~15 minutes on a 4-target verification run where
all four "failures" turned out to be stale DLLs from before the fix
commits.

### AOB decoder bailout is by design

Phase 18a-3's hand-rolled x64 length disassembler in
[AobPattern.cpp](../dll/src/shared/AobPattern.cpp) bails (falls
through with literal bytes, no wildcards) on any opcode it doesn't
fully model. The bailout is reported in the wire response as
`decoderBailed: true, bailedAt: N`. **Do not try to "fix" this by
over-wildcarding** — false positives cause symbol ambiguity, which is
the failure mode we cannot accept. A literal-bytes pattern is less
version-stable but always points at the right address.

---

## Pipe client / connection

### `PipeClient` write+read is intentionally uncancellable past commit

[PipeClient.cs](../ui/UnityDumpUI.Core/Services/PipeClient.cs)
deliberately stops honouring the caller's `CancellationToken` once
the request is on the wire. Don't "fix" this back to passing `ct`
everywhere — the race it solves (id-mismatch when caller cancels
mid-request and the next caller reads back the orphaned response) is
not theoretical. Rapid Find Instances clicks reproduce it within 10
seconds without the guard.

---

## Antivirus

Freshly-built `DiscreteUI.exe` is flagged by some AV vendors due to
the combination of many `LibraryImport` native calls + remote-thread
injection P/Invokes. Dev workflow: exclude the `dist\` folder. Mention
this if a fresh dev hits a quarantine error after `build.cmd publish`.

---

## Cheat Engine Lua API

### Verify CE Lua functions exist before using them

When working with Cheat Engine Lua APIs, always verify that a function
exists in the CE Lua documentation before using it. Don't assume
methods like `GotoAddressEx` exist — CE has churned its Lua surface
across 7.x versions and many "obvious" function names don't.

### Bridge scripts ship via glob, not a hand-coded list

[tools/Install-CEScripts.ps1](../tools/Install-CEScripts.ps1) used to
hand-list each `*.lua` to copy. New scripts kept getting forgotten
(`unity_addrfind.lua` and `unity_invoke.lua` both shipped one phase
late). Now uses `Get-ChildItem -Filter '*.lua'` glob; future scripts
ship automatically.

### `findInstances` heap scan picks up thread-stack false positives — validate before invoking

**Symptom**: `findInstances UnityEngine.GameObject` returns a list whose
first 1–2 entries have visibly suspect addresses (high-bit ranges like
`0x67_ACEF_EED0`, `0xB5_F7F5_EEA0`, `0xF5_AC39_F290`). Calling
`Object.get_name` (or any other icall) on them returns
`error: native_crash_in_runtime_invoke` from the SEH leaf. Same scene,
different process launch, same heap-scan can return 0/N or 30/32 valid
instances depending on luck.

**Root cause**: `findInstances` heuristic is "find any aligned qword in
the process's RW memory regions whose first 8 bytes equal the target
class's `Il2CppClass*`". It doesn't restrict the search to the IL2CPP
GC heap range — it sweeps thread stacks too. Stack frames frequently
hold local variables of type `GameObject*`, and even after the function
returns the bytes can sit in stack memory until overwritten. Each such
stale local satisfies the heuristic but isn't actually a live managed
object — calling runtime_invoke on it crashes inside the JIT/AOT entry
because the rest of the "object" is uninitialised stack bytes.

The DLL's SEH leaf catches the crash and returns
`{"error":"native_crash_in_runtime_invoke","ok":false}` cleanly so the
host doesn't die — that part of the design is correct. But every false
positive still costs 50–200 ms (the SEH leaf is expensive on the
trapping path) and contributes to main-thread pressure (lessons-learned
entry above). Both add up fast on large limits.

**Two false-positive categories** the heuristic surfaces:

1. **Thread-stack false positives** — local variables of type
   `GameObject*` left on stack frames after function return. Address
   ranges look like `0x67_xxxx`, `0xB5_xxxx`, `0xF5_xxxx` (high-bit
   thread-stack regions). First qword matches by coincidence;
   `runtime_invoke` on them crashes inside the JIT/AOT entry.
2. **IL2CPP metadata structures** — RGCTX entries, `Il2CppType`
   records, vtable pieces. These STORE class pointers at offset 0 by
   design (legitimate metadata bookkeeping). Address ranges look
   normal (heap-y), `runtime_invoke` returns `ok:true,returnValue:null`
   or similar non-error empty-ish results because the icall reads
   garbage at later offsets. Identifying signature: addresses come in
   pairs separated by exactly 0x18 bytes (paired metadata records).

**Rule**: any consumer of `findInstances` that's about to call
`runtime_invoke` on the matches needs a **two-layer** validator. Layer
1 is necessary but insufficient on its own:

```lua
local function _isValidInstance(addr, classPtr)
    if classPtr == 0 then return true end
    if readPointer == nil then return true end

    -- Layer 1: klass at offset 0 must equal classPtr.
    -- Kills stack false positives.
    local ok1, typePtr = pcall(readPointer, addr)
    if not ok1 or typePtr ~= classPtr then return false end

    -- Layer 2: m_CachedPtr at offset 0x10 must be a non-null, plausibly
    -- user-space pointer. UnityEngine.Object's layout is
    -- {klass@0, monitor@8, m_CachedPtr@0x10, ...}; live GameObjects
    -- always have the native pointer set. Metadata structures' +0x10
    -- slot is some unrelated field — usually 0, a small enum, or a
    -- stale value — and fails this check.
    local ok2, cachedPtr = pcall(readPointer, addr + 0x10)
    if not ok2 or cachedPtr == nil then return false end
    if cachedPtr < 0x10000 or cachedPtr > 0x7FFFFFFFFFFF then return false end
    return true
end
```

Both layers are local memory reads (zero pipe traffic, zero callMethod).
Layer 1 alone left 62/62 metadata false positives surviving in SaGa
F2R's equipment screen on 2026-05-08; Layer 2 dropped them. This is
specific to `UnityEngine.Object`-derived classes (which always have
m_CachedPtr at +0x10). For other class hierarchies the same idea
applies but with a class-specific "definitely-non-null managed field"
to validate.

Surfaced in two passes during Phase 20a's live tests on SaGa F2R:
- Pass 1 (PID 38144): `getAllGameObjects({limit=64, decodeNames=true})`
  returned 0/64 named because the first match was stack-y and
  runtime_invoke trapped → wrote Layer 1.
- Pass 2 (PID 41748, equipment screen): with Layer 1, 62/64 survived
  the filter but ALL 62 still get_name'd to nil — these were paired
  IL2CPP metadata structures (24-byte stride). Wrote Layer 2.

The DLL-side proper fix (restrict `findInstances` to the IL2CPP
managed heap range only — queryable via libgc / `il2cpp_gc_get_heap_section_count`)
is backlog'd.

**Resolved 2026-05-18 (Phase 23 / build #301)** — `ScanForPointer` now
filters out `MEM_IMAGE` regions (all DLL `.data` / `.bss` sections,
including IL2CPP RGCTX cache and Mono class-table memory) and every
live thread's stack range (collected via `NtQueryInformationThread` +
TEB walk). The "managed heap range" framing turned out to be a misdiagnosis
of the API surface: `il2cpp_gc_get_heap_section_count` doesn't exist
in any Unity version we surveyed (2017.1 → 6000.0); the closest public
API is `il2cpp_gc_foreach_heap` (2019.3+, IL2CPP only). The negative
filter (exclude module + stack) is runtime-agnostic, works on Mono +
all IL2CPP versions, and eliminates both documented false-positive
sources (RGCTX entries are in `MEM_IMAGE`, stack residue is in stack
ranges). Live smoke on BongoCat (Mono): 1194 regions → 571 scanned;
`UnityEngine.Mathf` (static class, expected 0 instances) found 3
matches (down from order-of-magnitude higher pre-fix counts; remaining
3 are reflection records, not stack/module residue). Per-call telemetry
exposed via `scan_stats` in the wire response + `scan` log category
so future regressions surface immediately. Deprecated Lua-side
`_isValidInstance` removed from `scripts/unity_scene.lua`.

**Even the two-layer Lua-side filter is structurally insufficient for
hot Unity classes.** Pass 3 of the SaGa F2R live test (PID 41748,
equipment screen, 2026-05-08) confirmed: 622 of 1024 candidates
survived both layers, but ALL 622 returned `native_crash_in_runtime_invoke`
on get_name. Forensic on 4 sample addresses showed every single
survivor had this metadata-structure layout:

```
+0x00: <classPtr>      ← passes Layer 1
+0x08: <flags / 0x12>
+0x10: 0x41BC0600      ← always identical across samples; passes Layer 2
                          loosely (>= 0x10000) but is clearly garbage,
                          not a real native pointer
+0x18: <classPtr>      ← classPtr stored AGAIN — definitive metadata
                          fingerprint
```

This is an IL2CPP RGCTX cache / generic class instantiation record —
legitimately stores the class pointer twice (once for resolution lookup,
once for the cache key). Real `Il2CppObject<UnityEngine.Object>`
instances never have classPtr at +0x18 (that slot would be a
managed-field offset).

**`whatIsAt` (Phase 17) shares the same heuristic** — it identifies the
4 metadata records as `class: "GameObject"` because it also just reads
the first qword. This isn't a Phase 17 bug per se (the heuristic is
correct for the typical "user has an address from a CE pointer scan"
use case), but it means our reverse lookup won't help us distinguish
real instances from metadata records either.

**Action**: for `UnityEngine.*`-derived classes, prefer the engine's
own enumeration APIs over `findInstances` — but verify they survived
IL2CPP code stripping first (see "IL2CPP code stripping..." below):
- `Object.FindObjectsOfTypeAll(Type)` — returns ALL objects of a type
  including hidden/inactive/HideAndDontSave. Authoritative,
  zero-false-positive. Now the implementation backing
  `unity_scene.lua` `getAllGameObjects`.
- `Resources.FindObjectsOfTypeAll(Type)` — older API, similar effect
  in older Unity versions.
- `SceneManager.GetActiveScene().GetRootGameObjects()` — narrower
  (only the active scene's roots) but bypasses the whole
  Type-marshaling step. Needs `Scene` value-type marshaling (Phase 19
  v2 candidate).

Use `findInstances` for **user-defined classes** where Unity has no
equivalent enumeration API — those classes don't have the dense IL2CPP
metadata cross-references that `UnityEngine.Object` accumulates, so
the heap scan is much cleaner. Phase 17 / 18 / 19's verified test
matrix (which all use user classes like `MainGame.PLY_WORK`) confirms
the heuristic is fine in those cases.

### `callMethod` on Unity icalls is main-thread-sensitive — sustained bursts freeze the game

**Symptom**: a Lua loop that calls `callMethod` against a Unity scene-graph
property (like `Object.get_name`, `Transform.get_parent`,
`Transform.GetChild`, `Component.gameObject`) thirty-two times: works.
The same loop continued for ~1024 iterations: the game becomes
unresponsive within seconds and has to be killed from Task Manager.

**Root cause**: `callMethod` invokes managed methods on the **DLL's pipe
worker thread**, NOT Unity's main thread. Phase 19's spec called this
out as a v1 limitation. Unity's internal locks / engine state assume
main-thread-only access for scene-graph icalls. Bursts up to a few
dozen calls slip past unscathed because the windows between game-thread
critical sections are short and we win the race; sustained streams of
hundreds-to-thousands of icalls have monotonically increasing odds of
hitting a critical section, and once one stalls the game thread's
cooperative scheduling falls apart and the whole process freezes.

This is NOT a deadlock in the strict CS sense (no cycle of mutex
ownership) — it's lock contention + missed game-loop deadlines until
the OS thinks the window is hung. `pcall` doesn't help; SEH leaves on
the DLL side don't help. The only real fix is main-thread marshaling.

**Rule** (until the proper fix lands):

- Stateless / pure managed methods (Mathf.*, Convert.*, String.*,
  Math.*, etc.) are safe at any call rate. Phase 19's verified test
  matrix is all of these — that's why we never noticed.
- Unity engine icalls (anything on `UnityEngine.Object` /
  `GameObject` / `Transform` / `Component` / `Camera` / scene-graph
  classes) must be **rate-limited and small-N**. Empirical safe band:
  ≤ 256 calls in one stretch, with a 1 ms `sleep()` between RPCs.
  Past that, the game starts to chug; past ~1024 it usually freezes.
- For batch operations on N>256 GameObjects, **don't iterate
  callMethods** — read managed fields via `walkInstance` / `walkArray`
  instead, where the heap is read-only memory access (no icall).
- The proper fix is a future phase: hook the DLL into Unity's main
  thread (e.g. patch `PlayerLoop` or `Update` to drain a queue of
  pending invocations). Until that lands, scripts/UI must enforce
  the call-rate budget themselves.

Surfaced 2026-05-08 during Phase 20a's first live test on SaGa F2R:
`getActiveSceneRoots({limit=1024})` issued ~2048 sustained icalls and
locked up the game. Fixes in `unity_scene.lua`: per-RPC `sleep(1)`
throttle, default limits dropped (256 for icall-bearing functions,
128 for tree-walk experimentals), explicit caveat block at file head.

### Wire envelope is `{id, result}` — bridge scripts must unwrap

**Symptom**: a freshly-written bridge script's high-level API returns
empty arrays / nil values for everything, even though a hand-paste
PowerShell smoke or raw `rpc()` against the same wire command shows
the correct `count` / `matches` / `returnValue` payload.

**Root cause**: the DLL pipe server wraps every reply in
`{"id": N, "result": { ... }}` (or `{"id": N, "error": {code, message}}`
on failure). A bridge script's local `rpc()` helper that does
`return parseJson(resp)` returns the **outer envelope**, not the
result payload. Top-level field reads like `r.matches` / `r.elements` /
`r.returnValue` then all see nil because the real fields live one
level down at `r.result.matches` etc.

**Rule**: every bridge-script `rpc()` must unwrap. Canonical pattern
copied from [unity_invoke.lua](../scripts/unity_invoke.lua) (lines
around 357–362):

```lua
local r = parseJson(resp)
if r.error then
    local err = r.error
    local msg = (type(err) == "table" and (err.message or err.code))
                or tostring(err)
    error(TAG .. " " .. cmd .. " failed: " .. tostring(msg))
end
return r.result or {}
```

The throw on error is optional — `unity_invoke.lua` returns the raw
envelope and lets `callUnityMethod` decide; `unity_scene.lua` throws
because every public function expects a non-error response. Pick one
and stick to it within a single bridge.

Surfaced 2026-05-08 during Phase 20a's first live test against SaGa
F2R: `getAllGameObjects()` returned 0 even though the same
`findInstances` call from a hand-built `rpc()` showed 4+ matches. Fix
in `unity_scene.lua`'s `rpc()`.

### CE's `mono_method_get_parameters(method).parameters[i].typename` is nil on Unity 2022+ Mono — fall back to `mono_method_getSignature`

**Symptom**: an AA script that calls CE's built-in `mono_*` Lua API to
locate an overload (e.g. our Phase 22e `discrete_mono_registerSymbolEx`
helper) substring-matches `paramTypes[i]` against `p.parameters[i].typename`.
On at least Unity 2022+ Mono (verified on BongoCat), CE returns
`p.parameters[i].typename == nil` for *every* parameter on *every*
method of at least `UnityEngine.Object` — including non-generic
compiled methods. `string.find(nil, ...)` then crashes (`bad
argument #1 to 'find'`), or with a nil guard the substring match
fails for every overload and the user sees `no matching overload`
even though the picker showed three.

**Critically, `mono_method_getSignature(m.method)` is fine on the same
target** — it returns the full rendered signature string with all
type info intact. The right-pane overload picker in DiscreteUI was
reading the same data via our DLL pipe (which doesn't go through
CE's introspection) and showed correct typenames, so the disparity
was specifically inside CE's MonoDataCollector Lua surface, not the
runtime itself.

**Fix** (in [`scripts/mono_registersymbol_helpers.lua`](../scripts/mono_registersymbol_helpers.lua)):

1. **`pcall`-shield `mono_method_get_parameters`** — some generic /
   uncompiled overloads throw inside the call; skip the row, don't
   crash the AA.
2. **Defensive nil-guard on `typename`** before any `string.find` —
   treat nil typename as "this overload doesn't match", continue
   to the next.
3. **Fall back to `mono_method_getSignature` substring match** when
   the typename path fails for any param of an otherwise-matching
   overload (right name + right param count). Success log
   distinguishes `(matched via typename)` vs `(matched via signature)`
   so the next debug session sees which path landed.
4. **Symmetric tail-match for typename comparisons** when both
   forms ARE populated — `_discrete_typename_match("UnityEngine.FindObjectsInactive",
   "FindObjectsInactive")` returns true (either side may be the
   namespaced form, the other the basename). Crucially still
   rejects `"System.Int32"` ↔ `"UInt32"`, so the long-standing
   Int32-substring-of-UInt32 trap stays caught.
5. **Diagnostic dump on no-match** prints every name-matched
   overload's `(N) <typename name>, ...  -- sig: <full signature>`
   so the next CE/Unity-version surprise surfaces the relevant data
   on the first run.

Surfaced 2026-05-19 during Phase 25 live-smoke. Same session also
caught the autorun-shadowing trap below.

### User-autorun `_G.mono_*` definitions shadow idempotent helpers — namespace under `_G.discrete_*`

**Symptom**: a freshly-generated AA script that loads our DLL-free
Mono register helper from the CT (Phase 22e `MonoRegisterSymbolAaScriptGenerator`
pattern — `findTableFile` → `chunk()` → `_G.mono_registerSymbolEx(...)`)
runs the *user's* hand-rolled autorun version of `mono_registerSymbolEx`
instead of ours. Two giveaways: error message format doesn't match
our helper's `[discrete] ...` convention (user's autorun prints
`💔 Error: ...`), and the autorun version lacks the
`mono_ensureAttached()` step our helper does, so `mono_findClass`
returns nil on the first run because `LaunchMonoDataCollector` was
never called.

**Root cause**: our helper uses `if _G.mono_registerSymbolEx == nil
then ... end` for idempotency (avoid re-defining functions across
multiple AA enable/disable cycles). When the user has a CE autorun
script that pre-registers the same name (CE forums pattern — very
common for cheat-table authors who chain together community
snippets), the guard skips our load, and the AA call invokes the
autorun's version.

**Fix**: don't share namespace with users. The helper now installs
`_G.discrete_mono_registerSymbol{,Ex,BySignatureMatch}` as a private
suite — **no idempotency guard, always overwrite** (our namespace,
our canonical version, no risk of stomping unrelated user code).
The user-facing `mono_*` names stay defined with their guards so
hand-written CT copy-paste examples still work as before. The
generator (`MonoRegisterSymbolAaScriptGenerator`) keys idempotency
off `_G.discrete_mono_registerSymbolEx == nil` (test asserts the
guard text doesn't drift back to the `mono_*` form). A `Assert.DoesNotContain`
in `MonoRegisterSymbolAaScriptGeneratorTests` catches accidental
regression.

**General lesson** for any user-facing scripting surface: assume
the user has *something* under your "obvious" function name. Either
prefix everything with your project's tag (`discrete_*`) or use a
table namespace (`Discrete = Discrete or {}; function Discrete.foo()`).
The idempotency guard pattern is fine for end-user examples (where
you want their override to win) but NOT for your own internal
function calls.

Surfaced 2026-05-19 during Phase 25 follow-on live-smoke on
BongoCat.

---

## AOBMaker CE plugin

### `InjectTableFile` is CRLF-sensitive — Stream.Size verification fires "wrote N, stream has N-K"

**Symptom**: AOBMaker plugin's `InjectTableFile` reply for one
specific lua file is
`failure — Stream size mismatch: wrote 37326, stream has 36466`,
while every other file in the same batch succeeds. The byte gap is
suspiciously close to the file's line count.

**Root cause**: CE's `createStringStream(content)` +
`Stream.copyFrom(ss, 0)` path in the plugin's
`HandleInjectTableFile` (see `D:\Github\AOBMaker\plugins\CEPlugin\src\pipe_server.cpp`
line ~1354) **silently normalises CRLF → LF** when writing into the
TableFile's underlying store. The plugin computes `expectedSize` from
the pre-normalisation byte count and verifies post-write
`Stream.Size` against it; any CRLF in the input mismatches by exactly
one byte per pair.

In Phase 22 live smoke (2026-05-12) this surfaced on
`scripts/unity_scene.lua` — 860 CRLF pairs in the working copy of
that one file (the other 6 bridge files were LF only), 860-byte
deficit, plugin verification fired and the entire bridge-inject
pipeline aborted at step 7/8. **Production DiscreteUI's "Inject
Discrete Bridge" would have hit the same failure on first user run** —
a silent P1 that only the live smoke caught.

**Rule**: every text file embedded as `<EmbeddedResource>` in a
project that ships through AOBMaker `InjectTableFile` must be LF-only
in the index AND the working tree. Enforce via:

1. `.gitattributes` at repo root — `* text=auto eol=lf` plus explicit
   `*.lua text eol=lf` belt-and-braces. Survives any contributor's
   `core.autocrlf=true` machine config.
2. A unit test that asserts `BridgeLuaResources.Read(name)` contains
   neither `\r\n` nor bare `\r`. See
   [BridgeLuaResourcesTests.cs `Read_EmbeddedContent_IsLfOnly_NoCrlf`](../ui/UnityDumpUI.Tests/BridgeLuaResourcesTests.cs).
3. A byte-for-byte round-trip test (NOT a 5%-tolerance length check —
   that was the original test that missed this). See same file's
   `Read_RoundTripsPhysicalFile_ByteForByte`.

The fix to a CRLF-contaminated file is one line of PowerShell:

```powershell
$src = 'path\to\file.lua'
$bytes = [System.IO.File]::ReadAllBytes($src)
$lf = ([System.Text.Encoding]::UTF8.GetString($bytes)) -replace "`r`n", "`n"
[System.IO.File]::WriteAllBytes($src, [System.Text.Encoding]::UTF8.GetBytes($lf))
```

`git ls-files --eol scripts/*.lua` is the diagnostic of choice — it
shows `i/<eol>` (index) and `w/<eol>` (working tree) per file. Both
must be `lf` for `*.lua` patterns.

A future `Phase 22.x` could push the fix upstream to the AOBMaker
plugin (compute `expectedSize` *after* the same normalisation lua
applies on the way in), but the source-side fix is sufficient and
doesn't depend on the plugin shipping a patch.

### Generated Lua AA scripts: never write `''` for an apostrophe inside a single-quoted string

**Symptom**: an AA script generated by Discrete (or any C#-side
template) hits a CE Lua parser error like
`')' expected near "s symbolhandler to finish scanning, then"` and
the *entire chunk fails to parse* — the [ENABLE] block silently does
nothing despite CE reporting the toggle succeeded.

**Root cause**: writing `print('... CE''s symbolhandler ...')` works
in SQL, VB, Pascal — but Lua interprets two adjacent single quotes
inside a single-quoted string as "close string, immediately open
new string". The following content (`s symbolhandler...`) is parsed
as a new string with no preceding `..` concat operator → syntax
error. Surfaced 2026-05-12 during Phase 22f live smoke on the
bootstrap AA's soft-fail print.

**Rule**: in Lua single-quoted strings, escape apostrophes as `\'`.
In C# verbatim strings (`@"..."`), that's literally `\'` (two
characters — `\` and `'`, both pass through). Or use Lua's
double-quoted string `"..."` which doesn't need apostrophe escaping
at all (but in a C# verbatim string then needs `""` for each `"`).

**Regression bar**: every AA-script generator's test should include
a `\w''\w` regex check (`Assert.DoesNotMatch(@"\w''\w", script)`).
The word-character bracket avoids false-positiving on legitimate
empty Lua strings between commas (e.g. `, '', ` from an empty
namespace arg in `callUnityMethod`). See
[BootstrapAaScriptGeneratorTests.cs `Build_HasNo_AdjacentSingleQuotes_LuaSyntaxTrap`](../ui/UnityDumpUI.Tests/BootstrapAaScriptGeneratorTests.cs)
+ matching tests in CallMethod / AddressLookup generators.

### Mono JIT AA scripts cannot use AOB scan (multi-instance + no module)

**Symptom**: AA script generated against a Mono game's method
either:
(a) emits `aobscanmodule(name, , pattern)` with empty MODULE and CE
silently fails every scan; or
(b) emits `aobscan(name, pattern)` (whole-process) and CE finds
N hits where only 1 is the real one — picks the first which is
rarely correct.

**Root cause (a)**: Mono JIT-compiles each method on first use and
the resulting code page is `VirtualAlloc`'d — doesn't live in any
.dll module. `getMethodAobPattern`'s ModuleName comes back empty.

**Root cause (b)**: Mono routinely emits MULTIPLE instantiations of
the same source method (different generic param combinations,
optimization tiers, IL → x64 retries). Their prologues are
byte-identical, so any AOB long enough to be unique within one
instance is also a substring of every other instance. Whole-process
AOB scan finds them all.

**Rule**: for Mono backends, AA scripts MUST resolve method
addresses through the DLL pipe (the `findMethodInstances` RPC calls
`mono_compile_method` server-side and is overload-precise).
Discrete's `unity_symbols.lua` exposes `registerMethodSymbol(symName,
asm, ns, cls, method, sigFilter)` as the single-overload bridge call
the AA generator emits. AA `[ENABLE]` form on Mono:

```lua
[ENABLE]
{$lua}
if not _G.discrete_bridgeLoaded then
  print('[Discrete] Enable "Load Discrete Bridge" first')
  return
end
registerMethodSymbol('Mathf_Abs_int32', 'UnityEngine.CoreModule',
  'UnityEngine', 'Mathf', 'Abs', '(System.Int32)')

[DISABLE]
{$lua}
if syntaxcheck then return end
pcall(unregisterSymbol, 'Mathf_Abs_int32')
```

IL2CPP backends are different — AOT-compiled methods live inside
`GameAssembly.dll` with a stable module offset, so `aobscanmodule`
form is fine. The generator branches per-overload on `ModuleName`
emptiness. See `AobAaScriptGenerator.cs` for the dispatcher.

### DLL pipe must support multiple concurrent clients in Phase 22

**Symptom**: bridge `rpc()` from CE Lua AA script fails with
`pipe open: \\.\pipe\UnityDumpBfx: Invalid argument` *while*
DiscreteUI is connected to the same DLL.

**Root cause**: the DLL's `CreateNamedPipeW` was called with
`nMaxInstances=1` (Phase 11 design — single-client at a time was the
expected case + avoided ghost-instance accumulation across game
crashes). Phase 22's workflow has DiscreteUI persistently connected
AND the CE Lua bridge issuing bursts → contention on the only slot
→ second client gets `ERROR_PIPE_BUSY` (translated as "Invalid
argument" by Lua's CRT `fopen`).

**Rule**: when designing the DLL's pipe server, plan for N
concurrent clients from day one if any "UI + scripted helper" mode
is on the roadmap. Implementation: bump `nMaxInstances` to a small
constant (Discrete uses 4) AND detach a worker thread per accepted
client so the main accept loop doesn't block in `ServeClient` for
the duration of a long-lived UI session. Handlers must be
thread-safe — most read-only walks over Mono / IL2CPP runtime
structures are inherently re-entrant; mutable caches need locks.
See [`PipeServer.cpp` `WorkerThread`](../dll/src/shared/PipeServer.cpp)
for the canonical pattern.

Trade-off: ghost-instance pile-up across game crashes can fill up
to `nMaxInstances` slots before the kernel cleans them. With N=4
this is rarely a problem; clients' existing connect-retry logic
handles the transient `ERROR_PIPE_BUSY` window.

### CE's `executeCodeEx` is unreliable for DLL function calls; prefer pipe RPC

**Symptom**: CE Lua's
`executeCodeEx(callConv, getAddress('UnityDumper_GetBuildNumber'))`
returns `nil` even when the DLL is fully loaded, the export resolves
(non-zero address from `getAddress`), and every pipe-based RPC to
the same DLL works fine.

**Root cause**: CE's `executeCodeEx` runs the target function via a
remote thread + thunk in the target process. The mechanism is
finicky on Windows x64 — varies across CE versions, sometimes loses
return values for `__cdecl` exports with simple integer returns.
Doesn't crash, doesn't log, just returns nil. Surfaced 2026-05-18
during Phase 22f live smoke as the bootstrap AA's "DLL build
unknown" false-negative.

**Rule**: when a DLL exposes BOTH C exports AND a named-pipe RPC
surface, prefer the pipe for ALL Lua-side calls. The pipe path is:
- more reliable (no thread / thunk fragility)
- structured (JSON wire — easy to add fields without breaking
  callers)
- testable (you can hit it from PowerShell / Python / curl-like
  tools without a CE instance)

`executeCodeEx` is fine as an escape hatch for one-off pokes (e.g.
calling a known-stable global init), but anything in the user-
visible smoke / startup path should go through the pipe. See
[`BootstrapAaScriptGenerator.cs`](../ui/UnityDumpUI.Core/Services/Export/BootstrapAaScriptGenerator.cs)
for the canonical inline-handshake pattern (12 lines of Lua;
open pipe, write `{"cmd":"handshake"}`, read response, regex-extract
two fields).

## x86 / WOW64 portability (Phase 12d)

### Pointer-width-relative offsets, not hardcoded x64

**Symptom**: post-Phase-12d, three different x86 Mono targets
(Legend of Mortal / Kingdom / LEAVES) all handshake-passed and
walked classes cleanly, but `findInstances` results varied
wildly: 19 / 100+ / **0** matches respectively. The `class_ptr`
(scan target) resolved correctly in all three cases — the bug
was in the SCAN FILTER, not the scan target.

**Root cause**:
[`Memory.cpp::CollectThreadStackRanges`](../dll/src/shared/Memory.cpp)
hardcoded the x64 TEB layout for reading per-thread stack
base/limit:

```cpp
SafeReadBytes(teb + 0x08, &stackBase,  sizeof(stackBase));  // x64
SafeReadBytes(teb + 0x10, &stackLimit, sizeof(stackLimit)); // x64
```

On x86, NT_TIB is `(ExceptionList, StackBase, StackLimit, ...)`
with each field being `sizeof(void*)` = 4 bytes wide, so:

- x64: StackBase at +0x08, StackLimit at +0x10
- x86: StackBase at **+0x04**, StackLimit at **+0x08**

Reading `+0x08` on x86 returned `StackLimit` into our `stackBase`
slot, and `+0x10` returned `FiberData` (NULL on most threads).
Most reads passed the `stackLimit < stackBase && both != 0` sanity
check by accident — producing **bogus "stack" ranges** that
overlapped legitimate heap regions. ScanForPointer then filtered
those heap regions out as thread-stack-overlapping → no instance
matches.

Why the variance:
- LEAVES (Unity 5.4 / legacy mono): bogus stack ranges happened
  to overlap *every* heap region → 0 matches.
- Kingdom (Unity 5.3 / legacy mono): partial overlap → some
  matches but pre-fix would have been 0 too (Kingdom wasn't tested
  before the fix).
- Legend of Mortal (Unity 2020 / bdwgc): bogus ranges happened
  to not overlap bdwgc's specific heap regions → 19 matches
  reported, but likely under-count (post-fix re-test pending).
- Endless Legend (Unity 5.3 / legacy mono / **x64**): not
  affected — x64 reads were already correct.

**Live confirmation (LEAVES, before vs after build #349)**:

| | Before | After |
|---|---|---|
| `skipStack` | 572 (1-2 bogus per thread) | **217** (= actual thread count, 1 per thread) |
| `regions scanned` | 23 | **380** (16×) |
| `bytes scanned` | 54 MB | **305 MB** |
| `count` (Transform) | **0** | **7** |

**Fix**: scale TEB offsets with `sizeof(void*)`:

```cpp
constexpr std::size_t kStackBaseOffset  = sizeof(void*);
constexpr std::size_t kStackLimitOffset = 2 * sizeof(void*);
SafeReadBytes(teb + kStackBaseOffset,  &stackBase,  sizeof(stackBase));
SafeReadBytes(teb + kStackLimitOffset, &stackLimit, sizeof(stackLimit));
```

Same pattern as
[`ManagedLayout.h`](../dll/src/shared/ManagedLayout.h)'s
`kObjectHeader = 2 * sizeof(void*)`, `kStringLengthOffset`, etc.

**Rule**: any byte-offset constant that addresses a struct from
the OS or a runtime that ships in both x86 and x64 variants MUST
scale with `sizeof(void*)` (or wrap in an `#if`-guarded
arch-specific value). This includes:

- NT_TIB / TEB offsets (Windows kernel structs)
- MonoObject / Il2CppObject prefix + field offsets
- System.Exception / MonoString / MonoArray layouts (corlib structs)
- Any C++ struct from `winternl.h` or `ntddk.h`

Cheap diagnostic when an x86 build behaves oddly: dump
`scan_stats` and look for `skipStack >> threadStacks`. The ratio
should be ~1:1 (one stack range per thread). Anything > 1.5:1
hints at bogus ranges from mis-sized reads.

### Bitness-mismatch inject can't `CreateRemoteThread` reliably

**Symptom**: pre-Phase-12d, DiscreteUI x64 explicitly refused to
inject into 32-bit targets with `"UnityCEDumper is x64-only."` —
not because it physically couldn't run `OpenProcess` + `VirtualAllocEx`
+ `CreateRemoteThread`, but because the WOW64 boundary makes the
remote `LoadLibraryW` thunk fragile:
- `LoadLibraryW` lives in WOW64's `kernel32.dll`, not the x64 one
  loaded into the x64 caller's address space. The `FARPROC` you
  resolve in your own process via `GetProcAddress` isn't valid
  in the WOW64 target.
- `GetExitCodeThread` returns a `DWORD` (32-bit), which can't
  faithfully carry a 64-bit HMODULE return from a 64-bit
  `LoadLibrary` (irrelevant on x86 target but the underlying
  thread API has the truncation issue baked in).
- Even when it appears to work, the loaded DLL would be x64,
  not x86 — same-bitness inject is the constraint.

**Rule**: cross-bitness inject = shell out to a same-bitness
helper. We ship
[`dist/x86/inject-x86.exe`](../tools/inject-x86/main.cpp)
(15.5 KB standalone) that does classic
`OpenProcess` → `VirtualAllocEx` → `WriteProcessMemory` →
`CreateRemoteThread(LoadLibraryW)` from a same-bitness world.
The C# `WindowsDllInjector` checks `IsWow64Process` and shells
out to the helper for WOW64 targets;
[`tools/Inject-Dll.ps1`](../tools/Inject-Dll.ps1) does the same.

The standard pattern used by BepInEx, MelonLoader, and similar
tooling for the same reason — there's no clean in-process
cross-bitness inject on Windows.

### MonoResolver gate is over-strict for legacy mono.dll

**Symptom**: handshake passed, BackendDetector reported
`backend=Mono` with the right Unity version, but `assemblies_count=0`
and every RPC after handshake returned `"mono runtime not resolved"`.
Init log showed `MonoResolver: mono.dll — bound 29/32 exports` with
specific exports missing.

**Root cause**: pre-bdwgc legacy `mono.dll` (Unity 5.x and earlier)
doesn't export the modern Mono API surface in full. Three exports
the resolver was treating as `tryBind` (counted toward
`resolved = (ok == total)`) are actually safe to be missing:

- `mono_assembly_get_name` + `mono_assembly_name_get_name` —
  `MonoDomain::OnAssembly` uses `image_get_name` as the primary
  path and only falls back to this pair when image-side returns
  empty. Both call sites are null-guarded.
- `mono_free` — used to free the `char*` returned by
  `mono_type_get_name`. Without it we leak a small string per
  type-name lookup; bounded, non-fatal.
  [`MonoTypeParser.cpp:35`](../dll/src/mono/MonoTypeParser.cpp)
  is already null-guarded.

**Rule**: `tryBind` (counts toward `resolved`) is only for exports
that the resolver genuinely *can't* operate without. Null-guarded
fallback paths → `optBind` (binds but doesn't gate). The cost of
demotion when wrong: a legacy target that should fail explicitly
fails silently instead. Mitigate with the `bound N/M exports` log
line at the bottom of `DoResolve` — any production target on a
modern Mono should hit `M/M`; anything less is a hint that an
optBind-demoted fallback path is being taken.

### MonoObject layout has no padding hole on x86

**Symptom**: walking a Transform on x64 + Mono shows
`m_InstanceID` at +0x10 and `m_CachedPtr` at +0x18 (4 bytes of
visible padding). On x86 the same fields appear at +0x08 and
**+0x0C** (no padding).

**Root cause**: `m_InstanceID` is a managed `int32` (always 4
bytes); `m_CachedPtr` is `System.IntPtr` (4 bytes on x86, 8 bytes
on x64). The CLR's natural-alignment rules require pointer-sized
fields to be aligned on their own size:
- x64: int32 at +0x10, then 4 bytes padding, then IntPtr at +0x18
  (8-byte aligned). Net offset += 12.
- x86: int32 at +0x08, IntPtr at +0x0C (4-byte aligned,
  immediately follows). Net offset += 8.

**Rule**: when documenting field offsets, ALWAYS state them
relative to `kObjectHeader` and call out the alignment story.
Don't write "+0x18 m_CachedPtr"; write "+kObjectHeader +
sizeof(int32) + align-up-to-sizeof(IntPtr) padding". The
on-the-wire `walkInstance` already gets this right because it
relies on `mono_field_get_offset` which the runtime computes per-
arch; the doc concern is purely about explaining offsets to
human readers without leading them to assume x64.
