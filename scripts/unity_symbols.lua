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
--   -- Phase 18: register every overload of a single method, with each
--   -- one disambiguated by its parameter types:
--   registerMethodOverloads("UnityEngine.CoreModule", "UnityEngine", "Vector3", "Set")
--   -- Optional sigFilter narrows on the rendered signature substring:
--   registerMethodOverloads("Assembly-CSharp", "", "Foo", "Bar", {sigFilter="Vector3"})
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
-- Ships its own inline pipe RPC + JSON parser (below) so the file
-- stays drop-in standalone — no extra require needed.

-- Logging convention (docs/lua-logging.md): log()/warn() print only when the
-- DEBUG flag (global DISCRETE_DEBUG == 1) is on, so routine symbol registration
-- stays quiet and doesn't pop CE's Lua Engine over its main window. err() always
-- prints and sets the shared _G.DISCRETE_HAD_ERROR flag so the generated AA block
-- that self-loads this file knows not to close the Lua Engine on a failed run.
local TAG = "[Discrete.symbols]"
local function _dbgOn() return (tonumber(rawget(_G, "DISCRETE_DEBUG")) or 0) >= 1 end
local function log(fmt, ...) if _dbgOn() then print(string.format(TAG .. " " .. fmt, ...)) end end
local function warn(fmt, ...) if _dbgOn() then print(string.format(TAG .. " WARN " .. fmt, ...)) end end
local function err(fmt, ...)
    rawset(_G, "DISCRETE_HAD_ERROR", true)
    print(string.format(TAG .. " ERROR " .. fmt, ...))
end

-- Minimal pipe RPC + JSON parser (duplicated to keep this script standalone).
local PIPE      = [[\\.\pipe\DiscreteBfx]]
local PIPE_NAME = "DiscreteBfx"
local kDefaultRpcTimeoutMs  = 10000
local kConnectRetryBudgetMs = 3000
-- Keep CE's window alive while we block. CE's Lua `sleep` is a bare Win32
-- Sleep and pumps NOTHING, so every sleep in a retry loop must be paired with
-- this or Cheat Engine freezes for the loop's whole duration.
--
-- Prefers processMessagesPaintOnly: celua.txt labels plain processMessages
-- "Warning, not recommended" (other Lua scripts and timers run during it and
-- can modify locals underneath us), and paint-only ignores mouse/keyboard so
-- it cannot re-enter our own handlers. Feature-tested, not version-gated -- an
-- undefined global is nil in Lua, not an error.
local function pump()
    local f = rawget(_G, "processMessagesPaintOnly") or rawget(_G, "processMessages")
    if type(f) == "function" then pcall(f) end
end

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

local function _nowMs()
    local tc = rawget(_G, "getTickCount")
    if type(tc) == "function" then
        local ok, v = pcall(tc)
        if ok and type(v) == "number" then return v end
    end
    return math.floor(os.clock() * 1000)
end

local function _elapsedMs(startMs, fallbackMs)
    local cur = _nowMs()
    if cur >= startMs then return cur - startMs end
    return fallbackMs
end

local function _closePipeOnce(state)
    local p = state.pipe
    state.pipe = nil
    if p ~= nil then pcall(function() p.destroy() end) end
end

