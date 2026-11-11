using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ErikTerm.Pty;
using ErikTerm.Ui.Compare;

namespace ErikTerm.App.Compare;

/// <summary>
/// <c>erikterm mergetool</c>: the conflict resolver. <c>LOCAL BASE REMOTE MERGED</c> resolves one file for
/// <c>git mergetool</c>; no arguments resolves every unmerged file of the repository, reading the index stages. It
/// never commits, continues or aborts the merge: a save writes the file and, in repository mode, stages that file.
/// </summary>
internal static class MergeTool
{
    public const int ExitOk = 0, ExitUnresolved = 1, ExitError = 2;

    public static int Start(string[] args)
    {
        if (args is ["-h" or "--help"]) { Console.WriteLine(Usage); return 0; }
        if (args.Length != 0 && args.Length != 4)
        {
            Console.Error.WriteLine("erikterm mergetool: expected no arguments or LOCAL BASE REMOTE MERGED\n" + Usage);
            return ExitError;
        }
        return TuiHost.Run(console => Run(console, args, Directory.GetCurrentDirectory(), console.CatchSignals, editor: null, console), "mergetool");
    }

    public const string Usage = """
        usage: erikterm mergetool                              (every unmerged file of this repository)
               erikterm mergetool LOCAL BASE REMOTE MERGED     (one file, from git mergetool)
        """;

    /// <summary>
    /// The program behind a terminal, testable with a fake one. <paramref name="editor"/> replaces launching $EDITOR
    /// (takes the seed and the file name, returns the edited text or null).
    /// </summary>
    public static int Run(ITuiTerminal terminal, string[] args, string cwd, Action<Action>? catchSignals, Func<string, string, string?>? editor, TuiConsole? console = null)
    {
        var loop = new TuiLoop(terminal);
        catchSignals?.Invoke(loop.Stop);

        MergeHost host;
        var pendingLoads = new List<(MergeEntry Entry, Func<MergeFile> Load)>();
        var session = default(MergeSession);
        try
        {
            host = args.Length == 4 ? MergeHost.ForFiles(args[3]) : MergeHost.ForRepository(cwd);
            host.AttachEditor(editor, loop, terminal);
            session = new MergeSession(host);
            host.Prepare(session, args, cwd, pendingLoads);
        }
        catch (MergeToolException ex)
        {
            terminal.LeaveFullScreen();
            Console.Error.WriteLine($"erikterm mergetool: {ex.Message}");
            return ex.ExitCode;
        }

        if (session.Entries.Count == 0)
        {
            terminal.LeaveFullScreen();
            Console.Out.WriteLine("erikterm mergetool: no unmerged files here");
            return ExitOk;
        }

        var screen = new MergeScreen(session);
        foreach (var (entry, load) in pendingLoads)
        {
            var e = entry;
            loop.Run(() =>
            {
                try { return (e.Error, File: load()); }
                catch (MergeToolException ex) { return (ex.Message, File: (MergeFile?)null); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (ex.Message, File: (MergeFile?)null); }
            }, r => { e.File = r.File; e.Error = r.File is null ? r.Item1 ?? "couldn't read this file" : null; });
        }

        loop.Attach(screen);
        try { loop.Run(); }
        finally { terminal.LeaveFullScreen(); }

        int signal = console?.StopSignal ?? 0;
        return signal != 0 ? signal : screen.ExitCode;
    }
}

public sealed class MergeToolException(string message, int exitCode = MergeTool.ExitError) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>Where a merge's results go and how the editor is launched; two instances, one per mode of the command.</summary>
internal sealed class MergeHost : IMergeHost
{
    private readonly string? _root; // repository mode
    private readonly string? _mergedPath; // file mode (the MERGED of git mergetool)
    private Func<string, string, string?>? _editorOverride;
    private TuiLoop? _loop;
    private ITuiTerminal? _terminal;

    private MergeHost(string? root, string? mergedPath)
    {
        _root = root;
        _mergedPath = mergedPath;
    }

