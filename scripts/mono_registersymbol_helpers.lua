-- ============================================================
-- mono_registersymbol_helpers.lua — DLL-free Mono symbol registration.
-- ============================================================
--
-- Pure-CE-Mono-API helpers. Lets users build cheat tables that
-- registerSymbol() one or more Mono method overloads WITHOUT ever
-- injecting the UnityCEDumper DLL. Uses CE's built-in `mono_*` Lua
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
        local p = mono_method_get_parameters(m.method)
        local matched = true

        if #p.parameters ~= #paramTypes then
          matched = false
        else
          for i = 1, #paramTypes do
            if not string.find(p.parameters[i].typename, paramTypes[i], 1, true) then
              matched = false
              break
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

print("[discrete] mono_registersymbol_helpers.lua loaded — DLL-free Mono symbol register helpers ready.")
