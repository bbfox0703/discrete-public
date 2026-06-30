// CeXmlExporterFlattenTests.cs — coverage for the UE5CEDumper-style collapse /
// flatten / record-colour / desc-offset export knobs ported into the live CE
// XML exporter (CeXmlExporter.Live). Each knob defaults OFF; the existing
// CeXmlExporterLiveTests assert the default shape, so these only exercise the
// ON behaviour and the gates that decide qualify-vs-not.
//
// Drives the same fake ILiveExportSource the live tests use: walkInstance /
// walkInstanceFromHeader return the field tree, and the exporter reshapes it.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;
using Xunit;

namespace UnityDumpUI.Tests;

public class CeXmlExporterFlattenTests
{
    // ---- Fake live source (mirrors CeXmlExporterLiveTests.FakeLive) -------

    private sealed class FakeLive : ILiveExportSource
    {
        private readonly Dictionary<string, List<WalkedField>> _instances = new();
        private readonly Dictionary<string, WalkArrayResult>   _arrays    = new();
        private readonly Dictionary<string, string>            _memory    = new();

        public void AddInstance(string addr, List<WalkedField> fields) => _instances[Norm(addr)] = fields;
        public void AddArray(string addr, WalkArrayResult r)           => _arrays[Norm(addr)]    = r;
        public void AddMemory(string addr, string leHex)               => _memory[Norm(addr)]    = leHex;

        private static string Norm(string a)
        {
            string t = a.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? a.Substring(2) : a;
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
        Name      = "Save",
        Fields    = new List<FieldEntry>(),
    };

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static Task<string> RenderAsync(FakeLive live, List<WalkedField> rootFields, ExportOptions options)
    {
        const string baseAddr = "0x000000000000B900";
        live.AddInstance(baseAddr, rootFields);
        return CeXmlExporter.RenderAsync(
            baseAddr, RootClass(), ancestors: null, "Assembly-CSharp",
            options, live, TestContext.Current.CancellationToken);
    }

    private static ExportOptions LiveOpts => ExportOptions.Defaults with
    {
        LiveData = true, PointerSize = 8, DrilldownDepth = 4, ArrayLimit = 16,
    };

    // ---- FlattenValueStructs --------------------------------------------

    [Fact]
    public async Task FlattenValueStructs_PromotesScalarMembersToSiblings()
    {
        const string vecAddr = "0x0000000000050000";
        var live = new FakeLive();
        // pos : MainGame.Vec3 (inline value struct) at +20, members x/y/z floats.
        live.AddInstance(vecAddr, new List<WalkedField>
        {
            new() { Name = "x", Type = "System.Single", Offset = 0x0, Value = Json("1.0") },
            new() { Name = "y", Type = "System.Single", Offset = 0x4, Value = Json("2.0") },
            new() { Name = "z", Type = "System.Single", Offset = 0x8, Value = Json("3.0") },
        });
        var root = new List<WalkedField>
        {
            new() { Name = "pos", Type = "MainGame.Vec3", Offset = 0x20, Address = vecAddr, IsValueType = true },
        };

        string flat = await RenderAsync(live, root, LiveOpts with { FlattenValueStructs = true });

        // Three sibling leaves at combined offsets 20 / 24 / 28; no nested group.
        Assert.Contains("pos ▸ x", flat);
        Assert.Contains("pos ▸ z", flat);
        Assert.Contains("<Address>+20</Address>", flat);
        Assert.Contains("<Address>+24</Address>", flat);
        Assert.Contains("<Address>+28</Address>", flat);
        Assert.Equal(3, Count(flat, "<VariableType>Float</VariableType>"));
        // No nested struct group: a flattened struct emits leaves only (the only
        // GroupHeader is the root instance).
        Assert.Equal(1, Count(flat, "<GroupHeader>1</GroupHeader>"));
    }

