-- job3: which extraction route actually works on CE 7.7?
--   route 1  tf.saveToFile(path)                     <- one line, no read at all
--   route 2  stream.read(count) with DOT syntax + chunked string.char
--   route 3  tf.getData().saveToFile(path)
-- Everything is pcall'd; job2 died because rawget() on userdata raises.
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

local function tail(p, n)
    local f = io.open(p, "rb")
    if not f then return nil end
    local sz = f:seek("end")
    f:seek("set", math.max(0, sz - n))
    local b = f:read(n)
    f:close()
    return b
end

local function verdict(p, src)
    local a, b = fsize(src), fsize(p)
    if a == nil or b == nil then return "NO FILE" end
    if a ~= b then return string.format("SIZE %s vs %s", tostring(a), tostring(b)) end
    return (tail(p, 256) == tail(src, 256)) and "EXACT" or "TAIL DIFFERS"
end

DLOG("=== job3: extraction routes on CE 7.7 ===")

for _, name in ipairs({ "utils.lua", "Discrete-Mono.dll" }) do
    local src = DIST .. name
    local tf = findTableFile(name) or createTableFile(name, src)
    DLOG("  --- %s (%s bytes)", name, tostring(fsize(src)))

    local p1 = OUT .. "r1_" .. name
    os.remove(p1)
    local ok1, err1 = pcall(function() return tf.saveToFile(p1) end)
    DLOG("    route1 tf.saveToFile         ok=%s %s  err=%s",
         tostring(ok1), verdict(p1, src), tostring(err1 or ""))

    local p2 = OUT .. "r2_" .. name
    os.remove(p2)
    local ok2, err2 = pcall(function()
        local s = tf.Stream
        s.Position = 0
        local bytes = s.read(s.Size)              -- DOT syntax: no self
        if type(bytes) ~= "table" then error("read returned " .. type(bytes)) end
        local f = assert(io.open(p2, "wb"))
        local CHUNK, n = 4096, #bytes
        for i = 1, n, CHUNK do
            f:write(string.char(table.unpack(bytes, i, math.min(i + CHUNK - 1, n))))
        end
        f:close()
        return true
    end)
    DLOG("    route2 stream.read + chunked ok=%s %s  err=%s",
         tostring(ok2), verdict(p2, src), tostring(err2 or ""))

    local p3 = OUT .. "r3_" .. name
    os.remove(p3)
    local ok3, err3 = pcall(function()
        local ms = tf.getData()
        return ms.saveToFile(p3)
    end)
    DLOG("    route3 getData().saveToFile  ok=%s %s  err=%s",
         tostring(ok3), verdict(p3, src), tostring(err3 or ""))
end
DLOG("=== job3 done ===")
