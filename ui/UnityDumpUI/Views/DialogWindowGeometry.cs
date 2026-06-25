using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using UnityDumpUI.Services;

namespace UnityDumpUI.Views;

/// <summary>
/// Per-dialog maximize→restore placement fix, attached to a live <see cref="Window"/>
/// via its events. It fixes the same bug the main window already handles: on
/// Avalonia 12 (Windows), restoring a maximized window can land it stretched across
/// monitors or at the wrong spot instead of where it was before the maximize. We
/// snapshot the last NORMAL geometry and re-apply it on the way back to Normal.
///
/// This is the discrete equivalent of UE5CEDumper's <c>ManagedDialogWindow</c> base
/// class. discrete's dialogs are XAML windows (<c>x:Class</c> on a <c>&lt;Window&gt;</c>
/// root), so rather than re-tag 16 XAML roots onto a custom base type we wire the same
/// logic externally through the public <see cref="Window"/> events — one
/// <see cref="Attach"/> call in each dialog's parameterless constructor. The
/// snapshot/decision logic lives in the pure, unit-tested
/// <see cref="WindowRestoreState"/>; this class only drives it from the window.
///
/// Seeding happens on <see cref="Window.Opened"/> (CenterOwner sets the position only
/// after the window is shown, so the ctor can't read it). The deferred commit handles
/// the Windows quirk where Width/Height change BEFORE WindowState during a maximize:
/// stash on the size/position change, then commit one Background-priority dispatcher
/// tick later and re-check WindowState — abandoning the stash if it has flipped to
/// maximized.
///
/// MainWindow keeps its own equivalent (entangled with cross-restart persistence +
/// screen validation) and is intentionally not driven by this helper.
/// </summary>
public sealed class DialogWindowGeometry
{
    private readonly Window _window;
    private readonly WindowRestoreState _restore = new();
    private WindowState _previousWindowState = WindowState.Normal;
    private bool _commitScheduled;

    /// <summary>Wire the maximize→restore fix onto <paramref name="window"/>. Call once
    /// from the window's parameterless constructor, after the XAML is loaded. The
    /// returned instance is kept alive by the window's event subscriptions; callers can
    /// discard it.</summary>
    public static DialogWindowGeometry Attach(Window window) => new(window);

    private DialogWindowGeometry(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        // Position isn't an AvaloniaProperty in 12 — listen via the explicit event.
        _window.PositionChanged += OnPositionChanged;
        // Width/Height/WindowState are AvaloniaProperties — observe via PropertyChanged.
        _window.PropertyChanged += OnWindowPropertyChanged;
        // Seed once shown: CenterOwner positions the window at Opened, not in the ctor.
        _window.Opened += OnOpenedSeed;
    }

    private void OnOpenedSeed(object? sender, EventArgs e)
    {
        // Unsubscribe in finally so a (vanishingly unlikely) throw while seeding can't
        // leave the handler attached and re-seed on a later Opened.
        try
        {
            _previousWindowState = _window.WindowState;
            if (_window.WindowState == WindowState.Normal)
                _restore.Seed(_window.Position, _window.Width, _window.Height);
        }
        finally
        {
            _window.Opened -= OnOpenedSeed;
        }
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (!_restore.Seeded) return;
        if (_window.WindowState == WindowState.Normal)
        {
            _restore.NotePosition(_window.Position);
            ScheduleSnapshotCommit();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            var newState = (WindowState)e.NewValue!;
            HandleWindowStateTransition(_previousWindowState, newState);
            _previousWindowState = newState;
        }
        else if (e.Property == Window.WidthProperty || e.Property == Window.HeightProperty)
        {
            // Reading WindowState here is unreliable on the to-maximized transition
            // (Width/Height flip first), so stash now and commit deferred (see below).
            if (_restore.Seeded && _window.WindowState == WindowState.Normal)
            {
                _restore.NoteSize(_window.Width, _window.Height);
                ScheduleSnapshotCommit();
            }
        }
    }

    // Coalesced deferred commit at Background priority so any WindowState change in the
    // same Win32 message has propagated before we promote the stash.
    private void ScheduleSnapshotCommit()
    {
        if (_commitScheduled) return;
        _commitScheduled = true;
        Dispatcher.UIThread.Post(CommitSnapshot, DispatcherPriority.Background);
    }

    private void CommitSnapshot()
    {
        _commitScheduled = false;
        _restore.Commit(_window.WindowState == WindowState.Normal);
    }

    private void HandleWindowStateTransition(WindowState oldState, WindowState newState)
    {
        // Leaving Normal: the snapshot is already kept fresh by the change handlers above.
        if (oldState == WindowState.Normal && newState != WindowState.Normal)
            return;
        // Returning to Normal: re-apply the snapshot so the dialog lands where it was.
        if (newState == WindowState.Normal && oldState != WindowState.Normal
            && _restore.TryGetRestoreRect(out var pos, out var w, out var h))
        {
            _window.Position = pos;
            _window.Width = w;
            _window.Height = h;
        }
    }
}
