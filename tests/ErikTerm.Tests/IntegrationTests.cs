using ErikTerm.App.Integrations;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

public class FileRefPatternTests
{
    private static (string Text, string Path, int Line, int Col)[] Spans(string text, List<FileRefMatch> found) =>
        found.Select(m => (text.Substring(m.Start, m.Length), m.Path, m.Line, m.Column)).ToArray();

    [Test]
    public async Task MsBuildError()
    {
        // Arrange
        const string text = "/home/erik/src/App/Program.cs(12,5): error CS1002: ; expected [/home/erik/src/App/App.csproj]";

        // Act
        var found = TextPatterns.FindFileRefs(text);
        var refs = Spans(text, found);

        // Assert
        await Assert.That(refs).Contains(("/home/erik/src/App/Program.cs(12,5)", "/home/erik/src/App/Program.cs", 12, 5));
        await Assert.That(refs).Contains(x => x.Path == "/home/erik/src/App/App.csproj" && x.Line == 0);
        await Assert.That(found[0].Strong).IsTrue();
    }

    [Test]
    [Arguments("Program.cs(7): warning CS0168", "Program.cs", 7, 0, true)]
    [Arguments("src/Foo.ts(3,14): error TS2322", "src/Foo.ts", 3, 14, true)]
    [Arguments("Foo.cs(1,2,1,9): error", "Foo.cs", 1, 2, true)]
    [Arguments("Console.WriteLine(42)", "Console.WriteLine", 42, 0, false)] // only a link if such a file exists
    public async Task MsBuildForms(string text, string path, int line, int col, bool strong)
    {
        // Act
        var found = TextPatterns.FindFileRefs(text);

        // Assert
        var m = await Assert.That(found).HasSingleItem(f => f.Line > 0);
        await Assert.That((m.Path, m.Line, m.Column, m.Strong)).IsEqualTo((path, line, col, strong));
    }

    [Test]
    public async Task DotnetStackFrame()
    {
        // Arrange
        const string text = "   at Erik.App.Run() in /home/erik/My Project/App.cs:line 42";

        // Act
        var found = TextPatterns.FindFileRefs(text);

        // Assert
        var m = await Assert.That(found).HasSingleItem(f => f.Line > 0);
        await Assert.That(m.Path).IsEqualTo("/home/erik/My Project/App.cs");
        await Assert.That(m.Line).IsEqualTo(42);
        await Assert.That(text.Substring(m.Start, m.Length)).IsEqualTo("/home/erik/My Project/App.cs:line 42");
    }

    [Test]
    public async Task PythonTraceback()
    {
        // Act
        var found = TextPatterns.FindFileRefs("  File \"/tmp/x.py\", line 3, in <module>");

        // Assert
        var m = await Assert.That(found).HasSingleItem(f => f.Line > 0);
        await Assert.That((m.Path, m.Line)).IsEqualTo(("/tmp/x.py", 3));
    }

    [Test]
    [Arguments("src/App.cs:42:7: error: nope", "src/App.cs", 42, 7)]
    [Arguments("./a/b.rs:10", "./a/b.rs", 10, 0)]
    [Arguments("~/notes.md:3", "~/notes.md", 3, 0)]
    [Arguments("/abs/path/x.go:1:2", "/abs/path/x.go", 1, 2)]
    [Arguments("Program.cs:12", "Program.cs", 12, 0)]
    public async Task ColonForms(string text, string path, int line, int col)
    {
        // Act
        var m = TextPatterns.FindFileRefs(text)[0];

        // Assert
        await Assert.That((m.Path, m.Line, m.Column)).IsEqualTo((path, line, col));
    }

    [Test]
    [Arguments("see README.md for details", "README.md")]
    [Arguments("edit src/ErikTerm.App/App.cs.", "src/ErikTerm.App/App.cs")]
    [Arguments("ls ~/.config/erikterm/erikterm.toml", "~/.config/erikterm/erikterm.toml")]
    public async Task PlainPaths(string text, string path)
    {
        // Act
        var found = TextPatterns.FindFileRefs(text);

        // Assert
        await Assert.That(found).Contains(m => m.Path == path && m.Line == 0);
    }

    [Test]
    [Arguments("at 12:34:56 nothing")]
    [Arguments("version 1.2.3 released")]
    [Arguments("just words here")]
    public async Task NoFileRefs(string text)
    {
        // Act
        var found = TextPatterns.FindFileRefs(text);

        // Assert
        await Assert.That(found).IsEmpty();
    }

    [Test]
    public async Task NotInsideUrls()
    {
        // Act
        var found = TextPatterns.FindFileRefs("https://github.com/o/r/blob/main/src/App.cs:12");

        // Assert
        await Assert.That(found).DoesNotContain(m => m.Line == 12);
    }