    [Fact]
    public async Task FlattenValueStructs_KeepsNestedWhenMemberIsItselfAStruct()
    {
        const string xfAddr  = "0x0000000000060000";
        const string vecAddr = "0x0000000000061000";
        var live = new FakeLive();
        // Transform2D { Vec3 pos; float angle } — pos is a struct → parent must
        // NOT flatten; pos itself flattens at its own level.
        live.AddInstance(xfAddr, new List<WalkedField>
        {
            new() { Name = "pos",   Type = "MainGame.Vec3",  Offset = 0x0, Address = vecAddr, IsValueType = true },
            new() { Name = "angle", Type = "System.Single",  Offset = 0xC, Value = Json("90.0") },
        });
        live.AddInstance(vecAddr, new List<WalkedField>
        {
            new() { Name = "x", Type = "System.Single", Offset = 0x0, Value = Json("1.0") },
            new() { Name = "y", Type = "System.Single", Offset = 0x4, Value = Json("2.0") },
            new() { Name = "z", Type = "System.Single", Offset = 0x8, Value = Json("3.0") },
        });
        var root = new List<WalkedField>
        {
            new() { Name = "xform", Type = "MainGame.Transform2D", Offset = 0x10, Address = xfAddr, IsValueType = true },
        };

        string flat = await RenderAsync(live, root, LiveOpts with { FlattenValueStructs = true });

        // xform stays a nested group (root + xform = 2 GroupHeaders); inside it,
        // the inner pos flattens to siblings (one level only).
        Assert.Equal(2, Count(flat, "<GroupHeader>1</GroupHeader>"));
        Assert.Contains("pos ▸ x", flat);
        Assert.Contains("angle", flat);
    }

    [Fact]
    public async Task FlattenValueStructs_DoesNotFlattenStringMember_ButRecordsDoes()
    {
        const string recAddr = "0x0000000000070000";
        const string strObj  = "0x0000000000071000";
        List<WalkedField> Members() => new()
        {
            new() { Name = "score", Type = "System.Int32",  Offset = 0x0, Value = Json("42") },
            new() { Name = "label", Type = "System.String", Offset = 0x8, Address = "0x0000000000070008",
                    Ptr = strObj, String = "hero", StringLength = 4 },
        };

        // FlattenValueStructs alone: the string member disqualifies → nested group.
        var live1 = new FakeLive();
        live1.AddInstance(recAddr, Members());
        string structs = await RenderAsync(live1, new List<WalkedField>
        {
            new() { Name = "rec", Type = "MainGame.Record", Offset = 0x18, Address = recAddr, IsValueType = true },
        }, LiveOpts with { FlattenValueStructs = true });
        // Not flattened: no "rec ▸ …" sibling labels; score / label stay nested
        // under the rec group (the string member emits its own deref group, so
        // the exact GroupHeader count includes it — assert the structural fact
        // instead: nothing was promoted to a sibling).
        Assert.DoesNotContain("rec ▸ score", structs);
        Assert.DoesNotContain("rec ▸ label", structs);
        Assert.Contains("score", structs);
        Assert.Contains("label", structs);

        // FlattenLeafRecords: the whole record flattens; the string member emits
        // the canonical String deref leaf at the combined offset (18+8 = 20).
        var live2 = new FakeLive();
        live2.AddInstance(recAddr, Members());
        string records = await RenderAsync(live2, new List<WalkedField>
        {
            new() { Name = "rec", Type = "MainGame.Record", Offset = 0x18, Address = recAddr, IsValueType = true },
        }, LiveOpts with { FlattenLeafRecords = true });
        Assert.Contains("rec ▸ score", records);
        Assert.Contains("rec ▸ label", records);
        Assert.Contains("<VariableType>String</VariableType>", records);
        Assert.Contains("hero", records);
        // score leaf at 18+0 = 18; the string deref GROUP at 18+8 = 20.
        Assert.Contains("<Address>+18</Address>", records);
        Assert.Contains("<Address>+20</Address>", records);
    }

