using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Sunrise.FileManager.Models;

public sealed class ExplorerTab : INotifyPropertyChanged
{
    private readonly List<string> _history = [];
    private int _position;
    private bool _isSelected;

    public ExplorerTab(string path)
    {
        _history.Add(path);
    }

    public string CurrentPath => _history[_position];
    public string Title
    {
        get
        {
            if (Sunrise.FileManager.Services.WindowsKnownLocations.IsRecycleBin(CurrentPath)) return "휴지통";
            string path = CurrentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(path) is { Length: > 0 } name ? name : CurrentPath;
        }
    }
    public bool CanGoBack => _position > 0;
    public bool CanGoForward => _position < _history.Count - 1;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Notify();
        }
    }

    public bool Navigate(string path)
    {
        if (string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase)) return false;
        if (CanGoForward) _history.RemoveRange(_position + 1, _history.Count - _position - 1);
        _history.Add(path);
        _position++;
        NotifyPath();
        return true;
    }

    public bool Back()
    {
        if (!CanGoBack) return false;
        _position--;
        NotifyPath();
        return true;
    }

    public bool Forward()
    {
        if (!CanGoForward) return false;
        _position++;
        NotifyPath();
        return true;
    }

    private void NotifyPath()
    {
        Notify(nameof(CurrentPath));
        Notify(nameof(Title));
        Notify(nameof(CanGoBack));
        Notify(nameof(CanGoForward));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
