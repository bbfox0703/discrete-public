// BootstrapAaScriptGenerator.cs — Phase 22c.
//
// Builds the "Load Discrete Bridge" Auto-Assembler script that the
// Phase 22d Tools-menu RelayCommand will push into CE via
// AobMakerBridge.CreateAAScriptAsync (after the 7 InjectTableFile
// calls from Phase 22b). End-users see a single checkbox in CE's
// address list — toggling it loads the entire Discrete bridge.
//
// The generated [ENABLE] block:
//   1. Idempotency guard: returns early if _G.discrete_bridgeLoaded is set.
//   2. Pulls each embedded file out of the CT via findTableFile +
//      Stream.copyFrom (the canonical CE pattern; see
//      autorun/java.lua line 537-541 in CE's repo). Stream.DataString
//      gives us the raw UTF-8 we wrote in InjectTableFile.
//   3. Pre-populates package.preload['utils'] with utils.lua's chunk
//      BEFORE loading unity_dump.lua. Without this, unity_dump.lua's
//      `require 'utils'` (line 55) fails because the CT context has
//      no filesystem package.path — CE's package.path doesn't include
//      our table files.
//   4. Loads unity_dump.lua. Its top-level main() runs backend
//      detection + DLL injection + StartPipe automatically. We do
//      NOT pre-validate the DLL pipe — unity_dump's error messages
//      ("No process attached. File → Open Process first." etc.) are
//      better than anything we'd write here.
//   5. Loads the 5 standalone bridge modules (no inter-module
//      dependencies — verified 2026-05-12 by repo-wide grep).
//   6. Smoke-tests Discrete_GetBuildNumber — confirms the DLL is
//      actually injected and CE's symbolhandler has scanned it.
//      Failure here is a soft warn (the bridge can still be useful
//      for some operations even if symbolhandler is lagging).
//   7. Sets _G.discrete_bridgeLoaded so re-enabling the entry no-ops.
//
// The generated [DISABLE] block performs a dormant teardown: it calls
// discreteShutdown() (defined by unity_dump.lua) to stop the DLL's pipe
// server + remove its runtime_invoke hook, leaving the DLL dormant but
// still mapped, then clears the global flag so the next [ENABLE] re-runs
// (which restarts the pipe without re-injecting). There is intentionally
// NO full-FreeLibrary "unload" entry: unmapping a DLL that hooks the
// per-frame runtime_invoke can't be made crash-safe, so dormant is the
// only teardown we expose.
//
// Why a const string template:
//   * The lua body is essentially fixed. Only the file list (LoadOrder)
//     varies, and even that we bake in via a Lua list literal at build
//     time so the lua side and BridgeLuaResources.LoadOrder stay in sync
//     automatically (test pins they match).
//   * No dynamic generation = trivial to AOT-publish, trivial to test
//     (string contains assertions, no full lua parser required).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UnityDumpUI.Core.Services.Export;

public static class BootstrapAaScriptGenerator
{
    /// <summary>
    /// CE memory-record description for the bootstrap entry. Phase 22d
    /// passes this to <c>AobMakerBridge.CreateAAScriptAsync</c>.
    /// Stable string — duplicate-detection downstream (when AOBMaker
    /// gains it) will key on this.
    /// </summary>
    public const string EntryDescription = "Load Discrete Bridge";

    /// <summary>
    /// Build the AA script using <see cref="BridgeLuaResources.LoadOrder"/>
    /// as the file list. This is the production overload Phase 22d calls.
    /// </summary>
    public static string Build() => Build(BridgeLuaResources.CoreFiles);

    /// <summary>
    /// Build with a caller-supplied file list — used by the feature-picker
    /// flow to emit a bootstrap that loads only the selected subset (the
    /// output of <see cref="BridgeLuaResources.ResolveFiles"/>). The list
    /// MUST contain <c>utils.lua</c> (the preload trick depends on it);
    /// <see cref="BridgeLuaResources.CoreFiles"/> guarantees this.
    /// </summary>
    public static string Build(IReadOnlyList<string> fileNames)
    {
        if (fileNames is null || fileNames.Count == 0)
            throw new ArgumentException("fileNames must contain at least one file", nameof(fileNames));

        // utils.lua MUST be present — the preload trick depends on it.
        // (unity_dump.lua, the only consumer of the preload, may or may
        // not be in the list — tests use shorter lists.)
        if (!fileNames.Contains("utils.lua", StringComparer.Ordinal))
            throw new ArgumentException(
                "fileNames must contain 'utils.lua' — bootstrap pre-populates package.preload[utils] from it",
                nameof(fileNames));

        // Build the Lua list literal: { 'utils.lua', 'unity_dump.lua', ... }.
        // We single-quote each name (no embedded quotes possible — we
        // control the source list, and the production list is .lua names).
        var listLua = new StringBuilder();
        listLua.Append("{ ");
        for (int i = 0; i < fileNames.Count; ++i)
        {
            if (i > 0) listLua.Append(", ");
            listLua.Append('\'').Append(fileNames[i]).Append('\'');
        }
        listLua.Append(" }");

        return Template
            .Replace("__BRIDGE_FILES_LIST__",      listLua.ToString())
            .Replace("__ENTRY_DESCRIPTION__",     EntryDescription);
    }

