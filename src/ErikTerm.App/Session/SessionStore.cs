namespace ErikTerm.App.Session;

/// <summary>Where session.json lives and how it's written (atomically, readable only by the user).</summary>
internal static class SessionStore
{
    public const string FileName = "session.json";

    /// <summary>The window class erikterm uses when --class isn't given.</summary>
    public const string DefaultClass = "erikterm";

    /// <summary>
    /// $XDG_STATE_HOME/erikterm/session.json (default ~/.local/state); Windows: %LOCALAPPDATA%\erikterm\session.json.
    /// A window started with another --class (e.g. a drop-down) keeps its own session-&lt;class&gt;.json.
    /// </summary>
    public static string DefaultPath(string? windowClass = null)
    {
        string name = FileNameFor(windowClass);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erikterm", name);
        string state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } x && Path.IsPathRooted(x)
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(state, "erikterm", name);
    }

    /// <summary>
    /// session.json for the default class (none, blank or "erikterm"), else session-&lt;class&gt;.json with the class
    /// reduced to [A-Za-z0-9._-] (anything else becomes '_', at most 64 characters), so it can't leave the directory.
    /// </summary>
    public static string FileNameFor(string? windowClass)
    {
        string c = windowClass?.Trim() ?? "";
        if (c.Length == 0 || c == DefaultClass) return FileName;
        Span<char> buf = stackalloc char[Math.Min(c.Length, 64)];
        for (int i = 0; i < buf.Length; i++)
        {
            char ch = c[i];
            buf[i] = ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' ? ch : '_';
        }
        if (buf[0] == '.') buf[0] = '_'; // no hidden files, no ".."
        return $"session-{buf}.json";
    }

    /// <summary>Reads the file; null when it's missing, unreadable, malformed or from another version (reason in <paramref name="problem"/>).</summary>
    public static SessionFile? Load(string path, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > 64 << 20) { problem = "file too large"; return null; }
            return SessionJson.Deserialize(File.ReadAllText(path), out problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>Writes to a temporary file next to <paramref name="path"/> and renames it over the old one.</summary>
    public static void Save(string path, SessionFile file)
    {
        string json = SessionJson.Serialize(file);
        string dir = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string tmp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(tmp, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the original error is rethrown below; a stray temp file does no harm */ }
            throw;
        }
    }

    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
