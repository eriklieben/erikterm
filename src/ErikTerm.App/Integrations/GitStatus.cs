namespace ErikTerm.App.Integrations;

/// <summary>A working tree's state from <c>git status --porcelain=v2 --branch</c>.</summary>
internal sealed record GitStatus
{
    /// <summary>Branch name; for a detached HEAD the abbreviated commit.</summary>
    public required string Branch { get; init; }
    public bool Detached { get; init; }
    public string? Oid { get; init; }
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    /// <summary>Entries with staged changes (index differs from HEAD).</summary>
    public int Staged { get; init; }
    /// <summary>Entries with unstaged changes (work tree differs from the index).</summary>
    public int Modified { get; init; }
    public int Untracked { get; init; }
    public int Conflicts { get; init; }

    public bool Clean => Staged == 0 && Modified == 0 && Untracked == 0 && Conflicts == 0;
    public int Changes => Staged + Modified + Untracked + Conflicts;
}

internal static class GitStatusParser
{
    /// <summary>
    /// Parses porcelain v2 with <c>--branch</c> headers. Returns null when there is no <c>branch.head</c> header
    /// (not porcelain v2 output).
    /// </summary>
    public static GitStatus? Parse(string output)
    {
        var s = default(Fields);
        foreach (var raw in output.AsSpan().EnumerateLines())
        {
            var line = raw;
            if (line.IsEmpty) continue;
            if (line.StartsWith("# ")) ReadHeader(line[2..], ref s);
            else ReadEntry(line, ref s);
        }
        return s.Head is null ? null : Build(s, s.Head);
    }

    /// <summary>What the lines of the output say so far.</summary>
    private struct Fields
    {
        public string? Head, Oid, Upstream;
        public int Ahead, Behind, Staged, Modified, Untracked, Conflicts;
    }

    private static void ReadHeader(ReadOnlySpan<char> h, ref Fields s)
    {
        if (h.StartsWith("branch.head ")) s.Head = h["branch.head ".Length..].ToString();
        else if (h.StartsWith("branch.oid ")) s.Oid = h["branch.oid ".Length..].ToString();
        else if (h.StartsWith("branch.upstream ")) s.Upstream = h["branch.upstream ".Length..].ToString();
        else if (h.StartsWith("branch.ab ")) ReadAheadBehind(h["branch.ab ".Length..], ref s);
    }

    private static void ReadAheadBehind(ReadOnlySpan<char> ab, ref Fields s)
    {
        int space = ab.IndexOf(' ');
        if (space <= 0) return;
        int.TryParse(ab[..space].TrimStart('+'), out s.Ahead);
        int.TryParse(ab[(space + 1)..].TrimStart('-'), out s.Behind);
    }

    private static void ReadEntry(ReadOnlySpan<char> line, ref Fields s)
    {
        switch (line[0])
        {
            case '1' or '2' when line.Length >= 4 && line[1] == ' ':
                if (line[2] != '.') s.Staged++;
                if (line[3] != '.') s.Modified++;
                break;
            case 'u': s.Conflicts++; break;
            case '?': s.Untracked++; break;
        }
    }

    private static GitStatus Build(in Fields s, string head)
    {
        bool detached = head == "(detached)";
        string branch = head;
        if (detached) branch = s.Oid is { Length: >= 7 } o && o != "(initial)" ? o[..7] : "HEAD";
        return new GitStatus
        {
            Branch = branch, Detached = detached, Oid = s.Oid == "(initial)" ? null : s.Oid, Upstream = s.Upstream,
            Ahead = Math.Max(0, s.Ahead), Behind = Math.Max(0, Math.Abs(s.Behind)),
            Staged = s.Staged, Modified = s.Modified, Untracked = s.Untracked, Conflicts = s.Conflicts,
        };
    }

    /// <summary>
    /// "owner/repo" for a github.com remote URL (https, ssh, scp-like <c>git@github.com:o/r.git</c>), else null.
    /// </summary>
    public static string? GitHubSlug(string url)
    {
        url = url.Trim();
        string? rest = null;
        #pragma warning disable S5332 // no connection is made: this recognises the forms a user's configured git remote can take, http:// among them
        foreach (var prefix in (ReadOnlySpan<string>)["https://github.com/", "http://github.com/", "ssh://git@github.com/", "git@github.com:", "ssh://github.com/", "git://github.com/"])
        #pragma warning restore S5332
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { rest = url[prefix.Length..]; break; }
        if (rest is null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && url.IndexOf("@github.com/", StringComparison.OrdinalIgnoreCase) is int at and > 0)
            rest = url[(at + "@github.com/".Length)..]; // https://user@github.com/o/r
        if (rest is null) return null;
        rest = rest.TrimEnd('/');
        if (rest.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) rest = rest[..^4];
        var parts = rest.Split('/');
        if (parts.Length != 2 || !IsSlugPart(parts[0]) || !IsSlugPart(parts[1])) return null;
        return parts[0] + "/" + parts[1];

        static bool IsSlugPart(string s) => s.Length is > 0 and <= 100 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && s != "." && s != "..";
    }

    /// <summary>
    /// The GitHub repo from <c>git config --get-regexp ^remote\..*\.url$</c> output: origin first, then upstream,
    /// then any other github.com remote.
    /// </summary>
    public static string? GitHubSlugFromRemotes(string configOutput)
    {
        string? origin = null, upstream = null, other = null;
        foreach (var raw in configOutput.Split('\n'))
        {
            var line = raw.Trim();
            int space = line.IndexOf(' ');
            if (space <= 0) continue;
            string key = line[..space], value = line[(space + 1)..];
            if (GitHubSlug(value) is not { } slug) continue;
            if (key == "remote.origin.url") origin ??= slug;
            else if (key == "remote.upstream.url") upstream ??= slug;
            else other ??= slug;
        }
        return origin ?? upstream ?? other;
    }
}
