-- job6 = P1 again, this time with the bridge's own diagnostics captured.
-- unity_dump.lua has no inject() - it runs main() at load via U.guard(main) -
-- and its progress banners go through U.log, which only prints when
-- DISCRETE_DEBUG is on. So: turn debug on (which also stops it from closing the
-- Lua Engine), mirror print() into the file log, then assert on callDLL.
DISCRETE_DEBUG = 1
package.path = [[D:\Github\discrete\scripts\?.lua;]] .. package.path

local realprint = print
print = function(...)
    local n = select('#', ...)
    local parts = {}
    for i = 1, n do parts[#parts + 1] = tostring((select(i, ...))) end
    DLOG("  [ce] %s", table.concat(parts, " "))
    return realprint(...)
end

DLOG("=== job6: P1 with bridge diagnostics ===")
DLOG("  attached pid=%s  targetIs64Bit=%s", tostring(getOpenedProcessID()),
     tostring(targetIs64Bit()))

package.loaded["utils"] = nil
local U = require("utils")
DLOG("  U.detectBackend() = %s", tostring(U.detectBackend()))
DLOG("  U.dllPath('Mono') = %s", tostring(U.dllPath("Mono")))
local p = U.dllPath("Mono")
local fh = io.open(p, "rb")
DLOG("  that path exists = %s", tostring(fh ~= nil))
if fh then fh:close() end

DLOG("  --- running unity_dump.lua (this is the inject step) ---")
local okDump, errDump = pcall(dofile, [[D:\Github\discrete\scripts\unity_dump.lua]])
DLOG("  dofile -> ok=%s err=%s", tostring(okDump), tostring(errDump or ""))

-- Did the module actually land?
local mapped = nil
if enumModules then
    for _, m in ipairs(enumModules() or {}) do
        local nm = (m.Name or m.name or ""):lower()
        if nm:find("discrete") then mapped = (m.Name or m.name) break end
    end
end
DLOG("  Discrete module mapped in target: %s", tostring(mapped))

local t1 = getTickCount()
local okCall, val = pcall(U.callDLL, "Discrete_GetBuildNumber", "uint32")
DLOG("  callDLL(Discrete_GetBuildNumber) -> ok=%s value=%s (%d ms)",
     tostring(okCall), tostring(val), getTickCount() - t1)
DLOG("  P1 verdict: %s", (okCall and tonumber(val) == 1408) and "PASS (build 1408)"
     or (okCall and ("value " .. tostring(val)) or "FAIL"))

local f = io.open([[\\.\pipe\DiscreteBfx]], "r+b")
if f then
    f:write('{"id":1,"cmd":"handshake"}\n') f:flush()
    local resp = f:read("*l") f:close()
    DLOG("  pipe handshake: %s", tostring(resp and resp:sub(1, 200)))
else
    DLOG("  pipe not open")
end

print = realprint
DLOG("=== job6 done ===")
