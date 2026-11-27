// CeXmlAndCsxTemplateExporterTests.cs — regression coverage for E-H2:
// template-mode CE XML (CeXmlExporter.RenderAsync / RenderFieldsAsync) and
// CE Structure Dissect (CsxExporter.RenderAsync).
//
// Pins:
//  1. System.Enum fields emit as inline scalar leaves/elements using the enum's
//     underlying integer type (never Offsets=[0] / Vartype="Pointer" / value__).
//  2. Custom System.ValueType structs emit inline (NO pointer deref), stripping
//     Mono/IL2CPP boxed header offsets (2*ptr) when present.
//  3. Reference-type T[] and List<T> emit SZARRAY header (+0x20 x64 / +0x10 x86),
//     List._items deref (+0x10 x64 / +0x08 x86), pointer-strided slots, and
//     per-slot pointer dereference.
//  4. Primitive, enum, and ValueType struct arrays emit contiguous inline slots
//     starting at +0x20 / +0x10 with natural-aligned element stride (no deref).
//  5. Non-array collections (HashSet, IList, IEnumerable, Dictionary) never
//     fabricate array slots or deref the container as an element instance.
//  6. Root template header annotates x64 vs x86 layout and respects PointerSize.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class CeXmlAndCsxTemplateExporterTests
{
    private sealed class FakeLookup : IClassInfoLookup
    {
        private readonly Dictionary<(string ns, string name), GetClassInfoResult> _map = new();

        public void Add(string ns, string name, List<FieldEntry> fields, ClassRef? parent = null)
        {
            _map[(ns, name)] = new GetClassInfoResult
            {
                Namespace = ns,
                Name      = name,
                Parent    = parent,
                Fields    = fields,
            };
        }

        public Task<GetClassInfoResult?> GetClassInfoAsync(
            string assembly, string @namespace, string name, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_map.TryGetValue((@namespace, name), out var r) ? r : null);
        }
    }

    private static FieldEntry F(string name, string type, int offset, bool isStatic = false) => new()
    {
        Name   = name,
        Type   = type,
        Offset = offset,
        Static = isStatic,
    };

    private static ClassRef SystemEnum()      => new() { Namespace = "System", Name = "Enum" };
    private static ClassRef SystemValueType() => new() { Namespace = "System", Name = "ValueType" };
    private static ClassRef SystemObject()    => new() { Namespace = "System", Name = "Object" };

    private static FakeLookup BuildGameLookup(int ptr = 8)
    {
        int hdr = 2 * ptr;
        var lookup = new FakeLookup();

        // Standard Int32 enum.
        lookup.Add("Game", "WeaponType", new List<FieldEntry>
        {
            F("value__", "System.Int32", hdr),
            F("Sword",   "Game.WeaponType", 0, isStatic: true),
        }, parent: SystemEnum());

        // Byte-backed enum.
        lookup.Add("Game", "Rarity", new List<FieldEntry>
        {
            F("value__", "System.Byte", hdr),
        }, parent: SystemEnum());

        // Custom ValueType struct with Mono/IL2CPP boxed header-inclusive offsets
        // and alignment padding: { int level (+0), byte tier (+4) } -> stride 8.
        lookup.Add("Game", "StatBlock", new List<FieldEntry>
        {
            F("level", "System.Int32", hdr + 0x0),
            F("tier",  "System.Byte",  hdr + 0x4),
        }, parent: SystemValueType());

        // Reference class with fields well past the pointer size (0x40 total)
        // so any regression that uses ComputeStructSize as reference array stride
        // immediately diverges from +0x20/+0x28/+0x30 (x64) or +0x10/+0x14/+0x18 (x86).
        lookup.Add("Game", "Enemy", new List<FieldEntry>
        {
            F("hp",     "System.Int32",   hdr + 0x0),
            F("damage", "System.Single",  hdr + 0x20),
        }, parent: SystemObject());

        return lookup;
    }

    private static XElement FindEntry(XDocument doc, string descriptionContains)
        => doc.Descendants("CheatEntry")
              .First(e => ((string?)e.Element("Description") ?? string.Empty).Contains(descriptionContains));

    // ---- 1. Enum fields ---------------------------------------------------

    [Fact]
    public async Task CeXml_Template_EnumFields_EmitInlineScalarLeaves_NeverDeref()
    {
        var lookup = BuildGameLookup(ptr: 8);
        var root = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Hero",
            Fields    = new List<FieldEntry>
            {
                F("weapon", "Game.WeaponType", 0x10),
                F("rarity", "Game.Rarity",     0x14),
            },
        };

        var opts = ExportOptions.Defaults with
        {
            LiveData = false, PointerSize = 8, DrilldownDepth = 1, VerboseDescription = true,
        };
        string xml = await CeXmlExporter.RenderAsync(
            "INSTANCE_ADDRESS", root, ancestors: null, "Assembly-CSharp", opts, lookup,
            TestContext.Current.CancellationToken);

        var doc = XDocument.Parse(xml);

        // Root annotates x64 layout.
        XElement rootEntry = doc.Root!.Element("CheatEntries")!.Element("CheatEntry")!;
        Assert.Contains("(x64 layout)", (string)rootEntry.Element("Description")!);
        Assert.Equal("8 Bytes", (string?)rootEntry.Element("VariableType"));

        // Int32 enum: 4 Bytes signed inline leaf, no Offsets, no value__ child.
        XElement weapon = FindEntry(doc, "weapon : Game.WeaponType");
        Assert.Null(weapon.Element("GroupHeader"));
        Assert.Null(weapon.Element("Offsets"));
        Assert.Equal("+10", (string?)weapon.Element("Address"));
        Assert.Equal("4 Bytes", (string?)weapon.Element("VariableType"));
        Assert.Equal("1", (string?)weapon.Element("ShowAsSigned"));

        // Byte enum: Byte unsigned inline leaf, no Offsets.
        XElement rarity = FindEntry(doc, "rarity : Game.Rarity");
        Assert.Null(rarity.Element("GroupHeader"));
        Assert.Null(rarity.Element("Offsets"));
        Assert.Equal("+14", (string?)rarity.Element("Address"));
        Assert.Equal("Byte", (string?)rarity.Element("VariableType"));
        Assert.Equal("0", (string?)rarity.Element("ShowAsSigned"));

        Assert.DoesNotContain("value__", xml);
    }

    [Fact]
    public async Task CeXml_Template_EnumAtDepthZero_StillInlineScalarLeaf()
    {
        var lookup = BuildGameLookup(ptr: 8);
        var root = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Hero",
            Fields    = new List<FieldEntry> { F("weapon", "Game.WeaponType", 0x10) },
        };

        var opts = ExportOptions.Defaults with { LiveData = false, DrilldownDepth = 0 };
        string xml = await CeXmlExporter.RenderAsync(
            "INSTANCE_ADDRESS", root, ancestors: null, "Assembly-CSharp", opts, lookup,
            TestContext.Current.CancellationToken);

        var doc = XDocument.Parse(xml);
        XElement weapon = FindEntry(doc, "weapon : Game.WeaponType");
        Assert.Null(weapon.Element("Offsets"));
        Assert.Equal("4 Bytes", (string?)weapon.Element("VariableType"));
        Assert.Equal("1", (string?)weapon.Element("ShowAsSigned"));
    }

    [Fact]
    public async Task Csx_EnumFields_EmitFlatScalarElements_NotPointer()
    {
        var lookup = BuildGameLookup(ptr: 8);
        var root = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Hero",
            Fields    = new List<FieldEntry>
            {
                F("weapon", "Game.WeaponType", 0x10),
                F("rarity", "Game.Rarity",     0x14),
            },
        };

        var opts = ExportOptions.Defaults with { PointerSize = 8, DrilldownDepth = 1 };
        string csx = await CsxExporter.RenderAsync(
            root, ancestors: null, "Assembly-CSharp", opts, lookup,
            TestContext.Current.CancellationToken);

        var doc = XDocument.Parse(csx);
        var elems = doc.Descendants("Element").ToList();
        Assert.Equal(2, elems.Count);

        XElement weapon = elems[0];
        Assert.Equal("16", (string?)weapon.Attribute("Offset"));
        Assert.Equal("4 Bytes", (string?)weapon.Attribute("Vartype"));
        Assert.Equal("4", (string?)weapon.Attribute("Bytesize"));
        Assert.Equal("signed integer", (string?)weapon.Attribute("DisplayMethod"));
        Assert.Null(weapon.Element("Structure"));

        XElement rarity = elems[1];
        Assert.Equal("20", (string?)rarity.Attribute("Offset"));
        Assert.Equal("Byte", (string?)rarity.Attribute("Vartype"));
        Assert.Equal("1", (string?)rarity.Attribute("Bytesize"));
        Assert.Equal("unsigned integer", (string?)rarity.Attribute("DisplayMethod"));
        Assert.Null(rarity.Element("Structure"));

        // AutoCreateStructsize reflects true span: 0x14 + 1 = 21.
        XElement rootStruct = doc.Root!.Element("Structure")!;
        Assert.Equal("21", (string?)rootStruct.Attribute("AutoCreateStructsize"));
    }

    // ---- 2. Custom ValueType structs --------------------------------------

    [Fact]
    public async Task CeXml_Template_CustomValueStruct_InlineGroupWithBoxedHeaderStripped()
    {
        var lookup = BuildGameLookup(ptr: 8);
        var root = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Hero",
            Fields    = new List<FieldEntry>
            {
                F("stats", "Game.StatBlock", 0x20),
            },
        };

        var opts = ExportOptions.Defaults with { LiveData = false, PointerSize = 8, DrilldownDepth = 1 };
        string xml = await CeXmlExporter.RenderAsync(
            "INSTANCE_ADDRESS", root, ancestors: null, "Assembly-CSharp", opts, lookup,
            TestContext.Current.CancellationToken);

        var doc = XDocument.Parse(xml);
        XElement stats = FindEntry(doc, "stats : Game.StatBlock");
        Assert.Equal("1", (string?)stats.Element("GroupHeader"));
        Assert.Equal("+20", (string?)stats.Element("Address"));
        // Inline value struct MUST NOT carry <Offsets><Offset>0</Offset></Offsets>.
        Assert.Null(stats.Element("Offsets"));

        // Boxed 0x10 / 0x14 offsets normalized to inline +0 / +4.
        XElement level = FindEntry(doc, "level : System.Int32");
        XElement tier  = FindEntry(doc, "tier : System.Byte");
        Assert.Equal("+0", (string?)level.Element("Address"));
        Assert.Equal("+4", (string?)tier.Element("Address"));
    }

    [Fact]
    public async Task Csx_CustomValueStruct_FlattenedInlineAtParentOffset_NoPointerDeref()
    {
        var lookup = BuildGameLookup(ptr: 8);
        var root = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Hero",
            Fields    = new List<FieldEntry>
            {
                F("stats", "Game.StatBlock", 0x20),
            },
        };

        var opts = ExportOptions.Defaults with { PointerSize = 8, DrilldownDepth = 1 };
        string csx = await CsxExporter.RenderAsync(
            root, ancestors: null, "Assembly-CSharp", opts, lookup,
            TestContext.Current.CancellationToken);

        var doc = XDocument.Parse(csx);
        var elems = doc.Root!.Element("Structure")!.Element("Elements")!.Elements("Element").ToList();
        Assert.Equal(2, elems.Count);
        Assert.DoesNotContain("Vartype=\"Pointer\"", csx);

        Assert.Equal("32", (string?)elems[0].Attribute("Offset"));
        Assert.Equal("00000020", (string?)elems[0].Attribute("OffsetHex"));
        Assert.Equal("4 Bytes", (string?)elems[0].Attribute("Vartype"));
        Assert.Equal("stats.level : System.Int32", (string?)elems[0].Attribute("Description"));

        Assert.Equal("36", (string?)elems[1].Attribute("Offset"));
        Assert.Equal("00000024", (string?)elems[1].Attribute("OffsetHex"));
        Assert.Equal("Byte", (string?)elems[1].Attribute("Vartype"));
        Assert.Equal("stats.tier : System.Byte", (string?)elems[1].Attribute("Description"));
    }

    // ---- 3. Reference-type T[] and List<T> (x64 & x86) --------------------

    [Theory]
    [InlineData(8, "+20", "+28", "+30", "x64 layout", "8 Bytes")]
    [InlineData(4, "+10", "+14", "+18", "x86 layout", "4 Bytes")]
    public async Task CeXml_Template_ReferenceArray_UsesSzArrayHeaderAndPointerStrideWithPerSlotDeref(
        int ptr, string slot0, string slot1, string slot2, string expectedLayoutTag, string expectedRootVarType)
    {
        var lookup = BuildGameLookup(ptr);
        var root = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Battle",
            Fields    = new List<FieldEntry> { F("enemies", "Game.Enemy[]", 0x20) },
        };

        var opts = ExportOptions.Defaults with
        {
            LiveData = false, PointerSize = ptr, DrilldownDepth = 1, ArrayLimit = 3,
        };
        string xml = await CeXmlExporter.RenderAsync(
            "INSTANCE_ADDRESS", root, ancestors: null, "Assembly-CSharp", opts, lookup,
            TestContext.Current.CancellationToken);

        var doc = XDocument.Parse(xml);
        XElement rootEntry = doc.Root!.Element("CheatEntries")!.Element("CheatEntry")!;
        Assert.Contains(expectedLayoutTag, (string)rootEntry.Element("Description")!);
        Assert.Equal(expectedRootVarType, (string?)rootEntry.Element("VariableType"));

        XElement arrGroup = FindEntry(doc, "enemies : Game.Enemy[]");
        Assert.Equal("+20", (string?)arrGroup.Element("Address"));
        Assert.Equal("0", (string?)arrGroup.Element("Offsets")?.Element("Offset"));

        var slots = arrGroup.Element("CheatEntries")!.Elements("CheatEntry").ToList();
        Assert.Equal(3, slots.Count);
        Assert.Equal(new[] { slot0, slot1, slot2 }, slots.Select(s => (string?)s.Element("Address")).ToArray());

        // Every reference slot MUST dereference via Offsets=[0] and carry Enemy's fields.
        foreach (XElement slot in slots)
        {
            Assert.Equal("0", (string?)slot.Element("Offsets")?.Element("Offset"));
            Assert.Contains(slot.Descendants("CheatEntry"),
                            c => ((string?)c.Element("Description") ?? "").Contains("hp : System.Int32"));
        }
    }

    [Theory]
    [InlineData(8, "+10", "+20", "+28")]
    [InlineData(4, "+8",  "+10", "+14")]
    public async Task CeXml_Template_ReferenceList_DerefItemsThenSzArrayPointerSlots(
        int ptr, string expectedItemsOff, string slot0, string slot1)
    {
        var lookup = BuildGameLookup(ptr);
        var root = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Battle",
            Fields    = new List<FieldEntry>
            {
                F("wave", "System.Collections.Generic.List`1<Game.Enemy>", 0x28),
            },
        };

        var opts = ExportOptions.Defaults with
        {
            LiveData = false, PointerSize = ptr, DrilldownDepth = 1, ArrayLimit = 2,
        };
        string xml = await CeXmlExporter.RenderAsync(
            "INSTANCE_ADDRESS", root, ancestors: null, "Assembly-CSharp", opts, lookup,
            TestContext.Current.CancellationToken);

        var doc = XDocument.Parse(xml);
        XElement listGroup = FindEntry(doc, "wave : System.Collections.Generic.List`1<Game.Enemy>");
        Assert.Equal("+28", (string?)listGroup.Element("Address"));
        Assert.Equal("0", (string?)listGroup.Element("Offsets")?.Element("Offset"));

        XElement itemsGroup = listGroup.Element("CheatEntries")!.Elements("CheatEntry").Single();
        Assert.Contains("_items", (string?)itemsGroup.Element("Description") ?? "");
        Assert.Equal(expectedItemsOff, (string?)itemsGroup.Element("Address"));
        Assert.Equal("0", (string?)itemsGroup.Element("Offsets")?.Element("Offset"));

        var slots = itemsGroup.Element("CheatEntries")!.Elements("CheatEntry").ToList();
        Assert.Equal(2, slots.Count);
        Assert.Equal(new[] { slot0, slot1 }, slots.Select(s => (string?)s.Element("Address")).ToArray());
        Assert.All(slots, s => Assert.Equal("0", (string?)s.Element("Offsets")?.Element("Offset")));
    }

    [Theory]
    [InlineData(8, 16, 24, 32, 40)]
    [InlineData(4,  8, 12, 16, 20)]
    public async Task Csx_ReferenceList_EmitsItemsDerefSizeAndSzArrayPointerSlots(
        int ptr, int expectedItemsOff, int expectedSizeOff, int slot0Off, int slot1Off)
    {
        var lookup = BuildGameLookup(ptr);
        var root = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Battle",
            Fields    = new List<FieldEntry>
            {
                F("wave", "System.Collections.Generic.List<Game.Enemy>", 0x28),
            },
        };

        var opts = ExportOptions.Defaults with { PointerSize = ptr, DrilldownDepth = 1, ArrayLimit = 2 };
        string csx = await CsxExporter.RenderAsync(
            root, ancestors: null, "Assembly-CSharp", opts, lookup,
            TestContext.Current.CancellationToken);

        var doc = XDocument.Parse(csx);
        XElement waveElem = doc.Root!.Element("Structure")!.Element("Elements")!.Element("Element")!;
        Assert.Equal("Pointer", (string?)waveElem.Attribute("Vartype"));
        Assert.Equal(ptr.ToString(), (string?)waveElem.Attribute("Bytesize"));

        var listChildren = waveElem.Element("Structure")!.Element("Elements")!.Elements("Element").ToList();
        Assert.Equal(2, listChildren.Count);

        XElement itemsElem = listChildren[0];
        Assert.Equal(expectedItemsOff.ToString(), (string?)itemsElem.Attribute("Offset"));
        Assert.Equal("Pointer", (string?)itemsElem.Attribute("Vartype"));
        Assert.Equal(ptr.ToString(), (string?)itemsElem.Attribute("Bytesize"));

        XElement sizeElem = listChildren[1];
        Assert.Equal(expectedSizeOff.ToString(), (string?)sizeElem.Attribute("Offset"));
        Assert.Equal("4 Bytes", (string?)sizeElem.Attribute("Vartype"));

        var slots = itemsElem.Element("Structure")!.Element("Elements")!.Elements("Element").ToList();
        Assert.Equal(2, slots.Count);
        Assert.Equal(slot0Off.ToString(), (string?)slots[0].Attribute("Offset"));
        Assert.Equal(slot1Off.ToString(), (string?)slots[1].Attribute("Offset"));
        Assert.All(slots, s =>
        {
            Assert.Equal("Pointer", (string?)s.Attribute("Vartype"));
            Assert.Equal(ptr.ToString(), (string?)s.Attribute("Bytesize"));
            Assert.NotNull(s.Element("Structure"));
        });
    }

    // ---- 4. ValueType struct, enum, and primitive arrays ------------------

    [Fact]
    public async Task CeXml_And_Csx_ValueStructAndEnumArrays_InlineContiguousSlotsNoSlotDeref()
    {
        var lookup = BuildGameLookup(ptr: 8);
        var root = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Inventory",
            Fields    = new List<FieldEntry>
            {
                F("stats",   "Game.StatBlock[]",  0x10),
                F("weapons", "Game.WeaponType[]", 0x18),
                F("scores",  "System.Int32[]",    0x20),
            },
        };

        var opts = ExportOptions.Defaults with
        {
            LiveData = false, PointerSize = 8, DrilldownDepth = 1, ArrayLimit = 2,
        };

        // CE XML template
        string xml = await CeXmlExporter.RenderAsync(
            "INSTANCE_ADDRESS", root, ancestors: null, "Assembly-CSharp", opts, lookup,
            TestContext.Current.CancellationToken);
        var xmlDoc = XDocument.Parse(xml);

        // StatBlock[] { int level (+0), byte tier (+4) } -> aligned stride 8 starting at +0x20.
        XElement statsArr = FindEntry(xmlDoc, "stats : Game.StatBlock[]");
        var statSlots = statsArr.Element("CheatEntries")!.Elements("CheatEntry").ToList();
        Assert.Equal(2, statSlots.Count);
        Assert.Equal("+20", (string?)statSlots[0].Element("Address"));
        Assert.Equal("+28", (string?)statSlots[1].Element("Address"));
        Assert.All(statSlots, s => Assert.Null(s.Element("Offsets"))); // inline, NO deref!

        // WeaponType[] (Int32 enum) -> 4-byte signed leaves at +0x20, +0x24.
        XElement weapArr = FindEntry(xmlDoc, "weapons : Game.WeaponType[]");
        var weapSlots = weapArr.Element("CheatEntries")!.Elements("CheatEntry").ToList();
        Assert.Equal(2, weapSlots.Count);
        Assert.Equal("+20", (string?)weapSlots[0].Element("Address"));
        Assert.Equal("+24", (string?)weapSlots[1].Element("Address"));
        Assert.All(weapSlots, s =>
        {
            Assert.Null(s.Element("Offsets"));
            Assert.Equal("4 Bytes", (string?)s.Element("VariableType"));
            Assert.Equal("1", (string?)s.Element("ShowAsSigned"));
        });

        // System.Int32[] -> 4-byte signed leaves at +0x20, +0x24.
        XElement scoresArr = FindEntry(xmlDoc, "scores : System.Int32[]");
        var scoreSlots = scoresArr.Element("CheatEntries")!.Elements("CheatEntry").ToList();
        Assert.Equal(2, scoreSlots.Count);
        Assert.Equal("+20", (string?)scoreSlots[0].Element("Address"));
        Assert.Equal("+24", (string?)scoreSlots[1].Element("Address"));

        // CSX
        string csx = await CsxExporter.RenderAsync(
            root, ancestors: null, "Assembly-CSharp", opts, lookup,
            TestContext.Current.CancellationToken);
        var csxDoc = XDocument.Parse(csx);
        var rootElems = csxDoc.Root!.Element("Structure")!.Element("Elements")!.Elements("Element").ToList();

        // StatBlock[] in CSX: flattened [0].level @ 32, [0].tier @ 36, [1].level @ 40, [1].tier @ 44.
        var csxStatElems = rootElems[0].Element("Structure")!.Element("Elements")!.Elements("Element").ToList();
        Assert.Equal(4, csxStatElems.Count);
        Assert.Equal(new[] { "32", "36", "40", "44" },
                     csxStatElems.Select(e => (string?)e.Attribute("Offset")).ToArray());
        Assert.All(csxStatElems, e => Assert.NotEqual("Pointer", (string?)e.Attribute("Vartype")));

        // WeaponType[] in CSX: 4 Bytes at 32, 36.
        var csxWeapElems = rootElems[1].Element("Structure")!.Element("Elements")!.Elements("Element").ToList();
        Assert.Equal(new[] { "32", "36" },
                     csxWeapElems.Select(e => (string?)e.Attribute("Offset")).ToArray());
        Assert.All(csxWeapElems, e => Assert.Equal("4 Bytes", (string?)e.Attribute("Vartype")));
    }

    // ---- 5. Non-array collections (HashSet, IList, IEnumerable, Dictionary)

    [Fact]
    public async Task CeXml_And_Csx_NonArrayCollections_EmitOpaquePointer_NoBogusArrayOrElementDrill()
    {
        var lookup = BuildGameLookup(ptr: 8);
        var root = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Roster",
            Fields    = new List<FieldEntry>
            {
                F("seen",    "System.Collections.Generic.HashSet`1<Game.Enemy>",          0x10),
                F("view",    "System.Collections.Generic.IEnumerable`1<Game.Enemy>",      0x18),
                F("iface",   "System.Collections.Generic.IList`1<Game.Enemy>",            0x20),
                F("byId",    "System.Collections.Generic.Dictionary`2<System.Int32, Game.Enemy>", 0x28),
            },
        };

        var opts = ExportOptions.Defaults with
        {
            LiveData = false, PointerSize = 8, DrilldownDepth = 2, ArrayLimit = 4,
        };

        string xml = await CeXmlExporter.RenderAsync(
            "INSTANCE_ADDRESS", root, ancestors: null, "Assembly-CSharp", opts, lookup,
            TestContext.Current.CancellationToken);
        var xmlDoc = XDocument.Parse(xml);

        // Must NOT expand [0] slots or drill into Enemy's hp field.
        Assert.DoesNotContain("[0]", xml);
        Assert.DoesNotContain("hp : System.Int32", xml);
        foreach (string fieldName in new[] { "seen", "view", "iface", "byId" })
        {
            XElement entry = FindEntry(xmlDoc, fieldName);
            Assert.Null(entry.Element("GroupHeader"));
            Assert.Null(entry.Element("Offsets"));
            Assert.Equal("8 Bytes", (string?)entry.Element("VariableType"));
        }

        string csx = await CsxExporter.RenderAsync(
            root, ancestors: null, "Assembly-CSharp", opts, lookup,
            TestContext.Current.CancellationToken);
        var csxDoc = XDocument.Parse(csx);
        var elems = csxDoc.Root!.Element("Structure")!.Element("Elements")!.Elements("Element").ToList();
        Assert.Equal(4, elems.Count);
        Assert.All(elems, e =>
        {
            Assert.Equal("Pointer", (string?)e.Attribute("Vartype"));
            Assert.Null(e.Element("Structure"));
        });
    }

    // ---- 6. E-L6: XML 1.0 control characters & unpaired surrogates

    [Fact]
    public async Task CeXml_And_Csx_SanitizeInvalidXml10ControlCharsAndUnpairedSurrogates()
    {
        // E-L6: obfuscated IL2CPP identifiers or live string previews may contain
        // XML-1.0-illegal chars (< 0x20 except TAB/LF/CR, lone surrogates, 0xFFFE/0xFFFF).
        // Both exporters must sanitize them so CE's XML parser (and XDocument.Parse)
        // accepts the output while preserving valid CJK, tabs, and emoji surrogate pairs.
        string badName = "Obf\u0001\u0000\u001F_\uD800_\uDC00_\uFFFE_\uFFFF_合法\t😀";
        var root = new GetClassInfoResult
        {
            Namespace = "Game\u0002Ns",
            Name      = badName,
            Fields    = new List<FieldEntry>
            {
                F("fld\u0007_\uD83D", "System.Int32", 0x10),
            },
        };

        var opts = ExportOptions.Defaults with { LiveData = false, PointerSize = 8 };

        string xml = await CeXmlExporter.RenderAsync(
            "INSTANCE_ADDRESS", root, ancestors: null, "Assembly-CSharp", opts, lookup: null,
            TestContext.Current.CancellationToken);
        var xmlDoc = XDocument.Parse(xml); // throws XmlException if any illegal char leaked
        Assert.Contains("\\u0001", xml);
        Assert.Contains("\\u0000", xml);
        Assert.Contains("\\uD800", xml);
        Assert.Contains("\\uDC00", xml);
        Assert.Contains("\\uFFFE", xml);
        Assert.Contains("\\uFFFF", xml);
        Assert.Contains("合法\t😀", xml);
        Assert.NotNull(xmlDoc.Root);

        string csx = await CsxExporter.RenderAsync(
            root, ancestors: null, "Assembly-CSharp", opts, lookup: null,
            TestContext.Current.CancellationToken);
        var csxDoc = XDocument.Parse(csx);
        Assert.Contains("\\u0001", csx);
        Assert.Contains("\\uD800", csx);
        Assert.Contains("合法\t😀", csx);
        Assert.NotNull(csxDoc.Root);
    }
}
