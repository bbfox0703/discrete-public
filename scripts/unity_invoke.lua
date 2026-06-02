-- ============================================================
-- unity_invoke.lua — call managed Unity methods via the DLL pipe.
-- ============================================================
--
-- Phase 19e. Sibling of `unity_symbols.lua`. Closes the long-standing
-- CLAUDE.md goal:
-- invoke managed methods (Mono JIT or IL2CPP AOT) from CE Lua with
-- marshaled args + typed return values + managed-exception capture.
--
-- Wire spec: docs/lua-invoke.md. Same `\\.\pipe\DiscreteBfx` protocol
-- as the sibling scripts; assumes the DLL is already injected (run
-- `unity_dump.lua` first, or have DiscreteUI.exe / a deployed proxy
-- inject it).
--
-- Quick start
-- -----------
-- dofile([[<CE-install>\discrete\unity_invoke.lua]])
--
-- -- Static call (Mathf.Abs):
-- local r = callUnityMethod(
--     "UnityEngine.CoreModule", "UnityEngine", "Mathf", "Abs",
--     { argInt32(-7) },
--     { signatureMatch = "(System.Int32)" })
-- print(r.ok, r.returnValue, r.returnType)  -- true, 7, System.Int32
--
-- -- Static call with a string arg + string return (String.Concat):
-- local r = callUnityMethod(
--     "mscorlib", "System", "String", "Concat",
--     { argString("Hello, "), argString("World") },
--     { signatureMatch = "(System.String, System.String)" })
-- print(r.returnValue)  -- "Hello, World"
--
-- -- Instance call (no args, returns uint):
-- local r = callUnityMethod(
--     "Assembly-CSharp", "", "ItemBundle", "get_numberOfItems",
--     {},
--     { instance = "0x16F8CEA0BE0", signatureMatch = "()" })
-- print(r.returnValue)  -- 1
--
-- -- Managed exception (Convert.ToByte(-1)):
-- local r = callUnityMethod(
--     "mscorlib", "System", "Convert", "ToByte",
--     { argInt32(-1) },
--     { signatureMatch = "(System.Int32)" })
-- print(r.ok, r.exception)
-- --> false, "System.OverflowException: Value was either too large or too..."
--
-- API
-- ---
--   callUnityMethod(asm, ns, class, method, args, opts) -> result
--       opts = { instance = "0x...", signatureMatch = "(System.Int32)" }
--       result = { ok, returnType, returnValue, exception, error,
--                  elapsedMs, method = { address, signature, humanSuffix } }
--
--   pcallUnityMethod(...)  -> ok, result    -- never throws; ok=false on RPC error
--
--   findOverloads(asm, ns, class, method, sigMatch?) -> { matches[], count }
--       wraps findMethodInstances; useful to prep a signatureMatch.
--
--   -- Phase 21b — Inspector field write-back (primitives + raw address).
--   setField(fieldAddr, wireType, value)                     -> result
--   setInstanceField(instanceAddr, fieldOffset, wireType, value) -> result
--       result = { ok, address, fieldOffset, fieldAddress, type, written, error, ... }
--
-- Arg builders (pass these inside the `args` table):
--   argNull()             argBool(b)         argInt8(v)   argByte(v)
--   argInt16(v)           argUInt16(v)       argInt32(v)  argUInt32(v)
--   argInt64(v)           argUInt64(v)       argFloat(v)  argDouble(v)
--   argChar(c)            argString(s)       argAddress(hexStringOrNumber)
--
-- Result helpers:
--   asAddress(v)   "0x..." | number -> Lua number
--   prettyPrint(r) log a one-paragraph human-readable summary
--
-- See docs/lua-invoke.md for the full type-tag table, error-code list,
-- GC-root caveat, and the marshaling convention shared with the UI /
-- PowerShell smoke.

local TAG = "[Discrete.invoke]"
local function log (fmt, ...) print(string.format(TAG .. " "       .. fmt, ...)) end
local function warn(fmt, ...) print(string.format(TAG .. " WARN "  .. fmt, ...)) end
local function err (fmt, ...) print(string.format(TAG .. " ERROR " .. fmt, ...)) end

