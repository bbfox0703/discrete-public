// CeXmlExporterLiveTests.cs — regression coverage for the walkInstance-driven
// live CE XML export (2026-05-30 rewrite).
//
// The old live path resolved child classes by name (getClassInfo) + decoded
// field bytes with UnityTypeMap, so it couldn't drill nested classes, decode
// enums, decode strings, or drill List<T> elements. The rewrite drives the
// export off a fake ILiveExportSource here — the same walkInstance /
// walkInstanceFromHeader contract the real backend implements — and asserts:
//   * enum field → signed int leaf at the field address (NOT a pointer-deref
//     group with a bogus enum-value "address"),
//   * String field → CE <VariableType>String</VariableType> entry at the
//     inline UTF-16 buffer (strObj + 2*ptr + 4), not an 8-byte pointer group,
//   * pointer-to-class field → group rooted at the deref'd object whose
//     members come from walkInstanceFromHeader,
//   * List<String> → group with per-slot String entries.

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;
using Xunit;

namespace UnityDumpUI.Tests;

public class CeXmlExporterLiveTests
{
    // ---- Fake live source ------------------------------------------------

    private sealed class FakeLive : ILiveExportSource
    {
        // All maps are keyed by the NORMALISED address (0x + 16 upper-hex)
        // because the exporter queries with the X16-padded form while tests
        // set up keys in shorthand — Norm() bridges the two so a "0x20000000"
        // store matches a "0x0000000020000000" lookup.
        private readonly Dictionary<string, List<WalkedField>> _instances = new();
        private readonly Dictionary<string, WalkArrayResult>   _arrays    = new();
        private readonly Dictionary<string, string>            _memory    = new();

        public void AddInstance(string addr, List<WalkedField> fields) => _instances[Norm(addr)] = fields;
        public void AddArray(string addr, WalkArrayResult r)           => _arrays[Norm(addr)]    = r;
        public void AddMemory(string addr, string leHex)               => _memory[Norm(addr)]    = leHex;

        internal static string Norm(string a)
        {
            string t = a.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) ? a.Substring(2) : a;
            return ulong.TryParse(t, System.Globalization.NumberStyles.HexNumber,
                                  System.Globalization.CultureInfo.InvariantCulture, out ulong v)
                ? "0x" + v.ToString("X16", System.Globalization.CultureInfo.InvariantCulture)
                : a;
        }

        public Task<GetClassInfoResult?> GetClassInfoAsync(
            string assembly, string @namespace, string name, CancellationToken ct = default)
            => Task.FromResult<GetClassInfoResult?>(null);

        public Task<ReadMemoryResult> ReadMemoryAsync(string address, int size, CancellationToken ct = default)
            => Task.FromResult(_memory.TryGetValue(Norm(address), out string? hex)
                ? new ReadMemoryResult { Address = address, Size = size, Hex = hex }
                : new ReadMemoryResult { Address = address, Size = size, Error = "unmapped" });

        public Task<WalkArrayResult> WalkArrayAsync(
            string address, string? elementType, int? limit, int? elementSize,
            string? elementAssembly, string? elementNamespace, string? elementName,
            CancellationToken ct = default)
            => Task.FromResult(_arrays.TryGetValue(Norm(address), out WalkArrayResult? r)
                ? r : new WalkArrayResult { Error = "no array" });

        public Task<WalkInstanceResult> WalkInstanceAsync(
            string address, string assembly, string? @namespace, string name, CancellationToken ct = default)
            => Task.FromResult(_instances.TryGetValue(Norm(address), out List<WalkedField>? f)
                ? new WalkInstanceResult { Fields = f } : new WalkInstanceResult());

