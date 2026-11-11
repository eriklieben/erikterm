namespace ErikTerm.App.Integrations;

internal enum SmartLinkKind : byte { File, Commit, Issue }

/// <summary>
/// A detected link that isn't a URL: a file reference (resolved to an existing absolute path), a commit hash in a git
/// repository pane, or a <c>#123</c> issue/PR number in a GitHub repository pane. <see cref="Key"/> identifies it for
/// hover comparisons; <see cref="Display"/> is what the hint pill shows.
/// </summary>
internal sealed record SmartLink(SmartLinkKind Kind, string Key, string Display)
{
    /// <summary>File: the absolute path. Commit: the hash as printed. Issue: the number.</summary>
    public string Value { get; init; } = "";
    public int Line { get; init; }
    public int Column { get; init; }
    /// <summary>Directory to run git / gh in (the pane's, or the repository root).</summary>
    public string? Cwd { get; init; }

    public static SmartLink File(string path, int line, int col, string? home = null) =>
        new(SmartLinkKind.File, "file:" + FileRefResolver.Describe(path, line, col), FileRefResolver.ShortDisplay(FileRefResolver.Describe(path, line, col), home))
        { Value = path, Line = line, Column = col };

    public static SmartLink Commit(string hash, string cwd) =>
        new(SmartLinkKind.Commit, "commit:" + hash, "commit " + hash) { Value = hash, Cwd = cwd };

    public static SmartLink Issue(int number, string root, string slug) =>
        new(SmartLinkKind.Issue, "issue:" + number, $"{slug}#{number}") { Value = number.ToString(), Cwd = root };
}
