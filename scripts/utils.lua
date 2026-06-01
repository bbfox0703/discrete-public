-- ============================================================
-- utils.lua — helper utilities for Discrete CE Lua scripts
-- ============================================================
--
-- Loaded by `unity_dump.lua` etc. via `require 'utils'`. Mirrors the
-- pattern used by the UE5CEDumper sibling project so anyone familiar
-- with that repo recognises the verbs.

local M = {}

-- ---------- Logging ----------------------------------------------------

local TAG = "[Discrete]"

function M.log(fmt, ...)
    print(string.format(TAG .. " " .. fmt, ...))
end

function M.warn(fmt, ...)
    print(string.format(TAG .. " WARN " .. fmt, ...))
end

function M.err(fmt, ...)
    print(string.format(TAG .. " ERROR " .. fmt, ...))
end

function M.toHex(addr)
    return string.format("0x%X", addr or 0)
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

    -- executeCodeEx: arg 1 = calling-convention flag.
    --   0 = void return, 1 = integer/bool return (cdecl-ish on x64).
    -- Our DLL exports use the default __cdecl on x64, which is the same
    -- as Microsoft's __fastcall/x64 calling convention CE assumes.
    local conv = (retType == "void") and 0 or 1
    local ret  = executeCodeEx(conv, fn, ...)
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
    local x86 = (is32bitProcess and is32bitProcess()) or false
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
