-- ============================================================
-- utils.lua — helper utilities for Discrete CE Lua scripts
-- ============================================================
--
-- Loaded by `unity_dump.lua` etc. via `require 'utils'`. Mirrors the
-- pattern used by the UE5CEDumper sibling project so anyone familiar
-- with that repo recognises the verbs.

local M = {}

-- ---------- Logging ----------------------------------------------------
--
-- Convention (docs/lua-logging.md): CE routes print() to the Lua Engine form
-- and force-shows it on any output, so it covers CE's main window on every run.
-- To keep a normal run quiet we gate informational output behind a DEBUG flag
-- and only ever print unconditionally for errors.
--   * log()/dbg()/warn() print ONLY when DEBUG is on.
--   * err() ALWAYS prints and sets a sticky "this run failed" flag.
-- Combined with closeLuaEngineIfQuiet()/guard() below, a clean run finishes
-- silently and closes the Lua Engine; a failed run leaves it open with the
-- error visible.

local TAG = "[Discrete]"

-- Per-module debug flag. Default off. The global `DISCRETE_DEBUG = 1` (set
-- before running a script) also turns output on without editing this file.
M.DEBUG = false

local function debugOn()
    if M.DEBUG then return true end
    local g = rawget(_G, "DISCRETE_DEBUG")
    return g == 1 or g == true or g == "1"
end
M.debugOn = debugOn

