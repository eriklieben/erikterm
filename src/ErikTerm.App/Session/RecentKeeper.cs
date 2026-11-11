namespace ErikTerm.App.Session;

/// <summary>
/// The process's recent commands and directories (remember_commands): one <see cref="RecentHistory"/> every window
/// records into, loaded when the setting is on and saved a couple of seconds after the last change.
/// </summary>
internal sealed class RecentKeeper(string path)
{
    /// <summary>Recent commands and directories of every window; null while remember_commands is off.</summary>
    public RecentHistory? Recent { get; private set; }

    /// <summary>Startup and after a reload: on loads the file, off forgets the list in memory (the file is left alone).</summary>
    public void Apply(bool remember)
    {
        if (!remember) Recent = null;
        else Recent ??= RecentHistory.Load(path);
    }

    /// <summary>Hook from the event loop: a couple of seconds after the last change.</summary>
    public void Tick(long now)
    {
        if (Recent is { Dirty: true } r && now - r.ChangedAt >= 2000) Save();
    }

    public void Save()
    {
        if (Recent is not { Dirty: true } r) return;
        try { r.Save(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"erikterm: can't save {path}: {ex.Message}");
        }
    }

    /// <summary>Clear Recent Commands and Directories (palette).</summary>
    public bool Forget() => Recent?.Clear(path) ?? true;
}
