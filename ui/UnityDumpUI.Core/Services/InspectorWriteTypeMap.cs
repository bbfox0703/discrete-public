// InspectorWriteTypeMap.cs — Phase 21b. Pure-logic mapping from a CLR
// type name (the value-typed field's `Type` as reported by walkInstance)
// onto:
//   • the wire-type tag the setInstanceField RPC accepts,
//   • the editor flavor the Inspector should render (NumericUpDown vs
//     CheckBox vs TextBox vs AutoCompleteBox),
//   • the inclusive [Min, Max] range for sized-int editors.
//
// Mirror of pipe-protocol.md §"setInstanceField" and of Phase 19d-3's
// MethodCallEditorKind — keep the editor selection rules in sync with both
// when wire types change.
//
// Reference-type writes (System.String, pointer-to-class) are intentionally
// out of scope for v1: they require allocating a managed string / object
// and tracking the GC root, deferred to v2. This helper returns
// Supported=false for those so the UI can hide the Write button.

using System;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Per-row UI flavor for the field-write editor. Mirrors Phase 19d-3's
/// MethodCallEditorKind so the inspector and method-call paths render
/// equivalent inputs for equivalent types.
/// </summary>
public enum FieldWriteEditorKind
{
    /// <summary>NumericUpDown bound to a decimal value with min/max range — sized integers.</summary>
    Integer,
    /// <summary>TextBox accepting any double-parseable text — float / double (decimal can't represent doubles).</summary>
    Float,
    /// <summary>AutoCompleteBox with true/false suggestions — System.Boolean.</summary>
    Bool,
    /// <summary>TextBox accepting a hex address (0x... or bare hex) — IntPtr / UIntPtr.</summary>
    Address,
    /// <summary>TextBox accepting a single-character string — System.Char.</summary>
    Char,
}

/// <summary>
/// Routing payload for one CLR type. <see cref="Supported"/> is the gate the
/// UI uses to decide whether to expose the Write button at all.
/// </summary>
public sealed class FieldWriteTypeInfo
{
    public bool                  Supported   { get; init; }
    public string                WireType    { get; init; } = string.Empty;
    public FieldWriteEditorKind  EditorKind  { get; init; }
    /// <summary>Inclusive minimum for sized-int editors. Zero for unsigned.</summary>
    public decimal               MinValue    { get; init; }
    /// <summary>Inclusive maximum for sized-int editors.</summary>
    public decimal               MaxValue    { get; init; }
    /// <summary>Byte width of the underlying field — informational, used for status text.</summary>
    public int                   ByteSize    { get; init; }
    /// <summary>True when the editor takes an unsigned value (drives the default seed when current value can't be parsed).</summary>
    public bool                  IsUnsigned  { get; init; }
}

/// <summary>
/// Pure functions that classify a managed CLR type name into a
/// <see cref="FieldWriteTypeInfo"/>. No I/O, no VM state.
/// </summary>
public static class InspectorWriteTypeMap
{
    /// <summary>
    /// Resolve a field's CLR type name to its write info. Returns a record
    /// with <c>Supported=false</c> for any type that needs GC-root marshaling
    /// (strings, object pointers) or that has no fixed width on the wire.
    /// </summary>
    public static FieldWriteTypeInfo Resolve(string? clrTypeName)
    {
        string type = (clrTypeName ?? string.Empty).Trim();
        switch (type)
        {
            case "System.Boolean":
                return new FieldWriteTypeInfo
                {
                    Supported = true, WireType = "bool",
                    EditorKind = FieldWriteEditorKind.Bool,
                    ByteSize = 1,
                };
            case "System.SByte":
                return new FieldWriteTypeInfo
                {
                    Supported = true, WireType = "int8",
                    EditorKind = FieldWriteEditorKind.Integer,
                    MinValue = sbyte.MinValue, MaxValue = sbyte.MaxValue,
                    ByteSize = 1,
                };
            case "System.Byte":
                return new FieldWriteTypeInfo
                {
                    Supported = true, WireType = "uint8",
                    EditorKind = FieldWriteEditorKind.Integer,
                    MinValue = byte.MinValue, MaxValue = byte.MaxValue,
                    ByteSize = 1, IsUnsigned = true,
                };
            case "System.Int16":
                return new FieldWriteTypeInfo
                {
                    Supported = true, WireType = "int16",
                    EditorKind = FieldWriteEditorKind.Integer,
                    MinValue = short.MinValue, MaxValue = short.MaxValue,
                    ByteSize = 2,
                };
            case "System.UInt16":
                return new FieldWriteTypeInfo
                {
                    Supported = true, WireType = "uint16",
                    EditorKind = FieldWriteEditorKind.Integer,
                    MinValue = ushort.MinValue, MaxValue = ushort.MaxValue,
                    ByteSize = 2, IsUnsigned = true,
                };
            case "System.Char":
                return new FieldWriteTypeInfo
                {
                    Supported = true, WireType = "char",
                    EditorKind = FieldWriteEditorKind.Char,
                    MinValue = ushort.MinValue, MaxValue = ushort.MaxValue,
                    ByteSize = 2, IsUnsigned = true,
                };
            case "System.Int32":
                return new FieldWriteTypeInfo
                {
                    Supported = true, WireType = "int32",
                    EditorKind = FieldWriteEditorKind.Integer,
                    MinValue = int.MinValue, MaxValue = int.MaxValue,
                    ByteSize = 4,
                };
            case "System.UInt32":
                return new FieldWriteTypeInfo
                {
                    Supported = true, WireType = "uint32",
                    EditorKind = FieldWriteEditorKind.Integer,
                    MinValue = uint.MinValue, MaxValue = uint.MaxValue,
                    ByteSize = 4, IsUnsigned = true,
                };
            case "System.Int64":
                return new FieldWriteTypeInfo
                {
                    Supported = true, WireType = "int64",
                    EditorKind = FieldWriteEditorKind.Integer,
                    MinValue = long.MinValue, MaxValue = long.MaxValue,
                    ByteSize = 8,
                };
            case "System.UInt64":
                // NumericUpDown is decimal-backed and decimal can't represent
                // every value in [0, ulong.MaxValue] — values above
                // 79_228_162_514_264_337_593_543_950_335 (decimal max) clamp.
                // The DLL still does the final range check on the wire.
                return new FieldWriteTypeInfo
                {
                    Supported = true, WireType = "uint64",
                    EditorKind = FieldWriteEditorKind.Integer,
                    MinValue = 0m, MaxValue = decimal.MaxValue,
                    ByteSize = 8, IsUnsigned = true,
                };
            case "System.Single":
                return new FieldWriteTypeInfo
                {
                    Supported = true, WireType = "float",
                    EditorKind = FieldWriteEditorKind.Float,
                    ByteSize = 4,
                };
            case "System.Double":
                return new FieldWriteTypeInfo
                {
                    Supported = true, WireType = "double",
                    EditorKind = FieldWriteEditorKind.Float,
                    ByteSize = 8,
                };
            case "System.IntPtr":
            case "System.UIntPtr":
                return new FieldWriteTypeInfo
                {
                    Supported = true, WireType = "address",
                    EditorKind = FieldWriteEditorKind.Address,
                    ByteSize = 8, IsUnsigned = true,
                };
            default:
                // Anything else (System.String, T[], custom classes,
                // generic instantiations, value-struct types like Vector3)
                // is deferred to v2 — reference writes need GC-root
                // marshaling and struct-by-value needs a full struct
                // marshaler.
                return new FieldWriteTypeInfo { Supported = false };
        }
    }
}
