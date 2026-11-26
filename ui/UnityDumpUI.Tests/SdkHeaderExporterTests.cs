// SdkHeaderExporterTests.cs — E-M7 + E-L4 regression suite.
//
// Verifies that generated C/C++ SDK headers are layout-accurate and
// compile-ready:
//   * Object header + inter-field padding from offset 0 (`_pad_XXXX`).
//   * Fields sorted by runtime offset regardless of input order.
//   * Guarded Unity math POD definitions emitted iff referenced.
//   * Duplicate member names across inheritance disambiguated with `_<Declaring>`.
//   * C++ reserved keywords safely suffixed; static / negative-offset fields skipped.
//   * Async lookup unwraps enums to underlying integer types and custom ValueTypes
//     to `uint8_t name[0xNN]` (or comment-only when size is unknown).
//   * Offline fallback prevents unconfirmed small fields (< ptr) from overlapping.
//   * Explicit-layout overlapping fields stay comment-only without shifting offsets.
//   * x86 (`pointerSize = 4`) layout + `UnityTypeMap.Resolve(type, 4)` sizing.
//   * Every emitted member's packed byte offset matches both its `// +0xNN` comment
//     and its `static_assert(offsetof(...))` line.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Services;
using UnityDumpUI.Core.Services.Export;
using Xunit;

namespace UnityDumpUI.Tests;

public class SdkHeaderExporterTests
{
    private sealed class StubLookup : IClassInfoLookup
    {
        private readonly Dictionary<string, GetClassInfoResult> _map = new(StringComparer.Ordinal);
        public Func<CancellationToken, Task>? OnCall { get; init; }

        public StubLookup Add(string ns, string name, GetClassInfoResult info)
        {
            _map[$"{ns}.{name}"] = info;
            return this;
        }

        public async Task<GetClassInfoResult?> GetClassInfoAsync(
            string assembly, string @namespace, string name, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (OnCall is not null)
                await OnCall(ct).ConfigureAwait(false);
            return _map.TryGetValue($"{@namespace}.{name}", out var res) ? res : null;
        }
    }

