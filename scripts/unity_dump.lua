-- ============================================================
-- unity_dump.lua — main CE script: detect backend, inject DLL,
--                  start the JSON-RPC pipe server.
-- ============================================================
--
-- Phase 11: replaces `Test-Handshake.ps1` for CE-driven workflows.
--
-- How to use:
--   1. Drop UnityDumper-Mono.dll + UnityDumper-IL2CPP.dll into
--      <CE-install>/discrete/  (alongside CE.exe).
--   2. In CE: File → Open Process → pick the Unity game.
--   3. Table → Show Cheat Table Lua Script (or Ctrl+Alt+L) → paste the
--      contents of this file → Execute Script.
--   4. Launch DiscreteUI.exe → Connect.
--
-- Banner output appears in CE's Lua console:
--   [UnityCEDumper] Backend: IL2CPP — DLL injected (build 99)
--   [UnityCEDumper] Pipe ready: \\.\pipe\UnityDumpBfx

-- Resolve sibling utils.lua. CE's package.path doesn't include the
-- script directory by default, so we have to teach it. Try (in order):
--   1. debug.getinfo — Lua's own "where is the running file?" query.
--      This is the only one that survives `dofile([[…]])` invocations.
--   2. <CE-install>/discrete/  — the documented install layout.
--   3. The CE-form's SaveDialog1.FileName (only populated when the user
--      manually opened the script via the editor, not via dofile()).
local function discoverScriptDir()
    -- (1) debug.getinfo: source is "@<absolute path>" when loaded from
    --     a file. Falls back to nil for inlined chunks.
    local src = debug and debug.getinfo and debug.getinfo(1, "S")
    if src and src.source and src.source:sub(1, 1) == "@" then
        local p = src.source:sub(2)
        local d = p:match("(.*[\\/])")
        if d and #d > 0 then return d end
    end
    -- (2) Standard install layout.
    if getCheatEngineDir then
        local d = getCheatEngineDir() .. "discrete\\"
        return d
    end
    -- (3) Editor-opened path.
    if getMainForm and getMainForm() and getMainForm().SaveDialog1 then
        local p = getMainForm().SaveDialog1.FileName or ""
        local d = p:match("(.*[\\/])")
        if d and #d > 0 then return d end
    end
    return nil
end

local thisDir = discoverScriptDir()
if thisDir then
    package.path = thisDir .. "?.lua;" .. package.path
end

local ok, U = pcall(require, "utils")
if not ok then
    print("[UnityCEDumper] FATAL: utils.lua not found alongside this script")
    print("[UnityCEDumper]   tried path: " .. tostring(thisDir) .. "utils.lua")
    print("[UnityCEDumper]   require error: " .. tostring(U))
    return
end

local function main()
    -- 1. Sanity: a process must be open in CE for any of this to work.
    local pid = getOpenedProcessID and getOpenedProcessID() or 0
    if pid == 0 then
        U.err("No process attached. File → Open Process first.")
        return
    end
    U.log("Attached PID: %d", pid)

    -- 2. Detect backend by inspecting loaded modules.
    local backend = U.detectBackend()
    if backend == nil then
        U.err("No Unity backend detected.")
        U.err("Looked for: GameAssembly.dll (IL2CPP), mono-2.0-bdwgc.dll / mono.dll (Mono).")
        U.err("Wait for the game to finish loading, then re-run.")
        return
    end
    U.log("Backend detected: %s", backend)

    -- 3. Inject the matching DLL via CE's loadLibrary helper.
    local dll = U.dllPath(backend)
    U.log("DLL path: %s", dll)

    local handle = loadLibrary(dll)
    if handle == nil or handle == 0 then
        U.err("loadLibrary returned NULL — DLL not found or LoadLibraryW failed.")
        U.err("Check %%LOCALAPPDATA%%\\UnityCEDumper\\Logs\\<pid>\\init\\current.log")
        return
    end

    -- DllMain auto-runs UnityDumper_Init + UnityDumper_StartPipe, so the
    -- pipe is usually up by the time loadLibrary returns. Give it a beat
    -- to settle and to flush the init log line.
    if sleep then sleep(300) end

    -- 4. Belt-and-braces: explicitly call StartPipe so the script reports
    -- a definitive ready state instead of trusting auto-start. Both calls
    -- are idempotent on the DLL side.
    local ok2, err = pcall(function()
        local startRet = U.callDLL("UnityDumper_StartPipe", "int32")
        if startRet ~= 0 then
            U.warn("UnityDumper_StartPipe returned %d (non-zero); pipe may still be running from auto-start.", startRet)
        end
    end)
    if not ok2 then
        U.warn("Skipping explicit StartPipe call: %s", tostring(err))
    end

    -- 5. Report build number for cross-checking with the UI handshake.
    local buildOk, build = pcall(function()
        return U.callDLL("UnityDumper_GetBuildNumber", "uint32")
    end)
    if buildOk then
        U.log("Backend: %s — DLL injected (build %d)", backend, build)
    else
        U.log("Backend: %s — DLL injected (build unknown — old build?)", backend)
    end
    U.log("Pipe ready: \\\\.\\pipe\\UnityDumpBfx")
    U.log("Launch DiscreteUI.exe and click Connect.")
end

local pcallOk, pcallErr = pcall(main)
if not pcallOk then
    print("[UnityCEDumper FATAL] " .. tostring(pcallErr))
end