    public static MergeHost ForFiles(string merged) => new(null, merged);

    public static MergeHost ForRepository(string cwd)
    {
        var root = GitTool.TopLevel(cwd) ?? throw new MergeToolException("not in a git repository; run it inside one, or pass LOCAL BASE REMOTE MERGED");
        return new MergeHost(root, null);
    }

    public bool RepoMode => _root is not null;

    public void AttachEditor(Func<string, string, string?>? editor, TuiLoop loop, ITuiTerminal terminal)
    {
        _editorOverride = editor;
        _loop = loop;
        _terminal = terminal;
    }

    public string? StageDescription(MergeEntry entry) => RepoMode ? $"git add -- {entry.Path}" : null;

    public void Prepare(MergeSession session, string[] args, string cwd, List<(MergeEntry, Func<MergeFile>)> loads)
    {
        if (args.Length == 4) PrepareFiles(session, args, loads);
        else PrepareRepository(session, loads);
        _ = cwd;
    }

    private static void PrepareFiles(MergeSession session, string[] args, List<(MergeEntry, Func<MergeFile>)> loads)
    {
        string local = args[0], @base = args[1], remote = args[2], merged = args[3];
        var entry = new MergeEntry(merged);
        session.Entries.Add(entry);
        loads.Add((entry, () =>
        {
            byte[] oursBytes = ReadSide(local), theirsBytes = ReadSide(remote);
            byte[] baseBytes = File.Exists(@base) ? File.ReadAllBytes(@base) : [];
            return BuildFile(merged, "ours", "theirs", oursBytes, baseBytes, theirsBytes, baseKnown: baseBytes.Length > 0);
        }
        ));
    }

    private static byte[] ReadSide(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new MergeToolException($"{path}: no such file");
        if (info.Length > TextDocument.MaxBytes) throw new MergeToolException($"{path} is {info.Length / (1024 * 1024)} MB, too large to merge here");
        return File.ReadAllBytes(path);
    }

    private static MergeFile BuildFile(string path, string oursLabel, string theirsLabel, byte[] ours, byte[] baseBytes, byte[] theirs, bool baseKnown)
    {
        var oursDoc = TextDocument.Decode(ours);
        var theirsDoc = TextDocument.Decode(theirs);
        var baseDoc = TextDocument.Decode(baseBytes);
        if (oursDoc.Problem != DocumentProblem.None || theirsDoc.Problem != DocumentProblem.None || baseDoc.Problem != DocumentProblem.None)
            return MergeFile.Binary(path, oursLabel, theirsLabel, ours, theirs);
        return MergeFile.FromText(path, oursLabel, theirsLabel, baseDoc, oursDoc, theirsDoc, baseKnown);
    }

    private void PrepareRepository(MergeSession session, List<(MergeEntry, Func<MergeFile>)> loads)
    {
        var listing = GitTool.Run(_root, "ls-files", "--unmerged", "-z");
        if (!listing.Ok) throw new MergeToolException($"git ls-files failed: {listing.Stderr}");
        var labels = MergeLabels.Read(_root!);
        foreach (var stages in UnmergedPaths.Parse(listing.Stdout))
        {
            var entry = new MergeEntry(stages.Path);
            session.Entries.Add(entry);
            var s = stages;
            loads.Add((entry, () => LoadStages(_root!, s, labels.Ours, labels.Theirs)));
        }
    }

