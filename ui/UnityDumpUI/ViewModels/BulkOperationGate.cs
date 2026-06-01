// BulkOperationGate.cs — cross-ViewModel mutual exclusion for heavy,
// long-running "bulk" operations (snapshot capture, value-scan sessions).
//
// These ops stream many RPCs over the bulk pipe connection. Running two at
// once would interleave on the bulk wire and crawl, and is rarely what the
// user wants. The gate enforces "one bulk op at a time": a VM TryEnter()s
// before starting, and every bulk command's CanExecute keys on IsBusy so
// the OTHER VMs' bulk buttons grey out (non-modal — light browse on the
// interactive connection stays fully usable).
//
// Single-threaded by construction: all callers are UI-thread command
// handlers, so no locking is needed. AOT-safe (no reflection).

using System;
using System.ComponentModel;

namespace UnityDumpUI.ViewModels;

public sealed class BulkOperationGate : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string? _current;

    /// <summary>True while some bulk op holds the lane.</summary>
    public bool IsBusy => _current is not null;

    /// <summary>Human label of the in-flight bulk op (e.g. "Snapshot"),
    /// or null when idle. Bind a status hint to this.</summary>
    public string? CurrentOperation => _current;

    /// <summary>
    /// Claim the bulk lane. Returns a lease to dispose when done, or null
    /// if another op already holds it. Callers normally gate their command
    /// CanExecute on <see cref="IsBusy"/> too, so a null return is the
    /// belt-and-suspenders path (a race that shouldn't happen on the UI
    /// thread).
    /// </summary>
    public IDisposable? TryEnter(string operationName)
    {
        if (_current is not null) return null;
        _current = operationName;
        Raise();
        return new Lease(this);
    }

    private void Exit()
    {
        _current = null;
        Raise();
    }

    private void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentOperation)));
    }

    private sealed class Lease : IDisposable
    {
        private BulkOperationGate? _gate;
        public Lease(BulkOperationGate gate) => _gate = gate;
        public void Dispose()
        {
            _gate?.Exit();
            _gate = null;
        }
    }
}
