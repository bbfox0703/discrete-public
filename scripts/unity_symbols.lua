-- ============================================================
-- unity_symbols.lua — batch-register CE symbols for a class's static
--                     fields and method addresses.
-- ============================================================
--
-- Phase 11. Run AFTER unity_dump.lua has injected the DLL. Adds CE
-- symbols using the Architecture.md naming convention so cheat tables
-- can reference fields by readable name (e.g. via
-- `[Unity:Assembly-CSharp:MainGame.PLY_WORK.HP] = 999`).
--
-- Usage from CE's Lua console:
--   dofile([[<CE-install>\discrete\unity_symbols.lua]])
--   registerClassSymbols("Assembly-CSharp", "MainGame", "PLY_WORK")
--   -- Mono targets: pass {compileMethods=false} to skip JIT-on-register
--   --   if you don't want unused methods compiled into the address space.
--   registerClassSymbols("Assembly-CSharp", "", "Player", {compileMethods=false})
--
-- Naming convention (Architecture.md §"Symbol Registration Pattern"):
--   Unity:{Asm}:{Ns}.{Cls}.s_{Field}    -> static field absolute address
--   Unity:{Asm}:{Ns}.{Cls}.m_{Method}   -> method native address (when known)
--
-- Method names previously used `::` as the separator. CE's symbol resolver
-- treats `::` as ambiguous and falls through to a slow Mono-runtime
-- enumeration that can hang the UI for minutes. The `.m_` form mirrors
-- `.s_` for static fields and resolves through the fast local-symbol path.
--
-- Backend differences:
--   IL2CPP: every method has a baked-in address; getClassInfo returns it
--           directly. Method-symbol registration is essentially free.
--   Mono:   methods are JIT-compiled on first call. We invoke
--           monoCompileMethod per method to force compilation and read
--           the returned native entry point. This means a `registerClass-
--           Symbols` call against a 50-method class triggers 50 JITs.
--           Override via opts.compileMethods=false to register only the
--           statics + already-JIT'd methods (m.address already populated).
--
-- Reuses the JSON parser from unity_dissect.lua via require if available;
-- otherwise an inline copy keeps the file standalone.

local TAG = "[UnityCEDumper.symbols]"
local function log(fmt, ...) print(string.format(TAG .. " " .. fmt, ...)) end
local function warn(fmt, ...) print(string.format(TAG .. " WARN " .. fmt, ...)) end
local function err(fmt, ...) print(string.format(TAG .. " ERROR " .. fmt, ...)) end

-- Minimal pipe RPC + JSON parser (duplicated to keep this script standalone).
local PIPE   = [[\\.\pipe\UnityDumpBfx]]
local nextId = 0

