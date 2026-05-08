// HighlightSegmenterTests.cs — offline regression coverage for the
// substring-segmentation logic that backs the Inspector / Browser filter
// highlight UX (HighlightedTextBlock).

using UnityDumpUI.Core.Services;
using Xunit;

namespace UnityDumpUI.Tests;

public class HighlightSegmenterTests
{
    [Fact]
    public void Build_EmptyFilter_ReturnsSingleNonMatchSegment()
    {
        var segs = HighlightSegmenter.Build("UnityEngine.Transform", string.Empty);
        Assert.Single(segs);
        Assert.False(segs[0].IsMatch);
        Assert.Equal("UnityEngine.Transform", segs[0].Text);
    }

    [Fact]
    public void Build_NullFilter_ReturnsSingleNonMatchSegment()
    {
        var segs = HighlightSegmenter.Build("UnityEngine.Transform", null);
        Assert.Single(segs);
        Assert.False(segs[0].IsMatch);
        Assert.Equal("UnityEngine.Transform", segs[0].Text);
    }

    [Fact]
    public void Build_EmptySource_ReturnsSingleEmptyNonMatchSegment()
    {
        var segs = HighlightSegmenter.Build(string.Empty, "anything");
        Assert.Single(segs);
        Assert.False(segs[0].IsMatch);
        Assert.Equal(string.Empty, segs[0].Text);
    }

    [Fact]
    public void Build_NullSource_TreatedAsEmpty()
    {
        var segs = HighlightSegmenter.Build(null, "anything");
        Assert.Single(segs);
        Assert.Equal(string.Empty, segs[0].Text);
    }

    [Fact]
    public void Build_FilterNotFound_ReturnsSingleNonMatchSegment()
    {
        var segs = HighlightSegmenter.Build("UnityEngine.Transform", "Mono");
        Assert.Single(segs);
        Assert.False(segs[0].IsMatch);
        Assert.Equal("UnityEngine.Transform", segs[0].Text);
    }

    [Fact]
    public void Build_FilterAtStart_TwoSegments()
    {
        var segs = HighlightSegmenter.Build("UnityEngine.Transform", "Unity");
        Assert.Equal(2, segs.Count);
        Assert.True(segs[0].IsMatch);
        Assert.Equal("Unity", segs[0].Text);
        Assert.False(segs[1].IsMatch);
        Assert.Equal("Engine.Transform", segs[1].Text);
    }

    [Fact]
    public void Build_FilterAtEnd_TwoSegments()
    {
        var segs = HighlightSegmenter.Build("UnityEngine.Transform", "form");
        Assert.Equal(2, segs.Count);
        Assert.False(segs[0].IsMatch);
        Assert.Equal("UnityEngine.Trans", segs[0].Text);
        Assert.True(segs[1].IsMatch);
        Assert.Equal("form", segs[1].Text);
    }

    [Fact]
    public void Build_FilterInMiddle_ThreeSegments()
    {
        var segs = HighlightSegmenter.Build("UnityEngine.Transform", "Engine");
        Assert.Equal(3, segs.Count);
        Assert.False(segs[0].IsMatch); Assert.Equal("Unity", segs[0].Text);
        Assert.True(segs[1].IsMatch);  Assert.Equal("Engine", segs[1].Text);
        Assert.False(segs[2].IsMatch); Assert.Equal(".Transform", segs[2].Text);
    }

    [Fact]
    public void Build_FilterCaseInsensitive_Matches()
    {
        // Filter case is normalised via OrdinalIgnoreCase. The captured segment
        // preserves the *source* casing — so a "engine" filter highlights
        // "Engine" with its original capitalisation, which is what the user
        // wants to see in the row.
        var segs = HighlightSegmenter.Build("UnityEngine.Transform", "engine");
        Assert.Equal(3, segs.Count);
        Assert.True(segs[1].IsMatch);
        Assert.Equal("Engine", segs[1].Text);  // source casing preserved
    }

    [Fact]
    public void Build_FilterEqualsSource_SingleMatchSegment()
    {
        var segs = HighlightSegmenter.Build("Hello", "hello");
        Assert.Single(segs);
        Assert.True(segs[0].IsMatch);
        Assert.Equal("Hello", segs[0].Text);
    }

    [Fact]
    public void Build_MultipleOccurrences_AlternatingSegments()
    {
        // "aXXabXXc" with filter "XX" → ["a", "XX", "ab", "XX", "c"]
        var segs = HighlightSegmenter.Build("aXXabXXc", "XX");
        Assert.Equal(5, segs.Count);
        Assert.False(segs[0].IsMatch); Assert.Equal("a",  segs[0].Text);
        Assert.True(segs[1].IsMatch);  Assert.Equal("XX", segs[1].Text);
        Assert.False(segs[2].IsMatch); Assert.Equal("ab", segs[2].Text);
        Assert.True(segs[3].IsMatch);  Assert.Equal("XX", segs[3].Text);
        Assert.False(segs[4].IsMatch); Assert.Equal("c",  segs[4].Text);
    }

    [Fact]
    public void Build_AdjacentMatches_TwoMatchSegmentsBackToBack()
    {
        // "abab" + filter "ab" → two adjacent match segments, no
        // non-match in between.
        var segs = HighlightSegmenter.Build("abab", "ab");
        Assert.Equal(2, segs.Count);
        Assert.True(segs[0].IsMatch); Assert.Equal("ab", segs[0].Text);
        Assert.True(segs[1].IsMatch); Assert.Equal("ab", segs[1].Text);
    }

    [Fact]
    public void Build_FilterLongerThanSource_NoMatch()
    {
        var segs = HighlightSegmenter.Build("ab", "abcde");
        Assert.Single(segs);
        Assert.False(segs[0].IsMatch);
        Assert.Equal("ab", segs[0].Text);
    }

    [Fact]
    public void Build_OverlappingPatternsResolveLeftToRight()
    {
        // Source "ababa" + filter "aba" — first hit consumes characters 0-2,
        // next search starts at index 3 where "ba" remains and doesn't match.
        // No greedy / overlapping behaviour.
        var segs = HighlightSegmenter.Build("ababa", "aba");
        Assert.Equal(2, segs.Count);
        Assert.True(segs[0].IsMatch);  Assert.Equal("aba", segs[0].Text);
        Assert.False(segs[1].IsMatch); Assert.Equal("ba",  segs[1].Text);
    }
}
