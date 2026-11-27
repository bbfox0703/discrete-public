#!/usr/bin/env python3
"""Live Lua harness test for scripts/mono_registersymbol_helpers.lua (E-M5).

Executes the actual Lua script under a mocked Cheat Engine `mono_*` environment
via `lua` (or `luac` syntax check) to verify:
  1. Mono shorthand primitive aliases (`int`, `single`, `string`, `bool`, ...)
     match CLR `System.*` names in both `mono_method_get_parameters` and
     `mono_method_getSignature` fallback paths.
  2. Exact parameter order (`(Int32, String)` vs `(String, Int32)`) and exact
     parameter count (`(Int32)` vs `(Int32, Int32)`).
  3. Suffix sensitivity (`System.Int32` does NOT match `System.Int32[]` or
     `int[]` or `int&`; `System.Int32[]` matches `int[]`; `System.Int32&`
     matches `int&`).
  4. Namespace tail-match (`FindObjectsInactive` <-> `UnityEngine.FindObjectsInactive`)
     without `Int32` matching `UInt32`.
  5. Fallback to positional `mono_method_getSignature` when
     `mono_method_get_parameters` throws or returns nil typenames.
  6. Multi-dimensional arrays & generic commas in signatures
     (`Dictionary<string,int>,int[,]`).
  7. Ambiguity rejection (`eprint`, `_G.DISCRETE_HAD_ERROR = true`, returns
     `false`, does NOT register any symbol) in BOTH `discrete_mono_registerSymbolEx`
     and `discrete_mono_registerSymbolBySignatureMatch`.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
HELPER_LUA = REPO_ROOT / "scripts" / "mono_registersymbol_helpers.lua"

LUA_HARNESS = r'''
local helperPath = arg[1]
assert(helperPath, "missing helper path")

-- Mock CE globals
local registered = {}
local errors = {}

local _origPrint = print
_G.print = function(msg)
  errors[#errors + 1] = tostring(msg)
end
_G.getOpenedProcessID = function() return 1234 end
_G.monopipe = {}
_G.LaunchMonoDataCollector = function() return true end
_G.registerSymbol = function(name, addr)
  registered[name] = addr
end

local currentMethods = {}
local currentParams = {}
local currentSigs = {}
local currentAddrs = {}

_G.mono_findClass = function(ns, cls) return 1 end
_G.mono_class_enumMethods = function(cls) return currentMethods end
_G.mono_method_get_parameters = function(m)
  local p = currentParams[m]
  if type(p) == "function" then return p() end
  return p
end
_G.mono_method_getSignature = function(m)
  local s = currentSigs[m]
  if type(s) == "function" then return s() end
  return s
end
_G.mono_compile_method = function(m)
  return currentAddrs[m] or 0
end

-- Load the real helper script
dofile(helperPath)

local function reset(spec)
  registered = {}
  errors = {}
  rawset(_G, "DISCRETE_HAD_ERROR", false)
  currentMethods = {}
  currentParams = {}
  currentSigs = {}
  currentAddrs = {}
  for idx, item in ipairs(spec) do
    local id = idx * 10
    currentMethods[#currentMethods + 1] = { name = item.name, method = id }
    currentParams[id] = item.params
    currentSigs[id] = item.sig
    currentAddrs[id] = item.addr
  end
end

local passed = 0
local function check(cond, msg)
  if not cond then
    _origPrint("FAIL: " .. tostring(msg))
    os.exit(1)
  end
  passed = passed + 1
end

-- 1. Swapped order + repeated count + array/byref + zero-arg (via signature fallback with nil typenames & Mono shorthands)
reset({
  { name = "Set", params = { parameters = {} }, sig = "", addr = 0x1001 },
  { name = "Set", params = { parameters = { { typename = nil } } }, sig = "int", addr = 0x1002 },
  { name = "Set", params = { parameters = { { typename = nil }, { typename = nil } } }, sig = "int,int", addr = 0x1003 },
  { name = "Set", params = { parameters = { { typename = nil }, { typename = nil } } }, sig = "int,string", addr = 0x1004 },
  { name = "Set", params = { parameters = { { typename = nil }, { typename = nil } } }, sig = "string,int", addr = 0x1005 },
  { name = "Set", params = { parameters = { { typename = nil } } }, sig = "int[]", addr = 0x1006 },
  { name = "Set", params = { parameters = { { typename = nil } } }, sig = "int&", addr = 0x1007 },
  { name = "Set", params = { parameters = { { typename = nil } } }, sig = "uint", addr = 0x1008 },
})

check(_G.discrete_mono_registerSymbolEx("sym_zero", "", "Foo", "Set", {}) == true, "zero-arg match")
check(registered["sym_zero"] == 0x1001, "zero-arg addr")

check(_G.discrete_mono_registerSymbolEx("sym_1int", "", "Foo", "Set", { "System.Int32" }) == true, "1-int match")
check(registered["sym_1int"] == 0x1002, "1-int must not hit int,int or int[] or int& or uint")

check(_G.discrete_mono_registerSymbolEx("sym_2int", "", "Foo", "Set", { "System.Int32", "System.Int32" }) == true, "2-int match")
check(registered["sym_2int"] == 0x1003, "2-int addr")

check(_G.discrete_mono_registerSymbolEx("sym_int_str", "", "Foo", "Set", { "System.Int32", "System.String" }) == true, "int,str match")
check(registered["sym_int_str"] == 0x1004, "int,str addr")

check(_G.discrete_mono_registerSymbolEx("sym_str_int", "", "Foo", "Set", { "System.String", "System.Int32" }) == true, "str,int match")
check(registered["sym_str_int"] == 0x1005, "str,int addr")

check(_G.discrete_mono_registerSymbolEx("sym_arr", "", "Foo", "Set", { "System.Int32[]" }) == true, "int[] match")
check(registered["sym_arr"] == 0x1006, "int[] addr")

check(_G.discrete_mono_registerSymbolEx("sym_ref", "", "Foo", "Set", { "System.Int32&" }) == true, "int& match")
check(registered["sym_ref"] == 0x1007, "int& addr")

check(_G.discrete_mono_registerSymbolEx("sym_uint", "", "Foo", "Set", { "System.UInt32" }) == true, "uint match")
check(registered["sym_uint"] == 0x1008, "uint addr")
check(rawget(_G, "DISCRETE_HAD_ERROR") == false, "no errors on clean matches")

-- 2. Mono shorthand primitives + namespace tail-match + generic/multi-dim commas + get_parameters throwing
reset({
  {
    name = "Apply",
    params = function() error("generic introspection fail") end,
    sig = "(single, bool, long, Dictionary<string,int>, int[,], FindObjectsInactive)",
    addr = 0x2001,
  },
})

check(_G.discrete_mono_registerSymbolEx("sym_apply", "Game", "C", "Apply", {
  "System.Single",
  "System.Boolean",
  "System.Int64",
  "Dictionary<string,int>",
  "System.Int32[,]",
  "UnityEngine.FindObjectsInactive",
}) == true, "Mono shorthand + tail-match + throwing get_parameters fallback")
check(registered["sym_apply"] == 0x2001, "Apply addr")

-- 3. Ambiguity rejection in discrete_mono_registerSymbolBySignatureMatch
reset({
  { name = "Pair", params = nil, sig = "int,string", addr = 0x3001 },
  { name = "Pair", params = nil, sig = "string,int", addr = 0x3002 },
})

local okAmb = _G.discrete_mono_registerSymbolBySignatureMatch(
  "sym_amb", "", "Foo", "Pair", { "System.Int32", "System.String" }, {})
check(okAmb == false, "BySignatureMatch must reject ambiguous multi-overload match")
check(registered["sym_amb"] == nil, "BySignatureMatch must not register symbol on ambiguity")
check(rawget(_G, "DISCRETE_HAD_ERROR") == true, "BySignatureMatch must set DISCRETE_HAD_ERROR on ambiguity")

-- 4. BySignatureMatch must NOT match System.Int32 against System.Int32[]
reset({
  { name = "OnlyArr", params = nil, sig = "System.Int32[]", addr = 0x4001 },
})
local okArr = _G.discrete_mono_registerSymbolBySignatureMatch(
  "sym_no_arr", "", "Foo", "OnlyArr", { "System.Int32" }, {})
check(okArr == false, "BySignatureMatch must not substring-match Int32 against Int32[]")
check(registered["sym_no_arr"] == nil, "no symbol registered")

-- 5. Ambiguity rejection in discrete_mono_registerSymbolEx (identical duplicate overloads)
reset({
  { name = "Dup", params = { parameters = { { typename = "System.Int32" } } }, sig = "int", addr = 0x5001 },
  { name = "Dup", params = { parameters = { { typename = "int" } } }, sig = "int", addr = 0x5002 },
})
local okDup = _G.discrete_mono_registerSymbolEx("sym_dup", "", "Foo", "Dup", { "System.Int32" })
check(okDup == false, "registerSymbolEx must reject ambiguous duplicate matches")
check(registered["sym_dup"] == nil, "registerSymbolEx must not register on ambiguity")
check(rawget(_G, "DISCRETE_HAD_ERROR") == true, "registerSymbolEx must set DISCRETE_HAD_ERROR on ambiguity")

-- 6. Typename path exact order (when typenames are populated, no signature needed)
reset({
  { name = "Ord", params = { parameters = { { typename = "System.Int32" }, { typename = "System.String" } } }, sig = nil, addr = 0x6001 },
  { name = "Ord", params = { parameters = { { typename = "System.String" }, { typename = "System.Int32" } } }, sig = nil, addr = 0x6002 },
})
check(_G.discrete_mono_registerSymbolEx("sym_ord2", "", "Foo", "Ord", { "System.String", "System.Int32" }) == true, "typename order")
check(registered["sym_ord2"] == 0x6002, "typename order addr")

_origPrint(string.format("OK: %d Lua checks passed", passed))
'''


def main() -> int:
    lua_bin = shutil.which("lua")
    if not lua_bin:
        print("SKIP: lua interpreter not found on PATH", file=sys.stderr)
        return 1

    luac_bin = shutil.which("luac")
    if luac_bin:
        rc = subprocess.run([luac_bin, "-p", str(HELPER_LUA)], check=False)
        if rc.returncode != 0:
            return rc.returncode

    with tempfile.NamedTemporaryFile(
        mode="w", suffix=".lua", encoding="utf-8", delete=False
    ) as tf:
        tf.write(LUA_HARNESS)
        harness_path = Path(tf.name)

    try:
        proc = subprocess.run(
            [lua_bin, str(harness_path), str(HELPER_LUA)],
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )
        if proc.stdout:
            print(proc.stdout.strip())
        if proc.stderr:
            print(proc.stderr.strip(), file=sys.stderr)
        return proc.returncode
    finally:
        harness_path.unlink(missing_ok=True)


if __name__ == "__main__":
    raise SystemExit(main())