    [Fact]
    public async Task FlattenValueStructs_StandaloneCopy_UsesAbsoluteMemberAddresses()
    {
        // "Copy CE field" on a value-struct node roots the entry at the field's
        // own ABSOLUTE storage address, so flattened members must be absolute
        // (struct base + member offset), not "+combined" relative offsets.
        const string structAbs = "0x0000000000050000";
        var live = new FakeLive();
        live.AddInstance(structAbs, new List<WalkedField>
        {
            new() { Name = "x", Type = "System.Single", Offset = 0x0, Value = Json("1.0") },
            new() { Name = "y", Type = "System.Single", Offset = 0x4, Value = Json("2.0") },
            new() { Name = "z", Type = "System.Single", Offset = 0x8, Value = Json("3.0") },
        });
        var node = (new WalkedField
        {
            Name = "pos", Type = "MainGame.Vec3", Offset = 0x20, Address = structAbs, IsValueType = true,
        }, structAbs);

        string xml = await CeXmlExporter.RenderFieldNodesLiveAsync(
            new[] { node }, "Assembly-CSharp", LiveOpts with { FlattenValueStructs = true },
            live, TestContext.Current.CancellationToken, rootAsOffset: false);

        Assert.Contains("pos ▸ x", xml);
        Assert.Contains("<Address>0x0000000000050000</Address>", xml);   // x @ base + 0
        Assert.Contains("<Address>0x0000000000050004</Address>", xml);   // y @ base + 4
        Assert.Contains("<Address>0x0000000000050008</Address>", xml);   // z @ base + 8
    }

    // ---- CollapseLeafPointers -------------------------------------------

    [Fact]
    public async Task CollapseLeafPointers_FoldsPointerToSingleScalar()
    {
        const string boxObj = "0x0000000000080000";
        var live = new FakeLive();
        live.AddInstance(boxObj, new List<WalkedField>
        {
            new() { Name = "value", Type = "System.Int32", Offset = 0x10, Value = Json("99") },
        });
        var root = new List<WalkedField>
        {
            new() { Name = "hpPtr", Type = "MainGame.IntBox", Offset = 0x30, Ptr = boxObj },
        };

        string xml = await RenderAsync(live, root, LiveOpts with { CollapseLeafPointers = true });

        // One collapsed leaf: <Address>+30</Address> <Offsets>[10]</Offsets>,
        // no group wrapping a single child (root is the only GroupHeader).
        Assert.Contains("hpPtr ▸ value", xml);
        Assert.Contains("<Address>+30</Address>", xml);
        Assert.Contains("<Offset>10</Offset>", xml);
        Assert.Equal(1, Count(xml, "<GroupHeader>1</GroupHeader>"));
    }

    [Fact]
    public async Task CollapseLeafPointers_OffKeepsNestedGroup()
    {
        const string boxObj = "0x0000000000080000";
        var live = new FakeLive();
        live.AddInstance(boxObj, new List<WalkedField>
        {
            new() { Name = "value", Type = "System.Int32", Offset = 0x10, Value = Json("99") },
        });
        var root = new List<WalkedField>
        {
            new() { Name = "hpPtr", Type = "MainGame.IntBox", Offset = 0x30, Ptr = boxObj },
        };

        string xml = await RenderAsync(live, root, LiveOpts);   // collapse OFF

        // Default: pointer group (root + hpPtr = 2 GroupHeaders), Offsets=[0] deref.
        Assert.Equal(2, Count(xml, "<GroupHeader>1</GroupHeader>"));
        Assert.Contains("<Offset>0</Offset>", xml);
    }

    // ---- CollapseChain ---------------------------------------------------

    [Fact]
    public async Task CollapseChain_FoldsSingleChildPointerSpine()
    {
        const string ptrA = "0x0000000000090000";
        const string ptrB = "0x0000000000091000";
        const string ptrC = "0x0000000000092000";
        var live = new FakeLive();
        live.AddInstance(ptrA, new List<WalkedField>
        {
            new() { Name = "b", Type = "MainGame.B", Offset = 0x18, Ptr = ptrB },
        });
        live.AddInstance(ptrB, new List<WalkedField>
        {
            new() { Name = "c", Type = "MainGame.C", Offset = 0x20, Ptr = ptrC },
        });
        live.AddInstance(ptrC, new List<WalkedField>
        {
            new() { Name = "val",   Type = "System.Int32", Offset = 0x10, Value = Json("7") },
            new() { Name = "other", Type = "System.Int32", Offset = 0x14, Value = Json("8") },
        });
        var root = new List<WalkedField>
        {
            new() { Name = "a", Type = "MainGame.A", Offset = 0x40, Ptr = ptrA },
        };

        string xml = await RenderAsync(live, root, LiveOpts with { CollapseChain = true });

        // One folded entry "a ▸ b ▸ c" with Offsets = [0, 20, 18] (final deref
        // first, then the intermediate field offsets in reverse depth order).
        Assert.Contains("a ▸ b ▸ c", xml);
        int o0 = xml.IndexOf("<Offset>0</Offset>",  StringComparison.Ordinal);
        int o20 = xml.IndexOf("<Offset>20</Offset>", StringComparison.Ordinal);
        int o18 = xml.IndexOf("<Offset>18</Offset>", StringComparison.Ordinal);
        Assert.True(o0 >= 0 && o20 >= 0 && o18 >= 0);
        Assert.True(o0 < o20 && o20 < o18, $"offset order wrong: 0@{o0} 20@{o20} 18@{o18}");
        // Final object's two members emitted relative beneath.
        Assert.Contains("val", xml);
        Assert.Contains("other", xml);
        // Root + the single folded group = 2 GroupHeaders (not 4 nested groups).
        Assert.Equal(2, Count(xml, "<GroupHeader>1</GroupHeader>"));
    }

