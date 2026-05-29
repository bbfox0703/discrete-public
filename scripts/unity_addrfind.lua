-- ============================================================
-- unity_addrfind.lua — reverse address lookup via the DLL pipe.
-- ============================================================
--
-- Phase 17d. Sibling of `unity_dissect.lua` and `unity_symbols.lua`:
-- assumes the DLL is already injected and the pipe is up (run
-- `unity_dump.lua` first, or have DiscreteUI.exe / a deployed proxy
-- inject it).
--
-- Usage from CE's Lua console:
--   dofile([[<CE-install>\discrete\unity_addrfind.lua]])
--   -- Direct call — pretty-prints result to the console:
--   lookupAddress(0x000001E55B2518F0)
--   lookupAddress("0x000001E55B2518F0")
--
--   -- Programmatic — get the table back:
--   local hit = whatIsAt(0x000001E55B2518F0)
--   if hit and hit.found and hit.field then
--       print(string.format("%s.%s @ %X",
--           hit.class, hit.field.name, hit.instance))
--   end
--
--   -- Optional: bind a global hotkey. The hotkey grabs the address
--   -- currently selected in CE's memory view (or prompts via
--   -- inputQuery if no memory view is open) and runs lookupAddress.
--   bindAddrLookupHotkey({ VK_CONTROL, VK_F2 })
--
-- The script speaks the same `\\.\pipe\DiscreteBfx` protocol as
-- `unity_dissect.lua` and `unity_symbols.lua`. JSON parser + RPC
-- helper are duplicated here (tiny, response-shape-specific) so this
-- file stays drop-in standalone — no extra `require` needed.

local TAG = "[Discrete.addrfind]"
local function log (fmt, ...) print(string.format(TAG .. " "       .. fmt, ...)) end
local function warn(fmt, ...) print(string.format(TAG .. " WARN "  .. fmt, ...)) end
local function err (fmt, ...) print(string.format(TAG .. " ERROR " .. fmt, ...)) end

