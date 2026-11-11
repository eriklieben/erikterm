using ErikTerm.Ui.Compare;

namespace ErikTerm.App.Compare;

/// <summary>
/// <c>erikterm compare A B</c>: side-by-side compare of two files in the terminal. A side is a path, or a git object
/// such as <c>main:src/a.ts</c>, <c>HEAD:./a.ts</c> or <c>:1:a.ts</c> (shown through <c>git show</c>, never a shell).
/// A path that exists always wins over the git form, so a file name containing ':' still compares as a file.
/// </summary>
internal static class CompareTool
{
    public const string Usage = """
        usage: erikterm compare <a> <b>
          a side is a file, or a git object: main:src/a.ts, HEAD:./a.ts, :1:a.ts
        """;

    public static int Start(string[] args)
    {
        if (args is ["-h" or "--help"]) { Console.WriteLine(Usage); return 0; }
        if (args.Length != 2)
        {
            Console.Error.WriteLine($"erikterm compare: expected two sides\n{Usage}");
            return 2;
        }
        return TuiHost.Run(console => Run(console, args[0], args[1], Directory.GetCurrentDirectory(), console), "compare");
    }

    /// <summary>The program behind a terminal, testable with a fake one.</summary>
    public static int Run(ITuiTerminal terminal, string left, string right, string cwd, TuiConsole? console = null)
    {
        var loop = new TuiLoop(terminal);
        console?.CatchSignals(loop.Stop);
        var screen = new CompareScreen(loop, () => Load(left, cwd), () => Load(right, cwd));
        screen.Start();
        loop.Attach(screen);
        try { loop.Run(); }
        finally { terminal.LeaveFullScreen(); }
        int signal = console?.StopSignal ?? 0;
        return signal != 0 ? signal : screen.ExitCode;
    }

    /// <summary>Reads one side; the title is what the syntax table looks at, so a git source keeps its path.</summary>
    private static CompareSide Load(string spec, string cwd)
    {
        string path = Path.GetFullPath(spec, cwd);
        if (File.Exists(path)) return LoadFile(path, spec);
        if (Directory.Exists(path)) return CompareSide.Failed(spec, "is a folder; compare works on two files");
        if (LooksLikeGitObject(spec)) return LoadGit(spec, cwd);
        return CompareSide.Failed(spec, "no such file");
    }

    private static CompareSide LoadFile(string path, string title)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > TextDocument.MaxBytes) return new CompareSide(title, "", TextDocument.TooLarge(info.Length));
            return new CompareSide(title, "", TextDocument.Decode(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CompareSide.Failed(title, ex.Message);
        }
    }

    private static CompareSide LoadGit(string spec, string cwd)
    {
        int colon = spec.LastIndexOf(':');
        string objectPath = spec[(colon + 1)..];
        string rev = spec[..colon].TrimEnd(':');
        string title = objectPath.Length > 0 ? objectPath : spec;
        var result = GitTool.Show(cwd, spec);
        if (result.ExitCode == -2) return new CompareSide(title, rev, TextDocument.TooLarge(TextDocument.MaxBytes + 1));
        if (!result.Ok) return CompareSide.Failed(spec, result.ExitCode == -1 ? "git is not available" : $"git show failed: {FirstLine(result.Stderr)}");
        return new CompareSide(title, rev.Length > 0 ? rev : "index", TextDocument.Decode(result.Stdout));
    }

    /// <summary>
    /// A git object name: something before a colon (a Windows drive letter is not git syntax, a path was already
    /// checked), or the index form <c>:path</c>, <c>:1:path</c>.
    /// </summary>
    public static bool LooksLikeGitObject(string spec)
    {
        if (spec.Length >= 2 && spec[1] == ':' && char.IsAsciiLetter(spec[0]) && (spec.Length == 2 || spec[2] is '\\' or '/')) return false;
        return spec.Contains(':');
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline];
    }
}