    private static MergeFile LoadStages(string root, UnmergedPaths.Stages stages, string oursLabel, string theirsLabel)
    {
        string path = stages.Path;
        if (stages.HasGitlinkOrSymlink) throw new MergeToolException($"{path}: a symlink or submodule conflict; resolve it with git");
        byte[] Read(string oid)
        {
            var r = GitTool.Show(root, oid);
            if (r.ExitCode == -2) throw new MergeToolException($"{path} is too large to merge here (over {TextDocument.MaxBytes / (1024 * 1024)} MB)");
            if (!r.Ok) throw new MergeToolException($"{path}: git show failed: {r.Stderr}");
            return r.Stdout;
        }

        if (stages.Ours is not null && stages.Theirs is not null)
        {
            byte[] baseBytes = stages.Base is { } b ? Read(b) : [];
            return BuildFile(path, oursLabel, theirsLabel, Read(stages.Ours), baseBytes, Read(stages.Theirs), baseKnown: stages.Base is not null);
        }
        if (stages.Ours is null && stages.Theirs is not null) return MergeFile.DeleteModify(path, oursLabel, theirsLabel, DeletedSide.Ours, Read(stages.Theirs));
        if (stages.Theirs is null && stages.Ours is not null) return MergeFile.DeleteModify(path, oursLabel, theirsLabel, DeletedSide.Theirs, Read(stages.Ours));
        throw new MergeToolException($"{path}: deleted on both sides; run git rm to resolve it");
    }

    private string TargetPath(MergeEntry entry) => _mergedPath is not null ? Path.GetFullPath(_mergedPath) : Path.Combine(_root!, entry.Path.Replace('/', Path.DirectorySeparatorChar));

    public string? Save(MergeEntry entry, byte[]? content, bool stage)
    {
        string target = TargetPath(entry);
        try
        {
            if (content is null) { if (File.Exists(target)) File.Delete(target); }
            else WriteAtomic(target, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't write {entry.Path}: {ex.Message}";
        }

        if (stage && _root is not null)
        {
            var add = GitTool.Add(_root, entry.Path);
            if (!add.Ok) return $"Saved {entry.Path}, but git add failed: {add.Stderr}";
        }
        return null;
    }

    /// <summary>Same-directory temp file then rename, so a failure never leaves a half-written file; the file's mode carries over.</summary>
    private static void WriteAtomic(string target, byte[] content)
    {
        string dir = Path.GetDirectoryName(target) ?? ".";
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $".{Path.GetFileName(target)}.erikterm-{Environment.ProcessId}");
        try
        {
            File.WriteAllBytes(temp, content);
            if (!OperatingSystem.IsWindows() && File.Exists(target)) File.SetUnixFileMode(temp, File.GetUnixFileMode(target));
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* a stray temp file does no harm */ }
        }
    }

    public string? EditExternally(MergeEntry entry, string text, int line)
    {
        if (_editorOverride is not null) return _editorOverride(text, Path.GetFileName(entry.Path));

        string dir = Path.Combine(Path.GetTempPath(), $"erikterm-merge-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Path.GetFileName(entry.Path).Length == 0 ? "result" : Path.GetFileName(entry.Path));
        _terminal?.LeaveFullScreen();
        try
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
            if (!RunEditor(path, line)) return null;
            return File.ReadAllText(path);
        }
        catch (IOException) { return null; }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* a stray temp directory does no harm */ } catch (UnauthorizedAccessException) { /* a stray temp directory does no harm */ }
            _terminal?.EnterFullScreen();
            _loop?.Invalidate();
        }
    }

    private static bool RunEditor(string path, int line)
    {
        string command = Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR") ?? (OperatingSystem.IsWindows() ? "notepad" : "nvim");
        var argv = EditorCommandLine.Split(command);
        if (argv.Count == 0) return false;
        if (Integrations.ToolRunner.ProgramPath(argv[0]) is not { } program) return false;
        var psi = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (string a in argv.Skip(1)) psi.ArgumentList.Add(a);
        if (EditorCommandLine.TakesLineNumber(argv[0])) psi.ArgumentList.Add($"+{Math.Max(1, line)}");
        psi.ArgumentList.Add(path);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return false; }
    }
}

