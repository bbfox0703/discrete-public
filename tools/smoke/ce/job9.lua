-- job9 = G5 C-1: the modular-UnityEngine image fallback.
--
-- CE's mono_enumImages is CALLBACK-ONLY and returns nothing (monoscript.lua):
--     function mono_enumImages(onImage) ... onImage(image) ... end
-- The old code did `local imgs = mono_enumImages()`, which left onImage nil, so
-- the first image raised "attempt to call a nil value" - i.e. the fallback blew
-- up exactly when it was needed. Core Keeper is modular Unity (CoreModule etc.),
-- so this is the right target.
DISCRETE_DEBUG = 1
local realprint = print
print = function(...)
    local n = select('#', ...)
    local parts = {}
    for i = 1, n do parts[#parts + 1] = tostring((select(i, ...))) end
    DLOG("  [ce] %s", table.concat(parts, " "))
    return realprint(...)
end

DLOG("=== job9: C-1 mono image fallback (modular UnityEngine) ===")
DLOG("  attached pid=%s", tostring(getOpenedProcessID()))

-- (a) reproduce the OLD call shape: no callback argument
local okOld, errOld = pcall(function() local imgs = mono_enumImages() return imgs end)
DLOG("  (a) mono_enumImages() with NO callback -> ok=%s err=%s",
     tostring(okOld), tostring(errOld or "(returned nothing)"))

-- (b) the fixed call shape: pass a callback, count the images
local count, names = 0, {}
local okNew, errNew = pcall(mono_enumImages, function(img)
    if not img or img == 0 then return end
    count = count + 1
    if count <= 6 then
        local nm = mono_image_get_name and mono_image_get_name(img) or "?"
        names[#names + 1] = tostring(nm)
    end
end)
DLOG("  (b) mono_enumImages(callback) -> ok=%s err=%s images=%d",
     tostring(okNew), tostring(errNew or ""), count)
DLOG("      first images: %s", table.concat(names, ", "))

-- (c) per-image class lookup, which is what the fallback actually does
local found = nil
pcall(mono_enumImages, function(img)
    if found or not img or img == 0 then return end
    local hit = mono_image_findClass and mono_image_findClass(img, "UnityEngine", "Camera")
    if hit and hit ~= 0 then found = hit end
end)
DLOG("  (c) fallback found UnityEngine.Camera: %s", tostring(found ~= nil))

-- (d) and now the shipped helper end to end
package.path = [[D:\Github\discrete\scripts\?.lua;]] .. package.path
local okLoad, errLoad = pcall(dofile,
    [[D:\Github\discrete\scripts\mono_registersymbol_helpers.lua]])
DLOG("  (d) dofile helpers -> ok=%s err=%s", tostring(okLoad), tostring(errLoad or ""))
if discrete_mono_registerSymbol then
    local ok1 = discrete_mono_registerSymbol("SmokeCamGetMain", "UnityEngine", "Camera", "get_main")
    DLOG("      discrete_mono_registerSymbol(UnityEngine.Camera.get_main) -> %s", tostring(ok1))
    local a = nil
    pcall(function() a = getAddress("SmokeCamGetMain") end)
    DLOG("      symbol resolves to: %s", a and string.format("0x%X", a) or "nil")
    DLOG("  C-1 verdict: %s",
         (okNew and found and ok1) and "PASS" or "FAIL")
else
    DLOG("      discrete_mono_registerSymbol not defined")
end
print = realprint
DLOG("=== job9 done ===")
