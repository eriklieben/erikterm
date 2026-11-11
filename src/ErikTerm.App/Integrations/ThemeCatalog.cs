using System.Text;
using ErikTerm.Config;

namespace ErikTerm.App.Integrations;

/// <summary>
/// Browse Themes… › Download: the wezterm-format schemes of <see href="https://github.com/mbadolato/iTerm2-Color-Schemes">
/// iTerm2-Color-Schemes</see> (MIT) into their own folder under the colors directory, where the scheme watcher picks them
/// up. Opt-in, one confirmation first (Windowing/WindowThemes.cs). Fetched with the user's <c>git</c> (a shallow, sparse clone)
/// rather than an HTTP client, which would add megabytes to the binary for one optional download. What arrives is data:
/// HTTPS to one fixed URL, no redirects, no hooks, symlinks neither created nor followed, per-file and overall size caps,
/// file names checked, each file parsed by the normal scheme-file parser before it is kept, and nothing is ever executed.
/// The new folder replaces the old one in one rename.
/// </summary>
internal static class ThemeCatalog
{
    public const string Repository = "mbadolato/iTerm2-Color-Schemes";
    public const string Branch = "master";
    #pragma warning disable S1075 // the one upstream of the theme catalog, pinned on purpose; a setting would let a config point the download elsewhere
    public const string CloneUrl = "https://github.com/" + Repository + ".git";
    #pragma warning restore S1075
    /// <summary>The folder of the repository holding the wezterm-format schemes.</summary>
    public const string SchemeFolder = "wezterm";

    public const int MaxFiles = 2000, MaxFileBytes = 64 * 1024, MaxLicenseBytes = 64 * 1024;
    public const long MaxTotalBytes = 32 * 1024 * 1024;
    private const int GitTimeoutMs = 180_000;

    /// <summary>What a download came to: schemes saved, files that weren't usable, or why nothing was saved.</summary>
    public sealed record Result(int Saved, int Skipped, string? Error);

