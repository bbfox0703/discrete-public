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
     UnityDumper-Mono.dll      OR  UnityDumper-IL2CPP.dll
     (whichever backend your target game uses; embed BOTH if your .CT
      targets a multi-backend game like Farthest Frontier)

     utils.lua
     unity_dump.lua            (provides `inject()` — the entry point)

   If targeting 32-bit games:
     UnityDumper-Mono.dll      (x86 build from dist/x86/; embed under
                                a distinct table-file name like
                                "UnityDumper-Mono-x86.dll" to avoid
                                colliding with the x64 entry)

   Optional, based on which RPCs your table actually uses:
     unity_dissect.lua         (Structure Dissect generator)
     unity_symbols.lua         (registersymbol bulk loader)
     unity_invoke.lua          (callMethod from Lua)
     unity_scene.lua           (Scene graph walker)
     unity_addrfind.lua        (whatIsAt reverse lookup)
     mono_registersymbol_helpers.lua  (DLL-free Mono register path)

2. Copy this file's `bootstrap()` function into your .CT's autorun
   Lua script. Customise `FILES_TO_EXTRACT` for the files you
   embedded. Call `bootstrap()` from your [ENABLE] / activation
   script.

================================================================
What this script does on table-activate
================================================================

  1. Decide TEMP target dir = %TEMP%\Discrete_UEDumper\<table-id>\
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
--   kind = "lua-helper"     → Other optional bridges (unity_invoke / unity_scene / etc.)
local FILES_TO_EXTRACT = {
    -- ----- Backend DLLs (embed only the ones your target needs) ----
    -- ["UnityDumper-Mono.dll"]      = { kind = "dll-mono-x64"   },
    -- ["UnityDumper-IL2CPP.dll"]    = { kind = "dll-il2cpp-x64" },
    -- ["UnityDumper-Mono-x86.dll"]  = { kind = "dll-mono-x86"   },

    -- ----- Required Lua --------------------------------------------
    ["utils.lua"]      = { kind = "lua-utils" },
    ["unity_dump.lua"] = { kind = "lua-main"  },

    -- ----- Optional Lua (uncomment as needed) ----------------------
    -- ["unity_invoke.lua"]               = { kind = "lua-helper" },
    -- ["unity_scene.lua"]                = { kind = "lua-helper" },
    -- ["unity_dissect.lua"]              = { kind = "lua-helper" },
    -- ["unity_symbols.lua"]              = { kind = "lua-helper" },
    -- ["unity_addrfind.lua"]             = { kind = "lua-helper" },
    -- ["mono_registersymbol_helpers.lua"] = { kind = "lua-helper" },
}

--------------------------------------------------------------------
-- Implementation. Edit only if you need behavioural changes; the
-- normal customisation surface is the FILES_TO_EXTRACT table above.
--------------------------------------------------------------------

local function _msg(fmt, ...) print("[bootstrap] " .. string.format(fmt, ...)) end

local function _extractDir()
    local tempRoot = os.getenv("TEMP") or os.getenv("TMP") or os.getenv("LOCALAPPDATA")
    if not tempRoot or #tempRoot == 0 then
        error("[bootstrap] no TEMP / TMP / LOCALAPPDATA env var — can't pick extract dir")
    end
    local dir = tempRoot .. "\\Discrete_UEDumper\\" .. TABLE_ID .. "\\"
    -- Make sure it exists. CE's Lua doesn't expose mkdir directly, so
    -- shell-out via os.execute. The /q flag silences "directory exists".
    os.execute('mkdir "' .. dir .. '" 2>nul')
    return dir
end

local function _extractOne(tableFileName, outPath)
    -- CE's TableFile object → stream API. Works on every CE 7.x build.
    local tf = findTableFile(tableFileName)
    if tf == nil then
        error("[bootstrap] table file not found in this .CT: " .. tableFileName
              .. " (did you add it via Table menu → Add file…?)")
    end
    local stream = tf.Stream
    local size   = stream.Size
    -- Read entire blob, write to disk. For Lua-string-friendly write
    -- use stream:read(size) → byte string → io.open binary write.
    stream.Position = 0
    local bytes = stream:read(size)
    local f, err = io.open(outPath, "wb")
    if not f then
        error("[bootstrap] cannot open " .. outPath .. " for write: " .. tostring(err))
    end
    f:write(bytes)
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
    -- CE exposes `is32bitProcess()` on 7.x. Fall back to checking the
    -- target's main module size / shape heuristic if missing.
    if is32bitProcess then return is32bitProcess() end
    -- Best-effort fallback: assume 64-bit on modern Unity. The CE
    -- table author should ship a 32-bit-aware bootstrap if targeting
    -- old games.
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
            error("[bootstrap] injectDLL returned nil — check %LOCALAPPDATA%\\Discrete_UEDumper\\Logs\\<pid>\\init\\current.log")
        end
    elseif loadLibrary ~= nil then
        local h = loadLibrary(dllPath)
        if h == nil or h == 0 then
            error("[bootstrap] loadLibrary returned NULL — check %LOCALAPPDATA%\\Discrete_UEDumper\\Logs\\<pid>\\init\\current.log")
        end
    else
        error("[bootstrap] neither injectDLL nor loadLibrary available in this CE build")
    end
    if sleep then sleep(300) end  -- pipe-server thread spin-up budget
    _msg("DLL loaded; pipe should be live at \\\\.\\pipe\\UnityDumpBfx")

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
end
