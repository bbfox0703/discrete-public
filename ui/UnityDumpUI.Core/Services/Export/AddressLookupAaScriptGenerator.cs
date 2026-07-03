// AddressLookupAaScriptGenerator.cs — Phase 22e.
//
// Wraps a WhatIsAtResult into payloads for the AOBMaker plugin's
// CreateAAScriptWithRecords command (Phase 22a). Pushed via
// IAobMakerBridge.CreateAAScriptWithRecordsAsync — the user clicks
// "Send to CE Table" on a successful Address-Lookup result, the parent
// AA script registers the instance pointer as a CE symbol, and child
// memory records expose the resolved field at its offset.
//
// What lands in CE's address list:
//
//   parent: "Discrete: PLY_WORK at 0x000002201761E3D0"  (vtAutoAssembler)
//     [ENABLE]
//       {$lua} guard: aborts if discrete_bridgeLoaded is false (the
//                     parent's AA itself works without the bridge, but
//                     the bootstrap presence is a useful "did the user
//                     actually set this up?" check).
//       {$asm}  registersymbol(DiscreteAt_PLY_WORK_2201761E3D0)
//                DiscreteAt_PLY_WORK_2201761E3D0:
//                dq 2201761E3D0
//     [DISABLE]
//       {$asm}  unregistersymbol(DiscreteAt_PLY_WORK_2201761E3D0)
//
//   child: "HP @+0x10  (System.Int32, 4 bytes)"
//     address = "DiscreteAt_PLY_WORK_2201761E3D0"
//     offsets = [0x10]
//     valueType = Dword (mapped from System.Int32)
//
// The "address as a registered symbol + offsets[]" pattern is what
// CreateAAScriptWithRecords' offsets array represents: each offset is
// a pointer dereference applied left-to-right. With one offset and a
// non-pointer value type CE displays the field at <symbol>+offset.
//
// Why a fabricated symbol (DiscreteAt_<Cls>_<hex>): bare hex addresses
// in CE memory records get treated as immediate, but the "Address" cell
// also accepts symbol names. Wiring through a registersymbol means
// (a) the user can rename the symbol if they want, (b) future reloads
// of the CT can re-resolve it via a different scan, and (c) the child
// records' "Address" cell renders as a readable name instead of a raw
// 64-bit hex value.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityDumpUI.Core.Services.Export;

/// <summary>
/// Inputs to <see cref="AddressLookupAaScriptGenerator.Build"/>. Mirrors
/// the subset of <c>WhatIsAtResult</c> fields the generator actually
/// uses — kept as a separate type so the generator stays in Core and
/// doesn't take a dependency on the wider ProtocolModels.cs surface.
/// </summary>
public sealed class AddressLookupRequest
{
    public required string  Instance     { get; init; }   // hex pointer ("0x...")
    public required string  ClassName    { get; init; }   // CLR class name
    public          string? Namespace    { get; init; }
    public          string? Assembly     { get; init; }
    /// <summary>Resolved field at the queried offset, when one exists.</summary>
    public          AddressLookupField? Field         { get; init; }
    /// <summary>Closest field below the queried offset (when no exact match).</summary>
    public          AddressLookupField? NearestField  { get; init; }
}

public sealed class AddressLookupField
{
    public required string  Name        { get; init; }
    public required string  Type        { get; init; }   // CLR type name, e.g. "System.Int32"
    public          int     Offset      { get; init; }   // bytes from instance base
    public          int     Size        { get; init; }
    public          bool    IsEnum      { get; init; }
    public          string? UnderlyingType { get; init; } // for enums
}

/// <summary>
/// What <see cref="AddressLookupAaScriptGenerator.Build"/> returns. Hand
/// the <see cref="ParentDescription"/> + <see cref="ParentScript"/> +
/// <see cref="ChildRecords"/> straight into
/// <c>IAobMakerBridge.CreateAAScriptWithRecordsAsync</c>.
/// </summary>
public sealed class AddressLookupAaPayload
{
    public required string                            ParentDescription { get; init; }
    public required string                            ParentScript      { get; init; }
    public required IReadOnlyList<AaScriptChildRecord> ChildRecords      { get; init; }
}

