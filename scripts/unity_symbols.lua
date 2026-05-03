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
--
-- Naming convention (Architecture.md §"Symbol Registration Pattern"):
--   Unity:{Asm}:{Ns}.{Cls}.s_{Field}    -> static field absolute address
--   Unity:{Asm}:{Ns}.{Cls}::{Method}    -> method native address (when known)
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
    local f, fe = io.open(PIPE, "r+b")
    if f == nil then error("pipe open: " .. tostring(fe)) end
    f:write(line .. "\n"); f:flush()
    local resp = f:read("*l"); f:close()
    return parseValue(resp, 1)
end

-- ---------- Symbol registration ---------------------------------------

--- Register CE symbols for every static field and method of the given
--- class. Returns counts so callers can verify how much landed.
function registerClassSymbols(assembly, ns, name)
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
    log("Registering symbols under '%s.*'", prefix)

    local statics, methods = 0, 0

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

    -- 2. Methods — IL2CPP only; Mono needs an explicit JIT compile call
    -- which we skip here. The address comes back from getMethodInfo.
    for _, m in ipairs(info.methods or {}) do
        if m.address and m.address ~= "0x0" and m.address ~= "" then
            local sym = prefix .. "::" .. m.name
            local addrInt = tonumber(m.address:gsub("^0x", ""), 16)
            if addrInt and addrInt ~= 0 and registerSymbol then
                registerSymbol(sym, addrInt, true)
                methods = methods + 1
            end
        end
    end

    log("Done: %d static-field + %d method symbols.", statics, methods)
    return statics, methods
end

log("Loaded. Call registerClassSymbols('Assembly-CSharp', '', 'YourClass') to register.")
