namespace Tunqio.Library.Scanning;

/// <summary>What a folder watch reports; the shape of <see cref="WatcherChangeTypes"/> without the flags.</summary>
internal enum FolderChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
}

/// <summary>
/// The file-system side of <see cref="LibraryWatcher"/>, behind an interface so its debounce, coalescing,
/// rename and overflow handling can be tested without touching a disk. <see cref="FileSystemWatchSource"/> is
/// the real one.
/// </summary>
internal interface IFolderWatchSource
{
    /// <summary>
    /// Starts watching <paramref name="root"/> and its subdirectories. <paramref name="onChange"/> gets the kind,
    /// the full path and, for a rename, the old full path; <paramref name="onError"/> gets a buffer overflow or
    /// a watch that died. Both are called on arbitrary threads. Disposing the result stops the watch.
    /// </summary>
    IDisposable Watch(string root, int bufferSize, Action<FolderChangeKind, string, string?> onChange, Action<Exception> onError);
}

/// <summary>One <see cref="FileSystemWatcher"/> per folder: names, last-write time and size, subdirectories included.</summary>
internal sealed class FileSystemWatchSource : IFolderWatchSource
{
    public static FileSystemWatchSource Instance { get; } = new();

    public IDisposable Watch(string root, int bufferSize, Action<FolderChangeKind, string, string?> onChange, Action<Exception> onError)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = bufferSize,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        try
        {
            watcher.Created += (_, e) => onChange(FolderChangeKind.Created, e.FullPath, null);
            watcher.Changed += (_, e) => onChange(FolderChangeKind.Changed, e.FullPath, null);
            watcher.Deleted += (_, e) => onChange(FolderChangeKind.Deleted, e.FullPath, null);
            watcher.Renamed += (_, e) => onChange(FolderChangeKind.Renamed, e.FullPath, e.OldFullPath);
            watcher.Error += (_, e) => onError(e.GetException());
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }
}