-- E-M4: Bounded-timeout pipe transaction. Prefers CE's connectToPipe (native
-- overlapped timeout + CancelIo) inside createThread with GUI pump polling so
-- a stalled DLL handler cannot freeze Cheat Engine indefinitely.
local function pipeTransact(line, timeoutMs)
    local budget = tonumber(timeoutMs) or kDefaultRpcTimeoutMs
    if budget < 500 then budget = 500 elseif budget > 120000 then budget = 120000 end
    local payload = line .. "\n"

    local ceConnect = rawget(_G, "connectToPipe")
    local ceThread  = rawget(_G, "createThread")
    local ceSleep   = rawget(_G, "sleep")

    local function transactCePipe(state)
        local pipe = nil
        local cStart = _nowMs()
        local cElapsed = 0
        while not state.aborted do
            local okC, p = pcall(ceConnect, PIPE_NAME, budget)
            if okC and p ~= nil then pipe = p; break end
            if _elapsedMs(cStart, cElapsed) >= kConnectRetryBudgetMs then break end
            if type(ceSleep) == "function" then ceSleep(200) end
            cElapsed = cElapsed + 200
            if not state.inWorker then pump() end
        end
        if pipe == nil then return nil, "pipe open failed: " .. PIPE end
        state.pipe = pipe
        if state.aborted then
            _closePipeOnce(state)
            return nil, "pipe RPC timed out after " .. tostring(budget) .. " ms"
        end
        local timedOut = false
        pcall(function() pipe.OnTimeout = function() timedOut = true end end)
        local okW, written = pcall(function() return pipe.writeString(payload, false) end)
        if not okW or written == nil then
            _closePipeOnce(state)
            if timedOut or state.aborted then
                return nil, "pipe RPC timed out after " .. tostring(budget) .. " ms"
            end
            return nil, "pipe write failed"
        end
        local bytes, n, gotNewline = {}, 0, false
        while not state.aborted do
            local okR, b = pcall(function() return pipe.readByte() end)
            if not okR or b == nil then break end
            if b == 10 then gotNewline = true; break end
            if b ~= 13 then n = n + 1; bytes[n] = string.char(b) end
        end
        _closePipeOnce(state)
        if timedOut or state.aborted then
            return nil, "pipe RPC timed out after " .. tostring(budget) .. " ms"
        end
        if not gotNewline or n == 0 then
            return nil, "empty or truncated response"
        end
        return table.concat(bytes), nil
    end

    local function transactStdIo(state)
        local f, fe
        local cStart = _nowMs()
        local cElapsed = 0
        while not state.aborted do
            f, fe = io.open(PIPE, "r+b")
            if f ~= nil then break end
            if _elapsedMs(cStart, cElapsed) >= kConnectRetryBudgetMs then break end
            if type(ceSleep) == "function" then ceSleep(200) end
            cElapsed = cElapsed + 200
            if not state.inWorker then pump() end
        end
        if f == nil then return nil, "pipe open: " .. tostring(fe) end
        local okW, wErr = f:write(payload)
        if not okW then
            pcall(function() f:close() end)
            return nil, "pipe write: " .. tostring(wErr)
        end
        f:flush()
        local resp = f:read("*l")
        pcall(function() f:close() end)
        if resp == nil then return nil, "empty response" end
        return resp, nil
    end

    local runner = (type(ceConnect) == "function") and transactCePipe or transactStdIo

    if type(ceThread) == "function" and type(ceSleep) == "function" then
        local state = { done = false, aborted = false, inWorker = true,
                        resp = nil, err = nil, pipe = nil }
        local okT, th = pcall(ceThread, function()
            local okCall, r, e = pcall(runner, state)
            if okCall then state.resp, state.err = r, e
            else           state.err = tostring(r) end
            state.done = true
        end)
        if okT and th ~= nil then
            local totalDeadline = budget + kConnectRetryBudgetMs
            local tStart, tElapsed = _nowMs(), 0
            while not state.done do
                if _elapsedMs(tStart, tElapsed) >= totalDeadline then
                    state.aborted = true
                    _closePipeOnce(state)
                    pcall(function() th.terminate() end)
                    return nil, "pipe RPC timed out after " .. tostring(budget) .. " ms"
                end
                ceSleep(20)
                tElapsed = tElapsed + 20
                pump()
            end
            return state.resp, state.err
        end
    end

    return runner({ done = false, aborted = false, inWorker = false, pipe = nil })
end

