-- ============================================================
-- utils.lua — helper utilities for UnityCEDumper CE Lua scripts
-- ============================================================
--
-- Loaded by `unity_dump.lua` etc. via `require 'utils'`. Mirrors the
-- pattern used by the UE5CEDumper sibling project so anyone familiar
-- with that repo recognises the verbs.

local M = {}

-- ---------- Logging ----------------------------------------------------

local TAG = "[UnityCEDumper]"

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

--- Call an exported function in the injected UnityDumper DLL by name.
--- @param funcName string  Symbol exported from UnityDumper-{Backend}.dll
--- @param retType  string  "bool" | "int32" | "uint32" | "uint64" | "void"
--- @param ...      any     Function arguments (CE handles primitive marshaling)
--- @return         any     Return value
function M.callDLL(funcName, retType, ...)
    local fn = getAddress(funcName)
    if fn == nil or fn == 0 then
        error(TAG .. " export not found: " .. funcName .. " — DLL injected?")
    end

    -- executeCodeEx: arg 1 = calling-convention flag.
    --   0 = void return, 1 = integer/bool return (cdecl-ish on x64).
    -- Our DLL exports use the default __cdecl on x64, which is the same
    -- as Microsoft's __fastcall/x64 calling convention CE assumes.
    local conv = (retType == "void") and 0 or 1
    local ret  = executeCodeEx(conv, fn, ...)
    if retType == "bool"  then return ret ~= 0 end
    if retType == "void"  then return nil end
    return ret
end

-- ---------- Backend detection (which DLL to inject) -------------------

--- Determine which Unity scripting backend the *currently-attached* CE
--- target process uses. CE exposes loaded modules via `getModule(name)`.
--- Returns "IL2CPP" / "Mono" / nil. Backend nil means we couldn't find
--- a recognised module — caller should bail with a helpful message.
function M.detectBackend()
    if getModule == nil then return nil end
    if getModule("GameAssembly.dll")   ~= nil then return "IL2CPP" end
    if getModule("mono-2.0-bdwgc.dll") ~= nil then return "Mono"   end
    if getModule("mono.dll")           ~= nil then return "Mono"   end
    return nil
end

-- ---------- DLL location ----------------------------------------------

--- Resolve the on-disk path to UnityDumper-{Backend}.dll.
--- Convention: scripts live alongside the DLLs in CE's `discrete/`
--- subfolder. Falls back to the script's own directory if CE didn't
--- expose its install dir (e.g. when running from a portable copy).
function M.dllPath(backend)
    local dllName = "UnityDumper-" .. backend .. ".dll"

    -- Preferred: <CE-install>/discrete/UnityDumper-*.dll
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