    // The AA script template. Placeholders:
    //   __BRIDGE_FILES_LIST__   — Lua list literal of filenames to load
    //                             (utils.lua FIRST so the preload trick
    //                             works for unity_dump.lua's require).
    //   __ENTRY_DESCRIPTION__   — used in log lines for self-identification.
    //
    // Kept as a verbatim string (@"…") so the lua reads the same in
    // source as it does in the generated AA script.
    private const string Template = @"[ENABLE]
{$lua}
if syntaxcheck then return end
-- Reset the shared did-anything-fail flag so the close-tail below judges THIS
-- run (docs/lua-logging.md). Any failure path sets it true again.
_G.DISCRETE_HAD_ERROR = false

-- Idempotency: re-enabling the entry should be a no-op until the user
-- explicitly disables it. CE doesn't unload lua chunks once they've
-- run, so the second [ENABLE] would otherwise duplicate every global
-- symbol unity_dump.lua + friends create. This is a benign no-op, not a
-- failure — gate its notice behind DEBUG so a quiet re-enable doesn't pop
-- CE's Lua Engine over its main window.
if _G.discrete_bridgeLoaded then
  if _G.DISCRETE_DEBUG == 1 then
    print('[Discrete] __ENTRY_DESCRIPTION__: already loaded (toggle DISABLE then re-enable to reload)')
  end
  return
end

local BRIDGE_FILES = __BRIDGE_FILES_LIST__

-- Read one CT-embedded file as a string, return a parsed-but-not-yet-
-- executed chunk. Errors are fatal — BRIDGE_FILES only lists files the
-- installer actually embedded (the feature-picker subset), so any name
-- here that findTableFile can't resolve means the CT is corrupt/stale;
-- bail the whole [ENABLE].
local function loadEmbeddedChunk(name)
  local f = findTableFile(name)
  if not f then
    error('[Discrete] missing embedded file: ' .. name ..
          ' — re-run ""Inject Discrete Bridge into CE Table"" from DiscreteUI')
  end
  local ss = createStringStream()
  ss.copyFrom(f.Stream, f.Stream.Size)
  local code = ss.DataString
  ss.destroy()
  local fn, err = load(code, '@' .. name)
  if not fn then
    error('[Discrete] parse error in ' .. name .. ': ' .. tostring(err))
  end
  return fn
end

-- Pre-populate package.preload['utils'] BEFORE running unity_dump.lua.
-- unity_dump.lua line 55 does `require 'utils'`; without this preload
-- entry, require would fall back to package.path which doesn't include
-- our CT table files. With it, require evaluates the chunk we already
-- have in hand and caches the returned module.
local utilsChunk = loadEmbeddedChunk('utils.lua')
package.preload['utils'] = utilsChunk

-- Load each remaining file. Skip utils.lua (already preloaded). Skip any
-- file that isn't actually embedded — useful in tests with shorter
-- file lists, harmless in production.
for _, name in ipairs(BRIDGE_FILES) do
  if name ~= 'utils.lua' then
    -- unity_dump.lua's main() runs at top-level — backend detection,
    -- DLL injection, StartPipe all happen on this call. Subsequent
    -- bridge files are pure module definitions (function declarations
    -- that go straight into _G).
    local chunk = loadEmbeddedChunk(name)
    chunk()
  end
end

-- Smoke: confirm the DLL pipe (\\.\pipe\DiscreteBfx) is reachable with a
-- bounded 5 s timeout via utils.pipeRpcRaw (E-M4 — uses CE's connectToPipe
-- overlapped timeout + createThread/pump so a stalled DLL never freezes CE's
-- GUI thread). Wrapped in pcall so any error flags DISCRETE_HAD_ERROR cleanly.
local okU, U = pcall(require, 'utils')
local resp, pipeErr = nil, nil
if okU and type(U) == 'table' and type(U.pipeRpcRaw) == 'function' then
  local okCall, r, e = pcall(U.pipeRpcRaw, '{""id"":0,""cmd"":""handshake"",""args"":{}}', 5000)
  if okCall then
    resp, pipeErr = r, e
  else
    pipeErr = tostring(r)
  end
else
  -- Fallback when an older embedded utils.lua lacks pipeRpcRaw.
  local pipe = io.open([[\\.\pipe\DiscreteBfx]], 'r+b')
  if pipe then
    pipe:write('{""id"":0,""cmd"":""handshake"",""args"":{}}\n'); pipe:flush()
    resp = pipe:read('*l'); pipe:close()
  else
    pipeErr = 'pipe unreachable'
  end
end
if resp then
  -- Cheap field extraction — full JSON parser is overkill for two ints
  -- and a string. The handshake response is small + well-formed; the
  -- field shapes are stable per pipe-protocol.md.
  local build   = resp:match('""build""%s*:%s*(%d+)')
  local backend = resp:match('""backend""%s*:%s*""([^""]+)""')
  if build and backend then
    -- Success is progress output — gate behind DEBUG so a clean load stays
    -- quiet and the close-tail below can dismiss the Lua Engine.
    if _G.DISCRETE_DEBUG == 1 then
      print(string.format('[Discrete] __ENTRY_DESCRIPTION__: ready — %s / DLL build %s',
                          backend, build))
    end
  else
    -- Handshake failed to parse — an error: flag it (keeps the window up) and
    -- always print so the user sees it.
    _G.DISCRETE_HAD_ERROR = true
    print('[Discrete] __ENTRY_DESCRIPTION__: bridge loaded, handshake response unparseable: '
          .. tostring(resp))
  end
else
  -- Pipe unreachable or timed out — an error: flag it + always print.
  _G.DISCRETE_HAD_ERROR = true
  print('[Discrete] __ENTRY_DESCRIPTION__: lua loaded, but DLL pipe handshake failed ('
        .. tostring(pipeErr or 'unreachable') .. ').')
  print('[Discrete]   If you ran this before DiscreteUI injected the DLL,')
  print('[Discrete]   wait a moment then disable + re-enable this entry.')
end

_G.discrete_bridgeLoaded = true

-- Close CE's Lua Engine on a clean run so it stops covering CE (docs/lua-logging.md).
if _G.DISCRETE_DEBUG ~= 1 and not _G.DISCRETE_HAD_ERROR then
  synchronize(function() local e = getLuaEngine(); if e then e.Close() end end)
end

[DISABLE]
{$lua}
if syntaxcheck then return end
-- Reset the shared did-anything-fail flag so the close-tail below judges THIS
-- run (docs/lua-logging.md).
_G.DISCRETE_HAD_ERROR = false
-- Teardown: stop the DLL's pipe server + remove its runtime_invoke hook
-- so it goes dormant. The DLL stays MAPPED — this is intentional, and the
-- only teardown we offer: a full FreeLibrary unload of a DLL that hooks
-- the per-frame runtime_invoke can't be made crash-safe, so we don't.
-- Re-enabling re-runs the loader; unity_dump.lua's main() sees the module
-- already mapped, skips re-injection, and just restarts the pipe.
-- discreteShutdown() is defined by unity_dump.lua (core), so it's present
-- whenever the bridge loaded.
if type(discreteShutdown) == 'function' then
  local ok, res = pcall(discreteShutdown)
  if not ok or res == false then
    _G.DISCRETE_HAD_ERROR = true
    print('[Discrete] __ENTRY_DESCRIPTION__: discreteShutdown() failed — '
          .. tostring(ok and 'DLL teardown returned false' or res))
  end
else
  -- Missing discreteShutdown is a failure worth surfacing: flag it + print.
  _G.DISCRETE_HAD_ERROR = true
  print('[Discrete] __ENTRY_DESCRIPTION__: discreteShutdown() unavailable — DLL left running')
end
_G.discrete_bridgeLoaded = nil
-- Teardown progress is gated so a clean disable stays quiet.
if _G.DISCRETE_DEBUG == 1 then
  print('[Discrete] __ENTRY_DESCRIPTION__: teardown — pipe stopped, ready for reload')
end

-- Close CE's Lua Engine on a clean run so it stops covering CE (docs/lua-logging.md).
if _G.DISCRETE_DEBUG ~= 1 and not _G.DISCRETE_HAD_ERROR then
  synchronize(function() local e = getLuaEngine(); if e then e.Close() end end)
end
";
}
