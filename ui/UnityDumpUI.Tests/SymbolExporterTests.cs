// SymbolExporterTests.cs — covers the three debugger-symbol exporters
// (x64dbg .dd64 JSON, Ghidra label text, IDA .idc script). The exporters
// are pure functions over SymbolDumpRow lists, so these tests pin the
// label-naming contract + overload-collision behaviour + format-specific
// quirks (module-relative addressing for x64dbg, function-vs-label tag
// for Ghidra, MakeName/MakeComm pairing for IDA) without touching the
// pipe layer.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;
using Xunit;

namespace UnityDumpUI.Tests;

public class SymbolExporterTests
{
    // ---- label sanitisation -----------------------------------------------

    [Theory]
    [InlineData("Foo.Bar",          "Foo_Bar")]
    [InlineData("Foo/Bar",          "Foo_Bar")]
    [InlineData("Foo<T>",           "Foo_T_")]
    [InlineData("Foo`1",            "Foo_1")]
    [InlineData("My Class",         "My_Class")]
    [InlineData("Already_Safe123",  "Already_Safe123")]
    [InlineData("",                 "unknown")]
    public void SanitizeLabel_CollapsesNonAlnumToUnderscore(string input, string expected)
    {
        Assert.Equal(expected, SymbolExporter.SanitizeLabel(input));
    }

    // ---- label naming -----------------------------------------------------

    [Fact]
    public void BuildLabel_ClassRow_PrefixesWithClass()
    {
        var row = new SymbolDumpRow
        {
            Kind = "class", Assembly = "Assembly-CSharp",
            Namespace = "Game", Class = "Player",
            Address = "0x00007FF6CAFEBABE",
        };
        Assert.Equal("Class_Game_Player", SymbolExporter.BuildLabel(row));
    }

    [Fact]
    public void BuildLabel_MethodRow_IncludesHumanSuffixForOverloadDisambiguation()
    {
        var a = new SymbolDumpRow
        {
            Kind = "method", Assembly = "Assembly-CSharp",
            Namespace = "Game", Class = "Player",
            Name = "TakeDamage", HumanSuffix = "int",
            Address = "0x00007FF600000010",
        };
        var b = new SymbolDumpRow
        {
            Kind = "method", Assembly = "Assembly-CSharp",
            Namespace = "Game", Class = "Player",
            Name = "TakeDamage", HumanSuffix = "float",
            Address = "0x00007FF600000020",
        };
        string la = SymbolExporter.BuildLabel(a);
        string lb = SymbolExporter.BuildLabel(b);
        Assert.Equal("Game_Player__TakeDamage_int",   la);
        Assert.Equal("Game_Player__TakeDamage_float", lb);
        Assert.NotEqual(la, lb);
    }

    [Fact]
    public void BuildLabel_MethodRow_NoNamespace_StartsWithClass()
    {
        var row = new SymbolDumpRow
        {
            Kind = "method", Assembly = "Assembly-CSharp",
            Namespace = "", Class = "Bootstrapper",
            Name = "Start", HumanSuffix = "noargs",
            Address = "0x00007FF600000100",
        };
        Assert.Equal("Bootstrapper__Start_noargs", SymbolExporter.BuildLabel(row));
    }

    // ---- x64dbg .dd64 -----------------------------------------------------

    [Fact]
    public void RenderX64dbgDatabase_EmitsModuleRelativeHexWithoutPrefix()
    {
        const ulong baseAddr = 0x00007FF6_00000000ul;
        var rows = new List<SymbolDumpRow>
        {
            new() {
                Kind = "method", Namespace = "Game", Class = "Player",
                Name = "Update", HumanSuffix = "noargs",
                Assembly = "Assembly-CSharp",
                Address = "0x00007FF600001234",
            },
        };

        string json = SymbolExporter.RenderX64dbgDatabase(
            rows, "GameAssembly.dll", baseAddr);

        using var doc = JsonDocument.Parse(json);
        var labels = doc.RootElement.GetProperty("labels");
        Assert.Equal(1, labels.GetArrayLength());

        var label = labels[0];
        Assert.Equal("GameAssembly.dll", label.GetProperty("module").GetString());
        // 0x00007FF600001234 - 0x00007FF600000000 = 0x1234. x64dbg expects
        // hex *without* the "0x" prefix and uppercase.
        Assert.Equal("1234", label.GetProperty("address").GetString());
        Assert.Equal("Game_Player__Update_noargs", label.GetProperty("text").GetString());
        Assert.True(label.GetProperty("manual").GetBoolean());
    }

