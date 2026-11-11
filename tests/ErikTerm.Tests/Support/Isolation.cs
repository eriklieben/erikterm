namespace ErikTerm.Tests;

/// <summary>
/// Keys for <c>[NotInParallel(...)]</c>. TUnit runs every test in parallel, including tests of the same class; tests
/// that share a key never overlap (they still run alongside the rest). A bare <c>[NotInParallel]</c> runs a test alone.
/// </summary>
internal static class Isolation
{
    /// <summary>Starts real processes or PTYs (/bin/sh, mkfifo, cmd.exe): forks and their timing stay out of each other's way.</summary>
    public const string Processes = "processes";

    /// <summary>Serves or connects to a real Unix socket (the IPC server, the tmux shim's fake erikterm).</summary>
    public const string Sockets = "sockets";

    /// <summary>Waits on FileSystemWatcher (inotify) events.</summary>
    public const string FileWatchers = "file-watchers";

    /// <summary>Loads native libraries (FreeType) or registers the process-wide DllImport resolver.</summary>
    public const string Natives = "natives";
}