    // Parse a `#pragma pack(push, 1) struct Name { ... };` block and verify:
    //   1. No duplicate member names.
    //   2. Every declared member's cumulative packed byte offset equals its `// +0xNN` comment.
    //   3. Every `static_assert(offsetof(Struct, member) == 0xNN, ...)` matches the computed offset.
    private static Dictionary<string, int> VerifyPackedLayout(string header, int pointerSize = 8)
    {
        var structMatches = Regex.Matches(
            header,
            @"#pragma pack\(push, 1\)\r?\nstruct\s+(\w+)\s*\{\r?\n(.*?)\r?\n\};\r?\n#pragma pack\(pop\)",
            RegexOptions.Singleline);
        Assert.True(structMatches.Count > 0, "Header did not contain a packed struct block:\n" + header);
        Match structMatch = structMatches[^1];

        string structName = structMatch.Groups[1].Value;
        string body       = structMatch.Groups[2].Value;

        var memberOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        int cursor = 0;

        foreach (string rawLine in body.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("/*", StringComparison.Ordinal))
                continue;

            // Array member: `uint8_t name[0xNN];    // +0xXX ...`
            var arrMatch = Regex.Match(line, @"^uint8_t\s+(\w+)\[0x([0-9A-Fa-f]+)\];\s*//\s*\+0x([0-9A-Fa-f]+)");
            if (arrMatch.Success)
            {
                string name    = arrMatch.Groups[1].Value;
                int    size    = int.Parse(arrMatch.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                int    comment = int.Parse(arrMatch.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                Assert.True(memberOffsets.TryAdd(name, cursor), $"Duplicate member '{name}' in {structName}");
                Assert.Equal(comment, cursor);
                cursor += size;
                continue;
            }

            // Scalar / struct / pointer member: `<type> <name>;    // +0xXX ...`
            var scalarMatch = Regex.Match(line, @"^([\w\*]+)\s+(\w+);\s*//\s*\+0x([0-9A-Fa-f]+)");
            Assert.True(scalarMatch.Success, $"Unparseable member declaration: {line}");

            string cType   = scalarMatch.Groups[1].Value;
            string mName   = scalarMatch.Groups[2].Value;
            int    cOffset = int.Parse(scalarMatch.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int    cSize   = SizeOfCType(cType, pointerSize);

            Assert.True(memberOffsets.TryAdd(mName, cursor), $"Duplicate member '{mName}' in {structName}");
            Assert.Equal(cOffset, cursor);
            cursor += cSize;
        }

        // Verify every static_assert(offsetof(...)) matches the parsed packed offset.
        var assertMatches = Regex.Matches(
            header,
            @"static_assert\(offsetof\((\w+),\s*(\w+)\)\s*==\s*0x([0-9A-Fa-f]+),");
        foreach (Match m in assertMatches)
        {
            Assert.Equal(structName, m.Groups[1].Value);
            string member   = m.Groups[2].Value;
            int    expected = int.Parse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            Assert.True(memberOffsets.TryGetValue(member, out int actual),
                $"static_assert references missing member '{member}'");
            Assert.Equal(expected, actual);
        }

        return memberOffsets;
    }

    private static int SizeOfCType(string cType, int pointerSize) => cType switch
    {
        "uint8_t" or "int8_t"         => 1,
        "uint16_t" or "int16_t"       => 2,
        "uint32_t" or "int32_t"       => 4,
        "float" or "Color32"          => 4,
        "uint64_t" or "int64_t"       => 8,
        "double" or "Vector2"         => 8,
        "Vector3"                     => 12,
        "Vector4" or "Quaternion"
            or "Color" or "Rect"      => 16,
        "Bounds"                      => 24,
        "void*"                       => pointerSize,
        _ => throw new InvalidOperationException($"Unknown C type in test parser: '{cType}'"),
    };

    [Fact]
    public void Render_SortsByOffset_InsertsHeaderAndInterFieldPadding_AndEmitsStaticAsserts()
    {
        // Deliberately unsorted fields + managed object header (+0x10) + inter-field gap.
        var self = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Player",
            Fields    = new List<FieldEntry>
            {
                new() { Name = "speed",  Type = "System.Single", Offset = 0x20, Static = false },
                new() { Name = "hp",     Type = "System.Int32",  Offset = 0x10, Static = false },
                new() { Name = "alive",  Type = "System.Boolean",Offset = 0x14, Static = false },
                new() { Name = "s_inst", Type = "Game.Player",   Offset = 0x00, Static = true  },
                new() { Name = "tls",    Type = "System.Int32",  Offset = -1,   Static = false },
            },
        };

        string h = SdkHeaderExporter.Render(self, assemblyName: "Assembly-CSharp");
        var offsets = VerifyPackedLayout(h, pointerSize: 8);

        Assert.Equal(0x00, offsets["_pad_0000"]);
        Assert.Equal(0x10, offsets["hp"]);
        Assert.Equal(0x14, offsets["alive"]);
        Assert.Equal(0x15, offsets["_pad_0015"]);
        Assert.Equal(0x20, offsets["speed"]);
        Assert.DoesNotContain("s_inst", h);
        Assert.DoesNotContain("DISCRETE_UNITY_MATH_TYPES", h); // no math types used
        Assert.Contains("static_assert(offsetof(Player, hp) == 0x10", h);
        Assert.Contains("static_assert(offsetof(Player, alive) == 0x14", h);
        Assert.Contains("static_assert(offsetof(Player, speed) == 0x20", h);
        Assert.Contains("skipped fields with non-instance / invalid offsets", h);
    }

    [Fact]
    public void Render_UnityMathTypes_EmitsGuardedPodDefinitionsAndExactOffsets()
    {
        var self = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "TransformSnapshot",
            Fields    = new List<FieldEntry>
            {
                new() { Name = "pos",    Type = "UnityEngine.Vector3",    Offset = 0x10, Static = false },
                new() { Name = "rot",    Type = "UnityEngine.Quaternion", Offset = 0x1C, Static = false },
                new() { Name = "bounds", Type = "UnityEngine.Bounds",     Offset = 0x2C, Static = false },
                new() { Name = "tint",   Type = "UnityEngine.Color32",    Offset = 0x44, Static = false },
            },
        };

        string h = SdkHeaderExporter.Render(self);
        Assert.Contains("#ifndef DISCRETE_UNITY_MATH_TYPES", h);
        Assert.Contains("struct Vector3    { float x, y, z; };", h);
        Assert.Contains("struct Bounds     { Vector3 center; Vector3 extents; };", h);

        var offsets = VerifyPackedLayout(h, pointerSize: 8);
        Assert.Equal(0x10, offsets["pos"]);
        Assert.Equal(0x1C, offsets["rot"]);
        Assert.Equal(0x2C, offsets["bounds"]);
        Assert.Equal(0x44, offsets["tint"]);
    }

