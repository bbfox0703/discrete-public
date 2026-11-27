// SdkHeaderExporter.cs — emits a compile-ready C/C++ header describing the
// memory layout of a Unity managed class. Mirror of docs/Architecture.md
// §"SDK Header (.h)".
//
// Layout guarantees (E-M7):
//   * Instance fields across ancestors + self are sorted by runtime offset
//     and padded from offset 0 (`uint8_t _pad_XXXX[0xNN]`), covering both the
//     managed object header (0x10 on x64 / 0x08 on x86) and inter-field gaps.
//   * Built-in Unity math value types (Vector2/3/4, Quaternion, Color,
//     Color32, Rect, Bounds) get guarded `#ifndef DISCRETE_UNITY_MATH_TYPES`
//     POD definitions whenever referenced so the header compiles standalone.
//   * When an IClassInfoLookup is supplied (RenderAsync), custom enums unwrap
//     to their underlying integer C type and custom ValueType structs emit
//     `uint8_t name[0xNN]` (or a comment when size is unknown).
//   * Duplicate member names across inheritance (or C++ keywords) are
//     disambiguated (`name_DeclaringClass` / `keyword_`).
//   * Every emitted member receives a compile-time
//     `static_assert(offsetof(Struct, member) == 0xNN, ...)` check.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Services.Export;

public static class SdkHeaderExporter
{
    private static readonly HashSet<string> UnityMathTypes = new(StringComparer.Ordinal)
    {
        "Vector2", "Vector3", "Vector4", "Quaternion",
        "Color", "Color32", "Rect", "Bounds",
    };

    private static readonly HashSet<string> CppKeywords = new(StringComparer.Ordinal)
    {
        "alignas", "alignof", "and", "and_eq", "asm", "auto", "bitand", "bitor",
        "bool", "break", "case", "catch", "char", "char8_t", "char16_t", "char32_t",
        "class", "compl", "concept", "const", "consteval", "constexpr", "constinit",
        "const_cast", "continue", "co_await", "co_return", "co_yield", "decltype",
        "default", "delete", "do", "double", "dynamic_cast", "else", "enum", "explicit",
        "export", "extern", "false", "float", "for", "friend", "goto", "if", "inline",
        "int", "long", "mutable", "namespace", "new", "noexcept", "not", "not_eq",
        "nullptr", "operator", "or", "or_eq", "private", "protected", "public",
        "register", "reinterpret_cast", "requires", "return", "short", "signed",
        "sizeof", "static", "static_assert", "static_cast", "struct", "switch",
        "template", "this", "thread_local", "throw", "true", "try", "typedef",
        "typeid", "typename", "union", "unsigned", "using", "virtual", "void",
        "volatile", "wchar_t", "while", "xor", "xor_eq",
    };

    private readonly record struct CollectedField(
        FieldEntry Field,
        string     DeclaringLabel,
        string     DeclaringShortName,
        bool       IsInherited);

    private readonly record struct ResolvedField(
        string? CType,            // null => comment-only (unknown value-type size)
        int     ByteSize,         // 0 when comment-only
        bool    IsByteArray,      // true => emit `uint8_t name[0xNN];`
        bool    UsesUnityMath,    // true => emit guarded Unity math structs
        string  TypeNote);        // trailing comment text

    /// <summary>
    /// Synchronously render an SDK header for <paramref name="self"/> without
    /// metadata lookup RPCs. <paramref name="ancestors"/> is optional, ordered
    /// oldest-first (root → … → immediate parent).
    /// </summary>
    public static string Render(GetClassInfoResult self,
                                IReadOnlyList<GetClassInfoResult>? ancestors = null,
                                string? assemblyName = null,
                                int pointerSize = 8)
        => RenderCoreAsync(self, ancestors, assemblyName, pointerSize, lookup: null, CancellationToken.None)
            .GetAwaiter().GetResult();

    /// <summary>
    /// Render an SDK header for <paramref name="self"/>, optionally resolving
    /// custom enum underlying types and inline ValueType struct sizes via
    /// <paramref name="lookup"/>. Honors <see cref="ExportOptions.PointerSize"/>.
    /// </summary>
    public static Task<string> RenderAsync(GetClassInfoResult self,
                                           IReadOnlyList<GetClassInfoResult>? ancestors = null,
                                           string? assemblyName = null,
                                           ExportOptions? options = null,
                                           IClassInfoLookup? lookup = null,
                                           CancellationToken ct = default)
        => RenderCoreAsync(self, ancestors, assemblyName, options?.PointerSize ?? 8, lookup, ct);

