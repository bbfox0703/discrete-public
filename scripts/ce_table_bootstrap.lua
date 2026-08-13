--[[
ce_table_bootstrap.lua — Mode B deployment reference for CE-table publishers.

The "Mode B" pattern lets you ship a single .CT file to end users with
zero external dependencies — no DiscreteUI.exe, no manual DLL copy
into Cheat Engine's install dir, no proxy `version.dll` next to the
game. Everything (backend DLL + Lua bridges) is embedded inside the
.CT, extracted to %TEMP% on table-activate, then loaded.

================================================================
How to embed files in your .CT
================================================================

1. In Cheat Engine: Table menu → Add file… → pick the file from the
   dist/ folder of the discrete repo. Repeat for each file you need:

   Required:
     Discrete-Mono.dll      OR  Discrete-IL2CPP.dll
     (whichever backend your target game uses; embed BOTH if your .CT
      targets a multi-backend game like Farthest Frontier)

     utils.lua
     unity_dump.lua            (provides `inject()` — the entry point)

   If targeting 32-bit games:
     Discrete-Mono.dll      (x86 build from dist/x86/; embed under
                                a distinct table-file name like
                                "Discrete-Mono-x86.dll" to avoid
                                colliding with the x64 entry)

   Optional, based on which RPCs your table actually uses:
     unity_symbols.lua         (registersymbol bulk loader)
     unity_invoke.lua          (callMethod from Lua)
     mono_registersymbol_helpers.lua  (DLL-free Mono register path)

2. Copy this file's `bootstrap()` function into your .CT's autorun
   Lua script. Customise `FILES_TO_EXTRACT` for the files you
   embedded. Call `bootstrap()` from your [ENABLE] / activation
   script.

================================================================
What this script does on table-activate
================================================================

  1. Decide TEMP target dir = %TEMP%\Discrete_Dumper\<table-id>\
  2. Extract each embedded table file to that dir
  3. Detect the target process's backend (Mono / IL2CPP)
  4. Pick the matching backend DLL from the extracted files
  5. injectDLL() the backend DLL into the game
  6. Wait for the pipe to come up (~0.5 s)
  7. dofile() the lua bridges so `inject()`/`getName()`/etc. are
     globally available to subsequent AA-script Lua blocks

================================================================
]]--

-- Per-CT identifier so multiple Mode-B tables on the same machine
-- don't stomp on each other's extracted files. Change this PER TABLE
-- (use the game name or a uuid) so concurrent table opens stay isolated.
local TABLE_ID = "default-table"

-- Files this CT embeds. Edit to match what you actually embedded.
-- Keys are the table-file names (Table menu → Add file… → "Save as");
-- values describe what to do with each on extract.
--   kind = "dll-mono-x64"   → 64-bit Mono backend
--   kind = "dll-mono-x86"   → 32-bit Mono backend
--   kind = "dll-il2cpp-x64" → 64-bit IL2CPP backend
--   kind = "lua-main"       → Lua bridge with `inject()` (must be unity_dump.lua)
--   kind = "lua-utils"      → Shared utilities (utils.lua) — needed by every bridge
--   kind = "lua-helper"     → Other optional bridges (unity_invoke / unity_symbols / etc.)
local FILES_TO_EXTRACT = {
    -- ----- Backend DLLs (embed only the ones your target needs) ----
    -- ["Discrete-Mono.dll"]      = { kind = "dll-mono-x64"   },
    -- ["Discrete-IL2CPP.dll"]    = { kind = "dll-il2cpp-x64" },
    -- ["Discrete-Mono-x86.dll"]  = { kind = "dll-mono-x86"   },

    -- ----- Required Lua --------------------------------------------
    ["utils.lua"]      = { kind = "lua-utils" },
    ["unity_dump.lua"] = { kind = "lua-main"  },

    -- ----- Optional Lua (uncomment as needed) ----------------------
    -- ["unity_invoke.lua"]               = { kind = "lua-helper" },
    -- ["unity_symbols.lua"]              = { kind = "lua-helper" },
    -- ["mono_registersymbol_helpers.lua"] = { kind = "lua-helper" },
}

--------------------------------------------------------------------
-- Implementation. Edit only if you need behavioural changes; the
-- normal customisation surface is the FILES_TO_EXTRACT table above.
--------------------------------------------------------------------