-- Sticky per-session error flag. err() sets it; guard()/closeLuaEngineIfQuiet()
-- read it to decide whether to close the Lua Engine. resetError() clears it at
-- the start of a guarded run. It is ALSO mirrored to the global
-- `_G.DISCRETE_HAD_ERROR` so standalone bridge scripts + generated AA {$lua}
-- blocks (which don't require 'utils') share one "did anything fail?" signal
-- when deciding whether to close the Lua Engine. See docs/lua-logging.md.
local hadError = false
function M.hadError()   return hadError end
function M.resetError()
    hadError = false
    rawset(_G, "DISCRETE_HAD_ERROR", false)
end

function M.log(fmt, ...)
    if debugOn() then print(string.format(TAG .. " " .. fmt, ...)) end
end

-- Alias for log(): use when you want the call site to read as "debug-only".
M.dbg = M.log

function M.warn(fmt, ...)
    if debugOn() then print(string.format(TAG .. " WARN " .. fmt, ...)) end
end

function M.err(fmt, ...)
    hadError = true
    rawset(_G, "DISCRETE_HAD_ERROR", true)  -- shared cross-script failure signal
    print(string.format(TAG .. " ERROR " .. fmt, ...))
end

function M.toHex(addr)
    return string.format("0x%X", addr or 0)
end

-- ---------- Lua Engine window + guarded run ---------------------------
--
-- CE's Lua Engine form pops on top of CE's main window whenever a script
-- prints. Close it on a clean run so it stops covering CE. See CE API notes in
-- docs/lua-logging.md — getLuaEngine()/synchronize() are verified CE functions;
-- form close is the published Pascal `Close()` (celua.txt also documents the
-- lowercase close()/hide()), so we try each in turn, all pcall-guarded.

--- Force-close the Lua Engine window (best-effort). Runs on the main thread
--- because form operations must. Never raises.
function M.closeLuaEngine()
    if synchronize == nil or getLuaEngine == nil then return end
    pcall(synchronize, function()
        local e = getLuaEngine()
        if e == nil then return end
        if not pcall(function() e.Close() end)
            and not pcall(function() e.close() end) then
            pcall(function() e.hide() end)
        end
    end)
end

--- Close the Lua Engine only on a "quiet" finish: no err() emitted and DEBUG
--- off. A debug run keeps the window (the user turned output on to read it);
--- an errored run keeps the window (so the error is seen). Returns true if it
--- closed the window.
function M.closeLuaEngineIfQuiet()
    if hadError or debugOn() then return false end
    M.closeLuaEngine()
    return true
end

--- Run an entry-point body under the logging/close convention. body() runs
--- inside pcall; a thrown error is reported via err() (always prints, keeps the
--- window open). On a clean run the Lua Engine is closed. Returns (ok, result)
--- where ok is false if body threw or emitted an err().
--- @param body function
function M.guard(body)
    M.resetError()
    local ok, result = pcall(body)
    if not ok then
        M.err("%s", tostring(result))
    end
    M.closeLuaEngineIfQuiet()
    return ok and not hadError, result
end

-- ---------- DLL function calls (CE Lua executeCodeEx wrapper) ----------

--- Call an exported function in the injected Discrete DLL by name.
--- @param funcName string  Symbol exported from Discrete-{Backend}.dll
--- @param retType  string  "bool" | "int32" | "uint32" | "uint64" | "void"
--- @param ...      any     Function arguments (CE handles primitive marshaling)
--- @return         any     Return value
function M.callDLL(funcName, retType, ...)
    local ok, fn = pcall(getAddress, funcName)
    if not ok or fn == nil or fn == 0 then
        error(TAG .. " export not found: " .. funcName ..
              " — symbolhandler may not have scanned the new DLL yet.")
    end

    -- CE signature (celua.txt): executeCodeEx(callmethod, timeout, address, ...)
    --   callmethod — 0 = stdcall, 1 = cdecl. Our exports are declared
    --     `extern "C" __declspec(dllexport)` with no explicit convention, so
    --     MSVC gives them __cdecl. This matters on the 32-bit Mono build;
    --     x64 has a single convention either way. It does NOT depend on the
    --     return type.
    --   timeout — milliseconds; nil = wait indefinitely. Never pass 0: CE then
    --     skips freeing the call stub, leaking it on every call.
    --   address — must be the THIRD argument. Passing it second makes CE read
    --     the export address as a timeout and the first vararg as the address.
    local ret = executeCodeEx(1, nil, fn, ...)
    if retType == "void" then return nil end
    if ret == nil then
        error(TAG .. " " .. funcName .. " call returned nil (executeCodeEx failed).")
    end
    if retType == "bool" then return ret ~= 0 end
    return ret
end

-- ---------- Backend detection (which DLL to inject) -------------------

--- Determine which Unity scripting backend the *currently-attached* CE
--- target process uses. We prefer `enumModules()` (queries the OS module
--- table directly) over `getModule()` (which reads CE's symbolhandler
--- list — that one lags behind the OS right after attach, before symbol
--- loading finishes, and was the cause of false-negative detections on
--- IL2CPP titles like SaGa F2R).
--- Returns "IL2CPP" / "Mono" / nil. Backend nil means we couldn't find
--- a recognised module — caller should bail with a helpful message.
function M.detectBackend()
    -- Known module names per backend, lowercased for matching.
    local IL2CPP_NAMES = {
        ["gameassembly.dll"] = true,
        ["userassembly.dll"] = true, -- some Unity titles ship under this name
    }
    local MONO_NAMES = {
        ["mono-2.0-bdwgc.dll"] = true,
        ["mono-2.0-sgen.dll"]  = true,
        ["mono-2.0-boehm.dll"] = true,
        ["mono.dll"]           = true,
    }

    -- Primary path: enumModules() — direct OS query, no symbolhandler lag.
    -- CE's symbolhandler can be stale right after attach; force a refresh
    -- before enumerating, otherwise we miss freshly-loaded Unity DLLs.
    if reinitializeSymbolhandler ~= nil then
        pcall(reinitializeSymbolhandler)
    end
    if enumModules ~= nil then
        local mods = enumModules()
        if mods then
            for _, m in ipairs(mods) do
                -- Some CE builds expose .Name, others .name; some return
                -- the full path. Normalise both.
                local raw = m.Name or m.name or ""
                local base = raw:match("([^\\/]+)$") or raw
                local name = base:lower()
                if IL2CPP_NAMES[name] then return "IL2CPP" end
                if MONO_NAMES[name]   then return "Mono"   end
            end
        end
    end

    -- Fallback: getModule() if enumModules isn't available (very old CE).
    if getModule ~= nil then
        if getModule("GameAssembly.dll")     ~= nil then return "IL2CPP" end
        if getModule("UserAssembly.dll")     ~= nil then return "IL2CPP" end
        if getModule("mono-2.0-bdwgc.dll")   ~= nil then return "Mono"   end
        if getModule("mono-2.0-sgen.dll")    ~= nil then return "Mono"   end
        if getModule("mono-2.0-boehm.dll")   ~= nil then return "Mono"   end
        if getModule("mono.dll")             ~= nil then return "Mono"   end
    end
    return nil
end

-- ---------- DLL location ----------------------------------------------

--- Pick the on-disk DLL *file name* for a backend + the target's bitness.
--- The x86 Mono build ships as Discrete-Mono-x86.dll (renamed from the
--- x64 build's Discrete-Mono.dll) so both can sit in one folder beside the
--- .CT without colliding. IL2CPP has no 32-bit build, so it's always x64.
local function _dllName(backend)
    if backend == "IL2CPP" then
        return "Discrete-IL2CPP.dll"  -- x64 only — no shipping 32-bit IL2CPP
    end
    -- CE's bitness query is `targetIs64Bit()`; there is no `is32bitProcess`.
    -- (`targetIsX86()` is the architecture family — true for x64 too — so it
    -- cannot answer this.) Missing query = very old CE: assume x64.
    local x86 = (targetIs64Bit and not targetIs64Bit()) or false
    if backend == "Mono" and x86 then
        return "Discrete-Mono-x86.dll"
    end
    return "Discrete-" .. backend .. ".dll"  -- Discrete-Mono.dll (x64)
end

--- Directory of a file path (keeps the trailing slash), or nil.
local function _dirOf(path)
    if not path or #path == 0 then return nil end
    return path:match("(.*[\\/])")
end

--- Candidate directories to search for the backend DLL, in priority order.
--- Per the "DLL beside the .CT" distribution model, the table's own folder
--- comes FIRST. CE doesn't expose the loaded table path directly, so we
--- approximate it from the Open/Save dialog file names + TrainerOrigin
--- (these hold the last .CT path), then fall back to CE-install locations.
--- We verify each with fileExists rather than trusting any single one —
--- CE dialog paths can be stale (cached from a previous session).
local function _dllCandidateDirs()
    local dirs = {}
    local seen = {}
    local function add(d)
        if d and #d > 0 and not seen[d] then
            seen[d] = true
            dirs[#dirs + 1] = d
        end
    end

    -- 1. The .CT's folder (best guess at "beside the table"): CE dialog
    --    FileNames + trainer origin. Order: Save, Open, Trainer.
    if getMainForm and getMainForm() then
        local mf = getMainForm()
        if mf.SaveDialog1 then add(_dirOf(mf.SaveDialog1.FileName)) end
        if mf.OpenDialog1 then add(_dirOf(mf.OpenDialog1.FileName)) end
    end
    if TrainerOrigin and #TrainerOrigin > 0 then
        add(_dirOf(TrainerOrigin))
    end

    -- 2. CE install dir + its discrete\ subfolder (legacy convention).
    if getCheatEngineDir then
        local ce = getCheatEngineDir()
        add(ce .. "discrete\\")
        add(ce)
    end

    -- 3. Known app-data drop location.
    local appData = os.getenv("LOCALAPPDATA")
    if appData and #appData > 0 then
        add(appData .. "\\Discrete_Dumper\\")
    end

    return dirs
end

--- Resolve the on-disk path to the backend DLL for the current target.
--- Searches the .CT folder first (then CE-install / app-data), picking the
--- first directory that actually contains the right-bitness DLL. Falls back
--- to the bare file name (PATH / cwd) when nothing resolves.
function M.dllPath(backend)
    local dllName = _dllName(backend)
    local dirs    = _dllCandidateDirs()

    for _, dir in ipairs(dirs) do
        local p = dir .. dllName
        if fileExists and fileExists(p) then
            return p
        end
    end

    -- Nothing matched (or fileExists unavailable on an old CE build).
    -- Prefer the highest-priority candidate dir so the .CT folder still
    -- wins; otherwise relies on PATH / cwd.
    if #dirs > 0 then
        return dirs[1] .. dllName
    end
    return dllName
end

-- ---------- File existence (CE doesn't always expose this) ------------

if fileExists == nil then
    function fileExists(p)
        local f = io.open(p, "rb")
        if f == nil then return false end
        f:close()
        return true
    end
end

return M