/// <summary>The labels of the two sides, read from where git keeps the operation's state.</summary>
internal static class MergeLabels
{
    public static (string Ours, string Theirs) Read(string root)
    {
        string head = Rev(root, "--abbrev-ref", "HEAD") ?? "ours";
        foreach (var (name, _) in new[] { ("MERGE_HEAD", "merge"), ("CHERRY_PICK_HEAD", "cherry-pick"), ("REVERT_HEAD", "revert") })
            if (Exists(root, name)) return (head, NameRev(root, name) ?? name);
        if (Exists(root, "REBASE_HEAD")) return ("upstream", RebaseHeadName(root) ?? NameRev(root, "REBASE_HEAD") ?? "your commit");
        return (head, "theirs");
    }

    private static bool Exists(string root, string rev) => GitTool.Run(root, "rev-parse", "-q", "--verify", rev + "^{commit}").Ok;

    private static string? Rev(string root, params string[] args)
    {
        IReadOnlyList<string> arguments = ["rev-parse", .. args];
        var r = GitTool.Run(root, arguments);
        return r.Ok && r.Text.Length > 0 ? r.Text : null;
    }

    private static string? NameRev(string root, string rev)
    {
        var r = GitTool.Run(root, "name-rev", "--name-only", "--exclude=tags/*", "--no-undefined", rev);
        return r.Ok && r.Text.Length > 0 ? r.Text.Replace("remotes/origin/", "", StringComparison.Ordinal) : null;
    }

    private static string? RebaseHeadName(string root)
    {
        var path = GitTool.Run(root, "rev-parse", "--git-path", "rebase-merge/head-name");
        if (!path.Ok) return null;
        string full = Path.GetFullPath(path.Text, root);
        if (!File.Exists(full)) return null;
        string name = File.ReadAllText(full).Trim();
        return name.StartsWith("refs/heads/", StringComparison.Ordinal) ? name["refs/heads/".Length..] : name;
    }
}

/// <summary>Parses <c>git ls-files --unmerged -z</c>: one record per stage, grouped by path.</summary>
internal static class UnmergedPaths
{
    public sealed class Stages(string path)
    {
        public string Path { get; } = path;
        public string? Base, Ours, Theirs;
        public bool HasGitlinkOrSymlink;
    }

    public static List<Stages> Parse(byte[] listing)
    {
        var result = new List<Stages>();
        var byPath = new Dictionary<string, Stages>(StringComparer.Ordinal);
        foreach (var record in Encoding.UTF8.GetString(listing).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> <oid> <stage>\t<path>"
            int tab = record.IndexOf('\t');
            if (tab < 0) continue;
            var fields = record[..tab].Split(' ');
            if (fields.Length != 3) continue;
            string path = record[(tab + 1)..];
            if (!byPath.TryGetValue(path, out var s)) { s = new Stages(path); byPath[path] = s; result.Add(s); }
            switch (fields[2])
            {
                case "1": s.Base = fields[1]; break;
                case "2": s.Ours = fields[1]; break;
                case "3": s.Theirs = fields[1]; break;
            }
            if (fields[0] is "120000" or "160000") s.HasGitlinkOrSymlink = true;
        }
        return result;
    }
}

/// <summary>Splits <c>$EDITOR</c> like a shell word list (quotes, no expansion); the program name decides whether it understands <c>+line</c>.</summary>
internal static class EditorCommandLine
{
    public static List<string> Split(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        bool hasCurrent = false;
        foreach (char c in command)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
                continue;
            }
            if (c is '"' or '\'') { quote = c; hasCurrent = true; continue; }
            if (char.IsWhiteSpace(c))
            {
                FinishPart(parts, current, ref hasCurrent);
                continue;
            }
            current.Append(c);
            hasCurrent = true;
        }
        FinishPart(parts, current, ref hasCurrent);
        return parts;
    }

    private static void FinishPart(List<string> parts, StringBuilder current, ref bool hasCurrent)
    {
        if (!hasCurrent) return;
        parts.Add(current.ToString());
        current.Clear();
        hasCurrent = false;
    }

    public static bool TakesLineNumber(string program) =>
        Path.GetFileNameWithoutExtension(program) is "nvim" or "vim" or "vi" or "nano" or "emacs" or "emacsclient" or "kak" or "micro" or "joe" or "ne";
}
