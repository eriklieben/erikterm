using System.Text.Json;
using System.Text.Json.Serialization;

namespace ErikTerm.App.Session;

/// <summary>A remembered command (its text and where it ran) or directory, and when it was last seen (Unix ms).</summary>
internal sealed class RecentEntry
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("at")] public long At { get; set; }
}

internal sealed class RecentFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("commands")] public List<RecentEntry> Commands { get; set; } = [];
    [JsonPropertyName("directories")] public List<RecentEntry> Directories { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RecentFile))]
internal sealed partial class RecentJsonContext : JsonSerializerContext;

/// <summary>
/// Recent commands (OSC 133) and directories (OSC 7) of every pane, for the palette's "Run again" / "Go to" entries:
/// deduplicated, most recent first, capped, and kept across restarts in <c>$XDG_STATE_HOME/erikterm/history.json</c>
/// (Windows <c>%LOCALAPPDATA%\erikterm</c>; mode 0600, written atomically, merged with what other erikterm processes
/// wrote). Commands the shell itself keeps out of its history — typed with a leading space (HISTCONTROL=ignorespace) —
/// are never recorded, nor are multi-line or overlong ones or any with control characters. remember_commands = false
/// turns all of it off. Main thread only.
/// </summary>
internal sealed class RecentHistory
{
    public const string FileName = "history.json";
    public const int MaxCommands = 200, MaxDirectories = 100, MaxCommandLength = 1024, MaxPathLength = 4096;
    private const long MaxFileBytes = 1 << 20;

    private readonly List<RecentEntry> _commands = [], _directories = [];

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<RecentEntry> Commands => _commands;
    public IReadOnlyList<RecentEntry> Directories => _directories;

    /// <summary>Something was recorded since the last <see cref="Save"/>.</summary>
    public bool Dirty { get; private set; }
    /// <summary>When the last change was recorded (Environment.TickCount64), for a debounced save.</summary>
    public long ChangedAt { get; private set; }

    public static string DefaultPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erikterm", FileName);
        string state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } x && Path.IsPathRooted(x)
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(state, "erikterm", FileName);
    }

    /// <summary>Whether a command may be kept: one line, no control characters, not blank, no leading space, not too long.</summary>
    public static bool Acceptable(string command) =>
        command.Length is > 0 and <= MaxCommandLength
        && !char.IsWhiteSpace(command[0])
        && !command.Any(Security.PasteSanitizer.IsControl)
        && command.AsSpan().Trim().Length > 0;

    /// <summary>Records a finished command (moving it to the front if it is known). False when it wasn't kept.</summary>
    public bool AddCommand(string command, string? cwd, long unixMs, bool isPrivate, long now)
    {
        if (isPrivate || !Acceptable(command)) return false;
        if (cwd is not null && !AcceptablePath(cwd)) cwd = null;
        Upsert(_commands, command, cwd, unixMs, MaxCommands);
        Dirty = true;
        ChangedAt = now;
        return true;
    }

    /// <summary>Records a directory a shell reported (OSC 7). False when it wasn't kept.</summary>
    public bool AddDirectory(string path, long unixMs, long now)
    {
        if (!AcceptablePath(path)) return false;
        if (_directories.Count > 0 && _directories[0].Text == path) { _directories[0].At = unixMs; return false; } // still there
        Upsert(_directories, path, null, unixMs, MaxDirectories);
        Dirty = true;
        ChangedAt = now;
        return true;
    }

    private static bool AcceptablePath(string path) =>
        path.Length is > 0 and <= MaxPathLength && Path.IsPathRooted(path) && !path.Any(Security.PasteSanitizer.IsControl);

    private static void Upsert(List<RecentEntry> list, string text, string? cwd, long at, int max)
    {
        int i = list.FindIndex(e => e.Text == text);
        if (i >= 0) list.RemoveAt(i);
        list.Insert(0, new RecentEntry { Text = text, Cwd = cwd, At = at });
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
    }

    /// <summary>Forgets everything and deletes the file. False when the file couldn't be deleted: the history is still on disk.</summary>
    public bool Clear(string path)
    {
        _commands.Clear();
        _directories.Clear();
        Dirty = false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The file's entries; an empty history when it is missing, too large or malformed. Entries that aren't acceptable (an edited file) are dropped.</summary>
    public static RecentHistory Load(string path)
    {
        var h = new RecentHistory();
        if (Read(path) is not { } file) return h;
        h.Merge(file);
        return h;
    }

    private static RecentFile? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), RecentJsonContext.Default.RecentFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Adds another list's entries (newest wins per text), keeping the order by time and the caps.</summary>
    private void Merge(RecentFile file)
    {
        MergeInto(_commands, file.Commands?.Where(e => e is not null && Acceptable(e.Text)), MaxCommands);
        MergeInto(_directories, file.Directories?.Where(e => e is not null && AcceptablePath(e.Text)), MaxDirectories);
    }

    private static void MergeInto(List<RecentEntry> list, IEnumerable<RecentEntry>? other, int max)
    {
        if (other is null) return;
        var byText = new Dictionary<string, RecentEntry>(StringComparer.Ordinal);
        foreach (var e in list) byText[e.Text] = e;
        foreach (var e in other)
            if (!byText.TryGetValue(e.Text, out var mine) || e.At > mine.At)
                byText[e.Text] = new RecentEntry { Text = e.Text, Cwd = e.Cwd is { } c && AcceptablePath(c) ? c : null, At = e.At };
        list.Clear();
        list.AddRange(byText.Values.OrderByDescending(e => e.At).Take(max));
    }

    /// <summary>Writes the history, merged with what is in the file now (another erikterm process may have written it): 0600, atomically.</summary>
    public void Save(string path)
    {
        if (Read(path) is { } onDisk) Merge(onDisk);
        var file = new RecentFile { Commands = [.. _commands], Directories = [.. _directories] };
        string json = JsonSerializer.Serialize(file, RecentJsonContext.Default.RecentFile);
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
            Dirty = false;
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the original error is rethrown below; a stray temp file does no harm */ }
            throw;
        }
    }
}
