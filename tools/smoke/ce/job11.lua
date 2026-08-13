-- job11 = G3 P3: for a 32-bit Mono target the bridge must select the x86 DLL.
--
-- The bug: the x86 branch asked `is32bitProcess()`, which does not exist on any
-- CE build (job0 confirmed it is nil here), and it was written as
-- `(fn and fn())` so it silently evaluated false - every 32-bit Mono game got
-- handed the x64 DLL. The fix uses targetIs64Bit(), which job0 confirmed exists.
-- targetIsX86() must NOT be used: it is the architecture family and is true for
-- x64 targets too (job0 showed it true with no process open at all).
package.path = [[D:\Github\discrete\scripts\?.lua;]] .. package.path
package.loaded["utils"] = nil

DLOG("=== job11: P3 x86 DLL selection ===")
local ok = pcall(openProcess, "Mortal.exe")
local pid = getOpenedProcessID()
DLOG("  openProcess(Mortal.exe) -> ok=%s pid=%s", tostring(ok), tostring(pid))
if pid == 0 then DLOG("  FAIL: not attached") return end

local U = require("utils")
DLOG("  targetIs64Bit() = %s   (false => 32-bit, so the x86 branch must fire)",
     tostring(targetIs64Bit()))
DLOG("  targetIsX86()   = %s   (family only - true for x64 as well)",
     tostring(rawget(_G, "targetIsX86") and targetIsX86()))
DLOG("  is32bitProcess  = %s   (the API the bug called)",
     tostring(rawget(_G, "is32bitProcess")))
DLOG("  U.detectBackend() = %s", tostring(U.detectBackend()))

local p = U.dllPath("Mono")
DLOG("  U.dllPath('Mono') = %s", tostring(p))
local picksX86 = tostring(p):lower():find("discrete%-mono%-x86%.dll") ~= nil
DLOG("  P3 verdict: %s", picksX86 and "PASS - selected the x86 DLL name"
     or "FAIL - selected " .. tostring(p))

-- Second half: prove the x86 build actually works on this target. Inject by
-- absolute path (dllPath's search dirs are a separate concern) and call an
-- export through executeCodeEx, same as the P1 check on x64.
local X86 = [[D:\Github\discrete\dist\x86\Discrete-Mono.dll]]
local mapped = nil
for _, m in ipairs(enumModules() or {}) do
    local nm = (m.Name or m.name or ""):lower()
    if nm:find("discrete") then mapped = (m.Name or m.name) break end
end
DLOG("  Discrete module already mapped (proxy): %s", tostring(mapped))
if not mapped then
    local r = injectDLL(X86)
    DLOG("  injectDLL(x86) -> %s", tostring(r))
    sleep(800)
end
if reinitializeSymbolhandler then pcall(reinitializeSymbolhandler) end
sleep(1500)
local okCall, val = pcall(U.callDLL, "Discrete_GetBuildNumber", "uint32")
DLOG("  callDLL(Discrete_GetBuildNumber) on 32-bit target -> ok=%s value=%s",
     tostring(okCall), tostring(val))
DLOG("  x86 export call verdict: %s",
     (okCall and tonumber(val) == 1409) and "PASS (build 1409 = the x86 build)"
     or (okCall and ("value " .. tostring(val)) or "FAIL"))
DLOG("=== job11 done ===")
