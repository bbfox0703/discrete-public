// BookmarkStoreTests.cs — round-trip, per-game separation, corruption tolerance,
// and field sanitization for the persistent Inspector bookmark store.

using System.Collections.Generic;
using System.IO;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class BookmarkStoreTests : System.IDisposable
{
    private readonly string _dir;
    private readonly BookmarkStore _store;

    public BookmarkStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "BookmarkTests_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new BookmarkStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static InstanceBookmark Bm(string label, string cls, string addr, string ns = "Game") => new()
    {
        Label = label, Assembly = "Assembly-CSharp", Namespace = ns,
        Class = cls, Address = addr, IsGameObject = false,
    };

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var input = new List<InstanceBookmark>
        {
            Bm("My player", "PlayerStats", "0x1000"),
            new() { Label = "", Assembly = "Assembly-CSharp", Namespace = "", Class = "GameManager",
                    Address = "0x2000", IsGameObject = true },
        };
        _store.Save("build-abc", input);
        var loaded = _store.Load("build-abc");

        Assert.Equal(2, loaded.Count);
        Assert.Equal("My player",    loaded[0].Label);
        Assert.Equal("PlayerStats",  loaded[0].Class);
        Assert.Equal("0x1000",       loaded[0].Address);
        Assert.Equal("Game",         loaded[0].Namespace);
        Assert.False(loaded[0].IsGameObject);
        Assert.Equal("GameManager",  loaded[1].Class);
        Assert.True(loaded[1].IsGameObject);
        Assert.Equal("GameManager",  loaded[1].DisplayLabel);   // blank label -> class
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
        => Assert.Empty(_store.Load("never-saved"));

    [Fact]
    public void PerGameKey_IsolatesBookmarks()
    {
        _store.Save("gameA", new[] { Bm("a", "AClass", "0x1") });
        _store.Save("gameB", new[] { Bm("b", "BClass", "0x2") });

        Assert.Equal("AClass", Assert.Single(_store.Load("gameA")).Class);
        Assert.Equal("BClass", Assert.Single(_store.Load("gameB")).Class);
        Assert.NotEqual(_store.PathFor("gameA"), _store.PathFor("gameB"));
    }

    [Fact]
    public void Parse_SkipsCorruptAndUnreinspectableLines()
    {
        // header, valid, too-few-fields, blank-class, blank-assembly, valid.
        string[] lines =
        {
            "# header",
            "player|Assembly-CSharp|Game|PlayerStats|0x1000|0",
            "broken|only|three",
            "noclass|Assembly-CSharp|Game||0x2000|0",
            "noasm||Game|SomeClass|0x3000|0",
            "boss|Assembly-CSharp||Boss|0x4000|1",
        };
        var parsed = BookmarkStore.Parse(lines);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("PlayerStats", parsed[0].Class);
        Assert.Equal("Boss", parsed[1].Class);
        Assert.True(parsed[1].IsGameObject);
    }

    [Fact]
    public void Format_StripsSeparatorFromFields()
    {
        var bm = Bm("has | pipe", "Weird|Class", "0x5");
        string[] lines = BookmarkStore.Format(new[] { bm });
        // The data line must still split into exactly 6 fields.
        string dataLine = lines[1];
        Assert.Equal(6, dataLine.Split('|').Length);
        Assert.DoesNotContain("has | pipe", dataLine);   // the label's pipe was removed
    }

    [Theory]
    [InlineData(null, "default")]
    [InlineData("", "default")]
    [InlineData("build-123_abc.def", "build-123_abc.def")]
    [InlineData("bad/key:name*?", "bad_key_name__")]
    public void Sanitize_MakesSafeFileFragment(string? key, string expected)
        => Assert.Equal(expected, BookmarkStore.Sanitize(key));

    // ── Audit 2026-08-13 batch F (F28): read-failure must not become data loss ──

    [Fact]
    public void Save_AfterUnreadableLoad_DoesNotWipeTheFile()
    {
        const string key = "game-a";
        _store.Save(key, new[] { Bm("keepme", "PlayerStats", "0x1000") });
        string path = _store.PathFor(key);
        string original = File.ReadAllText(path);

        // Simulate the transient failure: hold the file exclusively so
        // File.ReadAllLines throws, exactly as an AV scan or backup agent does.
        List<InstanceBookmark> loaded;
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            loaded = _store.Load(key);
        }
        Assert.Empty(loaded);   // display degrades gracefully...

        // ...but persisting that empty view must be refused, or the user's
        // bookmarks are gone for good.
        _store.Save(key, loaded);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Save_AfterSuccessfulLoad_StillAllowsClearingToEmpty()
    {
        // The guard must not block a legitimate "delete my last bookmark".
        const string key = "game-b";
        _store.Save(key, new[] { Bm("gone", "Temp", "0x1") });
        List<InstanceBookmark> loaded = _store.Load(key);
        Assert.Single(loaded);

        _store.Save(key, new List<InstanceBookmark>());
        Assert.Empty(_store.Load(key));
    }

    [Fact]
    public void Save_LeavesNoTempFileAndKeepsABackup()
    {
        const string key = "game-c";
        _store.Save(key, new[] { Bm("first", "One", "0x1") });
        _store.Save(key, new[] { Bm("second", "Two", "0x2") });

        string path = _store.PathFor(key);
        Assert.False(File.Exists(path + ".tmp"));     // write-then-replace cleaned up
        Assert.True(File.Exists(path + ".bak"));      // previous good copy retained
        Assert.Equal("Two", _store.Load(key)[0].Class);
    }
}
