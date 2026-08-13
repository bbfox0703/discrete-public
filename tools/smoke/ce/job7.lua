-- job7: two things.
--
-- (1) NEW FINDING to nail down: unity_dump.lua treats `injectDLL` failure as
--     `ok == nil`, but celua.txt:581 says injectDLL "returns true on success",
--     i.e. it returns FALSE on failure. So a missing DLL sails past the check
--     and the bridge prints "DLL injected" + "Pipe ready" having done neither.
--     Print the exact value and type for a bogus path.
--
-- (2) P1 itself, which needs a DLL that really is mapped: inject by absolute
--     path, then call Discrete_GetBuildNumber through U.callDLL and compare
--     against the build we already know (1408). A wrong-address call - the bug
--     being tested - cannot return that number.
package.path = [[D:\Github\discrete\scripts\?.lua;]] .. package.path
package.loaded["utils"] = nil
local U = require("utils")
local DLL = [[D:\Github\discrete\dist\Discrete-Mono.dll]]

DLOG("=== job7: injectDLL contract + P1 ===")

-- (1) contract probe
local bogus = [[D:\Github\discrete\dist\NoSuchDiscreteDll.dll]]
local r1 = injectDLL(bogus)
DLOG("  injectDLL(<missing file>) -> value=%s type=%s   (unity_dump only errors on == nil)",
     tostring(r1), type(r1))
DLOG("  => 'ok == nil' check catches this: %s", tostring(r1 == nil))

-- (2) real inject
DLOG("  injecting %s", DLL)
local r2 = injectDLL(DLL)
DLOG("  injectDLL(<real file>) -> value=%s type=%s", tostring(r2), type(r2))
sleep(500)

local mapped = nil
for _, m in ipairs(enumModules() or {}) do
    local nm = (m.Name or m.name or ""):lower()
    if nm:find("discrete") then mapped = (m.Name or m.name) break end
end
DLOG("  module mapped now: %s", tostring(mapped))

if reinitializeSymbolhandler then pcall(reinitializeSymbolhandler) end
sleep(1500)

local t = getTickCount()
local ok, val = pcall(U.callDLL, "Discrete_GetBuildNumber", "uint32")
DLOG("  callDLL(Discrete_GetBuildNumber,uint32) -> ok=%s value=%s (%d ms)",
     tostring(ok), tostring(val), getTickCount() - t)
DLOG("  P1 verdict: %s",
     (ok and tonumber(val) == 1408) and "PASS - correct value via executeCodeEx(1, timeout, addr)"
     or (ok and ("WRONG VALUE " .. tostring(val)) or "FAIL: " .. tostring(val)))

local t2 = getTickCount()
local ok2, val2 = pcall(U.callDLL, "Discrete_StartPipe", "int32")
DLOG("  callDLL(Discrete_StartPipe,int32) -> ok=%s ret=%s (%d ms)",
     tostring(ok2), tostring(val2), getTickCount() - t2)

local f = io.open([[\\.\pipe\DiscreteBfx]], "r+b")
if f then
    f:write('{"id":1,"cmd":"handshake"}\n') f:flush()
    local resp = f:read("*l") f:close()
    DLOG("  pipe handshake: %s", tostring(resp and resp:sub(1, 200)))
    DLOG("  pipe works after bridge inject: %s",
         (resp and resp:find('"backend"')) and "PASS" or "FAIL")
else
    DLOG("  pipe works after bridge inject: FAIL (cannot open)")
end
DLOG("=== job7 done ===")
