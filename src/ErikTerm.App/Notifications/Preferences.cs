using System.Text.Json;
using System.Text.Json.Serialization;

namespace ErikTerm.App.Notifications;

/// <summary>
/// Choices erikterm remembers for you without touching your config file (preferences.json in the state
/// directory). A setting in erikterm.toml always wins over what is stored here.
/// </summary>
internal sealed class PreferencesFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    /// <summary>"on" or "off" (the answer to "… wants to send desktop notifications"); null = not asked yet.</summary>
    [JsonPropertyName("os_notifications")] public string? OsNotifications { get; set; }
    /// <summary>The agent last started from New Agent Worktree (its key).</summary>
    [JsonPropertyName("worktree_agent")] public string? WorktreeAgent { get; set; }
    /// <summary>Per agent: the arguments field as last launched, where it differs from the config. Only ever applied by showing it in that field.</summary>
    [JsonPropertyName("worktree_agent_arguments")] public Dictionary<string, string>? WorktreeAgentArguments { get; set; }
    /// <summary>Per repository (its main worktree): how Finish Worktree was last done.</summary>
    [JsonPropertyName("worktree_finish_options")] public Dictionary<string, string>? WorktreeFinishOptions { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PreferencesFile))]
internal sealed partial class PreferencesJsonContext : JsonSerializerContext;

/// <summary>$XDG_STATE_HOME/erikterm/preferences.json (default ~/.local/state); Windows: %LOCALAPPDATA%\erikterm. 0600, written atomically.</summary>
internal static class PreferencesStore
{
    public const string FileName = "preferences.json";

    public static string DefaultPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erikterm", FileName);
        string state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } x && Path.IsPathRooted(x)
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(state, "erikterm", FileName);
    }

    /// <summary>The stored preferences; empty ones when the file is missing, unreadable, too large or malformed.</summary>
    public static PreferencesFile Load(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 << 10) return new PreferencesFile();
            return JsonSerializer.Deserialize(File.ReadAllText(path), PreferencesJsonContext.Default.PreferencesFile) ?? new PreferencesFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PreferencesFile();
        }
    }

    /// <summary>Writes a temporary file (0600) next to <paramref name="path"/> and renames it over the old one.</summary>
    public static void Save(string path, PreferencesFile prefs)
    {
        WriteAtomic(path, JsonSerializer.Serialize(prefs, PreferencesJsonContext.Default.PreferencesFile));
    }

    /// <summary>The directory holding <paramref name="path"/>, private to the user when this creates it.</summary>
    internal static void EnsureDirectory(string path)
    {
        string dir = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>The content in a temporary file (0600) next to <paramref name="path"/>, renamed over the old one.</summary>
    internal static void WriteAtomic(string path, string content)
    {
        EnsureDirectory(path);
        string tmp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(tmp, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
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

    public static OsNotificationMode? ParseMode(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "allow" => OsNotificationMode.On,
        "off" or "false" or "never" => OsNotificationMode.Off,
        "ask" => OsNotificationMode.Ask,
        _ => null,
    };

    public static string ModeWord(OsNotificationMode m) => m switch
    {
        OsNotificationMode.On => "on",
        OsNotificationMode.Off => "off",
        _ => "ask",
    };
}
