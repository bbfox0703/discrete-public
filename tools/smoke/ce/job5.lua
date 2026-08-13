-- job5 = G2 P1: `U.callDLL` must hit the right address.
--
-- CE's signature is executeCodeEx(callmethod, timeout, address, ...). The bug
-- passed the export address SECOND, so CE read it as a timeout and jumped to
-- whatever the first vararg was (nil = address 0 when there are no args).
--
-- `Discrete_GetBuildNumber` makes this exactly checkable: it returns a value we
-- already know from the pipe handshake. A wrong-address call cannot return it.
local GAME = "CoreKeeper.exe"
local EXPECT_BUILD = tonumber(arg and arg[1]) or 1408

DLOG("=== job5: P1 U.callDLL via the bridge ===")

local pid = getOpenedProcessID()
if pid == 0 then
    local ok = pcall(openProcess, GAME)
    pid = getOpenedProcessID()
    DLOG("  openProcess(%s) -> ok=%s pid=%s", GAME, tostring(ok), tostring(pid))
end
if pid == 0 then
    DLOG("  FAIL: could not attach to %s", GAME)
    return
end
DLOG("  attached pid=%s targetIs64Bit=%s", tostring(pid), tostring(targetIs64Bit()))

package.path = [[D:\Github\discrete\scripts\?.lua;]] .. package.path
local okLoad, errLoad = pcall(dofile, [[D:\Github\discrete\scripts\unity_dump.lua]])
DLOG("  dofile unity_dump.lua -> ok=%s err=%s", tostring(okLoad), tostring(errLoad or ""))

local t0 = getTickCount()
local okInj, errInj = pcall(inject)
DLOG("  inject() -> ok=%s err=%s  (%d ms)", tostring(okInj), tostring(errInj or ""),
     getTickCount() - t0)

-- Now the actual P1 assertion. utils is what unity_dump loaded; re-require is
-- cheap and gives us callDLL directly.
local U = require("utils")

local t1 = getTickCount()
local okCall, buildOrErr = pcall(U.callDLL, "Discrete_GetBuildNumber", "uint32")
local dt = getTickCount() - t1
DLOG("  callDLL(Discrete_GetBuildNumber,uint32) -> ok=%s value/err=%s  (%d ms)",
     tostring(okCall), tostring(buildOrErr), dt)
DLOG("  expected build = %d", EXPECT_BUILD)
DLOG("  P1 verdict: %s",
     (okCall and tonumber(buildOrErr) == EXPECT_BUILD) and "PASS"
     or (okCall and ("MISMATCH (got " .. tostring(buildOrErr) .. ")") or "FAIL/ERROR"))

-- StartPipe goes through the same call path and returns an int32.
local okSp, spRet = pcall(U.callDLL, "Discrete_StartPipe", "int32")
DLOG("  callDLL(Discrete_StartPipe,int32) -> ok=%s ret=%s (0 = ok, pipe already up is fine)",
     tostring(okSp), tostring(spRet))

-- And prove the pipe itself answers through the Lua bridge, not just the export.
local f = io.open([[\\.\pipe\DiscreteBfx]], "r+b")
if f then
    f:write('{"id":1,"cmd":"handshake"}\n')
    f:flush()
    local resp = f:read("*l")
    f:close()
    DLOG("  handshake via pipe: %s", tostring(resp and resp:sub(1, 220) or "nil"))
    DLOG("  pipe RPC after bridge inject: %s", (resp and resp:find('"backend"')) and "PASS" or "FAIL")
else
    DLOG("  pipe RPC after bridge inject: FAIL (cannot open pipe)")
end
DLOG("=== job5 done ===")