-- Logging convention (docs/lua-logging.md): _msg() is progress output, gated
-- behind the DEBUG flag (global DISCRETE_DEBUG == 1) so a normal table-activate
-- stays quiet. Failures below use error() (which keeps CE's Lua Engine visible).
local function _dbgOn() return (tonumber(rawget(_G, "DISCRETE_DEBUG")) or 0) >= 1 end
local function _msg(fmt, ...) if _dbgOn() then print("[bootstrap] " .. string.format(fmt, ...)) end end

-- Close CE's Lua Engine on a clean run so it stops covering CE's main window.
-- bootstrap()/teardown() error() out on failure, so reaching their end means
-- success; skip the close under DEBUG or if something set the shared error flag.
local function _closeLuaEngineIfQuiet()
    if _dbgOn() or rawget(_G, "DISCRETE_HAD_ERROR") then return end
    if synchronize == nil or getLuaEngine == nil then return end
    pcall(synchronize, function()
        local e = getLuaEngine()
        if e and not pcall(function() e.Close() end)
            and not pcall(function() e.close() end) then pcall(function() e.hide() end) end
    end)
end

local function _extractDir()
    local tempRoot = os.getenv("TEMP") or os.getenv("TMP") or os.getenv("LOCALAPPDATA")
    if not tempRoot or #tempRoot == 0 then
        error("[bootstrap] no TEMP / TMP / LOCALAPPDATA env var — can't pick extract dir")
    end
    local dir = tempRoot .. "\\Discrete_Dumper\\" .. TABLE_ID .. "\\"
    -- Make sure it exists. CE's Lua doesn't expose mkdir directly, so
    -- shell-out via os.execute. The /q flag silences "directory exists".
    os.execute('mkdir "' .. dir .. '" 2>nul')
    return dir
end

local function _extractOne(tableFileName, outPath)
    local tf = findTableFile(tableFileName)
    if tf == nil then
        error("[bootstrap] table file not found in this .CT: " .. tableFileName
              .. " (did you add it via Table menu → Add file…?)")
    end

    -- ⚠️ CE's Lua class methods are bound closures that take their documented
    -- arguments and NOTHING ELSE — there is no implicit self. So `obj:method(x)`
    -- passes the object itself as the FIRST documented argument and silently
    -- misbehaves. Verified on CE 7.7: `tf.Stream:read(16)` returns **nil**,
    -- while `tf.Stream.read(16)` returns the 16-byte bytetable. Every CE object
    -- call in this file therefore uses DOT syntax. (Plain Lua `io` handles are
    -- different — `f:write(...)` below is real Lua and correct.)
    --
    -- Route 1 is TableFile.saveToFile (celua.txt: "saveToFile(filename)"): CE
    -- writes the blob itself, so there is no read, no bytetable and no chunking.
    local okSave = pcall(function() return tf.saveToFile(outPath) end)
    if okSave then
        local chk = io.open(outPath, "rb")
        if chk then
            local n = chk:seek("end")
            chk:close()
            if n and n > 0 then return end
        end
    end

    -- Route 2 (older CE builds without TableFile.saveToFile): read the stream
    -- ourselves. `read(count)` hands back a BYTETABLE, not a string
    -- (celua.txt: "read(count): bytetable"), and string.char(table.unpack(t))
    -- in one shot blows the Lua stack (unpack is bounded by LUAI_MAXSTACK) on
    -- the multi-MB DLL blobs — so convert in chunks.
    local stream = tf.Stream
    local size   = stream.Size
    stream.Position = 0
    local bytes = stream.read(size)
    local f, err = io.open(outPath, "wb")
    if not f then
        error("[bootstrap] cannot open " .. outPath .. " for write: " .. tostring(err))
    end
    if type(bytes) == "string" then
        f:write(bytes)                      -- future-proof if CE ever changes it
    elseif type(bytes) == "table" then
        local CHUNK = 4096
        local n = #bytes
        for i = 1, n, CHUNK do
            local j = math.min(i + CHUNK - 1, n)
            f:write(string.char(table.unpack(bytes, i, j)))
        end
    else
        f:close()
        error("[bootstrap] Stream.read gave " .. type(bytes)
              .. " — if this says nil, something re-introduced colon syntax")
    end
    f:close()
end

local function _detectBackendInTarget()
    local pid = getOpenedProcessID()
    if pid == nil or pid == 0 then
        return nil, "no opened process — open the game first"
    end
    local mods = enumModules(pid)
    if not mods then return nil, "enumModules returned nil" end
    for _, m in ipairs(mods) do
        local name = (m.Name or m.name or ""):lower()
        if name:find("gameassembly%.dll") then return "IL2CPP" end
        if name:find("mono%-2%.0%-bdwgc%.dll") or name == "mono.dll" then
            return "Mono"
        end
    end
    return nil, "no GameAssembly.dll / mono*.dll found among modules"
end

local function _is32bit()
    -- CE's bitness query is `targetIs64Bit()` — there is no `is32bitProcess`
    -- on any CE version. (`targetIsX86()` is the architecture family and is
    -- true for x64 targets too, so it cannot answer this.)
    if targetIs64Bit then return not targetIs64Bit() end
    -- Query missing (very old CE): assume 64-bit, the modern Unity default.
    return false
end

--- Public entry point. Call from your CT's [ENABLE] script.
function bootstrap()
    local extractDir = _extractDir()
    _msg("extract dir = %s", extractDir)

    -- 1. Extract every embedded file. Capture per-kind paths so we
    -- can pick the right DLL after backend / bitness detection.
    local extracted = {}  -- kind → absolute path
    for tableName, meta in pairs(FILES_TO_EXTRACT) do
        local outPath = extractDir .. tableName
        _extractOne(tableName, outPath)
        extracted[meta.kind] = extracted[meta.kind] or {}
        table.insert(extracted[meta.kind], outPath)
        _msg("extracted %s → %s", tableName, outPath)
    end

    -- 2. Detect backend + bitness, pick matching DLL.
    local backend, err = _detectBackendInTarget()
    if not backend then error("[bootstrap] backend detect failed: " .. tostring(err)) end
    _msg("target backend = %s", backend)

    local x86 = _is32bit()
    _msg("target bitness = %s", x86 and "x86" or "x64")

    local dllKind
    if backend == "Mono"   and x86 then dllKind = "dll-mono-x86"
    elseif backend == "Mono"               then dllKind = "dll-mono-x64"
    elseif backend == "IL2CPP" and not x86 then dllKind = "dll-il2cpp-x64"
    elseif backend == "IL2CPP" and x86 then
        error("[bootstrap] target is 32-bit IL2CPP — not supported (no shipping IL2CPP target is x86 in modern Unity)")
    end
    local dllPaths = extracted[dllKind] or {}
    if #dllPaths == 0 then
        error("[bootstrap] no embedded " .. dllKind
              .. " — embed the matching DLL via Table menu → Add file…")
    end
    local dllPath = dllPaths[1]
    _msg("injecting %s", dllPath)

    -- 3. Inject the backend DLL.
    if injectDLL ~= nil then
        if injectDLL(dllPath) == nil then
            error("[bootstrap] injectDLL returned nil — check %LOCALAPPDATA%\\Discrete_Dumper\\Logs\\<pid>\\init\\current.log")
        end
    elseif loadLibrary ~= nil then
        local h = loadLibrary(dllPath)
        if h == nil or h == 0 then
            error("[bootstrap] loadLibrary returned NULL — check %LOCALAPPDATA%\\Discrete_Dumper\\Logs\\<pid>\\init\\current.log")
        end
    else
        error("[bootstrap] neither injectDLL nor loadLibrary available in this CE build")
    end
    if sleep then sleep(300) end  -- pipe-server thread spin-up budget
    _msg("DLL loaded; pipe should be live at \\\\.\\pipe\\DiscreteBfx")

    -- 4. Make the Lua bridges available. utils MUST load first so the
    -- other bridges can `require 'utils'` (or read M.* globals).
    local utilsPaths = extracted["lua-utils"] or {}
    if #utilsPaths > 0 then
        package.path = extractDir .. "?.lua;" .. package.path
        dofile(utilsPaths[1])
    end
    for _, p in ipairs(extracted["lua-main"] or {}) do
        dofile(p)
        _msg("loaded lua-main %s", p)
    end
    for _, p in ipairs(extracted["lua-helper"] or {}) do
        dofile(p)
        _msg("loaded lua-helper %s", p)
    end

    _msg("bootstrap done — use inject(), getName(addr), etc. from your AA-script Lua blocks")
    _closeLuaEngineIfQuiet()
end

--- Optional companion: tear down on table disable. Worth calling so a
--- next-launch fresh start doesn't pile up old extracts. Idempotent.
function teardown()
    local extractDir = _extractDir()
    _msg("teardown — leaving extracted files in %s (no-op cleanup; OS reclaims TEMP eventually)", extractDir)
    -- We intentionally don't rmdir here because (a) the DLL is still
    -- loaded in the game process so its file handle is held, and (b)
    -- TEMP gets cleaned by Windows Disk Cleanup / aggressive users.
    -- If you want hard cleanup, do it from the user's [DISABLE]
    -- script AFTER the game process exits.
    _closeLuaEngineIfQuiet()
end
