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

--- Resolve the on-disk path to Discrete-{Backend}.dll.
--- Convention: scripts live alongside the DLLs in CE's `discrete/`
--- subfolder. Falls back to the script's own directory if CE didn't
--- expose its install dir (e.g. when running from a portable copy).
function M.dllPath(backend)
    local dllName = "Discrete-" .. backend .. ".dll"

    -- Preferred: <CE-install>/discrete/Discrete-*.dll
    if getCheatEngineDir then
        local p = getCheatEngineDir() .. "discrete\\" .. dllName
        if fileExists and fileExists(p) then return p end
        -- Even when fileExists isn't available (older CE), try this path
        -- first and fall through to the alt only if it resolves to nil
        -- on loadLibrary.
        if not fileExists then return p end
    end

    -- Fallback: the directory this .lua sits in.
    if getMainForm and getMainForm() and getMainForm().SaveDialog1 then
        local scriptPath = getMainForm().SaveDialog1.FileName
        if scriptPath and #scriptPath > 0 then
            local dir = scriptPath:match("(.*[\\/])") or ""
            return dir .. dllName
        end
    end
    return dllName  -- last resort — relies on PATH / cwd
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