    private static async Task<string> RenderCoreAsync(
        GetClassInfoResult self,
        IReadOnlyList<GetClassInfoResult>? ancestors,
        string? assemblyName,
        int pointerSize,
        IClassInfoLookup? lookup,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(self);
        ct.ThrowIfCancellationRequested();

        int ptr = TemplateExportHelpers.NormalizePointerSize(pointerSize);

        string ns   = self.Namespace ?? string.Empty;
        string nm   = self.Name      ?? "Unknown";
        string full = string.IsNullOrEmpty(ns) ? nm : $"{ns}.{nm}";
        string structName = EscapeCppKeyword(SanitizeIdentifier(NestedToFlat(nm)));

        // Collect all non-static fields from ancestors (oldest-first) + self.
        var validFields   = new List<CollectedField>();
        var invalidFields = new List<CollectedField>();

        if (ancestors is { Count: > 0 })
        {
            foreach (GetClassInfoResult anc in ancestors)
                CollectFields(anc, isInherited: true, validFields, invalidFields);
        }
        CollectFields(self, isInherited: false, validFields, invalidFields);

        // Stable sort by runtime byte offset so padding + offsetof match memory.
        var sorted = validFields.OrderBy(x => x.Field.Offset).ToList();

        // Pre-resolve every field so we know before writing the struct header
        // whether Unity math POD definitions are needed.
        var resolved = new ResolvedField[sorted.Count];
        var visited  = new HashSet<string>(StringComparer.Ordinal);
        bool usesUnityMath = false;

        for (int i = 0; i < sorted.Count; ++i)
        {
            ct.ThrowIfCancellationRequested();
            int nextDistinctOffset = FindNextDistinctOffset(sorted, i);
            resolved[i] = await ResolveFieldLayoutAsync(
                sorted[i].Field, nextDistinctOffset, assemblyName, ptr, lookup, visited, ct)
                .ConfigureAwait(false);
            if (resolved[i].UsesUnityMath)
                usesUnityMath = true;
        }

        // Emit struct body + collect offsetof assertions.
        var body       = new StringBuilder(2048);
        var usedNames  = new HashSet<string>(StringComparer.Ordinal);
        var assertions = new List<(string Member, int Offset)>(sorted.Count);
        int cursor     = 0;
        string? lastDeclaring = null;
        bool hasInherited     = ancestors is { Count: > 0 };

        for (int i = 0; i < sorted.Count; ++i)
        {
            CollectedField cf = sorted[i];
            ResolvedField  rf = resolved[i];
            FieldEntry     f  = cf.Field;

            // Overlapping field (e.g. [StructLayout(Explicit)] union or packed overlap):
            // keep as a comment so subsequent members still land at exact offsets.
            if (f.Offset < cursor)
            {
                body.Append("    // +0x")
                    .Append(f.Offset.ToString("X", CultureInfo.InvariantCulture))
                    .Append("  [overlaps prior member ending at +0x")
                    .Append(cursor.ToString("X", CultureInfo.InvariantCulture))
                    .Append("] ")
                    .Append(string.IsNullOrEmpty(f.Type) ? "?" : f.Type)
                    .Append(' ')
                    .Append(f.Name)
                    .Append('\n');
                continue;
            }

            // Pad from current cursor up to f.Offset (covers object header at +0x00 and gaps).
            if (f.Offset > cursor)
            {
                int gap = f.Offset - cursor;
                string padName = AllocateMemberName($"_pad_{cursor:X4}", null, cursor, usedNames);
                string padNote = cursor == 0 ? "object header / padding" : "padding";
                body.Append("    uint8_t ").Append(padName)
                    .Append("[0x").Append(gap.ToString("X", CultureInfo.InvariantCulture))
                    .Append("];    // +0x").Append(cursor.ToString("X", CultureInfo.InvariantCulture))
                    .Append("  ").Append(padNote).Append('\n');
                cursor = f.Offset;
            }

            if (hasInherited && !string.Equals(lastDeclaring, cf.DeclaringLabel, StringComparison.Ordinal))
            {
                if (lastDeclaring is not null)
                    body.Append('\n');
                if (cf.IsInherited)
                    body.Append("    /* inherited from ").Append(cf.DeclaringLabel).Append(" */\n");
                else
                    body.Append("    /* declared on ").Append(cf.DeclaringLabel).Append(" */\n");
                lastDeclaring = cf.DeclaringLabel;
            }

            // Unknown value-type size: emit comment only and let subsequent padding advance cursor.
            if (rf.CType is null || rf.ByteSize <= 0)
            {
                body.Append("    // +0x")
                    .Append(f.Offset.ToString("X", CultureInfo.InvariantCulture))
                    .Append("  ").Append(f.Name)
                    .Append("  (").Append(rf.TypeNote).Append(")\n");
                continue;
            }

            string rawName    = EscapeCppKeyword(SanitizeIdentifier(f.Name));
            string memberName = AllocateMemberName(rawName, cf.DeclaringShortName, f.Offset, usedNames);

            if (rf.IsByteArray)
            {
                body.Append("    uint8_t ").Append(memberName)
                    .Append("[0x").Append(rf.ByteSize.ToString("X", CultureInfo.InvariantCulture))
                    .Append("];");
            }
            else
            {
                body.Append("    ").Append(rf.CType).Append(' ').Append(memberName).Append(';');
            }

            body.Append("    // +0x").Append(f.Offset.ToString("X", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(rf.TypeNote))
                body.Append("  ").Append(rf.TypeNote);
            body.Append('\n');

            assertions.Add((memberName, f.Offset));
            cursor += rf.ByteSize;
        }

        if (invalidFields.Count > 0)
        {
            if (body.Length > 0) body.Append('\n');
            body.Append("    /* skipped fields with non-instance / invalid offsets */\n");
            foreach (CollectedField cf in invalidFields)
            {
                body.Append("    // ")
                    .Append(string.IsNullOrEmpty(cf.Field.Type) ? "?" : cf.Field.Type)
                    .Append(' ').Append(cf.Field.Name)
                    .Append(" (offset=").Append(cf.Field.Offset.ToString(CultureInfo.InvariantCulture))
                    .Append(")\n");
            }
        }

        // Assemble final header.
        var sb = new StringBuilder(body.Length + 1024);
        sb.Append("// Generated by Discrete — SDK header\n");
        if (!string.IsNullOrEmpty(assemblyName))
            sb.Append("// Assembly: ").Append(assemblyName).Append('\n');
        sb.Append("// Class:    ").Append(full).Append('\n');
        if (self.Parent is { Name: { Length: > 0 } pn })
        {
            string pns = self.Parent.Namespace ?? string.Empty;
            sb.Append("// Parent:   ").Append(string.IsNullOrEmpty(pns) ? pn : $"{pns}.{pn}").Append('\n');
        }
        sb.Append("// Arch:     ").Append(ptr == 4 ? "x86 (4-byte pointers)" : "x64 (8-byte pointers)").Append('\n');
        sb.Append("// Note:     offsets reflect the instance layout reported by the runtime.\n");
        sb.Append("//           Static / literal / thread-static fields are omitted from the struct body.\n\n");

        sb.Append("#pragma once\n");
        sb.Append("#include <cstddef>\n");
        sb.Append("#include <cstdint>\n\n");

        if (usesUnityMath)
            EmitUnityMathDefinitions(sb);

        sb.Append("#pragma pack(push, 1)\n");
        sb.Append("struct ").Append(structName).Append(" {\n");
        sb.Append(body);
        sb.Append("};\n");
        sb.Append("#pragma pack(pop)\n");

        if (assertions.Count > 0)
        {
            sb.Append('\n');
            foreach ((string member, int offset) in assertions)
            {
                string hex = offset.ToString("X", CultureInfo.InvariantCulture);
                sb.Append("static_assert(offsetof(").Append(structName).Append(", ").Append(member)
                  .Append(") == 0x").Append(hex)
                  .Append(", \"").Append(structName).Append("::").Append(member)
                  .Append(" offset mismatch\");\n");
            }
        }

        return sb.ToString();
    }

    private static void CollectFields(
        GetClassInfoResult info,
        bool isInherited,
        List<CollectedField> valid,
        List<CollectedField> invalid)
    {
        if (info.Fields is not { Count: > 0 } fields) return;
        string shortName = string.IsNullOrEmpty(info.Name) ? "Base" : info.Name!;
        string label     = string.IsNullOrEmpty(info.Namespace) ? shortName : $"{info.Namespace}.{shortName}";

        foreach (FieldEntry f in fields)
        {
            if (f.Static) continue;
            var cf = new CollectedField(f, label, shortName, isInherited);
            if (f.Offset < 0)
                invalid.Add(cf);
            else
                valid.Add(cf);
        }
    }

    private static int FindNextDistinctOffset(List<CollectedField> sorted, int index)
    {
        int cur = sorted[index].Field.Offset;
        for (int j = index + 1; j < sorted.Count; ++j)
        {
            if (sorted[j].Field.Offset > cur)
                return sorted[j].Field.Offset;
        }
        return -1;
    }

    private static async Task<ResolvedField> ResolveFieldLayoutAsync(
        FieldEntry f,
        int nextDistinctOffset,
        string? assemblyName,
        int ptr,
        IClassInfoLookup? lookup,
        HashSet<string> visited,
        CancellationToken ct)
    {
        string typeText = f.Type ?? string.Empty;
        ManagedTypeShape shape = UnityTypeMap.Resolve(typeText, ptr);
        string ptrType  = ptr == 4 ? "uint32_t" : "void*";
        string ptrSuffix = ptr == 4 ? " (x86 ptr)" : string.Empty;

        // 1. Built-in value type (primitive, IntPtr/UIntPtr, Unity math struct).
        if (!shape.IsPointer)
        {
            if (TemplateExportHelpers.IsNativeIntType(typeText))
            {
                string nativeType = typeText == "System.IntPtr"
                    ? (ptr == 4 ? "int32_t"  : "int64_t")
                    : (ptr == 4 ? "uint32_t" : "uint64_t");
                return new ResolvedField(nativeType, ptr, IsByteArray: false, UsesUnityMath: false, typeText);
            }

            bool isMath = UnityMathTypes.Contains(shape.CHeaderType);
            return new ResolvedField(shape.CHeaderType, shape.SizeBytes, IsByteArray: false, UsesUnityMath: isMath, typeText);
        }

        // 2. Known reference/pointer types (blank, array T[], String, BCL collections).
        if (IsKnownManagedPointerType(typeText))
        {
            string note = string.IsNullOrEmpty(typeText) ? "pointer" : typeText + ptrSuffix;
            return new ResolvedField(ptrType, ptr, IsByteArray: false, UsesUnityMath: false, note);
        }

        // 3. Unknown type — resolve via metadata lookup when available so enums
        //    and custom ValueType structs get their true C type / byte width.
        if (lookup is not null
            && InstanceFieldClassifier.TryParseClassName(typeText, out string cns, out string cname))
        {
            GetClassInfoResult? sub = await TemplateExportHelpers.TryLookupClassAsync(
                lookup, assemblyName, cns, cname, ct).ConfigureAwait(false);

            if (sub is not null)
            {
                if (TemplateExportHelpers.IsEnumClass(sub))
                {
                    string underlying = TemplateExportHelpers.ResolveEnumUnderlyingType(sub);
                    ManagedTypeShape us = UnityTypeMap.Resolve(underlying, ptr);
                    return new ResolvedField(
                        us.CHeaderType, us.SizeBytes,
                        IsByteArray: false, UsesUnityMath: false,
                        $"{typeText} (enum : {underlying})");
                }

                if (TemplateExportHelpers.IsValueTypeClass(sub))
                {
                    visited.Clear();
                    int sz = await TemplateExportHelpers.ComputeInlineStructSizeAsync(
                        sub, assemblyName, ptr, lookup, visited, ct).ConfigureAwait(false);
                    int avail = nextDistinctOffset > f.Offset ? nextDistinctOffset - f.Offset : -1;
                    if (sz > 0 && (avail < 0 || sz <= avail))
                    {
                        return new ResolvedField(
                            "uint8_t", sz,
                            IsByteArray: true, UsesUnityMath: false,
                            $"{typeText} (value type, {sz}B)");
                    }

                    // Size could not be determined cleanly (or conflicts with next offset) —
                    // emit a comment and let padding bridge to the next known offset.
                    return new ResolvedField(
                        CType: null, ByteSize: 0,
                        IsByteArray: false, UsesUnityMath: false,
                        $"{typeText}, value type — unknown size; omitted");
                }

                // Confirmed reference class.
                return new ResolvedField(ptrType, ptr, IsByteArray: false, UsesUnityMath: false, typeText + ptrSuffix);
            }
        }

        // 4. Offline / unresolved fallback: if the next field starts sooner than
        //    a pointer width, this slot CANNOT be a pointer (it's an enum or small
        //    struct) — emit an opaque byte array of the exact gap width so the next
        //    member stays aligned to its runtime offset.
        int gapToNext = nextDistinctOffset > f.Offset ? nextDistinctOffset - f.Offset : -1;
        if (gapToNext > 0 && gapToNext < ptr)
        {
            return new ResolvedField(
                "uint8_t", gapToNext,
                IsByteArray: true, UsesUnityMath: false,
                $"{typeText} (value type / enum, inferred {gapToNext}B)");
        }

        return new ResolvedField(ptrType, ptr, IsByteArray: false, UsesUnityMath: false, typeText + ptrSuffix);
    }

    private static bool IsKnownManagedPointerType(string typeText)
    {
        if (string.IsNullOrWhiteSpace(typeText)) return true;
        if (string.Equals(typeText, "System.String", StringComparison.Ordinal)) return true;
        if (string.Equals(typeText, "System.Object", StringComparison.Ordinal)) return true;
        if (InstanceFieldClassifier.TryParseCollectionType(typeText, out _, out _)) return true;
        if (TemplateExportHelpers.IsNonArrayCollectionType(typeText)) return true;

        int lt = typeText.IndexOf('<');
        string root = lt > 0 ? typeText.Substring(0, lt) : typeText;
        return root.EndsWith("[]", StringComparison.Ordinal);
    }

    private static void EmitUnityMathDefinitions(StringBuilder sb)
    {
        sb.Append("#ifndef DISCRETE_UNITY_MATH_TYPES\n");
        sb.Append("#define DISCRETE_UNITY_MATH_TYPES\n");
        sb.Append("#pragma pack(push, 1)\n");
        sb.Append("struct Vector2    { float x, y; };\n");
        sb.Append("struct Vector3    { float x, y, z; };\n");
        sb.Append("struct Vector4    { float x, y, z, w; };\n");
        sb.Append("struct Quaternion { float x, y, z, w; };\n");
        sb.Append("struct Color      { float r, g, b, a; };\n");
        sb.Append("struct Color32    { uint8_t r, g, b, a; };\n");
        sb.Append("struct Rect       { float x, y, width, height; };\n");
        sb.Append("struct Bounds     { Vector3 center; Vector3 extents; };\n");
        sb.Append("#pragma pack(pop)\n");
        sb.Append("#endif // DISCRETE_UNITY_MATH_TYPES\n\n");
    }

    private static string AllocateMemberName(
        string baseName,
        string? declaringShortName,
        int offset,
        HashSet<string> usedNames)
    {
        if (usedNames.Add(baseName))
            return baseName;

        if (!string.IsNullOrEmpty(declaringShortName))
        {
            string qualified = $"{baseName}_{SanitizeIdentifier(NestedToFlat(declaringShortName))}";
            if (usedNames.Add(qualified))
                return qualified;
            baseName = qualified;
        }

        string withOffset = $"{baseName}_0x{offset.ToString("X", CultureInfo.InvariantCulture)}";
        if (usedNames.Add(withOffset))
            return withOffset;

        for (int n = 2; ; ++n)
        {
            string numbered = $"{withOffset}_{n.ToString(CultureInfo.InvariantCulture)}";
            if (usedNames.Add(numbered))
                return numbered;
        }
    }

    private static string EscapeCppKeyword(string ident)
        => CppKeywords.Contains(ident) ? ident + "_" : ident;

    /// <summary>
    /// Replace characters that aren't valid in a C identifier with underscores;
    /// prefix a leading digit with `_`. Nested-type names like `Outer/Inner`
    /// become `Outer_Inner` — see <see cref="NestedToFlat"/>.
    /// </summary>
    internal static string SanitizeIdentifier(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "_";
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; ++i)
        {
            char c = raw[i];
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                   || (c >= '0' && c <= '9') || c == '_';
            sb.Append(ok ? c : '_');
        }
        if (sb[0] >= '0' && sb[0] <= '9') sb.Insert(0, '_');
        return sb.ToString();
    }

    /// <summary>Outer/Inner → Outer_Inner for use in C identifiers.</summary>
    internal static string NestedToFlat(string raw)
        => string.IsNullOrEmpty(raw) ? raw : raw.Replace('/', '_');
}
