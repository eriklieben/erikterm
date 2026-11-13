using System.Diagnostics;
using System.Text;
using ErikTerm.App.Session;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

public class SessionModelTests
{
    // Store tests write below a fresh temp directory, never to the real session path.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "erikterm-session-test-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "erikterm", "session.json");

    [After(Test)]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static SessionFile Sample() => new()
    {
        SavedAt = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero),
        Windows =
        [
            new SessionWindow
            {
                Width = 1200, Height = 700, Fullscreen = true, FontZoom = 1.21, ActiveTab = 1,
                Tabs =
                [
                    new SessionTab { Layout = new SessionNode { Pane = new SessionPane { Cwd = "/tmp" } } },
                    new SessionTab
                    {
                        Title = "logs", Focused = 1, Zoomed = 1,
                        Layout = new SessionNode
                        {
                            Axis = "horizontal", Ratio = 0.3,
                            First = new SessionNode { Pane = new SessionPane { Cwd = "/a", Scrollback = ["\e[0;1mbold\e[0m", "plain"] } },
                            Second = new SessionNode
                            {
                                Pane = new SessionPane { Cwd = "/b b", Program = new SessionProgram { Name = "nvim", Argv = ["nvim", "x y.md"] } },
                            },
                        },
                    },
                ],
            },
        ],
    };

    [Test]
    public async Task RoundTripsAPanesAgentAndSessionId()
    {
        // Arrange
        var file = Sample();
        file.Windows[0].Tabs[0].Layout.Pane!.Agent = new SessionAgent { Id = "claude-code", SessionId = "abc-123" };

        // Act
        string json = SessionJson.Serialize(file);
        var back = SessionJson.Deserialize(json, out string? problem);

        // Assert
        await Assert.That(problem).IsNull();
        await Assert.That(json).Contains("\"session_id\": \"abc-123\"");
        var agent = await Assert.That(back!.Windows[0].Tabs[0].Layout.Pane!.Agent).IsNotNull();
        await Assert.That((agent.Id, agent.SessionId)).IsEqualTo(("claude-code", "abc-123"));
    }

    [Test]
    public async Task AFileFromBeforeAgentsWereSavedStillLoads()
    {
        // Arrange
        const string json = """
            { "version": 1, "windows": [ { "width": 800, "height": 600, "tabs": [
              { "focused": 0, "layout": { "pane": { "cwd": "/tmp", "program": { "name": "nvim" } } } } ] } ] }
            """;

        // Act
        var back = SessionJson.Deserialize(json, out string? problem);

        // Assert
        await Assert.That(problem).IsNull();
        var pane = back!.Windows[0].Tabs[0].Layout.Pane!;
        await Assert.That(pane.Cwd).IsEqualTo("/tmp");
        await Assert.That(pane.Agent).IsNull();
    }

    [Test]
    public async Task WritesVersionedJsonWithoutNulls()
    {
        // Arrange
        var file = Sample();

        // Act
        string json = SessionJson.Serialize(file);

        // Assert
        await Assert.That(json).Contains("\"version\": 1");
        await Assert.That(json).Contains("\"windows\"");
        await Assert.That(json).Contains("\"active_tab\": 1");
        await Assert.That(json).Contains("\"font_zoom\"");
        await Assert.That(json).DoesNotContain("null"); // nulls are left out
    }

    [Test]
    public async Task RoundTripsThroughJson()
    {
        // Arrange
        string json = SessionJson.Serialize(Sample());

        // Act
        var back = SessionJson.Deserialize(json, out string? problem);
        string? again = back is null ? null : SessionJson.Serialize(back);

        // Assert
        await Assert.That(problem).IsNull();
        var loaded = await Assert.That(back).IsNotNull();
        var w = await Assert.That(loaded.Windows).HasSingleItem();
        await Assert.That((w.Width, w.Height, w.Fullscreen, w.FontZoom, w.ActiveTab)).IsEqualTo((1200, 700, true, 1.21, 1));
        await Assert.That(w.Tabs.Count).IsEqualTo(2);
        await Assert.That(w.Tabs[0].Title).IsNull();
        await Assert.That((w.Tabs[1].Title, w.Tabs[1].Focused, w.Tabs[1].Zoomed)).IsEqualTo(("logs", 1, (int?)1));
        await Assert.That(w.Tabs[1].Layout.Axis).IsEqualTo("horizontal");
        await Assert.That(w.Tabs[1].Layout.Ratio).IsEqualTo(0.3);
        await Assert.That(w.Tabs[1].Layout.First!.Pane!.Scrollback!).IsEquivalentTo(["\e[0;1mbold\e[0m", "plain"], CollectionOrdering.Matching);
        await Assert.That(w.Tabs[1].Layout.Second!.Pane!.Program!.Name).IsEqualTo("nvim");
        await Assert.That(w.Tabs[1].Layout.Second!.Pane!.Program!.Argv!).IsEquivalentTo(["nvim", "x y.md"], CollectionOrdering.Matching);
        await Assert.That(again).IsEqualTo(json);
    }

    [Test]
    [Arguments("{\"version\": 2, \"windows\": []}", "version 2")]
    [Arguments("{\"windows\": []}", "no version")]
    [Arguments("[1, 2]", "not a JSON object")]
    public async Task IgnoresIncompatibleFilesWithTheReason(string json, string expected)
    {
        // Act
        var file = SessionJson.Deserialize(json, out string? problem);

        // Assert
        await Assert.That(file).IsNull();
        await Assert.That(problem).IsNotNull();
        await Assert.That(problem).Contains(expected);
    }

    [Test]
    [Arguments("{\"version\": 1, \"windows\": [")]
    [Arguments("{\"version\": 1, \"windows\": 5}")]
    [Arguments("")]
    public async Task IgnoresBrokenFiles(string json)
    {
        // Act
        var file = SessionJson.Deserialize(json, out string? problem);

        // Assert
        await Assert.That(file).IsNull();
        await Assert.That(problem).IsNotNull();
    }

    [Test]
    public async Task ToleratesUnknownFieldsAndMissingOnes()
    {
        // Act
        var file = SessionJson.Deserialize("{\"version\": 1, \"future\": {\"x\": 1}, \"windows\": [{\"tabs\": [{}]}]}", out _);
        var tab = file?.Windows.FirstOrDefault()?.Tabs.FirstOrDefault();
        var leaves = tab is null ? null : SessionLayout.Leaves(SessionLayout.Sanitize(tab.Layout));

        // Assert
        var loaded = await Assert.That(file).IsNotNull();
        var window = await Assert.That(loaded.Windows).HasSingleItem();
        await Assert.That(window.Tabs).HasSingleItem();
        await Assert.That(leaves).HasSingleItem();
    }

    [Test]
    [Arguments(null, "session.json")]
    [Arguments("", "session.json")]
    [Arguments("  ", "session.json")]
    [Arguments("erikterm", "session.json")]
    [Arguments("erikterm-dropdown", "session-erikterm-dropdown.json")]
    [Arguments("org.example.Term_2", "session-org.example.Term_2.json")]
    [Arguments("../../etc/passwd", "session-_._.._etc_passwd.json")]
    [Arguments(".hidden", "session-_hidden.json")]
    [Arguments("drop down/ü", "session-drop_down__.json")]
    public async Task SessionFileIsKeyedByWindowClass(string? windowClass, string expected)
    {
        // Act
        // DefaultPath only builds the path here; nothing reads or writes it.
        string fileName = SessionStore.FileNameFor(windowClass);
        string? defaultFileName = Path.GetFileName(SessionStore.DefaultPath(windowClass));

        // Assert
        await Assert.That(fileName).IsEqualTo(expected);
        await Assert.That(defaultFileName).IsEqualTo(expected);
    }

    [Test]
    public async Task LongWindowClassesAreTruncated()
    {
        // Act
        string name = SessionStore.FileNameFor(new string('x', 500));

        // Assert
        await Assert.That(name.Length).IsEqualTo("session-".Length + 64 + ".json".Length);
    }

    [Test]
    public async Task EachWindowClassHasItsOwnFileInTheSameDirectory()
    {
        // Act
        // Paths only; nothing reads or writes them.
        string main = SessionStore.DefaultPath();
        string dropdown = SessionStore.DefaultPath("erikterm-dropdown");

        // Assert
        await Assert.That(dropdown).IsNotEqualTo(main);
        await Assert.That(Path.GetDirectoryName(dropdown)).IsEqualTo(Path.GetDirectoryName(main));
    }

    [Test]
    public async Task StoreWritesAtomicallyOverAnExistingFile()
    {
        // Arrange
        SessionStore.Save(StorePath, Sample());

        // Act
        SessionStore.Save(StorePath, Sample()); // over an existing file
        string[] files = Directory.GetFiles(Path.GetDirectoryName(StorePath)!);

        // Assert
        await Assert.That(files).HasSingleItem(); // no temp files left behind
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task StoredFileIsReadableOnlyByTheUser()
    {
        // Arrange
        SessionStore.Save(StorePath, Sample());

        // Act
        SessionStore.Save(StorePath, Sample()); // over an existing file
        UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(StorePath);

        // Assert
        await Assert.That(mode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task StoreLoadsBackWhatItSaved()
    {
        // Arrange
        SessionStore.Save(StorePath, Sample());
        SessionStore.Save(StorePath, Sample()); // over an existing file

        // Act
        var back = SessionStore.Load(StorePath, out string? problem);

        // Assert
        await Assert.That(problem).IsNull();
        var loaded = await Assert.That(back).IsNotNull();
        await Assert.That(loaded.Windows[0].Tabs.Count).IsEqualTo(2);
    }

    [Test]
    public async Task DeletedSessionLoadsAsNothing()
    {
        // Arrange
        SessionStore.Save(StorePath, Sample());

        // Act
        SessionStore.Delete(StorePath);
        var back = SessionStore.Load(StorePath, out string? problem);

        // Assert
        await Assert.That(back).IsNull();
        await Assert.That(problem).IsNull();
    }
}

public class SessionLayoutTests
{
    private sealed class P(string name)
    {
        public readonly string Name = name;
        public override string ToString() => Name;
    }

    private static List<(string, RectI)> Rects(LayoutTree<P> tree, RectI bounds)
    {
        var list = new List<(P, RectI)>();
        tree.Layout(bounds, 4, list);
        return list.Select(x => (x.Item1.Name, x.Item2)).ToList();
    }

    /// <summary>a | (b / c).</summary>
    private static (LayoutTree<P> Tree, P B, P C) ThreePanes()
    {
        P a = new("a"), b = new("b"), c = new("c");
        var tree = new LayoutTree<P>(a);
        tree.Split(a, b, SplitAxis.Horizontal, ratio: 0.3);
        tree.Split(b, c, SplitAxis.Vertical, ratio: 0.7);
        return (tree, b, c);
    }

    private static SessionNode CaptureWithCwds(LayoutTree<P> tree) =>
        SessionLayout.Capture(tree, p => new SessionPane { Cwd = "/" + p.Name });

    /// <summary>The captured layout in a tab with b focused and c zoomed, written and read back as on disk.</summary>
    private static SessionTab ThroughJson(SessionNode node)
    {
        var file = new SessionFile { Windows = [new SessionWindow { Tabs = [new SessionTab { Layout = node, Focused = 1, Zoomed = 2 }] }] };
        return SessionJson.Deserialize(SessionJson.Serialize(file), out _)!.Windows[0].Tabs[0];
    }

    /// <summary>((a | b) / c) | d</summary>
    private static LayoutTree<P> DeepLeftHeavyTree()
    {
        P a = new("a"), b = new("b"), c = new("c"), d = new("d");
        var tree = new LayoutTree<P>(a);
        tree.Split(a, d, SplitAxis.Horizontal, ratio: 0.6);
        tree.Split(a, c, SplitAxis.Vertical, ratio: 0.4);
        tree.Split(a, b, SplitAxis.Horizontal, ratio: 0.25);
        return tree;
    }

    [Test]
    public async Task CapturesTheSplitsOfTheTree()
    {
        // Arrange
        var (tree, _, _) = ThreePanes();

        // Act
        var node = CaptureWithCwds(tree);

        // Assert
        await Assert.That(node.Axis).IsEqualTo("horizontal");
        await Assert.That(node.Ratio).IsEqualTo(0.3);
        await Assert.That(node.Second!.Axis).IsEqualTo("vertical");
    }

    [Test]
    public async Task LeafIndexNumbersThePanesInLayoutOrder()
    {
        // Arrange
        var (tree, b, c) = ThreePanes();

        // Act
        int indexOfB = SessionLayout.LeafIndex(tree, b);
        int indexOfC = SessionLayout.LeafIndex(tree, c);
        int indexOfNone = SessionLayout.LeafIndex(tree, null);

        // Assert
        await Assert.That(indexOfB).IsEqualTo(1);
        await Assert.That(indexOfC).IsEqualTo(2);
        await Assert.That(indexOfNone).IsEqualTo(-1);
    }

    [Test]
    public async Task RebuildsTheSameTreeThroughJson()
    {
        // Arrange
        var (tree, _, _) = ThreePanes();
        var bounds = new RectI(0, 0, 1000, 600);
        var original = Rects(tree, bounds);
        var node = CaptureWithCwds(tree);

        // Act
        // Through JSON, as on disk.
        var root = SessionLayout.Sanitize(ThroughJson(node).Layout);
        var leaves = SessionLayout.Leaves(root);
        var panes = leaves.Select(l => new P(l.Cwd![1..])).ToList();
        var rebuilt = Rects(SessionLayout.Rebuild(root, panes), bounds);

        // Assert
        await Assert.That(leaves.Select(l => l.Cwd!)).IsEquivalentTo(["/a", "/b", "/c"], CollectionOrdering.Matching);
        await Assert.That(rebuilt).IsEquivalentTo(original, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FocusAndZoomPointAtTheSamePanesThroughJson()
    {
        // Arrange
        var (tree, _, _) = ThreePanes();
        var node = CaptureWithCwds(tree);

        // Act
        var tab = ThroughJson(node);
        var panes = SessionLayout.Leaves(SessionLayout.Sanitize(tab.Layout)).Select(l => new P(l.Cwd![1..])).ToList();

        // Assert
        await Assert.That(panes[tab.Focused].Name).IsEqualTo("b");
        await Assert.That(panes[tab.Zoomed!.Value].Name).IsEqualTo("c");
    }

    [Test]
    public async Task PanesAreSpawnedAtTheSizeTheyWillGet()
    {
        // Arrange
        var (tree, _, _) = ThreePanes();
        var bounds = new RectI(0, 0, 1000, 600);
        var expected = Rects(tree, bounds).Select(r => r.Item2).ToList();
        var root = SessionLayout.Sanitize(ThroughJson(CaptureWithCwds(tree)).Layout);

        // Act
        var rects = SessionLayout.LeafRects(root, bounds, 4);

        // Assert
        await Assert.That(rects).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RebuildsDeepAndLeftHeavyTrees()
    {
        // Arrange
        var tree = DeepLeftHeavyTree();
        var bounds = new RectI(10, 20, 1280, 777);
        var original = Rects(tree, bounds);

        // Act
        var node = SessionLayout.Capture(tree, p => new SessionPane { Cwd = p.Name });
        var panes = SessionLayout.Leaves(node).Select(l => new P(l.Cwd!)).ToList();
        var rebuilt = Rects(SessionLayout.Rebuild(node, panes), bounds);

        // Assert
        await Assert.That(string.Concat(panes.Select(p => p.Name))).IsEqualTo("abcd");
        await Assert.That(rebuilt).IsEquivalentTo(original, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RebuildRejectsTooFewPanes()
    {
        // Arrange
        var node = SessionLayout.Capture(DeepLeftHeavyTree(), p => new SessionPane { Cwd = p.Name });
        var tooFew = SessionLayout.Leaves(node).Select(l => new P(l.Cwd!)).Take(3).ToList();

        // Act
        var act = () => SessionLayout.Rebuild(node, tooFew);

        // Assert
        await Assert.That(act).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task SanitizeRepairsBadInput()
    {
        // Arrange
        var bad = new SessionNode
        {
            Axis = "diagonal", Ratio = double.NaN,
            First = new SessionNode { Pane = new SessionPane { Cwd = "x" } },
            Second = new SessionNode { Axis = "vertical", Ratio = 7, First = new SessionNode { Pane = new SessionPane { Cwd = "y" } } }, // half a split
        };

        // Act
        var root = SessionLayout.Sanitize(bad);
        var cwds = SessionLayout.Leaves(root).Select(l => l.Cwd!).ToList();

        // Assert
        await Assert.That(root.Axis).IsEqualTo("horizontal");
        await Assert.That(root.Ratio).IsEqualTo(0.5);
        await Assert.That(root.Second!.IsSplit).IsFalse();
        await Assert.That(cwds).IsEquivalentTo(["x", "y"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SanitizeTurnsNothingIntoOnePane()
    {
        // Act
        int leaves = SessionLayout.CountLeaves(SessionLayout.Sanitize(null));

        // Assert
        await Assert.That(leaves).IsEqualTo(1);
    }

    [Test]
    public async Task SanitizeCapsThePaneCount()
    {
        // Arrange
        var node = new SessionNode { Pane = new SessionPane() };
        for (int i = 0; i < 100; i++) node = new SessionNode { Axis = "horizontal", Ratio = 0.5, First = node, Second = new SessionNode { Pane = new SessionPane() } };
        int leavesBefore = SessionLayout.CountLeaves(node);

        // Act
        int cappedAt8 = SessionLayout.CountLeaves(SessionLayout.Sanitize(node, 8));
        int cappedByDefault = SessionLayout.CountLeaves(SessionLayout.Sanitize(node));

        // Assert
        await Assert.That(leavesBefore).IsEqualTo(101);
        await Assert.That(cappedAt8).IsBetween(1, 8);
        await Assert.That(cappedByDefault).IsBetween(1, SessionLayout.MaxPanesPerTab);
    }

    [Test]
    public async Task SanitizeClampsRatios()
    {
        // Act
        var clamped = SessionLayout.Sanitize(new SessionNode { Ratio = 2, First = new SessionNode(), Second = new SessionNode() });

        // Assert
        await Assert.That(clamped.Ratio).IsEqualTo(0.95);
    }
}

public class RestoreProgramsTests
{
    private static readonly string[] Defaults = ["nvim", "btop", "htop", "less", "man"];

    // The wrapper test runs in a fresh temp directory.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "erikterm-restore-test-" + Guid.NewGuid().ToString("N"));

    [After(Test)]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static SessionProgram NotesInNvim() => new() { Name = "nvim", Argv = ["nvim", "my notes.md", "+set ft=md", "it's"] };

    [Test]
    [Arguments("nvim", true)]
    [Arguments("/usr/bin/nvim", true)]
    [Arguments("btop", true)]
    [Arguments("vim", false)]
    [Arguments("nvim-qt", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task MatchesOnTheProgramName(string? name, bool expected)
    {
        // Act
        bool allowed = RestorePrograms.Allowed(Defaults, name);

        // Assert
        await Assert.That(allowed).IsEqualTo(expected);
    }

    [Test]
    public async Task ListEntriesMayBePaths()
    {
        // Act
        bool allowed = RestorePrograms.Allowed(["/opt/bin/htop"], "htop");

        // Assert
        await Assert.That(allowed).IsTrue();
    }

    [Test]
    [Arguments("nvim", new[] { "/usr/bin/nvim", "a.c" }, "nvim")]
    [Arguments("some-long-progr", null, "some-long-progr")] // comm is truncated
    [Arguments("some-long-progr", new[] { "some-long-program-name" }, "some-long-program-name")]
    [Arguments(null, null, null)]
    public async Task ProgramNamePrefersArgv(string? comm, string[]? argv, string? expected)
    {
        // Act
        string? name = RestorePrograms.ProgramName(comm, argv);

        // Assert
        await Assert.That(name).IsEqualTo(expected);
    }

    [Test]
    [Arguments("zsh", true)]
    [Arguments("-zsh", true)]
    [Arguments("/bin/bash", true)]
    [Arguments("pwsh.exe", true)]
    [Arguments("nvim", false)]
    public async Task KnowsShells(string name, bool expected)
    {
        // Act
        bool isShell = RestorePrograms.IsShell(name);

        // Assert
        await Assert.That(isShell).IsEqualTo(expected);
    }

    [Test]
    public async Task RestoredProgramIsTheSavedArgv()
    {
        // Arrange
        var program = NotesInNvim();

        // Act
        var argv = RestoreLaunch.ProgramArgv(program, Defaults);

        // Assert
        await Assert.That(argv!).IsEquivalentTo(["nvim", "my notes.md", "+set ft=md", "it's"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ProgramWithoutArgvIsRestoredByName()
    {
        // Act
        var argv = RestoreLaunch.ProgramArgv(new SessionProgram { Name = "btop" }, Defaults);

        // Assert
        await Assert.That(argv!).IsEquivalentTo(["btop"], CollectionOrdering.Matching); // no argv: the name
    }

    [Test]
    public async Task ProgramsNotOnTheListAreNotRestored()
    {
        // Act
        var cargo = RestoreLaunch.ProgramArgv(new SessionProgram { Name = "cargo", Argv = ["cargo", "build"] }, Defaults);
        var nvimWithEmptyList = RestoreLaunch.ProgramArgv(new SessionProgram { Name = "nvim", Argv = ["nvim"] }, []);

        // Assert
        await Assert.That(cargo).IsNull();
        await Assert.That(nvimWithEmptyList).IsNull();
    }

    [Test]
    public async Task NoProgramRestoresNothing()
    {
        // Act
        var argv = RestoreLaunch.ProgramArgv(null, Defaults);

        // Assert
        await Assert.That(argv).IsNull();
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task RestoredProgramIsTheSavedArgvThenTheShell()
    {
        // Arrange
        var program = NotesInNvim();

        // Act
        var argv = RestoreLaunch.CommandFor(program, Defaults, ["zsh", "-l"])!;

        // Assert
        await Assert.That(argv).IsEquivalentTo(
            ["/bin/sh", "-c", RestoreLaunch.Script, RestoreLaunch.WrapperName, "4", "nvim", "my notes.md", "+set ft=md", "it's", "zsh", "-l"],
            CollectionOrdering.Matching);
    }

    /// <summary>The restore PoC: argv as /proc/PID/cmdline shows for `nvim *` in a directory with a crafted file name.</summary>
    [Test]
    [Arguments("\x15touch PWNED\r")]
    [Arguments("a\nb")]
    [Arguments("\e]52;c;eA==\a")]
    [Arguments("tab\there")]
    [Arguments("del\x7f")]
    [Arguments("csi\u009b31m")]
    [Arguments("nul\0")]
    public async Task ArgvWithControlCharactersIsNeverRestored(string arg)
    {
        // Arrange
        string[] argv = ["nvim", arg];

        // Act
        bool safe = RestoreLaunch.IsSafe(argv);
        var programArgv = RestoreLaunch.ProgramArgv(new SessionProgram { Name = "nvim", Argv = [.. argv] }, Defaults);
        var wrapped = RestoreLaunch.Wrap(argv, ["zsh"]);

        // Assert
        await Assert.That(safe).IsFalse();
        await Assert.That(programArgv).IsNull();
        await Assert.That(wrapped).IsNull();
    }

    [Test]
    public async Task ProgramNameWithControlCharactersIsNeverRestored()
    {
        // Act
        var argv = RestoreLaunch.ProgramArgv(new SessionProgram { Name = "nvim\r" }, Defaults);

        // Assert
        await Assert.That(argv).IsNull();
    }

    public static IEnumerable<Func<(string Case, string[] Argv, bool Safe)>> CappedArgvs() =>
    [
        () => ("MaxArgs arguments", Enumerable.Repeat("a", RestoreLaunch.MaxArgs).ToArray(), true),
        () => ("one argument more than MaxArgs", Enumerable.Repeat("a", RestoreLaunch.MaxArgs + 1).ToArray(), false),
        () => ("an argument longer than MaxArgChars", ["nvim", new string('x', RestoreLaunch.MaxArgChars + 1)], false),
        () => ("> 32 KiB in all", Enumerable.Repeat(new string('x', 4000), 9).Prepend("nvim").ToArray(), false),
        () => ("no arguments", [], false),
        () => ("an empty program", ["", "a"], false),
        () => ("non-ASCII text and shell syntax", ["nvim", "ünïcödé 文件.md", "$(touch x)"], true),
    ];

    [Test]
    [MethodDataSource(nameof(CappedArgvs))]
    public async Task ArgvIsCapped(string @case, string[] argv, bool expected)
    {
        // Act
        bool safe = RestoreLaunch.IsSafe(argv);

        // Assert
        await Assert.That(safe).IsEqualTo(expected).Because(@case);
    }

    /// <summary>Runs the real wrapper: shell metacharacters in the argv reach the program verbatim, then the "shell" runs.</summary>
    [Test]
    [ExcludeOn(OS.Windows)]
    [NotInParallel(Isolation.Processes)]
    public async Task WrapperPassesArgvVerbatimWithoutAShellParse(CancellationToken cancellationToken)
    {
        // Arrange
        Skip.Unless(File.Exists("/bin/sh"), "needs /bin/sh");
        Directory.CreateDirectory(_dir);
        string marker = Path.Combine(_dir, "PWNED");
        string[] evil = ["$(touch " + marker + ")", "`touch " + marker + "`", "; touch " + marker, "' \" $1 ${2} *", "-n", ""];
        var argv = RestoreLaunch.Wrap(["printf", "<%s>", .. evil], ["printf", "|shell %s|", "-l"])!;
        var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _dir };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);
        // Only PATH (to find printf): nothing of the terminal session the tests run in reaches the child.
        string path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        psi.Environment.Clear();
        psi.Environment["PATH"] = path;

        // Act
        using var p = Process.Start(psi)!;
        string output = await p.StandardOutput.ReadToEndAsync(cancellationToken);
        await p.WaitForExitAsync(cancellationToken);
        bool markerCreated = File.Exists(marker);

        // Assert
        await Assert.That(output).IsEqualTo(string.Concat(evil.Select(e => "<" + e + ">")) + "|shell -l|");
        await Assert.That(markerCreated).IsFalse();
    }

    [Test]
    [Arguments("nvim\0a b\0\0", new[] { "nvim", "a b", "" })]
    [Arguments("less", new[] { "less" })]
    public async Task ParsesProcCmdline(string cmdline, string[] expected)
    {
        // Arrange
        byte[] raw = Encoding.UTF8.GetBytes(cmdline);

        // Act
        var argv = ErikTerm.Pty.ProcessInfo.ParseCommandLine(raw);

        // Assert
        await Assert.That(argv!).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task EmptyProcCmdlineIsNothing()
    {
        // Act
        var argv = ErikTerm.Pty.ProcessInfo.ParseCommandLine(""u8);

        // Assert
        await Assert.That(argv).IsNull();
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task ReadsOwnCommandLineOnLinux()
    {
        // Act
        var commandLine = ErikTerm.Pty.ProcessInfo.CommandLine(Environment.ProcessId);

        // Assert
        await Assert.That(commandLine).IsNotNull();
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task ReadsOwnWorkingDirectoryOnLinux()
    {
        // Arrange
        string expected = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('/');

        // Act
        string? cwd = ErikTerm.Pty.ProcessInfo.WorkingDirectory(Environment.ProcessId)?.TrimEnd('/');

        // Assert
        await Assert.That(cwd).IsEqualTo(expected);
    }
}

public class SessionScrollbackTests
{
    private static Terminal Feed(Terminal t, string s)
    {
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    /// <summary>Four lines: plain, bold red, a soft-wrapped truecolor one, and the cursor line.</summary>
    private static Terminal StyledLines() =>
        Feed(new Terminal(10, 4, 100), "one\r\n\e[1;31mred\e[0m plain\r\n\e[38;2;1;2;3mabcdefghijklm\e[0m\r\nlast");

    /// <summary>Four lines in different styles, as a session saves them.</summary>
    private static List<string> SavedLines() =>
        Feed(new Terminal(20, 3, 100), "alpha\r\n\e[7mbeta\e[0m\r\n\e[38;5;200mgamma\e[0m\r\ndelta").ExportStyledLines(10);

    [Test]
    public async Task ExportsTheLastLinesWithStyle()
    {
        // Arrange
        var t = StyledLines();

        // Act
        var lines = t.ExportStyledLines(3);

        // Assert
        await Assert.That(lines.Count).IsEqualTo(3);
        await Assert.That(lines[0]).IsEqualTo("\e[0;1;31mred\e[0m plain");
        await Assert.That(lines[1]).IsEqualTo("\e[0;38;2;1;2;3mabcdefghijklm\e[0m"); // soft wrap joined
        await Assert.That(lines[2]).IsEqualTo("last");
    }

    [Test]
    public async Task ExportSkipsTrailingBlankRows()
    {
        // Arrange
        var t = StyledLines();

        // Act
        var lines = t.ExportStyledLines(100);

        // Assert
        await Assert.That(lines.Count).IsEqualTo(4); // trailing blank rows skipped
    }

    [Test]
    public async Task ExportingNoLinesGivesNothing()
    {
        // Arrange
        var t = StyledLines();

        // Act
        var lines = t.ExportStyledLines(0);

        // Assert
        await Assert.That(lines).IsEmpty();
    }

    [Test]
    public async Task ExportsTheMainScreenWhileAProgramUsesTheAlternateOne()
    {
        // Arrange
        var t = Feed(new Terminal(10, 4, 100), "shell\e[?1049h\e[Hnvim stuff");

        // Act
        var lines = t.ExportStyledLines(10);

        // Assert
        await Assert.That(lines).IsEquivalentTo(["shell"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReplayReproducesTheText()
    {
        // Arrange
        var lines = SavedLines();
        var dst = new Terminal(20, 3, 100);

        // Act
        dst.Feed(SessionScrollback.Replay(lines));
        string text = dst.GetText(dst.FirstLine, 0, dst.EndLine - 1, 19).TrimEnd('\n');

        // Assert
        await Assert.That(text).IsEqualTo("alpha\nbeta\ngamma\ndelta\n── restored session ──");
    }

    [Test]
    public async Task ReplayReproducesTheStylesAndDimsTheMarker()
    {
        // Arrange
        var lines = SavedLines();
        var dst = new Terminal(20, 3, 100);

        // Act
        dst.Feed(SessionScrollback.Replay(lines));
        var beta = dst.GetLine(dst.FirstLine + 1)!.Cells[0];
        var gamma = dst.GetLine(dst.FirstLine + 2)!.Cells[0];
        var marker = dst.GetLine(dst.FirstLine + 4)!.Cells[0];

        // Assert
        await Assert.That((beta.Flags & CellAttributes.Inverse) != 0).IsTrue();
        await Assert.That(gamma.Fg).IsEqualTo(TermColor.Indexed(200));
        await Assert.That((marker.Flags & CellAttributes.Dim) != 0).IsTrue();
    }

    [Test]
    public async Task ShellStartsOnAFreshLineAfterTheReplay()
    {
        // Arrange
        var lines = SavedLines();
        var dst = new Terminal(20, 3, 100);

        // Act
        dst.Feed(SessionScrollback.Replay(lines));

        // Assert
        await Assert.That(dst.CursorX).IsEqualTo(0); // the shell starts on a fresh line below
    }

    [Test]
    [Arguments("\e[1mhi\e[0m there", "\e[1mhi\e[0m there")]
    [Arguments("a\rb\e[2Jc\e]0;title\ax", "ab[2Jc]0;titlex")]
    [Arguments("\e[38:2::1:2:3mx", "\e[38:2::1:2:3mx")]
    public async Task ReplayStripsAnythingButSgr(string line, string expected)
    {
        // Act
        string sanitized = SessionScrollback.Sanitize(line);

        // Assert
        await Assert.That(sanitized).IsEqualTo(expected);
    }
}

public class ScrollbackCapacityTests
{
    private static Terminal Lines(int n, int capacity = 100)
    {
        var t = new Terminal(10, 2, capacity);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(i > 0 ? "\r\n" : "").Append('L').Append(i);
        Feed(t, sb.ToString());
        return t;
    }

    private static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));

    private static string Line(Terminal t, long line) => t.GetText(line, 0, line, 9);

    [Test]
    public async Task ShrinkingKeepsTheNewestLinesAndAdvancesDropped()
    {
        // Arrange
        var t = Lines(50); // 48 in scrollback, 2 on screen
        int scrollbackBefore = t.ScrollbackCount;
        long screenTop = t.ScreenTopLine, end = t.EndLine;
        string newest = Line(t, screenTop - 1);

        // Act
        t.ScrollbackCapacity = 10;
        string newestAfter = Line(t, screenTop - 1);
        string oldest = Line(t, t.FirstLine);
        var beforeOldest = t.GetLine(t.FirstLine - 1);

        // Assert
        await Assert.That(scrollbackBefore).IsEqualTo(48);
        await Assert.That(t.ScrollbackCapacity).IsEqualTo(10);
        await Assert.That(t.ScrollbackCount).IsEqualTo(10);
        await Assert.That(t.FirstLine).IsEqualTo(screenTop - 10); // dropped lines advance the numbering
        await Assert.That(t.ScreenTopLine).IsEqualTo(screenTop);   // nothing on screen moved
        await Assert.That(t.EndLine).IsEqualTo(end);
        await Assert.That(newestAfter).IsEqualTo(newest);
        await Assert.That(oldest).IsEqualTo("L38");
        await Assert.That(beforeOldest).IsNull();
    }

    [Test]
    public async Task OutputAfterShrinkingKeepsTheNewCapacity()
    {
        // Arrange
        var t = Lines(50);
        t.ScrollbackCapacity = 10;

        // Act
        Feed(t, "\r\nL50");
        string oldest = Line(t, t.FirstLine);

        // Assert
        await Assert.That(t.ScrollbackCount).IsEqualTo(10);
        await Assert.That(oldest).IsEqualTo("L39");
    }

    [Test]
    public async Task GrowingKeepsEverything()
    {
        // Arrange
        var t = Lines(30, capacity: 5);
        long first = t.FirstLine;

        // Act
        t.ScrollbackCapacity = 1000;

        // Assert
        await Assert.That(t.ScrollbackCount).IsEqualTo(5);
        await Assert.That(t.FirstLine).IsEqualTo(first);
    }

    [Test]
    public async Task GrowingMakesRoom()
    {
        // Arrange
        var t = Lines(30, capacity: 5);
        long first = t.FirstLine;
        t.ScrollbackCapacity = 1000;

        // Act
        Feed(t, "\r\nmore\r\nlines\r\nhere");
        string oldest = Line(t, first);

        // Assert
        await Assert.That(t.ScrollbackCount).IsEqualTo(8);
        await Assert.That(t.FirstLine).IsEqualTo(first);
        await Assert.That(oldest).IsEqualTo("L23");
    }

    [Test]
    public async Task ShrinkingClampsTheView()
    {
        // Arrange
        var t = Lines(20);
        t.ScrollView(15);
        int offsetBefore = t.ViewOffset;

        // Act
        t.ScrollbackCapacity = 4;

        // Assert
        await Assert.That(offsetBefore).IsEqualTo(15);
        await Assert.That(t.ViewOffset).IsEqualTo(4);
    }

    [Test]
    public async Task ZeroCapacityKeepsNoScrollback()
    {
        // Arrange
        var t = Lines(20);
        t.ScrollView(15);
        t.ScrollbackCapacity = 4;

        // Act
        t.ScrollbackCapacity = 0;

        // Assert
        await Assert.That(t.ScrollbackCount).IsEqualTo(0);
        await Assert.That(t.ViewOffset).IsEqualTo(0);
        await Assert.That(t.FirstLine).IsEqualTo(t.ScreenTopLine);
    }

    [Test]
    public async Task ScrollbackRingKeepsNewestAcrossCapacityChanges()
    {
        // Arrange
        static Row R(char c) { var r = new Row(1); r.Cells[0].Rune = c; return r; }
        static string All(Scrollback s) => string.Concat(Enumerable.Range(0, s.Count).Select(i => (char)s[i].Cells[0].Rune));
        var sb = new Scrollback(4);

        // Act
        // Each step works on what the previous one left in the ring.
        foreach (char c in "abcdef") sb.Push(R(c));
        long droppedAfterPushes = sb.Dropped;
        string afterPushes = All(sb);
        sb.SetCapacity(2);
        long droppedAfterShrink = sb.Dropped;
        string afterShrink = All(sb);
        sb.SetCapacity(3);
        sb.Push(R('g'));
        string afterGrowAndPush = All(sb);
        sb.Push(R('h'));
        string afterOneMore = All(sb);
        long droppedAtTheEnd = sb.Dropped;

        // Assert
        await Assert.That(droppedAfterPushes).IsEqualTo(2);
        await Assert.That(afterPushes).IsEqualTo("cdef");
        await Assert.That(droppedAfterShrink).IsEqualTo(4);
        await Assert.That(afterShrink).IsEqualTo("ef");
        await Assert.That(afterGrowAndPush).IsEqualTo("efg");
        await Assert.That(afterOneMore).IsEqualTo("fgh");
        await Assert.That(droppedAtTheEnd).IsEqualTo(5);
    }
}

public class ActivityTrackerTests
{
    private static PaneActivity A(long version, long commands = 0, bool running = false, bool marks = false, long spawned = 0) =>
        new(version, commands, running, marks, spawned);

    [Test]
    public async Task OutputSoonAfterSpawnIsIgnored()
    {
        // Arrange
        var t = new ActivityTracker();
        t.Seen(1, A(0, spawned: 10_000));

        // Act
        bool firstPrompt = t.Check(1, A(5, spawned: 10_000), 10_800);
        bool afterTheGrace = t.Check(1, A(5, spawned: 10_000), 12_000);
        bool laterOutput = t.Check(1, A(6, spawned: 10_000), 12_000);

        // Assert
        await Assert.That(firstPrompt).IsFalse();   // the first prompt
        await Assert.That(afterTheGrace).IsFalse(); // absorbed
        await Assert.That(laterOutput).IsTrue();    // later output counts
    }

    [Test]
    public async Task ResizeRedrawIsIgnored()
    {
        // Arrange
        var t = new ActivityTracker();
        t.Seen(1, A(3));
        t.Resized(1, 50_000);

        // Act
        bool redraw = t.Check(1, A(4), 50_500);
        bool later = t.Check(1, A(5), 51_200);

        // Assert
        await Assert.That(redraw).IsFalse();
        await Assert.That(later).IsTrue();
    }

    [Test]
    public async Task PromptRedrawWithShellIntegrationIsIgnored()
    {
        // Arrange
        var t = new ActivityTracker();
        t.Seen(1, A(10, commands: 3, marks: true));

        // Act
        bool promptRedraw = t.Check(1, A(12, commands: 3, marks: true), 100_000);
        bool commandRan = t.Check(1, A(20, commands: 4, marks: true), 100_000);

        // Assert
        await Assert.That(promptRedraw).IsFalse(); // e.g. an async git prompt segment
        await Assert.That(commandRan).IsTrue();    // a command ran (and finished)
    }

    [Test]
    public async Task OutputOfARunningCommandCounts()
    {
        // Arrange
        var t = new ActivityTracker();
        t.Seen(1, A(10, commands: 3, running: true, marks: true));

        // Act
        bool active = t.Check(1, A(11, commands: 3, running: true, marks: true), 100_000);

        // Assert
        await Assert.That(active).IsTrue();
    }

    [Test]
    public async Task PromptAfterACommandFinishedSinceTheTabWasLeftCounts()
    {
        // Arrange
        // A command that was running when the tab was left and has finished since: its prompt is news.
        var u = new ActivityTracker();
        u.Seen(2, A(10, commands: 3, running: true, marks: true));

        // Act
        bool active = u.Check(2, A(11, commands: 3, running: false, marks: true), 100_000);

        // Assert
        await Assert.That(active).IsTrue();
    }

    [Test]
    public async Task WithoutIntegrationAnyLaterOutputCounts()
    {
        // Arrange
        var t = new ActivityTracker();
        t.Seen(1, A(10));

        // Act
        bool noNewOutput = t.Check(1, A(10), 100_000);
        bool newOutput = t.Check(1, A(11), 100_000);
        bool stillLit = t.Check(1, A(11), 100_001);
        t.Seen(1, A(11));
        bool afterSeen = t.Check(1, A(11), 100_002);

        // Assert
        await Assert.That(noNewOutput).IsFalse();
        await Assert.That(newOutput).IsTrue();
        await Assert.That(stillLit).IsTrue(); // stays lit until seen
        await Assert.That(afterSeen).IsFalse();
    }

    [Test]
    public async Task UnseenPanesStartFromWhatTheyPrinted()
    {
        // Arrange
        var t = new ActivityTracker();

        // Act
        bool firstSight = t.Check(7, A(40), 100_000);
        bool moreOutput = t.Check(7, A(41), 100_000);

        // Assert
        await Assert.That(firstSight).IsFalse(); // first sight: baseline
        await Assert.That(moreOutput).IsTrue();
    }

    [Test]
    public async Task RetainForgetsPanesThatAreGone()
    {
        // Arrange
        var t = new ActivityTracker();
        t.Check(7, A(40), 100_000);
        t.Check(7, A(41), 100_000);

        // Act
        t.Retain(new HashSet<int>());

        // Assert
        await Assert.That(t.Count).IsEqualTo(0);
    }
}

public class PaneCwdTests
{
    private static readonly HashSet<string> Existing = ["/osc7", "/fg", "/shell", "/default"];
    private static bool Exists(string d) => Existing.Contains(d);

    private static string? Resolve(string? osc7, string? fg, string? shell, List<string> calls) =>
        ErikTerm.App.Panes.PaneCwd.Resolve(osc7,
            () => { calls.Add("fg"); return fg; },
            () => { calls.Add("shell"); return shell; },
            "/default", Exists);

    [Test]
    public async Task ReportedDirectoryWinsWithoutLookingUpProcesses()
    {
        // Arrange
        var calls = new List<string>();

        // Act
        string? cwd = Resolve("/osc7", "/fg", "/shell", calls);

        // Assert
        await Assert.That(cwd).IsEqualTo("/osc7");
        await Assert.That(calls).IsEmpty();
    }

    [Test]
    [Arguments("/fg", "/shell", "/fg", new[] { "fg" })]
    [Arguments(null, "/shell", "/shell", new[] { "fg", "shell" })]
    [Arguments(null, null, "/default", new[] { "fg", "shell" })]
    public async Task FallsBackToForegroundThenShellThenDefault(string? fg, string? shell, string expected, string[] expectedCalls)
    {
        // Arrange
        var calls = new List<string>();

        // Act
        string? cwd = Resolve(null, fg, shell, calls);

        // Assert
        await Assert.That(cwd).IsEqualTo(expected);
        await Assert.That(calls).IsEquivalentTo(expectedCalls, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("/gone", "/also-gone", "/shell", "/shell")]
    [Arguments("", "", "/gone", "/default")]
    public async Task SkipsDirectoriesThatNoLongerExist(string osc7, string fg, string shell, string expected)
    {
        // Arrange
        var calls = new List<string>();

        // Act
        string? cwd = Resolve(osc7, fg, shell, calls);

        // Assert
        await Assert.That(cwd).IsEqualTo(expected);
    }

    [Test]
    public async Task WithoutADefaultNothingIsResolved()
    {
        // Act
        string? cwd = ErikTerm.App.Panes.PaneCwd.Resolve(null, () => null, () => null, null, Exists);

        // Assert
        await Assert.That(cwd).IsNull();
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task ReadsTheProcessDirectoryOnLinux()
    {
        // Arrange
        string expected = Directory.GetCurrentDirectory();

        // Act
        string? cwd = ErikTerm.Pty.ProcessInfo.WorkingDirectory(Environment.ProcessId);

        // Assert
        await Assert.That(cwd).IsEqualTo(expected);
    }
}

/// <summary>The title a tab's capture keeps (Windowing/WindowSession.cs saves <see cref="ErikTerm.App.Tab.SessionTitle"/>).</summary>
public class SessionTabTitleTests
{
    [Test]
    public async Task ARenamedTabSavesTheNameNotTheProgramsTitle()
    {
        // Arrange
        var tab = new ErikTerm.App.Tab { AutoTitle = "program-title" };

        // Act
        tab.Rename("logs");

        // Assert
        await Assert.That(tab.SessionTitle).IsEqualTo("logs");
    }

    [Test]
    public async Task AnAutomaticTitleIsNotSaved()
    {
        // Arrange
        var tab = new ErikTerm.App.Tab { AutoTitle = "program-title" };

        // Act
        string? saved = tab.SessionTitle;

        // Assert
        await Assert.That(saved).IsNull();
    }

    [Test]
    public async Task AToolTabsOwnLabelIsNotSaved()
    {
        // Arrange
        var tab = new ErikTerm.App.Tab { CustomTitle = "Settings", TransientTitle = true };

        // Act
        string? saved = tab.SessionTitle;

        // Assert
        await Assert.That(saved).IsNull();
    }

    [Test]
    public async Task AToolTabRenamedByTheUserSavesTheName()
    {
        // Arrange: the Settings tab, renamed (tab bar, palette, or erikterm cli set-tab-title).
        var tab = new ErikTerm.App.Tab { CustomTitle = "Settings", TransientTitle = true };

        // Act
        tab.Rename("config");

        // Assert
        await Assert.That(tab.SessionTitle).IsEqualTo("config");
    }

    [Test]
    public async Task ATabNamedBackToAutomaticSavesNoTitle()
    {
        // Arrange
        var tab = new ErikTerm.App.Tab { CustomTitle = "logs" };

        // Act
        tab.Rename(null);

        // Assert
        await Assert.That(tab.SessionTitle).IsNull();
    }
}

/// <summary>The directory a pane's capture keeps (<see cref="ErikTerm.App.Panes.Pane.SessionDirectory"/>), with real shells (sh: no OSC 7).</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class SessionPaneDirectoryTests
{
    private readonly string _dir = Path.GetFullPath(Directory.CreateTempSubdirectory("erikterm-session-cwd-").FullName);

    [After(Test)]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    private static ErikTerm.App.Panes.Pane Shell(string cwd)
    {
        var pty = ErikTerm.Pty.Pty.Spawn(new ErikTerm.Pty.PtyOptions
        {
            Program = "/bin/sh",
            Arguments = ["sh", "-i"],
            WorkingDirectory = cwd,
            Columns = 80,
            Rows = 24,
        });
        return new ErikTerm.App.Panes.Pane(new Terminal(80, 24, 100), pty);
    }

    private static void Type(ErikTerm.App.Panes.Pane pane, string line) => pane.Pty.Write(Encoding.UTF8.GetBytes(line + "\n"));

    /// <summary>macOS reports /private/var/… for a temp directory under /var/….</summary>
    private static bool Same(string? reported, string expected) =>
        reported is not null && (reported == expected || reported == "/private" + expected);

    [Test]
    public async Task EachPaneKeepsTheFolderItsOwnShellCdInto()
    {
        // Arrange: two panes started in the same folder; only the first cds.
        string start = Directory.CreateDirectory(Path.Combine(_dir, "start")).FullName;
        string moved = Directory.CreateDirectory(Path.Combine(_dir, "moved")).FullName;
        using var first = Shell(start);
        using var second = Shell(start);

        // Act
        Type(first, $"cd '{moved}'");
        bool firstMoved = await Eventually.True(() => Same(first.SessionDirectory, moved));
        string? secondDir = second.SessionDirectory;

        // Assert
        await Assert.That(firstMoved).IsTrue().Because(first.SessionDirectory ?? "(none)");
        await Assert.That(Same(secondDir, start)).IsTrue().Because(secondDir ?? "(none)");
    }

    [Test]
    public async Task APaneKeepsTheFolderAChildShellCdInto()
    {
        // Arrange: the pane's shell starts another sh (its own process group, in the foreground).
        string start = Directory.CreateDirectory(Path.Combine(_dir, "start")).FullName;
        string moved = Directory.CreateDirectory(Path.Combine(_dir, "moved")).FullName;
        using var pane = Shell(start);
        Type(pane, "sh -i");
        bool childRuns = await Eventually.True(() =>
            ErikTerm.Pty.ProcessInfo.ForegroundProcessGroup(pane.Pty) is > 0 and var pgid && pgid != pane.Pty.ProcessId);

        // Act: the child cds; the pane's own shell stays in start.
        Type(pane, $"cd '{moved}'");
        bool childMoved = await Eventually.True(() => Same(pane.SessionDirectory, moved), TimeSpan.FromSeconds(5));

        // Assert
        await Assert.That(childRuns).IsTrue();
        await Assert.That(childMoved).IsTrue().Because(pane.SessionDirectory ?? "(none)");
    }
}
