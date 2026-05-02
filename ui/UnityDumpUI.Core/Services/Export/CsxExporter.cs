// CsxExporter.cs — emits a Cheat Engine Structure Dissect XML
// (.csx-importable) describing the layout of a Unity managed class.
//
// Wire shape: a single <Structures><Structure>...</Structure></Structures>
// document. CE expects each <Element ... /> to carry **both** an `Offset`
// (decimal) AND `OffsetHex` (8-digit zero-padded uppercase) attribute — if
// either is missing CE's parser raises `EConvertError: '' is an invalid
// integer` while loading. `DisplayMethod` is also required (CE defaults to
// "unsigned integer" but the attribute itself must be present).
//
// We deliberately omit the `<?xml ?>` prolog and any <!-- comments -->: CE's
// stock "Save structure" output has neither, and stray nodes have triggered
// load failures in the wild.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace UnityDumpUI.Core.Services.Export;

public static class CsxExporter
{
    /// <summary>
    /// Render a CE Structure Dissect XML for <paramref name="self"/>.
    /// <paramref name="ancestors"/> (oldest-first) inserts parent fields
    /// before declared ones, mirroring SdkHeaderExporter.
    /// </summary>
    public static string Render(GetClassInfoResult self,
                                IReadOnlyList<GetClassInfoResult>? ancestors = null,
                                string? assemblyName = null)
    {
        string ns   = self.Namespace ?? string.Empty;
        string nm   = self.Name      ?? "Unknown";
        string full = string.IsNullOrEmpty(ns) ? nm : $"{ns}.{nm}";
        string structName = string.IsNullOrEmpty(assemblyName) ? full : $"{assemblyName}!{full}";

        // CE caps total struct size by the max offset; we iterate to compute it.
        int totalSize = 0;
        var elements = new List<(int offset, string vartype, int bytesize, string desc)>();

        if (ancestors is { Count: > 0 })
        {
            foreach (GetClassInfoResult anc in ancestors)
                CollectFields(anc, elements, ref totalSize, isInherited: true);
        }
        CollectFields(self, elements, ref totalSize, isInherited: false);

        var sb = new StringBuilder(2048);
        sb.Append("<Structures>\n");
        sb.Append("  <Structure Name=\"").Append(XmlEscape(structName))
          .Append("\" AutoFill=\"0\" AutoCreate=\"1\" DefaultHex=\"0\" AutoDestroy=\"0\" DoNotSaveLocal=\"0\" RLECompression=\"1\" AutoCreateStructsize=\"")
          .Append(totalSize.ToString(CultureInfo.InvariantCulture))
          .Append("\">\n");
        sb.Append("    <Elements>\n");
        foreach (var (offset, vartype, bytesize, desc) in elements)
        {
            sb.Append("      <Element Offset=\"")
              .Append(offset.ToString(CultureInfo.InvariantCulture))
              .Append("\" Vartype=\"").Append(XmlEscape(vartype))
              .Append("\" Bytesize=\"").Append(bytesize.ToString(CultureInfo.InvariantCulture))
              .Append("\" OffsetHex=\"").Append(offset.ToString("X8", CultureInfo.InvariantCulture))
              .Append("\" Description=\"").Append(XmlEscape(desc))
              .Append("\" DisplayMethod=\"").Append(DisplayMethodFor(vartype))
              .Append("\"/>\n");
        }
        sb.Append("    </Elements>\n");
        sb.Append("  </Structure>\n");
        sb.Append("</Structures>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Map a CE Vartype to the matching <c>DisplayMethod</c> attribute. The
    /// attribute is required by CE's loader (missing it fails the parse with
    /// the same EConvertError we hit), so we always emit one.
    /// </summary>
    private static string DisplayMethodFor(string vartype) => vartype switch
    {
        "Float"   => "float",
        "Double"  => "double",
        "Pointer" => "hexadecimal",
        _         => "unsigned integer",
    };

    private static void CollectFields(GetClassInfoResult info,
                                      List<(int, string, int, string)> sink,
                                      ref int totalSize,
                                      bool isInherited)
    {
        if (info.Fields is not { Count: > 0 } fields) return;
        string declaredIn = string.IsNullOrEmpty(info.Namespace)
            ? (info.Name ?? "?")
            : $"{info.Namespace}.{info.Name}";

        foreach (FieldEntry f in fields.Where(x => !x.Static))
        {
            ManagedTypeShape shape = UnityTypeMap.Resolve(f.Type);
            string desc = isInherited
                ? $"{f.Name} : {f.Type ?? "?"}  (inherited from {declaredIn})"
                : $"{f.Name} : {f.Type ?? "?"}";
            sink.Add((f.Offset, shape.CeTypeKeyword, shape.SizeBytes, desc));
            int end = f.Offset + shape.SizeBytes;
            if (end > totalSize) totalSize = end;
        }
    }

    private static string XmlEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '<'  => "&lt;",
                '>'  => "&gt;",
                '&'  => "&amp;",
                '"'  => "&quot;",
                '\'' => "&apos;",
                _    => c.ToString(),
            });
        }
        return sb.ToString();
    }
}
