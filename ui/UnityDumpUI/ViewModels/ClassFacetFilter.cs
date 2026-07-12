// ClassFacetFilter.cs — reusable client-side "class -> hit count" noise filter
// for any result list (Symbol Finder / Value Search / Snapshot diff / …).
// Ported in spirit from UE5CEDumper's ClassFacetFilter.
//
// Builds a Top-N histogram of the classes present in the current result set and
// lets the user tick classes to HIDE. It's a pure DISPLAY filter over the
// retained full list (no re-query), the hidden set is preserved across rebuilds
// (re-searches keep your exclusions), and an "auto-hide engine noise" helper
// pre-ticks classes in well-known engine / system namespaces.
//
// Self-contained: it owns no rows — the consumer calls Rebuild(classDisplays)
// after a search and IsHidden(classDisplay) inside its own refilter, subscribing
// to Changed to re-run that refilter when a facet toggles.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UnityDumpUI.ViewModels;

public sealed partial class ClassFacetFilter : ObservableObject
{
    // Engine / framework namespace prefixes the auto-hide button pre-ticks.
    private static readonly string[] NoisePrefixes =
    {
        "System", "UnityEngine", "Unity.", "Microsoft", "Mono", "TMPro",
        "Cinemachine", "Newtonsoft", "Sirenix", "DG.", "Spine", "Rewired",
    };

    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
    private bool _suppress;

    /// <summary>Top-N class facets in the current result set (highest count first).</summary>
    public ObservableCollection<ClassFacet> Facets { get; } = new();

    /// <summary>Fired when the hidden set changes — the consumer re-runs its refilter.</summary>
    public event Action? Changed;

    public bool AnyFacets => Facets.Count > 0;
    public bool AnyHidden => _hidden.Count > 0;

    /// <summary>Rebuild the histogram from the classes in the current results,
    /// preserving the hidden set. <paramref name="classNames"/> is one entry per
    /// row (duplicates are the counts).</summary>
    public void Rebuild(IEnumerable<string?> classNames, int topN = 40)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string? c in classNames)
        {
            if (string.IsNullOrEmpty(c)) continue;
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        foreach (ClassFacet old in Facets) old.PropertyChanged -= OnFacetPropertyChanged;
        Facets.Clear();
        foreach (var kv in counts.OrderByDescending(k => k.Value)
                                 .ThenBy(k => k.Key, StringComparer.Ordinal)
                                 .Take(topN))
        {
            var facet = new ClassFacet(kv.Key, kv.Value) { IsHidden = _hidden.Contains(kv.Key) };
            facet.PropertyChanged += OnFacetPropertyChanged;
            Facets.Add(facet);
        }
        OnPropertyChanged(nameof(AnyFacets));
        OnPropertyChanged(nameof(AnyHidden));
    }

    /// <summary>True when <paramref name="className"/> is currently hidden.</summary>
    public bool IsHidden(string? className) =>
        className is not null && _hidden.Contains(className);

    /// <summary>Pre-tick every facet whose class sits in a known engine / system
    /// namespace (a zero-backend "hide the noise" heuristic).</summary>
    public void AutoHideNoise()
    {
        _suppress = true;
        foreach (ClassFacet f in Facets)
            if (IsNoise(f.ClassName)) { f.IsHidden = true; _hidden.Add(f.ClassName); }
        _suppress = false;
        OnPropertyChanged(nameof(AnyHidden));
        Changed?.Invoke();
    }

    /// <summary>Un-hide everything.</summary>
    public void ClearHidden()
    {
        _suppress = true;
        _hidden.Clear();
        foreach (ClassFacet f in Facets) f.IsHidden = false;
        _suppress = false;
        OnPropertyChanged(nameof(AnyHidden));
        Changed?.Invoke();
    }

    private void OnFacetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppress || e.PropertyName != nameof(ClassFacet.IsHidden) || sender is not ClassFacet f) return;
        if (f.IsHidden) _hidden.Add(f.ClassName); else _hidden.Remove(f.ClassName);
        OnPropertyChanged(nameof(AnyHidden));
        Changed?.Invoke();
    }

    internal static bool IsNoise(string className)
    {
        foreach (string p in NoisePrefixes)
            if (className.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>One class row in the facet histogram — name, hit count, hide toggle.</summary>
public sealed partial class ClassFacet : ObservableObject
{
    public ClassFacet(string className, int count)
    {
        ClassName = className;
        Count     = count;
    }

    public string ClassName { get; }
    public int    Count     { get; }

    [ObservableProperty] private bool _isHidden;

    /// <summary>"ClassName (count)" for the checkbox label.</summary>
    public string Display => $"{ClassName} ({Count})";
}