        public Task<WalkInstanceResult> WalkInstanceFromHeaderAsync(string address, CancellationToken ct = default)
            => Task.FromResult(_instances.TryGetValue(Norm(address), out List<WalkedField>? f)
                ? new WalkInstanceResult { Fields = f } : new WalkInstanceResult());
    }

    private static JsonElement Json(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static GetClassInfoResult RootClass() => new()
    {
        Namespace = "MainGame",
        Name      = "CharacterData",
        Fields    = new List<FieldEntry>(),   // unused in live mode
    };

    [Fact]
    public async Task RenderLive_EnumString_NestedClass_AndStringList()
    {
        const string baseAddr = "0x000000000000B900";   // root instance
        const string charaInfoPtr = "0x00000000000660C0";
        const string strObj = "0x0000000000097C440";    // charaName_ String object
        const string listObj = "0x0000000000009C5280";  // hasSkill_ List object
        const string itemsArr = "0x000000000009E0000";   // List._items T[]
        const string s0 = "0x00000000000A0000";          // hasSkill_[0] String obj

        var live = new FakeLive();

        // Root instance fields (what walkInstance(base) returns).
        live.AddInstance(baseAddr, new List<WalkedField>
        {
            // String field — decoded text present, non-null ptr.
            new() { Name = "charaName_", Type = "System.String", Offset = 0x18,
                    Address = "0x000000000000B918", Ptr = strObj,
                    String = "Chara_00000", StringLength = 11 },
            // Enum field — server classifies IsEnum, value is the underlying int.
            new() { Name = "charaKind_", Type = "CharacterData.CharaKind", Offset = 0x20,
                    Address = "0x000000000000B920", Value = Json("101"), IsEnum = true },
            // Plain int.
            new() { Name = "level_", Type = "System.Int32", Offset = 0x3C,
                    Address = "0x000000000000B93C", Value = Json("1") },
            // Pointer-to-class — nested CharacterData.CharacterInfo.
            new() { Name = "charaInfo", Type = "CharacterData.CharacterInfo", Offset = 0x130,
                    Address = "0x000000000000BA30", Ptr = charaInfoPtr, IsValueType = false },
            // List<String> — non-null handle.
            new() { Name = "hasSkill_", Type = "System.Collections.Generic.List`1<System.String>",
                    Offset = 0x1C0, Address = "0x000000000000BAC0", Ptr = listObj },
        });

        // Nested CharacterInfo fields (walkInstanceFromHeader(charaInfoPtr)).
        live.AddInstance(charaInfoPtr, new List<WalkedField>
        {
            new() { Name = "EnableWP", Type = "System.Boolean", Offset = 0x10,
                    Address = "0x00000000000660D0", Value = Json("false") },
            new() { Name = "GemCount", Type = "System.Int32", Offset = 0x18,
                    Address = "0x00000000000660D8", Value = Json("7") },
        });

        // List<String>._items pointer at listObj + 0x10 → itemsArr (LE hex).
        live.AddMemory(HexAdd(listObj, 0x10), LeHex(0x0000000009E0000UL));

        // walkArray(itemsArr) → 1 string slot.
        live.AddArray(itemsArr, new WalkArrayResult
        {
            Length = 1, Count = 1, IsPointer = true,
            ElementClassName = "String",
            Elements = new List<WalkedArrayElement>
            {
                new() { Index = 0, Address = "0x000000000009E0020", Ptr = s0,
                        String = "PlayerAttack_0000", StringLength = 17 },
            },
        });

        var options = new ExportOptions(
            DrilldownDepth: 3, ArrayLimit: 16, CollapsePointerNodes: true,
            LiveData: true, PointerSize: 8);

        string xml = await CeXmlExporter.RenderAsync(
            baseAddr, RootClass(), ancestors: null, "Assembly-CSharp",
            options, live, TestContext.Current.CancellationToken);

        // ---- Relative addressing: the ONLY absolute address is the root.
        Assert.Contains($"<Address>{baseAddr}</Address>", xml);
        // No other field carries an absolute (0x…) address — everything below
        // the root is +offset. (Count the 0x-prefixed <Address> entries: 1.)
        Assert.Equal(1, CountOccurrences(xml, "<Address>0x"));

        // ---- Enum: signed int leaf at +offset (was the bogus 0x65 deref group).
        Assert.Contains("charaKind_ : CharacterData.CharaKind", xml);
        Assert.Contains("<Address>+20</Address>", xml);   // enum field offset 0x20

        // ---- String: 2-level deref group at +18 (charaName_ offset) → the
        //      _stringLength / _firstChar pair. Decoded text in the label.
        Assert.Contains("<VariableType>String</VariableType>", xml);
        Assert.Contains("charaName_", xml);
        Assert.Contains("Chara_00000", xml);
        Assert.Contains("<Address>+18</Address>", xml);   // charaName_ deref group
        Assert.Contains("_stringLength", xml);
        Assert.Contains("_firstChar", xml);
        Assert.Contains("<Address>+10</Address>", xml);   // _stringLength (2*ptr)
        Assert.Contains("<Address>+14</Address>", xml);   // _firstChar (2*ptr+4)

        // ---- Nested class: pointer deref group at +130; drilled members at
        //      their relative offsets (GemCount at +18 within CharacterInfo).
        Assert.Contains("charaInfo : CharacterData.CharacterInfo", xml);
        Assert.Contains("<Address>+130</Address>", xml);  // charaInfo deref group
        Assert.Contains("EnableWP", xml);
        Assert.Contains("GemCount", xml);

        // ---- List<String>: outer deref group at +1C0, inner _items group,
        //      per-slot String deref group. Decoded text present.
        Assert.Contains("hasSkill_", xml);
        Assert.Contains("PlayerAttack_0000", xml);
        Assert.Contains("<Address>+1C0</Address>", xml);  // hasSkill_ outer deref
        Assert.Contains("_items", xml);

        // ---- Pointer groups carry the Offsets=[0] dereference.
        Assert.Contains("<Offset>0</Offset>", xml);
    }

    [Fact]
    public async Task RenderLive_X86_StringBufferOffsetIsPlus0xC()
    {
        const string baseAddr = "0x10000000";
        const string strObj   = "0x20000000";
        var live = new FakeLive();
        live.AddInstance(baseAddr, new List<WalkedField>
        {
            new() { Name = "name_", Type = "System.String", Offset = 0x8,
                    Address = "0x10000008", Ptr = strObj, String = "abc", StringLength = 3 },
        });

        var options = new ExportOptions(
            DrilldownDepth: 1, ArrayLimit: 16, CollapsePointerNodes: true,
            LiveData: true, PointerSize: 4);          // 32-bit target

        string xml = await CeXmlExporter.RenderAsync(
            baseAddr, RootClass(), ancestors: null, "Assembly-CSharp",
            options, live, TestContext.Current.CancellationToken);

        // x86: header = 2*4, so within the String object _stringLength is at
        // +8 and _firstChar at +C. The field itself is a deref group at +8
        // (name_ offset). Relative addressing → only the root is absolute.
        Assert.Contains("<VariableType>String</VariableType>", xml);
        Assert.Contains("<Address>+8</Address>", xml);    // name_ deref group AND _stringLength (both +8)
        Assert.Contains("<Address>+C</Address>", xml);    // _firstChar (2*4 + 4)
        Assert.Equal(1, CountOccurrences(xml, "<Address>0x"));   // only the root is absolute
    }

    // Mirror of LiveInspectionViewModel.HexAdd / little-endian formatting so the
    // fake's memory map keys match what the exporter computes.
    private static string HexAdd(string hexAddr, long delta)
    {
        string t = hexAddr.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)
            ? hexAddr.Substring(2) : hexAddr;
        ulong v = ulong.Parse(t, System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture);
        return "0x" + (v + (ulong)delta).ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string LeHex(ulong v)
    {
        var sb = new System.Text.StringBuilder(16);
        for (int i = 0; i < 8; ++i) { sb.Append((v & 0xFF).ToString("X2")); v >>= 8; }
        return sb.ToString();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
