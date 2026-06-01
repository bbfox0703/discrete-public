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

-- ============================================================
-- Module lookup + unload helpers (Tier 1 dormant / Tier 2 unmap)
-- ============================================================

--- Find the base address (== HMODULE) of our injected backend DLL in the
--- target. Matches any Discrete-*.dll variant (Mono / IL2CPP / Mono-x86).
--- Returns base, name — or nil when the DLL isn't currently mapped.
local function findDiscreteModule()
    if enumModules == nil then return nil end
    local mods = enumModules()
    if not mods then return nil end
    for _, m in ipairs(mods) do
        local raw  = m.Name or m.name or ""
        local base = (raw:match("([^\\/]+)$") or raw):lower()
        if base:match("^discrete%-.*%.dll$") then
            return (m.Address or m.address), raw
        end
    end
    return nil
end

--- Stop the DLL's pipe server + remove its runtime_invoke hook so the DLL
--- goes dormant. It STAYS mapped; re-enabling the bridge restarts the pipe
--- (main() detects the mapped module and just calls StartPipe). This is the
--- ONLY teardown we expose: a full FreeLibrary unload of a DLL that hooks
--- the per-frame runtime_invoke can't be made crash-safe (the game main
--- thread is almost always inside the detour at unmap time), so we don't.
--- Discrete_Shutdown internally stops + joins the pipe thread and removes
--- the MinHook detour, so one call is the whole teardown. Idempotent.
function discreteShutdown()
    local ok, err = pcall(function()
        U.callDLL("Discrete_Shutdown", "void")
    end)
    if ok then
        U.log("DLL shut down — pipe stopped, hooks removed (still mapped).")
        return true
    end
    U.warn("discreteShutdown failed: %s", tostring(err))
    return false
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

    -- 3. Inject the matching DLL into the *target* process — UNLESS it's
    --    already mapped (a previous [ENABLE], or DiscreteUI injected it).
    --    Re-injecting just bumps the refcount and complicates a later
    --    unload, so when the module is present we skip injection and fall
    --    through to StartPipe (step 4), which revives a dormant pipe after
    --    a Tier-1 discreteShutdown(). This makes enable/disable reversible
    --    without leaking refcounts.
    local mappedBase, mappedName = findDiscreteModule()
    if mappedBase ~= nil then
        U.log("DLL already mapped (%s) — skipping inject, (re)starting pipe.", mappedName or "?")
    else
        local dll = U.dllPath(backend)
        U.log("DLL path: %s", dll)

        if injectDLL ~= nil then
            local ok = injectDLL(dll)
            if ok == nil then
                U.err("injectDLL returned nil — DLL not found or injection failed.")
                U.err("Check %%LOCALAPPDATA%%\\Discrete_Dumper\\Logs\\<pid>\\init\\current.log")
                return
            end
        elseif loadLibrary ~= nil then
            local handle = loadLibrary(dll)
            if handle == nil or handle == 0 then
                U.err("loadLibrary returned NULL — DLL not found or LoadLibraryW failed.")
                U.err("Check %%LOCALAPPDATA%%\\Discrete_Dumper\\Logs\\<pid>\\init\\current.log")
                return
            end
        else
            U.err("Neither injectDLL nor loadLibrary is available in this CE build.")
            return
        end
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

-- Surface the teardown verb in CE's Lua editor highlighter (best-effort).
if registerLuaFunctionHighlight ~= nil then
    pcall(registerLuaFunctionHighlight, "discreteShutdown")
end

local pcallOk, pcallErr = pcall(main)
if not pcallOk then
    print("[Discrete FATAL] " .. tostring(pcallErr))
end
