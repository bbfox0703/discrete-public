-- ============================================================
-- mono_registersymbol_helpers.lua — DLL-free Mono symbol registration.
-- ============================================================
--
-- Pure-CE-Mono-API helpers. Lets users build cheat tables that
-- registerSymbol() one or more Mono method overloads WITHOUT ever
-- injecting the Discrete DLL. Uses CE's built-in `mono_*` Lua
-- API (provided by CE's MonoDataCollector) — works on any Mono
-- target CE itself can talk to.
--
-- Loaded into a cheat table by Tools → Inject Discrete Bridge
-- (alongside the 7 unity_*.lua bridge files). Generated AA scripts
-- pull this file out of the CT via findTableFile() and run it once
-- per CE session — the `if _G.xxx == nil` guards make repeated
-- loads cheap and harmless.
--
-- The three register helpers:
--   mono_registerSymbol(name, ns, cls, method)
--     First match by method name. Fine when there's no overload.
--   mono_registerSymbolEx(name, ns, cls, method, paramTypes)
--     Exact count + ordered substring match per parameter type
--     (string.find with literal=true). Pass the full
--     `System.Int32` / `UnityEngine.Vector3` form to avoid the
--     `Int32` matches `UInt32` substring trap.
--   mono_registerSymbolBySignatureMatch(name, ns, cls, method,
--                                       sigContains, sigExcludes)
--     Substring match against mono_method_getSignature's rendered
--     signature string. Pass `sigExcludes` to disambiguate when
--     two overloads share most of their signature.
--
-- Also exposes:
--   mono_ensureAttached()   — launches MonoDataCollector if needed.
--                             Auto-called by each register helper.
--   SafeMonoDestroy()       — clean teardown of the CE Mono pipe.
--                             Useful when switching processes.
--
-- The file does no Mono attach at load time — only function
-- definitions + a single load banner land. Attach to Mono happens
-- lazily on the first register call so the bridge bootstrap can
-- ship this even to users who never touch Mono helpers.

-- Idempotency: every public symbol is guarded so multiple loads
-- (re-running the bootstrap, re-opening the .CT) don't redefine
-- in-flight functions. Each guard wraps a single function.

-- Logging convention (docs/lua-logging.md): this file's own print()s are
-- progress/info, so we shadow print() with a DEBUG-gated version; a normal
-- register call stays quiet and doesn't pop CE's Lua Engine over its main
-- window. Failures use eprint(), which ALWAYS prints and sets the shared
-- _G.DISCRETE_HAD_ERROR flag so the generated AA that self-loads this file
-- keeps the Lua Engine open on a failed run. Both are declared before every
-- function below so the _G.* closures capture them as upvalues.
local _rawprint = print
local function _dbgOn() return (tonumber(rawget(_G, "DISCRETE_DEBUG")) or 0) >= 1 end
local function print(...) if _dbgOn() then _rawprint(...) end end
local function eprint(...) rawset(_G, "DISCRETE_HAD_ERROR", true); _rawprint(...) end

-- ----------------------------------------------------------------
-- SafeMonoDestroy — clean teardown of CE's Mono data collector.
-- ----------------------------------------------------------------
if _G.SafeMonoDestroy == nil then
  _G.SafeMonoDestroy = function()
    print("SafeMonoDestroy: begin cleanup...")

    if monoSymbolEnum then
      print("  terminating monoSymbolEnum thread...")
      pcall(function()
        monoSymbolEnum.terminate()
        if not monoSymbolEnum.waitfor(3000) then
          print("  WARN: monoSymbolEnum did not terminate within 3s")
        end
        monoSymbolEnum.destroy()
        monoSymbolEnum = nil
      end)
    end

    if monoSymbolList and monoSymbolList.progressbar then
      monoSymbolList.progressbar.OnTimer = nil
    end

    print("  unlocking pipe (if held)...")
    pcall(function()
      if monopipe and monopipe.unlock then monopipe.unlock() end
    end)

    print("  sleep 250ms to flush pipe state...")
    sleep(250)

    local t0 = os.clock()
    print(string.format("  destroying monopipe — may take a while [start %.3fs]", t0))
    pcall(function()
      if monopipe then monopipe.destroy(); monopipe = nil end
    end)
    local t1 = os.clock()
    print(string.format("  monopipe.destroy() done [end %.3fs | duration %.3fs]",
                        t1, t1 - t0))

    if monoSymbolList then monoSymbolList.destroy(); monoSymbolList = nil end
    if monoeventpipe then monoeventpipe.destroy(); monoeventpipe = nil end

    print("SafeMonoDestroy: cleanup complete.")
  end
end

-- ----------------------------------------------------------------
-- mono_ensureAttached — lazy attach to CE's MonoDataCollector.
-- Auto-called by each register helper. Tracks last-seen PID so a
-- process switch tears down the stale collector before relaunching.
-- ----------------------------------------------------------------
if _G.mono_ensureAttached == nil then
  _G.mono_ensureAttached = function()
    local pid = getOpenedProcessID()
    if pid == 0 then
      eprint("mono_ensureAttached: ERROR no process attached. File -> Open Process first.")
      return false
    end

    if _G.lastMonoPID == nil then _G.lastMonoPID = -1 end

    if monopipe == nil or _G.lastMonoPID ~= pid then
      if monopipe ~= nil then
        pcall(_G.SafeMonoDestroy)
      end
      local ok, err = pcall(LaunchMonoDataCollector)
      if not ok then
        eprint(string.format("mono_ensureAttached: ERROR LaunchMonoDataCollector failed: %s",
                            tostring(err)))
        return false
      end
      _G.lastMonoPID = pid
    end

    return monopipe ~= nil
  end
end

-- ----------------------------------------------------------------
-- mono_registerSymbol — first matching method name (no overload).
-- ----------------------------------------------------------------
if _G.mono_registerSymbol == nil then
  _G.mono_registerSymbol = function(symbolname, namespace, classname, methodname)
    if not _G.mono_ensureAttached() then return false end

    local m = mono_findMethod(namespace, classname, methodname)
    if m == nil or m == 0 then
      eprint(string.format("mono_registerSymbol: ERROR method not found - %s.%s.%s",
                          namespace, classname, methodname))
      return false
    end

    local addr = mono_compile_method(m)
    if addr == 0 then
      eprint(string.format("mono_registerSymbol: ERROR could not compile - %s.%s.%s",
                          namespace, classname, methodname))
      return false
    end

    registerSymbol(symbolname, addr)
    print(string.format("mono_registerSymbol: registered %s = %X", symbolname, addr))
    return true
  end
end

-- ----------------------------------------------------------------
-- mono_registerSymbolEx — exact-count, ordered param-type match.
-- Each paramTypes[i] is substring-matched against the i-th param's
-- typename via string.find(plain=true). Pass full names like
-- "System.Int32" / "UnityEngine.Vector3" — the substring matcher
-- will otherwise let "Int32" match "UInt32" and friends.
-- ----------------------------------------------------------------
if _G.mono_registerSymbolEx == nil then
  _G.mono_registerSymbolEx = function(symbolname, namespace, classname, methodname, paramTypes)
    if not _G.mono_ensureAttached() then return false end

    local cls = mono_findClass(namespace, classname)
    if not cls then
      eprint(string.format("mono_registerSymbolEx: ERROR class not found - %s.%s",
                          namespace, classname))
      return false
    end

    local methods = mono_class_enumMethods(cls)
    if not methods then
      eprint(string.format("mono_registerSymbolEx: ERROR no methods in %s.%s",
                          namespace, classname))
      return false
    end

    for _, m in ipairs(methods) do
      if m.name == methodname then
        -- mono_method_get_parameters can throw (generic / uncompiled methods)
        -- and an entry's `.typename` can be nil on some CE/Mono builds — both
        -- previously crashed string.find with "bad argument #1 (got nil)".
        local ok, p = pcall(mono_method_get_parameters, m.method)
        local matched = ok and type(p) == "table" and type(p.parameters) == "table"

        if matched then
          if #p.parameters ~= #paramTypes then
            matched = false
          else
            for i = 1, #paramTypes do
              local tn = p.parameters[i] and p.parameters[i].typename
              if type(tn) ~= "string" or type(paramTypes[i]) ~= "string"
                  or not string.find(tn, paramTypes[i], 1, true) then
                matched = false
                break
              end
            end
          end
        end

        if matched then
          local addr = mono_compile_method(m.method)
          if addr == 0 then
            eprint(string.format("mono_registerSymbolEx: ERROR matched but failed to compile - %s.%s.%s",
                                namespace, classname, methodname))
            return false
          end
          registerSymbol(symbolname, addr)
          print(string.format("mono_registerSymbolEx: registered %s = %X", symbolname, addr))
          return true
        end
      end
    end

    eprint(string.format("mono_registerSymbolEx: ERROR no matching overload - %s.%s.%s",
                        namespace, classname, methodname))
    return false
  end
end

-- ----------------------------------------------------------------
-- mono_registerSymbolBySignatureMatch — substring match against
-- mono_method_getSignature's rendered string. Use when typed
-- paramTypes are unstable across builds, or when one overload
-- differs from another by something other than param count.
-- sigExcludes is optional; pass to filter out near-misses.
-- ----------------------------------------------------------------
if _G.mono_registerSymbolBySignatureMatch == nil then
  _G.mono_registerSymbolBySignatureMatch = function(symbolname, namespace, classname,
                                                    methodname, sigContains, sigExcludes)
    if not _G.mono_ensureAttached() then return false end

    local cls = mono_findClass(namespace, classname)
    if not cls then
      eprint(string.format("mono_registerSymbolBySignatureMatch: ERROR class not found - %s.%s",
                          namespace, classname))
      return false
    end

    local methods = mono_class_enumMethods(cls)
    if not methods then
      eprint(string.format("mono_registerSymbolBySignatureMatch: ERROR no methods in %s.%s",
                          namespace, classname))
      return false
    end

    for _, m in ipairs(methods) do
      if m.name == methodname then
        -- Same defensive shape as the discrete_* twin below.
        -- mono_method_getSignature can throw or return nil for generic /
        -- uncompiled methods; skip past those rather than crashing the whole
        -- AA script. Unguarded, string.find(nil, ...) aborted the [ENABLE]
        -- block on any class that merely HAS such an overload — and
        -- ipairs(sigContains) threw outright when the caller omitted it.
        local sigOk, sig = pcall(mono_method_getSignature, m.method)
        if sigOk and type(sig) == "string" then
          local matched = true

          for _, kw in ipairs(sigContains or {}) do
            if type(kw) ~= "string" or not string.find(sig, kw, 1, true) then
              matched = false
              break
            end
          end

          if matched and sigExcludes then
            for _, ex in ipairs(sigExcludes) do
              if type(ex) == "string" and string.find(sig, ex, 1, true) then
                matched = false
                break
              end
            end
          end

          if matched then
            local addr = mono_compile_method(m.method)
            if addr == 0 then
              eprint(string.format("mono_registerSymbolBySignatureMatch: ERROR matched but failed to compile - %s.%s.%s",
                                  namespace, classname, methodname))
              return false
            end
            registerSymbol(symbolname, addr)
            print(string.format("mono_registerSymbolBySignatureMatch: registered %s = %X (sig=%s)",
                                symbolname, addr, sig))
            return true
          end
        end
      end
    end

    eprint(string.format("mono_registerSymbolBySignatureMatch: ERROR no matching signature - %s.%s.%s",
                        namespace, classname, methodname))
    return false
  end
end

-- ================================================================
-- discrete_* — always-overwrite private variants used by Discrete's
-- generated AA scripts. The `mono_*` versions above use idempotency
-- guards so a user's pre-existing autorun definitions take priority
-- for hand-written CTs. That's great for muscle-memory copy-paste,
-- but it bites the auto-generated path: if the user's autorun lacks
-- an attach step (common), `mono_findClass` returns nil because the
-- collector was never launched. The `discrete_*` variants below own
-- their namespace outright — no guards, no override risk — and add
-- two robustness shims the autorun pattern typically lacks:
--   * explicit attach (mirrors mono_ensureAttached above)
--   * image-iteration fallback for Unity 2022+ split modules
--     (UnityEngine.CoreModule / .PhysicsModule / ...). CE's stock
--     mono_findClass already iterates mono_enumImages(), but the
--     cache isn't always warm on first call after LaunchMonoDataCollector
--     — re-iterating with mono_image_findClass against a freshly
--     enumerated list catches the late-bound case.
-- ================================================================

local function _discrete_ensure_attached()
  local pid = getOpenedProcessID()
  if pid == 0 then
    eprint("[discrete] No process attached. File -> Open Process first.")
    return false
  end
  if _G.lastMonoPID == nil then _G.lastMonoPID = -1 end
  if monopipe == nil or _G.lastMonoPID ~= pid then
    if monopipe ~= nil and _G.SafeMonoDestroy then
      pcall(_G.SafeMonoDestroy)
    end
    local ok, err = pcall(LaunchMonoDataCollector)
    if not ok then
      eprint(string.format("[discrete] LaunchMonoDataCollector failed: %s",
                          tostring(err)))
      return false
    end
    _G.lastMonoPID = pid
  end
  return monopipe ~= nil
end

-- Primitive / BCL alias table so Mono's mono_signature_get_desc shorthands
-- ("int", "single", "string", "bool", ...) match CLR full names
-- ("System.Int32", "System.Single", "System.String", "System.Boolean", ...)
-- in both typename and signature-string matching.
local _DISCRETE_PRIMITIVE_ALIASES = {
  ["bool"]            = "System.Boolean",
  ["boolean"]         = "System.Boolean",
  ["system.boolean"]  = "System.Boolean",
  ["byte"]            = "System.Byte",
  ["uint8"]           = "System.Byte",
  ["system.byte"]     = "System.Byte",
  ["sbyte"]           = "System.SByte",
  ["int8"]            = "System.SByte",
  ["system.sbyte"]    = "System.SByte",
  ["char"]            = "System.Char",
  ["system.char"]     = "System.Char",
  ["short"]           = "System.Int16",
  ["int16"]           = "System.Int16",
  ["system.int16"]    = "System.Int16",
  ["ushort"]          = "System.UInt16",
  ["uint16"]          = "System.UInt16",
  ["system.uint16"]   = "System.UInt16",
  ["int"]             = "System.Int32",
  ["int32"]           = "System.Int32",
  ["system.int32"]    = "System.Int32",
  ["uint"]            = "System.UInt32",
  ["uint32"]          = "System.UInt32",
  ["system.uint32"]   = "System.UInt32",
  ["long"]            = "System.Int64",
  ["int64"]           = "System.Int64",
  ["system.int64"]    = "System.Int64",
  ["ulong"]           = "System.UInt64",
  ["uint64"]          = "System.UInt64",
  ["system.uint64"]   = "System.UInt64",
  ["single"]          = "System.Single",
  ["float"]           = "System.Single",
  ["system.single"]   = "System.Single",
  ["double"]          = "System.Double",
  ["system.double"]   = "System.Double",
  ["string"]          = "System.String",
  ["system.string"]   = "System.String",
  ["object"]          = "System.Object",
  ["system.object"]   = "System.Object",
  ["void"]            = "System.Void",
  ["system.void"]     = "System.Void",
  ["intptr"]          = "System.IntPtr",
  ["native int"]      = "System.IntPtr",
  ["system.intptr"]   = "System.IntPtr",
  ["uintptr"]         = "System.UIntPtr",
  ["native uint"]     = "System.UIntPtr",
  ["system.uintptr"]  = "System.UIntPtr",
  ["decimal"]         = "System.Decimal",
  ["system.decimal"]  = "System.Decimal",
}

local function _discrete_trim(s)
  return (s:gsub("^%s+", ""):gsub("%s+$", ""))
end

-- Split a parameter type string into canonical (base, suffix), where suffix
-- preserves array ranks (`[]`, `[,]`), byref (`&`), and pointer (`*`) modifiers
-- verbatim so `System.Int32` never matches `System.Int32[]` or `System.Int32&`,
-- while `int[]` canonicalizes to `System.Int32` + `[]`.
local function _discrete_canonical_type(raw)
  local s = _discrete_trim(raw)
  local suffixes = {}
  while true do
    local arr = s:match("(%[%,*%])%s*$")
    if arr then
      table.insert(suffixes, 1, arr)
      s = _discrete_trim(s:sub(1, #s - #arr))
    elseif s:sub(-1) == "&" or s:sub(-1) == "*" then
      table.insert(suffixes, 1, s:sub(-1))
      s = _discrete_trim(s:sub(1, -2))
    else
      break
    end
  end
  local canon = _DISCRETE_PRIMITIVE_ALIASES[s:lower()] or s
  return canon, table.concat(suffixes)
end

-- Symmetric type-name match. CE's `mono_method_get_parameters` reports
-- `typename` with inconsistent namespace prefixes — `System.Int32`
-- keeps `System.`, Mono's `mono_signature_get_desc` uses `int`/`single`/
-- `string`, and `UnityEngine.FindObjectsInactive` may come back as bare
-- `FindObjectsInactive`. Matching rule:
--   * trailing modifiers (`[]`, `[,]`, `&`, `*`) must match identically, AND
--   * after primitive alias normalization, base names are equal OR one
--     side ends with "." .. (the other).
-- Crucially, "System.Int32" matches "int", never "UInt32", "System.UInt32",
-- or "System.Int32[]".
local function _discrete_typename_match(pname, needle)
  if type(pname) ~= "string" or type(needle) ~= "string" then return false end
  local pBase, pSuf = _discrete_canonical_type(pname)
  local nBase, nSuf = _discrete_canonical_type(needle)
  if pBase == "" or nBase == "" then return false end
  if pSuf ~= nSuf then return false end
  if pBase == nBase then return true end
  -- pBase ends with ".nBase" → pBase is namespaced, nBase is bare
  if #pBase > #nBase then
    local tail = pBase:sub(-#nBase - 1)
    if tail == "." .. nBase then return true end
  end
  -- nBase ends with ".pBase" → nBase is namespaced, pBase is bare
  if #nBase > #pBase then
    local tail = nBase:sub(-#pBase - 1)
    if tail == "." .. pBase then return true end
  end
  return false
end

-- Parse a rendered signature from `mono_method_getSignature` into an ordered
-- list of parameter type strings. Handles bare comma lists (`int,string`),
-- parenthesised forms (`(int, string)` or `void (int, string)`), generic
-- commas (`Dictionary<string,int>,bool`), multi-dimensional arrays (`int[,]`),
-- and optional trailing parameter names (`System.Int32 amount`).
local function _discrete_split_sig_params(sig)
  if type(sig) ~= "string" then return nil end
  local inner = _discrete_trim(sig)
  local lp = inner:find("(", 1, true)
  local rp = inner:match(".*()%)")
  if lp and rp and rp > lp then
    inner = _discrete_trim(inner:sub(lp + 1, rp - 1))
  end
  if inner == "" then return {} end

  local params = {}
  local angleDepth, bracketDepth = 0, 0
  local startIdx = 1
  for i = 1, #inner do
    local ch = inner:sub(i, i)
    if ch == "<" then
      angleDepth = angleDepth + 1
    elseif ch == ">" and angleDepth > 0 then
      angleDepth = angleDepth - 1
    elseif ch == "[" then
      bracketDepth = bracketDepth + 1
    elseif ch == "]" and bracketDepth > 0 then
      bracketDepth = bracketDepth - 1
    elseif ch == "," and angleDepth == 0 and bracketDepth == 0 then
      params[#params + 1] = _discrete_trim(inner:sub(startIdx, i - 1))
      startIdx = i + 1
    end
  end
  params[#params + 1] = _discrete_trim(inner:sub(startIdx))

  local out = {}
  for i = 1, #params do
    local tok = params[i]
    if tok ~= "" then
      -- Strip optional leading C# parameter modifier (`ref`/`out`/`in`).
      local mod, rest = tok:match("^(%a+)%s+(.+)$")
      if mod == "ref" or mod == "out" then
        tok = _discrete_trim(rest)
        if tok:sub(-1) ~= "&" then tok = tok .. "&" end
      elseif mod == "in" then
        tok = _discrete_trim(rest)
      end
      -- Strip optional trailing parameter identifier (`System.Int32 amount`),
      -- preserving multi-word Mono type names (`native int`, `native uint`).
      local low = tok:lower()
      if low ~= "native int" and low ~= "native uint" then
        local lastSpace = tok:match(".*()%s+")
        if lastSpace then
          local head = _discrete_trim(tok:sub(1, lastSpace - 1))
          local tail = _discrete_trim(tok:sub(lastSpace + 1))
          local headLow = head:lower()
          if head ~= "" and tail:match("^[A-Za-z_][A-Za-z0-9_]*$")
              and headLow ~= "native" and headLow ~= "unsigned" then
            tok = head
          end
        end
      end
      out[#out + 1] = tok
    end
  end
  return out
end

local function _discrete_find_class(namespace, classname)
  -- Fast path: CE's built-in (iterates cached image list).
  local r = mono_findClass(namespace, classname)
  if r and r ~= 0 then return r end

  -- Fallback: force enumeration + per-image search. Unity 2022+ splits
  -- UnityEngine into many modular DLLs (CoreModule / PhysicsModule /...)
  -- and CE's cached image list is sometimes stale on first lookup after
  -- the data collector launches.
  -- CE's mono_enumImages is CALLBACK-ONLY and returns nothing:
  --     function mono_enumImages(onImage)  ... onImage(image) ... end
  -- (monoscript.lua). Calling it as `local imgs = mono_enumImages()` left
  -- onImage nil, so the first image found raised "attempt to call a nil
  -- value" — this fallback threw instead of running, precisely in the case
  -- it exists for (a class CE's cached image list cannot resolve).
  if not mono_image_findClass then return nil end
  local found = nil
  local ok, err = pcall(mono_enumImages, function(img)
    if found or not img or img == 0 then return end
    local hit = mono_image_findClass(img, namespace, classname)
    if hit and hit ~= 0 then found = hit end
  end)
  if not ok then
    eprint("[discrete] mono_enumImages fallback failed: " .. tostring(err))
    return nil
  end
  return found
end

-- discrete_mono_registerSymbol — first matching method name.
_G.discrete_mono_registerSymbol = function(symbolname, namespace, classname, methodname)
  if not _discrete_ensure_attached() then return false end

  local m = mono_findMethod(namespace, classname, methodname)
  if m == nil or m == 0 then
    -- mono_findMethod uses its own class-lookup path; fall back through
    -- discrete_find_class + enumMethods so the split-module shim helps
    -- here too.
    local cls = _discrete_find_class(namespace, classname)
    if not cls then
      eprint(string.format("[discrete] method not found - %s.%s.%s (class missing)",
                          namespace, classname, methodname))
      return false
    end
    local methods = mono_class_enumMethods(cls)
    for _, mm in ipairs(methods or {}) do
      if mm.name == methodname then m = mm.method; break end
    end
    if not m or m == 0 then
      eprint(string.format("[discrete] method not found - %s.%s.%s",
                          namespace, classname, methodname))
      return false
    end
  end

  local addr = mono_compile_method(m)
  if addr == 0 then
    eprint(string.format("[discrete] could not compile - %s.%s.%s",
                        namespace, classname, methodname))
    return false
  end

  registerSymbol(symbolname, addr)
  print(string.format("[discrete] registered %s = %X", symbolname, addr))
  return true
end

-- discrete_mono_registerSymbolEx — exact-count, ordered param-type match.
_G.discrete_mono_registerSymbolEx = function(symbolname, namespace, classname, methodname, paramTypes)
  if not _discrete_ensure_attached() then return false end

  local cls = _discrete_find_class(namespace, classname)
  if not cls then
    eprint(string.format("[discrete] class not found - %s.%s",
                        namespace, classname))
    return false
  end

  local methods = mono_class_enumMethods(cls)
  if not methods then
    eprint(string.format("[discrete] no methods in %s.%s",
                        namespace, classname))
    return false
  end

  paramTypes = paramTypes or {}
  local candidates = 0          -- name-matched overloads we considered
  local diag = {}               -- shape strings for the post-mortem log
  local matches = {}            -- all matching overloads (ambiguity check)

  for _, m in ipairs(methods) do
    if m.name == methodname then
      candidates = candidates + 1
      local matched = false
      local via = nil
      local paramSummary = nil

      -- Primary path: positional typename check via mono_method_get_parameters.
      local ok, p = pcall(mono_method_get_parameters, m.method)
      local haveParams = ok and type(p) == "table" and type(p.parameters) == "table"
      if haveParams then
        local parts = {}
        for i, e in ipairs(p.parameters) do
          parts[i] = string.format("%s %s", tostring(e.typename or "<nil>"),
                                            tostring(e.name or "?"))
        end
        paramSummary = string.format("(%d) %s", #p.parameters, table.concat(parts, ", "))

        if #p.parameters == #paramTypes then
          matched = true
          for i = 1, #paramTypes do
            local entry = p.parameters[i]
            local pname = entry and entry.typename
            if not _discrete_typename_match(pname, paramTypes[i]) then
              matched = false
              break
            end
          end
          if matched then via = "typename" end
        end
      end

      -- Fallback path: positional signature parse via mono_method_getSignature.
      -- Needed when Unity 2022+ Mono returns nil `typename` entries or throws
      -- inside `mono_method_get_parameters`, and on Mono builds where
      -- `mono_signature_get_desc` emits shorthand names ("int", "single", "string").
      -- Always checks EXACT parameter count and EXACT parameter order.
      local sigStr = nil
      if not matched and (not haveParams or #p.parameters == #paramTypes) then
        local okS, sig = pcall(mono_method_getSignature, m.method)
        if okS and type(sig) == "string" then
          sigStr = sig
          local sigParams = _discrete_split_sig_params(sig)
          if sigParams and #sigParams == #paramTypes then
            matched = true
            for i = 1, #paramTypes do
              if not _discrete_typename_match(sigParams[i], paramTypes[i]) then
                matched = false
                break
              end
            end
            if matched then via = "signature" end
          end
        end
      end

      if matched then
        matches[#matches + 1] = {
          method = m.method,
          via    = via,
          desc   = paramSummary or (sigStr and ("sig: " .. sigStr) or "<matched>"),
        }
      else
        if not sigStr then
          local okS, sig = pcall(mono_method_getSignature, m.method)
          sigStr = (okS and type(sig) == "string") and sig or "<unavailable>"
        end
        if paramSummary then
          diag[#diag + 1] = string.format("  %s  -- sig: %s", paramSummary, sigStr)
        else
          diag[#diag + 1] = string.format("  (introspection unavailable)  -- sig: %s", sigStr)
        end
      end
    end
  end

  if #matches == 1 then
    local hit = matches[1]
    local addr = mono_compile_method(hit.method)
    if addr == 0 then
      eprint(string.format("[discrete] matched but failed to compile - %s.%s.%s",
                          namespace, classname, methodname))
      return false
    end
    registerSymbol(symbolname, addr)
    print(string.format("[discrete] registered %s = %X  (matched via %s)",
                        symbolname, addr, hit.via))
    return true
  end

  if #matches > 1 then
    eprint(string.format("[discrete] ambiguous overload match (%d hits) - %s.%s.%s",
                        #matches, namespace, classname, methodname))
    for _, hit in ipairs(matches) do
      eprint("[discrete]   hit: " .. tostring(hit.desc))
    end
    return false
  end

  -- Reaching here means: class found, methods enumerated, ≥0 named matches
  -- but no full match. Dump what we saw so the user can diagnose typename
  -- mismatches without rebuilding the helper.
  eprint(string.format("[discrete] no matching overload - %s.%s.%s",
                      namespace, classname, methodname))
  eprint(string.format("[discrete]   looked at %d candidate(s); expected (%d): %s",
                      candidates, #paramTypes, table.concat(paramTypes, ", ")))
  for _, line in ipairs(diag) do eprint("[discrete] " .. line) end
  return false
end

-- Check whether a rendered signature matches a single token in sigContains /
-- sigExcludes. Prefers token-wise canonical matching against the parsed
-- parameter list (so "System.Int32" matches Mono's "int" and NEVER matches
-- "System.Int32[]" or "System.UInt32"); falls back to literal substring only
-- if the signature could not be split into parameter tokens.
local function _discrete_sig_has_token(sig, sigParams, token)
  if type(token) ~= "string" or token == "" then return false end
  if sigParams and #sigParams > 0 then
    for i = 1, #sigParams do
      if _discrete_typename_match(sigParams[i], token) then
        return true
      end
    end
    return false
  end
  return string.find(sig, token, 1, true) ~= nil
end

-- discrete_mono_registerSymbolBySignatureMatch — token-aware signature match
-- against mono_method_getSignature's rendered string, with optional excludes.
-- Kept as a fallback / backwards-compatibility helper for legacy CTs; refuses
-- ambiguous multi-overload matches instead of silently registering the first.
_G.discrete_mono_registerSymbolBySignatureMatch = function(symbolname, namespace, classname,
                                                           methodname, sigContains, sigExcludes)
  if not _discrete_ensure_attached() then return false end

  local cls = _discrete_find_class(namespace, classname)
  if not cls then
    eprint(string.format("[discrete] class not found - %s.%s",
                        namespace, classname))
    return false
  end

  local methods = mono_class_enumMethods(cls)
  if not methods then
    eprint(string.format("[discrete] no methods in %s.%s",
                        namespace, classname))
    return false
  end

  local matches = {}
  for _, m in ipairs(methods) do
    if m.name == methodname then
      local ok, sig = pcall(mono_method_getSignature, m.method)
      if ok and type(sig) == "string" then
        local sigParams = _discrete_split_sig_params(sig)
        local matched = true

        for _, kw in ipairs(sigContains or {}) do
          if not _discrete_sig_has_token(sig, sigParams, kw) then
            matched = false
            break
          end
        end

        if matched and sigExcludes then
          for _, ex in ipairs(sigExcludes) do
            if _discrete_sig_has_token(sig, sigParams, ex) then
              matched = false
              break
            end
          end
        end

        if matched then
          matches[#matches + 1] = { method = m.method, sig = sig }
        end
      end
    end
  end

  if #matches == 1 then
    local hit = matches[1]
    local addr = mono_compile_method(hit.method)
    if addr == 0 then
      eprint(string.format("[discrete] matched but failed to compile - %s.%s.%s",
                          namespace, classname, methodname))
      return false
    end
    registerSymbol(symbolname, addr)
    print(string.format("[discrete] registered %s = %X (sig=%s)",
                        symbolname, addr, hit.sig))
    return true
  end

  if #matches > 1 then
    eprint(string.format("[discrete] ambiguous signature match (%d hits) - %s.%s.%s",
                        #matches, namespace, classname, methodname))
    for _, hit in ipairs(matches) do
      eprint("[discrete]   hit sig: " .. tostring(hit.sig))
    end
    return false
  end

  eprint(string.format("[discrete] no matching signature - %s.%s.%s",
                      namespace, classname, methodname))
  return false
end

print("[discrete] mono_registersymbol_helpers.lua loaded — DLL-free Mono symbol register helpers ready.")
