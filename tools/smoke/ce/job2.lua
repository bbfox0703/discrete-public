-- job2: pin down CE 7.7's Stream/TableFile API, because job1 showed
-- `stream:read(size)` returning nil (not a string, not a bytetable) - so the
-- shipped C-2 fix cannot extract anything on this CE build.
--
-- Hypothesis: CE's Lua class methods are bound closures that take arguments
-- WITHOUT self, so colon syntax passes the stream object as `count`.
local DIST = [[D:\Github\discrete\dist\]]
local OUT = [[D:\Github\discrete\tools\smoke\ce\_probe\]]
os.execute('mkdir "' .. OUT .. '" 2>nul')

local function fsize(p)
    local f = io.open(p, "rb")
    if not f then return nil end
    local n = f:seek("end")
    f:close()
    return n
end

DLOG("=== job2: CE 7.7 Stream / TableFile API probe ===")

local name = "utils.lua"
local tf = findTableFile(name) or createTableFile(name, DIST .. name)
DLOG("  tableFile type=%s  Name=%s", type(tf), tostring(tf.Name))

local s = tf.Stream
DLOG("  Stream type=%s Size=%s Position=%s", type(s), tostring(s.Size), tostring(s.Position))

-- (a) colon syntax, as the shipped code does
local okA, a = pcall(function() s.Position = 0 return s:read(16) end)
DLOG("  (a) s:read(16)  ok=%s type=%s len=%s", tostring(okA), type(a),
     tostring(type(a) == "table" and #a or (type(a) == "string" and #a or "-")))

-- (b) dot syntax = read(count) with no self
local okB, b = pcall(function() s.Position = 0 return s.read(16) end)
DLOG("  (b) s.read(16)  ok=%s type=%s len=%s", tostring(okB), type(b),
     tostring(type(b) == "table" and #b or (type(b) == "string" and #b or "-")))
if type(b) == "table" then
    local head = {}
    for i = 1, math.min(8, #b) do head[#head + 1] = string.format("%02X", b[i]) end
    DLOG("      first bytes: %s   (source starts with '-- utils.lua' = 2D 2D 20)",
         table.concat(head, " "))
end

-- (c) does Position actually move?
s.Position = 0
local before = s.Position
local _ = s.read and s.read(4)
DLOG("  (c) Position before=%s after reading 4 bytes=%s", tostring(before), tostring(s.Position))

-- (d) readBytes / getData variants
for _, m in ipairs({ "readBytes", "getData", "saveToFile" }) do
    DLOG("  (d) tf.%s = %s / stream.%s = %s", m, type(rawget(tf, m) or tf[m]), m, type(s[m]))
end

-- (e) the one-liner the docs promise: TableFile.saveToFile(filename)
local p1 = OUT .. "via_tableFile_saveToFile.lua"
os.remove(p1)
local okE, errE = pcall(function() return tf.saveToFile(p1) end)
DLOG("  (e) tf.saveToFile -> ok=%s err=%s  size=%s (src %s)  %s",
     tostring(okE), tostring(errE or ""), tostring(fsize(p1)), tostring(fsize(DIST .. name)),
     (fsize(p1) == fsize(DIST .. name)) and "EXACT" or "MISMATCH")

-- (f) same for the 1 MB DLL, which is what makes chunked unpack necessary
local dllName = "Discrete-Mono.dll"
local tdll = findTableFile(dllName) or createTableFile(dllName, DIST .. dllName)
local p2 = OUT .. dllName
os.remove(p2)
local okF = pcall(function() return tdll.saveToFile(p2) end)
DLOG("  (f) 1 MB DLL via saveToFile -> ok=%s size=%s (src %s)  %s",
     tostring(okF), tostring(fsize(p2)), tostring(fsize(DIST .. dllName)),
     (fsize(p2) == fsize(DIST .. dllName)) and "EXACT" or "MISMATCH")

-- (g) and the stream.getData() -> MemoryStream.saveToFile route
local p3 = OUT .. "via_getData.lua"
os.remove(p3)
local okG, errG = pcall(function()
    local ms = tf.getData()
    return ms.saveToFile(p3)
end)
DLOG("  (g) tf.getData().saveToFile -> ok=%s err=%s size=%s",
     tostring(okG), tostring(errG or ""), tostring(fsize(p3)))
DLOG("=== job2 done ===")