local function rpc(cmd, args, timeoutMs)
    -- Yield to CE's Windows message pump every RPC. registerClassSymbols on
    -- a 300+ method class issues that many back-to-back pipe round-trips on
    -- CE's main thread; without this CE appears hung for the duration of
    -- the loop. BOTH calls are needed and they do different jobs: pump()
    -- drains the message queue, while sleep(1) yields the timeslice so the
    -- queue actually refills. CE's `sleep` is a bare Win32 Sleep and pumps
    -- nothing on its own. Per-method cost is ~1 ms, so 330 methods ~= +330 ms.
    pump()
    if sleep then sleep(1) end
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
    local resp, rerr = pipeTransact(line, timeoutMs)
    if resp == nil then
        error(TAG .. " rpc(" .. tostring(cmd) .. "): " .. tostring(rerr))
    end
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

--- Register CE symbols for every overload of a single method, with each
--- overload disambiguated by its parameter-type suffix. Used when a class
--- has overloaded methods and `registerClassSymbols` would only catch the
--- first match (CE rejects duplicate symbol registrations within one
--- session).
---
--- Backend differences:
---   IL2CPP: addresses come from the AOT MethodInfo->methodPointer field —
---           every overload has a real address.
---   Mono:   the DLL JIT-compiles each match server-side via
---           mono_compile_method as part of findMethodInstances. Abstract
---           methods / generic definitions without instantiation come back
---           with compiled=false and are skipped here (matching the same
---           rule registerClassSymbols already applies).
---
--- Symbol shape:
---   Unity:{Asm}:{Ns}.{Cls}.m_{Method}__{HumanSuffix}
---
--- HumanSuffix is the symbol-safe parameter-type fingerprint emitted by
--- the DLL's MethodSignatureFormat.h — same convention the
--- DiscreteUI overload picker uses for AA-script symbols, e.g.:
---   Vector3.Set(float, float, float)   → __3float
---   Vector3.Set(Vector3)               → __Vector3
---   Foo.Bar()                          → __noargs
---   Foo.Bar(string, Card, Point)       → __string_Card_Point
---
--- @param assembly  string
--- @param ns        string|nil   Namespace ("" if top-level)
--- @param name      string       Class name
--- @param method    string       Method name to enumerate overloads for
--- @param opts      table|nil    {sigFilter=string, verbose=bool}
---                               sigFilter narrows on the rendered signature
---                               substring (e.g. "Vector3" picks only the
---                               Vector3-arg overload).
--- @return integer  Number of symbols actually registered.
function registerMethodOverloads(assembly, ns, name, method, opts)
    opts = opts or {}
    local sigFilter = opts.sigFilter
    local verbose   = opts.verbose == true

    local args = { assembly = assembly, namespace = ns or "", name = name, method = method }
    if sigFilter and sigFilter ~= "" then args.signatureMatch = sigFilter end

    local resp = rpc("findMethodInstances", args)
    if resp.error or (resp.result and resp.result.error) then
        err("findMethodInstances failed: %s", resp.error or resp.result.error)
        return 0
    end
    local r = resp.result
    if r == nil or r.matches == nil then err("no result"); return 0 end

    local prefix = string.format("Unity:%s:%s%s.m_%s",
        assembly, (ns and ns ~= "" and (ns .. ".") or ""), name, method)
    log("Registering %d overload(s) under '%s__*' (sigFilter=%s)",
        tonumber(r.count) or 0, prefix, tostring(sigFilter or "—"))

    local registered, skipped = 0, 0
    for i, m in ipairs(r.matches or {}) do
        local addr = m.address
        local suffix = m.humanSuffix or ("ovl" .. tostring(i))
        if (not m.compiled) or (not addr) or addr == "0x0" or addr == "" then
            skipped = skipped + 1
            if verbose then warn("skip uncompiled overload: %s%s", method, m.signature or "") end
        else
            local addrInt = tonumber(addr:gsub("^0x", ""), 16)
            if addrInt and addrInt ~= 0 and registerSymbol then
                local sym = prefix .. "__" .. suffix
                registerSymbol(sym, addrInt, true)
                registered = registered + 1
                if verbose then log("  %s = %X  // %s%s", sym, addrInt, method, m.signature or "") end
            end
        end
    end

    if skipped > 0 then
        log("Done: %d overload(s) registered, %d skipped (abstract / uncompilable).",
            registered, skipped)
    else
        log("Done: %d overload(s) registered.", registered)
    end
    return registered
