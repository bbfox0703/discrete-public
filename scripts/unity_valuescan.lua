-- ============================================================
-- unity_valuescan.lua — Unity-aware value search via the DLL pipe.
-- ============================================================
--
-- Phase 27e. Sibling of `unity_addrfind.lua` / `unity_symbols.lua`:
-- assumes the DLL is already injected (DiscreteUI.exe / proxy DLL /
-- unity_dump.lua first).
--
-- Workflow:
--   1) `firstValueScan(value, dataType)` — initial scan, returns sessionId
--      + first 50 candidates.
--   2) `nextValueScan(sessionId, scanType, value?)` — refine: filter the
--      session per scanType against this round's read values. scanType =
--      "exact" | "range" | "changed" | "unchanged" | "increased" |
--      "decreased" | "newValue".
--   3) `endValueScan(sessionId)` — drop the session.
--
-- Quick examples (from CE's Lua console):
--   dofile([[<CE-install>\discrete\unity_valuescan.lua]])
--
--   local r = firstValueScan(100, "int32")            -- find every Int32 == 100
--   print(string.format("session=%s candidates=%d", r.sessionId, r.count))
--
--   -- change a value in-game, then narrow:
--   local r2 = nextValueScan(r.sessionId, "changed")
--   for _, row in ipairs(r2.candidates) do
--       print(string.format("  %s.%s = %s (%s)",
--           row.class, row.field or "(unk)", row.currentValue, row.addr))
--   end
--   endValueScan(r.sessionId)
--
--   -- One-shot (no session) — useful for "find this string":
--   findString("GameOver")
--
--   -- Optional hotkey:
--   bindValueScanHotkey({ VK_CONTROL, VK_F3 })

local TAG = "[UnityCEDumper.valuescan]"
local function log (fmt, ...) print(string.format(TAG .. " "       .. fmt, ...)) end
local function warn(fmt, ...) print(string.format(TAG .. " WARN "  .. fmt, ...)) end
local function err (fmt, ...) print(string.format(TAG .. " ERROR " .. fmt, ...)) end

-- ---------- Tiny JSON parser (matches unity_addrfind.lua) ----------------
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
            local v; v, j = parseValue(s, j); t[#t + 1] = v
            j = j + #(s:sub(j):match("^%s*") or "")
            local sep = s:sub(j, j)
            if     sep == ',' then j = j + 1
            elseif sep == ']' then return t, j + 1
            else error("expected , or ] at " .. j) end
        end
    else
        local num = s:sub(i):match("^%-?%d+%.?%d*[eE]?[%+%-]?%d*")
        if num and num ~= "" then return tonumber(num), i + #num end
        if s:sub(i, i + 3) == "true"  then return true,  i + 4 end
        if s:sub(i, i + 4) == "false" then return false, i + 5 end
        if s:sub(i, i + 3) == "null"  then return nil,   i + 4 end
        error("unexpected char at " .. i .. ": " .. c)
    end
end

local function parseJson(s)
    local i = 1 + #(s:match("^%s*") or "")
    local v = parseValue(s, i)
    return v
end

local function quote(s)
    return '"' .. tostring(s):gsub('\\','\\\\'):gsub('"','\\"'):gsub('\n','\\n') .. '"'
end

-- ---------- Pipe RPC -----------------------------------------------------
local PIPE = [[\\.\pipe\UnityDumpBfx]]
local nextId = 1
local function rpc(cmd, args)
    local id = nextId; nextId = nextId + 1
    local argsJson = "{}"
    if args then
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
        error(TAG .. " cannot open pipe " .. PIPE .. ": " .. tostring(fe))
    end
    f:write(line .. "\n"); f:flush()
    local resp = f:read("*l"); f:close()
    if resp == nil then error(TAG .. " empty response for " .. cmd) end
    return parseJson(resp)
end

-- ---------- Public API ---------------------------------------------------

--- First Scan — open a session.
--- @param value     number|string  initial search value
--- @param dataType  string?        wire dataType, default "int32"
--- @param opts      table?         { scanType="exact"|"range", valueTo=N, limit=200, tolerance=0.0001 }
--- @return          table          { ok, sessionId, count, candidates, ... }
function firstValueScan(value, dataType, opts)
    opts = opts or {}
    local args = {
        dataType = dataType or "int32",
        scanType = opts.scanType or "exact",
        value    = value,
        limit    = opts.limit or 200,
    }
    if opts.valueTo   ~= nil then args.valueTo   = opts.valueTo   end
    if opts.tolerance ~= nil then args.tolerance = opts.tolerance end
    if opts.maxBackwardScan ~= nil then args.maxBackwardScan = opts.maxBackwardScan end
    local resp = rpc("beginValueScan", args)
    if resp.error then err("firstValueScan: %s", resp.error.message); return nil end
    return resp.result
end

--- Next Scan — refine an existing session by scanType.
--- @param sessionId string|number  session ID returned by firstValueScan
--- @param scanType  string         "exact"|"range"|"changed"|"unchanged"|"increased"|"decreased"|"newValue"
--- @param value     number?        required for exact/range/newValue
--- @param valueTo   number?        upper bound for range
--- @return          table          { ok, count, totalCandidates, removed, candidates, ... }
function nextValueScan(sessionId, scanType, value, valueTo)
    local args = {
        sessionId = tostring(sessionId),
        scanType  = scanType,
        limit     = 50,
    }
    if value   ~= nil then args.value   = value   end
    if valueTo ~= nil then args.valueTo = valueTo end
    local resp = rpc("refineValueScan", args)
    if resp.error then err("nextValueScan: %s", resp.error.message); return nil end
    return resp.result
end

--- End session — frees the candidate buffer on the DLL side.
function endValueScan(sessionId)
    local resp = rpc("endValueScan", { sessionId = tostring(sessionId) })
    if resp.error then err("endValueScan: %s", resp.error.message); return nil end
    return resp.result
end

--- One-shot numeric scan (no session). Returns immediate result.
--- @param value     number
--- @param dataType  string?
--- @return          table?  { candidates = {…}, count, droppedNonManaged, ... }
function scanForValue(value, dataType)
    local resp = rpc("scanFieldValue", {
        dataType = dataType or "int32",
        scanType = "exact",
        value    = value,
        limit    = 200,
    })
    if resp.error then err("scanForValue: %s", resp.error.message); return nil end
    return resp.result
end

--- One-shot string scan. Useful for finding NPC names / item names / etc.
--- @param needle    string
--- @param encoding  string?  "utf16"|"utf8"|"ascii" (default utf16)
--- @param matchMode string?  "contains"|"exact" (default contains)
--- @return          table?   { candidates = {…} }
function findString(needle, encoding, matchMode)
    local resp = rpc("scanFieldValue", {
        dataType  = "string",
        value     = needle,
        encoding  = encoding  or "utf16",
        matchMode = matchMode or "contains",
        limit     = 200,
    })
    if resp.error then err("findString: %s", resp.error.message); return nil end
    return resp.result
end

--- Pretty-print a scan result table to CE's console.
function printScanResult(r, header)
    if r == nil then return end
    if header then log("%s", header) end
    log("  count=%d  total=%s  truncated=%s  dropped=%s",
        r.count or 0, tostring(r.totalCandidates or r.count or 0),
        tostring(r.truncated), tostring(r.droppedNonManaged or 0))
    local shown = 0
    for _, row in ipairs(r.candidates or {}) do
        local cls = row.ns ~= nil and row.ns ~= "" and (row.ns .. "." .. row.class) or row.class
        log("  [%2d] %s +0x%X %s = %s  (%s %s)",
            shown, tostring(row.addr), row.fieldOffset or 0,
            cls or "(unknown)", tostring(row.currentValue),
            row.fieldKind or "?", row.field or "")
        shown = shown + 1
        if shown >= 12 then log("  ... %d more", #r.candidates - shown); break end
    end
end

-- ---------- Hotkey binding (optional) ------------------------------------
local _hotkey = nil
function bindValueScanHotkey(keys, value, dataType)
    if _hotkey ~= nil then
        pcall(function() _hotkey.destroy() end); _hotkey = nil
    end
    _hotkey = createHotkey(function()
        local v = value
        if v == nil then
            local s = inputQuery("Value scan", "Value to search for:", "")
            if s == nil or s == "" then return end
            v = tonumber(s) or s
        end
        local r = firstValueScan(v, dataType or "int32")
        printScanResult(r, string.format("firstValueScan(%s, %s)",
                                          tostring(v), dataType or "int32"))
        if r and r.sessionId then
            log("  -> session %s (use nextValueScan + endValueScan)", r.sessionId)
        end
    end, table.unpack(keys))
    log("hotkey bound (keys = %s)", table.concat(keys, "+"))
end

log("unity_valuescan ready (firstValueScan / nextValueScan / endValueScan / findString)")
