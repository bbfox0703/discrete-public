// HighlightedTextBlock.cs — substring-highlighting TextBlock for the
// Browser / Inspector filter UX (2026-05-08).
//
// Each of the four filter inputs (Assemblies / Classes / Fields / Methods)
// already drives a *visibility* refilter (rows that don't match are
// hidden). With the matching rows shown, this control renders the matching
// substring in a styled <Run> so the user can see exactly which characters
// triggered the match — useful when filtering against a long type-qualified
// name where the match is buried mid-string.
//
// Design choices:
//   * Subclass TextBlock so existing styling (FontWeight / Opacity /
//     TextTrimming) on the consumer's <TextBlock> just keeps working when
//     swapping to <controls:HighlightedTextBlock>.
//   * Source text comes through the bindable `SourceText` property — we
//     deliberately don't reuse the inherited `Text` property because we
//     toggle between plain-text and inline-runs rendering, and Avalonia's
//     TextBlock treats `Text` and `Inlines` as mutually-exclusive sources.
//   * Highlight string comes through `Highlight` — bound from the filter
//     TextBox via `{Binding #FilterBox.Text}` element-name binding so each
//     row inherits the live filter value without the VM having to push it
//     onto every row record.
//   * AOT-compatible: AvaloniaProperty.Register + class handlers are
//     analyzer-clean.

using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using UnityDumpUI.Core.Services;

namespace UnityDumpUI.Controls;

public class HighlightedTextBlock : TextBlock
{
    /// <summary>
    /// The source text. Bind this in place of <see cref="TextBlock.Text"/>
    /// — the control flips between <c>Text</c> (no highlight) and
    /// <c>Inlines</c> (with highlight) internally as
    /// <see cref="Highlight"/> changes.
    /// </summary>
    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, string?>(nameof(SourceText));

    public string? SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    /// <summary>
    /// Substring to highlight (case-insensitive). Empty / null clears all
    /// highlighting and renders <see cref="SourceText"/> as plain text.
    /// </summary>
    public static readonly StyledProperty<string?> HighlightProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, string?>(nameof(Highlight));

    public string? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    /// <summary>
    /// Background brush painted behind the matching characters. Default is
    /// a translucent amber so it reads on both light and dark themes.
    /// </summary>
    public static readonly StyledProperty<IBrush> HighlightBackgroundProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, IBrush>(
            nameof(HighlightBackground),
            defaultValue: new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xC1, 0x07)));

    public IBrush HighlightBackground
    {
        get => GetValue(HighlightBackgroundProperty);
        set => SetValue(HighlightBackgroundProperty, value);
    }

    /// <summary>
    /// Foreground brush for matching characters. Defaults to the inherited
    /// <see cref="TextBlock.Foreground"/> via <c>null</c> so the host theme
    /// keeps deciding.
    /// </summary>
    public static readonly StyledProperty<IBrush?> HighlightForegroundProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, IBrush?>(nameof(HighlightForeground));

    public IBrush? HighlightForeground
    {
        get => GetValue(HighlightForegroundProperty);
        set => SetValue(HighlightForegroundProperty, value);
    }

    static HighlightedTextBlock()
    {
        SourceTextProperty.Changed.AddClassHandler<HighlightedTextBlock>((c, _) => c.Rebuild());
        HighlightProperty.Changed.AddClassHandler<HighlightedTextBlock>((c, _) => c.Rebuild());
        HighlightBackgroundProperty.Changed.AddClassHandler<HighlightedTextBlock>((c, _) => c.Rebuild());
        HighlightForegroundProperty.Changed.AddClassHandler<HighlightedTextBlock>((c, _) => c.Rebuild());
    }

    private void Rebuild()
    {
        string src = SourceText ?? string.Empty;
        string? hl = Highlight;

        if (string.IsNullOrEmpty(hl) || string.IsNullOrEmpty(src))
        {
            // No filter / no source — clear inlines first so plain Text
            // rendering takes over. Clear before set to avoid Avalonia's
            // dual-source guard tripping.
            Inlines = null;
            Text = src;
            return;
        }

        IReadOnlyList<HighlightSegmenter.Segment> segments =
            HighlightSegmenter.Build(src, hl);

        // Single non-match segment ⇒ no actual hit; render as plain text
        // (saves a per-row InlineCollection allocation when the filter
        // doesn't match this particular row but the Refilter routed it
        // here anyway, e.g. a Fields-grid row matched on Type but the
        // Name column is non-match).
        if (segments.Count == 1 && !segments[0].IsMatch)
        {
            Inlines = null;
            Text = src;
            return;
        }

        Text = null;
        var inlines = new InlineCollection();
        foreach (HighlightSegmenter.Segment seg in segments)
        {
            var run = new Run(seg.Text);
            if (seg.IsMatch)
            {
                run.Background = HighlightBackground;
                if (HighlightForeground is { } fg)
                    run.Foreground = fg;
                run.FontWeight = FontWeight.SemiBold;
            }
            inlines.Add(run);
        }
        Inlines = inlines;
    }
}

// HighlightSegmenter lives in UnityDumpUI.Core.Services so the segmentation
// can be unit-tested without an Avalonia dependency. See
// UnityDumpUI.Core/Services/HighlightSegmenter.cs.
