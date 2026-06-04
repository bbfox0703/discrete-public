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
        //      length/chars pair. Decoded text in the label.
        Assert.Contains("<VariableType>String</VariableType>", xml);
        Assert.Contains("charaName_", xml);
        Assert.Contains("Chara_00000", xml);
        Assert.Contains("<Address>+18</Address>", xml);   // charaName_ deref group
        Assert.Contains("length (int32)", xml);
        Assert.Contains("chars (UTF-16)", xml);
        Assert.Contains("<Address>+10</Address>", xml);   // length child (2*ptr)
        Assert.Contains("<Address>+14</Address>", xml);   // chars child (2*ptr+4)

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
    public async Task RenderLive_CompactDescriptions_EmitBareFieldName()
    {
        // VerboseDescription:false → every entry's <Description> is the bare
        // field name. The " : Type" suffix, the string " = \"value\"" decode,
        // and the collection "[N of T]" header text are all dropped — while the
        // addresses, offsets, deref structure and variable types are unchanged,
        // so the CE table still resolves.
        const string baseAddr = "0x000000000000B900";
        const string strObj   = "0x0000000000097C440";

        var live = new FakeLive();
        live.AddInstance(baseAddr, new List<WalkedField>
        {
            new() { Name = "charaName_", Type = "System.String", Offset = 0x18,
                    Address = "0x000000000000B918", Ptr = strObj,
                    String = "Chara_00000", StringLength = 11 },
            new() { Name = "charaKind_", Type = "CharacterData.CharaKind", Offset = 0x20,
                    Address = "0x000000000000B920", Value = Json("101"), IsEnum = true },
        });

        var options = new ExportOptions(
            DrilldownDepth: 1, ArrayLimit: 16, CollapsePointerNodes: true,
            LiveData: true, PointerSize: 8, VerboseDescription: false);

        string xml = await CeXmlExporter.RenderAsync(
            baseAddr, RootClass(), ancestors: null, "Assembly-CSharp",
            options, live, TestContext.Current.CancellationToken);

        // Bare field-name descriptions only — no type, no decoded value.
        Assert.Contains("<Description>\"charaName_\"</Description>", xml);
        Assert.Contains("<Description>\"charaKind_\"</Description>", xml);
        Assert.DoesNotContain("charaName_ : System.String", xml);
        Assert.DoesNotContain("charaKind_ : CharacterData.CharaKind", xml);
        Assert.DoesNotContain("Chara_00000", xml);

        // Structure intact: the String stays a 2-level deref group (typed child
        // rows present) and the enum stays an int leaf at its offset.
        Assert.Contains("<VariableType>String</VariableType>", xml);
        Assert.Contains("length (int32)", xml);
        Assert.Contains("<Address>+18</Address>", xml);   // charaName_ deref group
        Assert.Contains("<Address>+20</Address>", xml);   // charaKind_ enum leaf
    }

    [Fact]
    public async Task RenderLive_EngineInternalTypes_NotRecursed()
    {
        // Phase 31-6 — a deep Copy-CE-XML walk must NOT recurse into Unity DOTS
        // / native / pointer types (the recursion that exploded into the ECS
        // graph and CTD'd Core Keeper). They appear as watchable pointer leaves
        // but their children are never walked. Gameplay fields still recurse.
        const string baseAddr = "0x000000000000C000";
        const string sysObj   = "0x00000000000D0000";   // an ECS system object

        var live = new FakeLive();
        live.AddInstance(baseAddr, new List<WalkedField>
        {
            // Real gameplay field — must still be emitted.
            new() { Name = "_hp", Type = "System.Int32", Offset = 0x10,
                    Address = "0x000000000000C010", Value = Json("100") },
            // Unity DOTS system pointer — engine-internal, must NOT recurse.
            new() { Name = "ecbSystem",
                    Type = "Unity.Entities.BeginSimulationEntityCommandBufferSystem",
                    Offset = 0x18, Address = "0x000000000000C018", Ptr = sysObj },
            // Native pointer type (ends with '*') — must NOT recurse.
            new() { Name = "m_StatePtr", Type = "Unity.Entities.SystemState*",
                    Offset = 0x20, Address = "0x000000000000C020", Ptr = sysObj },
            // UnityEngine reference objects — must NOT recurse (scene graph /
            // invocation list explosion). Emitted as pointer leaves.
            new() { Name = "optionalTransformToFollow", Type = "UnityEngine.Transform",
                    Offset = 0x28, Address = "0x000000000000C028", Ptr = sysObj },
            new() { Name = "onDeath", Type = "UnityEngine.Events.UnityEvent",
                    Offset = 0x30, Address = "0x000000000000C030", Ptr = sysObj },
        });
        // The ECS object's fields — reachable only if the gate fails.
        live.AddInstance(sysObj, new List<WalkedField>
        {
            new() { Name = "SECRET_ECS_FIELD", Type = "System.Int32", Offset = 0x10,
                    Address = "0x00000000000D0010", Value = Json("999") },
        });

        var options = new ExportOptions(
            DrilldownDepth: 5, ArrayLimit: 16, CollapsePointerNodes: true,
            LiveData: true, PointerSize: 8);

        string xml = await CeXmlExporter.RenderAsync(
            baseAddr, RootClass(), ancestors: null, "Assembly-CSharp",
            options, live, TestContext.Current.CancellationToken);

        Assert.Contains("_hp", xml);                    // gameplay field kept
        Assert.Contains("ecbSystem", xml);              // pointer leaf still listed
        Assert.Contains("m_StatePtr", xml);
        Assert.Contains("optionalTransformToFollow", xml);  // UnityEngine ref → leaf
        Assert.Contains("onDeath", xml);                    // UnityEvent → leaf
        Assert.DoesNotContain("SECRET_ECS_FIELD", xml); // but NOT recursed into
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

        // x86: header = 2*4, so within the String object the length child is at
        // +8 and the chars child at +C. The field itself is a deref group at +8
        // (name_ offset). Relative addressing → only the root is absolute.
        Assert.Contains("<VariableType>String</VariableType>", xml);
        Assert.Contains("<Address>+8</Address>", xml);    // name_ deref group AND _stringLength (both +8)
        Assert.Contains("<Address>+C</Address>", xml);    // _firstChar (2*4 + 4)
        Assert.Equal(1, CountOccurrences(xml, "<Address>0x"));   // only the root is absolute
    }

    [Fact]
    public async Task RenderFieldNodes_StandaloneSubtree_StringExpandsNotFlatLeaf()
    {
        // "Copy CE field" on a String field/slot must emit a STANDALONE subtree
        // rooted at the field's absolute address with the length/chars deref
        // children — NOT a single flat pointer leaf (the old WrapLeavesXml bug
        // where [0] came out as "<VariableType>Pointer</VariableType>").
        const string fieldAddr = "0x0000000027BDD1C0";   // the String slot's storage
        var live = new FakeLive();
        var field = new WalkedField
        {
            Name = "[0]", Type = "System.String", Offset = 0x10,
            Address = fieldAddr, Ptr = "0x000000000A0B0C00",
            String = "karma01", StringLength = 7,
        };

        var options = new ExportOptions(
            DrilldownDepth: 2, ArrayLimit: 16, CollapsePointerNodes: true,
            LiveData: true, PointerSize: 8);

        string xml = await CeXmlExporter.RenderFieldNodesLiveAsync(
            new[] { (field, fieldAddr) }, "Assembly-CSharp", options, live,
            TestContext.Current.CancellationToken);

        // Root entry carries the field's ABSOLUTE address (not +offset) and
        // is a deref GROUP, not a flat pointer leaf.
        Assert.Contains($"<Address>{fieldAddr}</Address>", xml);
        Assert.Contains("<GroupHeader>1</GroupHeader>", xml);
        Assert.Contains("<Offset>0</Offset>", xml);             // deref the String object
        Assert.Contains("length (int32)", xml);
        Assert.Contains("chars (UTF-16)", xml);
        Assert.Contains("karma01", xml);                        // decoded text in label
        Assert.DoesNotContain("<VariableType>Pointer</VariableType>", xml);  // the old bug
    }

    [Fact]
    public async Task RenderFieldNodes_MultiSelect_OneRootPerNode()
    {
        var live = new FakeLive();
        var a = new WalkedField { Name = "_value", Type = "System.Int32", Offset = 0x28,
                                  Address = "0x1000", Value = Json("58") };
        var b = new WalkedField { Name = "_max", Type = "System.Int32", Offset = 0x30,
                                  Address = "0x1008", Value = Json("100") };

        var options = new ExportOptions(
            DrilldownDepth: 1, ArrayLimit: 16, CollapsePointerNodes: true,
            LiveData: true, PointerSize: 8);

        string xml = await CeXmlExporter.RenderFieldNodesLiveAsync(
            new[] { (a, "0x1000"), (b, "0x1008") }, "Assembly-CSharp", options, live,
            TestContext.Current.CancellationToken);

        // Two standalone scalar leaves, each at its own absolute address.
        Assert.Contains("_value : System.Int32", xml);
        Assert.Contains("_max : System.Int32", xml);
        Assert.Contains("<Address>0x1000</Address>", xml);
        Assert.Contains("<Address>0x1008</Address>", xml);
        Assert.Equal(2, CountOccurrences(xml, "<CheatEntry>"));
    }

    [Fact]
    public async Task RenderFieldNodes_RootAsOffset_EmitsRelativeRoot()
    {
        // "Copy CE field (relative root)" — the top entry's Address is the field's
        // +offset (paste under the parent struct in CE), not the absolute address.
        var live = new FakeLive();
        var field = new WalkedField { Name = "_value", Type = "System.Int32", Offset = 0x28,
                                      Address = "0x1000", Value = Json("58") };
        var options = new ExportOptions(
            DrilldownDepth: 1, ArrayLimit: 16, CollapsePointerNodes: true,
            LiveData: true, PointerSize: 8);

        string xml = await CeXmlExporter.RenderFieldNodesLiveAsync(
            new[] { (field, "0x1000") }, "Assembly-CSharp", options, live,
            TestContext.Current.CancellationToken, progress: null, rootAsOffset: true);

        Assert.Contains("<Address>+28</Address>", xml);          // offset 0x28 → +28 (hex, no 0x)
        Assert.DoesNotContain("<Address>0x1000</Address>", xml); // not the absolute addr
    }

    [Fact]
    public async Task RenderLive_Dictionary_RelativeDerefChain_PerPair()
    {
        // Dictionary<string,int> fanned out by the DLL: parent carries dict=true
        // + chain offsets, each pair a child WalkedField named "[key]". The
        // exporter must emit: dict field deref → entries deref → per-pair value
        // leaf at +(data_offset + index*stride + value_offset).
        const string baseAddr = "0x000000000000B900";
        var live = new FakeLive();
        live.AddInstance(baseAddr, new List<WalkedField>
        {
            new()
            {
                Name = "game_flags",
                Type = "System.Collections.Generic.Dictionary<System.String,System.Int32>",
                Offset = 0xE8, Address = "0x000000000000B9E8",
                Dict = true, DictCount = 2,
                DictEntriesOffset = 0x10,   // `entries` field within dict object
                DictDataOffset = 0x20,      // SZARRAY data start
                DictStride = 0x18,          // Entry<string,int> size
                Fields = new List<WalkedField>
                {
                    // value member offset within the Entry = 0x10.
                    new() { Name = "[sc_00_01000]", Type = "System.Int32", Offset = 0x10,
                            Index = 0, Value = Json("1"), DictKey = "sc_00_01000" },
                    new() { Name = "[sc_01_05050]", Type = "System.Int32", Offset = 0x10,
                            Index = 1, Value = Json("254"), DictKey = "sc_01_05050" },
                },
            },
        });

        var options = new ExportOptions(
            DrilldownDepth: 3, ArrayLimit: 16, CollapsePointerNodes: true,
            LiveData: true, PointerSize: 8);

        string xml = await CeXmlExporter.RenderAsync(
            baseAddr, RootClass(), ancestors: null, "Assembly-CSharp",
            options, live, TestContext.Current.CancellationToken);

        // Relative throughout — only the root is absolute.
        Assert.Equal(1, CountOccurrences(xml, "<Address>0x"));
        // Two pointer derefs (dict object + entries array).
        Assert.Contains("<Offset>0</Offset>", xml);
        // Outer dict deref group at the field offset, entries group at +10.
        Assert.Contains("game_flags", xml);
        Assert.Contains("<Address>+E8</Address>", xml);   // dict field deref
        Assert.Contains("entries", xml);
        Assert.Contains("<Address>+10</Address>", xml);   // entries field deref
        // Per-pair value leaves: +(0x20 + index*0x18 + 0x10).
        Assert.Contains("[sc_00_01000]", xml);
        Assert.Contains("<Address>+30</Address>", xml);   // 0x20 + 0*0x18 + 0x10
        Assert.Contains("[sc_01_05050]", xml);
        Assert.Contains("<Address>+48</Address>", xml);   // 0x20 + 1*0x18 + 0x10
        // Int values render as a 4-byte leaf.
        Assert.Contains("<VariableType>4 Bytes</VariableType>", xml);
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

    // ---- DLL-reported pointer size overrides the caller's value -----------

    /// <summary>A fake whose walks report pointer_size=4 (x86/WOW64). The
    /// live export must adopt it over a caller-supplied PointerSize:8, so an
    /// IntPtr field emits "4 Bytes" and the managed-String length sits at the
    /// x86 header offset (+0x8) instead of x64 (+0x10). Regression for the
    /// 活俠傳 Copy CE XML bug where a stale GamePointerSize=0→8 produced x64
    /// offsets + "8 Bytes" on a 32-bit target.</summary>
    private sealed class FakeLiveX86 : ILiveExportSource
    {
        private readonly List<WalkedField> _rootFields;
        public FakeLiveX86(List<WalkedField> rootFields) { _rootFields = rootFields; }

        public Task<GetClassInfoResult?> GetClassInfoAsync(string a, string n, string m, CancellationToken ct = default)
            => Task.FromResult<GetClassInfoResult?>(null);
        public Task<ReadMemoryResult> ReadMemoryAsync(string address, int size, CancellationToken ct = default)
            => Task.FromResult(new ReadMemoryResult { Address = address, Size = size, Error = "unmapped" });
        public Task<WalkArrayResult> WalkArrayAsync(string a, string? et, int? l, int? es,
            string? ea, string? en, string? em, CancellationToken ct = default)
            => Task.FromResult(new WalkArrayResult { Error = "no array" });
        public Task<WalkInstanceResult> WalkInstanceAsync(string a, string asm, string? ns, string n, CancellationToken ct = default)
            => Task.FromResult(new WalkInstanceResult { Fields = _rootFields, PointerSize = 4 });
        public Task<WalkInstanceResult> WalkInstanceFromHeaderAsync(string a, CancellationToken ct = default)
            => Task.FromResult(new WalkInstanceResult { Fields = _rootFields, PointerSize = 4 });
    }

    [Fact]
    public async Task RenderLive_DllReportsX86_OverridesCallerPointerSize()
    {
        const string baseAddr = "0x0000000027313000";
        var live = new FakeLiveX86(new List<WalkedField>
        {
            new() { Name = "m_CachedPtr", Type = "System.IntPtr", Offset = 0x8,
                    Address = "0x0000000027313008", Ptr = "0x00000000093DBEA0" },
            new() { Name = "_name", Type = "System.String", Offset = 0x10,
                    Address = "0x0000000027313010", Ptr = "0x000000002731FD68",
                    String = "學問", StringLength = 2 },
        });

        // Caller passes the WRONG (x64) pointer size — the DLL's pointer_size=4
        // must win.
        var options = ExportOptions.Defaults with { LiveData = true, PointerSize = 8, DrilldownDepth = 2 };
        string xml = await CeXmlExporter.RenderAsync(
            baseAddr, RootClass(), ancestors: null, "Assembly-CSharp", options, live,
            TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        // IntPtr leaf is pointer-sized → "4 Bytes" on x86 (NOT "8 Bytes").
        Assert.Contains("<VariableType>4 Bytes</VariableType>", xml);
        Assert.DoesNotContain("<VariableType>8 Bytes</VariableType>", xml);
        // String length child sits at the x86 header offset +0x8 (2*ptr),
        // chars at +0xC — not the x64 +0x10/+0x14.
        Assert.Contains("<Address>+8</Address>", xml);
        Assert.Contains("<Address>+C</Address>", xml);
        Assert.DoesNotContain("<Address>+14</Address>", xml);
    }
}
