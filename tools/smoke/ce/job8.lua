-- job8 = G2 "有限 timeout(不再凍結 CE)".
--
-- The target process is suspended (by the Python side) before this runs, so the
-- remote thread executeCodeEx starts can never finish. Pre-fix the timeout was
-- nil = INFINITE and the wait happens on CE's GUI thread with nothing pumping,
-- so Cheat Engine froze permanently. Post-fix the ceiling is
-- kDllCallTimeoutMs = 10000, so CE must come back with an error inside ~10 s.
--
-- ce_watchdog.py measures the block from outside: a bounded ~10 s block that
-- RECOVERS is the PASS shape; never recovering is the FAIL shape.
package.path = [[D:\Github\discrete\scripts\?.lua;]] .. package.path
package.loaded["utils"] = nil
local U = require("utils")

DLOG("=== job8: finite executeCodeEx timeout with the target suspended ===")
DLOG("  attached pid=%s", tostring(getOpenedProcessID()))

local t0 = getTickCount()
local ok, val = pcall(U.callDLL, "Discrete_GetBuildNumber", "uint32")
local dt = getTickCount() - t0
DLOG("  callDLL -> ok=%s value/err=%s", tostring(ok), tostring(val))
DLOG("  elapsed = %d ms  (ceiling is 10000 ms)", dt)
DLOG("  bounded by the 10 s cap: %s", tostring(dt < 13000))
DLOG("  came back at all (pre-fix: never): %s", "yes")
local e = tostring(val)
DLOG("  error names CE's own reason (Execution timeout / Failure launching thread): %s",
     tostring(e:find("timeout") ~= nil or e:find("Timeout") ~= nil
              or e:find("Failure") ~= nil))
DLOG("=== job8 done ===")