end

--- Phase 22 follow-up: register ONE specific overload under a caller-
--- chosen short symbol name. Same DLL path as registerMethodOverloads
--- (findMethodInstances → mono_compile_method server-side / AOT
--- pointer for IL2CPP) but trims the result to the first match
--- against `sigFilter` and uses the caller's name verbatim instead of
--- the long Unity:Asm:Ns.Cls.m_Method__suffix shape.
---
--- Designed as the runtime target for AA scripts produced by
--- Discrete's UI right-click "Create AA in CE" / "Send AA script to
--- CE Table" when the backend is Mono (no module → AOB scan would be
--- unstable across the JIT's multiple instantiations of the same
--- source method; calling back through the DLL is server-authoritative
--- and overload-precise).
---
--- @param symbolName string  CE symbol name to register under (e.g. "Mathf_Abs_int32").
--- @param assembly   string  Assembly name (e.g. "UnityEngine.CoreModule").
--- @param ns         string  Namespace ("" if global).
--- @param className  string  Class name.
--- @param methodName string  Method name.
--- @param sigFilter  string|nil  Optional substring of the rendered signature to
---                              disambiguate overloads (e.g. "(System.Int32)"
---                              vs "(System.Single)"). Pass nil/empty when
---                              there's only one overload — first compiled
---                              match wins.
--- @return boolean  true on success (symbol registered to a valid JIT address).
function registerMethodSymbol(symbolName, assembly, ns, className, methodName, sigFilter)
    if not symbolName or symbolName == "" then
        err("registerMethodSymbol: symbolName required"); return false
    end
    local args = {
        assembly  = assembly,
        namespace = ns or "",
        name      = className,
        method    = methodName,
    }
    if sigFilter and sigFilter ~= "" then args.signatureMatch = sigFilter end

    local resp = rpc("findMethodInstances", args)
    if resp.error or (resp.result and resp.result.error) then
        err("findMethodInstances failed for %s: %s",
            symbolName, resp.error or resp.result.error)
        return false
    end
    local r = resp.result
    if r == nil or r.matches == nil or #r.matches == 0 then
        err("%s: no overload matched (sigFilter=%s)", symbolName, tostring(sigFilter or "—"))
        return false
    end

    -- Pick the first compiled overload. Multiple matches with a non-
    -- empty sigFilter usually means the filter wasn't unique — log
    -- both addresses so the user can tighten it. With nil filter,
    -- the first match is by design.
    if #r.matches > 1 and sigFilter and sigFilter ~= "" then
        warn("%s: sigFilter '%s' matched %d overloads; using first.",
            symbolName, sigFilter, #r.matches)
        for i, m in ipairs(r.matches) do
            warn("  [%d] %s%s  @ %s", i, methodName,
                m.signature or "", tostring(m.address))
        end
    end

    for _, m in ipairs(r.matches) do
        local addr = m.address
        if m.compiled and addr and addr ~= "0x0" and addr ~= "" then
            local addrInt = tonumber(addr:gsub("^0x", ""), 16)
            if addrInt and addrInt ~= 0 then
                registerSymbol(symbolName, addrInt, true)
                log("%s = %X  // %s%s", symbolName, addrInt,
                    methodName, m.signature or "")
                return true
            end
        end
    end

    err("%s: all matched overloads were uncompiled/abstract", symbolName)
    return false
end

log("Loaded. registerClassSymbols(asm, ns, cls) — bulk class symbols.")
log("        registerMethodOverloads(asm, ns, cls, method [, {sigFilter=...}]) — overload-disambiguated symbols.")
log("        registerMethodSymbol(symName, asm, ns, cls, method [, sigFilter]) — single overload under chosen short name.")
