using ErikTerm.App.Integrations;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>
/// Links that aren't URLs: file references (<c>path:line:col</c>, <c>File.cs(12,5)</c>, .NET stack frames, plain
/// paths) open in Neovim; commit hashes in a git repository open <c>git show</c>; <c>#123</c> in a GitHub repository
/// opens the issue/PR. Hover underlines them like URLs, Ctrl+click opens, the context menu and quick select
/// (Ctrl+label) offer the same (<see cref="WindowLinks"/> finds them through <see cref="FindSmartLinkAt"/>).
///
/// Opening a file prefers a Neovim already running in the tab (the most recently focused pane whose
/// contrib/nvim/erikterm.lua published IS_NVIM + NVIM_SERVER): <c>nvim --server S --remote-send …:drop path…</c>,
/// then that pane gets focus. Otherwise a split next to the current pane runs <c>nvim +call\ cursor(l,c) path</c>
/// ($VISUAL/$EDITOR when that isn't nvim). Only existing regular files are opened, always via argv.
/// </summary>
internal sealed class WindowEditor(
    IFeatureWindow window, BackgroundJobs jobs, PaneTracks tracks, WindowGit git, WindowGitHub github, WindowLaunches launches, Lock gate)
{
    private readonly Dictionary<string, (string? Path, long At)> _resolveCache = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (string? Path, long At)> _directoryCache = [];

    private static long Now => Environment.TickCount64;

    public EditorOpenMode Mode => EditorCommands.ParseMode(window.Settings.EditorOpen);

    /// <summary>The config was reloaded: paths may resolve differently.</summary>
    public void SettingsChanged() => _resolveCache.Clear();

    // ==== Detection (called under the terminal lock, when the pointer enters a new cell) ===========

    /// <summary>FindLinkAt hook: the smart link covering text index <paramref name="index"/>, and its span.</summary>
    public TextMatch? FindSmartLinkAt(Pane pane, string text, int index, out SmartLink link)
    {
        link = null!;
        var tr = tracks.Of(pane);
        string? cwd = tr?.Cwd ?? LinkDirectory(pane);
        var repo = tr?.Repo;

        if (Mode != EditorOpenMode.None && FindFileLinkAt(pane, text, index, cwd, repo?.Root, ref link) is { } file) return file;
        if (repo is { Status: not null } && cwd is not null && window.Settings.GitStatus && FindCommitLinkAt(text, index, repo.Root ?? cwd, ref link) is { } commit) return commit;
        if (repo is { Root: { } root, Slug: { } slug } && github.Enabled) return FindIssueLinkAt(text, index, root, slug, ref link);
        return null;
    }

    private TextMatch? FindFileLinkAt(Pane pane, string text, int index, string? cwd, string? root, ref SmartLink link)
    {
        foreach (var m in TextPatterns.FindFileRefs(text))
        {
            if (index < m.Start || index >= m.End) continue;
            // A path printed in a container is opened on the host, through its bind mounts (Containers/PaneContainer.cs).
            if (ResolveCached(pane.Container?.ToHostPath(m.Path) ?? m.Path, cwd, root) is not { } path) break;
            link = SmartLink.File(path, m.Line, m.Column, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return new TextMatch(m.Start, m.Length, MatchKind.Path);
        }
        return null;
    }

    private static TextMatch? FindCommitLinkAt(string text, int index, string repoRoot, ref SmartLink link)
    {
        foreach (var m in TextPatterns.FindHashes(text))
        {
            if (index < m.Start || index >= m.End) continue;
            link = SmartLink.Commit(m.Of(text), repoRoot);
            return m;
        }
        return null;
    }

    private static TextMatch? FindIssueLinkAt(string text, int index, string root, string slug, ref SmartLink link)
    {
        foreach (var m in TextPatterns.FindIssueRefs(text))
        {
            if (index < m.Start || index >= m.End) continue;
            int n = TextPatterns.ParseIssueNumber(m.Of(text));
            if (n <= 0) break;
            link = SmartLink.Issue(n, root, slug);
            return m;
        }
        return null;
    }

    /// <summary>
    /// Where a relative path is resolved when the integrations track no directory for the pane (git status off, or
    /// before their first scan): OSC 7, else the foreground program's or the shell's directory (PaneCwd). The /proc
    /// lookups are cached a few seconds per pane, since hovering probes on every cell. A container's shell has no
    /// such fallback: the directory of the program that runs it isn't the one the paths are printed relative to.
    /// </summary>
    private string? LinkDirectory(Pane pane)
    {
        string? reported = pane.WorkingDirectory;
        if (reported is not null || pane.Container is not null) return reported;
        long now = Now;
        if (_directoryCache.TryGetValue(pane.Id, out var hit) && now - hit.At < 3000) return hit.Path;
        if (_directoryCache.Count > 64) _directoryCache.Clear();
        string? dir = PaneTracks.PaneDirectory(pane);
        _directoryCache[pane.Id] = (dir, now);
        return dir;
    }

    /// <summary>Path resolution with a short-lived cache: hovering re-probes on every cell.</summary>
    private string? ResolveCached(string path, string? cwd, string? root)
    {
        long now = Now;
        string key = string.Concat(cwd, "\n", root, "\n", path);
        if (_resolveCache.TryGetValue(key, out var hit) && now - hit.At < 3000) return hit.Path;
        if (_resolveCache.Count > 512) _resolveCache.Clear();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? found = FileRefResolver.Resolve(path, cwd, root, home, FileRefResolver.IsRegularFile);
        _resolveCache[key] = (found, now);
        return found;
    }

    public static int SmartLinkIcon(SmartLink link) => link.Kind switch
    {
        SmartLinkKind.File => Icons.Edit,
        SmartLinkKind.Commit => RepoBadge.CommitIcon,
        _ => RepoBadge.GitHubIcon,
    };

    public string SmartLinkAction(SmartLink link) => link.Kind switch
    {
        SmartLinkKind.File => Mode switch
        {
            EditorOpenMode.System => "Click to open",
            EditorOpenMode.Split => "Click to edit in a split",
            _ => RunningNvim() is not null ? "Click to open in Neovim" : "Click to edit in a split",
        },
        SmartLinkKind.Commit => "Click to show the commit",
        _ => "Click to open on GitHub",
    };

    // ==== Opening ==============================================================================

    public void OpenSmartLink(Pane pane, SmartLink link, bool forceSplit = false)
    {
        switch (link.Kind)
        {
            case SmartLinkKind.File: OpenFile(link.Value, link.Line, link.Column, forceSplit); break;
            case SmartLinkKind.Commit:
                string cwd = link.Cwd ?? tracks.Cwd(pane) ?? ".";
                git.ResolveCommit(link.Value, cwd, full => git.ShowCommit(full, cwd));
                break;
            case SmartLinkKind.Issue:
                if (link.Cwd is { } root) github.OpenIssue(TextPatterns.ParseIssueNumber(link.Value), root);
                break;
        }
    }

    /// <summary>Opens an existing file at a line: a running Neovim in this tab, else a new split (or the system opener).</summary>
    public void OpenFile(string path, int line, int col, bool forceSplit = false)
    {
        if (!FileRefResolver.IsRegularFile(path)) { window.Toast(ToastKind.Error, "Not a file", path); return; }
        var mode = Mode;
        if (mode == EditorOpenMode.None && !forceSplit) return;
        if (mode == EditorOpenMode.System && !forceSplit)
        {
            if (ToolRunner.Launch([LinkOpener.SystemOpener(), path]) is { } error) window.Toast(ToastKind.Error, "Couldn't open the file", error);
            return;
        }
        string? editor = PreferredEditor();
        if (!forceSplit && mode == EditorOpenMode.Nvim && EditorCommands.IsNvim(editor) && RunningNvim() is { } target && ToolRunner.Find("nvim") is { } nvim)
        {
            OpenInRunningNvim(nvim, target.Pane, target.Server, editor, path, line, col);
            return;
        }
        OpenInSplit(editor, path, line, col);
    }

    private void OpenInRunningNvim(string nvim, Pane pane, string server, string? editor, string path, int line, int col)
    {
        string[] argv = EditorCommands.RemoteSendArgv(nvim, server, path, line, col);
        jobs.Run(() => ToolRunner.Run(argv, null, 3000) is { Ok: true } ? "ok" : "failed", r =>
        {
            if (r == "ok" && window.FindPane(pane.Id) is { } still)
            {
                if (window.Layout.Contains(still)) window.Focus(still);
                window.Toast(ToastKind.Info, "Opened in Neovim", FileRefResolver.ShortDisplay(FileRefResolver.Describe(path, line, col), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            }
            else OpenInSplit(editor, path, line, col); // stale socket, nvim busy: a fresh editor instead
        }, wakeLoop: true);
    }

    private void OpenInSplit(string? editor, string path, int line, int col)
    {
        var argv = EditorCommands.EditorArgv(editor ?? "nvim", path, line, col);
        if (ToolRunner.Find(argv[0]) is null && !File.Exists(argv[0])) { window.Toast(ToastKind.Error, "Editor not found", argv[0]); return; }
        launches.SplitRun(tracks.Cwd(window.Focused), argv);
    }

    /// <summary>$VISUAL / $EDITOR (set_environment_variables first, then erikterm's own environment).</summary>
    public string? PreferredEditor() => EditorCommands.PreferredEditor(name =>
        window.Settings.SetEnvironmentVariables.TryGetValue(name, out var v) ? v : Environment.GetEnvironmentVariable(name));

    /// <summary>
    /// The Neovim to send files to: in the active tab, IS_NVIM with a valid NVIM_SERVER socket, nvim in the foreground;
    /// the most recently focused one.
    /// </summary>
    public (Pane Pane, string Server)? RunningNvim()
    {
        (Pane, string)? best = null;
        long bestAt = long.MinValue;
        var focused = window.Focused;
        foreach (var p in window.Layout.LeafList)
        {
            string? server;
            lock (gate)
            {
                var vars = p.Term.UserVars;
                server = vars.TryGetValue("IS_NVIM", out var isNvim) && isNvim == "true" && vars.TryGetValue("NVIM_SERVER", out var s) ? s : null;
            }
            if (!EditorCommands.IsValidServer(server) || p.Pty.ForegroundProcessName != "nvim") continue;
            long at = p == focused ? long.MaxValue : tracks.Of(p)?.FocusedAt ?? 0;
            if (at > bestAt) { bestAt = at; best = (p, server!); }
        }
        return best;
    }

    // ==== Context menu & quick select ============================================================

    /// <summary>Context-menu hook for a smart link under the pointer.</summary>
    public void AddMenuItems(List<ContextMenu.Item> items, Pane pane, SmartLink link)
    {
        var add = new List<ContextMenu.Item>();
        switch (link.Kind)
        {
            case SmartLinkKind.File:
                string where = FileRefResolver.Describe(link.Value, link.Line, link.Column);
                string open;
                if (Mode == EditorOpenMode.System) open = "Open File";
                else open = EditorCommands.IsNvim(PreferredEditor()) ? "Open in Neovim" : "Open in Editor";
                add.Add(new(open, Icons.Edit, null, () => OpenSmartLink(pane, link), Mode != EditorOpenMode.None));
                add.Add(new("Open in New Split", Icons.SplitRight, null, () => OpenSmartLink(pane, link, forceSplit: true)));
                add.Add(new(link.Line > 0 ? "Copy path:line" : "Copy Path", Icons.Copy, null, () => Clipboard.Copy(window, where, "Copied")));
                break;
            case SmartLinkKind.Commit:
                string cwd = link.Cwd ?? tracks.Cwd(pane) ?? ".";
                add.Add(new("Show Commit", RepoBadge.CommitIcon, null, () => OpenSmartLink(pane, link)));
                add.Add(new("Copy Full Hash", Icons.Copy, null, () => git.ResolveCommit(link.Value, cwd, full => Clipboard.Copy(window, full, "Copied full hash"))));
                break;
            case SmartLinkKind.Issue:
                add.Add(new($"Open #{link.Value} on GitHub", RepoBadge.GitHubIcon, null, () => OpenSmartLink(pane, link)));
                add.Add(new("Copy Reference", Icons.Copy, null, () => Clipboard.Copy(window, link.Display, "Copied")));
                break;
        }
        add.Add(ContextMenu.Item.Separator);
        items.InsertRange(0, add);
    }

    /// <summary>Quick select with Ctrl+label: open the match. False when it isn't something we open (it's copied then).</summary>
    public bool OpenQuickSelectItem(Pane pane, QuickSelect.Item item)
    {
        string text = item.Text;
        switch (item.Kind)
        {
            case MatchKind.Url:
                Clipboard.OpenLink(window, text);
                return true;
            case MatchKind.Path:
                var tr = tracks.Of(pane);
                string? cwd = tr?.Cwd ?? tracks.Cwd(pane);
                var parsed = TextPatterns.ParseFileRef(text);
                string path = parsed?.Path ?? text;
                if (pane.Container is { } container) path = container.ToHostPath(path); // a path in the container, on the host
                if (ResolveCached(path, cwd, tr?.Repo?.Root) is { } file)
                {
                    OpenFile(file, parsed?.Line ?? 0, parsed?.Column ?? 0, forceSplit: Mode == EditorOpenMode.None);
                    return true;
                }
                window.Toast(ToastKind.Info, "No such file", text);
                return true;
            case MatchKind.Hash when tracks.Of(pane)?.Repo is { Status: not null } repo:
                string dir = repo.Root ?? repo.Cwd;
                git.ResolveCommit(text, dir, full => git.ShowCommit(full, dir));
                return true;
            default:
                return false;
        }
    }
}
