using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SimplifiedLanguageEditor.Model;

/// <summary>
/// One row in the language table. Two-way bound to a WPF DataGrid so the
/// user can edit keys/values inline.
/// </summary>
public sealed class LngEntryVm : INotifyPropertyChanged
{
    private string _key;
    private string _value;

    public LngEntryVm(string key, string value)
    {
        _key = key;
        _value = value;
    }

    public string Key
    {
        get => _key;
        set { if (_key != value) { _key = value; OnPropertyChanged(); } }
    }

    public string Value
    {
        get => _value;
        set { if (_value != value) { _value = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