    [Test]
    public async Task QuickSelectOffersMsBuildRefsAsPaths()
    {
        // Arrange
        const string text = "/p/File.cs(12,5): error CS1002";

        // Act
        var q = TextPatterns.FindQuickSelect(text).Select(m => (m.Kind, m.Of(text))).ToArray();

        // Assert
        await Assert.That(q).Contains((MatchKind.Path, "/p/File.cs(12,5)"));
    }

    [Test]
    [Arguments("/p/File.cs(12,5)", "/p/File.cs", 12, 5)]
    [Arguments("src/a.cs:3:4", "src/a.cs", 3, 4)]
    [Arguments("/p/My App/App.cs:line 9", "/p/My App/App.cs", 9, 0)] // a stack-frame span, as quick select copies it
    [Arguments("/tmp/x.py\", line 3", "/tmp/x.py", 3, 0)]
    public async Task ParseFileRefOfAMatch(string text, string path, int line, int col)
    {
        // Act
        var parsed = TextPatterns.ParseFileRef(text);

        // Assert
        var m = await Assert.That(parsed).IsNotNull();
        await Assert.That((m.Path, m.Line, m.Column)).IsEqualTo((path, line, col));
    }

    [Test]
    [Arguments("3f2a9c1", true)]
    [Arguments("3f2a9c1d3f2a9c1d3f2a9c1d3f2a9c1d3f2a9c1d", true)]
    [Arguments("3F2A9C1", false)]
    [Arguments("3f2a9c", false)]
    [Arguments("3f2a9c1;rm", false)]
    [Arguments("--output=x", false)]
    public async Task CommitHashValidation(string s, bool ok)
    {
        // Act
        bool isHash = TextPatterns.IsCommitHash(s);

        // Assert
        await Assert.That(isHash).IsEqualTo(ok);
    }

    [Test]
    public async Task IssueReferences()
    {
        // Arrange
        const string text = "fixes #123, see #45 and color #fff, &#38; url#7 and #0";

        // Act
        var found = TextPatterns.FindIssueRefs(text).Select(m => m.Of(text)).ToArray();

        // Assert
        await Assert.That(found).IsEquivalentTo(["#123", "#45"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("#123", 123)]
    [Arguments("#12a", 0)]
    [Arguments("#012", 0)]
    [Arguments("#12345678", 0)]
    public async Task IssueNumbers(string s, int expected)
    {
        // Act
        int number = TextPatterns.ParseIssueNumber(s);

        // Assert
        await Assert.That(number).IsEqualTo(expected);
    }
}

public class FileRefResolverTests
{
    // Resolve returns Path.GetFullPath's result, which on Windows is D:\\work\\repo\\... for /work/repo/...: the fake file
    // system and the expected values go through it too.
    private static readonly HashSet<string> Files =
        [.. ((string[])["/work/repo/src/App.cs", "/work/repo/README.md", "/home/me/notes.md", "/work/repo/sub/x.cs"]).Select(Path.GetFullPath)];
    private static bool Exists(string p) => Files.Contains(p);

    private string? _dir;

    /// <summary>A fresh directory holding a non-empty <c>a.cs</c> and an empty <c>e.cs</c>; removed after the test.</summary>
    private string TempDirWithFiles()
    {
        _dir = Directory.CreateTempSubdirectory("erikterm-resolve").FullName;
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "x");
        File.WriteAllText(Path.Combine(_dir, "e.cs"), "");
        return _dir;
    }

    [After(Test)]
    public void Cleanup()
    {
        if (_dir is not null) Directory.Delete(_dir, true);
    }

    [Test]
    [Arguments("src/App.cs", "/work/repo", "/work/repo/src/App.cs")]
    [Arguments("./src/../src/App.cs", "/work/repo", "/work/repo/src/App.cs")]
    [Arguments("/work/repo/README.md", "/elsewhere", "/work/repo/README.md")]
    [Arguments("~/notes.md", "/x", "/home/me/notes.md")]
    [Arguments("x.cs", "/work/repo/sub", "/work/repo/sub/x.cs")]
    [Arguments("src/App.cs", "/work/repo/sub", "/work/repo/src/App.cs")]   // relative to the repository root
    [Arguments("b/src/App.cs", "/work/repo", "/work/repo/src/App.cs")]     // git diff prefix
    [Arguments("a/README.md", "/work/repo/sub", "/work/repo/README.md")]
    public async Task Resolves(string path, string cwd, string expected)
    {
        // Act
        string? resolved = FileRefResolver.Resolve(path, cwd, "/work/repo", "/home/me", Exists);

        // Assert
        await Assert.That(resolved).IsEqualTo(Path.GetFullPath(expected));
    }

    [Test]
    [Arguments("missing.cs")]
    [Arguments("src/App.cs\nrm -rf")]
    [Arguments("")]
    public async Task RejectsMissingOrOdd(string path)
    {
        // Act
        string? resolved = FileRefResolver.Resolve(path, "/work/repo", "/work/repo", "/home/me", Exists);

        // Assert
        await Assert.That(resolved).IsNull();
    }

    [Test]
    public async Task RelativeWithoutAnyDirectoryIsNull()
    {
        // Act
        string? resolved = FileRefResolver.Resolve("src/App.cs", null, null, "/home/me", Exists);

        // Assert
        await Assert.That(resolved).IsNull();
    }

    [Test]
    [Arguments("a.cs", true)]
    [Arguments("e.cs", false)] // empty
    public async Task OnlyNonEmptyRegularFiles(string name, bool regular)
    {
        // Arrange
        string dir = TempDirWithFiles();

        // Act
        bool isFile = FileRefResolver.IsRegularFile(Path.Combine(dir, name));

        // Assert
        await Assert.That(isFile).IsEqualTo(regular);
    }

    [Test]
    public async Task DirectoriesAreNotRegularFiles()
    {
        // Arrange
        string dir = TempDirWithFiles();

        // Act
        bool isFile = FileRefResolver.IsRegularFile(dir);

        // Assert
        await Assert.That(isFile).IsFalse();
    }

    [Test]
    [Arguments("/proc/self/status")]
    [Arguments("/dev/null")]
    public async Task SpecialFilesAreNotRegularFiles(string path)
    {
        // Act
        bool isFile = FileRefResolver.IsRegularFile(path);

        // Assert
        await Assert.That(isFile).IsFalse();
    }

    [Test]
    public async Task ResolvesAgainstTheRealFileSystem()
    {
        // Arrange
        string dir = TempDirWithFiles();

        // Act
        string? resolved = FileRefResolver.Resolve("a.cs", dir, null, null, FileRefResolver.IsRegularFile);

        // Assert
        await Assert.That(resolved).IsEqualTo(Path.Combine(dir, "a.cs"));
    }

    [Test]
    [Arguments("/home/me/src/App.cs:3", "~/src/App.cs:3")]
    [Arguments("/home/meow/x.cs", "/home/meow/x.cs")]
    [Arguments("/home/me/Repository/erikterm/.claude/worktrees/agent-123456/src/ErikTerm.App/App.cs:12", "…/worktrees/agent-123456/src/ErikTerm.App/App.cs:12")]
    public async Task ShortDisplay(string path, string expected)
    {
        // Act
        string shown = FileRefResolver.ShortDisplay(path, "/home/me", 52);

        // Assert
        await Assert.That(shown).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0, 0, "/a.cs")]
    [Arguments(3, 0, "/a.cs:3")]
    [Arguments(3, 4, "/a.cs:3:4")]
    public async Task Describe(int line, int col, string expected)
    {
        // Act
        string described = FileRefResolver.Describe("/a.cs", line, col);

        // Assert
        await Assert.That(described).IsEqualTo(expected);
    }
}

