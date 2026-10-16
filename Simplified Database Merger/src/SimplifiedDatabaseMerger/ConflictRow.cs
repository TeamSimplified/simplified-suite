using System.ComponentModel;
using SimplifiedDatabaseMerger.Core;

namespace SimplifiedDatabaseMerger;

/// <summary>
/// One conflict as the grid shows it. The choice is the conflict's own, so the
/// plan that writes the file reads exactly what the grid shows; this only adds
/// the text and the change notice the row needs to redraw.
/// </summary>
public sealed class ConflictRow(Conflict conflict) : INotifyPropertyChanged
{
    public Conflict Conflict { get; } = conflict;

    /// <summary>
    /// The record's key, in hex. The file stores no names, only the hashes of
    /// them, so this is the one name a record has.
    /// </summary>
    public string KeyText { get; } = conflict.Key.ToString("X16");

    public string CleanText { get; } = DatabaseMerge.Describe(conflict.Clean);
    public string FirstText { get; } = DatabaseMerge.Describe(conflict.First);
    public string SecondText { get; } = DatabaseMerge.Describe(conflict.Second);

    public Keep Keep
    {
        get => Conflict.Keep;
        set
        {
            if (Conflict.Keep == value) return;
            Conflict.Keep = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Keep)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
