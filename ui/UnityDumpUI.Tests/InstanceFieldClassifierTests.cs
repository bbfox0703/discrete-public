// InstanceFieldClassifierTests.cs — offline regression coverage for the
// Inspector's kind-routing logic. Mirrors the precedence rules documented at
// the top of UnityDumpUI.Core/Services/InstanceFieldClassifier.cs.
//
// Test scope:
//   * Each precedence rule has at least one positive test (kind matches) and,
//     where useful, a negative test (a similar input that should NOT trigger
//     that rule).
//   * Type-name parsing helpers (TryParseCollectionType / TryParseClassName)
//     have separate fixtures because they're individually consumed elsewhere
//     (LoadArrayChildrenAsync / Copy CE XML / FromArrayElement).
//   * Inputs the wire layer can plausibly produce — null Type, "0x0..." pointer,
//     IsValueType true alongside an inline Value, generic instantiations with
//     dotted namespaces in the type argument — are exercised explicitly.

using System.Text.Json;
using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class InstanceFieldClassifierTests
{
    // ----------------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Build a wire <see cref="WalkedField"/> shaped like one row of a
    /// <c>walkInstance</c> response. All defaults match the wire-default of
    /// the corresponding JSON property (null / 0 / false).
    /// </summary>
    private static WalkedField Field(
        string name = "f",
        string? type = null,
        int offset = 0,
        string? address = null,
        string? ptr = null,
        string? str = null,
        JsonElement? value = null,
        bool isEnum = false,
        bool isValueType = false)
        => new()
        {
            Name        = name,
            Type        = type,
            Offset      = offset,
            Address     = address,
            Ptr         = ptr,
            String      = str,
            Value       = value,
            IsEnum      = isEnum,
            IsValueType = isValueType,
        };

    /// <summary>Wrap a primitive as a <see cref="JsonElement"/> the way the wire layer would.</summary>
    private static JsonElement Json(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    // ----------------------------------------------------------------------
    //  Rule 1 — IsEnum trumps everything else
    // ----------------------------------------------------------------------

    [Fact]
    public void Classify_EnumWithDecodedValue_IsLeaf()
    {
        // Server-side enum: even with IsValueType=true (every enum technically
        // is one) and a decoded Value, the row is a Leaf showing the int.
        WalkedField f = Field(
            type:        "MyGame.WeaponKind",
            address:     "0x1000",
            value:       Json("3"),
            isEnum:      true,
            isValueType: true);

        Assert.Equal(WalkedFieldKind.Leaf, InstanceFieldClassifier.Classify(f, isPointerValue: false));
    }

    [Fact]
    public void Classify_EnumWithoutValue_StillLeaf()
    {
        // Older runtimes that didn't bind underlying-type decode still flag
        // IsEnum. We must not promote to ValueStruct — enums have no walkable
        // inner fields, so expanding crashes the inspector.
        WalkedField f = Field(
            type:        "MyGame.WeaponKind",
            address:     "0x1000",
            isEnum:      true,
            isValueType: true);

        Assert.Equal(WalkedFieldKind.Leaf, InstanceFieldClassifier.Classify(f, isPointerValue: false));
    }

    // ----------------------------------------------------------------------
    //  Rule 2 — Collection (T[] / List<T>)
    // ----------------------------------------------------------------------

    [Fact]
    public void Classify_NonNullArrayPointer_IsCollection()
    {
        WalkedField f = Field(
            type:    "System.Int32[]",
            address: "0x2000",
            ptr:     "0x7FFE12340000");

        Assert.Equal(WalkedFieldKind.Collection, InstanceFieldClassifier.Classify(f, isPointerValue: true));
    }

    [Fact]
    public void Classify_NonNullListPointer_IsCollection()
    {
        WalkedField f = Field(
            type:    "System.Collections.Generic.List`1<MainGame.CARRYITEM>",
            address: "0x2000",
            ptr:     "0x7FFE56780000");

        Assert.Equal(WalkedFieldKind.Collection, InstanceFieldClassifier.Classify(f, isPointerValue: true));
    }

    [Fact]
    public void Classify_NonNullDemangledListPointer_IsCollection()
    {
        // Live-runtime wire form for List<T> — the `1 arity marker is
        // stripped. Regression for the bug where enchantList_ /
        // passiveSkillList_ / hasSkill_ etc. showed no expand chevron.
        WalkedField f = Field(
            type:    "System.Collections.Generic.List<CharacterData.EnchantData>",
            address: "0x2000",
            ptr:     "0x0000023ABE054B10");

        Assert.Equal(WalkedFieldKind.Collection, InstanceFieldClassifier.Classify(f, isPointerValue: true));
    }

    [Fact]
    public void Classify_DictionaryPointer_StaysLeaf()
    {
        // Dictionary<K,V> is intentionally a Leaf (NOT expandable from the UI):
        // its inner _entries are System.…Entry value structs that can't be
        // resolved by name and have no header to walk, so a UI-only expansion
        // would never surface key/value — only noisy internals. Leafing also
        // keeps Copy CE XML / Copy CE field on dicts safe (no recursion → no
        // CTD). A real dict expansion needs a native Dictionary-aware walk.
        WalkedField f = Field(
            type:    "System.Collections.Generic.Dictionary<System.Int32,System.Int32>",
            address: "0x2000",
            ptr:     "0x000002768BC65A0");

        Assert.Equal(WalkedFieldKind.Leaf, InstanceFieldClassifier.Classify(f, isPointerValue: true));
    }

    [Fact]
    public void Classify_NullArrayPointer_DegradesToLeaf()
    {
        // Empty array → no expansion; row is a leaf (no chevron).
        WalkedField f = Field(
            type:    "System.Int32[]",
            address: "0x2000",
            ptr:     "0x0000000000000000");

        Assert.Equal(WalkedFieldKind.Leaf, InstanceFieldClassifier.Classify(f, isPointerValue: true));
    }

    [Fact]
    public void Classify_MissingArrayPointer_DegradesToLeaf()
    {
        WalkedField f = Field(
            type:    "MainGame.CARRYITEM[]",
            address: "0x2000",
            ptr:     null);

        Assert.Equal(WalkedFieldKind.Leaf, InstanceFieldClassifier.Classify(f, isPointerValue: false));
    }

    // ----------------------------------------------------------------------
    //  Rule 3 — Inline value-type struct (Vector3 / custom struct)
    // ----------------------------------------------------------------------

    [Fact]
    public void Classify_ServerSideValueTypeWithoutInlineValue_IsValueStruct()
    {
        // Newer runtime: IsValueType=true, no inline Value/String → expand.
        WalkedField f = Field(
            type:        "UnityEngine.Vector3",
            address:     "0x3000",
            ptr:         "0xDEADBEEF",  // garbage from catch-all 8-byte read
            isValueType: true);

        Assert.Equal(WalkedFieldKind.ValueStruct, InstanceFieldClassifier.Classify(f, isPointerValue: false));
    }

    [Fact]
    public void Classify_LegacyValueTypeViaUnityTypeMap_IsValueStruct()
    {
        // Older resolver doesn't bind the type→class bridge — IsValueType=false
        // on the wire. The legacy fallback only catches types that
        // UnityTypeMap explicitly classifies as non-pointer (the hard-coded
        // Unity value-type list: Vector2/3/4, Quaternion, Color, Color32,
        // Rect, Bounds). Custom user structs (e.g. MainGame.PLY_WORK) are
        // unknown to UnityTypeMap and fall through to IsPointer=true, so
        // they'd require IsValueType=true from a modern runtime resolver.
        WalkedField f = Field(
            type:        "UnityEngine.Vector3",
            address:     "0x4000",
            ptr:         "0xDEADBEEF",
            isValueType: false);

        Assert.Equal(WalkedFieldKind.ValueStruct, InstanceFieldClassifier.Classify(f, isPointerValue: false));
    }

    [Fact]
    public void Classify_UnknownUserStructWithoutValueTypeFlag_IsPointerToClassWhenPointed()
    {
        // Documenting the limit of the legacy path: an unknown user value-type
        // struct without the IsValueType flag is indistinguishable on the wire
        // from a pointer-to-class, so the catch-all rule routes it to
        // PointerToClass. The runtime side must populate IsValueType to fix
        // this — that's what the modern resolver bindings exist for.
        WalkedField f = Field(
            type:        "MainGame.PLY_WORK",
            address:     "0x4000",
            ptr:         "0x7FFE12345678",
            isValueType: false);

        Assert.Equal(WalkedFieldKind.PointerToClass, InstanceFieldClassifier.Classify(f, isPointerValue: true));
    }

    [Fact]
    public void Classify_ValueTypeFlagWithInlinePrimitive_IsLeaf()
    {
        // Every primitive technically IS a value type. The wire layer sets
        // IsValueType=true for System.Int32, but it also decodes the value
        // inline. Without the primitiveDecoded short-circuit, we'd promote
        // Int32 fields to ValueStruct ("expand"), which is wrong.
        WalkedField f = Field(
            type:        "System.Int32",
            address:     "0x5000",
            value:       Json("42"),
            isValueType: true);

        Assert.Equal(WalkedFieldKind.Leaf, InstanceFieldClassifier.Classify(f, isPointerValue: false));
    }

    [Fact]
    public void Classify_ValueTypeFlagWithDecodedString_IsLeaf()
    {
        // String fields come back with `string` populated; even if the wire
        // layer mis-flags IsValueType, we must not route to ValueStruct.
        WalkedField f = Field(
            type:        "System.String",
            address:     "0x5100",
            ptr:         "0x7FFE99990000",
            str:         "hello",
            isValueType: true);

        Assert.Equal(WalkedFieldKind.Leaf, InstanceFieldClassifier.Classify(f, isPointerValue: true));
    }

    [Fact]
    public void Classify_SystemIntPtrValueType_IsLeaf_NotExpandableStruct()
    {
        // m_CachedPtr (UnityEngine.Object's native handle) is System.IntPtr:
        // IsValueType=true, decoded as a Ptr (not Value/String). It has no
        // walkable managed fields, so it must be a Leaf — not an "(struct —
        // expand)" chevron that fails with "class not resolved".
        WalkedField f = Field(
            type:        "System.IntPtr",
            address:     "0x5200",
            ptr:         "0x000000000EAE7B80",
            isValueType: true);

        Assert.Equal(WalkedFieldKind.Leaf, InstanceFieldClassifier.Classify(f, isPointerValue: false));
    }

    [Fact]
    public void Classify_SystemSingleValueTypeUndecoded_IsLeaf()
    {
        // A System.Single whose value wasn't inline-decoded (e.g. it sits at a
        // garbage address inside a null array element) must still be a Leaf —
        // the IsValueType flag alone must not promote a System.* scalar to an
        // expandable struct.
        WalkedField f = Field(
            type:        "System.Single",
            address:     "0x0000000000000010",
            isValueType: true);

        Assert.Equal(WalkedFieldKind.Leaf, InstanceFieldClassifier.Classify(f, isPointerValue: false));
    }

    [Fact]
    public void Classify_ValueStructMissingAddress_DegradesAwayFromValueStruct()
    {
        // ValueStruct routing requires Address (the field's own storage location)
        // — without it, walkInstance has nowhere to start. The classifier should
        // step down to Leaf rather than emit a row that crashes on expand.
        WalkedField f = Field(
            type:        "UnityEngine.Vector3",
            address:     null,
            isValueType: true);

        // Falls through Rule 3, has no Ptr for Rule 4 either → Leaf.
        Assert.Equal(WalkedFieldKind.Leaf, InstanceFieldClassifier.Classify(f, isPointerValue: false));
    }

    // ----------------------------------------------------------------------
    //  Rule 4 — Pointer-to-class
    // ----------------------------------------------------------------------

    [Fact]
    public void Classify_NonNullManagedPointer_IsPointerToClass()
    {
        WalkedField f = Field(
            type:    "MainGame.PlayerState",
            address: "0x6000",
            ptr:     "0x7FFEABCDEF00");

        Assert.Equal(WalkedFieldKind.PointerToClass, InstanceFieldClassifier.Classify(f, isPointerValue: true));
    }

    [Fact]
    public void Classify_GenericInstancePointer_IsPointerToClass()
    {
        // Make sure the dotted namespace inside the generic argument doesn't
        // confuse TryParseClassName — outer name is `MainGame.ClassAry`, not
        // `MainGame.ClassAry<MainGame`.
        WalkedField f = Field(
            type:    "MainGame.ClassAry<MainGame.CARRYITEM>",
            address: "0x6100",
            ptr:     "0x7FFEABCDEF80");

        Assert.Equal(WalkedFieldKind.PointerToClass, InstanceFieldClassifier.Classify(f, isPointerValue: true));
    }

    [Fact]
    public void Classify_NullManagedPointer_IsLeaf()
    {
        // Null managed pointer — show the null but don't offer expansion.
        WalkedField f = Field(
            type:    "MainGame.PlayerState",
            address: "0x6000",
            ptr:     "0x0000000000000000");

        Assert.Equal(WalkedFieldKind.Leaf, InstanceFieldClassifier.Classify(f, isPointerValue: true));
    }

    [Fact]
    public void Classify_SystemPrimitivePointerLikeValue_IsLeaf()
    {
        // System.* never resolves as PointerToClass; primitives always degrade
        // to Leaf even when the wire layer happens to hand us a non-null Ptr
        // (catch-all 8-byte decode for unknown types).
        WalkedField f = Field(
            type:    "System.Int32",
            address: "0x6200",
            ptr:     "0x000000000000002A");

        Assert.Equal(WalkedFieldKind.Leaf, InstanceFieldClassifier.Classify(f, isPointerValue: true));
    }

    [Fact]
    public void Classify_UnknownTypeWithPointer_IsLeaf()
    {
        // No type info → can't route as PointerToClass / ValueStruct; degrade.
        WalkedField f = Field(
            type:    null,
            address: "0x6300",
            ptr:     "0x7FFE11110000");

        Assert.Equal(WalkedFieldKind.Leaf, InstanceFieldClassifier.Classify(f, isPointerValue: true));
    }

    // ----------------------------------------------------------------------
    //  TryParseCollectionType
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData("System.Int32[]",                    "System.Int32",                false)]
    [InlineData("MainGame.CARRYITEM[]",              "MainGame.CARRYITEM",          false)]
    [InlineData("System.Collections.Generic.List`1<MainGame.CARRYITEM>", "MainGame.CARRYITEM", true)]
    [InlineData("System.Collections.Generic.List`1<System.Int32>",       "System.Int32",       true)]
    // Demangled form (no `1 arity marker) — what the live runtime resolver
    // actually hands us for List<T> fields. Must classify the same as the
    // IL-form above, otherwise the Inspector shows no expand chevron.
    [InlineData("System.Collections.Generic.List<CharacterData.EnchantData>", "CharacterData.EnchantData", true)]
    [InlineData("System.Collections.Generic.List<System.String>",            "System.String",             true)]
    public void TryParseCollectionType_Recognised(string typeName, string expectedElement, bool expectedIsList)
    {
        bool ok = InstanceFieldClassifier.TryParseCollectionType(typeName, out string elementType, out bool isList);
        Assert.True(ok);
        Assert.Equal(expectedElement, elementType);
        Assert.Equal(expectedIsList, isList);
    }

    [Theory]
    [InlineData("System.Int32")]
    [InlineData("UnityEngine.Vector3")]
    [InlineData("System.Collections.Generic.Dictionary`2<int, int>")]   // not List, not raw array
    [InlineData("")]
    public void TryParseCollectionType_NotRecognised(string typeName)
    {
        Assert.False(InstanceFieldClassifier.TryParseCollectionType(typeName, out _, out _));
    }

    // ----------------------------------------------------------------------
    //  TryParseClassName
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData("MainGame.PlayerState",   "MainGame",   "PlayerState")]
    [InlineData("UnityEngine.GameObject", "UnityEngine", "GameObject")]
    [InlineData("BongoCat",               "",           "BongoCat")]               // top-level, no namespace
    [InlineData("MainGame.ClassAry<MainGame.CARRYITEM>", "MainGame", "ClassAry")]   // dotted namespace inside generic arg
    [InlineData("MainGame.Outer<int, string>",          "MainGame", "Outer")]      // multi-arg generic
    public void TryParseClassName_Splits(string type, string expectedNs, string expectedName)
    {
        bool ok = InstanceFieldClassifier.TryParseClassName(type, out string ns, out string name);
        Assert.True(ok);
        Assert.Equal(expectedNs, ns);
        Assert.Equal(expectedName, name);
    }

    [Theory]
    [InlineData("System.Int32")]            // System.* primitives have no class to walk
    [InlineData("System.Single")]
    [InlineData("System.String")]
    [InlineData("System.Int32[]")]          // arrays go through Collection routing
    [InlineData("MainGame.PlayerState[]")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParseClassName_Rejects(string? type)
    {
        Assert.False(InstanceFieldClassifier.TryParseClassName(type, out _, out _));
    }
}
