using Sunrise.FileManager.Models;

namespace Sunrise.FileManager.Services;

/// <summary>UI-thread-owned thumbnail lifetime. Old off-screen entries relinquish their decoded bitmap.</summary>
public sealed class ThumbnailRetention
{
    public const int Capacity = 256;
    private readonly Queue<FileEntry> _entries = new();
    public int Count => _entries.Count;
    public void Remember(FileEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity) _entries.Dequeue().ThumbnailSource = null;
    }
    public void Clear()
    {
        while (_entries.Count > 0) _entries.Dequeue().ThumbnailSource = null;
    }
}