    [Fact]
    public void RenderX64dbgDatabase_MethodRow_AlsoEmitsClassQualifierComment()
    {
        var rows = new List<SymbolDumpRow>
        {
            new() {
                Kind = "method", Namespace = "Game.Combat", Class = "Damageable",
                Name = "Hit", HumanSuffix = "int",
                Assembly = "Assembly-CSharp",
                Address = "0x00007FF6FFFF0001",
            },
        };
        string json = SymbolExporter.RenderX64dbgDatabase(
            rows, "GameAssembly.dll", 0x00007FF6_00000000ul);
        using var doc = JsonDocument.Parse(json);
        var comments = doc.RootElement.GetProperty("comments");
        Assert.Equal(1, comments.GetArrayLength());
        Assert.Equal("Assembly-CSharp:Game.Combat.Damageable",
            comments[0].GetProperty("text").GetString());
    }

    [Fact]
    public void RenderX64dbgDatabase_ZeroModuleBase_FallsBackToAbsolute()
    {
        var rows = new List<SymbolDumpRow>
        {
            new() {
                Kind = "class", Namespace = "Game", Class = "World",
                Assembly = "Assembly-CSharp",
                Address = "0x00007FF600005000",
            },
        };
        string json = SymbolExporter.RenderX64dbgDatabase(rows, "GameAssembly.dll", 0ul);
        using var doc = JsonDocument.Parse(json);
        // moduleBase = 0 → emit absolute hex (still without "0x").
        Assert.Equal("7FF600005000",
            doc.RootElement.GetProperty("labels")[0].GetProperty("address").GetString());
    }

    // ---- Ghidra -----------------------------------------------------------

    [Fact]
    public void RenderGhidraSymbols_EmitsFunctionTypeForMethodLabelForClass()
    {
        var rows = new List<SymbolDumpRow>
        {
            new() { Kind = "class",  Namespace = "Game", Class = "Player",
                    Assembly = "Assembly-CSharp",
                    Address = "0x00007FF6CAFEBABE" },
            new() { Kind = "method", Namespace = "Game", Class = "Player",
                    Name = "Move", HumanSuffix = "Vector3",
                    Assembly = "Assembly-CSharp",
                    Address = "0x00007FF6DEADBEEF" },
        };
        string text = SymbolExporter.RenderGhidraSymbols(rows);

        // Header comments are emitted but every symbol line must be one of
        //   "<label> <addr> <f|l>"
        var bodyLines = text
            .Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith("//"))
            .Select(l => l.Trim())
            .ToArray();

        Assert.Contains("Class_Game_Player 0x00007FF6CAFEBABE l", bodyLines);
        Assert.Contains("Game_Player__Move_Vector3 0x00007FF6DEADBEEF f", bodyLines);
    }

    // ---- IDA .idc ---------------------------------------------------------

    [Fact]
    public void RenderIdaScript_WrapsInIdcMain_EmitsMakeNameAndMakeComm()
    {
        var rows = new List<SymbolDumpRow>
        {
            new() { Kind = "method", Namespace = "Game", Class = "Player",
                    Name = "TakeDamage", HumanSuffix = "int",
                    Assembly = "Assembly-CSharp",
                    Address = "0x00007FF600009999" },
        };
        string idc = SymbolExporter.RenderIdaScript(rows);

        Assert.Contains("#include <idc.idc>", idc);
        Assert.Contains("static main() {", idc);
        Assert.Contains("MakeName(0x00007FF600009999, \"Game_Player__TakeDamage_int\");", idc);
        // Method rows also annotate with the declaring class.
        Assert.Contains("MakeComm(0x00007FF600009999, \"Assembly-CSharp:Game.Player\");", idc);
        Assert.EndsWith("}\r\n", idc.Replace("\n", "\r\n").TrimEnd() + "\r\n");
    }

    [Fact]
    public void RenderIdaScript_EscapesEmbeddedQuotes()
    {
        // Defensive — though SanitizeLabel collapses quotes to '_', the IDC
        // string-literal escape lives in EscapeIdcString. The MakeComm
        // payload (BuildClassQualifier) is NOT sanitised, so a class
        // genuinely named with a quote would otherwise blow up the IDC
        // parser.
        var rows = new List<SymbolDumpRow>
        {
            new() {
                Kind = "method", Namespace = "He said \"hi\"",
                Class = "Player",
                Name = "Start", HumanSuffix = "noargs",
                Assembly = "Assembly-CSharp",
                Address = "0x00007FF600000300",
            },
        };
        string idc = SymbolExporter.RenderIdaScript(rows);
        // Comment should contain escaped quotes — \" rather than a raw ".
        Assert.Contains("MakeComm(0x00007FF600000300, \"Assembly-CSharp:He said \\\"hi\\\".Player\");", idc);
    }
}
