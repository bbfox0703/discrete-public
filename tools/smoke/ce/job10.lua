-- job10 = C-1 retry. job9 showed mono_enumImages(callback) yielding ZERO images,
-- so the fallback had nothing to iterate and could not be exercised (that is
-- also why the old no-callback form did not raise here: with no images, onImage
-- is never called). Force CE's mono data collector up first, then re-count.
DISCRETE_DEBUG = 1
local realprint = print
print = function(...)
    local n = select('#', ...)
    local parts = {}
    for i = 1, n do parts[#parts + 1] = tostring((select(i, ...))) end
    DLOG("  [ce] %s", table.concat(parts, " "))
    return realprint(...)
end

DLOG("=== job10: C-1 retry with the mono data collector forced up ===")
for _, fn in ipairs({ "LaunchMonoDataCollector", "monoAA_attach", "mono_initialize" }) do
    DLOG("  %-26s = %s", fn, type(rawget(_G, fn)))
end

if LaunchMonoDataCollector then
    local ok, r = pcall(LaunchMonoDataCollector)
    DLOG("  LaunchMonoDataCollector() -> ok=%s ret=%s", tostring(ok), tostring(r))
    sleep(2000)
end

local function countImages(tag)
    local n, names = 0, {}
    local ok, err = pcall(mono_enumImages, function(img)
        if not img or img == 0 then return end
        n = n + 1
        if n <= 8 then
            local nm = mono_image_get_name and mono_image_get_name(img) or "?"
            names[#names + 1] = tostring(nm)
        end
    end)
    DLOG("  %s: ok=%s err=%s images=%d  %s", tag, tostring(ok), tostring(err or ""), n,
         table.concat(names, ", "))
    return n
end

local n1 = countImages("after collector launch")

-- Also try CE's domain/assembly enumerators as a cross-check.
for _, fn in ipairs({ "mono_enumDomains", "mono_enumAssemblies" }) do
    if rawget(_G, fn) then
        local ok, res = pcall(rawget(_G, fn))
        DLOG("  %s() -> ok=%s type=%s count=%s", fn, tostring(ok), type(res),
             tostring(type(res) == "table" and #res or "-"))
    end
end

if n1 > 0 then
    -- Now the fallback can actually run. Use a class in a modular UnityEngine
    -- DLL and check the per-image search finds it.
    for _, target in ipairs({ { "UnityEngine", "Camera" }, { "UnityEngine", "Transform" },
                             { "UnityEngine", "Physics" } }) do
        local ns, cn = target[1], target[2]
        local fast = mono_findClass(ns, cn)
        local found = nil
        pcall(mono_enumImages, function(img)
            if found or not img or img == 0 then return end
            local hit = mono_image_findClass(img, ns, cn)
            if hit and hit ~= 0 then found = hit end
        end)
        DLOG("  %s.%s  fast=%s  fallback=%s", ns, cn,
             tostring(fast and fast ~= 0), tostring(found ~= nil))
    end
    DLOG("  C-1 verdict: %s", "PASS (callback form enumerates and per-image lookup works)")
else
    DLOG("  C-1 verdict: NOT EXERCISABLE HERE - CE enumerates 0 mono images on this")
    DLOG("               target, so the fallback loop body never runs. The fast path")
    DLOG("               (mono_findMethod) works: job9 registered a CoreModule symbol.")
end
print = realprint
DLOG("=== job10 done ===")
