// ClassFacetFilterTests.cs — the reusable class-noise histogram: counts,
// tick-to-hide, hidden-set preserved across rebuilds, auto-hide, clear.

using System.Linq;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class ClassFacetFilterTests
{
    private static ClassFacet Facet(ClassFacetFilter f, string cls) =>
        f.Facets.First(x => x.ClassName == cls);

    [Fact]
    public void Rebuild_CountsAndOrdersByHitCount()
    {
        var f = new ClassFacetFilter();
        f.Rebuild(new[] { "Player", "Enemy", "Enemy", "Enemy", "Player" });

        Assert.True(f.AnyFacets);
        Assert.Equal("Enemy", f.Facets[0].ClassName);   // 3 > 2
        Assert.Equal(3, f.Facets[0].Count);
        Assert.Equal("Player", f.Facets[1].ClassName);
        Assert.Equal(2, f.Facets[1].Count);
    }

    [Fact]
    public void Tick_HidesClass_AndRaisesChanged()
    {
        var f = new ClassFacetFilter();
        f.Rebuild(new[] { "Player", "Enemy" });
        int changed = 0;
        f.Changed += () => changed++;

        Facet(f, "Enemy").IsHidden = true;

        Assert.True(f.IsHidden("Enemy"));
        Assert.False(f.IsHidden("Player"));
        Assert.True(f.AnyHidden);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Rebuild_PreservesHiddenSet()
    {
        var f = new ClassFacetFilter();
        f.Rebuild(new[] { "Player", "Enemy" });
        Facet(f, "Enemy").IsHidden = true;

        // Re-search returns a fresh (overlapping) result set.
        f.Rebuild(new[] { "Enemy", "Enemy", "Boss" });

        Assert.True(f.IsHidden("Enemy"));                 // exclusion survived
        Assert.True(Facet(f, "Enemy").IsHidden);          // reflected on the new facet
        Assert.False(Facet(f, "Boss").IsHidden);
    }

    [Fact]
    public void AutoHideNoise_TicksEngineNamespaces_Only()
    {
        var f = new ClassFacetFilter();
        f.Rebuild(new[] { "UnityEngine.Transform", "System.String", "Game.Player", "Assembly-CSharp" });
        f.AutoHideNoise();

        Assert.True(f.IsHidden("UnityEngine.Transform"));
        Assert.True(f.IsHidden("System.String"));
        Assert.False(f.IsHidden("Game.Player"));
    }

    [Fact]
    public void ClearHidden_UnhidesEverything()
    {
        var f = new ClassFacetFilter();
        f.Rebuild(new[] { "UnityEngine.Transform", "Game.Player" });
        f.AutoHideNoise();
        Assert.True(f.AnyHidden);

        f.ClearHidden();

        Assert.False(f.AnyHidden);
        Assert.All(f.Facets, x => Assert.False(x.IsHidden));
    }
}
