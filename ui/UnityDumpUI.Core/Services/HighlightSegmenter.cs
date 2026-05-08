// HighlightSegmenter.cs — split a string into matching / non-matching runs
// against a substring filter (case-insensitive). Pure logic, lifted out of
// UnityDumpUI's HighlightedTextBlock control so the segmentation can be
// unit-tested without spinning up an Avalonia visual tree.

using System;
using System.Collections.Generic;

namespace UnityDumpUI.Core.Services;

/// <summary>
/// Pure splitter for the substring-highlight UX.
/// </summary>
public static class HighlightSegmenter
{
    /// <summary>
    /// One contiguous run inside the source string. <see cref="IsMatch"/>
    /// is true when the run came from a hit on the filter substring;
    /// false otherwise.
    /// </summary>
    public readonly record struct Segment(string Text, bool IsMatch);

    /// <summary>
    /// Split <paramref name="source"/> into runs of matching / non-matching
    /// text against <paramref name="filter"/> using
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>. Empty / null filter
    /// returns a single non-match segment containing the entire source. Empty
    /// source returns a single empty non-match segment.
    /// </summary>
    public static IReadOnlyList<Segment> Build(string? source, string? filter)
    {
        string src = source ?? string.Empty;
        if (string.IsNullOrEmpty(filter))
            return new[] { new Segment(src, IsMatch: false) };
        if (src.Length == 0)
            return new[] { new Segment(string.Empty, IsMatch: false) };

        var segments = new List<Segment>();
        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
        int idx = 0;
        while (idx < src.Length)
        {
            int hit = src.IndexOf(filter, idx, cmp);
            if (hit < 0)
            {
                segments.Add(new Segment(src.Substring(idx), IsMatch: false));
                break;
            }
            if (hit > idx)
                segments.Add(new Segment(src.Substring(idx, hit - idx), IsMatch: false));
            segments.Add(new Segment(src.Substring(hit, filter.Length), IsMatch: true));
            idx = hit + filter.Length;
        }
        return segments;
    }
}
