--[[
  unity_dump.lua — Phase 0 stub.
  Loaded inside Cheat Engine. Final form (Phase 11):
    1. Locate target Unity process
    2. Inject UnityDumper-{Mono|IL2CPP}.dll
    3. Call exported StartPipe()
    4. Display banner with backend + handshake info

  For now this just prints a banner so we can verify CE script loading.
]]

local BANNER = "[UnityCEDumper] unity_dump.lua loaded (Phase 0 stub)"

if print then
  print(BANNER)
end

if showMessage then
  -- showMessage is a CE Lua API; guarded so this file can also be sanity-checked
  -- in plain Lua.
  showMessage(BANNER)
end
