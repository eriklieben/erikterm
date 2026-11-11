using ErikTerm.App.Ui;

namespace ErikTerm.App.Session;

/// <summary>
/// Session restore, process half: every window's tabs, splits, directories, foreground programs, size / position and
/// font zoom are saved to session.json (<see cref="SessionStore"/>) on exit and ~2 s after they change, and rebuilt on
/// the next start (one window per saved window; the per-window half is Windowing/WindowSession.cs). The file reflects the
/// process: a window you close is gone from it, and quitting (closing the last window, logout, SIGTERM) saves what's
/// left. Exiting the last shell of the last window forgets the session (there is nothing left to restore). Windows
/// started with -e aren't saved; new windows (Ctrl+Shift+N) start fresh but are saved. Separate processes
/// (--new-process, or a different config) each write the file; the one that saves last wins.
/// </summary>
/// <param name="path">session.json, per --class: windows of another class (a drop-down) keep their own file.</param>
/// <param name="capture">Every open window that saves, on the main thread; null when there are none. The argument says whether scrollback goes in.</param>
internal sealed class SessionKeeper(string path, Func<bool, SessionFile?> capture)
{
    private const int SessionCheckMs = 1000, SessionDebounceMs = 2000, SessionMaxDelayMs = 10_000;
    /// <summary>A corrupt or hand-edited file must not open dozens of windows.</summary>
    private const int MaxRestoredWindows = 16;

    private bool _saving = true;     // off after "Forget Saved Session"
    private string? _signature;
    private long _checkAt, _changedAt, _firstChangeAt;
    /// <summary>The writes in progress, in order: a disk that stalls must not show up as dropped frames.</summary>
    private Task _writes = Task.CompletedTask;

    /// <summary>Startup finished: safe to capture.</summary>
    public bool Started { get; set; }
    /// <summary>The user exited the last shell of the last window: forget the session on exit.</summary>
    public bool ClosedLastPane { get; set; }

    /// <summary>Startup: the saved windows to rebuild (TermWindow.PrepareRestore takes it from there).</summary>
    public List<SessionWindow> LoadRestorable()
    {
        var file = SessionStore.Load(path, out string? problem);
        if (problem is not null) Console.Error.WriteLine($"erikterm: ignoring saved session {path}: {problem}");
        return SessionWindows.Restorable(file, MaxRestoredWindows);
    }

    /// <summary>Hook from the event loop: saves the session when it changed.</summary>
    public void Tick(long now)
    {
        if (!Started || now < _checkAt) return;
        _checkAt = now + SessionCheckMs;
        if (!_saving) return;

        string signature;
        try
        {
            if (capture(false) is not { } file) return;
            signature = SessionJson.Serialize(file);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"erikterm: session capture failed: {ex.Message}");
            return;
        }
        if (_signature is null) { _signature = signature; return; } // the state we started in
        if (signature != _signature)
        {
            _signature = signature;
            _changedAt = now;
            if (_firstChangeAt == 0) _firstChangeAt = now;
        }
        if (_firstChangeAt != 0 && (now - _changedAt >= SessionDebounceMs || now - _firstChangeAt >= SessionMaxDelayMs))
        {
            _firstChangeAt = 0;
            SaveInBackground();
        }
    }

    /// <summary>Captures on this thread (the panes are its) and writes on another. Errors are logged.</summary>
    private void SaveInBackground()
    {
        SessionFile? file;
        try
        {
            file = capture(true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"erikterm: session capture failed: {ex.Message}");
            return;
        }
        if (file is null) return;
        file.SavedAt = DateTimeOffset.UtcNow;
        string to = path;
        _writes = _writes.ContinueWith(_ =>
        {
            try { SessionStore.Save(to, file); }
            catch (Exception ex) { Console.Error.WriteLine($"erikterm: couldn't save the session to {to}: {ex.Message}"); }
        }, TaskScheduler.Default);
    }

    /// <summary>Waits for background writes, so a save or delete on this thread comes after them.</summary>
    private void FinishWrites()
    {
        try { _writes.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { /* each write reports its own errors */ }
    }

    /// <summary>Writes session.json now. Errors are reported (a toast through <paramref name="toast"/>), never thrown.</summary>
    private bool Save(bool quiet, Action<ToastKind, string, string?>? toast = null)
    {
        try
        {
            FinishWrites();
            if (capture(true) is not { } file) return false;
            file.SavedAt = DateTimeOffset.UtcNow;
            SessionStore.Save(path, file);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"erikterm: couldn't save the session to {path}: {ex.Message}");
            if (!quiet) toast?.Invoke(ToastKind.Error, "Couldn't save the session", ex.Message);
            return false;
        }
    }

    /// <summary>Hook from Run's cleanup (the windows are still open): save (or forget) the session. Never throws.</summary>
    public void SaveOnExit()
    {
        try
        {
            if (!Started || !_saving) return;
            FinishWrites();
            if (ClosedLastPane) SessionStore.Delete(path);
            else Save(quiet: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"erikterm: session: {ex.Message}");
        }
    }

    /// <summary>Save Session Now and Forget Saved Session (palette); <paramref name="toast"/> tells the window they ran in.</summary>
    public void AddCommands(List<Command> list, Action<ToastKind, string, string?> toast)
    {
        list.Add(new Command
        {
            Id = "session-save", Category = "Window", Title = "Save Session Now", Icon = Icons.Check,
            Run = () =>
            {
                _saving = true;
                if (Save(quiet: false, toast)) toast(ToastKind.Success, "Session saved", ShortPath(path));
            },
        });
        list.Add(new Command
        {
            Id = "session-forget", Category = "Window", Title = "Forget Saved Session", Icon = Icons.Clear,
            Run = () =>
            {
                try
                {
                    SessionStore.Delete(path);
                    _saving = false;
                    toast(ToastKind.Info, "Saved session forgotten", "These windows won't be saved unless you run Save Session Now");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    toast(ToastKind.Error, "Couldn't delete the saved session", ex.Message);
                }
            },
        });
    }

    internal static string ShortPath(string full)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && full.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + full[home.Length..] : full;
    }
}
