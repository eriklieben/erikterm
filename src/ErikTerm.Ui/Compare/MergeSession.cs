namespace ErikTerm.Ui.Compare;

/// <summary>One file of the session; <see cref="File"/> is null while it's still loading or when it failed.</summary>
public sealed class MergeEntry(string path)
{
    public string Path { get; } = path;
    public MergeFile? File { get; set; }
    public string? Error { get; set; }

    /// <summary>Written (and staged, in repo mode) with no conflicts left.</summary>
    public bool Done { get; set; }

    public bool Loaded => File is not null || Error is not null;
}

/// <summary>Side effects a merge needs from the outside world.</summary>
public interface IMergeHost
{
    /// <summary>True for <c>erikterm mergetool</c> with no arguments in a repository: saves also <c>git add</c> the file.</summary>
    bool RepoMode { get; }

    /// <summary>The label shown for the action a save performs ("git add -- x"), or null.</summary>
    string? StageDescription(MergeEntry entry);

    /// <summary>Writes the result to the file (or deletes it for a null body) and, in repo mode with <paramref name="stage"/>, stages it. Null on success, otherwise the reason.</summary>
    string? Save(MergeEntry entry, byte[]? content, bool stage);

    /// <summary>Opens text in the user's editor (the screen stays suspended meanwhile); the new text, or null when the editor failed.</summary>
    string? EditExternally(MergeEntry entry, string text, int line);
}

/// <summary>The files of a merge and the actions on them, independent of how they're drawn.</summary>
public sealed class MergeSession(IMergeHost host)
{
    public IMergeHost Host { get; } = host;
    public List<MergeEntry> Entries { get; } = [];
    public int Current { get; private set; }
    public string Status { get; set; } = "";
    public bool StatusIsError { get; set; }

    public MergeEntry? Entry => Current < Entries.Count ? Entries[Current] : null;
    public MergeFile? File => Entry?.File;

    public bool AnyDirty => Entries.Any(e => e.File is { Dirty: true });

    /// <summary>Everything resolved and saved: exit code 0.</summary>
    public bool AllDone => Entries.Count > 0 && Entries.All(e => e.Done);

    public int ExitCode => AllDone ? 0 : 1;

    public void Select(int index)
    {
        if (index >= 0 && index < Entries.Count) Current = index;
    }

    public void SelectNext(int delta)
    {
        if (Entries.Count == 0) return;
        Current = ((Current + delta) % Entries.Count + Entries.Count) % Entries.Count;
    }

    /// <summary>Whether saving the current file is blocked by unresolved conflicts.</summary>
    public bool SaveNeedsMarkers => File is { Kind: MergeFileKind.Text } file && file.UnresolvedCount > 0;

    /// <summary>Saves the current file. With conflicts left it writes markers only when <paramref name="withMarkers"/> is set, and never stages then.</summary>
    public bool Save(bool withMarkers)
    {
        var entry = Entry;
        var file = entry?.File;
        if (entry is null || file is null) return false;
        int unresolved = file.UnresolvedCount;
        if (SaveBlockedBy(entry, file, unresolved, withMarkers) is { } reason) { Fail(reason); return false; }

        byte[]? content = file.Serialize(withMarkers && unresolved > 0);
        string? error = Host.Save(entry, content, stage: unresolved == 0);
        if (error is not null) { Fail(error); return false; }

        file.MarkSaved();
        ReportSaved(entry, unresolved);
        return true;
    }

    private static string? SaveBlockedBy(MergeEntry entry, MergeFile file, int unresolved, bool withMarkers)
    {
        if (file.Kind != MergeFileKind.Text)
            return file.FileChoice == FileChoice.None ? $"Choose a version for {entry.Path} first" : null;
        return unresolved > 0 && !withMarkers ? $"{Counted(unresolved, "conflict")} left in {entry.Path}" : null;
    }

    private void ReportSaved(MergeEntry entry, int unresolved)
    {
        StatusIsError = false;
        entry.Done = unresolved == 0;
        if (unresolved > 0)
        {
            Status = $"Saved {entry.Path} with {Counted(unresolved, "conflict marker")}; not staged";
            return;
        }
        string? staged = Host.RepoMode ? Host.StageDescription(entry) : null;
        Status = staged is null ? $"Saved {entry.Path}" : $"Saved {entry.Path} and ran {staged}";
        if (Host.RepoMode) AdvanceAfterSave();
    }

    private void AdvanceAfterSave()
    {
        for (int i = 1; i <= Entries.Count; i++)
        {
            int k = (Current + i) % Entries.Count;
            if (!Entries[k].Done) { Current = k; return; }
        }
        Status = AllDone ? $"All {Counted(Entries.Count, "file")} resolved and staged. Nothing was committed: continue the merge yourself." : Status;
    }

    public void Fail(string message)
    {
        Status = message;
        StatusIsError = true;
    }

    public static string Counted(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
