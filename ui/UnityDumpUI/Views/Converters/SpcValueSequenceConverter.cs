// SpcValueSequenceConverter — Phase 29d-2.
//
// Renders a SpcMatch's per-snapshot value array as "v0 → v1 → v2"
// for the SPC Query results DataGrid. AOT-friendly: no reflection,
// no LINQ-internal allocation surprises.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia.Data.Converters;

namespace UnityDumpUI.Views.Converters;

public sealed class SpcValueSequenceConverter : IValueConverter
{
    public static readonly SpcValueSequenceConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyList<string> seq || seq.Count == 0)
            return string.Empty;

        var sb = new StringBuilder(seq.Count * 12);
        for (int i = 0; i < seq.Count; i++)
        {
            if (i > 0) sb.Append(" → ");
            sb.Append(seq[i]);
        }
        return sb.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