    [Fact]
    public void Render_InheritedShadowedFieldsAndCppKeywords_AreDisambiguatedAndCompileSafe()
    {
        var grandParent = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Entity",
            Fields    = new List<FieldEntry>
            {
                new() { Name = "id",      Type = "System.Int32", Offset = 0x10, Static = false },
                new() { Name = "default", Type = "System.Byte",  Offset = 0x14, Static = false },
            },
        };
        var parent = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Actor",
            Fields    = new List<FieldEntry>
            {
                new() { Name = "id",    Type = "System.Int32", Offset = 0x18, Static = false },
                new() { Name = "class", Type = "System.Int32", Offset = 0x1C, Static = false },
            },
        };
        var self = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Hero",
            Parent    = new ClassRef { Namespace = "Game", Name = "Actor" },
            Fields    = new List<FieldEntry>
            {
                new() { Name = "id", Type = "System.Int32", Offset = 0x20, Static = false },
            },
        };

        string h = SdkHeaderExporter.Render(self, ancestors: new[] { grandParent, parent });
        var offsets = VerifyPackedLayout(h, pointerSize: 8);

        Assert.Equal(0x10, offsets["id"]);
        Assert.Equal(0x14, offsets["default_"]);
        Assert.Equal(0x18, offsets["id_Actor"]);
        Assert.Equal(0x1C, offsets["class_"]);
        Assert.Equal(0x20, offsets["id_Hero"]);
    }

    [Fact]
    public async Task RenderAsync_WithLookup_ResolvesEnumValueTypeUnknownStructAndClass()
    {
        var lookup = new StubLookup()
            .Add("Game", "Faction", new GetClassInfoResult
            {
                Namespace = "Game", Name = "Faction",
                Parent = new ClassRef { Namespace = "System", Name = "Enum" },
                Fields = new List<FieldEntry>
                {
                    new() { Name = "value__", Type = "System.Byte", Offset = 0x10, Static = false },
                },
            })
            .Add("Game", "StatPair", new GetClassInfoResult
            {
                Namespace = "Game", Name = "StatPair",
                Parent = new ClassRef { Namespace = "System", Name = "ValueType" },
                Fields = new List<FieldEntry>
                {
                    new() { Name = "cur", Type = "System.Int32", Offset = 0x10, Static = false },
                    new() { Name = "max", Type = "System.Int32", Offset = 0x14, Static = false },
                },
            })
            .Add("Game", "OpaqueStruct", new GetClassInfoResult
            {
                Namespace = "Game", Name = "OpaqueStruct",
                Parent = new ClassRef { Namespace = "System", Name = "ValueType" },
                Fields = new List<FieldEntry>(), // unknown / empty size
            })
            .Add("Game", "Inventory", new GetClassInfoResult
            {
                Namespace = "Game", Name = "Inventory",
                Parent = new ClassRef { Namespace = "System", Name = "Object" },
                Fields = new List<FieldEntry>
                {
                    new() { Name = "gold", Type = "System.Int32", Offset = 0x10, Static = false },
                },
            });

        var self = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Unit",
            Fields    = new List<FieldEntry>
            {
                new() { Name = "faction", Type = "Game.Faction",      Offset = 0x10, Static = false },
                new() { Name = "hpPair",  Type = "Game.StatPair",     Offset = 0x14, Static = false },
                new() { Name = "opaque",  Type = "Game.OpaqueStruct", Offset = 0x1C, Static = false },
                new() { Name = "bag",     Type = "Game.Inventory",    Offset = 0x28, Static = false },
            },
        };

        string h = await SdkHeaderExporter.RenderAsync(
            self, assemblyName: "Assembly-CSharp", options: new ExportOptions(PointerSize: 8),
            lookup: lookup, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

        var offsets = VerifyPackedLayout(h, pointerSize: 8);
        Assert.Equal(0x10, offsets["faction"]);
        Assert.Contains("uint8_t faction;", h);
        Assert.Equal(0x14, offsets["hpPair"]);
        Assert.Contains("uint8_t hpPair[0x8];", h);
        // OpaqueStruct emitted as comment only; padding bridges 0x1C..0x28 so `bag` lands at 0x28.
        Assert.Contains("// +0x1C  opaque  (Game.OpaqueStruct, value type — unknown size; omitted)", h);
        Assert.Equal(0x1C, offsets["_pad_001C"]);
        Assert.Equal(0x28, offsets["bag"]);
        Assert.Contains("void* bag;", h);
    }

    [Fact]
    public void Render_OfflineUnknownSmallFieldBeforeNextField_InferredAsByteArrayWithoutOverlap()
    {
        // Without lookup, an unknown type (e.g. 4-byte enum) at +0x10 followed by
        // Int32 at +0x14 must NOT emit an 8-byte `void*` that would push `hp` to +0x18.
        var self = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "Status",
            Fields    = new List<FieldEntry>
            {
                new() { Name = "state", Type = "Game.UnitState", Offset = 0x10, Static = false },
                new() { Name = "hp",    Type = "System.Int32",   Offset = 0x14, Static = false },
            },
        };

        string h = SdkHeaderExporter.Render(self);
        var offsets = VerifyPackedLayout(h, pointerSize: 8);

        Assert.Equal(0x10, offsets["state"]);
        Assert.Contains("uint8_t state[0x4];", h);
        Assert.Equal(0x14, offsets["hp"]);
        Assert.Contains("static_assert(offsetof(Status, hp) == 0x14", h);
    }

    [Fact]
    public void Render_OverlappingExplicitLayoutFields_EmitsSecondAsCommentAndPreservesLaterOffsets()
    {
        var self = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "UnionLike",
            Fields    = new List<FieldEntry>
            {
                new() { Name = "asInt",   Type = "System.Int32",  Offset = 0x10, Static = false },
                new() { Name = "asFloat", Type = "System.Single", Offset = 0x10, Static = false },
                new() { Name = "after",   Type = "System.Int64",  Offset = 0x18, Static = false },
            },
        };

        string h = SdkHeaderExporter.Render(self);
        var offsets = VerifyPackedLayout(h, pointerSize: 8);

        Assert.Equal(0x10, offsets["asInt"]);
        Assert.False(offsets.ContainsKey("asFloat"));
        Assert.Contains("[overlaps prior member ending at +0x14] System.Single asFloat", h);
        Assert.Equal(0x18, offsets["after"]);
    }

    [Fact]
    public void Render_X86PointerSize_UsesFourBytePointersAndNativeInts()
    {
        var self = new GetClassInfoResult
        {
            Namespace = "Game",
            Name      = "X86Obj",
            Fields    = new List<FieldEntry>
            {
                new() { Name = "handle", Type = "System.IntPtr",  Offset = 0x08, Static = false },
                new() { Name = "uptr",   Type = "System.UIntPtr", Offset = 0x0C, Static = false },
                new() { Name = "name",   Type = "System.String",  Offset = 0x10, Static = false },
                new() { Name = "hp",     Type = "System.Int32",   Offset = 0x14, Static = false },
            },
        };

        string h = SdkHeaderExporter.Render(self, pointerSize: 4);
        var offsets = VerifyPackedLayout(h, pointerSize: 4);

        Assert.Contains("uint8_t _pad_0000[0x8];", h);
        Assert.Contains("int32_t handle;", h);
        Assert.Contains("uint32_t uptr;", h);
        Assert.Contains("uint32_t name;", h);
        Assert.Equal(0x08, offsets["handle"]);
        Assert.Equal(0x0C, offsets["uptr"]);
        Assert.Equal(0x10, offsets["name"]);
        Assert.Equal(0x14, offsets["hp"]);
    }

    [Theory]
    [InlineData("System.IntPtr",  4, 4, "4 Bytes", "int32_t",   false)]
    [InlineData("System.UIntPtr", 4, 4, "4 Bytes", "uint32_t",  false)]
    [InlineData("System.String",  4, 4, "Pointer", "void* /* MonoString / Il2CppString */", true)]
    [InlineData("MyNs.MyClass",   4, 4, "Pointer", "void* /* MyNs.MyClass */", true)]
    [InlineData("System.IntPtr",  8, 8, "8 Bytes", "intptr_t",  false)]
    [InlineData("System.UIntPtr", 8, 8, "8 Bytes", "uintptr_t", false)]
    public void UnityTypeMap_Resolve_RespectsPointerSize(
        string unityType, int pointerSize, int expectedBytes, string expectedCe, string expectedC, bool expectedPtr)
    {
        ManagedTypeShape shape = UnityTypeMap.Resolve(unityType, pointerSize);
        Assert.Equal(expectedBytes, shape.SizeBytes);
        Assert.Equal(expectedCe,    shape.CeTypeKeyword);
        Assert.Equal(expectedC,     shape.CHeaderType);
        Assert.Equal(expectedPtr,   shape.IsPointer);
    }

    [Fact]
    public async Task RenderAsync_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var lookup = new StubLookup
        {
            OnCall = _ =>
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };

        var self = new GetClassInfoResult
        {
            Namespace = "Game", Name = "Big",
            Fields = new List<FieldEntry>
            {
                new() { Name = "sub", Type = "Game.Sub", Offset = 0x10, Static = false },
            },
        };

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await SdkHeaderExporter.RenderAsync(self, lookup: lookup, ct: cts.Token).ConfigureAwait(true))
            .ConfigureAwait(true);
    }
}
