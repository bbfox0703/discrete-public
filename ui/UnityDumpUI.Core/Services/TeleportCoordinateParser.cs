// TeleportCoordinateParser.cs — Task 5 (Teleport wave 1). Copy / paste of
// world coordinates. Unity has no "BugItGo" clipboard convention (that's a UE
// CheatManager thing), so this is deliberately format-agnostic: it extracts
// every number from the pasted text — tolerating parentheses, commas, labels,
// and newlines — and interprets 3 numbers as a position, 6 as position +
// Euler rotation. Formatting emits Unity's familiar "(x, y, z)" shape so a
// copied pose round-trips (and a copied Vector3.ToString() pastes back).
//
// Pure + AOT-safe (source-generated regex; no reflection). Unit-tested.

using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UnityDumpUI.Core.Services;

/// <summary>Parsed world coordinates (position + optional Euler rotation).</summary>
public sealed class ParsedCoordinates
{
    public bool    Ok    { get; init; }
    public string? Error { get; init; }

    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }

    public bool   HasRotation { get; init; }
    public double EulerX { get; init; }
    public double EulerY { get; init; }
    public double EulerZ { get; init; }
}

/// <summary>
/// Parses and formats teleport coordinate strings. Parsing is tolerant: any
/// text containing exactly 3 numbers is a position; 6 numbers is a position +
/// Euler rotation; anything else is rejected. Formatting produces
/// <c>(x, y, z)</c> (Unity Vector3 style).
/// </summary>
public static partial class TeleportCoordinateParser
{
    // Matches an integer / decimal / scientific-notation number with an
    // optional leading sign. Requires at least one digit, so lone signs or
    // dots and label letters are skipped.
    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    public static ParsedCoordinates Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new ParsedCoordinates { Ok = false, Error = "empty input" };

        var nums = new List<double>(6);
        foreach (Match m in NumberRegex().Matches(input))
        {
            if (double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                nums.Add(d);
        }

        if (nums.Count == 3)
        {
            return new ParsedCoordinates { Ok = true, X = nums[0], Y = nums[1], Z = nums[2] };
        }
        if (nums.Count == 6)
        {
            return new ParsedCoordinates
            {
                Ok = true,
                X = nums[0], Y = nums[1], Z = nums[2],
                HasRotation = true,
                EulerX = nums[3], EulerY = nums[4], EulerZ = nums[5],
            };
        }

        return new ParsedCoordinates
        {
            Ok = false,
            Error = $"expected 3 (position) or 6 (position + rotation) numbers, found {nums.Count}",
        };
    }

    /// <summary>Format a position as <c>(x, y, z)</c>.</summary>
    public static string Format(double x, double y, double z) =>
        $"({F(x)}, {F(y)}, {F(z)})";

    /// <summary>Format a position + Euler rotation as <c>(x, y, z) (rx, ry, rz)</c>.</summary>
    public static string Format(double x, double y, double z, double rx, double ry, double rz) =>
        $"({F(x)}, {F(y)}, {F(z)}) ({F(rx)}, {F(ry)}, {F(rz)})";

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
