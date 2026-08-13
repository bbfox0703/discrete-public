-- Resident job dispatcher for the CE-side live-smoke items (G2 / G5 / G3-P3).
--
-- Load it ONCE, by hand, in CE:  Table -> Show Cheat Table Lua Script
-- (Ctrl+Alt+L), paste this single line, Execute:
--
--     dofile[[D:\Github\discrete\tools\smoke\ce\dispatcher.lua]]
--
-- After that every CE test is driven from OUTSIDE CE by dropping job<N>.lua
-- into this folder - no further GUI interaction, no writes into CE's install
-- directory. Results are appended to ce_log.txt beside this file.
--
-- Stop it with:  DISCRETE_JOBS.stop()

local function scriptDir()
    local src = debug and debug.getinfo and debug.getinfo(1, "S")
    if src and src.source and src.source:sub(1, 1) == "@" then
        local d = src.source:sub(2):match("(.*[\\/])")
        if d and #d > 0 then return d end
    end
    return ".\\"
end

local BASE = scriptDir()
local LOG = BASE .. "ce_log.txt"

local function logline(s)
    local f = io.open(LOG, "a")
    if f then f:write(tostring(s) .. "\n") f:close() end
end

-- Helper the job scripts use for their own output.
function DLOG(fmt, ...)
    logline(select('#', ...) > 0 and string.format(fmt, ...) or tostring(fmt))
end

-- Heartbeat: the objective measure for the "CE must stay responsive" items.
-- A blocked GUI thread stops firing timers, so a gap in these timestamps is the
-- FAIL signal - no need to watch the window by eye.
function DHEARTBEAT(tag, ticks, everyMs)
    local n, t0 = 0, getTickCount()
    local hb
    hb = createTimer(everyMs or 100, function()
        n = n + 1
        DLOG("  [hb:%s] tick %d at +%d ms", tag, n, getTickCount() - t0)
        if n >= (ticks or 20) then hb.destroy() DLOG("  [hb:%s] done", tag) end
    end)
    return hb
end

local next_job = 0

local function poll()
    local path = BASE .. "job" .. next_job .. ".lua"
    local f = io.open(path, "rb")
    if f == nil then return end
    f:close()
    -- Rename before running: a crash must never re-run the same job forever.
    local ran = BASE .. "ran_job" .. next_job .. ".lua"
    os.remove(ran)
    os.rename(path, ran)
    logline("")
    logline("######## job" .. next_job .. " start (tick=" .. getTickCount() .. ") ########")
    local ok, err = pcall(dofile, ran)
    if not ok then logline("JOB-ERROR: " .. tostring(err)) end
    logline("######## job" .. next_job .. " end (tick=" .. getTickCount() .. ") ########")
    logline("JOB" .. next_job .. "-COMPLETE")
    next_job = next_job + 1
end

DISCRETE_JOBS = DISCRETE_JOBS or {}
if DISCRETE_JOBS.timer then pcall(function() DISCRETE_JOBS.timer.destroy() end) end
DISCRETE_JOBS.timer = createTimer(500, poll)
DISCRETE_JOBS.stop = function() pcall(function() DISCRETE_JOBS.timer.destroy() end) end
DISCRETE_JOBS.dir = BASE

logline("=== dispatcher armed (CE " .. tostring(getCEVersion and getCEVersion() or "?") .. ") ===")
print("[discrete-smoke] dispatcher armed; polling " .. BASE .. "job<N>.lua")
print("[discrete-smoke] log: " .. LOG)
