using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SimplifiedLanguageEditor.Model;

/// <summary>
/// Drop-in <see cref="ObservableCollection{T}"/> that can pause notifications
/// during a bulk reload and fire a single <c>Reset</c> at the end. Used by the
/// language editor's Load and Undo paths so the DataGrid doesn't re-layout
/// per-row across thousands of additions.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private int _suspendDepth;

    /// <summary>
    /// Pause CollectionChanged + PropertyChanged. Dispose the returned token
    /// to fire one Reset notification and resume normal behavior.
    /// </summary>
    public IDisposable BeginBulkUpdate()
    {
        _suspendDepth++;
        return new Releaser(this);
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suspendDepth > 0) return;
        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suspendDepth > 0) return;
        base.OnPropertyChanged(e);
    }

    private void EndBulkUpdate()
    {
        if (_suspendDepth == 0) return;
        _suspendDepth--;
        if (_suspendDepth > 0) return;

        // Fire the catch-all notifications so any bound view rebinds wholesale.
        base.OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        base.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private sealed class Releaser(BulkObservableCollection<T> owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.EndBulkUpdate();
        }
    }
}
