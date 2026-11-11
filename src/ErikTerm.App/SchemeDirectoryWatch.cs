namespace ErikTerm.App;

/// <summary>
/// Watches a color scheme directory (e.g. ~/.config/erikterm/colors) for *.toml changes — including one that doesn't
/// exist yet: until it does, the nearest existing ancestor is watched for the next path component, and the watch moves
/// down as directories appear. The parent of an existing directory is watched too, so deleting and re-creating (or
/// renaming into place) the directory re-arms it. <c>changed</c> runs on a thread-pool thread for every scheme file
/// change and whenever the directory appears or goes away.
/// </summary>
internal sealed class SchemeDirectoryWatch : IDisposable
{
    private readonly string _target;
    private readonly Action _changed;
    private readonly Lock _lock = new();
    private FileSystemWatcher? _files, _structure;
    private readonly Action? _afterLooking;
    private bool _disposed;

    /// <param name="target">The directory to watch.</param>
    /// <param name="changed">Runs on a thread-pool thread for every change.</param>
    /// <param name="afterLooking">Runs between reading the directory's state and starting the watchers (for tests).</param>
    internal SchemeDirectoryWatch(string target, Action changed, Action? afterLooking = null)
    {
        _target = Path.GetFullPath(target);
        _changed = changed;
        _afterLooking = afterLooking;
        Arm(initial: true);
    }

    /// <summary>The directory whose entries are watched for the next step toward the target (for tests and diagnostics).</summary>
    public string? WatchingFor { get; private set; }

    /// <summary>True while the target exists and its *.toml files are watched.</summary>
    public bool WatchingFiles { get { lock (_lock) return _files is not null; } }

    private void Arm(bool initial)
    {
        bool movedWhileArming = false;
        lock (_lock)
        {
            if (_disposed) return;
            // A change between looking and the watcher starting raises no event, so look again once it runs. macOS hits
            // this: FSEvents reports one deletion as several events, and each re-arms while the directory may be coming back.
            for (int attempt = 0; attempt < MaxArmAttempts; attempt++)
            {
                _files?.Dispose();
                _structure?.Dispose();
                _files = _structure = null;
                WatchingFor = null;

                var state = Look();
                _afterLooking?.Invoke();
                if (state.Exists)
                {
                    _files = Watch(_target, "*.toml", NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, files: true);
                    if (Path.GetDirectoryName(_target) is { } parent)
                        _structure = Watch(parent, Path.GetFileName(_target), NotifyFilters.DirectoryName, files: false);
                }
                else if (state.Nearest is var (ancestor, next))
                {
                    _structure = Watch(ancestor, next, NotifyFilters.DirectoryName, files: false);
                    WatchingFor = Path.Combine(ancestor, next);
                }
                if (Look() == state) break;
                movedWhileArming = true;
            }
        }
        if (!initial || movedWhileArming) _changed(); // the directory appeared (maybe with files in it) or went away
    }

    private const int MaxArmAttempts = 8;

    private (bool Exists, (string Ancestor, string Next)? Nearest) Look() =>
        Directory.Exists(_target) ? (true, null) : (false, NearestExistingAncestor(_target));

    /// <summary>The deepest existing ancestor of <paramref name="path"/> and the name of the next component below it.</summary>
    internal static (string Ancestor, string Next)? NearestExistingAncestor(string path)
    {
        string current = path;
        while (Path.GetDirectoryName(current) is { } parent)
        {
            if (Directory.Exists(parent)) return (parent, Path.GetFileName(current));
            current = parent;
        }
        return null;
    }

    private FileSystemWatcher? Watch(string dir, string filter, NotifyFilters notify, bool files)
    {
        try
        {
            var w = new FileSystemWatcher(dir, filter) { NotifyFilter = notify };
            if (files)
            {
                FileSystemEventHandler changed = (_, _) => _changed();
                w.Changed += changed;
                w.Created += changed;
                w.Deleted += changed;
                w.Renamed += (_, _) => _changed();
            }
            else
            {
                // Don't dispose a watcher from inside its own event: re-arm from the pool.
                FileSystemEventHandler moved = (_, _) => ThreadPool.QueueUserWorkItem(_ => Arm(initial: false));
                w.Created += moved;
                w.Deleted += moved;
                w.Renamed += (_, _) => ThreadPool.QueueUserWorkItem(_ => Arm(initial: false));
            }
            w.EnableRaisingEvents = true;
            return w;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"erikterm: not watching {dir}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _files?.Dispose();
            _structure?.Dispose();
            _files = _structure = null;
        }
    }
}
