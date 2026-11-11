namespace ErikTerm.App.Integrations;

/// <summary>
/// Turns a path printed in a pane into an existing file: <c>~</c> expands, relative paths resolve against the pane's
/// directory (then the repository root, then without git's <c>a/</c> / <c>b/</c> diff prefixes). Only regular files
/// count (see <see cref="IsRegularFile"/>). Pure apart from the injected file test, so it is unit-tested.
/// </summary>
internal static class FileRefResolver
{
    public const int MaxPathLength = 4096;

    /// <summary>The absolute path of an existing regular file, or null.</summary>
    public static string? Resolve(string path, string? cwd, string? repoRoot, string? home, Func<string, bool> isFile)
    {
        if (path.Length == 0 || path.Length > MaxPathLength) return null;
        if (path.Any(char.IsControl)) return null;
        if (UnwrapFileUri(path) is not { } unwrapped) return null;
        if (ExpandHome(unwrapped, home) is not { } local) return null;
        if (Path.IsPathRooted(local)) return Check(local, isFile);
        return ResolveRelative(local, cwd, repoRoot, isFile);
    }

    /// <summary>A file:// URI's local path, the path as it is when it isn't one, or null for a URI that isn't a file.</summary>
    private static string? UnwrapFileUri(string path)
    {
        if (!path.StartsWith("file://", StringComparison.Ordinal)) return path;
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
    }

    /// <summary>Expands a leading <c>~</c>; null when the path has one but there is no home directory.</summary>
    private static string? ExpandHome(string path, string? home)
    {
        if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal)) return path;
        return string.IsNullOrEmpty(home) ? null : home + path[1..];
    }

    private static string? ResolveRelative(string path, string? cwd, string? repoRoot, Func<string, bool> isFile)
    {
        foreach (var dir in (ReadOnlySpan<string?>)[cwd, repoRoot])
        {
            if (string.IsNullOrEmpty(dir) || !Path.IsPathRooted(dir)) continue;
            if (Check(Path.Combine(dir, path), isFile) is { } found) return found;
            // `git diff` headers: "--- a/src/x.cs" / "+++ b/src/x.cs".
            if (path.Length > 2 && path[1] == '/' && path[0] is 'a' or 'b' && Check(Path.Combine(dir, path[2..]), isFile) is { } stripped)
                return stripped;
        }
        return null;
    }

    private static string? Check(string candidate, Func<string, bool> isFile)
    {
        string full;
        try { full = Path.GetFullPath(candidate); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return isFile(full) ? full : null;
    }

    /// <summary>
    /// A non-empty regular file outside /proc, /sys and /dev (devices, FIFOs and sockets report a size of 0, and an
    /// editor opening a FIFO would hang). Follows symlinks.
    /// </summary>
    public static bool IsRegularFile(string path)
    {
        if (IsVirtual(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            if (info.LinkTarget is not null)
            {
                if (info.ResolveLinkTarget(returnFinalTarget: true) is not FileInfo target || !target.Exists || IsVirtual(target.FullName)) return false;
                info = target;
            }
            return (info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) == 0 && info.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool IsVirtual(string path) =>
        path.StartsWith("/proc/", StringComparison.Ordinal) || path.StartsWith("/sys/", StringComparison.Ordinal)
        || path.StartsWith("/dev/", StringComparison.Ordinal);

    /// <summary>A path as the status and worktree overlays show it: ~ for the home directory.</summary>
    public static string HomeRelative(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 1 && path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }

    /// <summary>For hints and toasts: ~ for the home directory, and long paths keep their tail (…/src/App.cs:12).</summary>
    public static string ShortDisplay(string path, string? home, int max = 56)
    {
        if (!string.IsNullOrEmpty(home) && home.Length > 1 && (path == home || path.StartsWith(home + "/", StringComparison.Ordinal)))
            path = "~" + path[home.Length..];
        if (path.Length <= max) return path;
        int start = path.Length - (max - 1);
        int slash = path.IndexOf('/', start);
        return "…" + (slash >= 0 && slash < path.Length - 1 ? path[slash..] : path[start..]);
    }

    /// <summary>"path:line:col" as shown and copied (line/col omitted when 0).</summary>
    public static string Describe(string path, int line, int col)
    {
        if (line <= 0) return path;
        return col <= 0 ? $"{path}:{line}" : $"{path}:{line}:{col}";
    }
}