    /// <summary>A plain <c>*.toml</c> name for our folder: no directories, no dot files, no control characters, reasonably short.</summary>
    public static bool SafeFileName(string name) =>
        name.Length is > 5 and <= 128
        && name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)
        && name[0] != '.'
        && name.IndexOfAny(['/', '\\', ':']) < 0
        && !name.Any(char.IsControl)
        && name == Path.GetFileName(name);

    /// <summary>Options for every git call: HTTPS only, no redirects, no hooks, no credentials, no symlinks in the checkout.</summary>
    private static string[] HardenedGit() =>
    [
        "git",
        "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
        "-c", "http.followRedirects=false",
        "-c", "core.symlinks=false",
        "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
        "-c", "credential.helper=",
    ];

    /// <summary>The command that clones only the repository's top level into <paramref name="checkout"/>, without the file contents yet.</summary>
    public static string[] CloneCommand(string checkout) =>
        [.. HardenedGit(), "clone", "--quiet", "--depth", "1", "--single-branch", "--branch", Branch, "--no-tags",
            "--filter=blob:none", "--sparse", "--", CloneUrl, checkout];

    /// <summary>The command that then checks out the scheme folder.</summary>
    public static string[] SparseCommand(string checkout) =>
        [.. HardenedGit(), "-C", checkout, "sparse-checkout", "set", "--", SchemeFolder];

    /// <summary>Fetches the catalogue into <paramref name="checkout"/> with git; the error to show, or null.</summary>
    public static string? GitFetch(string checkout)
    {
        if (!ToolRunner.Has("git")) return "git isn't installed";
        foreach (var argv in new[] { CloneCommand(checkout), SparseCommand(checkout) })
        {
            var r = ToolRunner.Run(argv, null, GitTimeoutMs);
            if (r.TimedOut) return "timed out talking to GitHub";
            if (r.StartError is { } start) return start;
            if (r.ExitCode != 0) return LastLine(r.Stderr) ?? $"git exited with {r.ExitCode}";
        }
        return null;
    }

    /// <summary>The last line of git's error output, without control characters and reasonably short.</summary>
    internal static string? LastLine(string stderr)
    {
        string? last = stderr.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (last is null) return null;
        string clean = new(last.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length > 200 ? clean[..200] : clean;
    }

    /// <summary>
    /// Fetches the catalogue with <paramref name="fetch"/> (given an empty folder; returns an error or null) and installs
    /// its schemes into <paramref name="targetDir"/>, replacing it once all is checked.
    /// <paramref name="progress"/> gets (done, total) as files are checked.
    /// </summary>
    public static Result Download(Func<string, string?> fetch, string targetDir, Action<int, int>? progress)
    {
        string tmp = Beside(targetDir, "tmp"), checkout = Beside(targetDir, "clone");
        try
        {
            DeleteTree(checkout);
            if (fetch(checkout) is { } error) return new Result(0, 0, error);

            string folder = Path.Combine(checkout, SchemeFolder);
            var names = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f)).Where(SafeFileName)
                    .Order(StringComparer.Ordinal).Take(MaxFiles).ToList()
                : [];
            if (names.Count == 0) return new Result(0, 0, "no scheme files arrived from GitHub");

            DeleteTree(tmp);
            Directory.CreateDirectory(tmp);
            int saved = 0, skipped = 0, done = 0;
            long total = 0;
            foreach (string name in names)
            {
                string source = Path.Combine(folder, name);
                if (ReadRegularFile(source, MaxFileBytes) is { } bytes && (total += bytes.Length) <= MaxTotalBytes
                    && ColorSchemeFiles.Parse(Encoding.UTF8.GetString(bytes), name, []) is not null)
                {
                    File.WriteAllBytes(Path.Combine(tmp, name), bytes);
                    saved++;
                }
                else skipped++;
                progress?.Invoke(++done, names.Count);
            }
            if (saved == 0) return new Result(0, skipped, "no usable scheme files arrived");

            // The license travels with the files.
            if (ReadRegularFile(Path.Combine(checkout, "LICENSE"), MaxLicenseBytes) is { } license)
                File.WriteAllBytes(Path.Combine(tmp, "LICENSE"), license);

            Replace(targetDir, tmp);
            return new Result(saved, skipped, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(0, 0, ex.Message);
        }
        finally
        {
            DeleteTree(checkout);
            DeleteTree(tmp);
        }
    }

    /// <summary>The bytes of a regular file of at most <paramref name="maxBytes"/>; null for a link, a missing file or a larger one.</summary>
    private static byte[]? ReadRegularFile(string path, int maxBytes)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LinkTarget is null && info.Length <= maxBytes ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Removes a folder if it's there, best effort; git marks its object files read-only on Windows.</summary>
    private static void DeleteTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            if (OperatingSystem.IsWindows())
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort: a leftover working folder does no harm */ }
    }

    /// <summary>
    /// A working folder next to the colors directory, not in it: that directory is watched, and each change there would
    /// reload the configuration mid-download. On the same file system, so the final move is a rename.
    /// </summary>
    private static string Beside(string targetDir, string kind)
    {
        string colors = Path.GetDirectoryName(targetDir)!;
        return Path.Combine(Path.GetDirectoryName(colors) ?? colors, $".{Path.GetFileName(targetDir)}.{kind}-{Environment.ProcessId}");
    }

    /// <summary>Swaps the finished folder in: the old one is moved aside, the new one renamed into place, the old one deleted.</summary>
    private static void Replace(string targetDir, string tmp)
    {
        string old = Beside(targetDir, "old");
        Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
        bool hadOld = Directory.Exists(targetDir);
        if (hadOld) Directory.Move(targetDir, old);
        Directory.Move(tmp, targetDir);
        if (hadOld) Directory.Delete(old, recursive: true);
    }
}
