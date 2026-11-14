# tools/smoke — headless live-smoke harness

Drives the injected DLL over `\\.\pipe\DiscreteBfx` directly, so the live-smoke
items in [../../docs/audit-2026-08-13-smoke.md](../../docs/audit-2026-08-13-smoke.md)
can be run **without DiscreteUI and without Cheat Engine**. Python 3 only (stdlib
+ ctypes); no PowerShell (AV heuristics flag it on this dev box).

Why it exists: the checklist items are mostly *silent* defects (wrong field name,
half an array, a wrapped integer). Eyeballing the UI cannot tell a correct result
from a plausible-looking wrong one, but a script that cross-checks the DLL's
answer against `ReadProcessMemory` can.

## Getting a target up

No injection step needed if the game has the proxy installed — `DllMain`
auto-runs `Discrete_Init` + `Discrete_StartPipe`
([dll/src/main.cpp](../../dll/src/main.cpp)), so launching the game is enough:

```bash
cp dist/version.dll dist/Discrete-Mono.dll dist/Discrete-IL2CPP.dll "<game folder>/"
"C:/Program Files (x86)/Steam/steam.exe" -applaunch <appid>
python tools/smoke/probe.py <exe-name-substring>
```

`dh.inject(pid, dll)` is there for targets without a proxy (x64 LoadLibraryW
injection); 32-bit targets need `dist/x86/inject-x86.exe <pid> <dll>` instead.

**Always check `handshake.build`** before trusting a result — `dist/` can lag the
working tree, and a stale DLL silently tests old code.

## Scripts

| file | what it checks |
|---|---|
| `dh.py` | the harness: pipe RPC client, process CPU/RSS samplers, `VirtualQueryEx`/`ReadProcessMemory`, LoadLibraryW injection, PASS/FAIL reporter |
| `probe.py` | handshake + assembly count + memory; run this first |
| `discover.py` | finds a class with live instances and int32 fields, writes `target.json` for the other scripts |
| `g1a2.py` | **P2** (cancel a value scan, then `scanFieldValue` must still be complete) and **I-2/I-3** (peak private bytes during the whole-heap walk). Run it twice — once per DLL build — for a before/after |
| `g1b2.py` + `scan_child.py` | **E-4/E-5**: force-kill the client mid-scan; CPU must fall back to baseline within seconds instead of at the scan's natural end |
| `r2_live.py` | **Rust core R1 + R2 live A/B**: `run` records restart-stable results (metadata RPCs, method AOBs, static-field xrefs) plus PASS/FAIL value/group-scan refine checks (writes two int32 fields of one object, restores them); run once per DLL build, then `compare a.json b.json`. Warm both games the same way first — IL2CPP class-pointer slots fill lazily. Turns on `setKeepForeground` for the run (unfocused games pause) and confirms field ownership via `scanFieldValue` before writing. Pass the game's main assembly as the 4th arg when `Assembly-CSharp` is thin (活俠傳: `Mortal.Core`). See [rust-core.md → Live A/B](../../docs/rust-core.md#live-ab-r1--r2) |
| `h1b.py` | **H-1**: every `getMethodAobPattern` result must match the bytes at its own method address (a desynced decoder puts wildcards on the wrong bytes), reported separately for windows containing an F6/F7 opcode |
| `g3.py`, `g3b.py` | **D-1 / D-2** on 32-bit Mono. Incomplete: see the caveat in the smoke doc — `g3.py` must skip **value-type** classes (their metadata offsets are not addressable from an instance base) and needs a hand-picked, populated `IntPtr[]` |
| `b1_proxy_exports.py` | **B-1** with no game running: parses every installed Unity game's `UnityPlayer.dll` imports and diffs them against our proxy's export table |
| `Program.cs` + `ProxySmoke.csproj` | **B-4 / B-4b**: drives the real `WindowsProxyInstaller` against synthetic game folders (a third-party `version.dll`, our own, and a foreign DLL in the other slot). Never touches a real game install. `dotnet build` then run the exe — not `dotnet run`, because the installer resolves its source dir from `Environment.ProcessPath` |

## Gotchas found the hard way

- `scanFieldValue` owns **no** `ScanCancel::Scope`, so it is not cancellable and
  ignores client disconnect. Use `beginValueScan` when testing cancellation.
- Response keys are not guessable: value-scan candidates use `addr` (not
  `address`), and `findMethodInstances` returns `matches` (not `overloads`).
- Field offsets from `getClassInfo` **include** the object header, so
  `instanceBase + offset` is the field address — but only for reference types.
- Git Bash mangles `taskkill /IM`; terminate via `dh` + `TerminateProcess`.
- Run Python with `-u` when backgrounding, or buffered output is lost on kill.

## CE-side items (G2 / G5 / G3-P3)

These need one manual step, because CE only auto-runs Lua from its own install
directory and a `.CT`'s table script prompts on load. In Cheat Engine open
**Table → Show Cheat Table Lua Script** (Ctrl+Alt+L), paste this one line and
press *Execute*:

```
dofile[[D:\Github\discrete\tools\smoke\ce\dispatcher.lua]]
```

That arms a resident dispatcher. From then on every CE test is driven from
outside CE: drop `job<N>.lua` into `tools/smoke/ce/` (N counting up from 0) and
read the appended `ce_log.txt`. `job0.lua` is an environment probe — it records
which CE Lua APIs this build really has (`is32bitProcess` must be nil; calling it
was the P3 bug) and exercises `DHEARTBEAT`, the timer whose tick timestamps prove
whether CE's GUI thread is still pumping. That heartbeat is how the "CE must stay
responsive" items get an objective PASS/FAIL instead of an eyeball judgement.
- Don't point this harness at a process while **CE's mono data collector is also
  attached** (i.e. after `LaunchMonoDataCollector()` / opening the game in CE).
  Two agents walking the same Mono runtime is how the 2026-08-13 x86 session
  ended: `getClassInfo` started answering `assembly not found` for an assembly
  `getAssemblies` had just listed, and the next `handshake` got no reply and the
  server closed the connection. No crash and nothing in the DLL logs — the game
  survived — but the runtime state was no longer trustworthy for a data check.
  Finish the headless items first, then hand the target to CE.
