-- ============================================================
-- unity_dump.lua — main CE script: detect backend, inject DLL,
--                  start the JSON-RPC pipe server.
-- ============================================================
--
-- Phase 11: replaces `Test-Handshake.ps1` for CE-driven workflows.
--
-- How to use:
--   1. Drop Discrete-Mono.dll + Discrete-IL2CPP.dll into
--      <CE-install>/discrete/  (alongside CE.exe).
--   2. In CE: File → Open Process → pick the Unity game.
--   3. Table → Show Cheat Table Lua Script (or Ctrl+Alt+L) → paste the
--      contents of this file → Execute Script.
--   4. Launch DiscreteUI.exe → Connect.
--
-- Banner output appears in CE's Lua console:
--   [Discrete] Backend: IL2CPP — DLL injected (build 99)
--   [Discrete] Pipe ready: \\.\pipe\DiscreteBfx

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
    print("[Discrete] FATAL: utils.lua not found alongside this script")
    print("[Discrete]   tried path: " .. tostring(thisDir) .. "utils.lua")
    print("[Discrete]   require error: " .. tostring(U))
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
        U.err("Looked for: GameAssembly.dll / UserAssembly.dll (IL2CPP),")
        U.err("            mono-2.0-bdwgc.dll / mono-2.0-sgen.dll / mono-2.0-boehm.dll / mono.dll (Mono).")
        U.err("Wait for the game to finish loading, then re-run.")
        -- Diagnostic: dump every module CE *did* see so we can spot the
        -- real Unity DLL name (or confirm enumeration came back empty).
        if enumModules ~= nil then
            local mods = enumModules() or {}
            U.err("enumModules() returned %d entries. Suspect candidates:", #mods)
            local hits = 0
            for _, m in ipairs(mods) do
                local raw = m.Name or m.name or ""
                local low = raw:lower()
                if low:find("mono") or low:find("il2cpp")
                   or low:find("unity") or low:find("assembly") then
                    print(string.format("  %s", raw))
                    hits = hits + 1
                end
            end
            if hits == 0 then
                U.err("(no module names contained mono/il2cpp/unity/assembly —")
                U.err(" enumeration likely returned the wrong process or is empty)")
            end
        end
        return
    end
    U.log("Backend detected: %s", backend)

    -- 3. Inject the matching DLL into the *target* process. Modern CE
    --    (7.x) exposes `injectDLL(path)` for this; older builds had
    --    `loadLibrary` as an alias. Prefer injectDLL when available.
    local dll = U.dllPath(backend)
    U.log("DLL path: %s", dll)

    local injected = false
    if injectDLL ~= nil then
        local ok = injectDLL(dll)
        if ok == nil then
            U.err("injectDLL returned nil — DLL not found or injection failed.")
            U.err("Check %%LOCALAPPDATA%%\\Discrete_Dumper\\Logs\\<pid>\\init\\current.log")
            return
        end
        injected = true
    elseif loadLibrary ~= nil then
        local handle = loadLibrary(dll)
        if handle == nil or handle == 0 then
            U.err("loadLibrary returned NULL — DLL not found or LoadLibraryW failed.")
            U.err("Check %%LOCALAPPDATA%%\\Discrete_Dumper\\Logs\\<pid>\\init\\current.log")
            return
        end
        injected = true
    else
        U.err("Neither injectDLL nor loadLibrary is available in this CE build.")
        return
    end

    -- DllMain auto-runs Discrete_Init + Discrete_StartPipe, so the
    -- pipe is usually up by the time injectDLL returns. Give it a beat
    -- to settle and to flush the init log line.
    if sleep then sleep(300) end

    -- Force CE to re-scan symbols so getAddress() can resolve the freshly-
    -- injected DLL's exports. Without this, getAddress returns garbage
    -- (often 0 or stale), executeCodeEx then returns nil, and downstream
    -- formatters (e.g. "%d") crash on the nil return value.
    if reinitializeSymbolhandler ~= nil then
        pcall(reinitializeSymbolhandler)
    end

    -- 4. Optional: explicitly call StartPipe + GetBuildNumber. These are
    --    pure nice-to-haves — DllMain has already started the pipe — so
    --    we treat any failure (e.g. CE can't resolve the export) as a
    --    soft warn rather than aborting the script.
    local ok2, err = pcall(function()
        local startRet = U.callDLL("Discrete_StartPipe", "int32")
        if startRet ~= nil and startRet ~= 0 then
            U.warn("Discrete_StartPipe returned %d (non-zero); pipe may still be running from auto-start.", startRet)
        end
    end)
    if not ok2 then
        U.warn("Skipping explicit StartPipe call: %s", tostring(err))
    end

    -- 5. Report build number for cross-checking with the UI handshake.
    local buildOk, build = pcall(function()
        return U.callDLL("Discrete_GetBuildNumber", "uint32")
    end)
    if buildOk and type(build) == "number" then
        U.log("Backend: %s — DLL injected (build %d)", backend, build)
    else
        U.log("Backend: %s — DLL injected (build unknown — symbol not yet resolvable)", backend)
    end
    U.log("Pipe ready: \\\\.\\pipe\\DiscreteBfx")
    U.log("Launch DiscreteUI.exe and click Connect.")
end

local pcallOk, pcallErr = pcall(main)
if not pcallOk then
    print("[Discrete FATAL] " .. tostring(pcallErr))
end