// Internal: the editor open mode (an app type) is a test parameter.
internal class EditorCommandTests
{
    [Test]
    [Arguments("/p/File.cs", "/p/File.cs")]
    [Arguments("/p/My File.cs", @"/p/My\ File.cs")]
    [Arguments("/p/a|b%#$x.cs", @"/p/a\|b\%\#\$x.cs")]
    [Arguments("/p/it's \"q\".cs", @"/p/it\'s\ \""q\"".cs")]
    [Arguments("+x", @"\+x")]
    [Arguments("/p/[x]*?.cs", @"/p/\[x]\*\?.cs")]
    public async Task FnameEscape(string path, string expected)
    {
        // Act
        string escaped = EditorCommands.FnameEscape(path);

        // Assert
        await Assert.That(escaped).IsEqualTo(expected);
    }

    [Test]
    public async Task RemoteSendKeysEscapeKeyNotation()
    {
        // Act
        string keys = EditorCommands.RemoteSendKeys("/p/<CR>:q!.cs", 12, 5);
        int carriageReturns = keys.Split("<CR>").Length - 1;

        // Assert
        await Assert.That(keys).IsEqualTo(@"<C-\><C-N>:<C-U>drop /p/\<lt>CR>:q\!.cs<CR>:<C-U>call cursor(12,5) | execute 'normal! zvzz' | echo ''<CR>");
        // Exactly the intended <CR>s: the one in the path is neutralized.
        await Assert.That(carriageReturns).IsEqualTo(2);
    }

    [Test]
    public async Task RemoteSendWithoutLine()
    {
        // Act
        string keys = EditorCommands.RemoteSendKeys("/p/a b.cs", 0, 0);

        // Assert
        await Assert.That(keys).IsEqualTo(@"<C-\><C-N>:<C-U>drop /p/a\ b.cs<CR>");
    }

    [Test]
    public async Task RemoteSendArgv()
    {
        // Act
        string[] argv = EditorCommands.RemoteSendArgv("/usr/bin/nvim", "/run/user/1000/nvim.1.0", "/a.cs", 3, 0);

        // Assert
        await Assert.That(argv).IsEquivalentTo(
            ["/usr/bin/nvim", "--server", "/run/user/1000/nvim.1.0", "--remote-send", @"<C-\><C-N>:<C-U>drop /a.cs<CR>:<C-U>call cursor(3,1) | execute 'normal! zvzz' | echo ''<CR>"],
            CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("nvim", new[] { "nvim", "+call cursor(12,5)", "/a.cs" })]
    [Arguments("vim", new[] { "vim", "+call cursor(12,5)", "/a.cs" })]
    [Arguments("code -w", new[] { "code", "-w", "+12", "/a.cs" })]
    [Arguments("nano", new[] { "nano", "+12", "/a.cs" })]
    [Arguments("hx", new[] { "hx", "/a.cs:12:5" })]
    [Arguments(null, new[] { "nvim", "+call cursor(12,5)", "/a.cs" })]
    public async Task EditorArgv(string? editor, string[] expected)
    {
        // Act
        string[] argv = EditorCommands.EditorArgv(editor, "/a.cs", 12, 5);

        // Assert
        await Assert.That(argv).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task EditFileUsesTheEditorWhenItIsThere()
    {
        // Act
        string[]? argv = EditorCommands.EditFileArgv("code -w", @"C:\Users\me\erikterm.toml", windows: true, found: _ => true);

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[] { "code", "-w", @"C:\Users\me\erikterm.toml" });
    }

    [Test]
    public async Task EditFileFallsBackToViOnPosix()
    {
        // Act
        string[]? argv = EditorCommands.EditFileArgv(null, "/home/me/erikterm.toml", windows: false, found: _ => false);

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[] { "vi", "/home/me/erikterm.toml" });
    }

    [Test]
    public async Task EditFileOnWindowsWithoutAnEditorLeavesItToTheSystem()
    {
        // Act
        string[]? argv = EditorCommands.EditFileArgv(null, @"C:\Users\me\erikterm.toml", windows: true, found: _ => false);

        // Assert
        await Assert.That(argv).IsNull();
    }

    [Test]
    public async Task EditorArgvWithoutLine()
    {
        // Act
        string[] argv = EditorCommands.EditorArgv("nvim", "/a.cs", 0, 0);

        // Assert
        await Assert.That(argv).IsEquivalentTo(["nvim", "/a.cs"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("/run/user/1000/nvim.123.0", true)]
    [Arguments("127.0.0.1:6666", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    [Arguments("/tmp/../etc/x", false)]
    [Arguments("/tmp/a\nb", false)]
    public async Task ServerValidation(string? server, bool ok)
    {
        // Act
        bool valid = EditorCommands.IsValidServer(server);

        // Assert
        await Assert.That(valid).IsEqualTo(ok);
    }

    [Test]
    [Arguments("nvim", EditorOpenMode.Nvim)]
    [Arguments("split", EditorOpenMode.Split)]
    [Arguments("system", EditorOpenMode.System)]
    [Arguments("none", EditorOpenMode.None)]
    public async Task Modes(string text, EditorOpenMode expected)
    {
        // Act
        var mode = EditorCommands.ParseMode(text);

        // Assert
        await Assert.That(mode).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null, true)]
    [Arguments("/usr/bin/nvim -u x", true)]
    [Arguments("code -w", false)]
    public async Task IsNvim(string? editor, bool nvim)
    {
        // Act
        bool isNvim = EditorCommands.IsNvim(editor);

        // Assert
        await Assert.That(isNvim).IsEqualTo(nvim);
    }

    [Test]
    public async Task PreferredEditorFallsBackToEditor()
    {
        // Act
        string? editor = EditorCommands.PreferredEditor(n => n == "EDITOR" ? "vi" : null);

        // Assert
        await Assert.That(editor).IsEqualTo("vi");
    }

    [Test]
    public async Task PreferredEditorPrefersVisual()
    {
        // Act
        string? editor = EditorCommands.PreferredEditor(n => n == "VISUAL" ? "code" : "vi");

        // Assert
        await Assert.That(editor).IsEqualTo("code");
    }
}

public class GitStatusParserTests
{
    private const string Porcelain = """
        # branch.oid 9a3f2c1d4e5f60718293a4b5c6d7e8f901234567
        # branch.head feature/badges
        # branch.upstream origin/feature/badges
        # branch.ab +2 -1
        1 .M N... 100644 100644 100644 aaaa bbbb src/App.cs
        1 M. N... 100644 100644 100644 aaaa bbbb src/Tab.cs
        1 MM N... 100644 100644 100644 aaaa bbbb README.md
        2 R. N... 100644 100644 100644 aaaa bbbb R100 new.cs    old.cs
        u UU N... 100644 100644 100644 100644 aaaa bbbb cccc conflict.cs
        ? notes.txt
        ? tmp/
        ! bin/
        """;

    [Test]
    public async Task ParsesBranchAndCounts()
    {
        // Act
        var s = GitStatusParser.Parse(Porcelain)!;

        // Assert
        await Assert.That(s.Branch).IsEqualTo("feature/badges");
        await Assert.That(s.Detached).IsFalse();
        await Assert.That(s.Upstream).IsEqualTo("origin/feature/badges");
        await Assert.That((s.Ahead, s.Behind)).IsEqualTo((2, 1));
        await Assert.That(s.Staged).IsEqualTo(3);     // M., MM, R.
        await Assert.That(s.Modified).IsEqualTo(2);   // .M, MM
        await Assert.That(s.Untracked).IsEqualTo(2);
        await Assert.That(s.Conflicts).IsEqualTo(1);
        await Assert.That(s.Clean).IsFalse();
    }

    [Test]
    public async Task CleanDetachedHead()
    {
        // Act
        var detached = GitStatusParser.Parse("# branch.oid 9a3f2c1d4e5f60718293a4b5c6d7e8f901234567\n# branch.head (detached)\n")!;

        // Assert
        await Assert.That(detached.Detached).IsTrue();
        await Assert.That(detached.Branch).IsEqualTo("9a3f2c1");
        await Assert.That(detached.Clean).IsTrue();
    }

    [Test]
    public async Task InitialCommit()
    {
        // Act
        var initial = GitStatusParser.Parse("# branch.oid (initial)\n# branch.head main\n? a.txt\n")!;

        // Assert
        await Assert.That(initial.Branch).IsEqualTo("main");
        await Assert.That(initial.Oid).IsNull();
        await Assert.That(initial.Untracked).IsEqualTo(1);
        await Assert.That(initial.Ahead).IsEqualTo(0);
    }

    [Test]
    public async Task NotPorcelain()
    {
        // Act
        var status = GitStatusParser.Parse("fatal: not a git repository");

        // Assert
        await Assert.That(status).IsNull();
    }

    [Test]
    [Arguments("git@github.com:eriklieben/erikterm.git", "eriklieben/erikterm")]
    [Arguments("https://github.com/dotnet/aspire", "dotnet/aspire")]
    [Arguments("https://github.com/dotnet/aspire.git/", "dotnet/aspire")]
    [Arguments("ssh://git@github.com/o/r.git", "o/r")]
    [Arguments("https://user@github.com/o/r.git", "o/r")]
    [Arguments("git@gitlab.com:o/r.git", null)]
    [Arguments("https://github.com/o", null)]
    [Arguments("https://github.com/o/r/../x", null)]
    public async Task GitHubSlugs(string url, string? slug)
    {
        // Act
        string? found = GitStatusParser.GitHubSlug(url);

        // Assert
        await Assert.That(found).IsEqualTo(slug);
    }

    [Test]
    public async Task PrefersOrigin()
    {
        // Act
        string? slug = GitStatusParser.GitHubSlugFromRemotes(
            "remote.upstream.url https://github.com/org/repo.git\nremote.origin.url git@github.com:me/fork.git\nremote.lab.url git@gitlab.com:x/y.git\n");

        // Assert
        await Assert.That(slug).IsEqualTo("me/fork");
    }
}

// Internal: the check state (an app type) is a test parameter.
internal class GitHubParserTests
{
    private const string PrJson = """
        {"isDraft":false,"number":42,"reviewDecision":"REVIEW_REQUIRED","state":"OPEN","title":"Add badges",
         "url":"https://github.com/eriklieben/erikterm/pull/42",
         "statusCheckRollup":[
           {"__typename":"CheckRun","completedAt":"2026-11-07T10:00:00Z","conclusion":"SUCCESS","detailsUrl":"https://github.com/eriklieben/erikterm/actions/runs/111/job/1","name":"build","startedAt":"2026-11-07T09:58:00Z","status":"COMPLETED","workflowName":"CI"},
           {"__typename":"CheckRun","completedAt":"0001-01-01T00:00:00Z","conclusion":"","detailsUrl":"https://github.com/eriklieben/erikterm/actions/runs/222/job/2","name":"test","startedAt":"2026-11-07T09:58:00Z","status":"IN_PROGRESS","workflowName":"CI"},
           {"__typename":"StatusContext","context":"ci/legacy","startedAt":"2026-11-07T09:58:00Z","state":"SUCCESS","targetUrl":"https://ci.example/1"}
         ]}
        """;

    [Test]
    public async Task ParsesPrWithPendingChecks()
    {
        // Act
        var pr = GitHubParser.ParsePr(PrJson)!;
        var s = GitHubParser.FromPr(pr);

        // Assert
        await Assert.That(pr.Number).IsEqualTo(42);
        await Assert.That(pr.StatusCheckRollup![0].TypeName).IsEqualTo("CheckRun");
        await Assert.That(s.Checks).IsEqualTo(CheckState.Pending);
        await Assert.That((s.ChecksTotal, s.ChecksFailed, s.ChecksPending)).IsEqualTo((3, 0, 1));
        await Assert.That(s.RunId).IsEqualTo(222); // the pending run is the one to watch
        await Assert.That(s.PrUrl).IsEqualTo("https://github.com/eriklieben/erikterm/pull/42");
        await Assert.That(s.ReviewDecision).IsEqualTo("REVIEW_REQUIRED");
    }

    [Test]
    public async Task FailureWins()
    {
        // Arrange
        string failing = PrJson.Replace("\"conclusion\":\"SUCCESS\"", "\"conclusion\":\"FAILURE\"");

        // Act
        var checks = GitHubParser.FromPr(GitHubParser.ParsePr(failing)!).Checks;

        // Assert
        await Assert.That(checks).IsEqualTo(CheckState.Fail);
    }

    [Test]
    public async Task PassNeedsAllDone()
    {
        // Arrange
        string done = PrJson.Replace("\"conclusion\":\"\"", "\"conclusion\":\"SKIPPED\"").Replace("IN_PROGRESS", "COMPLETED");

        // Act
        var checks = GitHubParser.FromPr(GitHubParser.ParsePr(done)!).Checks;

        // Assert
        await Assert.That(checks).IsEqualTo(CheckState.Pass);
    }

    [Test]
    public async Task StatusContextFailureWins()
    {
        // Arrange
        string statusFail = PrJson.Replace("\"state\":\"SUCCESS\"", "\"state\":\"ERROR\"");

        // Act
        var checks = GitHubParser.FromPr(GitHubParser.ParsePr(statusFail)!).Checks;

        // Assert
        await Assert.That(checks).IsEqualTo(CheckState.Fail);
    }

    [Test]
    public async Task NoChecks()
    {
        // Act
        var s = GitHubParser.FromPr(GitHubParser.ParsePr("""{"number":7,"title":"x","state":"OPEN","url":"https://github.com/o/r/pull/7","isDraft":true,"reviewDecision":"","statusCheckRollup":[]}""")!);

        // Assert
        await Assert.That(s.Checks).IsEqualTo(CheckState.None);
        await Assert.That(s.Draft).IsTrue();
        await Assert.That(s.ReviewDecision).IsNull();
    }

    [Test]
    public async Task ParsesRuns()
    {
        // Act
        var runs = GitHubParser.ParseRuns("""[{"conclusion":"","databaseId":987654321,"name":"CI","status":"in_progress","url":"https://github.com/o/r/actions/runs/987654321"}]""")!;
        var s = GitHubParser.FromRun(runs[0]);

        // Assert
        await Assert.That((s.Checks, s.RunId, s.RunName)).IsEqualTo((CheckState.Pending, 987654321L, "CI"));
    }

    [Test]
    [Arguments("failure", CheckState.Fail)]
    [Arguments("success", CheckState.Pass)]
    public async Task CompletedRunState(string conclusion, CheckState expected)
    {
        // Act
        var state = GitHubParser.RunState(new GhRun { Status = "completed", Conclusion = conclusion });

        // Assert
        await Assert.That(state).IsEqualTo(expected);
    }

    [Test]
    public async Task NoRunMeansNoChecks()
    {
        // Act
        var s = GitHubParser.FromRun(null);

        // Assert
        await Assert.That(s.Checks).IsEqualTo(CheckState.None);
    }

    [Test]
    public async Task EmptyRunList()
    {
        // Act
        var runs = GitHubParser.ParseRuns("[]")!;

        // Assert
        await Assert.That(runs).IsEmpty();
    }

    [Test]
    public async Task BadPrJsonIsNull()
    {
        // Act
        var pr = GitHubParser.ParsePr("not json");

        // Assert
        await Assert.That(pr).IsNull();
    }

    [Test]
    public async Task BadRunsJsonIsNull()
    {
        // Act
        var runs = GitHubParser.ParseRuns("{");

        // Assert
        await Assert.That(runs).IsNull();
    }

    [Test]
    [Arguments("https://github.com/o/r/actions/runs/123/job/456", 123L)]
    [Arguments("https://github.com/o/r/actions/runs/123", 123L)]
    [Arguments("https://evil.example/o/r/actions/runs/123", 0L)]
    [Arguments(null, 0L)]
    public async Task RunIds(string? url, long id)
    {
        // Act
        long runId = GitHubParser.RunIdFromUrl(url);

        // Assert
        await Assert.That(runId).IsEqualTo(id);
    }

    [Test]
    [Arguments("https://github.com/o/r/pull/1", true)]
    [Arguments("http://github.com/o/r", false)]
    [Arguments("https://github.com.evil.example/x", false)]
    [Arguments("https://github.com/o/r\"; rm", false)]
    public async Task GitHubUrls(string url, bool ok)
    {
        // Act
        bool isGitHub = GitHubParser.IsGitHubUrl(url);

        // Assert
        await Assert.That(isGitHub).IsEqualTo(ok);
    }

    [Test]
    public async Task NoPrError()
    {
        // Act
        bool noPr = GitHubParser.IsNoPr("no pull requests found for branch \"main\"");

        // Assert
        await Assert.That(noPr).IsTrue();
    }

    [Test]
    [Arguments("HTTP 403: API rate limit exceeded for user", true)]
    [Arguments("could not resolve host", false)]
    public async Task RateLimitErrors(string stderr, bool limited)
    {
        // Act
        bool rateLimited = GitHubParser.IsRateLimited(stderr);

        // Assert
        await Assert.That(rateLimited).IsEqualTo(limited);
    }
}

public class DropQuotingTests
{
    [Test]
    [Arguments("/home/erik/a.png", "/home/erik/a.png")]
    [Arguments("/home/erik/My Pictures/a b.png", "'/home/erik/My Pictures/a b.png'")]
    [Arguments("/tmp/it's.txt", @"'/tmp/it'\''s.txt'")]
    [Arguments("/tmp/$(rm -rf ~).txt", "'/tmp/$(rm -rf ~).txt'")]
    [Arguments("/tmp/a;b`c`", "'/tmp/a;b`c`'")]
    [Arguments("-rf", "'-rf'")]
    [Arguments("", "''")]
    public async Task Quote(string path, string expected)
    {
        // Act
        string quoted = ShellQuote.Quote(path);

        // Assert
        await Assert.That(quoted).IsEqualTo(expected);
    }

    [Test]
    public async Task JoinsWithTrailingSpaceAndDropsNewlines()
    {
        // Act
        string joined = ShellQuote.JoinPaths(["/a.png", "/b c.png", "/evil\nrm -rf ~"]);

        // Assert
        await Assert.That(joined).IsEqualTo("/a.png '/b c.png' ");
    }

    [Test]
    public async Task UriListBecomesPaths()
    {
        // Act
        var (paths, _) = ShellQuote.FromDroppedText("file:///home/erik/a%20b.png\r\nfile:///tmp/c.txt\r\n");

        // Assert
        await Assert.That(paths).IsEquivalentTo(["/home/erik/a b.png", "/tmp/c.txt"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task PlainTextIsFlattened()
    {
        // Act
        var (paths, text) = ShellQuote.FromDroppedText("echo hi\nrm -rf /\e[201~");

        // Assert
        await Assert.That(paths).IsNull();
        await Assert.That(text).IsEqualTo("echo hi rm -rf /�[201~");
    }
}

public class RefreshPolicyTests
{
    private static readonly RefreshTiming T = new(MinIntervalMs: 2000, PeriodMs: 10_000, BackoffMs: 1000, MaxBackoffMs: 8000, TimeoutBackoffMs: 60_000, RateLimitBackoffMs: 900_000);

    [Test]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task FirstRunWhenVisible(bool periodic, bool due)
    {
        // Arrange
        var s = new RefreshState();

        // Act
        bool isDue = RefreshPolicy.Due(s, 0, T, periodic);

        // Assert
        await Assert.That(isDue).IsEqualTo(due);
    }

    [Test]
    public async Task FirstRunWhenWanted()
    {
        // Arrange
        var s = new RefreshState();

        // Act
        RefreshPolicy.Request(ref s);
        bool isDue = RefreshPolicy.Due(s, 0, T, periodic: false);

        // Assert
        await Assert.That(isDue).IsTrue();
    }

    [Test]
    public async Task RequestsAreThrottledAndNeverOverlap()
    {
        // A walk-through: each step works on the state the previous one left.
        // Arrange
        var s = new RefreshState();
        RefreshPolicy.Request(ref s);

        // Act
        RefreshPolicy.Started(ref s, 1000);
        RefreshPolicy.Request(ref s); // a prompt ended while running
        bool dueInFlight = RefreshPolicy.Due(s, 1500, T, true);
        RefreshPolicy.Completed(ref s, 1200, RefreshOutcome.Ok, T);
        bool dueTooSoon = RefreshPolicy.Due(s, 2500, T, false);
        long msUntilDue = RefreshPolicy.MsUntilDue(s, 2500, T, false);
        bool dueAfterMinInterval = RefreshPolicy.Due(s, 3000, T, false);

        // Assert
        await Assert.That(dueInFlight).IsFalse(); // in flight
        await Assert.That(dueTooSoon).IsFalse(); // < 2 s since the last start
        await Assert.That(msUntilDue).IsEqualTo(500);
        await Assert.That(dueAfterMinInterval).IsTrue();
    }

    [Test]
    [Arguments(10_099L, true, false)]
    [Arguments(10_100L, true, true)]
    [Arguments(50_000L, false, false)]
    public async Task PeriodicOnlyWhenVisible(long now, bool periodic, bool due)
    {
        // Arrange
        var s = new RefreshState();
        RefreshPolicy.Started(ref s, 0);
        RefreshPolicy.Completed(ref s, 100, RefreshOutcome.Ok, T);

        // Act
        bool isDue = RefreshPolicy.Due(s, now, T, periodic);

        // Assert
        await Assert.That(isDue).IsEqualTo(due);
    }

    [Test]
    public async Task HiddenKeyIsNeverScheduled()
    {
        // Arrange
        var s = new RefreshState();
        RefreshPolicy.Started(ref s, 0);
        RefreshPolicy.Completed(ref s, 100, RefreshOutcome.Ok, T);

        // Act
        long msUntilDue = RefreshPolicy.MsUntilDue(s, 50_000, T, false);

        // Assert
        await Assert.That(msUntilDue).IsEqualTo(long.MaxValue);
    }

    [Test]
    [Arguments(1, 1000L)]
    [Arguments(2, 2000L)]
    [Arguments(3, 4000L)]
    [Arguments(4, 8000L)]
    [Arguments(5, 8000L)]
    public async Task ErrorsBackOffExponentially(int failures, long expected)
    {
        // Act
        long backoff = RefreshPolicy.Backoff(failures, T);

        // Assert
        await Assert.That(backoff).IsEqualTo(expected);
    }

    [Test]
    public async Task TimeoutsAndRateLimitsBackOffLonger()
    {
        // A walk-through: the failure count carries from one outcome to the next, and a success resets it.
        // Arrange
        var s = new RefreshState();

        // Act
        RefreshPolicy.Started(ref s, 0);
        RefreshPolicy.Completed(ref s, 0, RefreshOutcome.Failed, T);
        RefreshPolicy.Request(ref s);
        bool dueDuringErrorBackoff = RefreshPolicy.Due(s, 999, T, true);
        bool dueAfterErrorBackoff = RefreshPolicy.Due(s, 2000, T, true);

        RefreshPolicy.Started(ref s, 2000);
        RefreshPolicy.Completed(ref s, 2000, RefreshOutcome.TimedOut, T); // a huge repository
        RefreshPolicy.Request(ref s);
        bool dueDuringTimeoutBackoff = RefreshPolicy.Due(s, 61_999, T, true);
        bool dueAfterTimeoutBackoff = RefreshPolicy.Due(s, 62_000, T, true);

        RefreshPolicy.Started(ref s, 62_000);
        RefreshPolicy.Completed(ref s, 62_000, RefreshOutcome.RateLimited, T);
        bool dueDuringRateLimitBackoff = RefreshPolicy.Due(s, 62_000 + 899_999, T, true);

        RefreshPolicy.Started(ref s, 962_000);
        RefreshPolicy.Completed(ref s, 962_000, RefreshOutcome.Ok, T);

        // Assert
        await Assert.That(dueDuringErrorBackoff).IsFalse();
        await Assert.That(dueAfterErrorBackoff).IsTrue();
        await Assert.That(dueDuringTimeoutBackoff).IsFalse();
        await Assert.That(dueAfterTimeoutBackoff).IsTrue();
        await Assert.That(dueDuringRateLimitBackoff).IsFalse();
        await Assert.That(s.Failures).IsEqualTo(0);
        await Assert.That(s.BlockedUntil).IsEqualTo(0);
    }
}

public class RepoBadgeModelTests
{
    [Test]
    public async Task BuildsCompactStrings()
    {
        // Arrange
        var git = new GitStatus { Branch = "main", Ahead = 1, Behind = 0, Staged = 1, Modified = 2, Untracked = 1 };

        // Act
        var m = RepoBadgeModel.From(git, new GitHubStatus { PrNumber = 42, Checks = CheckState.Fail, ChecksFailed = 2 })!;

        // Assert
        await Assert.That((m.AheadText, m.BehindText, m.StagedText, m.ModifiedText, m.UntrackedText, m.DirtyText, m.PrText))
            .IsEqualTo(("↑1", null, "+1", "2", "?1", "4", "#42"));
        await Assert.That(m.ChecksText).IsEqualTo("2 failing");
    }

    [Test]
    public async Task NoStatusNoBadge()
    {
        // Act
        var m = RepoBadgeModel.From(null, null);

        // Assert
        await Assert.That(m).IsNull();
    }

    [Test]
    public async Task ValueEqualitySkipsRedundantRedraws()
    {
        // Arrange
        var git = new GitStatus { Branch = "main" };

        // Act
        var model = RepoBadgeModel.From(git, null);
        var sameStatus = RepoBadgeModel.From(git with { }, null);
        var changedStatus = RepoBadgeModel.From(git with { Modified = 1 }, null);

        // Assert
        await Assert.That(sameStatus).IsEqualTo(model);
        await Assert.That(changedStatus).IsNotEqualTo(model);
    }

    [Test]
    public async Task LongBranchesAreShortened()
    {
        // Act
        var m = RepoBadgeModel.From(new GitStatus { Branch = "feature/a-really-long-branch-name-here" }, null)!;

        // Assert
        await Assert.That(m.BranchShort.Length).IsEqualTo(RepoBadgeModel.MaxBranchChars);
        await Assert.That(m.BranchShort).EndsWith("…");
    }
}