-- ---------- Tiny JSON parser (response-shape-specific) ------------------
-- Minimal standalone parser (kept inline so this file stays drop-in).
-- Handles the response shapes
-- callMethod / findMethodInstances emit: top-level objects with string
-- keys, primitive / nested object / array values, basic escapes.

local function parseValue(s, i)
    local c = s:sub(i, i)
    if c == '"' then
        local out, j = {}, i + 1
        while j <= #s do
            local ch = s:sub(j, j)
            if ch == '\\' then
                local nx = s:sub(j + 1, j + 1)
                if     nx == '"' or nx == '\\' or nx == '/' then out[#out + 1] = nx
                elseif nx == 'n' then out[#out + 1] = '\n'
                elseif nx == 'r' then out[#out + 1] = '\r'
                elseif nx == 't' then out[#out + 1] = '\t'
                elseif nx == 'u' then
                    -- Decode \uXXXX → UTF-8. Method bodies that round-trip
                    -- managed-string returns may carry non-ASCII (e.g.
                    -- Vector3.ToString uses culture-formatted floats with
                    -- non-breaking-space separators on some locales).
                    local hex = s:sub(j + 2, j + 5)
                    local cp = tonumber(hex, 16) or 0xFFFD
                    if     cp < 0x80 then
                        out[#out + 1] = string.char(cp)
                    elseif cp < 0x800 then
                        out[#out + 1] = string.char(
                            0xC0 + (cp >> 6),
                            0x80 + (cp & 0x3F))
                    else
                        out[#out + 1] = string.char(
                            0xE0 + (cp >> 12),
                            0x80 + ((cp >> 6) & 0x3F),
                            0x80 + (cp & 0x3F))
                    end
                    j = j + 4
                else                  out[#out + 1] = nx end
                j = j + 2
            elseif ch == '"' then
                return table.concat(out), j + 1
            else
                out[#out + 1] = ch; j = j + 1
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
            local val; val, j = parseValue(s, j); t[key] = val
            j = j + #(s:sub(j):match("^%s*") or "")
            local sep = s:sub(j, j)
            if     sep == ',' then j = j + 1
            elseif sep == '}' then return t, j + 1
            else error("expected , or } at " .. j) end
        end
    elseif c == '[' then
        local t, j = {}, i + 1
        j = j + #(s:sub(j):match("^%s*") or "")
        if s:sub(j, j) == ']' then return t, j + 1 end
        while true do
            j = j + #(s:sub(j):match("^%s*") or "")
            local val; val, j = parseValue(s, j); t[#t + 1] = val
            j = j + #(s:sub(j):match("^%s*") or "")
            local sep = s:sub(j, j)
            if     sep == ',' then j = j + 1
            elseif sep == ']' then return t, j + 1
            else error("expected , or ] at " .. j) end
        end
    elseif c == 't' then return true,  i + 4
    elseif c == 'f' then return false, i + 5
    elseif c == 'n' then return nil,   i + 4
    else
        local m = s:match("^%-?%d+%.?%d*[eE]?[%+%-]?%d*", i)
        if m == nil then error("bad value at " .. i) end
        return tonumber(m), i + #m
    end
end

local function parseJson(s)
    s = s:match("^%s*(.-)%s*$")
    return (parseValue(s, 1))
end

-- ---------- JSON serializer (callMethod has nested args array) ----------
-- Sibling scripts only send flat key-value request args, so they get away
-- with hand-built `{"k":v}` strings. callMethod's `args` is an array of
-- {type, value} objects, so we need a real serializer that handles arrays
-- vs objects + nested values. Kept tiny and request-shape-specific —
-- doesn't try to handle every JSON edge case the parser does.

local escapeMap = {
    ['\\'] = '\\\\', ['"']  = '\\"',  ['\n'] = '\\n',
    ['\r'] = '\\r',  ['\t'] = '\\t',  ['\b'] = '\\b',
    ['\f'] = '\\f',
}

local function escapeStr(s)
    local out = s:gsub('[\\"%c]', function(c)
        local m = escapeMap[c]
        if m then return m end
        return string.format('\\u%04X', string.byte(c))
    end)
    return '"' .. out .. '"'
end

local serialize  -- fwd-decl

-- Detect "array-shaped" Lua tables: 1..#t with no string keys. Empty
-- table is treated as object {}; callers needing an empty array build
-- it explicitly via `{ [0]=true }` workaround — but in practice we never
-- send an empty array (empty `args` is omitted entirely).
local function isArray(t)
    local n = 0
    for k, _ in pairs(t) do
        if type(k) ~= "number" then return false end
        if k > n then n = k end
    end
    return n == #t
end

serialize = function(v)
    local ty = type(v)
    if v == nil           then return "null" end
    if ty == "boolean"    then return v and "true" or "false" end
    if ty == "number"     then
        -- Avoid scientific notation for integer-valued numbers — keeps the
        -- wire output readable when the user inspects the request line in
        -- a log file. Lua's default tostring(1e6) → "1000000" but tostring
        -- of large ints can lose precision; %d for integers, %g for floats.
        if v == math.floor(v) and math.abs(v) < 1e15 then
            return string.format("%d", v)
        end
        return tostring(v)
    end
    if ty == "string"     then return escapeStr(v) end
    if ty == "table" then
        if next(v) == nil then return "{}" end
        if isArray(v) then
            local parts = {}
            for i = 1, #v do parts[i] = serialize(v[i]) end
            return "[" .. table.concat(parts, ",") .. "]"
        end
        local parts = {}
        for k, val in pairs(v) do
            parts[#parts + 1] = escapeStr(tostring(k)) .. ":" .. serialize(val)
        end
        return "{" .. table.concat(parts, ",") .. "}"
    end
    error(TAG .. " cannot serialize " .. ty)
end

-- ---------- Pipe RPC ----------------------------------------------------
-- Same retry scaffolding + main-thread yield as the sibling scripts.

local PIPE   = [[\\.\pipe\DiscreteBfx]]
local nextId = 0

local function rpc(cmd, args)
    if processMessages then processMessages() end
    nextId = nextId + 1
    local id = nextId

    local argsJson = (args == nil or next(args) == nil) and "{}" or serialize(args)
    local line = string.format('{"id":%d,"cmd":%s,"args":%s}',
                               id, escapeStr(cmd), argsJson)

    local f, fe
    for _ = 1, 15 do
        f, fe = io.open(PIPE, "r+b")
        if f then break end
        if sleep then sleep(200) end
    end
    if f == nil then
        error(TAG .. " cannot open pipe " .. PIPE .. ": " .. tostring(fe) ..
              " — run unity_dump.lua first to inject the DLL.")
    end

    f:write(line .. "\n")
    f:flush()
    local resp = f:read("*l")
    f:close()
    if resp == nil then error(TAG .. " empty response for " .. cmd) end
    return parseJson(resp)
end

-- ---------- Arg builders ------------------------------------------------
-- One per wire `type` tag from docs/lua-invoke.md. These keep callers from
-- hand-typing `{type="int32", value=...}` for every arg — both clearer at
-- the call site and harder to typo.

function argNull()                  return { type = "null"   } end
function argBool(v)                 return { type = "bool",   value = v and true or false } end
function argByte(v)                 return { type = "byte",   value = v } end
function argInt8(v)                 return { type = "sbyte",  value = v } end  -- alias for clarity
function argSByte(v)                return { type = "sbyte",  value = v } end
function argInt16(v)                return { type = "int16",  value = v } end
function argUInt16(v)               return { type = "uint16", value = v } end
function argInt32(v)                return { type = "int32",  value = v } end
function argUInt32(v)               return { type = "uint32", value = v } end
function argInt64(v)                return { type = "int64",  value = v } end
function argUInt64(v)               return { type = "uint64", value = v } end
function argFloat(v)                return { type = "float",  value = v } end
function argDouble(v)               return { type = "double", value = v } end
function argChar(v)
    -- Accept either a 1-char string or a numeric code-unit. The DLL handler
    -- accepts both forms, but normalising on the Lua side keeps the JSON
    -- compact and removes one source of "char came through as 0".
    if type(v) == "string" and #v == 1 then
        return { type = "char", value = string.byte(v) }
    end
    return { type = "char", value = v }
end
function argString(s)
    return { type = "string", value = tostring(s) }
end

-- Address arg — accepts a hex string ("0x..." or bare hex) or a Lua
-- number. Internally normalised to "0x..." per the DLL's parseAddr
-- preference.
function argAddress(v)
    local s
    if     type(v) == "number" then s = string.format("0x%X", v)
    elseif type(v) == "string" then
        v = v:gsub("%s", "")
        if v:sub(1, 2):lower() == "0x" then s = v
        else                                 s = "0x" .. v end
    else
        error(TAG .. " argAddress: expected number or hex string, got " .. type(v))
    end
    return { type = "address", value = s }
end

-- ---------- Public API --------------------------------------------------

--- Invoke a managed method. Returns the parsed result table; throws on
--- pipe / dispatch errors (use `pcallUnityMethod` for non-throwing form).
---
--- @param assembly       string            e.g. "Assembly-CSharp"
--- @param namespace      string            e.g. "MainGame" (empty for global)
--- @param class          string            e.g. "PLY_WORK"
--- @param method         string            e.g. "AddGrowHpMax"
--- @param args           table             array of arg* builders' tables
--- @param opts           table?            { instance = "0x...", signatureMatch = "..." }
--- @return               table             { ok, returnType, returnValue, exception, error, elapsedMs, method }
function callUnityMethod(assembly, namespace, class, method, args, opts)
    if assembly == nil or class == nil or method == nil then
        error(TAG .. " callUnityMethod: assembly, class, method are required")
    end
    if args ~= nil and type(args) ~= "table" then
        error(TAG .. " callUnityMethod: args must be a table (or nil)")
    end
    opts = opts or {}

    local body = {
        assembly  = assembly,
        ["namespace"] = namespace or "",
        name      = class,
        method    = method,
        args      = args or {},
    }
    if opts.signatureMatch and opts.signatureMatch ~= "" then
        body.signatureMatch = opts.signatureMatch
    end
    if opts.instance and opts.instance ~= "" then
        -- Accept number too — normalise to hex string.
        if type(opts.instance) == "number" then
            body.instance = string.format("0x%X", opts.instance)
        else
            body.instance = tostring(opts.instance)
        end
    end

    local resp = rpc("callMethod", body)
    if resp.error then
        error(TAG .. " pipe error " .. tostring(resp.error.code or "?") ..
              ": " .. tostring(resp.error.message or "?"))
    end
    return resp.result or {}
end

--- Non-throwing wrapper. Returns (true, result) on success, (false,
--- errMessage) on pipe / dispatch failure. Managed exceptions still come
--- back as ok=true with `result.ok=false, result.exception="Type: msg"`.
function pcallUnityMethod(assembly, namespace, class, method, args, opts)
    local ok, r = pcall(callUnityMethod, assembly, namespace, class, method, args, opts)
    if not ok then return false, r end
    return true, r
end

--- Wrap `findMethodInstances` so users can prep a signatureMatch from CE
--- Lua before calling. Returns the parsed `{matches, count, error}`
--- payload. Useful when an overload disambiguation isn't obvious — e.g.
--- `Mathf.Abs` has 6 overloads; this lets you list them and pick.
function findOverloads(assembly, namespace, class, method, sigMatch)
    if assembly == nil or class == nil or method == nil then
        error(TAG .. " findOverloads: assembly, class, method are required")
    end
    local body = {
        assembly  = assembly,
        ["namespace"] = namespace or "",
        name      = class,
        method    = method,
    }
    if sigMatch and sigMatch ~= "" then body.signatureMatch = sigMatch end
    local resp = rpc("findMethodInstances", body)
    if resp.error then
        error(TAG .. " pipe error: " .. tostring(resp.error.message or "?"))
    end
    return resp.result or {}
end

-- ---------- Result helpers ---------------------------------------------

--- Convert a returnValue (or an arg) that's an address to a Lua number.
--- Accepts a "0x..." hex string, bare hex string, or already-a-number.
--- Returns nil for null / empty / unparseable input.
function asAddress(v)
    if type(v) == "number" then return v end
    if type(v) == "string" then
        if v == "" then return nil end
        if v:sub(1, 2):lower() == "0x" then return tonumber(v:sub(3), 16) end
        return tonumber(v, 16)
    end
    return nil
end

--- Pretty-print a callUnityMethod result to CE's Lua console. Returns
--- the same table so callers can `prettyPrint(callUnityMethod(...))`.
function prettyPrint(r)
    if r == nil then log("(no result)"); return r end
    if r.error and r.error ~= "" then
        err("call failed: %s", tostring(r.error))
        return r
    end
    local m = r.method or {}
    local addr = m.address or "?"
    local sig  = m.signature or "?"
    if r.exception and r.exception ~= "" then
        err("managed exception @ %s%s  (%sms)", addr, sig, tostring(r.elapsedMs or 0))
        err("  %s", r.exception)
        return r
    end

    local rv = r.returnValue
    local rt = r.returnType or "?"
    local rendered
    if rv == nil then
        rendered = "(null)"
    elseif type(rv) == "number" then
        if rv == math.floor(rv) and math.abs(rv) < 1e15 then
            rendered = string.format("%d", rv)
        else
            rendered = tostring(rv)
        end
    elseif type(rv) == "boolean" then
        rendered = rv and "true" or "false"
    else
        rendered = tostring(rv)
    end

    log("%s%s -> %s = %s  (%sms)",
        m.humanSuffix or "?",
        sig,
        rt,
        rendered,
        tostring(r.elapsedMs or 0))
    if r.note and r.note ~= "" then warn("note: %s", r.note) end
    return r
end

-- ---------- Phase 21b — Inspector field write-back ---------------------
--
-- setField / setInstanceField wrap the new setInstanceField pipe RPC
-- (spec: docs/pipe-protocol.md §"setInstanceField"). Inverse of the
-- per-field read in walkInstance — given a field's storage address +
-- wire type + a typed value, writes sizeof(type) bytes through the DLL's
-- SEH-protected SafeWriteBytes.
--
-- v1 scope: primitives (bool / int8..int64 / uint8..uint64 / float /
-- double / char) plus raw address. Reference-type writes
-- (System.String, object pointer) are deferred to v2 — they need
-- mono_string_new / il2cpp_string_new plus proper GC-root marshaling.
--
-- The DLL accepts both shortform wire tags ("int32") and CLR-style names
-- ("System.Int32") — pass whatever the walkInstance row gave you.
--
-- Quick start:
--   -- Write 999 into a field at +0x28 of an instance:
--   local r = setInstanceField("0x1F8CEA0BE0", 0x28, "int32", 999)
--   if r.ok then log("wrote %d bytes", r.written)
--   else      err("write failed: %s", r.error or "?") end
--
--   -- When you already have the field's storage address (e.g. from a
--   -- walkInstance row's `address`), use setField(addr, type, value):
--   local r = setField("0x1F8CEA0C08", "float", 3.14)
--
--   -- Boolean: 0/1, true/false, "true"/"false" all accepted:
--   setField("0x1234", "bool", true)
--
--   -- Address (raw 8-byte pointer write):
--   setField("0x1234", "address", "0x00007FFE12345678")
--
-- API
-- ---
--   setField(fieldAddr, type, value)                        -> result
--   setInstanceField(instanceAddr, fieldOffset, type, value) -> result
--
-- result = { ok, address, fieldOffset, fieldAddress, type, written, error, size, hint }
--   - ok        true on success, false otherwise
--   - written   bytes actually written on success
--   - error     soft-error message ("memory not writable" / "'value' out
--               of range for ..." / "unsupported type '...'"). Pipe-level
--               errors throw via the underlying rpc() call; managed
--               exceptions don't apply (no managed code runs).

--- Normalise an address argument to the "0x..." hex string the DLL
--- expects. Accepts a hex string (with or without 0x prefix) or a Lua
--- number.
local function _normalizeAddr(v, label)
    if type(v) == "number" then return string.format("0x%X", v) end
    if type(v) == "string" then
        v = v:gsub("%s", "")
        if v:sub(1, 2):lower() == "0x" then return v end
        return "0x" .. v
    end
    error(TAG .. " " .. label .. ": expected address (number or hex string), got " .. type(v))
end

--- Normalise the JSON value sent over the wire based on the wire type.
--- Same per-type rules as the DLL handler's marshaling — keeping the
--- conversion on the Lua side means error messages surface immediately
--- instead of round-tripping through a "memory not writable" reply.
local function _wireValueFor(wireType, v)
    if type(wireType) ~= "string" then
        error(TAG .. " setField: 'type' must be a string, got " .. type(wireType))
    end
    local t = wireType:lower()
    if t == "bool" or t == "boolean" or t == "system.boolean" then
        if type(v) == "boolean" then return v end
        if type(v) == "number"  then return v ~= 0 end
        if type(v) == "string"  then
            local s = v:lower()
            if s == "true"  or s == "1" then return true  end
            if s == "false" or s == "0" then return false end
        end
        error(TAG .. " setField: bad bool value: " .. tostring(v))
    end
    -- Numeric primitives — pass the Lua number through; the DLL clamps
    -- to the type's range and returns a soft error if out-of-range.
    if t == "byte"   or t == "uint8"  or t == "system.byte"
    or t == "sbyte"  or t == "int8"   or t == "system.sbyte"
    or t == "int16"  or t == "short"  or t == "system.int16"
    or t == "uint16" or t == "ushort" or t == "system.uint16"
    or t == "int32"  or t == "int"    or t == "system.int32"
    or t == "uint32" or t == "uint"   or t == "system.uint32"
    or t == "int64"  or t == "long"   or t == "system.int64"
    or t == "uint64" or t == "ulong"  or t == "system.uint64"
    or t == "float"  or t == "single" or t == "system.single"
    or t == "double" or t == "system.double" then
        if type(v) ~= "number" then
            -- Tolerate a numeric string ("999"). Reject anything else.
            local n = tonumber(v)
            if n == nil then
                error(TAG .. " setField: value for " .. wireType .. " must be a number, got " .. type(v))
            end
            return n
        end
        return v
    end
    if t == "char" or t == "system.char" then
        if type(v) == "string" and #v == 1 then return string.byte(v) end
        if type(v) == "number" then return v end
        error(TAG .. " setField: char value must be a single-char string or numeric code unit")
    end
    if t == "address" or t == "intptr" or t == "uintptr" or t == "ptr"
    or t == "system.intptr" or t == "system.uintptr" then
        return _normalizeAddr(v, "setField address value")
    end
    -- Unknown type — let the DLL reject it; caller still gets a soft
    -- error rather than a Lua throw, matching the rest of this script.
    return v
end

--- Write a primitive value to an instance field. Combines the
--- `instanceAddress + fieldOffset = fieldAddress` indirection that
--- mirrors the wire RPC shape — pass either form (set offset=0 if the
--- address you already have IS the field storage).
---
--- @param instanceAddr  number|string  instance start address
--- @param fieldOffset   integer        cumulative bytes from the instance base (0 if pre-computed)
--- @param wireType      string         "int32" / "float" / "bool" / "address" / ...
--- @param value         any            typed per the wireType (see _wireValueFor)
--- @return              table          { ok, address, fieldOffset, fieldAddress, type, written, error, ... }
function setInstanceField(instanceAddr, fieldOffset, wireType, value)
    if instanceAddr == nil then
        error(TAG .. " setInstanceField: instanceAddr is required")
    end
    if type(fieldOffset) ~= "number" then
        error(TAG .. " setInstanceField: fieldOffset must be a number")
    end
    local body = {
        address     = _normalizeAddr(instanceAddr, "setInstanceField address"),
        fieldOffset = fieldOffset,
        type        = wireType,
        value       = _wireValueFor(wireType, value),
    }
    local resp = rpc("setInstanceField", body)
    if resp.error then
        error(TAG .. " pipe error " .. tostring(resp.error.code or "?") ..
              ": " .. tostring(resp.error.message or "?"))
    end
    return resp.result or {}
end

--- Convenience wrapper for the "I already have the field's storage
--- address" path — pass fieldOffset=0 implicitly. Most CE Lua callers
--- will use this form because the inspector / address-finder hand them
--- the exact field address.
---
--- @param fieldAddr  number|string  storage address of the field
--- @param wireType   string         "int32" / "float" / "bool" / "address" / ...
--- @param value      any            typed per the wireType
--- @return           table          same as setInstanceField
function setField(fieldAddr, wireType, value)
    return setInstanceField(fieldAddr, 0, wireType, value)
end

log("Loaded. callUnityMethod(asm, ns, class, method, args, opts) — see header comment for examples.")
