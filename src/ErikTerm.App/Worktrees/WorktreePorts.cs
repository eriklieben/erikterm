using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErikTerm.App.Notifications;
using ErikTerm.Ui;

namespace ErikTerm.App.Worktrees;

internal sealed class WorktreePortsFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    /// <summary>Normalized worktree root to the first port of its block.</summary>
    [JsonPropertyName("blocks")] public Dictionary<string, int>? Blocks { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WorktreePortsFile))]
internal sealed partial class WorktreePortsJsonContext : JsonSerializerContext;

/// <summary>
/// A block of ports per worktree, so dev servers in several worktrees of a repository can run at once. Programs in the
/// worktree's panes read <see cref="BaseVariable"/> and <see cref="CountVariable"/>. Blocks live in
/// worktree-ports.json in the state directory and belong to a worktree for good: one is only taken back when the range
/// is full and its directory is gone. The file is rewritten under a lock, because several erikterm processes share it.
/// </summary>
internal sealed class WorktreePorts(string statePath, Func<string, bool>? directoryExists = null, int lockWaitMs = 500)
{
    /// <summary>Below is launchSettings' fixed range (15000-22999), from 32768 Linux hands out ephemeral ports.</summary>
    public const int RangeStart = 23000, RangeEnd = 32000;
    /// <summary>Room for a whole stack (web, API, database, cache, workers) and still 450 blocks.</summary>
    public const int BlockSize = 20;
    public const string BaseVariable = "ERIKTERM_PORT_BASE", CountVariable = "ERIKTERM_PORT_COUNT";
    public const string FileName = "worktree-ports.json";

    private readonly Func<string, bool> _directoryExists = directoryExists ?? Directory.Exists;

    /// <summary>The process's registry, next to preferences.json.</summary>
    public static WorktreePorts Shared { get; } = new(Path.Combine(Path.GetDirectoryName(PreferencesStore.DefaultPath())!, FileName));

    /// <summary>
    /// Hook from spawning a pane's program in <paramref name="directory"/> (null: erikterm's own). Outside a worktree the
    /// variables are removed, so a pane can't inherit the block of the erikterm that started this one.
    /// </summary>
    public void AddEnvironment(Dictionary<string, string> env, string? directory)
    {
        env.Remove(BaseVariable);
        env.Remove(CountVariable);
        if (WorktreeRoot(directory ?? Environment.CurrentDirectory) is not { } root || BlockFor(root) is not { } first) return;
        env[BaseVariable] = first.ToString(CultureInfo.InvariantCulture);
        env[CountVariable] = BlockSize.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The deepest directory at or above <paramref name="directory"/> with a <c>.git</c> entry (a linked worktree's is a file). No git: this runs while a pane starts.</summary>
    public static string? WorktreeRoot(string directory)
    {
        try
        {
            for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
            {
                string marker = Path.Combine(dir.FullName, ".git");
                if (File.Exists(marker) || Directory.Exists(marker)) return dir.FullName;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { /* not a path we can walk: not in a worktree */ }
        return null;
    }

    /// <summary>The first port of the worktree's block, or null when none could be recorded: a block that isn't on disk can't be promised to stay.</summary>
    public int? BlockFor(string root)
    {
        string key = WorktreeList.Normalize(root, OperatingSystem.IsWindows());
        try
        {
            if (Read().TryGetValue(key, out int known)) return known;
            using var held = TakeLock();
            var blocks = Read(); // another process may have added it meanwhile
            if (blocks.TryGetValue(key, out known)) return known;
            int? free = FirstFree(blocks.Values);
            if (free is null)
            {
                foreach (string gone in blocks.Keys.Where(k => !_directoryExists(k)).ToList()) blocks.Remove(gone);
                free = FirstFree(blocks.Values);
            }
            if (free is not { } first) return null;
            blocks[key] = first;
            PreferencesStore.WriteAtomic(statePath, JsonSerializer.Serialize(new WorktreePortsFile { Blocks = blocks }, WorktreePortsJsonContext.Default.WorktreePortsFile));
            return first;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static int? FirstFree(IEnumerable<int> used)
    {
        var taken = used.ToHashSet();
        for (int first = RangeStart; first + BlockSize <= RangeEnd; first += BlockSize)
            if (!taken.Contains(first)) return first;
        return null;
    }

    private static bool IsBlockStart(int port) => port >= RangeStart && port + BlockSize <= RangeEnd && (port - RangeStart) % BlockSize == 0;

    /// <summary>What the file says, leaving out anything that isn't a block or claims one twice.</summary>
    private Dictionary<string, int> Read()
    {
        var blocks = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(statePath)) return blocks;
        WorktreePortsFile? file;
        try { file = JsonSerializer.Deserialize(File.ReadAllText(statePath), WorktreePortsJsonContext.Default.WorktreePortsFile); }
        catch (JsonException) { return blocks; }
        var seen = new HashSet<int>();
        foreach (var (key, first) in file?.Blocks ?? [])
            if (IsBlockStart(first) && seen.Add(first)) blocks[key] = first;
        return blocks;
    }

    /// <summary>Held while it stays open. Past the wait the IOException stands: the pane starts without a block rather than late.</summary>
    private FileStream TakeLock()
    {
        PreferencesStore.EnsureDirectory(statePath);
        long giveUp = Environment.TickCount64 + lockWaitMs;
        while (true)
        {
            try { return new FileStream(statePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < giveUp) { Thread.Sleep(10); }
        }
    }
}
