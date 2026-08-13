-- job1 = C-2: Mode-B .CT bootstrap. The fix under test is _extractOne(), which
-- has to cope with CE's Stream:read() returning a BYTETABLE rather than a string
-- (pre-fix it died on the very first embedded file with
-- "bad argument #1 to 'write' (string expected, got table)").
--
-- Embeds the real dist files INCLUDING the ~1 MB Discrete-Mono.dll, because the
-- multi-MB blob is what makes the chunked string.char(table.unpack(...)) path
-- matter: one-shot unpack blows LUAI_MAXSTACK.
local DIST = [[D:\Github\discrete\dist\]]
local HERE = [[D:\Github\discrete\tools\smoke\ce\]]
local EMBED = { "utils.lua", "unity_dump.lua", "Discrete-Mono.dll" }

local function fsize(p)
    local f = io.open(p, "rb")
    if not f then return nil end
    local n = f:seek("end")
    f:close()
    return n
end

local function tailbytes(p, n)
    local f = io.open(p, "rb")
    if not f then return nil end
    local sz = f:seek("end")
    f:seek("set", math.max(0, sz - n))
    local b = f:read(n)
    f:close()
    return b
end

DLOG("=== job1: C-2 Mode-B .CT bootstrap ===")

-- 1. embed each file as a table file (this is Table -> Add file..., scripted)
for _, name in ipairs(EMBED) do
    local existing = findTableFile(name)
    if existing then existing.delete() end
    local tf = createTableFile(name, DIST .. name)
    local ok = tf ~= nil
    local streamSize = ok and tf.Stream and tf.Stream.Size or -1
    DLOG("  embed %-22s src=%-9s tableFile.Stream.Size=%s  %s",
         name, tostring(fsize(DIST .. name)), tostring(streamSize),
         (streamSize == fsize(DIST .. name)) and "SIZE-OK" or "SIZE-MISMATCH")
end

-- 2. run the SHIPPED bootstrap (only edit vs scripts/: the documented
--    "uncomment the backend DLL you embedded" line)
local okLoad, errLoad = pcall(dofile, HERE .. "ce_table_bootstrap_test.lua")
DLOG("  dofile bootstrap script: %s %s", tostring(okLoad), tostring(errLoad or ""))

local okBoot, errBoot = pcall(bootstrap)
DLOG("  bootstrap() -> ok=%s err=%s", tostring(okBoot), tostring(errBoot or ""))

-- With no process open, bootstrap MUST get past extraction and fail at backend
-- detection. Reaching that error is the proof extraction succeeded.
local reachedDetect = (not okBoot) and tostring(errBoot):find("backend detect failed") ~= nil
DLOG("  reached backend-detect stage (=> extraction completed): %s", tostring(reachedDetect))
if (not okBoot) and tostring(errBoot):find("string expected, got table") then
    DLOG("  *** C-2 FAIL: the bytetable bug is still present ***")
end

-- 3. compare every extracted file against its source
local temp = os.getenv("TEMP") .. [[\Discrete_Dumper\default-table\]]
DLOG("  extract dir = %s", temp)
local allOk = true
for _, name in ipairs(EMBED) do
    local a, b = fsize(DIST .. name), fsize(temp .. name)
    local ta, tb = tailbytes(DIST .. name, 64), tailbytes(temp .. name, 64)
    local same = (a ~= nil and a == b and ta == tb)
    if not same then allOk = false end
    DLOG("  %-22s src=%-9s extracted=%-9s tail64=%s  %s",
         name, tostring(a), tostring(b), (ta == tb) and "same" or "DIFFER",
         same and "OK" or "MISMATCH")
end
DLOG("  C-2 verdict: %s", (reachedDetect and allOk) and "PASS" or "FAIL")

-- 4. and now the real distribution shape: save to a .CT, load it back, and
--    re-extract, so the bytes also pass through CE's .CT serializer.
local ct = HERE .. "modeb_roundtrip.CT"
local okSave = pcall(saveTable, ct)
DLOG("  saveTable(%s) -> %s size=%s", ct, tostring(okSave), tostring(fsize(ct)))
if okSave then
    local okLoad2 = pcall(loadTable, ct)
    DLOG("  loadTable -> %s", tostring(okLoad2))
    local present = {}
    for _, name in ipairs(EMBED) do
        local tf = findTableFile(name)
        present[#present + 1] = string.format("%s=%s", name,
            tf and tostring(tf.Stream.Size) or "MISSING")
    end
    DLOG("  table files after reload: %s", table.concat(present, "  "))
    -- wipe the extract dir so re-extraction cannot pass on stale files
    for _, name in ipairs(EMBED) do os.remove(temp .. name) end
    local okBoot2, errBoot2 = pcall(bootstrap)
    DLOG("  bootstrap() after .CT reload -> ok=%s err=%s", tostring(okBoot2), tostring(errBoot2 or ""))
    local rtOk = true
    for _, name in ipairs(EMBED) do
        local a, b = fsize(DIST .. name), fsize(temp .. name)
        if a ~= b then rtOk = false end
        DLOG("  round-trip %-22s src=%-9s extracted=%-9s %s",
             name, tostring(a), tostring(b), (a == b) and "OK" or "MISMATCH")
    end
    DLOG("  .CT round-trip verdict: %s", rtOk and "PASS" or "FAIL")
end
DLOG("=== job1 done ===")
