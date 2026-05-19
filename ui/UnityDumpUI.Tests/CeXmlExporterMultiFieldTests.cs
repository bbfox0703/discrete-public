// CeXmlExporterMultiFieldTests.cs — regression coverage for the
// Phase-23 "Copy CE field" multi-row export fix.
//
// Bug: the Fields-tab and Inspector-tab "Copy CE field" right-click
// commands only emitted the last-focused row when the user Ctrl/Shift-
// selected multiple rows. The VMs now snapshot DataGrid.SelectedItems
// via a View callback and route the multi-row case through
// CeXmlExporter.RenderFieldsAsync (Fields tab) / WrapLeavesXml
// (Inspector tab). These tests pin the exporter's multi-row shape so a
// future regression — e.g. someone re-introducing the single-field-only
// API in CeXmlExporter — fails loudly.

using System.Collections.Generic;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;
using Xunit;
using Xunit.v3;

namespace UnityDumpUI.Tests;

public class CeXmlExporterMultiFieldTests
{
    private static FieldEntry Field(string name, string type, int offset) => new()
    {
        Name   = name,
        Type   = type,
        Offset = offset,
        Flags  = 0,
        Static = false,
    };

    private static GetClassInfoResult DeclaringClass() => new()
    {
        Namespace = "MainGame",
        Name      = "PlayerState",
        Fields    = new List<FieldEntry>
        {
            Field("hp",     "System.Int32", 0x10),
            Field("mp",     "System.Int32", 0x14),
            Field("name",   "System.String", 0x18),
        },
    };

    private static ExportOptions Options() => new(
        DrilldownDepth:       0,    // keep output flat for the snapshot
        ArrayLimit:           0,
        CollapsePointerNodes: false,
        LiveData:             false);

    [Fact]
    public async Task RenderFieldsAsync_SingleField_MatchesLegacyShape()
    {
        // Single-element list MUST byte-match the old RenderFieldAsync output
        // — the legacy path is still the most common right-click flow and any
        // diff here would churn existing CE tables users have already pasted
        // out.
        var info = DeclaringClass();
        FieldEntry hp = info.Fields![0];

        string legacy = await CeXmlExporter.RenderFieldAsync(
            "0x1000", hp, info, "Assembly-CSharp", Options(), lookup: null, ct: TestContext.Current.CancellationToken);
        string multi  = await CeXmlExporter.RenderFieldsAsync(
            "0x1000", new[] { hp }, info, "Assembly-CSharp", Options(), lookup: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(legacy, multi);
    }

    [Fact]
    public async Task RenderFieldsAsync_MultipleFields_EmitsOneEntryPerRow()
    {
        var info = DeclaringClass();
        var fields = new[] { info.Fields![0], info.Fields![1], info.Fields![2] };

        string xml = await CeXmlExporter.RenderFieldsAsync(
            "0x1000", fields, info, "Assembly-CSharp", Options(), lookup: null, ct: TestContext.Current.CancellationToken);

        // Document header + single root <CheatTable>/<CheatEntries> wrapper.
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<CheatTable>\n  <CheatEntries>\n", xml);
        Assert.EndsWith("  </CheatEntries>\n</CheatTable>\n", xml);

        // Exactly one root group (the declaring-class wrapper) + one entry
        // per supplied field underneath. Count via substring — four
        // <CheatEntry> tags total: 1 root group + 3 fields.
        Assert.Equal(4, CountOccurrences(xml, "<CheatEntry>"));
        Assert.Equal(4, CountOccurrences(xml, "</CheatEntry>"));

        // Multi-row header uses the bare class label (no per-field suffix
        // — multi-row entries underneath would make any single field's name
        // misleading in the header).
        Assert.Contains("\"Assembly-CSharp!MainGame.PlayerState\"", xml);
        Assert.DoesNotContain("PlayerState.hp", xml);  // single-field suffix only

        // Every selected field is present by name + type label.
        Assert.Contains("hp : System.Int32",     xml);
        Assert.Contains("mp : System.Int32",     xml);
        Assert.Contains("name : System.String",  xml);

        // Per-field addresses are the wrapper-relative +offset form (the
        // wrapper itself carries the base address).
        Assert.Contains("<Address>+10</Address>", xml);  // hp at 0x10
        Assert.Contains("<Address>+14</Address>", xml);  // mp at 0x14
        Assert.Contains("<Address>+18</Address>", xml);  // name at 0x18
    }

    [Fact]
    public async Task RenderFieldsAsync_FieldsArePreservedInSelectionOrder()
    {
        var info = DeclaringClass();
        // Hand the fields in reverse order — the export must follow the
        // caller's order, not the declaring class's offset order, so a user
        // who Ctrl-clicked rows in a custom order gets that order back.
        var fields = new[] { info.Fields![2], info.Fields![0] };

        string xml = await CeXmlExporter.RenderFieldsAsync(
            "0x1000", fields, info, "Assembly-CSharp", Options(), lookup: null, ct: TestContext.Current.CancellationToken);

        int nameIdx = xml.IndexOf("name : System.String", System.StringComparison.Ordinal);
        int hpIdx   = xml.IndexOf("hp : System.Int32",   System.StringComparison.Ordinal);
        Assert.True(nameIdx > 0 && hpIdx > 0, "both fields should appear");
        Assert.True(nameIdx < hpIdx, "supplied order must be preserved");
    }

    [Fact]
    public async Task RenderFieldsAsync_EmptyInput_Throws()
    {
        var info = DeclaringClass();
        await Assert.ThrowsAsync<System.ArgumentException>(() =>
            CeXmlExporter.RenderFieldsAsync(
                "0x1000", System.Array.Empty<FieldEntry>(),
                info, "Assembly-CSharp", Options(), lookup: null,
                ct: TestContext.Current.CancellationToken));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int idx   = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