    [Fact]
    public async Task CollapseChain_OffEmitsNestedGroupsPerHop()
    {
        const string ptrA = "0x0000000000090000";
        const string ptrB = "0x0000000000091000";
        const string ptrC = "0x0000000000092000";
        var live = new FakeLive();
        live.AddInstance(ptrA, new List<WalkedField> { new() { Name = "b", Type = "MainGame.B", Offset = 0x18, Ptr = ptrB } });
        live.AddInstance(ptrB, new List<WalkedField> { new() { Name = "c", Type = "MainGame.C", Offset = 0x20, Ptr = ptrC } });
        live.AddInstance(ptrC, new List<WalkedField> { new() { Name = "val", Type = "System.Int32", Offset = 0x10, Value = Json("7") } });
        var root = new List<WalkedField> { new() { Name = "a", Type = "MainGame.A", Offset = 0x40, Ptr = ptrA } };

        string xml = await RenderAsync(live, root, LiveOpts);   // chain OFF

        // root + a + b + c = 4 nested GroupHeaders, no folded "a ▸ b ▸ c" label.
        Assert.Equal(4, Count(xml, "<GroupHeader>1</GroupHeader>"));
        Assert.DoesNotContain("a ▸ b ▸ c", xml);
    }

    // ---- Record colours --------------------------------------------------

    [Fact]
    public async Task RecordColors_TintAlternatingArrayElements()
    {
        const string arrObj = "0x00000000000A0000";
        var live = new FakeLive();
        live.AddArray(arrObj, new WalkArrayResult
        {
            Length = 3, Count = 3, IsPointer = false, ElementSize = 4,
            ElementClassName = "Int32",
            Elements = new List<WalkedArrayElement>
            {
                new() { Index = 0, Value = Json("10") },
                new() { Index = 1, Value = Json("20") },
                new() { Index = 2, Value = Json("30") },
            },
        });
        var root = new List<WalkedField>
        {
            new() { Name = "nums", Type = "System.Int32[]", Offset = 0x18, Ptr = arrObj },
        };

        string xml = await RenderAsync(live, root,
            LiveOpts with { RecordColors = true, RecordColorEven = "0080FF", RecordColorOdd = null });

        // Even indices (0, 2) → <Color> with BBGGRR of 0080FF = FF8000; odd (1)
        // has no colour (RecordColorOdd null = CE theme).
        Assert.Equal(2, Count(xml, "<Color>FF8000</Color>"));
        Assert.Equal(2, Count(xml, "<Color>"));   // exactly the two even rows
    }

    // ---- DescShowOffset --------------------------------------------------

    [Fact]
    public async Task DescShowOffset_AppendsOffsetToDescription()
    {
        var live = new FakeLive();
        var root = new List<WalkedField>
        {
            new() { Name = "gold", Type = "System.Int32", Offset = 0x18, Value = Json("500") },
        };

        string with = await RenderAsync(live, root, LiveOpts with { DescShowOffset = true });
        string without = await RenderAsync(new FakeLive(), root, LiveOpts);

        Assert.Contains("gold : System.Int32 (+18)", with);
        Assert.DoesNotContain("(+18)", without);
    }
}
