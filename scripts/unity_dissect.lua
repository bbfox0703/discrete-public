-- ============================================================
-- unity_dissect.lua — query the DLL via the named pipe and feed
--                      the result into CE's Structure Dissect view.
-- ============================================================
--
-- Phase 11. Sibling of `unity_dump.lua`: assumes the DLL is already
-- injected and the pipe is up (run unity_dump.lua first, or have the
-- DiscreteUI.exe inject path do it).
--
-- Usage from CE's Lua console:
--   dofile([[<CE-install>\discrete\unity_dissect.lua]])
--   dissectClass("Assembly-CSharp", "MainGame", "PLY_WORK")
--
-- The script opens the named pipe directly from CE's process, sends a
-- single `getClassInfo` request, parses the response, and creates a
-- structure under "Structures" → "Define new structure".
--
-- Note on JSON: CE Lua doesn't ship a JSON parser. We hand-roll a tiny
-- single-purpose decoder for the known response shape (top-level object,
-- string keys, primitive / nested object / array values, no escape
-- sequences beyond \" / \\). Good enough for the DLL's output; not a
-- general-purpose JSON library.

local TAG = "[UnityCEDumper.dissect]"
local function log(fmt, ...) print(string.format(TAG .. " " .. fmt, ...)) end
local function err(fmt, ...) print(string.format(TAG .. " ERROR " .. fmt, ...)) end

-- ---------- Tiny JSON-ish parser (response-shape-specific) -----------

local function parseValue(s, i)
    local c = s:sub(i, i)
    if c == '"' then
        -- string
        local out, j = {}, i + 1
        while j <= #s do
            local ch = s:sub(j, j)
            if ch == '\\' then
                local nx = s:sub(j + 1, j + 1)
                if nx == '"' or nx == '\\' or nx == '/' then out[#out + 1] = nx
                elseif nx == 'n' then out[#out + 1] = '\n'
                elseif nx == 't' then out[#out + 1] = '\t'
                elseif nx == 'u' then
                    -- \uXXXX — keep the four-hex sequence verbatim; we
                    -- don't decode UTF-16 here because the DLL never emits
                    -- non-ASCII in these field names anyway.
                    out[#out + 1] = '?'
                    j = j + 4
                else out[#out + 1] = nx end
                j = j + 2
            elseif ch == '"' then
                return table.concat(out), j + 1
            else
                out[#out + 1] = ch
                j = j + 1
            end
        end
        error("unterminated string at " .. i)
    elseif c == '{' then
        local t, j = {}, i + 1
        j = j + #(s:sub(j):match("^%s*") or "")
        if s:sub(j, j) == '}' then return t, j + 1 end
        while true do
            j = j + #(s:sub(j):match("^%s*") or "")
            local key; key, j = parseValue(s, j)
            j = j + #(s:sub(j):match("^%s*:%s*") or "")
            local val; val, j = parseValue(s, j)
            t[key] = val
            j = j + #(s:sub(j):match("^%s*") or "")
            local sep = s:sub(j, j)
            if sep == ',' then j = j + 1
            elseif sep == '}' then return t, j + 1
            else error("expected , or } at " .. j) end
        end
    elseif c == '[' then
        local t, j = {}, i + 1
        j = j + #(s:sub(j):match("^%s*") or "")
        if s:sub(j, j) == ']' then return t, j + 1 end
        while true do
            j = j + #(s:sub(j):match("^%s*") or "")
            local val; val, j = parseValue(s, j)
            t[#t + 1] = val
            j = j + #(s:sub(j):match("^%s*") or "")
            local sep = s:sub(j, j)
            if sep == ',' then j = j + 1
            elseif sep == ']' then return t, j + 1
            else error("expected , or ] at " .. j) end
        end
    elseif c == 't' then
        return true, i + 4   -- true
    elseif c == 'f' then
        return false, i + 5  -- false
    elseif c == 'n' then
        return nil, i + 4    -- null  (note: nil entries get dropped)
    else
        -- number
        local m = s:match("^%-?%d+%.?%d*[eE]?[%+%-]?%d*", i)
        if m == nil then error("bad value at " .. i) end
        return tonumber(m), i + #m
    end
end

local function parseJson(s)
    s = s:match("^%s*(.-)%s*$")
    local v = parseValue(s, 1)
    return v
end

-- ---------- Pipe RPC --------------------------------------------------

local PIPE = [[\\.\pipe\UnityDumpBfx]]
local nextId = 0

local function rpc(cmd, args)
    nextId = nextId + 1
    local id = nextId

    -- Hand-build the request line so we don't depend on a JSON encoder.
    local function quote(x) return '"' .. tostring(x):gsub('"', '\\"') .. '"' end
    local argsJson = "{}"
    if args and next(args) ~= nil then
        local parts = {}
        for k, v in pairs(args) do
            local val
            if type(v) == "number" then val = tostring(v)
            elseif type(v) == "boolean" then val = v and "true" or "false"
            else val = quote(v) end
            parts[#parts + 1] = quote(k) .. ":" .. val
        end
        argsJson = "{" .. table.concat(parts, ",") .. "}"
    end
    local line = string.format('{"id":%d,"cmd":%s,"args":%s}', id, quote(cmd), argsJson)

    local f, fe = io.open(PIPE, "r+b")
    if f == nil then
        error("Cannot open pipe " .. PIPE .. ": " .. tostring(fe) ..
              " — run unity_dump.lua first to inject the DLL.")
    end
    f:write(line .. "\n")
    f:flush()
    local resp = f:read("*l")
    f:close()
    if resp == nil then error("empty response for " .. cmd) end
    return parseJson(resp)
end

-- ---------- Public API ------------------------------------------------

--- Pull a class's field list and create a CE Structure Dissect entry.
--- Static fields are skipped (they don't sit inside the instance).
--- @param assembly  string  e.g. "Assembly-CSharp"
--- @param ns        string  empty string for top-level types
--- @param name      string  class simple name (use Outer/Inner for nested)
function dissectClass(assembly, ns, name)
    local resp = rpc("getClassInfo", { assembly = assembly, namespace = ns or "", name = name })
    if resp.error or (resp.result and resp.result.error) then
        err("getClassInfo failed: %s", resp.error or resp.result.error)
        return
    end
    local info = resp.result
    if info == nil then err("no result from getClassInfo"); return end

    local label = string.format("%s!%s%s%s",
        assembly, (ns and ns ~= "" and (ns .. ".") or ""), name, "")
    log("Building structure '%s' (%d fields)", label, #(info.fields or {}))

    -- CE 7.5.x: createStructure returns an unregistered TDissectedStruct.
    -- It only appears in the Structure dissect window after we explicitly
    -- call addToGlobalStructureList() at the end. Drop any prior structure
    -- with the same label first so re-runs don't accumulate duplicates.
    if getStructureCount and getStructure then
        for i = getStructureCount() - 1, 0, -1 do
            local existing = getStructure(i)
            if existing and existing.Name == label and existing.removeFromGlobalStructureList then
                existing.removeFromGlobalStructureList()
            end
        end
    end

    local s = createStructure(label)
    if s == nil then err("createStructure returned nil"); return end

    for _, f in ipairs(info.fields or {}) do
        if not f["static"] then
            local elem = s.addElement()
            elem.Name        = f.name
            elem.Offset      = f.offset
            -- Map managed type → CE Vartype (matching CsxExporter table).
            local vartype, bytesize = "Byte", 1
            local t = f.type
            if     t == "System.Boolean" then vartype, bytesize = "Byte",    1
            elseif t == "System.Byte"    or t == "System.SByte" then vartype, bytesize = "Byte", 1
            elseif t == "System.Int16"   or t == "System.UInt16" or t == "System.Char"
                                          then vartype, bytesize = "2 Bytes", 2
            elseif t == "System.Int32"   or t == "System.UInt32" then vartype, bytesize = "4 Bytes", 4
            elseif t == "System.Int64"   or t == "System.UInt64" then vartype, bytesize = "8 Bytes", 8
            elseif t == "System.Single"  then vartype, bytesize = "Float",   4
            elseif t == "System.Double"  then vartype, bytesize = "Double",  8
            else                              vartype, bytesize = "Pointer", 8
            end
            elem.Vartype  = vartype
            elem.Bytesize = bytesize
        end
    end

    if s.addToGlobalStructureList then
        s.addToGlobalStructureList()
    else
        err("TDissectedStruct.addToGlobalStructureList missing on this CE build — structure won't appear in the dissect menu.")
    end

    log("Done. Open Memory View → Tools → Dissect Data → pick '%s'.", label)
    return s
end

log("Loaded. Call dissectClass('Assembly-CSharp', '', 'YourClass') to build a structure.")