-- ---------- Tiny JSON parser (response-shape-specific) -------------------
-- Same shape as unity_dissect.lua's parser. Handles top-level objects with
-- string keys, primitive / nested object / array values. Supports basic
-- escapes (\\, \", \n, \t) and treats \uXXXX as '?' since the DLL never
-- emits non-ASCII in field names / addresses.

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
                elseif nx == 't' then out[#out + 1] = '\t'
                elseif nx == 'u' then out[#out + 1] = '?'; j = j + 4
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

-- ---------- Pipe RPC ----------------------------------------------------
-- Same retry loop + main-thread yield pattern as unity_dissect.lua.

local PIPE   = [[\\.\pipe\DiscreteBfx]]
local nextId = 0

local function rpc(cmd, args)
    if processMessages then processMessages() end
    nextId = nextId + 1
    local id = nextId

    local function quote(x) return '"' .. tostring(x):gsub('"', '\\"') .. '"' end
    local argsJson = "{}"
    if args and next(args) ~= nil then
        local parts = {}
        for k, v in pairs(args) do
            local val
            if     type(v) == "number"  then val = tostring(v)
            elseif type(v) == "boolean" then val = v and "true" or "false"
            else                              val = quote(v) end
            parts[#parts + 1] = quote(k) .. ":" .. val
        end
        argsJson = "{" .. table.concat(parts, ",") .. "}"
    end
    local line = string.format('{"id":%d,"cmd":%s,"args":%s}', id, quote(cmd), argsJson)

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

-- ---------- Address normalisation ---------------------------------------
-- Lua numbers can carry a 64-bit address on CE's bundled Lua build, but
-- the DLL parser also accepts hex strings — sending a string is more
-- robust against accidental float coercion at the high end of the
-- address space.

local function toHexString(addr)
    if type(addr) == "string" then
        local s = addr:gsub("%s", "")
        if s:sub(1, 2):lower() == "0x" then return s end
        if s:match("^[0-9a-fA-F]+$") then return "0x" .. s end
        return s   -- pass through; DLL will reject if malformed
    end
    if type(addr) == "number" then
        return string.format("0x%X", addr)
    end
    error(TAG .. " expected address as number or hex string, got " .. type(addr))
end

-- ---------- Public API --------------------------------------------------

--- Run the `whatIsAt` RPC for one address. Returns the parsed result table
--- (`hit.found`, `hit.instance`, `hit.class`, `hit.field`, `hit.nearestField`,
--- `hit.scannedSteps`, …) or nil on hard error. Soft outcomes (no class
--- in range, address unreadable, …) come back as `hit.found == false`
--- with a `hit.reason` string.
--- @param addr        number|string  Address to look up (e.g. 0x000001E55B2518F0).
--- @param maxBackward number?        Bytes to scan backwards (default DLL = 4096; cap 16384).
--- @return            table?         Parsed result, or nil on error.
function whatIsAt(addr, maxBackward)
    local args = { address = toHexString(addr) }
    if type(maxBackward) == "number" and maxBackward > 0 then
        args.maxBackwardScan = maxBackward
    end
    local resp = rpc("whatIsAt", args)
    if resp == nil then return nil end
    if resp.error then
        err("whatIsAt: %s", resp.error.message or tostring(resp.error))
        return nil
    end
    return resp.result
end

--- Force-rebuild (or pre-warm) the address-finder class-pointer cache.
--- Cheap on subsequent calls — lookup builds the cache implicitly on
--- first use, so this is mostly useful for diagnostics.
--- @param rebuild boolean?  Drop the cache before rebuilding.
--- @return        table?    Stats table from the DLL, or nil on error.
function getClassRangeStats(rebuild)
    local args = {}
    if rebuild then args.rebuild = true end
    local resp = rpc("getClassRangeStats", args)
    if resp == nil or resp.error then return nil end
    return resp.result
end

--- Convenience: run whatIsAt and pretty-print the result to CE's Lua
--- console. Returns the same table as whatIsAt (or nil) so callers can
--- chain further work after printing.
--- @param addr        number|string
--- @param maxBackward number?
--- @return            table?
function lookupAddress(addr, maxBackward)
    local hit = whatIsAt(addr, maxBackward)
    if hit == nil then return nil end
    if not hit.found then
        log("address %s -> NOT FOUND  reason=%s  steps=%s",
            tostring(hit.address or addr),
            tostring(hit.reason or "unknown"),
            tostring(hit.scannedSteps or "?"))
        return hit
    end

    local cls = hit.class or "?"
    if hit["namespace"] and hit["namespace"] ~= "" then
        cls = hit["namespace"] .. "." .. cls
    end
    log("address %s -> instance=%s  class=%s  fieldOffset=%s  (steps=%s)",
        tostring(hit.address or addr),
        tostring(hit.instance or "?"),
        cls,
        tostring(hit.fieldOffset or "?"),
        tostring(hit.scannedSteps or "?"))

    if hit.field then
        local f = hit.field
        log("  field: %s  type=%s  @+0x%X  size=%s  kind=%s  declaredIn=%s",
            tostring(f.name),
            tostring(f.type),
            tonumber(f.offset) or 0,
            tostring(f.size),
            tostring(f.kind),
            tostring(f.declaredIn or "?"))
        if f.kind == "interior" and f.interiorOffset then
            log("    (interior +0x%X within field)", f.interiorOffset)
        end
        if f.isEnum and f.underlying_type then
            log("    (enum, underlying %s)", f.underlying_type)
        end
    elseif hit.nearestField then
        local nf = hit.nearestField
        log("  nearestField: %s  type=%s  @+0x%X  size=%s  delta=%s",
            tostring(nf.name),
            tostring(nf.type),
            tonumber(nf.offset) or 0,
            tostring(nf.size),
            tostring(hit.nearestFieldDelta))
    elseif hit.fieldOffset and hit.fieldOffset < 16 then
        log("  (object header — klass ptr at +0..7, sync block at +8..15)")
    else
        log("  (no declared field at this offset)")
    end
    return hit
end

-- ---------- Memory-view bridge ------------------------------------------
-- Try to read whatever address is currently focused in CE's open memory
-- view. CE's Lua surface for this is older and not perfectly stable
-- across versions, so each path is wrapped in pcall — falls through to
-- inputQuery if none of the well-known accessors are available.

local function tryGetMemViewSelection()
    if getMemoryViewForm == nil then return nil end
    local ok, mvf = pcall(getMemoryViewForm)
    if not ok or mvf == nil then return nil end

    -- Hex view selection — newer CE builds expose SelectionStart on the
    -- HexadecimalView control. Some versions return 0 when no selection;
    -- treat that as "no selection".
    local hv
    do
        local okHv, val = pcall(function() return mvf.HexadecimalView end)
        if okHv then hv = val end
    end
    if hv ~= nil then
        local okSel, sel = pcall(function() return hv.SelectionStart end)
        if okSel and type(sel) == "number" and sel ~= 0 then
            return sel
        end
        -- Some builds expose .TopAddress as the fallback "currently shown"
        -- address. Use it only when no explicit selection.
        local okTop, top = pcall(function() return hv.TopAddress end)
        if okTop and type(top) == "number" and top ~= 0 then
            return top
        end
    end
    return nil
end

--- Fire one lookup based on whatever the user is currently looking at in
--- CE's memory view. If no memory-view selection can be discovered (e.g.
--- the memory view isn't open), pop an inputQuery dialog so the user can
--- paste an address. Wired into the optional hotkey below.
function lookupSelectedAddress()
    local addr = tryGetMemViewSelection()
    if addr == nil then
        if inputQuery == nil then
            err("Cannot read memory view selection and inputQuery isn't available — call lookupAddress(addr) directly.")
            return nil
        end
        local typed = inputQuery(
            "Address Lookup",
            "Memory view has no active selection.\n" ..
            "Paste an address to look up (hex with or without 0x):",
            "")
        if typed == nil or typed == "" then return nil end
        return lookupAddress(typed)
    end
    return lookupAddress(addr)
end

--- Optional: register a global hotkey that fires `lookupSelectedAddress`.
--- @param keys table  Array of CE virtual-key codes (e.g. {VK_CONTROL, VK_F2}).
--- @return     userdata? CE hotkey handle on success, nil on failure.
function bindAddrLookupHotkey(keys)
    if createHotkey == nil then
        err("createHotkey isn't available on this CE build — hotkey binding skipped.")
        return nil
    end
    if type(keys) ~= "table" or #keys == 0 then
        err("bindAddrLookupHotkey: expected a non-empty array of virtual-key codes.")
        return nil
    end
    local ok, hk = pcall(createHotkey, lookupSelectedAddress, table.unpack(keys))
    if not ok or hk == nil then
        err("createHotkey failed: %s", tostring(hk))
        return nil
    end
    log("hotkey registered — press the bound combo from anywhere to look up the memory-view selection.")
    return hk
end

log("Loaded. lookupAddress(addr) / whatIsAt(addr) / bindAddrLookupHotkey({VK_CONTROL, VK_F2}).")
