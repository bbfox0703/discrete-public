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
- [Overload disambiguation](#overload-disambiguation) — CE "Activate
  Mono features" is universal (Mono + IL2CPP) so signature-match
  register isn't Mono-only, IL2CPP code stripping,
  enum-marshal-as-int32, `signatureMatch` substring traps, AA-script
  idempotency.
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
- [x86 / WOW64 portability (Phase 12d)](#x86--wow64-portability-phase-12d)
  — TEB-offset bug in `CollectThreadStackRanges`, kObjectHeader
  arch-scaling, cross-bitness pipe ABI is bitness-neutral.
- [Obfuscated / packed IL2CPP target diagnostic fingerprint](#obfuscated--packed-il2cpp-target-diagnostic-fingerprint)
  — two-symptom signature (stripped exports + encrypted metadata) in
  `init` + `metadata` logs that identifies packed / encrypted IL2CPP
  builds within seconds; why `walk` log category never appears;
  third-party `*Hook*` DLLs are *not* definitive evidence (counter-
  example: Subnautica); what work would be needed to support these
  targets (~6-8 days, deferred until 2+ samples land).
- [Unity DOTS / ECS targets — gameplay data is NOT in managed objects](#unity-dots--ecs-targets--gameplay-data-is-not-in-managed-objects)
  — why a managed snapshot comes back near-empty on a DOTS game
  (Core Keeper), why CE sees 40–60 duplicate values, what
  `lib_burst_generated_*` actually is, and the parked ECS-aware-walker
  idea (Mono makes it feasible but C/P not worth it yet).

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
The per-PID init log (`Discrete_Dumper\Logs\<pid>\init\current.log`)
stopped at the post-init "hook armed on il2cpp_runtime_invoke" line
with no shutdown banner — meaning `Discrete_Shutdown` either never
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

### Phase 29a — even ONE attach guard isn't enough if the work volume inside the guard explodes ReadClassInfo calls — close-hang via BDWGC small-object pressure  ✅ **Fixed build #602**

**Symptom**: After Phase 29a's snapshot RPCs landed at build #598, the
BongoCat (Mono / Unity 6000.2 / x64) target stopped freezing
*during* the snapshot — but **the game then hung on close** and
required force-kill. 活俠傳 (Mono x86) at the same build was clean.
SaGa F2R (IL2CPP) was clean.

Cross-target matrix:
* BongoCat: ~12s snapshot, **game hangs on close**.
* 活俠傳:    ~48s snapshot, no close-hang.
* SaGa F2R: ~29s snapshot, no close-hang (IL2CPP has no BDWGC).

Diff between BongoCat and 活俠傳 wasn't the platform (both Mono BDWGC)
— it was the *instance volume captured*: BongoCat 20,664 vs 活俠傳
787. The instance walker did per-instance × per-parent-class
`Mono::ReadClassInfo` calls (in turn calling `mono_type_get_name` →
small string allocs → BDWGC bookkeeping). BongoCat math: 20,664 ×
~4 parent depth ≈ **80,000 ReadClassInfo calls per snapshot**.
活俠傳: 787 × ~3 ≈ 2,400 calls — well within tolerance.

**Root cause**: even under ONE outer `ThreadAttachGuard` (the build
#598 fix vs the per-chunk attach pattern), the *workload* inside the
guard can saturate BDWGC's small-object pool with intermediate
allocations. When the game eventually shuts down, BDWGC has to drain
all those allocs through its finalizer / sweep pipeline → main
thread stalls.

Two distinct flavours of "BDWGC stall on shutdown" now documented:
1. **Many attach/detach cycles** (this file's earlier "Phase 27
   follow-up" entry) — runtime queues a thread-handle ref per cycle;
   accumulates regardless of work volume.
2. **One attach but heavy runtime-call traffic inside** (this entry) —
   accumulated small-object allocs from `mono_type_get_name` etc.

The first is fixed by reentrant guard; the second is fixed by
**deduping the runtime calls themselves**.

**Fix**: pre-cache `ReadClassInfo` per chain entry, ONCE per class
plan item, reuse across all instances of that class. New call volume:
448 classes × ~4 chain depth = **~1,800 ReadClassInfo calls** instead
of 80,000 — a 40× reduction. The per-instance inner loop becomes
pure memory reads via `SafeReadBytes`, zero Mono calls.

Per [`dll/src/shared/ExportAPI.cpp` snapshot handlers](../dll/src/shared/ExportAPI.cpp):

```cpp
// Bad — per-instance × per-parent ReadClassInfo (build #598):
for (auto instAddr : item.instances) {
    std::vector<MonoClass*> chain;  // rebuilt every iteration
    for (MonoClass* cur = k; ...) chain.push_back(cur);
    for (auto chIt = chain.rbegin(); chIt != chain.rend(); ++chIt) {
        auto pr = Mono::ReadClassInfo(*chIt);  // ← N × M calls
        ...
    }
}

// Good — chain + ChainInfo built ONCE per class (build #602):
std::vector<MonoClass*> chain;
for (MonoClass* cur = k; ...) chain.push_back(cur);
std::vector<Mono::ClassInfoResult> chainInfo;
for (auto* c : chain) chainInfo.push_back(Mono::ReadClassInfo(c));

for (auto instAddr : item.instances) {
    for (std::size_t ci = chain.size(); ci > 0; --ci) {
        const auto& pr = chainInfo[ci - 1];  // ← cached
        ...
    }
}
```

Verified end-to-end on BongoCat at build #602: 12.3s → 8.4s (30%
faster) AND **no close hang**.

**Generalization**: when a batch loop calls into Mono / IL2CPP
metadata APIs that return "the same answer per class", cache by
class pointer. The reentrant-guard rule (one outer attach) is
necessary but **not sufficient** if the volume of inner runtime
calls is itself high enough to strain BDWGC. Always check the
volume math: instance count × parent chain depth × API-calls-per-
ReadClassInfo. If product > ~10K, cache or batch.

**How to spot this in the future**: if a feature works on a small
target but causes "game hangs on close" on a big target with the
same platform, suspect heavy runtime-API traffic inside the work
loop. Symptom signature: the script/UI completes normally, but
killing the game window blocks for tens of seconds → forced
process kill required.

---

### Phase 29a-v4 — mono_class_vtable from worker thread wedges game's main thread on next allocation  ✅ **Fixed build #637**

**Symptom**: After Phase 29b snapshot tab landed (build #604), and
through five iterations of "fix the Mono freeze" debugging (#618 GC
drain, #627 batch attach + yields, #629 heap-walk yield, #631 ulong
DTO + revert batch-attach), BongoCat (Mono x64) AND 活俠傳 (Mono x86)
both continued to wedge the game window AFTER our snapshotBegin RPC
returned. Three diagnostic clues converged:

1. **BGM kept playing** on 活俠傳 — audio thread was alive, only
   game's main thread (rendering + input) frozen.
2. **BongoCat's cat moved once on mouse click then wedged** — main
   thread processed ONE input event, then blocked.
3. **Hang happened AFTER Capturing ended** — not during our work,
   but right when our pipe worker stopped doing things and game's
   main thread tried to resume.

SaGa F2R (IL2CPP x64) had ZERO freeze with the same code paths.

**Root cause**: `Mono::ResolveStaticBaseAddress` calls
`mono_class_vtable(domain, klass)` once per class with static fields
— under our worker thread's `ThreadAttachGuard`. `mono_class_vtable`
lazily allocates a `MonoVTable*` in BDWGC's managed heap when one
doesn't already exist. For 活俠傳 with 3,647 user classes that's
~700-1000 vtable allocations during a snapshot; for BongoCat ~100-
200.

BDWGC tracks every allocation it makes. When the game's main thread
NEXT tries to allocate anything (which is roughly every frame for a
Unity game — UI rendering, boxed values, interned strings, etc.),
BDWGC's allocation path may trigger a major GC. With our 700+
vtable refs added since the last GC, that major GC takes much longer
than usual — long enough for Windows DWM to mark the window
unresponsive (≥5 s of no WM_PAINT).

The mono_gc_collect drain we added at the end of our decode (build
#618-633) ran in 19-22ms on our worker thread. It did COLLECT
unreachable objects, but it didn't drain the work the game's
main thread is responsible for — the bookkeeping for the new vtable
refs sits in metadata structures the game's main thread needs to
acknowledge on its own GC cycle.

The 500ms Sleep "breathe" we added didn't help either — the game's
main thread won't be CPU-starved by us during that sleep (we
released the attach + we're sleeping), but it has nothing else to
GC against until it tries to ALLOCATE again, which is whatever the
next frame happens to do.

**Why IL2CPP doesn't have this**: IL2CPP uses a different GC
implementation (typically Boehm-Demers-Weiser variant in il2cpp.dll's
managed heap, but with different collection scheduling that doesn't
piggyback on the next allocation from the main thread). Also,
`il2cpp_class_get_static_data(klass)` doesn't trigger vtable
allocation — IL2CPP's static fields are AOT-laid-out, so the static
data pointer is already resolved at compile time.

**Fix**: stop calling `mono_class_vtable` from the snapshot decode
path on Mono. The snapshot still captures static field METADATA
(name, type, offset) for diff purposes — those come from
`Mono::ReadClassInfo` which doesn't trigger vtable allocation. The
VALUE read (`base + offset`) requires the static base, which
requires the vtable, which is the part we skip. Static rows now
appear with `error: "static_value_skipped_mono"` in the wire shape;
UI shows "(static, value not captured on Mono)" so users know it's
intentional, not a bug.

Trade-off acknowledged: snapshot diff on Mono loses static-value
tracking. Users who specifically need a static value still get it
via the Browser tab's static-field path — that's a single
`mono_class_vtable` call for one class, no bulk pressure on the
game.

**Generalization**:

1. ANY Mono runtime call that lazily allocates managed memory
   (vtables, type info, JIT trampolines, string interning) from a
   non-main thread defers cleanup work to the game's main thread.
   On heavy batch workloads this stalls the main thread for as long
   as the next GC cycle takes.
2. Calling such APIs from a `mono_thread_attach`'d worker thread
   doesn't make them safe — attach just lets you call them without
   crashing. It doesn't make Mono's GC scheduling treat your
   allocations as the main thread's.
3. The disconnect-time drain hook (Phase 27 follow-up entry above)
   covers the OPPOSITE direction: you've ALREADY done the work, the
   queue is sitting there, drain it before disconnect. That doesn't
   help WITHIN a session.

**How to spot this in the future**: if a Mono-only feature shows
"game responsive during our work, freezes AFTER our work returns",
suspect lazy-allocation calls in your worker-thread runtime API
usage. Audit for `mono_class_vtable`, `mono_class_init`,
`mono_runtime_class_init`, `mono_compile_method`,
`mono_string_intern`, and anything in the `mono_object_new` /
`mono_array_new` family. If you find any: either move them to the
game's main thread via the Phase 20-MT dispatcher, or find a
metadata-driven alternative.

**Cross-check**: BongoCat at build #602 (where this lesson didn't
exist) didn't freeze the game — but that's because #602 used the
LEGACY `ResolveStaticFieldAddress(class, fieldName)` per-static-
field path, which called `mono_class_vtable` once per static
INSTEAD of once per CLASS. Larger absolute call count BUT each call
returned an existing vtable (from build #602's earlier scan paths),
so few new allocations. Build #631's per-class `ResolveStaticBaseAddress`
optimisation that LOOKED like a perf win was actually triggering
MORE fresh vtable allocations because the call rate was lower so
the runtime had less chance to satisfy each call from cache.
Counter-intuitive but logged here.

---

### Phase C follow-up — IL2CPP equivalent: per-RPC ThreadAttachGuard batching across N pipe calls stalls shutdown  ✅ **Fixed build #496**

**Symptom**: SaGa Frontier 2 Remastered (IL2CPP v29, Unity 2021.3.39f1)
takes **>60 seconds to quit** after running the new Tools menu
**Export il2cpp.h (whole game)…** action (build #486). User has to
kill via Task Manager. Game window closes immediately on quit
request; process lingers.

The Phase C exporter walks 3,765 classes by calling `getClassInfo`
as 3,765 **separate pipe RPCs** from the UI side. Each RPC enters
the DLL handler, opens a `ThreadAttachGuard`, calls IL2CPP runtime
APIs (`il2cpp_class_from_name`, `_get_field_count`, `_get_methods`),
and closes the guard.

**Root cause family**: identical to the Mono Phase 27 follow-up
entry above — mass-batch runtime API calls leave per-call cleanup
work that the runtime defers to shutdown. The difference is:

* The Mono fix amortises attach/detach **within one RPC** (the
  `ThreadAttachGuard` reentrancy counter lets a scan handler open
  one outer guard before the WhatIsAt batch loop).
* The Phase C case crosses **N separate RPCs**, so the per-thread
  reentrancy counter doesn't fire — each pipe handler is its own
  entry point.

The IL2CPP runtime's per-class lookups may add metadata refs that
accumulate over the batch; quit-time IL2CPP teardown then has 3,765×
deferred cleanup to walk. The Mono BDWGC finalizer-queue mechanism
is the canonical example, but IL2CPP shows analogous behaviour on
large batches.

**Fix (build #496)**: new server-side `dumpClassInfo` RPC handler
([`dll/src/shared/ExportAPI.cpp`](../dll/src/shared/ExportAPI.cpp))
walks every user-assembly class in ONE pipe round-trip — registered
on BOTH backends (Mono mirror at the equivalent line in the Mono
handler block; Mono method rows carry null `Address`). The UI's
`Il2cppHeaderBatchExporter` gained a `RenderFromBulk()` entry point
that projects `DumpClassInfoResult` through the same `EmitOneClass`
helper the legacy delegate-driven `ExportAsync` uses, so output
shape is identical. MainWindowViewModel switched from the per-class
loop to one `BackendAdapter.DumpClassInfoAsync` call. Side benefits
realised in the fix:

* Cuts whole-game export time from ~20-40s to ~2-5s (one pipe
  round-trip vs thousands).
* Restores quit to baseline (~1s).
* Pairs naturally with offline Phase D — same
  `Il2cppHeaderBatchExporter` rendering code can consume either the
  bulk RPC (live) or the offline PE-parser output, just by swapping
  the data source.

The legacy `ExportAsync(getClasses, getClassInfo, ...)` delegate
surface stays as the test-friendly entry point (mock the lookups,
exercise the rendering contract). Production uses `RenderFromBulk`.

**Where the design went wrong**: when picking Phase C path B (UI
live-driven over path A offline), the assumption was that
"reuse existing `getClasses` / `getClassInfo` RPCs, no DLL changes"
was the small-scope path. That **was** true for the rendering
work, but it ignored the well-documented Mono lesson above — any
batch loop over runtime APIs needs amortised attach. The lesson
generalises across the pipe boundary too: any batch of pipe RPCs
that hit the runtime needs a server-side bulk variant, not just a
client-side loop.

**How to spot this**: if a new feature does N>100 pipe RPCs that
each call into the IL2CPP/Mono runtime, expect either
shutdown-latency growth or runtime resource exhaustion. Either
batch server-side, or amortise the runtime work via a cache the
RPCs can hit without re-attaching.

**Symmetry audit (build #496)** — catalogued every UI batch site
that does N back-to-back RPCs into the runtime:

| UI site | Volume | Status |
|---|---|---|
| `Il2cppHeaderBatchExporter` (whole-game il2cpp.h) | 3K-200K classes | ✅ **Fixed via bulk `dumpClassInfo`** |
| `CeXmlExporter` / `CsxExporter` live-mode pointer drilldown | tens-hundreds | Low risk — bounded by user-controlled depth + opt-in toggle |
| `AssemblyBrowserViewModel.GetClassesAsync` | 1 per click | OK |
| `LiveInspectionViewModel.ReadMemoryAsync` | 1-few per op | OK |
| `SdkHeaderExporter` ancestor walk | 1-5 depth | OK |
| `dumpIl2cppSymbols` whole-process labels | 1 RPC | Already bulk ✓ |

If you ship a new feature that does back-to-back RPCs into the
runtime, add it to this table and decide: (a) batch server-side if
volume can grow with content (assemblies / classes / methods), or
(b) leave inline and document the bound if volume is user-action
limited (clicks, drill-down depth caps).

See [docs/il2cppdumper-comparison.md](il2cppdumper-comparison.md)
Phase C log entry for the production-ship history.

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

### Phase 28b IL2CPP — `static_fields` is runtime-heap-allocated so RIP scan cannot find references

**Symptom**: `findFieldReferences` on an IL2CPP static field that
clearly MUST have readers/writers (e.g. C# compiler-generated lambda-
cache field `<>9__0_1` of type `Action<...>` — the cache pattern
literally requires `ldsfld` + `stsfld` ops at every lambda site)
returns `0 matches` with `methods scanned > 19000` and no error.
Diagnostic log shows `candidateHits=0` (not "candidates dropped" —
candidates literally never materialised). Multiple unrelated static
fields all hit the same symptom on SaGa F2R (build #546 instrumentation
+ build #555 root-cause).

**Root cause**: IL2CPP's `il2cpp_class_get_static_data(klass)` returns
the `Il2CppClass::static_fields` pointer. That pointer is allocated by
the IL2CPP runtime at class-init time via the runtime heap — observed
addresses live in `0x18xxxxxxxx` (Windows high-VA heap), well outside
the GameAssembly.dll module's image. Phase 28b v1's scanner walks
GameAssembly.dll's .text for `mov reg, [rip+disp32]` / `lea reg,
[rip+disp32]` / `mov [rip+disp32], reg` whose absolute target equals
`static_fields + fieldOffset`. **No machine instruction can RIP-
relative-address a runtime-allocated heap address — the displacement
isn't known at compile time.** AOT-compiled code reaches the field
via a 2-hop indirection:

```asm
mov  rax, [rip + CLASS_PTR_DISP32]       ; load Il2CppClass* (in .data — RIP-addressable)
mov  rax, [rax + offsetof(static_fields)] ; deref to static_fields ptr
mov  rax, [rax + 0x10]                    ; read field at offset 16 (immediate, NOT RIP)
```

So the scan structurally cannot see any reference. Returning 0 matches
is technically correct but misleading — the user infers "field is
unused" when really "this scanner can't see them".

**Rule**: Phase 28b v1's IL2CPP path is fundamentally unable to find
references for any IL2CPP static whose `static_fields` lives outside
the module image (AFAICT in modern Unity, that's every user-code
static). Build #555 surfaces a yellow advisory banner in
[FieldReferencesWindow.axaml](../ui/UnityDumpUI/Views/FieldReferencesWindow.axaml)
explaining the limitation when the scanner detects an out-of-module
target with `candidateHits=0`. The Mono backend's IL walker handles
this case correctly because IL bytecode preserves the `ldsfld <token>`
op — recommend Mono targets for x-ref work until Phase 28b-v2 lands.

**Cross-reference**: Phase 28b-v2 in [plan.md](plan.md) shipped the
proper fix (build #565) — scan for RIP refs to the `Il2CppClass*`
pointer (which DOES live in .data and IS RIP-addressable), then
Zydis forward-disassemble each hit to follow the 2-hop chain.
Live-verified on SaGa F2R: `SteamManager.s_instance` returned
**11 unique chain-exact matches** (7 reads + 4 writes across 6
methods).

**Source**: [`IL2CPPFieldXref.cpp::FindStaticFieldReferences`](../dll/src/il2cpp/IL2CPPFieldXref.cpp)
sets `result.note` when scan completes with 0 matches and target
sits outside the GameAssembly.dll module. Phase 28b-v2 chain
detector ([`IL2CPPChainDetector.cpp`](../dll/src/il2cpp/IL2CPPChainDetector.cpp))
fires as fallback before the advisory note is set; only fields the
v2 chain detector ALSO can't find still trigger the banner (e.g.
the deferred Phase 28b-v3 `<>c` lambda-cache codegen pattern).

### Phase 28b IL2CPP instance fields — displacement-scan heuristic (no `this` taint)

**Context**: Instance fields have no single storage address (per-object on the
GC heap), so the static-field RIP/chain scanners don't apply. The codegen for
`this.field` is `mov reg, [base + fieldOffset]` where `base` derives from the
instance pointer — the only stable signal is the **displacement == the field's
offset within `Il2CppObject`** (`il2cpp_field_get_offset`, which already
includes the 0x10 header for reference types).

**Approach (v1, 2026-05-31)**: `FindInstanceFieldReferences` Zydis-decodes the
**declaring class's own methods only** (filter `BuildMethodIndex` by ns+class;
method end = next-greater address in the full sorted index, clamped to
`start+8KB` and the section end; stop at `int3` padding) and emits every
`[gpReg + fieldOffset]` access (base ≠ `rip`/`rsp`/`rbp`, `index==NONE`,
`disp.size!=0`) as `confidence="heuristic"`, classified read/write via the
memory operand's `ZYDIS_OPERAND_ACTION_MASK_*` (`lea` → read). The ExportAPI
handler resolves the field's `isStatic` up front and dispatches static →
`FindStaticFieldReferences`, instance → `FindInstanceFieldReferences`.

**Why heuristic / why no taint**: `[reg+offset]` is ubiquitous; declaring-class
scoping makes `disp==fieldOffset` *plausible* but not certain (the same method
may touch another object at the same offset). A light rcx-derivation taint was
considered (reusing ChainDetector's `ChainState` propagation) but deliberately
**dropped for v1** — the simplest shippable version owns the FP risk via the
`heuristic` tag + UI advisory instead of code. Revisit taint only if field
testing shows the FP rate is unusable.

**Known recall gaps (documented, accepted)**: external classes accessing a
public field are missed (declaring-class scope only); accessors **inlined** into
other-class callers are missed (inherent vs Mono's pre-JIT IL walk — Mono sees
`ldfld` regardless of inlining). `this` spilled to the stack and reloaded is
still matched (we key on the displacement, not the base's provenance), so that
path keeps recall.

**Rule**: for exact instance-field x-ref prefer the Mono backend; IL2CPP
instance results are a best-effort heuristic. See
[`IL2CPPFieldXref.cpp::FindInstanceFieldReferences`](../dll/src/il2cpp/IL2CPPFieldXref.cpp).

### Phase 28b-v2 chain detector — single-register tracking misses real IL2CPP codegen

**Symptom**: First-cut Phase 28b-v2 chain detector returned 0 chain
matches against SaGa F2R `SteamManager.s_instance` despite finding
220 hop-1 candidates (`mov reg, [rip+CLASS_PTR]` instructions where
disp resolved to the class-pointer slot in `.data`). Build #562
diagnostic log:

```
ClassPointerScanner: 1 slot(s) found ✓
FieldXref: v2 scan complete — hopOneCandidates=220 chainBroken=220 chainsEmitted=0
```

Every forward-trace failed within the 8-instruction window. Pattern
recognition was theoretically sound (the 3-hop chain `mov R1,[rip+ptr]
→ mov R2,[R1+sfo] → mov R3,[R2+fo]`) but it never matched.

**Root cause**: IL2CPP AOT codegen interleaves the chain with a
class-init check that EITHER conditionally branches around the init
call OR makes a CALL to `il2cpp_runtime_class_init`. Live disasm from
SaGa F2R's `SteamManager.get_Instance`:

```asm
mov rcx, [rip + CLASS_PTR_2824308]    ; hop-1 candidate (rcx = class*)
cmp dword ptr [rcx + 0xE0], 0          ; init flag check
jne short skip_init                    ; ← first-cut trace abandoned here (COND_BR)
call il2cpp_runtime_class_init         ; ← and here (CALL clobbers volatile rcx)
skip_init:
mov rax, [rip + CLASS_PTR_2824308]    ; class ptr RELOAD (second hop-1 candidate)
mov rcx, [rax + 0xB8]                  ; hop 2 — offsetof static_fields
mov rbx, [rcx + 0x08]                  ; hop 3 READ ✓
```

Three independent issues, all surfaced by this single pattern:

1. **Single-register tracking** can't carry the class pointer across
   `mov rbx, rax` (a save-to-non-volatile that's standard before any
   CALL).
2. **COND_BR abandonment** kills the trace at `jne skip_init` even
   though the fall-through path eventually re-merges at the same
   hop-2 site.
3. **CALL abandonment** kills the trace at `il2cpp_runtime_class_init`
   even though the calling convention's non-volatile registers
   (RBX/RBP/RSI/RDI/R12-R15) survive across the call.

**Fix** ([`IL2CPPChainDetector.cpp`](../dll/src/il2cpp/IL2CPPChainDetector.cpp)
build #565):

* **Multi-register state** — replace single `classPtrReg` /
  `staticFieldsReg` with `std::vector<ZydisRegister>` sets per
  chain stage. Class pointer can live in N registers simultaneously
  after copy propagation.
* **`mov reg, reg` propagation** — when source register holds class
  ptr / static_fields ptr tag, propagate to dest. Done AFTER
  clobbering dest (handles `mov rax, rax` correctly — no-op).
* **CALL tolerance** — drop only volatile regs (RAX/RCX/RDX/R8-R11)
  from tracking sets. Non-volatile regs preserve their tags.
* **COND_BR tolerance** — skip cond branches without state update.
  Fall-through trace continues. Init-check patterns merge back to
  the same hop-2 site.
* **Trace window** — 8 → 32 instructions to absorb the scheduling
  slack between hops.
* **RET / UNCOND_BR** — still terminate (could be tail call into
  unrelated code; not worth tracing).

**Surprising observation**: IL2CPP doesn't actually save to a
non-volatile across the init call — it RELOADS the class pointer
from `.data` after the call. So both hop-1 candidates (pre-init
and post-init) are independent chain starts. The first one
abandons (CALL drops volatile), the second succeeds. Our multi-reg
machinery handles it because each hop-1 candidate spawns its own
trace.

**Result**: 0 chains → 11 unique `chain-exact` matches on
`SteamManager.s_instance`. See plan.md §Phase 28b-v2 for the
full session log.

### Phase 28b-v2 byte-pattern pre-filter — REX-prefixed insns match twice without explicit skip

**Symptom**: After the multi-register chain detector started
matching, every chain hit was reported twice in the UI. SaGa F2R
SteamManager.s_instance showed **22 rows** when the actual unique
count was 11.

**Root cause**: The byte-pattern pre-filter that gates the expensive
Zydis decode was checking `IsRexByte(buf[p])` to detect 7-byte
REX-prefixed `mov reg, [rip+disp32]` instructions, but NOT skipping
when the previous byte was a REX prefix. So a 7-byte instruction
`48 8B 05 XX XX XX XX` at IP=N would match TWICE in the scanner:

1. At p=N: REX-prefix path matches the 7-byte form correctly.
2. At p=N+1: 6-byte form path interprets the 0x8B as a fresh
   opcode, ModRM=0x05 still indicates RIP-relative, disp32 is the
   same 4 bytes — and `IP=(N+1) + 6 + disp == N + 7 + disp` =
   same resolved target. Spurious match.

Both candidates would Zydis-decode + forward-trace + emit the
same chain.

**Fix** ([`IL2CPPChainDetector.cpp:512`](../dll/src/il2cpp/IL2CPPChainDetector.cpp)
build #565):

```cpp
// Skip 6-byte form when previous byte was a REX prefix — the
// 7-byte form already covered this instruction at p-1.
if (p > 0 && IsRexByte(buf[p - 1])) continue;
```

This is the same check the v1 scanner (`FindStaticFieldReferences`
v1 path) has in `IL2CPPFieldXref.cpp:505`. The chain detector
forgot to mirror it because it inlines its own pre-filter rather
than reusing the v1 helper. Belt-and-braces hit-IP `std::unique`
at the end of `DetectFieldAccessChains` catches any remaining
convergence cases (e.g. multiple hop-1 candidates that legitimately
fall through to the same hop-3 IP).

**Rule for the codebase**: any new byte-pattern scanner for x64
RIP-relative instructions must skip when the previous byte was a
REX prefix (`0x40-0x4F`). Otherwise REX-prefixed instructions are
double-counted. Failure mode is silent — duplicate results
look like correct results unless you cross-check counts.

### Phase 28b-v3 — `<>c` lambda-cache classes use per-AOT-method lazy-init slots

**Symptom**: Phase 28b-v2 chain detector finds `SteamManager.s_instance`
fine (11 chain-exact matches) but returns 0 matches for compiler-generated
`<>c` lambda-cache classes (e.g. SaGa F2R
`MainGame/StrReader2UShortAry/<>c.<>9__0_0`). Original plan-doc hypothesis
was that IL2CPP routed compiler-generated classes through
`g_codegenModule->types[index]` — investigation showed that field doesn't
even exist in `Il2CppCodeGenModule`
([vendor/Il2CppDumper/Il2CppDumper/Il2Cpp/Il2CppClass.cs:261](../vendor/Il2CppDumper/Il2CppDumper/Il2Cpp/Il2CppClass.cs)).

**Root cause** (recovered by `diagnoseClassReferences` pipe RPC +
`getMethodAobPattern` disasm pull on live SaGa F2R, 2026-05-26): the
`<>c` class pointer IS stored in `.data`/`.bss`, but in **per-AOT-method
lazy-init cache slots** populated by `il2cpp_codegen_initialize_method`.
Each AOT method that touches a class has its own dedicated slot. The
codegen pattern is:

```asm
; Per-method lazy-init guard
cmp  byte [rip + INIT_FLAG], 0
jne  skip_init
lea  rcx, [rip + SLOT_ADDR]              ; pass &slot to init
call il2cpp_codegen_initialize_method    ; ← runtime writes Il2CppClass* into slot
lock or [rsp], 0                          ; memory barrier
mov  byte [rip + INIT_FLAG], 1
skip_init:
; Standard Phase 28b-v2 3-hop chain follows
mov  rcx, [rip + SLOT_ADDR]              ; hop 1 — now holds Il2CppClass*
mov  rdx, [rcx + 0xB8]                   ; hop 2 — static_fields
mov  [rdx + FIELD_OFFSET], rax           ; hop 3
```

Crucially, the slot stays **NULL until the AOT method first executes**.
`SteamManager` accessor methods run during game startup, so their slots
are always populated by the time the user scans for xrefs.
`<>c.<>9__0_X` lambda body methods may never execute in a given game
session (e.g. if the user hasn't triggered the LINQ query that uses the
lambda), in which case ALL the slots holding the `<>c` class pointer
stay NULL and v2's scanner finds nothing in `.data`.

**Fix** (Phase 28b-v3.0, build #581): new
[`IL2CPPLazyInitSlotScanner`](../dll/src/il2cpp/IL2CPPLazyInitSlotScanner.cpp)
that one-time `.text` walks for `lea reg, [rip+disp32]; call rel32`
2-instruction sequences (`lea` target in `.data`/`.bss` of the module,
`call` target in an exec section of the same module). Result cached via
`std::call_once`. `IL2CPPFieldXref::FindStaticFieldReferences` reads
each slot's current value with `Memory::SafeRead` — slots holding the
target class pointer get merged into the v2 chain detector's input set.
Matches surfaced solely via v3 lazy slots get `confidence="chain-lazy"`.

**v3.0 limitation**: slots whose values are still NULL can't be linked
to a class via runtime value comparison. v3.1 (deferred, ~1-2 days)
plans to parse the `MetadataInitializerEntry` struct that the `lea`
target points to and recover the typeIndex statically, cross-lookup in
`Il2CppMetadataRegistration->types[]`, derive the class — making the
mapping work even for never-executed methods.

**Rule**: when designing scanners for IL2CPP `.data` references, don't
assume every reference uses a stable cache cell — compiler-generated
classes use per-method lazy-init slots that are zero until the method
runs. The detection has to handle both "direct cache" and "lazy-init
cache" patterns, and the lazy path needs either runtime value snooping
(v3.0) or static metadata-entry parsing (v3.1) to attribute slots to
classes.

**Source**:
[`IL2CPPLazyInitSlotScanner.{h,cpp}`](../dll/src/il2cpp/IL2CPPLazyInitSlotScanner.cpp),
[`IL2CPPFieldXref.cpp` v2 fallback block](../dll/src/il2cpp/IL2CPPFieldXref.cpp),
diagnostic in
[`IL2CPPClassReferenceDiagnostic.{h,cpp}`](../dll/src/il2cpp/IL2CPPClassReferenceDiagnostic.cpp).

### Phase 28b-v3.1 dead-end — `Il2CppMetadataRegistration.metadataUsages` is NULL on v29

**Symptom**: Phase 28b-v3.1 attempt to statically map lazy-init slots
to classes (so `<>c.<>9__0_X` xref works even when the lambda has never
executed). Walked SaGa F2R lazy-init slots, captured the original tokens
(format `(kind << 29) | index`), confirmed decoding produces sensible
kind values (1=TypeInfo, 5=StringLiteral, 6=MethodRef). But every
mapping attempt failed:

| Attempt | Hypothesis | Result |
|---|---|---|
| (1) `types[index]` | index → `Il2CppType*` in `Il2CppMetadataRegistration.types[]` | typesCount=29456, indices reach 50999 — out of range |
| (2) `typeDefMap[index]` | index → `Il2CppClass*` via per-image typeStart + il2cpp_image_get_class | map size=10001, indices reach 50999 — out of range |
| (3) `metadataUsages[index]` | index → slot pointer in central usage array (v27 layout) | **metadataUsages = NULL, count = 0 in v29** |

**Root cause**: `metadataUsages` (the central indirection array at
`Il2CppMetadataRegistration + 0x78` in v24-v27) was **removed in IL2CPP
v29**. The new per-method init system (`il2cpp_codegen_initialize_runtime_metadata`
called via `lea reg,[slot]; call init` per the lessons above) uses a
completely different lookup mechanism whose static decode layout is not
yet reverse-engineered. The tokens we observe in slot cells (e.g.
`0x20007CD7` with kind=1, index=31959) are presumably keys into per-image
or per-method init data structures inside `global-metadata.dat` or the
new `Il2CppCodeGenModule` extensions, not into any runtime array we
currently expose.

**What works in v3.0**: when a lazy-init slot's CURRENT value is the
target class pointer (= the AOT method that uses the slot has executed
at least once in the current session), the runtime-value comparison
matches and v3.0 surfaces the xref correctly with `confidence=chain-lazy`.
This catches singletons + UI controller-style classes that initialise
early in game lifecycle. Compiler-generated `<>c` lambda caches whose
bodies haven't run stay unmappable.

**Diagnostic infrastructure left in place** (so v3.2 doesn't have to
rebuild it):

* [`IL2CPPClassReferenceDiagnostic.{h,cpp}`](../dll/src/il2cpp/IL2CPPClassReferenceDiagnostic.cpp)
  + `diagnoseClassReferences` pipe RPC — dumps every storage slot
  (module + heap + FieldInfo) holding a target's class ptr / static_fields /
  FieldInfo pointers, with surrounding context.
* `dumpLazyInitSlots` pipe RPC (in `ExportAPI.cpp`) — samples lazy-init
  slots with token decode + `valueLocation` heuristic + 64 B context.
* [`IL2CPPLazyInitSlotScanner.cpp`](../dll/src/il2cpp/IL2CPPLazyInitSlotScanner.cpp)
  — captures `tokenAtScan` + `tokenKind` + `tokenIndex` per slot at scan
  time, ready for v3.2 to consume once the v29 decode layout is known.
* [`IL2CPPMetadataRegistration.{h,cpp}`](../dll/src/il2cpp/IL2CPPMetadataRegistration.cpp)
  — exposes `MetadataUsagesArray()` + `MetadataUsagesCount()` (return null
  on v29 but populated on v24/v27 if a back-port is ever needed).
* `tools/Test-LazyInitSlots.ps1` — pretty-printer for live verification.

**v3.2 cookbook** (estimated 2-4 days, deferred until needed):

1. Find the v29 IL2CPP runtime's `il2cpp_codegen_initialize_runtime_metadata`
   function (frequency analysis: it's the single callTarget appearing
   thousands of times in the lazy-init pattern matches).
2. Disassemble that function with Zydis (already vendored). Trace
   what it reads from its `rcx` argument — that gives us the entry layout.
3. Likely path: the function reads a `MetadataInitializerToken` structure
   (probably 4-8 bytes after the slot, or pointed to by it). The token
   contains `(kind, source_index)` where source_index can be cross-looked
   in the global-metadata.dat's `Il2CppMetadataUsageList` /
   `Il2CppMetadataUsagePair` tables (these still exist on disk per
   Il2CppDumper).
4. Extend `MetadataParser` to read those tables.
5. Build `tokenIndex → (kind, sourceIndex)` map at startup. For
   sourceIndex with kind=TypeInfo, look up in `typeDefMap` (already
   built). For other kinds, ignore (not relevant to class-xref).
6. Wire into `ResolveTypeInfoSlotClass` — replace the current
   "metadataUsages lookup" stub.

**Rule**: when a documented offset (`0x78` for `metadataUsages`)
unexpectedly reads NULL on a target whose registration struct is
otherwise valid, suspect a runtime-version layout change rather than
your scanner being broken. Verify by reading offsets BEFORE and AFTER
the suspect field — if they're sensible, the struct's there but the
field has moved or been removed.

---

## Overload disambiguation

### CE "Activate Mono features" is universal — `registerSymbolBySignatureMatch` works on IL2CPP too, not just Mono

**Symptom**: assumed the `mono_*` CE Lua API (and therefore our
`discrete_mono_registerSymbolBySignatureMatch` helper) was Mono-only, so the
"register (sig-match)" buttons were gated behind a Mono backend check and hidden
on IL2CPP targets. For IL2CPP we instead emitted hardcoded absolute addresses
(break on relaunch) or a module-relative `GameAssembly.dll+offset` (breaks on
game update) or a whole-module `aobscanmodule` of a non-unique prologue (matched
hundreds of look-alike sites).

**Root cause**: CE's **"Activate Mono features"** dissector / data collector
drives the `mono_*` Lua API (`mono_findClass`, `mono_class_enumMethods`,
`mono_method_getSignature`, `mono_compile_method`, …) on **BOTH Mono AND
IL2CPP** Unity games. The `mono_` naming is a misnomer — it is the universal
Unity runtime-introspection surface in CE. The user's own
`registerSymbolBySignatureMatch` sample was running on an IL2CPP game.

**Rule**:
- **Never** gate signature-match registration behind a Mono-only backend check —
  surface it for any connected Unity target (`SupportsSignatureMatch` = Mono ∨
  IL2CPP). The only prerequisite is the user enabling *Activate Mono features* in
  CE (a CE-side toggle we can't detect — surface it in the tooltip).
- **Never** assert `ModuleName == ""` in `MonoRegisterSymbolAaScriptGenerator` —
  the generated AA resolves by class+method+signature, so an IL2CPP method's
  `GameAssembly.dll` module name is irrelevant.
- **Never** special-case IL2CPP with `GameAssembly.dll+offset` — a baked module
  offset is the ONE thing that breaks on a game update. Signature-match is the
  only register path that survives BOTH relaunch (ASLR) and update (re-resolves
  by name). See [ce-bridge-integration.md](ce-bridge-integration.md).

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

`Discrete-Mono.dll` / `Discrete-IL2CPP.dll` stay mapped in the
target process after injection. Trying to rebuild while the game is
running fails with `LNK1104: cannot open file ...`. Close the game,
then rebuild.

### `Inject-Dll.ps1` reads from `build/dll-both-release/bin/`, NOT `dist/`

**Symptom**: a freshly-committed DLL fix appears to fail every live-
verification run. The wire response shows the pre-fix behaviour even
though the source code clearly contains the new branch.

**Root cause**: `Inject-Dll.ps1` (called by `Test-Handshake.ps1` and
DiscreteUI's "Pick & Inject" alike) reads the DLL from
`build\dll-both-release\bin\Discrete-IL2CPP.dll` (or `…-Mono.dll`).
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
Get-ChildItem build\dll-both-release\bin\Discrete-*.dll | Select Name,LastWriteTime
```

The build_number bump in `build_number.txt` is the cheapest signal —
if the UI's status bar reads a build # lower than `cat build_number.txt`,
**the running DLL is out of date no matter what `dist/` says**. Caught
2026-05-08 burning ~15 minutes on a 4-target verification run where
all four "failures" turned out to be stale DLLs from before the fix
commits.

### `build.ps1` prints `dist/` listing even when ninja fails — always grep for `Status:`

**Symptom**: Made a header-only edit (added a function decl referencing a forward-declared type but forgot the include), ran `build.ps1`, saw the script print its usual `Output: D:\Github\discrete\dist\` listing with all files. Assumed success, told user to test. User runs against an outdated DLL because the actual `.dll` file in `dist/` is from an earlier commit — the failed build never overwrote it. Wasted two round-trips before realising the build never actually succeeded.

**Root cause**: `build.ps1`'s tail block (`Get-ChildItem dist | Format-Table`) runs unconditionally and prints the EXISTING `dist/` contents whether or not the build itself succeeded. The build status (`Status: FAILED`) is printed ABOVE that listing in the summary block, but if the script's output is truncated (e.g. `Select-Object -Last 12`), the listing dominates and the status line gets cut. The only reliable signal is `ninja: build stopped: subcommand failed.` somewhere mid-output, or `Status: FAILED` in the summary table.

**Rules**:
1. After invoking `build.ps1`, **always** check `Status: SUCCESS` explicitly (or `[FAIL]` / `FAILED` if you're greping for the bad path).
   ```powershell
   .\build.cmd -Target DLL -Backend IL2CPP 2>&1 | Select-String -Pattern 'Status:|FAIL|ninja: build stopped'
   ```
2. After a build that touched the DLL, **also verify the binary actually changed** — compare the `.dll` LastWriteTime to "now", or grep for a string you know is new in this session:
   ```powershell
   (Get-Item dist\Discrete-IL2CPP.dll).LastWriteTime
   grep -c '<some-new-marker>' dist\Discrete-IL2CPP.dll
   ```
3. **User runs from `dist/`, not from `build/dll-both-release-x64/bin/`.** Even though `Inject-Dll.ps1` reads from `build/.../bin/` (per the lesson above), DiscreteUI and the proxy DLLs that the user manually copies into game folders come from `dist/`. After any partial backend rebuild, ensure `dist/` got refreshed — `build.ps1` does this only when its install step succeeds.
4. **Prefer `build.cmd` over `build.ps1`**: `build.cmd` is the canonical entry point and matches how the user invokes it manually. It also captures a transcript (`-LogFile`) which makes post-mortem diagnosis easier.

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
`pipe open: \\.\pipe\DiscreteBfx: Invalid argument` *while*
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
`executeCodeEx(callConv, getAddress('Discrete_GetBuildNumber'))`
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
inject into 32-bit targets with `"Discrete is x64-only."` —
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

---

## Obfuscated / packed IL2CPP target diagnostic fingerprint

### Three-symptom signature identifies packed / encrypted IL2CPP within seconds of injection

**Symptom**: pipe connects cleanly, no crash, no hang — but every RPC
beyond the handshake returns `"il2cpp runtime not resolved"`. The UI
shows a connected backend that can't do anything useful. No `walk`
log category gets created for the PID at all.

**First sample**: [Leaf it Alone](test-matrix.md#unsupported-targets-confirmed-failures)
(Steam #3981100, Unity 6000.2.6f2 / Unity 6.1, IL2CPP x64), reported
2026-05-25.

**Root cause**: the target ships an obfuscated / packed IL2CPP runtime.
**Two definitive symptoms** appear together because they're both
side-effects of the same protection toolchain:

1. **All `il2cpp_*` exports stripped or renamed** — in `init\current.log`:
   ```
   IL2CPPResolver: missing REQUIRED export il2cpp_domain_get
   IL2CPPResolver: missing REQUIRED export il2cpp_thread_attach
   ...
   IL2CPPResolver: GameAssembly.dll — required 0/16, optional 0/27 (resolved=no)
   ```
   `GameAssembly.dll` is loaded (visible in the module-list dump just
   above the resolver lines) but none of the 43 standard exports
   resolve through `GetProcAddress`. Packers usually replace the
   PE export table with a stub that returns null, or rename every
   export to a non-discoverable symbol.

2. **`global-metadata.dat` encrypted** — in `metadata\current.log`:
   ```
   global-metadata.dat magic mismatch (corrupt or encrypted)
   ```
   The canonical IL2CPP magic word `0xFAB11BAF` is missing from
   offset 0. Packers commonly XOR or AES the metadata at rest; the
   runtime decrypts it in-memory on first access, so an on-disk
   reader (our parser) can never see the cleartext.

**Symptom that is NOT definitive — was originally listed as #3 but
demoted 2026-05-25 after a counter-example**: a third-party `*Hook*` /
`*Protector*` DLL in the module list (e.g. `AnvirHook8_64.dll`,
`BeebyteProtector.dll`, `il2cpp_inspector_anti.dll`). This *can* be
game-side protection, but it can equally be **user-machine-wide
injected sysadmin tooling**. The counter-example: `AnvirHook8_64.dll`
appeared in both Leaf it Alone (packed IL2CPP) AND
[Subnautica](test-matrix.md#unity-targets-verified) (unprotected Mono /
Unity 2019.4 — every Discrete Mono DLL-side primitive resolves cleanly
on it: `MonoResolver bound 29/29`, main-thread hook armed, 104
assemblies enumerable). Subnautica is not plausibly self-protected;
the only consistent explanation is the user has Anvir Task Manager Pro
(or equivalent) installed and it injects into every running process at
the OS level. **Rule**: treat `*Hook*` / `*Protector*` modules as
*weak* evidence at best — cross-check with another known-good target
from the same user before concluding it's game protection. The two
real symptoms above (export stripping + metadata encryption) are
sufficient on their own.

**Why no `walk` log appears**: every pipe handler that does real work
starts with `if (!IsResolved()) return json{{"error", "..."}};`. With
the resolver at `resolved=no`, every RPC short-circuits before reaching
a single `Log::Write(LogCategoryWalk, ...)` call site. The `pipe\`
log shows the connect succeeded; no other category gets data.

**Diagnostic recipe** (30 seconds, no code changes needed):
1. Grab `%LOCALAPPDATA%\Discrete_Dumper\Logs\<pid>\init\current.log`
2. Look at the last 5 lines before `Init: main thread id = ...`
3. If you see `required 0/16, optional 0/27 (resolved=no)` → symptom 1 confirmed
4. `metadata\current.log` will show `magic mismatch` → symptom 2 confirmed
5. Verify no `walk\` subdirectory exists for the PID (proves every RPC
   short-circuited at the `!IsResolved` gate)
6. *Optional cross-check*: scan the module-dump for `*Hook*` /
   `*Protector*` DLLs — but **do not rely on this alone**, see the
   "NOT definitive" note above. If you find one, ask the user to
   run a known-good Unity game (any unprotected Mono / IL2CPP target)
   and check if the same DLL appears in that PID's `init\current.log`
   too; if yes, it's machine-wide tooling, not game protection.

If symptoms 1 + 2 + 5 all hold, the target is packed/obfuscated.
**Stop investigating the user's setup** — it's not their config, not
a Discrete bug, not the wrong DLL version. Don't try to "fix"
anything; document the target as unsupported in the matrix.

**What would be needed to support these targets** (NOT scheduled):

| Layer | Work | Effort |
|---|---|---|
| Metadata | Phase 5-2d (`IL2CPPMetadataRegistration` AOB cross-link, already noted in `plan.md` Phase 5 as a non-blocking follow-up) — find the runtime's in-memory `Il2CppMetadataRegistration` struct by AOB pattern, read decrypted metadata pointers from RAM instead of parsing `global-metadata.dat` from disk | ~2-3 days |
| Runtime API | New phase: **export-less IL2CPP fallback** — instead of `GetProcAddress("il2cpp_domain_get")`, AOB-scan `GameAssembly.dll`'s `.text` for the known function prologue patterns of each `il2cpp_*` API and self-bind. Highly version-fragile (Unity 2019.x vs 2021.x vs 6.x emit different prologues) but the only path that survives export stripping | ~3-4 days for the v1, ongoing maintenance per Unity major |
| Anti-hook coexistence | Profile what the loaded `*Hook*` DLL actually does (often nothing to injected pipe-server DLLs — it usually targets game-state cheats). Document tolerance class | ~1 day per protector |

Total: ~6-8 days for a v1 obfuscated-target backend. Defer until at
least 2 more samples land, so the AOB patterns can be cross-validated
across protector flavours.

**Rule**: when a user reports "DLL injects but nothing works on this
Unity game", FIRST check the 2-symptom fingerprint (export stripping +
metadata encryption) plus the missing-`walk\`-subdir tell before any
other debugging. Saves a 30-min wild-goose chase ~95% of the time. If
either of the two real symptoms is absent, fall back to normal
debugging — the issue is something else (wrong DLL bitness, AV
quarantine, pipe-name collision, etc.).

---

### MainWindow tab-index C# constants must mirror XAML `TabItem` order — silent mis-routing  ✅ **Fixed 2026-05-27 (`db8bb9b`)**

**Symptom**: User clicks the snapshot-diff right-click menu item
"Find what's at this address (Address Lookup)" → UI switches to
**Symbol Finder** instead. Reported 2026-05-27 immediately after the
Phase 29 polish menu addition exposed the path.

**Root cause**: `MainWindowViewModel.cs` declares one `const int`
per right-pane tab so cross-tab handoff code reads as
`SelectedDetailTabIndex = TabIndexAddressLookup;` instead of magic
numbers. The constants were defined as:

```csharp
public const int TabIndexAddressLookup = 3;
public const int TabIndexSymbolFinder  = 4;
```

But the actual `TabItem` ordering in `MainWindow.axaml` is **Symbol
Finder at index 3, Address Lookup at index 4** — Symbol Finder
(Phase 25) was inserted before Address Lookup (Phase 17c) in the
visual tree, and nobody re-checked the constant block. Every
`LookupInspectorAddressAsync` / `OnSnapshotNavigateToAddressLookup`
call has been silently routing to Symbol Finder since Phase 25
landed (commit `7b12bdf`, 2025-Q4-ish). It only surfaced now
because:

1. Address Lookup is a passive "pre-fill + query" tab — the user
   sees the address pop in regardless of whether the tab is
   actually selected; they thought the tab switch was working.
2. Pre-existing entry points (Inspector → "Find at address",
   Symbol Finder activation) routed back to tabs whose constants
   happened to match (`TabIndexFields`, `TabIndexMethods`,
   `TabIndexInspector` are all at the low end and haven't moved).
3. The snapshot-diff "Find at address" menu was the first NEW
   handoff into Address Lookup since the swap, and the user
   immediately spotted the wrong tab.

**Rule**: when adding a new `TabItem` to MainWindow.axaml's right-
pane TabControl, the very next edit MUST be to renumber the
`TabIndex*` constants in MainWindowViewModel.cs to match. Cheap
mechanical step that nobody else will catch. There's no
build-time symbol coupling between the XAML positional index and
the C# constant — Avalonia just uses zero-based positional
ordering of `<TabItem>` children, full stop.

**Detection hint**: any time a cross-tab handoff goes to the
"wrong" tab AND the underlying state (pre-fill / selection /
query) clearly happened correctly, suspect this swap before
hunting elsewhere. A 5-second grep of `TabIndex*` constant
values against the XAML's `<TabItem Header=` ordering settles it.

---

### Snapshot `FindAllInstances` 2-level scan accepts non-object memory as fake "instances" — eats per-class cap before reaching real instances  ✅ **PROPER FIX 2026-05-28 (DLL #650)**

**Symptom**: Live Inspector + per-class `findInstances` reports 152
instances of `Mortal.Core.GameStat` (e.g. 活俠傳 / Mortal money
stat at 0x2974ACC0). Snapshot Diff doesn't surface the
known-stable money value change. UI debug "list captured
instances of class X" reveals snapshot captured **256** GameStat
entries — both A and B at the cap — most with garbage values
like `_type=0 _property=712051296 _value=2061519408`. Live address
0x2974ACC0 is past the last captured entry (0x29176510), so the
real money instance never made the cap.

**Root cause**: two completely different scan strategies between
the live and bulk paths.

- Live `findInstances` (per-class):
  `Memory::ScanForPointer(vtable_ptr, limit)` — **1-level scan**.
  Looks for memory locations whose qword value equals a known
  vtable pointer. Finds object headers directly.

- Snapshot `FindAllInstances` (bulk):
  for each qword in memory, dereference it as a vtable, check
  whether the dereferenced klass pointer is in our known class
  set — **2-level scan**. Accepts ANY pointer-storing memory
  whose target happens to be a vtable.

The 2-level scan is structurally over-permissive. Anywhere a
vtable pointer is stored as a value — captured locals on the
stack, ad-hoc pointer fields in non-object structs, RGCTX cache,
copies in game data files — gets reported as a "Mono object" at
that location. Read-from-there as a "field" produces nonsense
numbers (the user-visible "garbage `_value`" tell).

On 32-bit Mono with the auto-clamped 256 per-class cap, the
~100+ false positives squeeze out real instances. Snap #2's
last captured address is below the real money instance →
diff never sees the change → user can't find it even with the
correct filter set.

**Partial fix (DLL build #648, 2026-05-28)**: cheap heuristics
that don't add Mono runtime calls (we still can't call
`mono_class_vtable` per class without wedging the game, per the
build #637 lesson):

1. Skip `MEM_MAPPED` regions in addition to `MEM_IMAGE`. Mono
   BDWGC + IL2CPP libgc heaps are always `MEM_PRIVATE`
   (VirtualAlloc/HeapAlloc backed); file-mapped regions hold
   game data that just happens to contain matching vtable
   pointer values. Filter at region level → cheap.
2. Require the instance slot address to be 8-byte aligned. Real
   Mono / IL2CPP objects are 8-byte aligned; many false-positive
   slots aren't (4-byte stride in the scan + values stored at
   word boundaries that don't double-align). Filters ~30-50% of
   the false-positive volume on the 活俠傳 / Mortal test case.
3. (Mono side only) Also applied to the inner classification
   loop so the cache memo doesn't pollute future scans with
   misclassified vtables.

**Proper fix shipped 2026-05-28 (DLL #650)**: vtable structural
validation via the `MonoDomain*` field. `MonoVTable` layout
(from `mono/metadata/class-internals.h`):

```
+0           MonoClass*       (already checked in classSet)
+ptr_size    MonoGCDescriptor (gc_descr, packed bits)
+2*ptr_size  MonoDomain*      ← signature field
+3*ptr_size  MonoType*
...
```

Real vtables ALWAYS have `*(qw + 2*ptr_size) == root_domain_ptr`.
Random structures whose first qword happens to be a class
pointer (the false-positive case) almost never have the domain
pointer stored at that offset.

The check is a single SEH-safe memory read per candidate, no
Mono runtime calls. Zero BDWGC interaction = zero risk of the
build #637 wedge. Implementation in
[MonoAddressFinder.cpp FindAllInstances](../dll/src/mono/MonoAddressFinder.cpp):

1. Sample `GetRootDomain()` once before the walk → `rootDomainAddr`.
2. After the existing 2-level deref accepts a candidate, do one
   more `SehReadKlassAt(qw + 2*ptr_size, domainAt)` read.
3. If `domainAt != rootDomainAddr`, reject (memo as non-vtable
   for the rest of the scan).

Also: stopped seeding the per-walk `vtMemo` from the global
`cache().vtableToKlass`. Older walks before this fix would have
memoised some non-vtable structures as legitimate vtables; the
old entries persist in the cache, but starting `vtMemo` fresh
each walk forces re-validation under the new check.

**Why this works and why earlier heuristics didn't**: the
8-byte alignment + MEM_MAPPED skip filters caught ~30-50% of
false positives but plenty leaked through (false positives at
8-byte-aligned addresses inside MEM_PRIVATE Mono heap memory —
e.g., RGCTX cache slots, internal lookup tables that store
vtable pointer values as their payload). The domain check
catches those because while they store a vtable POINTER (which
happens to equal a real vtable address), the surrounding bytes
are the cache structure's own layout, not the MonoVTable
layout — so the qword at "where domain should be" is whatever
the cache uses there, not the root domain.

**Detection hint** (if a future variant of this bug appears):
if `findInstances` per-class returns N matches but
`FindAllInstances` reports N + dozens of garbage-valued
"instances" for the same class, look for what other Mono
structures might have `MonoClass*` at offset 0 AND happen to
have the root domain pointer (or domain-shaped value) at
offset 2*ptr_size. So far MonoVTable is unique in that
combination, but check the Mono source for newer Unity
versions if symptoms reappear.

---

### Phase 29 high-default-caps wedge 32-bit Mono games on snapshot #2 — DLL-side JSON cache pushes past 4 GB user space  ✅ **MITIGATED 2026-05-27 (DLL #645 + UI #647)**

**Symptom**: 活俠傳 (Mono x86, Unity 2020.3.49f1, PID 26296) on
build #646:

| Step | What | Result |
|---|---|---|
| Take Snapshot #1 | InstanceCap=512, ArrayCap=256 (the new defaults) | ✅ DLL captures 3,647 classes / 119,370 instances / 19,680 arrays in 15s. UI logs `workingSet=1.83 GB (+1.62 GB)` |
| Play 2 minutes | game continues normally | (heap grows naturally inside the game) |
| Take Snapshot #2 | same caps | DLL heap-walks 886K candidate instances, decodes 258,035 (2.2× snap #1) and 30,793 arrays (1.6× snap #1), `snapshotBegin` returns clean at 10:34:47 |
| ~6s later | UI calls `snapshotChunk` repeatedly to drain the cached JSON | UI logs `Snapshot.Take failed: server closed pipe before responding`. **Game CTD.** |

DLL log has zero error markers — `snapshotBegin` #2 succeeded; the
crash happened between Begin returning and the user-side chunk
loop draining the payload.

**Root cause**: the Phase 29a-v2 architecture caches the entire
snapshot's pre-built JSON in the DLL's session struct (lives in
the **game process**), then streams it to the UI as the user
calls `snapshotChunk`. On a 32-bit Mono target the user process
has at most **~4 GB virtual address space** (3 GB on a
non-large-address-aware build). With the post-bump defaults
(InstanceCap=512 vs original 100, ArrayCap=256 vs original 50)
snap #2 holds **5–10× more JSON in the game process** than the
original sizing supported:

- ~258K captured instances × ~30 fields × ~50 bytes JSON/field
  ≈ ~390 MB
- 30K arrays × up to 256 elements × ~10 bytes each ≈ ~75 MB
- Plus heap-walk metadata + class-info cache + the game's own
  growing managed heap (~1.5–2 GB for 活俠傳 mid-game)

Total comfortably past the 32-bit ceiling on snap #2 (snap #1 fits
because the game's own state is smaller at that point in the
session). Hard OOM in the game process kills it before the UI
finishes draining chunks — visible as "server closed pipe before
responding".

**Why it worked yesterday**: yesterday's user-controlled cap was
300/50, today's default is 512/256. Same game, same UI build, same
DLL build — the only variable is the cap.

**Mitigation shipped 2026-05-27** (DLL build #645, UI #647):

1. **DLL pre-check abort gate** in both `snapshotBegin` handlers
   — `QueryProcessMemory()` samples `GetProcessMemoryInfo`, and
   if `pointerSize == 4` AND `privateUsage ≥ 3 GB` the handler
   returns `{"error": "Game process at X.X GB private memory —
   refusing snapshot..."}` before touching the heap walker.
   Threshold = 4 GB ceiling − ~1 GB safety budget for our own
   JSON cache. Hard backstop: even if everything else fails,
   the DLL refuses to do the dangerous allocation.
2. **Auto-clamp defaults when `pointerSize == 4`** — handshake
   now carries `game_pointer_size`, `game_working_set`,
   `game_private_usage`.
   `MainWindowViewModel.ConnectAsync` calls
   `SnapshotViewModel.OnGameConnected(ps, ws, pu)` after every
   handshake, which clamps `InstanceCap` to ≤ 256 +
   `ArrayCap` to ≤ 64 on 32-bit targets if currently above.
   Never clamps upward — manual user picks stay respected.
3. **Game memory banner** in the Snapshot tab — visible
   post-handshake: `Game: 32-bit Mono — WorkingSet 1.42 GB,
   committed 1.78 GB — snapshot caps auto-clamped for safety`.
   Suffix escalates: `⚠ approaching 4 GB ceiling` at 2 GB,
   `⛔ over 3 GB — DLL will refuse next snapshot` at 3 GB.
4. **Status enrichment** — streaming + final snapshot status
   lines show game-process memory alongside UI memory so the
   user can watch the game grow during capture.

Option not taken (deferred — gate makes it unnecessary unless
a user explicitly needs > 256/64 caps on x86):
**Streaming-chunk architecture** — build chunks lazily
server-side rather than caching the entire payload upfront.
Bigger refactor; revisit only if a user reports the DLL
refusal blocking a legitimate use case.

**Detection hint**: when `Snapshot.Take failed: server closed
pipe before responding` appears in `ui/view/current.log` AND the
DLL's `scan/current.log` shows a clean `snapshotBegin` return
just before, AND the game has WOW64 / 32-bit pointers, this
is the OOM-on-chunk-drain pattern. Lower the caps and re-run.

---

### Phase 29c — `SQLitePCLRaw.bundle_e_sqlite3` needs `Batteries_V2.Init()` early in `Program.Main`  ✅ **Wired 2026-05-29 (UI build #680)**

**Symptom (during development)**: every `SqliteConnection.Open` throws
`Unable to load DLL 'e_sqlite3' or one of its dependencies` /
`InvalidOperationException: You need to call SQLitePCL.raw.SetProvider().`
even though the NuGet packages are referenced correctly.

**Root cause**: `Microsoft.Data.Sqlite.Core` is provider-agnostic by
design — it defers SQLite-native binding to whichever
`SQLitePCLRaw.bundle_*` package the app pulls in. The provider isn't
auto-registered just by referencing the bundle — there must be an
explicit `SQLitePCL.Batteries_V2.Init()` call somewhere on the
startup path **before any `SqliteConnection` is constructed**.

The trap: in a test process running individual `[Fact]`s the call
can sit in each test's constructor. In the production UI we need it
exactly once, and it must precede `SnapshotViewModel.OnGameDbContextAsync`
(which fires on handshake, very early).

**Fix**: a single `SQLitePCL.Batteries_V2.Init();` in
[Program.Main](../ui/UnityDumpUI/Program.cs) immediately after the
`SingleInstanceMutex` setup. Idempotent — safe to call again from
tests.

**AOT note**: `bundle_e_sqlite3` static-links the SQLite C source so
there's no runtime DLL resolution to fail at publish time. The `Init`
call IS still required because the .NET-side provider hookup is
explicit even when the native is statically linked.

---

### Phase 29c — atomic JSON writes for `settings.json` need `.tmp` + `File.Move(overwrite: true)`, not `Set-Content`  ✅ **Shipped 2026-05-29**

**Symptom**: if `JsonSerializer.SerializeAsync` is called directly
against the live `settings.json` path and the process is killed
mid-write (or the disk fills mid-write), the on-disk file is
truncated / half-formed JSON. Next launch reads it, sees
`JsonException`, falls back to defaults — **silently losing the
user's preferences**.

**Fix**: [UserSettingsStore.SaveAsync](../ui/UnityDumpUI.Core/Services/UserSettingsStore.cs)
writes to `settings.json.tmp` first, flushes, then calls
`File.Move(tmp, target, overwrite: true)`. On NTFS, same-volume
`Move` with `overwrite: true` is atomic at the directory-entry
level — either the new contents are visible or the old contents
are, never half-and-half. Same pattern git uses for its index.

**Defensive catch in `LoadAsync`**: corrupt-file path still exists
even with atomic writes (disk corruption, network share quirks, the
user editing the file manually). Catch `JsonException` +
`IOException` + `UnauthorizedAccessException`, log with a recovery
hint ("delete the file to reset to defaults"), return a fresh
`UserSettings()`. Never throw out of Load.

---

### Phase 29c — `Microsoft.Data.Sqlite` schema: per-class gzipped JSON blobs beat full normalization for snapshot row counts  ✅ **Shipped 2026-05-29**

**Symptom (designing the schema)**: fully normalized snapshot
schema (one row per field-value across all snapshots) means
~1M INSERT/snapshot on a wide 活俠傳 capture. Even batched inside
one transaction, this is 8-15s per save and the resulting DB grows
unboundedly past the prune threshold within 3-4 snapshots.

**Fix**: hybrid schema — `snapshots` table holds metadata (one row
per snapshot, 17 columns) + `snapshot_classes` table holds one row
per `SnapshotClassEntry` with the entry's fields/instances/arrays as
a **gzipped JSON blob** in a `BLOB` column. Reuses the existing
source-gen `ProtocolJsonContext.SnapshotClassEntry` serializer →
zero new mapping code, AOT-safe automatically.

Wide snap: ~3,647 classes → 3,647 INSERTs per save (sub-second).
Gzip cuts payload ~50× over raw JSON (verified live — 100 MB
in-memory snapshot lands at <2 MB on disk because class FQN /
field name / type strings repeat heavily within a snap).

**Cost**: can't `WHERE field_name = 'x'` directly with SQL — every
query that filters fields has to decompress blobs. Acceptable for
the v1 workload (diff intersects N snaps anyway, decompresses each
once); future SQL-side analytics would need a materialized view or
schema v2.

**Default prune threshold lowered from 1 GB to 200 MB on the same
day**: with ~50× compression, 200 MB DB holds 100+ wide snapshots,
which is already overkill for the practical "two-or-three snaps
per session" workflow.

---

### Phase 29c-eviction — `SnapshotEnvelope.Unload()` MUST replace the FieldIndex dictionary, not just clear it  ✅ **Wired 2026-05-29**

**Symptom**: clearing `FieldIndex` in place via
`FieldIndex.Clear()` releases the value entries but **NOT the
dictionary's bucket array** — for a 1M-key dictionary that's
already grown its capacity, the bucket array hangs around at
~8 MB per envelope. Eviction "works" but memory only drops
fractionally.

**Fix**: `Unload()` assigns `FieldIndex = new Dictionary<...>()`,
making the entire old dictionary (entries + buckets +
auxiliary arrays) eligible for GC. Combined with `Classes.Clear()`
which drops the list of `SnapshotClassEntry` (and transitively the
`SnapshotFieldEntry` JsonElement objects), this returns the
envelope to roughly the same RAM cost as a freshly-loaded
metadata-only stub.

Verify locally: take 5 wide snapshots, run diff or SPC, watch
`Process.WorkingSet64` in Task Manager — should drop hundreds of
MB after the post-Diff `EvictUnusedEnvelopes` fires.

**Side note**: same pattern applies to any cache where the
underlying collection's capacity vs count divergence is large.
`Dictionary` / `List<T>` / `HashSet` all have this "Clear keeps
capacity" semantic by design (so re-use is cheap); but if the
goal is releasing memory, reassign.

---

### Phase 29c — .NET 10 SDK + `Microsoft.Testing.Platform.MSBuild` 2.2.3 explicit pin breaks `dotnet test` two ways  ✅ **Fixed 2026-05-29**

**Symptom**: after a routine NuGet bump that pulled
`Microsoft.Testing.Extensions.Telemetry` /
`Microsoft.Testing.Extensions.TrxReport.Abstractions` /
`Microsoft.Testing.Platform.MSBuild` to version 2.2.3, `dotnet test`
fails with:

```
Microsoft.Testing.Platform.MSBuild.targets(263,5): error :
  Testing with VSTest target is no longer supported by
  Microsoft.Testing.Platform on .NET 10 SDK and later. If you use
  dotnet test, you should opt-in to the new dotnet test experience.
```

And even after opting in via
`<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>`,
the runner throws `MissingMethodException` on
`IOutputDevice.DisplayAsync` because xunit.v3 3.2.2 was built against
an older `Microsoft.Testing.Platform` API surface.

**Two issues, one fix**:

1. `Microsoft.Testing.Platform.MSBuild` 2.2.3 actively refuses the
   classic VSTest target on .NET 10 SDK.
2. `Microsoft.Testing.Platform` 2.2.3's `IOutputDevice` changed
   `DisplayAsync` signature; xunit.v3 3.2.2's bundled runner calls the
   old shape.

**Fix**: don't pin any `Microsoft.Testing.*` package version
explicitly — let `xunit.v3` transitively select a compatible
`Microsoft.Testing.Platform` version. Keep the
`UseMicrosoftTestingPlatformRunner` + `TestingPlatformDotnetTestSupport`
properties (required by SDK 10 regardless).

```xml
<PropertyGroup>
  <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="xunit.v3" Version="3.*" />
  <PackageReference Include="xunit.runner.visualstudio" Version="3.*" />
  <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.*" />
  <!-- NO explicit Microsoft.Testing.* refs -->
</ItemGroup>
```

`dotnet test` then drives the MTP runner directly through xunit.v3's
auto-generated `XunitAutoGeneratedEntryPoint.Main`. Test output looks
slightly different (one-line `成功! - 失敗: 0，通過: N` vs the
legacy multi-line VSTest summary) but the wall-clock behaviour is
identical.

> **UPDATE 2026-06-11 — same regression, now resolved via MTP *mode* (not the VSTest bridge).**
> A later package bump re-pinned the v2 MTP packages (2.2.3) AND the
> SDK moved to **10.0.301**. On that SDK, `Microsoft.Testing.Platform.MSBuild`
> 2.2.3 hard-refuses the VSTest target, so the Phase 29c stopgap of "keep
> `TestingPlatformDotnetTestSupport=true` and stay in VSTest-bridge mode" no
> longer works — it just re-triggers the `targets(263,5)` error. The durable
> fix is to opt into the **new `dotnet test` MTP mode** instead of the legacy
> bridge:
>
> 1. `ui/global.json` → add a `test` section:
>    ```json
>    { "sdk": { ... }, "test": { "runner": "Microsoft.Testing.Platform" } }
>    ```
> 2. `*.Tests.csproj` → **remove** `TestingPlatformDotnetTestSupport` (no longer
>    required in MTP mode); keep `UseMicrosoftTestingPlatformRunner=true`.
> 3. Still **do not pin any `Microsoft.Testing.*` version** — xunit.v3 3.2.2 is
>    built against the MTP **v1** API (`xunit.v3.core.mtp-v1` / `.mtp-v1`), so it
>    must run on the 1.x runtime. Pinning the 2.2.3 packages forces
>    `Microsoft.Testing.Platform` to 2.2.3 and the v1 runner dies with
>    `MissingMethodException` on `IOutputDevice.DisplayAsync`. With the pins
>    gone, the transitive MTP resolves to **1.9.1** (≥ 1.7.0, which MTP mode
>    requires). Also drop the Azure / OpenTelemetry / Identity packages the v2
>    `Microsoft.Testing.Extensions.Telemetry` dragged in — the test code uses
>    none of them.
>
> **Gotcha — MTP mode is cwd-sensitive.** `dotnet test` reads the `test.runner`
> setting from the `global.json` found by walking *up* from the working
> directory. Our `global.json` lives in `ui/`, so the runner must be invoked
> from `ui/` (or a descendant). From the repo root there is no `global.json` on
> the upward path, so `dotnet test` silently falls back to the VSTest target and
> the 2.2.3 error returns. `build.ps1` `Run-Tests` now `Push-Location`s into
> `ui/` and calls `dotnet test --project <proj> -c Release -v minimal`. Note the
> flag changes for MTP mode: `--nologo` is a VSTest-only flag and is rejected
> (exit 5), and the project is passed via `--project`, not positionally.
>
> **Regression guard.** Because this has now bitten twice via routine NuGet
> bumps, `UnityDumpUI.Tests.csproj` carries a `_FailOnExplicitMtpPin` MSBuild
> target that hard-fails the build (batched over `@(PackageReference)`) if any
> `Microsoft.Testing.*` package is pinned explicitly. A comment can't stop an
> "update all"; this does — and it surfaces the fix message at build time, long
> before a test run. Remove the target only when xunit.v3 ships an mtp-v2
> integration (at which point pinning 2.2.x becomes correct).

---

### Phase 29d-2 — `CommunityToolkit.Mvvm` `[RelayCommand]` strips `Async` suffix when naming the generated command  ✅ **Surfaced 2026-05-29**

**Symptom**: XAML binding `Command="{Binding SpcQuery.SaveSnapshotAsyncCommand}"`
fails with `AVLN2000: Unable to resolve suitable regular or attached
property SaveSnapshotAsyncCommand on type SpcQueryViewModel`.

**Why**: `[RelayCommand]` source generator looks at the C# method
name, strips `Async` if present, and appends `Command`. So:

| C# method name | Generated property |
|---|---|
| `Save()` | `SaveCommand` |
| `SaveAsync()` | `SaveCommand` (NOT `SaveAsyncCommand`) |
| `RefreshSpcAsync()` | `RefreshSpcCommand` |

This is documented but easy to miss when porting binding code from
older projects that used `Async` explicitly in the property name.

**Fix**: drop the `Async` suffix in the XAML binding name. Same
applies to event names, message tags, etc. — anywhere the source-gen
contract surfaces the public command identifier.

---

### Phase 29d-2 — Avalonia 12 `DataGrid` does NOT expose WPF's `CanUserAddRows` / `CanUserDeleteRows`  ✅ **Surfaced 2026-05-29**

**Symptom**: copy-pasting WPF DataGrid XAML throws
`AVLN2000: Unable to resolve suitable regular or attached property
CanUserAddRows on type Avalonia.Controls.DataGrid`.

**Why**: Avalonia's DataGrid intentionally omits the WPF
"new-row footer" / "Delete-key handler" properties because they
require row-CRUD plumbing Avalonia doesn't ship by default. Read-only
behavior is achieved through `IsReadOnly="True"` (covers both add
and delete + edit). For per-column edit control there's no need
to disable add/delete separately.

**Fix**: drop the two attributes. Use `IsReadOnly="True"` for fully
read-only grids; for editable grids (like SPC's per-snapshot
predicate row), leave `IsReadOnly="False"` and bind individual cells
to mutable VM properties.

---

### Phase 29d — SPC engine anchor selection: walk the **smallest** FieldIndex, not the first  ✅ **Designed 2026-05-29**

**Problem**: the engine's intersection over N snapshots' FieldIndex
sets is N×K dictionary `TryGetValue` calls where K is the anchor
size. Picking the wrong anchor multiplies query latency.

**Example**: 4 snapshots with FieldIndex sizes 1,200,000 / 12,000 /
1,150,000 / 1,180,000. Anchoring on snap[0] = 1.2M keys means 1.2M
× 3 lookups = 3.6M Dictionary probes. Anchoring on snap[1] = 12K
keys means 12K × 3 lookups = 36K probes. **100× speedup from a
two-line min-size pick.**

When does this matter? Any time the user mixes wide and narrow
snapshots (typical: 3 wide game snaps + 1 quick boot snap to anchor
"the field already existed pre-game-start").

**Implementation**: a single O(N) pass over the snapshot list at
`Run()` entry to pick the anchor index, then the rest of the engine
treats it identically to the canonical first snapshot. See
[SpcQueryEngine.Run](../ui/UnityDumpUI/ViewModels/SpcQueryEngine.cs).

---

### Phase 29d-polish — Avalonia 12 DataGrid built-in column types need a double-click before they accept input ✅ **Surfaced 3× on the SPC Query tab, 2026-05-29**

**Symptom**: user clicks an "Operand" cell on the SPC predicate
DataGrid, types a value, nothing happens. Same with the "Use"
checkbox column — first click selects the row, second click finally
toggles. Users reasonably conclude the cells are read-only — and
the Run button stays disabled because predicates are missing
operands, compounding the confusion.

**Root cause**: Avalonia 12's `DataGridTextColumn` /
`DataGridCheckBoxColumn` / `DataGridComboBoxColumn` are
"edit-on-double-click" by design. First click selects the row,
second click enters edit mode, only the third interaction
actually mutates the cell. This matches WPF behaviour but is
surprising in a 2026 UI where Excel / Google Sheets / VS Code etc.
accept single-click-edit.

**Fix**: replace the built-in single-purpose columns with
`DataGridTemplateColumn` + a live control:

```xml
<!-- DON'T — needs double-click to enter edit mode -->
<DataGridTextColumn Header="Operand"
                    Binding="{Binding OperandText, Mode=TwoWay}" />

<!-- DO — single click → focus → type -->
<DataGridTemplateColumn Header="Operand">
  <DataGridTemplateColumn.CellTemplate>
    <DataTemplate>
      <TextBox Text="{Binding OperandText, Mode=TwoWay}"
               Margin="2" VerticalAlignment="Center" />
    </DataTemplate>
  </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

Same pattern works for `CheckBox` and `ComboBox`. **Rule**: if the
user mutates a cell on a single click, use `DataGridTemplateColumn`
and put a live control inside. The single-purpose `DataGrid*Column`
types are only ergonomic for infrequent edits where the
row-select-first interaction is fine. Hit this three times in one
session (Operand cell, Operand 2 cell, Use checkbox) before adding
the rule.

---

### Phase 29d-polish — `RelayCommand` CanExecute doesn't re-fire on external collection changes ✅ **Surfaced 2026-05-29**

**Symptom**: SPC Query "Run" button stayed grayed even after 4
snapshots were taken (and visible in the picker grid). `CanRun`
returned `true` when inspected manually; the button just never
re-evaluated.

**Root cause**: `CommunityToolkit.Mvvm`'s `[RelayCommand]` source
generator hooks `CanExecuteChanged` to the VM's own
`PropertyChanged` events — but only for `[ObservableProperty]`-
decorated fields on the same VM. Our `CanRun` reads
`_snapshot.Snapshots.Count`, which is an external
`ObservableCollection` not surfaced as an `[ObservableProperty]`
here. When that collection mutates, the source-gen has no hook to
know we need to re-evaluate.

**Fix**: subscribe to the external collection's
`CollectionChanged` event and call the generated
`<Name>Command.NotifyCanExecuteChanged()` method explicitly:

```csharp
_snapshot.Snapshots.CollectionChanged += (_, _) =>
{
    SyncSelectionRows();
    RunCommand.NotifyCanExecuteChanged();   // ← without this the
                                            //   button stays stale
};
```

Same pattern applies whenever a command's CanExecute depends on
state that isn't a local `[ObservableProperty]`. The source-gen
DOES generate the `NotifyCanExecuteChanged()` method automatically
— you just have to call it.

---

### Phase 29d-polish — empty Operand on a value-required predicate silently returns 0 matches ✅ **Surfaced + caught with preflight 2026-05-29**

**Symptom**: user picked `Exact` on a SPC predicate row but didn't
type a value in the Operand cell. Run produced `0 matches`,
indistinguishable from "no field met the criteria". User wasted
~5 minutes assuming the engine was working but their predicates
were wrong.

**Root cause**: `SpcSnapshotRow.BuildPredicate()` parses the
Operand text via `double.TryParse` — empty / unparseable → null.
The engine's `Evaluate(Exact)` short-circuits when `pred.Operand`
is null: returns false for every row. Same path for `NotEqual`,
`DeltaExact`, and `Range`/`DeltaRange` with both bounds empty.

**Fix**: preflight validation in `RunAsync` BEFORE calling the
engine. For each selected row, check the predicate kind's
operand requirements:

| Predicate | Operand 1 | Operand 2 |
|---|---|---|
| Any / Unchanged / Increased / Decreased | not needed | not needed |
| Exact / NotEqual / DeltaExact | **required** | not used |
| Range / DeltaRange | at least one of | the two must be set |

If validation fails, surface a row-specific error message naming
the index + kind so the user knows exactly which cell to fix.
**Rule**: any "user-facing predicate language with optional
operands" needs explicit empty-vs-meaningful-zero validation;
silent fail-to-zero-results is the worst UX outcome.

## Unity DOTS / ECS targets — gameplay data is NOT in managed objects

**Symptom (Core Keeper, Mono x64, 2026-06-08)**: a full snapshot /
`findInstances` over the managed heap returns almost nothing useful —
16 in-scope classes with 0–28 live instances even though
`FindAllInstances` reports **293 827** managed instances exist. In CE
the same values show up as **40–60 identical copies** that have to be
narrowed by binary-search, and "find what writes" always lands inside
a module named **`lib_burst_generated_xxxxxxxxxxxxxxxx`** at
`[reg+offset]`-style instructions with no static address.

**Root cause — the game is built on Unity DOTS** (Data-Oriented
Technology Stack). The data model is completely unlike MonoBehaviour
games:

| | Classic Unity | DOTS / ECS |
|---|---|---|
| Game object | a C# **object** (MonoObject, has a vtable) | an **Entity = just an ID** (index+version), no data |
| Where data lives | the object's fields | a **Component** (`IComponentData`, a plain blittable struct) packed into a **16 KB chunk** |
| Logic | the object's methods | a **System** running over all entities of a matching archetype |

Two consequences that explain every symptom above:

1. **Component data is unmanaged.** Components live in chunk memory
   (chunk allocator → VirtualAlloc/native heap), NOT as MonoObjects.
   Our snapshot / `findInstances` / `walkInstance` find instances by
   matching a **MonoVTable\*** at object+0 — ECS chunk data has no
   vtable, so the managed-instance scan **cannot see it**. The only
   ECS-related things on the managed heap are `World` / `EntityManager`
   / System objects and **entity handles** (e.g. the
   `<entity>k__BackingField (Unity.Entities.Entity)` field on
   `Pug.Other.PlayerController` is just the id, not the data).

2. **Chunks are SoA (Structure of Arrays).** All entities of one
   archetype store the same component contiguously — every `Health` in
   one array, every `Position` in the next, NOT interleaved per-entity.
   So 50 same-archetype entities → a contiguous run of 50 `Health`
   values; identical defaults → CE finds 40–60 duplicates and the
   player's value is one indistinguishable slot (hence binary-search).
   Chunks also **move** on structural changes (entity create/destroy,
   component add/remove) → a raw address is not stable across frames.

**`lib_burst_generated_*` is CODE, not data.** Burst AOT-compiles the
`[BurstCompile]` jobs/systems (C# → LLVM → native SIMD) into that
module. The component **data** is in chunk heap; the **instructions
that read/write it** are the Burst output, so CE's "what accesses
this" points there. Those instructions take the chunk pointer in a
register computed at runtime → no static address to lock. (Burst is
independent of the scripting backend — it exists even on this Mono
target.)

**Is it solvable?** Not fundamentally unsolvable, in three tiers:

- **Our tool (managed-instance path): no — for component data.** We can
  surface the managed `EntityManager` / Systems and entity handles, but
  not decode chunk contents.
- **CE manually: yes, but painful.** (a) narrow with a 2-criteria scan
  (filter by a value unique to the player's entity first, then the
  target value) instead of value-alone; (b) don't lock addresses —
  **AOB-hook the Burst system** in `lib_burst_generated` and read/modify
  the component as the system iterates the player's slot each frame.
  This is the standard DOTS cheat approach.
- **Future "ECS-aware walker" (PARKED — C/P not worth it unless needed,
  user call 2026-06-08).** Because Core Keeper is **Mono** (not IL2CPP),
  `EntityManager` / `EntityComponentStore` ARE reachable managed
  objects, so in principle:
  `World → EntityManager → EntityComponentStore (→ unmanaged) → resolve
  Entity{index,version} to archetype+chunk+index → chunk base +
  component offset + index*stride → value address`. Doable and a real
  differentiator, but needs reverse-engineering the specific
  `Unity.Entities` version's internal `EntityComponentStore` /
  `Archetype` / `Chunk` struct ABI, which is **undocumented and breaks
  on game updates**. ~2–4 days reverse + a new backend module. Spec was
  not written; revisit only if a DOTS target becomes a priority.

**Rule**: before sinking time into managed-instance dumping on a Unity
target, check for DOTS. Tells: a `lib_burst_generated_*` module loaded;
`Unity.Entities` / `Unity.Mathematics` / `Unity.Collections` assemblies;
`Unity.Entities.Entity` handle fields on game classes; CE value scans
returning many identical duplicates. If DOTS, set expectations up front
— the managed snapshot only covers managers/singletons; component data
needs CE (multi-criteria scan + Burst AOB-hook) or a future ECS walker.