local function parseValue(s, i)
    local c = s:sub(i, i)
    if c == '"' then
        local out, j = {}, i + 1
        while j <= #s do
            local ch = s:sub(j, j)
            if ch == '\\' then out[#out + 1] = s:sub(j + 1, j + 1); j = j + 2
            elseif ch == '"' then return table.concat(out), j + 1
            else out[#out + 1] = ch; j = j + 1 end
        end
        error("string")
    elseif c == '{' then
        local t, j = {}, i + 1
        j = j + #(s:sub(j):match("^%s*") or "")
        if s:sub(j, j) == '}' then return t, j + 1 end
        while true do
            j = j + #(s:sub(j):match("^%s*") or "")
            local k; k, j = parseValue(s, j)
            j = j + #(s:sub(j):match("^%s*:%s*") or "")
            local v; v, j = parseValue(s, j); t[k] = v
            j = j + #(s:sub(j):match("^%s*") or "")
            local sep = s:sub(j, j)
            if sep == ',' then j = j + 1
            elseif sep == '}' then return t, j + 1 end
        end
    elseif c == '[' then
        local t, j = {}, i + 1
        j = j + #(s:sub(j):match("^%s*") or "")
        if s:sub(j, j) == ']' then return t, j + 1 end
        while true do
            j = j + #(s:sub(j):match("^%s*") or "")
            local v; v, j = parseValue(s, j); t[#t + 1] = v
            j = j + #(s:sub(j):match("^%s*") or "")
            local sep = s:sub(j, j)
            if sep == ',' then j = j + 1
            elseif sep == ']' then return t, j + 1 end
        end
    elseif c == 't' then return true,  i + 4
    elseif c == 'f' then return false, i + 5
    elseif c == 'n' then return nil,   i + 4
    else
        local m = s:match("^%-?%d+%.?%d*[eE]?[%+%-]?%d*", i)
        return tonumber(m), i + #m
    end
end

local function rpc(cmd, args)
    -- Yield to CE's Windows message pump every RPC. registerClassSymbols on
    -- a 300+ method class issues that many back-to-back pipe round-trips on
    -- CE's main thread; without this CE appears hung for the duration of
    -- the loop. processMessages() alone proved insufficient — empirically
    -- a kernel-level sleep(1) is required to actually let the message pump
    -- breathe. Per-method cost is ~1 ms, so 330 methods ~= +330 ms total.
    if processMessages then processMessages() end
    sleep(1)
    nextId = nextId + 1
    local function quote(x) return '"' .. tostring(x):gsub('"', '\\"') .. '"' end
    local parts = {}
    for k, v in pairs(args or {}) do
        local val = (type(v) == "number") and tostring(v)
                 or (type(v) == "boolean") and (v and "true" or "false")
                 or quote(v)
        parts[#parts + 1] = quote(k) .. ":" .. val
    end
    local argsJson = next(parts) and ("{" .. table.concat(parts, ",") .. "}") or "{}"
    local line = string.format('{"id":%d,"cmd":%s,"args":%s}', nextId, quote(cmd), argsJson)
    -- The DLL's pipe-server worker thread may not have called CreateNamedPipeW
    -- yet by the time the inject script returns "Pipe ready". Retry up to ~3s
    -- before giving up — same race the C# UI's PipeClient handles with retry.
    local f, fe
    for attempt = 1, 15 do
        f, fe = io.open(PIPE, "r+b")
        if f then break end
        sleep(200)
    end
    if f == nil then error("pipe open: " .. tostring(fe)) end
    f:write(line .. "\n"); f:flush()
    local resp = f:read("*l"); f:close()
    return parseValue(resp, 1)
end

-- ---------- Symbol registration ---------------------------------------

-- Backend cache — handshake is cheap but we only need to ask once per
-- session; subsequent registerClassSymbols calls reuse the value.
local cachedBackend = nil

local function detectBackend()
    if cachedBackend ~= nil then return cachedBackend end
    local hs = rpc("handshake", {})
    cachedBackend = (hs.result and hs.result.backend) or "?"
    return cachedBackend
end

--- Register CE symbols for every static field and method of the given
--- class. Returns counts so callers can verify how much landed.
---
--- @param assembly string
--- @param ns       string|nil  Namespace ("" if top-level)
--- @param name     string      Class name
--- @param opts     table|nil   {compileMethods=true|false, verbose=false}
function registerClassSymbols(assembly, ns, name, opts)
    opts = opts or {}
    local compileMethods = opts.compileMethods
    if compileMethods == nil then compileMethods = true end
    local verbose = opts.verbose == true

    local backend = detectBackend()
    local isMono  = backend == "Mono"

    local clsResp = rpc("getClassInfo",
                        { assembly = assembly, namespace = ns or "", name = name })
    if clsResp.error or (clsResp.result and clsResp.result.error) then
        err("getClassInfo failed: %s", clsResp.error or clsResp.result.error)
        return 0, 0
    end
    local info = clsResp.result
    if info == nil then err("no result"); return 0, 0 end

    local prefix = string.format("Unity:%s:%s%s%s",
        assembly, (ns and ns ~= "" and (ns .. ".") or ""), name, "")
    log("Registering symbols under '%s.*' (backend=%s)", prefix, backend)

    local statics, methods, methodFails = 0, 0, 0

    -- 1. Static fields — query the DLL for each field's runtime address.
    for _, f in ipairs(info.fields or {}) do
        if f["static"] then
            local sd = rpc("getStaticFieldData",
                           { assembly = assembly, namespace = ns or "", name = name, field = f.name })
            local addr = sd.result and sd.result.address
            if addr and #addr > 0 then
                local sym = prefix .. ".s_" .. f.name
                local addrInt = tonumber(addr:gsub("^0x", ""), 16)
                if registerSymbol then
                    registerSymbol(sym, addrInt, true)
                    statics = statics + 1
                end
            else
                warn("static field %s: no address (%s)", f.name,
                     (sd.result and sd.result.error) or "?")
            end
        end
    end

    -- 2. Methods — IL2CPP returns the address baked into the metadata via
    --    getClassInfo. Mono only knows the address after JIT compile, so
    --    we issue monoCompileMethod per method to force compilation. The
    --    address-already-populated check still runs first because Mono
    --    can pre-JIT some methods (e.g. ones called during scene load).
    --
    -- IMPORTANT: large classes (300+ methods) issue hundreds of back-to-back
    -- pipe RPCs, which starves CE's main-thread message pump and makes CE
    -- *appear* hung even though Lua is making forward progress. The progress
    -- log every 25 methods is a yield point — print writes to CE's console
    -- widget and lets CE service paint/input events between batches.
    local methodList = info.methods or {}
    local methodTotal = #methodList
    for i, m in ipairs(methodList) do
        if i % 25 == 0 then log("  progress: %d / %d methods", i, methodTotal) end
        local addr = m.address
        if (not addr or addr == "0x0" or addr == "") and isMono and compileMethods then
            if verbose then log("  JIT: %s", m.name) end
            local cm = rpc("monoCompileMethod",
                { assembly = assembly, namespace = ns or "", name = name, method = m.name })
            if cm.result and cm.result.address and #cm.result.address > 0 then
                addr = cm.result.address
            else
                -- Compile failures are common: abstract methods, generic
                -- definitions without instantiation, methods that touch
                -- unresolved AOT slots. Tally them but don't bail.
                methodFails = methodFails + 1
            end
        end
        if addr and addr ~= "0x0" and addr ~= "" then
            local addrInt = tonumber(addr:gsub("^0x", ""), 16)
            if addrInt and addrInt ~= 0 and registerSymbol then
                local sym = prefix .. ".m_" .. m.name
                registerSymbol(sym, addrInt, true)
                methods = methods + 1
            end
        end
    end

    if methodFails > 0 then
        log("Done: %d static-field + %d method symbols (%d method JITs failed).",
            statics, methods, methodFails)
    else
        log("Done: %d static-field + %d method symbols.", statics, methods)
    end
    return statics, methods
end

log("Loaded. Call registerClassSymbols('Assembly-CSharp', '', 'YourClass') to register.")
