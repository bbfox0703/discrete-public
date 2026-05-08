-- ============================================================
-- unity_scene.lua — Scene / GameObject explorer over the DLL pipe.
-- ============================================================
--
-- Phase 20a. Sibling of `unity_invoke.lua`, `unity_addrfind.lua`,
-- `unity_dissect.lua`, `unity_symbols.lua`. Reproduces the most useful
-- concept from `vendor/UnityExplorer/` — its Scene Explorer tab — for
-- our out-of-process architecture: instead of running in-process via
-- BepInEx/MelonLoader and using Unity's `SceneManager` API directly,
-- we compose already-shipped wire commands (`findInstances` heap scan
-- from Phase 9 + `callMethod` from Phase 19 + `walkInstance` /
-- `walkArray`) to walk the live scene from outside the game.
--
-- ⚠️ MAIN-THREAD MARSHALING CAVEAT — READ BEFORE BUMPING LIMITS ⚠️
-- ─────────────────────────────────────────────────────────────────
-- Every Unity scene-graph property reachable from this script
-- (`Object.get_name`, `GameObject.get_transform`, `Transform.get_parent`,
-- `Transform.get_childCount`, `Transform.GetChild`,
-- `Component.get_gameObject`, `GameObject.GetComponents`) is an
-- **icall into Unity's native side**. Phase 19's `callMethod` invokes
-- those on the pipe worker thread, NOT on Unity's main thread. Unity's
-- internal locks tolerate small bursts (verified: ~32 calls is safe);
-- they do NOT tolerate sustained 1000+ call streams — the game becomes
-- unresponsive within seconds and you have to kill the process.
--
-- Until Phase 19 v2 ships main-thread marshaling, the rules are:
--   1. `getName(addr)` — single call. Always safe.
--   2. `getAllGameObjects(opts)` — heap scan only when decodeNames=false
--      (no callMethod). Safe at any limit. With decodeNames=true, treat
--      every call as costing one Unity icall — keep limit ≤ 256 unless
--      you can afford the game to chug.
--   3. `findByName(pattern, opts)` — same as #2; default limit dropped
--      to 256 below for safety.
--   4. `getActiveSceneRoots` / `getChildren` / `getComponents` —
--      EXPERIMENTAL in v1. Each issues 2-3 icalls per node visited.
--      Do NOT call with limit > 256 on a busy scene. If the game
--      freezes, you'll have to kill it. Defaults dropped to 128 below.
--
-- This file inserts a `sleep(1)` between every RPC to give the main
-- thread breathing room. Doesn't eliminate the risk — just slows the
-- attack rate. The proper fix lives in the DLL (a frame-hooked
-- runtime_invoke marshaler) and is its own future phase.
--
-- Standalone — does NOT depend on the sibling scripts. Same rules as
-- `unity_invoke.lua`: ships its own tiny JSON parser + serializer + pipe
-- RPC. (Phase 22 plans to refactor the shared JSON + pipe scaffolding
-- into a single `unity_pipe.lua` module before embedding the toolkit
-- into a `.CT`. Until then, each bridge stays self-contained.)
--
-- Quick start
-- -----------
-- dofile([[<CE-install>\discrete\unity_scene.lua]])
--
-- -- All GameObjects in every loaded scene (heap scan; fast, no name decode):
-- local all = getAllGameObjects({ limit = 1024 })
-- print(#all .. " GameObjects in heap")
--
-- -- Same, with names decoded eagerly (one extra callMethod per object):
-- local all = getAllGameObjects({ limit = 1024, decodeNames = true })
-- for i = 1, math.min(20, #all) do
--     print(string.format("0x%X  %s", all[i].address, all[i].name or "<nil>"))
-- end
--
-- -- Find by substring of the GameObject's `name`:
-- local hits = findByName("Player")
-- for _, h in ipairs(hits) do print(string.format("0x%X  %s", h.address, h.name)) end
--
-- -- Just the active scene's root GameObjects (filters by transform.parent == 0):
-- local roots = getActiveSceneRoots()
--
-- -- Walk the children of a known GameObject:
-- local kids = getChildren(0x12345678)
--
-- -- List the Components attached to a GameObject:
-- local comps = getComponents(0x12345678)
--
-- API (defaults below reflect the main-thread caveat above)
-- ---
--   getName(addr)                      -> string | nil
--   getAllGameObjects(opts)            -> { { address, name? }, ... }
--       opts.decodeNames=false  → default limit 1024 (cap 4096; cheap heap scan)
--       opts.decodeNames=true   → default limit 256  (one icall per object)
--   findByName(pattern, opts)          -> { { address, name }, ... }
--       opts = { caseSensitive = false, limit = 256 }
--   getActiveSceneRoots(opts)          -> { { address, name }, ... }   (EXPERIMENTAL)
--       opts = { limit = 128 }
--   getChildren(addr)                  -> { { address, name }, ... }   (EXPERIMENTAL)
--   getComponents(addr)                -> { { address, className, namespace, assembly }, ... }   (EXPERIMENTAL)
--
-- ALL functions return Lua arrays (1-indexed). Empty arrays mean "no match",
-- not "error" — RPC errors throw via `error()` so callers can `pcall`.
--
-- See docs/pipe-protocol.md for the underlying wire commands and
-- docs/lua-invoke.md for `callMethod` marshaling rules. Verified against
-- (Phase 20d): TBD on first live test pass.

local TAG = "[UnityCEDumper.scene]"
local function log (fmt, ...) print(string.format(TAG .. " "       .. fmt, ...)) end
local function warn(fmt, ...) print(string.format(TAG .. " WARN "  .. fmt, ...)) end
local function err (fmt, ...) print(string.format(TAG .. " ERROR " .. fmt, ...)) end

-- ---------- Tiny JSON parser (response-shape-specific) ------------------
-- Same shape as unity_invoke.lua's parser. Could be lifted into a shared
-- unity_pipe.lua during Phase 22 consolidation; until then, kept inline so
-- this file stays a single dofile-able artifact.

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

local PIPE   = [[\\.\pipe\UnityDumpBfx]]
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
    -- Per-call throttle — 1 ms yield to give the game's main thread
    -- breathing room between icall-heavy callMethod RPCs. See the
    -- "MAIN-THREAD MARSHALING CAVEAT" block at the top of the file.
    if sleep then sleep(1) end
    if resp == nil then error(TAG .. " empty response for " .. cmd) end
    local r = parseJson(resp)
    -- Wire envelope: { id, result } on success or { id, error: {code, message} }
    -- on failure. Unwrap so callers see the result payload directly — same
    -- contract as unity_invoke.lua's callUnityMethod (`return resp.result or {}`).
    if r.error then
        local err = r.error
        local msg = (type(err) == "table" and (err.message or err.code)) or tostring(err)
        error(TAG .. " " .. cmd .. " failed: " .. tostring(msg))
    end
    return r.result or {}
end

-- ---------- Local arg builders (subset; we only need a few) -------------
-- Local-scope on purpose so this script can be loaded alongside
-- unity_invoke.lua without redefining its globals.

local function _argString(s)  return { type = "string",  value = tostring(s) } end
local function _argBool(v)    return { type = "bool",    value = (v and true or false) } end
local function _argAddress(v)
    local s
    if     type(v) == "number" then s = string.format("0x%X", v)
    elseif type(v) == "string" then
        v = v:gsub("%s", "")
        s = (v:sub(1, 2):lower() == "0x") and v or ("0x" .. v)
    else
        error(TAG .. " _argAddress: expected number or hex string, got " .. type(v))
    end
    return { type = "address", value = s }
end
local function _argInt32(v)   return { type = "int32",   value = v } end

-- ---------- Unity class identity constants ------------------------------
-- The asm/ns/class triples we use most. Extracted into one place so a
-- future Unity-version-specific tweak (e.g. UnityEngine.CoreModule split)
-- happens once. Verified via `getClasses` against:
--   • Unity 2021.3 (SaGa F2R)         — IL2CPP, metadata v29
--   • Unity 6000.3 (Songs of Silence) — IL2CPP, metadata v39

local CORE_ASM    = "UnityEngine.CoreModule"
local CORE_NS     = "UnityEngine"
local MSCORLIB    = "mscorlib"

-- ---------- callMethod helper wrappers ----------------------------------
-- Single-purpose convenience — keeps the call sites readable and routes
-- all RPC responses through one error-handling path.

local function _call(opts)
    -- opts = { asm, ns, class, method, args = {}, sig = nil, instance = nil }
    local req = {
        assembly = opts.asm,
        ["namespace"] = opts.ns,
        name = opts.class,
        method = opts.method,
    }
    if opts.args     then req.args     = opts.args     end
    if opts.sig      then req.signatureMatch = opts.sig end
    if opts.instance then req.instance = opts.instance end
    local r = rpc("callMethod", req)
    if not r.ok and r.exception then
        error(TAG .. " " .. opts.class .. "." .. opts.method ..
              " threw: " .. tostring(r.exception))
    end
    return r
end

-- ---------- Address helpers ---------------------------------------------
-- callMethod returns reference-type returnValue as "0x..." hex strings;
-- coerce to a Lua number when we need to pass it back as an address arg.

local function _toAddrNum(v)
    if v == nil then return 0 end
    if type(v) == "number" then return v end
    if type(v) == "string" then return tonumber(v, 16) or tonumber(v:gsub("^0[xX]", ""), 16) or 0 end
    return 0
end

local function _isNullAddr(v)
    local n = _toAddrNum(v)
    return n == 0
end

-- ---------- Public: getName ---------------------------------------------
-- Decode a GameObject's `name` property via UnityEngine.Object.get_name.
-- This is an internal-call (icall) into Unity's native side on both
-- backends; runtime_invoke routes through the JIT/AOT entry point fine.
-- Returns nil on null / decode failure (does NOT throw — caller usually
-- iterates a list and wants to keep going).

function getName(addr)
    if _isNullAddr(addr) then return nil end
    local r = _call({
        asm = CORE_ASM, ns = CORE_NS, class = "Object", method = "get_name",
        instance = string.format("0x%X", _toAddrNum(addr)),
        args = {},
        sig = "()",
    })
    if r.returnType == "System.String" and r.returnValue then
        return tostring(r.returnValue)
    end
    return nil
end

-- ---------- Public: getAllGameObjects -----------------------------------
-- Heap-scan via findInstances. Fastest entry point — no callMethod cost
-- unless decodeNames is set. Returns up to opts.limit GameObjects.
--
-- WARNING: findInstances can produce false positives (Phase 9 caveat —
-- the same Il2CppClass*/MonoVTable* can appear in metadata tables too).
-- For Scene-explorer use, opts.decodeNames=true filters most of those out
-- because non-GameObject false positives won't have a decodable name.

--- DEPRECATED — kept only for users who explicitly want the old
--- findInstances-based path. The two-layer validator below is
--- structurally insufficient for `UnityEngine.GameObject` because
--- IL2CPP metadata structures (RGCTX cache entries) legitimately
--- store class pointers at +0x00 AND +0x18, with garbage at +0x10
--- that happens to fall within "plausible user-space pointer" range.
--- See lessons-learned.md "findInstances heap scan picks up..." for
--- the full forensic. Use `getAllGameObjects` (which now goes through
--- `Object.FindObjectsOfTypeAll`) instead.
local function _isValidInstance(addr, classPtr)
    if classPtr == 0 then return true end
    if readPointer == nil then return true end
    local ok1, typePtr = pcall(readPointer, addr)
    if not ok1 or typePtr ~= classPtr then return false end
    local ok2, cachedPtr = pcall(readPointer, addr + 0x10)
    if not ok2 or cachedPtr == nil then return false end
    if cachedPtr < 0x10000 or cachedPtr > 0x7FFFFFFFFFFF then return false end
    return true
end

--- Resolve a managed Type instance for a fully-qualified type name.
--- Used as the entry point for `Object.FindObjectsOfTypeAll(Type)`
--- (and structurally identical to the recipe in `getComponents`).
local function _getManagedType(fqn)
    local r = _call({
        asm = MSCORLIB, ns = "System", class = "Type", method = "GetType",
        args = { _argString(fqn) },
        sig = "(System.String)",
    })
    if not r.ok then
        warn("_getManagedType failed for " .. fqn .. ": " .. tostring(r.exception or r.error))
        return 0
    end
    return _toAddrNum(r.returnValue)
end

--- The PROPER way to enumerate GameObjects. Goes through Unity's own
--- engine API (`UnityEngine.Object.FindObjectsOfTypeAll(Type)`) which
--- walks the engine's authoritative object tables — zero false
--- positives, includes hidden + DontDestroyOnLoad + HideAndDontSave
--- objects (i.e. matches what UnityExplorer's "Object Search" returns).
---
--- Cost shape: 2 callMethod RPCs (Type.GetType + FindObjectsOfTypeAll)
--- + 1 walkArray (pure memory read, zero main-thread pressure).
--- Constant cost regardless of result count — the array walk is local.
---
--- Returns the same shape as the old findInstances path:
--- `{ { address = 0x..., name? = "..." }, ... }`
---
--- Why this replaced findInstances: see lessons-learned.md.
--- `findInstances UnityEngine.GameObject` returns IL2CPP metadata
--- structures + thread-stack residue alongside real instances; even
--- two-layer Lua-side validation can't reliably tell them apart
--- (RGCTX entries fool the layout heuristic). Unity's own enumeration
--- is authoritative.
function getAllGameObjects(opts)
    opts = opts or {}
    -- Default limits — `walkArray` cap is the wire's max in-pipe element
    -- count (4096). `decodeNames=true` keeps the same 256 cap as before
    -- because that's the empirical main-thread-safe icall budget.
    local arrayLimit = opts.limit
    if not arrayLimit then
        arrayLimit = opts.decodeNames and 256 or 4096
    end

    local typeAddr = _getManagedType("UnityEngine.GameObject, UnityEngine.CoreModule")
    if typeAddr == 0 then
        return {}  -- _getManagedType already warned
    end

    -- Unity 2021 API layout (live forensic on SaGa F2R 2026-05-08, Unity
    -- 2021.3.39f1, IL2CPP, code stripping enabled):
    --
    --   ❌ Resources.FindObjectsOfTypeAll(Type)            — STRIPPED
    --   ❌ Object.FindObjectsOfTypeAll<T>() (generic)      — stripped + unsupported
    --   ❌ Object.FindObjectsOfType(Type) (single arg)     — stripped (only the
    --                                                        2-arg overload survives)
    --   ✅ Object.FindObjectsOfType(Type, bool incl. inactive) — survived
    --                                                            (this is what we use)
    --   ✅ GameObject.Find(string)                         — survived (per-name)
    --   ✅ Camera.get_main()                                — survived (singleton)
    --   ✅ SceneManager.GetActiveScene() → Scene struct    — survived but needs
    --                                                         value-type marshaling
    --
    -- Substring-match note: signatureMatch="(System.Type)" does NOT match
    -- "(System.Type, System.Boolean)" because the close paren in our needle
    -- isn't in the haystack — Type is followed by a comma. Use the exact
    -- sig string for two-arg methods.
    --
    -- includeInactive=true gives us roughly what FindObjectsOfTypeAll would
    -- have, modulo the HideAndDontSave-only objects (rare in user code).
    local tries = {
        {
            class = "Object", method = "FindObjectsOfType",
            sig   = "(System.Type, System.Boolean)",
            args  = function() return { _argAddress(typeAddr), _argBool(true) } end,
            note  = "all (incl. inactive); 2-arg overload",
        },
        -- Fallback: the 1-arg overload IF it ever survives stripping in some
        -- Unity build. signatureMatch ends with ')' so it won't accidentally
        -- match the 2-arg form.
        {
            class = "Object", method = "FindObjectsOfType",
            sig   = "(System.Type)",
            args  = function() return { _argAddress(typeAddr) } end,
            note  = "active only; 1-arg overload",
        },
    }
    local arrR, arrAddr, usedAttempt, lastErr
    for _, t in ipairs(tries) do
        local r = _call({
            asm = CORE_ASM, ns = CORE_NS, class = t.class, method = t.method,
            args = t.args(),
            sig = t.sig,
        })
        if r.ok and r.returnValue then
            arrR = r
            arrAddr = _toAddrNum(r.returnValue)
            usedAttempt = t
            break
        else
            lastErr = r.exception or r.error or "unknown"
            if opts.verbose then
                print(string.format(
                    "%s %s.%s %s failed: %s — trying next",
                    TAG, t.class, t.method, t.sig, tostring(lastErr)))
            end
        end
    end
    if arrAddr == nil or arrAddr == 0 then
        warn("getAllGameObjects: all FindObjects* attempts failed: " .. tostring(lastErr))
        return {}
    end

    local elemsR = rpc("walkArray", {
        address = string.format("0x%X", arrAddr),
        limit   = arrayLimit,
    })

    local out = {}
    local elements = elemsR.elements or {}
    for i = 1, #elements do
        local e = elements[i]
        local addr = _toAddrNum(e.value or e.address or e)
        if addr ~= 0 then
            local entry = { address = addr }
            if opts.decodeNames then
                local ok, n = pcall(getName, addr)
                entry.name = ok and n or nil
            end
            out[#out + 1] = entry
        end
    end

    if opts.verbose then
        print(string.format(
            "%s getAllGameObjects: %d GameObjects via %s.%s — %s" ..
            " (array=0x%X, walked=%d, total=%s)",
            TAG, #out, usedAttempt.class, usedAttempt.method, usedAttempt.note,
            arrAddr, #elements, tostring(elemsR.count or "?")))
    end
    return out
end

-- ---------- Public: findByName ------------------------------------------
-- Substring filter on the GameObject's name property. Always decodes
-- names (otherwise we have nothing to filter on) — opts.limit caps the
-- INITIAL heap scan, not the returned set, so a small limit means
-- "search the first N GameObjects in heap order" not "return up to N".

function findByName(pattern, opts)
    opts = opts or {}
    if pattern == nil or pattern == "" then
        error(TAG .. " findByName: pattern is required")
    end

    local needle = opts.caseSensitive and pattern or pattern:lower()
    -- Default 256 (decodeNames=true) per main-thread caveat. Bigger limits
    -- are fine for short bursts but risky on busy scenes; document it on
    -- the call site if you bump this.
    local limit = opts.limit or 256
    local all = getAllGameObjects({ limit = limit, decodeNames = true })

    local out = {}
    for _, go in ipairs(all) do
        if go.name then
            local hay = opts.caseSensitive and go.name or go.name:lower()
            if hay:find(needle, 1, true) then  -- plain text search, no regex
                out[#out + 1] = { address = go.address, name = go.name }
            end
        end
    end
    return out
end

-- ---------- Internal: get a GameObject's Transform ----------------------
-- GameObject.transform is a property that calls into native; on both
-- backends runtime_invoke routes through the icall. Result is the
-- managed Transform object pointer.

local function _transformOf(gameObjectAddr)
    local r = _call({
        asm = CORE_ASM, ns = CORE_NS, class = "GameObject", method = "get_transform",
        instance = string.format("0x%X", _toAddrNum(gameObjectAddr)),
        args = {},
        sig = "()",
    })
    return _toAddrNum(r.returnValue)
end

-- ---------- Internal: Transform parent / childCount / GetChild ----------

local function _parentOf(transformAddr)
    if _isNullAddr(transformAddr) then return 0 end
    local r = _call({
        asm = CORE_ASM, ns = CORE_NS, class = "Transform", method = "get_parent",
        instance = string.format("0x%X", _toAddrNum(transformAddr)),
        args = {},
        sig = "()",
    })
    return _toAddrNum(r.returnValue)
end

local function _childCountOf(transformAddr)
    if _isNullAddr(transformAddr) then return 0 end
    local r = _call({
        asm = CORE_ASM, ns = CORE_NS, class = "Transform", method = "get_childCount",
        instance = string.format("0x%X", _toAddrNum(transformAddr)),
        args = {},
        sig = "()",
    })
    return tonumber(r.returnValue) or 0
end

local function _getChildAt(transformAddr, index)
    local r = _call({
        asm = CORE_ASM, ns = CORE_NS, class = "Transform", method = "GetChild",
        instance = string.format("0x%X", _toAddrNum(transformAddr)),
        args = { _argInt32(index) },
        sig = "(System.Int32)",
    })
    return _toAddrNum(r.returnValue)
end

local function _gameObjectOf(componentAddr)
    -- Component.gameObject — returns the owning GameObject.
    -- Transform inherits from Component, so the same icall works.
    local r = _call({
        asm = CORE_ASM, ns = CORE_NS, class = "Component", method = "get_gameObject",
        instance = string.format("0x%X", _toAddrNum(componentAddr)),
        args = {},
        sig = "()",
    })
    return _toAddrNum(r.returnValue)
end

-- ---------- Public: getActiveSceneRoots ---------------------------------
-- Filter all GameObjects by `transform.parent == null`. Slow on big
-- scenes — one Transform getter + one parent getter per GameObject.
--
-- **Important optimisation**: decode names FIRST and skip nil-name
-- entries before issuing transform/parent calls. findInstances heap-scan
-- has ~5–10% false positives (non-GameObject class-header pointer
-- collisions); calling runtime_invoke on those traps in the SEH leaf and
-- adds 50–200 ms each. Filtering them out via name decode (which is one
-- icall and short-circuits cleanly on bad pointers) cuts total time by
-- 5–20× and removes a class of garbage results from the output.
--
-- Note: this returns roots across ALL loaded scenes, not just the
-- active scene. Distinguishing per-scene needs `Scene.handle` matching,
-- which requires Scene-struct marshaling (currently not supported by
-- callMethod). v2: add `Scene` (value-type) marshaling, then narrow the
-- filter via SceneManager.GetActiveScene().

function getActiveSceneRoots(opts)
    opts = opts or {}
    -- EXPERIMENTAL — issues 2-3 icalls per non-false-positive GameObject
    -- (transform getter + parent getter). Default 128 keeps total
    -- callMethod count well under the empirical "main thread chokes"
    -- threshold (~512 sustained icalls). Bumping past 256 risks
    -- freezing the game. See main-thread marshaling caveat at file head.
    local limit = opts.limit or 128

    -- Pass 1: heap scan + name decode. Skip nil names (heap-scan false
    -- positives — saves ~2 callMethods per skipped entry in pass 2).
    local all = getAllGameObjects({ limit = limit, decodeNames = true })

    -- Pass 2: among survivors, keep only those with transform.parent == null.
    -- Wrap each callMethod in pcall — if any survivor still trips a
    -- runtime_invoke crash (rare after the name-filter, but possible —
    -- e.g. a freshly destroyed GameObject whose vtable is intact but
    -- whose native side is half-torn-down), we skip it instead of
    -- aborting the whole scan.
    local out = {}
    for _, go in ipairs(all) do
        if go.name then  -- skip false positives
            local trOk, tr = pcall(_transformOf, go.address)
            if trOk and tr and tr ~= 0 then
                local prOk, pr = pcall(_parentOf, tr)
                if prOk and pr == 0 then
                    out[#out + 1] = {
                        address  = go.address,
                        transform = tr,
                        name     = go.name,
                    }
                end
            end
        end
    end
    return out
end

-- ---------- Public: getChildren -----------------------------------------
-- Accepts either a GameObject address or a Transform address. Returns
-- the immediate children as { address, transform, name }. (Detection is
-- by trial-and-error — if get_transform succeeds and yields the same
-- address, we were given a Transform; otherwise we were given a
-- GameObject.)

function getChildren(addr)
    addr = _toAddrNum(addr)
    if addr == 0 then return {} end

    -- Try as GameObject first (get_transform); fall back to treating
    -- the address as a Transform itself if that yields nothing.
    local trOk, tr = pcall(_transformOf, addr)
    if not trOk or not tr or tr == 0 then
        tr = addr
    end

    local cntOk, count = pcall(_childCountOf, tr)
    if not cntOk or not count or count <= 0 then return {} end

    local out = {}
    for i = 0, count - 1 do
        local chOk, childTr = pcall(_getChildAt, tr, i)
        if chOk and childTr and childTr ~= 0 then
            local goOk, childGo = pcall(_gameObjectOf, childTr)
            if goOk and childGo and childGo ~= 0 then
                local nmOk, n = pcall(getName, childGo)
                out[#out + 1] = {
                    address  = childGo,
                    transform = childTr,
                    name     = nmOk and n or nil,
                }
            end
        end
    end
    return out
end

-- ---------- Public: getComponents ---------------------------------------
-- Use GameObject.GetComponents(typeof(Component)) — returns a Component[]
-- array. We then walkArray + per-element walkInstance to recover the
-- runtime class identity (Il2CppClass*/MonoVTable*) which we feed through
-- whatIsAt to get the assembly/namespace/class triple.

function getComponents(gameObjectAddr)
    gameObjectAddr = _toAddrNum(gameObjectAddr)
    if gameObjectAddr == 0 then return {} end

    -- Step 1: get a Type instance for UnityEngine.Component.
    -- Type.GetType("UnityEngine.Component, UnityEngine.CoreModule")
    local typeOk, typeR = pcall(_call, {
        asm = MSCORLIB, ns = "System", class = "Type", method = "GetType",
        args = { _argString("UnityEngine.Component, UnityEngine.CoreModule") },
        sig = "(System.String)",
    })
    if not typeOk or not typeR then
        warn("getComponents: Type.GetType call failed: " .. tostring(typeR))
        return {}
    end
    local typeAddr = _toAddrNum(typeR.returnValue)
    if typeAddr == 0 then
        warn("getComponents: Type.GetType for Component returned null — no fallback yet")
        return {}
    end

    -- Step 2: GameObject.GetComponents(Type) returns Component[]
    local arrOk, arrR = pcall(_call, {
        asm = CORE_ASM, ns = CORE_NS, class = "GameObject", method = "GetComponents",
        instance = string.format("0x%X", gameObjectAddr),
        args = { _argAddress(typeAddr) },
        sig = "(System.Type)",
    })
    if not arrOk or not arrR then
        warn("getComponents: GetComponents call failed: " .. tostring(arrR))
        return {}
    end
    local arrAddr = _toAddrNum(arrR.returnValue)
    if arrAddr == 0 then return {} end

    -- Step 3: walkArray to enumerate the slots.
    local walkR = rpc("walkArray", {
        address = string.format("0x%X", arrAddr),
        elementAssembly  = CORE_ASM,
        elementNamespace = CORE_NS,
        elementName      = "Component",
        limit            = 256,
    })

    local out = {}
    for _, e in ipairs(walkR.elements or {}) do
        local compAddr = _toAddrNum(e.ptr or e.address)
        if compAddr ~= 0 then
            -- Step 4: identify the actual runtime class via whatIsAt.
            -- The class header is the first qword of the object — feeding
            -- compAddr into whatIsAt gives us the most-derived class.
            local entry = { address = compAddr }
            local idOk, idR = pcall(rpc, "whatIsAt", {
                address = string.format("0x%X", compAddr),
            })
            if idOk and idR and idR.matches and idR.matches[1] then
                local m = idR.matches[1]
                entry.assembly  = m.classAssembly  or m.assembly
                entry.namespace = m.classNamespace or m["namespace"]
                entry.className = m.className      or m.name
            end
            out[#out + 1] = entry
        end
    end
    return out
end

-- ---------- Banner ------------------------------------------------------

log("loaded — getAllGameObjects / findByName / getActiveSceneRoots / getChildren / getComponents / getName")

-- =========================================================================
-- Test plan (verify on first live test pass — Phase 20d)
-- =========================================================================
-- Targets: at least one IL2CPP (e.g. SaGa F2R or Songs of Silence) +
--          at least one Mono     (e.g. BongoCat or Farthest Frontier).
--
-- Pre-flight: inject the DLL via Test-Handshake.ps1 / DiscreteUI Pick.
-- Then dofile([[<CE-install>\discrete\unity_scene.lua]]) from CE Lua.
--
-- 1. Heap scan baseline:
--      local all = getAllGameObjects()
--      print(#all)  -- expect a few hundred to a few thousand
--
-- 2. Name decode:
--      local some = getAllGameObjects({ limit = 32, decodeNames = true })
--      for _, g in ipairs(some) do print(string.format("0x%X %s", g.address, g.name or "<nil>")) end
--      -- expect at least some non-nil names; non-GameObject false positives
--      -- from findInstances will show as nil here (filter heuristic)
--
-- 3. findByName:
--      local hits = findByName("Camera")
--      -- expect MainCamera and any other camera GameObjects
--
-- 4. Scene roots:
--      local roots = getActiveSceneRoots({ limit = 1024 })
--      -- expect 5–50 entries on a typical game scene
--      for _, r in ipairs(roots) do print(string.format("0x%X %s", r.address, r.name or "<nil>")) end
--
-- 5. Child walk:
--      local kids = getChildren(roots[1].address)
--      -- expect kids of the first scene root
--
-- 6. Components:
--      local comps = getComponents(roots[1].address)
--      -- expect at least Transform; other components depend on the GameObject
--
-- Known unknowns:
-- • UnityEngine.Object.get_name is an icall on both backends — should pass
--   through runtime_invoke fine, but never tested. If it crashes the host,
--   the SEH leaf in IL2CPPMethodInvoker / MonoMethodInvoker will surface
--   "native_crash_in_runtime_invoke" instead of tearing down.
-- • Transform.GetChild signatureMatch "(System.Int32)" — unique on stock
--   Unity, but if a game ships a Transform subclass with an overload it
--   could disambiguate-fail. Workaround: pass a more specific signatureMatch.
-- • getComponents Step 4 (whatIsAt of each component) is N+1 RPCs per
--   GameObject. For deep inspector use this is fine; for list views
--   consider deferring component identification until the user expands.
