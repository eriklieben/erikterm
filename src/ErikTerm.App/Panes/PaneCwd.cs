namespace ErikTerm.App.Panes;

/// <summary>
/// Where a split, tab or window opened from a pane starts: the directory the shell reported (OSC 7), else the
/// foreground program's current directory, else the shell's (both from /proc on Linux), else default_cwd.
/// </summary>
internal static class PaneCwd
{
    /// <summary>The first of the candidates that names an existing directory; lookups are only made when needed.</summary>
    public static string? Resolve(string? reported, Func<string?> foreground, Func<string?> shell, string? defaultCwd,
        Func<string, bool>? exists = null)
    {
        exists ??= Directory.Exists;
        if (Usable(reported, exists)) return reported;
        if (foreground() is { } fg && Usable(fg, exists)) return fg;
        if (shell() is { } sh && Usable(sh, exists)) return sh;
        return defaultCwd;
    }

    /// <summary>
    /// The directory in an OSC 7 report (<c>file://host/path</c>, percent-encoded), or null when it isn't one or
    /// names another machine: only an empty host, <c>localhost</c> or this machine's name count (a shell on a remote
    /// host reports a path there, which must not become the directory of a local split). Not checked for existence.
    /// <para>
    /// A file URI's path always begins with a slash, so a Windows directory arrives as <c>/C:/src</c> — which is a
    /// path no Windows API accepts, and the report would be dropped as a directory that doesn't exist. Shells report
    /// it that way because the URI form is the same everywhere, so the slash before a drive letter is taken off here.
    /// </para>
    /// </summary>
    public static string? FromOsc7(string? uri, Func<string, bool>? isLocalHost = null, bool? windows = null)
    {
        if (uri is null || !uri.StartsWith("file://", StringComparison.Ordinal)) return null;
        int slash = uri.IndexOf('/', 7);
        if (slash < 0) return null;
        string host = uri[7..slash];
        if (!(isLocalHost ?? IsLocalHost)(host)) return null;
        string path;
        try { path = Uri.UnescapeDataString(uri[slash..]); }
        catch (UriFormatException) { return null; }
        // "/C:/src" -> "C:/src". Only on Windows, and only for a drive letter: a Unix path that happens to start
        // with a letter and a colon is a real directory there, and dropping its leading slash would break it.
        if ((windows ?? OperatingSystem.IsWindows()) && path.Length >= 3 && path[0] == '/' && path[2] == ':'
            && char.IsAsciiLetter(path[1]))
            path = path[1..];
        return path.Contains('\0') ? null : path;
    }

    /// <summary>Empty, localhost, or this machine's host name (full or its first label), ignoring case.</summary>
    public static bool IsLocalHost(string host)
    {
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return LocalNames.Value.Any(name => host.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Lazy<string[]> LocalNames = new(() =>
    {
        var names = new List<string>();
        try { names.Add(System.Net.Dns.GetHostName()); } catch (System.Net.Sockets.SocketException) { /* no host name: the machine name may still match */ }
        try { names.Add(Environment.MachineName); } catch (InvalidOperationException) { /* no machine name: the host name may still match */ }
        foreach (string n in names.ToArray())
            if (n.IndexOf('.') is > 0 and var dot) names.Add(n[..dot]);
        return names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    });

    private static bool Usable(string? dir, Func<string, bool> exists) => dir is { Length: > 0 } && exists(dir);
}
