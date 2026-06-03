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
      print("mono_ensureAttached: ERROR no process attached. File -> Open Process first.")
      return false
    end

    if _G.lastMonoPID == nil then _G.lastMonoPID = -1 end

    if monopipe == nil or _G.lastMonoPID ~= pid then
      if monopipe ~= nil then
        pcall(_G.SafeMonoDestroy)
      end
      local ok, err = pcall(LaunchMonoDataCollector)
      if not ok then
        print(string.format("mono_ensureAttached: ERROR LaunchMonoDataCollector failed: %s",
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
      print(string.format("mono_registerSymbol: ERROR method not found - %s.%s.%s",
                          namespace, classname, methodname))
      return false
    end

    local addr = mono_compile_method(m)
    if addr == 0 then
      print(string.format("mono_registerSymbol: ERROR could not compile - %s.%s.%s",
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
      print(string.format("mono_registerSymbolEx: ERROR class not found - %s.%s",
                          namespace, classname))
      return false
    end

    local methods = mono_class_enumMethods(cls)
    if not methods then
      print(string.format("mono_registerSymbolEx: ERROR no methods in %s.%s",
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
            print(string.format("mono_registerSymbolEx: ERROR matched but failed to compile - %s.%s.%s",
                                namespace, classname, methodname))
            return false
          end
          registerSymbol(symbolname, addr)
          print(string.format("mono_registerSymbolEx: registered %s = %X", symbolname, addr))
          return true
        end
      end
    end

    print(string.format("mono_registerSymbolEx: ERROR no matching overload - %s.%s.%s",
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
      print(string.format("mono_registerSymbolBySignatureMatch: ERROR class not found - %s.%s",
                          namespace, classname))
      return false
    end

    local methods = mono_class_enumMethods(cls)
    if not methods then
      print(string.format("mono_registerSymbolBySignatureMatch: ERROR no methods in %s.%s",
                          namespace, classname))
      return false
    end

    for _, m in ipairs(methods) do
      if m.name == methodname then
        local sig = mono_method_getSignature(m.method)
        local matched = true

        for _, kw in ipairs(sigContains) do
          if not string.find(sig, kw, 1, true) then
            matched = false
            break
          end
        end

        if matched and sigExcludes then
          for _, ex in ipairs(sigExcludes) do
            if string.find(sig, ex, 1, true) then
              matched = false
              break
            end
          end
        end

        if matched then
          local addr = mono_compile_method(m.method)
          if addr == 0 then
            print(string.format("mono_registerSymbolBySignatureMatch: ERROR matched but failed to compile - %s.%s.%s",
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

    print(string.format("mono_registerSymbolBySignatureMatch: ERROR no matching signature - %s.%s.%s",
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
    print("[discrete] No process attached. File -> Open Process first.")
    return false
  end
  if _G.lastMonoPID == nil then _G.lastMonoPID = -1 end
  if monopipe == nil or _G.lastMonoPID ~= pid then
    if monopipe ~= nil and _G.SafeMonoDestroy then
      pcall(_G.SafeMonoDestroy)
    end
    local ok, err = pcall(LaunchMonoDataCollector)
    if not ok then
      print(string.format("[discrete] LaunchMonoDataCollector failed: %s",
                          tostring(err)))
      return false
    end
    _G.lastMonoPID = pid
  end
  return monopipe ~= nil
end

-- Symmetric type-name match. CE's `mono_method_get_parameters` reports
-- `typename` with inconsistent namespace prefixes — `System.Int32`
-- keeps `System.`, but `UnityEngine.FindObjectsInactive` may come back
-- as just `FindObjectsInactive` (CE strips the assembly's "primary"
-- namespace in some versions). Plain substring would either over- or
-- under-match. Tail-match rule:
--   * exact string equality, OR
--   * one side ends with "." .. (the other) — i.e. the shorter form
--     is the basename of the longer form.
-- Crucially, "System.Int32" tail-matches neither "UInt32" nor
-- "System.UInt32", so the long-standing Int32/UInt32 substring trap
-- (memory: feedback_signaturematch_substring) is preserved.
local function _discrete_typename_match(pname, needle)
  if type(pname) ~= "string" or type(needle) ~= "string" then return false end
  if pname == needle then return true end
  -- pname ends with ".needle" → pname is namespaced, needle is bare
  if #pname > #needle then
    local tail = pname:sub(-#needle - 1)
    if tail == "." .. needle then return true end
  end
  -- needle ends with ".pname" → needle is namespaced, pname is bare
  if #needle > #pname then
    local tail = needle:sub(-#pname - 1)
    if tail == "." .. pname then return true end
  end
  return false
end

local function _discrete_find_class(namespace, classname)
  -- Fast path: CE's built-in (iterates cached image list).
  local r = mono_findClass(namespace, classname)
  if r and r ~= 0 then return r end

  -- Fallback: force enumeration + per-image search. Unity 2022+ splits
  -- UnityEngine into many modular DLLs (CoreModule / PhysicsModule /...)
  -- and CE's cached image list is sometimes stale on first lookup after
  -- the data collector launches.
  local imgs = mono_enumImages()
  if imgs then
    for _, entry in ipairs(imgs) do
      local img = (type(entry) == "table" and entry.image) or entry
      if mono_image_findClass and img and img ~= 0 then
        r = mono_image_findClass(img, namespace, classname)
        if r and r ~= 0 then return r end
      end
    end
  end
  return nil
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
      print(string.format("[discrete] method not found - %s.%s.%s (class missing)",
                          namespace, classname, methodname))
      return false
    end
    local methods = mono_class_enumMethods(cls)
    for _, mm in ipairs(methods or {}) do
      if mm.name == methodname then m = mm.method; break end
    end
    if not m or m == 0 then
      print(string.format("[discrete] method not found - %s.%s.%s",
                          namespace, classname, methodname))
      return false
    end
  end

  local addr = mono_compile_method(m)
  if addr == 0 then
    print(string.format("[discrete] could not compile - %s.%s.%s",
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
    print(string.format("[discrete] class not found - %s.%s",
                        namespace, classname))
    return false
  end

  local methods = mono_class_enumMethods(cls)
  if not methods then
    print(string.format("[discrete] no methods in %s.%s",
                        namespace, classname))
    return false
  end

  paramTypes = paramTypes or {}
  local candidates = 0          -- name-matched overloads we considered
  local diag = {}               -- shape strings for the post-mortem log
  for _, m in ipairs(methods) do
    if m.name == methodname then
      candidates = candidates + 1
      -- pcall — some uncompiled / generic methods throw inside
      -- mono_method_get_parameters. Treat any failure as "skip this
      -- overload, try the next" rather than letting the whole AA die.
      local ok, p = pcall(mono_method_get_parameters, m.method)
      if not ok or not p or type(p.parameters) ~= "table" then
        diag[#diag+1] = "  (introspection unavailable — likely generic / uncompiled)"
      elseif #p.parameters ~= #paramTypes then
        -- Count mismatch — won't match regardless of which path. Log
        -- and continue.
        local parts = {}
        for i, e in ipairs(p.parameters) do
          parts[i] = string.format("%s %s", tostring(e.typename or "<nil>"),
                                            tostring(e.name or "?"))
        end
        diag[#diag+1] = string.format("  (%d) %s",
                                      #p.parameters, table.concat(parts, ", "))
      else
        -- Count matches. Try the typename path first; on failure,
        -- fall back to signature-string substring matching. The
        -- typename field on `parameters[i]` is unreliable across CE
        -- versions / Mono targets — Unity 2022+ Mono returns it as
        -- nil for every parameter on some builds, even though
        -- `mono_method_getSignature` renders the full type info
        -- cleanly. Signature fallback handles that case.
        local matched = true
        for i = 1, #paramTypes do
          local entry = p.parameters[i]
          local pname = entry and entry.typename
          if not _discrete_typename_match(pname, paramTypes[i]) then
            matched = false
            break
          end
        end

        local via = "typename"
        if not matched then
          local okS, sig = pcall(mono_method_getSignature, m.method)
          if okS and type(sig) == "string" then
            matched = true
            for i = 1, #paramTypes do
              if type(paramTypes[i]) ~= "string"
                  or not string.find(sig, paramTypes[i], 1, true) then
                matched = false
                break
              end
            end
            if matched then via = "signature" end
          end
        end

        if matched then
          local addr = mono_compile_method(m.method)
          if addr == 0 then
            print(string.format("[discrete] matched but failed to compile - %s.%s.%s",
                                namespace, classname, methodname))
            return false
          end
          registerSymbol(symbolname, addr)
          print(string.format("[discrete] registered %s = %X  (matched via %s)",
                              symbolname, addr, via))
          return true
        end

        -- Both paths failed for this overload — log its shape so the
        -- user can see why neither typename nor signature substring
        -- match landed.
        local parts = {}
        for i, e in ipairs(p.parameters) do
          parts[i] = string.format("%s %s", tostring(e.typename or "<nil>"),
                                            tostring(e.name or "?"))
        end
        local okS, sig = pcall(mono_method_getSignature, m.method)
        local sigStr = (okS and type(sig) == "string") and sig or "<unavailable>"
        diag[#diag+1] = string.format("  (%d) %s  -- sig: %s",
                                      #p.parameters, table.concat(parts, ", "),
                                      sigStr)
      end
    end
  end

  -- Reaching here means: class found, methods enumerated, ≥0 named matches
  -- but no full match. Dump what we saw so the user can diagnose typename
  -- mismatches without rebuilding the helper.
  print(string.format("[discrete] no matching overload - %s.%s.%s",
                      namespace, classname, methodname))
  print(string.format("[discrete]   looked at %d candidate(s); expected (%d): %s",
                      candidates, #paramTypes, table.concat(paramTypes, ", ")))
  for i, line in ipairs(diag) do print("[discrete] " .. line) end
  return false
end

-- discrete_mono_registerSymbolBySignatureMatch — substring match against
-- mono_method_getSignature's rendered string, with optional excludes.
_G.discrete_mono_registerSymbolBySignatureMatch = function(symbolname, namespace, classname,
                                                           methodname, sigContains, sigExcludes)
  if not _discrete_ensure_attached() then return false end

  local cls = _discrete_find_class(namespace, classname)
  if not cls then
    print(string.format("[discrete] class not found - %s.%s",
                        namespace, classname))
    return false
  end

  local methods = mono_class_enumMethods(cls)
  if not methods then
    print(string.format("[discrete] no methods in %s.%s",
                        namespace, classname))
    return false
  end

  for _, m in ipairs(methods) do
    if m.name == methodname then
      -- pcall — same defensive shape as registerSymbolEx above.
      -- mono_method_getSignature can throw or return nil for
      -- generic / uncompiled methods; skip past those rather
      -- than crashing the AA.
      local ok, sig = pcall(mono_method_getSignature, m.method)
      if ok and type(sig) == "string" then
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
            print(string.format("[discrete] matched but failed to compile - %s.%s.%s",
                                namespace, classname, methodname))
            return false
          end
          registerSymbol(symbolname, addr)
          print(string.format("[discrete] registered %s = %X (sig=%s)",
                              symbolname, addr, sig))
          return true
        end
      end
    end
  end

  print(string.format("[discrete] no matching signature - %s.%s.%s",
                      namespace, classname, methodname))
  return false
end

print("[discrete] mono_registersymbol_helpers.lua loaded — DLL-free Mono symbol register helpers ready.")