public static class AddressLookupAaScriptGenerator
{
    public static AddressLookupAaPayload Build(AddressLookupRequest req)
    {
        if (req is null)                            throw new ArgumentNullException(nameof(req));
        if (string.IsNullOrEmpty(req.Instance))     throw new ArgumentException("Instance required",  nameof(req));
        if (string.IsNullOrEmpty(req.ClassName))    throw new ArgumentException("ClassName required", nameof(req));

        // Normalise the address: strip "0x", uppercase. CE Auto-Assembler's
        // `dq <hex>` and registersymbol both want bare hex; the formatted
        // symbol name uses the same form.
        string bareHex = StripHexPrefix(req.Instance).Trim().ToUpperInvariant();
        string symbol  = $"DiscreteAt_{Sanitize(req.ClassName)}_{bareHex}";

        // ----- Parent AA script ----------------------------------------
        var sb = new StringBuilder(512);
        sb.AppendLine("[ENABLE]");
        sb.AppendLine("{$lua}");
        sb.AppendLine("if syntaxcheck then return end");
        // Reset the shared did-anything-fail flag so the close-tail judges THIS
        // run (docs/lua-logging.md).
        AaLuaLoader.AppendHadErrorReset(sb);
        sb.AppendLine("if not _G.discrete_bridgeLoaded then");
        sb.AppendLine("  _G.DISCRETE_HAD_ERROR = true");
        sb.AppendLine("  print('[Discrete] Enable \"Load Discrete Bridge\" first')");
        sb.AppendLine("  return");
        sb.AppendLine("end");
        // Close CE's Lua Engine here on the clean path: the following {$asm}
        // alloc/registersymbol prints nothing, and CE runs {$lua} before {$asm},
        // so a quiet enable ends with the window closed. (docs/lua-logging.md)
        AaLuaLoader.AppendCloseLuaEngineTail(sb);
        sb.AppendLine();
        sb.AppendLine("{$asm}");
        sb.Append("alloc(").Append(symbol).AppendLine(", 8)");
        sb.Append("registersymbol(").Append(symbol).AppendLine(")");
        sb.Append(symbol).AppendLine(":");
        sb.Append("dq 0x").AppendLine(bareHex);
        sb.AppendLine();
        sb.AppendLine("[DISABLE]");
        sb.AppendLine("{$asm}");
        sb.Append("unregistersymbol(").Append(symbol).AppendLine(")");
        sb.Append("dealloc(").Append(symbol).AppendLine(")");

        // ----- Child records -------------------------------------------
        var children = new List<AaScriptChildRecord>(2);
        if (req.Field is { } f)
        {
            children.Add(BuildChild(symbol, f, exactMatch: true));
        }
        else if (req.NearestField is { } nf)
        {
            // No exact field at the queried offset; expose the nearest
            // one below as a hint with a clear "delta from query" label.
            children.Add(BuildChild(symbol, nf, exactMatch: false));
        }

        // ----- Description --------------------------------------------
        string ns = req.Namespace ?? string.Empty;
        string display = string.IsNullOrEmpty(ns)
            ? req.ClassName
            : $"{ns}.{req.ClassName}";
        string parentDesc = $"Discrete: {display} @ 0x{bareHex}";

        return new AddressLookupAaPayload
        {
            ParentDescription = parentDesc,
            ParentScript      = sb.ToString(),
            ChildRecords      = children,
        };
    }

    // ---------- Internals -----------------------------------------------

    private static AaScriptChildRecord BuildChild(string parentSymbol,
                                                  AddressLookupField f,
                                                  bool exactMatch)
    {
        CeValueType ce = MapClrToCe(f.Type, f.IsEnum, f.UnderlyingType);
        bool signed    = IsSignedClrType(f.IsEnum && f.UnderlyingType is { } u ? u : f.Type);

        // Description carries the offset + size + CLR type so the user
        // can read the row in CE's grid without back-referencing.
        // Non-exact matches get a "(closest below)" tag so the user
        // knows the offset they queried wasn't a known field.
        string desc = $"{f.Name} @+0x{f.Offset:X}  ({f.Type}, {f.Size}B)";
        if (!exactMatch) desc += "  [closest below]";

        return new AaScriptChildRecord
        {
            Description = desc,
            Address     = parentSymbol,
            ValueType   = ce,
            IsSigned    = signed,
            Offsets     = new[] { f.Offset },
        };
    }

    /// <summary>
    /// Map a CLR primitive type name to CE's TVariableType. Falls back
    /// to <see cref="CeValueType.Qword"/> for anything we don't
    /// recognise — that's a 64-bit slot in CE which fits a managed
    /// pointer, so the user can re-classify in CE if it's wrong.
    /// </summary>
    internal static CeValueType MapClrToCe(string clrType, bool isEnum, string? underlying)
    {
        // Unwrap enums to their underlying integer type.
        string t = (isEnum && !string.IsNullOrEmpty(underlying)) ? underlying! : clrType;
        return t switch
        {
            "System.Boolean" => CeValueType.Byte,
            "System.Byte"    => CeValueType.Byte,
            "System.SByte"   => CeValueType.Byte,
            "System.Int16"   => CeValueType.Word,
            "System.UInt16"  => CeValueType.Word,
            "System.Char"    => CeValueType.Word,
            "System.Int32"   => CeValueType.Dword,
            "System.UInt32"  => CeValueType.Dword,
            "System.Int64"   => CeValueType.Qword,
            "System.UInt64"  => CeValueType.Qword,
            "System.IntPtr"  => CeValueType.Qword,
            "System.UIntPtr" => CeValueType.Qword,
            "System.Single"  => CeValueType.Single,
            "System.Double"  => CeValueType.Double,
            "System.String"  => CeValueType.UnicodeString,
            _                => CeValueType.Qword,
        };
    }

    private static bool IsSignedClrType(string t) => t switch
    {
        "System.SByte" or "System.Int16" or "System.Int32" or "System.Int64" => true,
        _ => false,
    };

    private static string StripHexPrefix(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s;
    }

    /// <summary>
    /// Replace any non-[A-Za-z0-9_] with '_'. CE Auto-Assembler symbols
    /// must match the C-identifier shape; class names with dots / slashes
    /// (nested types) would otherwise blow up the assemble step.
    /// </summary>
    internal static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "T";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.Length == 0 ? "T" : sb.ToString();
    }
}
