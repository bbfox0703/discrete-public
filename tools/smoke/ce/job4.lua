-- job4 = G2 "retry loop 不再凍結 CE" + "錯誤原因".
-- No DLL injected on purpose: unity_symbols' rpc() must then walk its full
-- 15 x 200 ms pipe-open retry budget. The fix put pump() INSIDE that loop, so
-- CE's window has to keep repainting for those ~3 s. ce_watchdog.py measures
-- that from outside; this job just drives the path and records the wall clock
-- plus the exact error text (the "錯誤原因" item).
local HERE = [[D:\Github\discrete\tools\smoke\ce\]]
package.path = [[D:\Github\discrete\scripts\?.lua;]] .. package.path

DLOG("=== job4: pipe-open retry loop (no DLL injected) ===")
DLOG("  openedProcessID = %s (0 = no process, which is fine for this item)",
     tostring(getOpenedProcessID()))

local okLoad, errLoad = pcall(dofile, [[D:\Github\discrete\scripts\unity_symbols.lua]])
DLOG("  dofile unity_symbols.lua -> ok=%s err=%s", tostring(okLoad), tostring(errLoad or ""))

local t0 = getTickCount()
local ok, err = pcall(registerClassSymbols, "Assembly-CSharp", "", "NoSuchClassForSmoke")
local dt = getTickCount() - t0
DLOG("  registerClassSymbols -> ok=%s  elapsed=%d ms", tostring(ok), dt)
DLOG("  error text: %s", tostring(err))

-- The retry budget is 15 x 200 ms; anything in that ballpark means the loop ran
-- to completion rather than failing instantly for some unrelated reason.
DLOG("  spent the retry budget (>=2500 ms): %s", tostring(dt >= 2500))
local e = tostring(err)
DLOG("  message names the cause (pipe open / No such file): %s",
     tostring(e:find("pipe open") ~= nil or e:find("No such file") ~= nil
              or e:find("cannot find") ~= nil))
DLOG("=== job4 done ===")
