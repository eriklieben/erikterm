using System.Text;
using ErikTerm.App;
using ErikTerm.App.Compare;
using ErikTerm.Pty;
using ErikTerm.Ui.Compare;
using static ErikTerm.Tests.CompareFixtures;

namespace ErikTerm.Tests;

/// <summary>The conflict resolver screen: layout, keys, saving and exit codes, then real git through a scripted terminal.</summary>
public class MergeScreenTests
{
    private static MergeFile OneConflict(string path = "src/auth.ts") => Merged("a\nb\nc\n", "a\nours\nc\n", "a\ntheirs\nc\n", path);

    private static MergeFile AutoOnly(string path = "README.md") => Merged("a\nb\nc\n", "A\nb\nc\n", "a\nb\nC\n", path);

    private static void Press(MergeScreen screen, char c) => screen.OnKey(KeyEvent.OfChar(c));

    [Test]
    public async Task WideLayoutHasBothColumnsAndTheOutputPane()
    {
        // Arrange
        var screen = new MergeScreen(Session(new FakeMergeHost(), OneConflict()));

        // Act
        var frame = Render(screen, 140, 40);

        // Assert
        await Assert.That(frame.AllText()).Contains("main");
        await Assert.That(frame.AllText()).Contains("feature/x");
        await Assert.That(frame.AllText()).Contains("ours");
        await Assert.That(frame.AllText()).Contains("theirs");
        await Assert.That(frame.AllText()).Contains("OUTPUT");
        await Assert.That(frame.AllText()).Contains("unresolved: press o, t, b or e");
    }

    [Test]
    public async Task SeveralFilesShowTheFileList()
    {
        // Arrange
        var screen = new MergeScreen(Session(new FakeMergeHost(), OneConflict("src/a.ts"), AutoOnly("src/b.ts")));

        // Act
        var frame = Render(screen, 140, 40);

        // Assert
        await Assert.That(frame.AllText()).Contains("CONFLICTED FILES");
        await Assert.That(frame.AllText()).Contains("src/b.ts");
    }

    [Test]
    public async Task OneFileHasNoFileList()
    {
        // Arrange
        var screen = new MergeScreen(Session(new FakeMergeHost(), OneConflict()));

        // Act
        var frame = Render(screen, 140, 40);

        // Assert
        await Assert.That(frame.AllText()).DoesNotContain("CONFLICTED FILES");
    }

    [Test]
    public async Task NarrowPaneUsesCardsInsteadOfColumns()
    {
        // Arrange
        var screen = new MergeScreen(Session(new FakeMergeHost(), OneConflict()));

        // Act
        var frame = Render(screen, 80, 24);

        // Assert
        await Assert.That(frame.AllText()).DoesNotContain("OUTPUT");
        await Assert.That(frame.AllText()).Contains("conflict 1 of 1");
        await Assert.That(frame.AllText()).Contains("main");
        await Assert.That(frame.AllText()).Contains("ours");
        await Assert.That(frame.AllText()).Contains("theirs");
    }

    [Test]
    [Arguments(99, true)]
    [Arguments(100, false)]
    public async Task CompactThresholdIsAboutHundredColumns(int cols, bool compact)
    {
        // Arrange
        var screen = new MergeScreen(Session(new FakeMergeHost(), OneConflict()));

        // Act
        bool result = screen.IsCompact(cols, 40);

        // Assert
        await Assert.That(result).IsEqualTo(compact);
    }

    [Test]
    public async Task CompactCardShowsTheConflictAfterTakingASide()
    {
        // Arrange
        var screen = new MergeScreen(Session(new FakeMergeHost(), OneConflict()));
        Render(screen, 80, 24);

        // Act
        Press(screen, 'o');
        var frame = Render(screen, 80, 24);

        // Assert
        await Assert.That(frame.AllText()).Contains("took main");
    }

    [Test]
    public async Task TakeTheirsKeyUpdatesTheOutput()
    {
        // Arrange
        var host = new FakeMergeHost();
        var file = OneConflict();
        var screen = new MergeScreen(Session(host, file));
        Render(screen, 140, 40);

        // Act
        Press(screen, 't');

        // Assert
        await Assert.That(file.Output.Any(l => l.Raw == "theirs\n")).IsTrue();
        await Assert.That(file.UnresolvedCount).IsEqualTo(0);
    }

    [Test]
    public async Task UndoKeyRestoresTheConflict()
    {
        // Arrange
        var file = OneConflict();
        var screen = new MergeScreen(Session(new FakeMergeHost(), file));
        Render(screen, 140, 40);
        Press(screen, 'o');

        // Act
        Press(screen, 'u');

        // Assert
        await Assert.That(file.UnresolvedCount).IsEqualTo(1);
    }

    [Test]
    public async Task ClickingTakeTheirsButtonDecides()
    {
        // Arrange
        var file = OneConflict();
        var screen = new MergeScreen(Session(new FakeMergeHost(), file));
        var frame = Render(screen, 140, 40);
        var hit = frame.Hits.First(h => Enumerable.Range(h.X, h.Width).Any(x => frame.RowText(h.Y).Length > x) && Button(frame, h).Contains("Take feature/x"));

        // Act
        screen.OnMouse(new MouseEvent(MouseKind.Press, hit.X + 1, hit.Y), frame);

        // Assert
        await Assert.That(file.DecisionOf(file.Conflicts[0]).Choice).IsEqualTo(ConflictChoice.Theirs);
    }

    private static string Button(Frame frame, HitRegion hit)
    {
        var text = new StringBuilder();
        for (int x = hit.X; x < hit.X + hit.Width; x++) text.Append(frame.Cells[hit.Y * frame.Width + x].Text);
        return text.ToString();
    }

    [Test]
    public async Task NextConflictMovesTheSelection()
    {
        // Arrange — two conflicts with stable lines between them
        var file = Merged("1\n2\n3\n4\n5\n", "O1\n2\n3\n4\nO5\n", "T1\n2\n3\n4\nT5\n");
        var screen = new MergeScreen(Session(new FakeMergeHost(), file));
        Render(screen, 140, 40);

        // Act
        Press(screen, 'n');
        Press(screen, 't');

        // Assert — the second conflict was decided, the first left open
        await Assert.That(file.DecisionOf(file.Conflicts[1]).Choice).IsEqualTo(ConflictChoice.Theirs);
        await Assert.That(file.DecisionOf(file.Conflicts[0]).Choice).IsEqualTo(ConflictChoice.None);
    }

    [Test]
    public async Task SaveRefusesWithUnresolvedConflictsAndOffersMarkers()
    {
        // Arrange
        var host = new FakeMergeHost();
        var screen = new MergeScreen(Session(host, OneConflict()));
        Render(screen, 140, 40);

        // Act
        screen.OnKey(KeyEvent.CtrlOf('s'));
        var frame = Render(screen, 140, 40);

        // Assert — nothing written, a question asked
        await Assert.That(host.Saved.Count).IsEqualTo(0);
        await Assert.That(frame.AllText()).Contains("Save with markers");
    }

    [Test]
    public async Task ConfirmedSaveWithMarkersWritesThemAndDoesNotStage()
    {
        // Arrange
        var host = new FakeMergeHost();
        var file = OneConflict();
        var screen = new MergeScreen(Session(host, file));
        Render(screen, 140, 40);
        screen.OnKey(KeyEvent.CtrlOf('s'));

        // Act
        Press(screen, 'y');

        // Assert
        await Assert.That(host.Saved[file.Path].Text()).Contains("<<<<<<< main");
        await Assert.That(host.Staged).IsEmpty();
    }

    [Test]
    public async Task SaveOfAResolvedFileWritesAndStagesThenAdvances()
    {
        // Arrange
        var host = new FakeMergeHost();
        var a = OneConflict("src/a.ts");
        var b = AutoOnly("src/b.ts");
        var session = Session(host, a, b);
        var screen = new MergeScreen(session);
        Render(screen, 140, 40);
        Press(screen, 'o');

        // Act
        screen.OnKey(KeyEvent.CtrlOf('s'));

        // Assert
        await Assert.That(host.Staged).IsEquivalentTo(["src/a.ts"]);
        await Assert.That(session.Current).IsEqualTo(1);
        await Assert.That(session.Status).Contains("git add -- src/a.ts");
    }

    [Test]
    public async Task ExitCodeIsZeroOnlyWhenEverythingIsSaved()
    {
        // Arrange
        var host = new FakeMergeHost();
        var session = Session(host, OneConflict());
        var screen = new MergeScreen(session);
        Render(screen, 140, 40);
        Press(screen, 'o');

        // Act
        int beforeSave = screen.ExitCode;
        screen.OnKey(KeyEvent.CtrlOf('s'));

        // Assert
        await Assert.That(beforeSave).IsEqualTo(1);
        await Assert.That(screen.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task QuitAsksWhenThereIsAnUnsavedResult()
    {
        // Arrange
        var screen = new MergeScreen(Session(new FakeMergeHost(), OneConflict()));
        Render(screen, 140, 40);
        Press(screen, 'o');

        // Act
        Press(screen, 'q');
        bool doneAfterFirstQ = screen.Done;
        var frame = Render(screen, 140, 40);

        // Assert
        await Assert.That(doneAfterFirstQ).IsFalse();
        await Assert.That(frame.AllText()).Contains("Quit without saving");
    }

    [Test]
    public async Task ConfirmingQuitLeaves()
    {
        // Arrange
        var screen = new MergeScreen(Session(new FakeMergeHost(), OneConflict()));
        Render(screen, 140, 40);
        Press(screen, 'o');
        Press(screen, 'q');

        // Act
        Press(screen, 'y');

        // Assert
        await Assert.That(screen.Done).IsTrue();
        await Assert.That(screen.ExitCode).IsEqualTo(1);
    }

    [Test]
    public async Task QuitWithoutChangesDoesNotAsk()
    {
        // Arrange
        var screen = new MergeScreen(Session(new FakeMergeHost(), OneConflict()));
        Render(screen, 140, 40);

        // Act
        Press(screen, 'q');

        // Assert
        await Assert.That(screen.Done).IsTrue();
    }

    [Test]
    public async Task SaveInFileModeEndsTheProgramSoGitMergetoolContinues()
    {
        // Arrange
        var host = new FakeMergeHost(repoMode: false);
        var screen = new MergeScreen(Session(host, OneConflict()));
        Render(screen, 140, 40);
        Press(screen, 'o');

        // Act
        screen.OnKey(KeyEvent.CtrlOf('s'));

        // Assert
        await Assert.That(screen.Done).IsTrue();
        await Assert.That(screen.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task EditKeyAppliesTheEditorTextToTheConflict()
    {
        // Arrange
        var host = new FakeMergeHost { Editor = _ => "merged by hand\n" };
        var file = OneConflict();
        var screen = new MergeScreen(Session(host, file));
        Render(screen, 140, 40);

        // Act
        Press(screen, 'e');

        // Assert
        await Assert.That(file.Output.Any(l => l.Raw == "merged by hand\n")).IsTrue();
        await Assert.That(file.UnresolvedCount).IsEqualTo(0);
    }

    [Test]
    public async Task DeleteModifyOffersKeepOrDelete()
    {
        // Arrange
        var host = new FakeMergeHost();
        var file = MergeFile.DeleteModify("gone.txt", "main", "feature/x", DeletedSide.Ours, "still here\n"u8.ToArray());
        var screen = new MergeScreen(Session(host, file));

        // Act
        var frame = Render(screen, 140, 40);
        Press(screen, 'x');

        // Assert
        await Assert.That(frame.AllText()).Contains("was deleted on main");
        await Assert.That(frame.AllText()).Contains("Keep the file");
        await Assert.That(file.FileChoice).IsEqualTo(FileChoice.Delete);
    }

    [Test]
    public async Task DeletingADeleteModifyFileSavesADeletion()
    {
        // Arrange
        var host = new FakeMergeHost();
        var file = MergeFile.DeleteModify("gone.txt", "main", "feature/x", DeletedSide.Theirs, "still here\n"u8.ToArray());
        var screen = new MergeScreen(Session(host, file));
        Render(screen, 140, 40);
        Press(screen, 'x');

        // Act
        screen.OnKey(KeyEvent.CtrlOf('s'));

        // Assert
        await Assert.That(host.Saved["gone.txt"]).IsNull();
        await Assert.That(host.Staged).IsEquivalentTo(["gone.txt"]);
    }

    [Test]
    public async Task BinaryFileIsResolvedBySide()
    {
        // Arrange
        var host = new FakeMergeHost();
        byte[] theirs = [9, 9];
        var file = MergeFile.Binary("logo.png", "main", "feature/x", [1], theirs);
        var screen = new MergeScreen(Session(host, file));
        Render(screen, 140, 40);

        // Act
        Press(screen, 't');
        screen.OnKey(KeyEvent.CtrlOf('s'));

        // Assert
        await Assert.That(host.Saved["logo.png"]!).IsEquivalentTo(theirs, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ControlCharactersInFileContentNeverReachTheTerminal()
    {
        // Arrange — an escape sequence inside a line of the file
        var file = Merged("a\n", "x\e]0;pwned\u0007\n", "y\n");
        var screen = new MergeScreen(Session(new FakeMergeHost(), file));

        // Act
        var frame = Render(screen, 140, 40);
        var encoder = new FrameEncoder();
        string bytes = Encoding.UTF8.GetString(encoder.Encode(frame));

        // Assert — our own sequences are fine; the file's ESC must not appear before "]0;"
        await Assert.That(bytes).DoesNotContain("\e]0;");
        await Assert.That(bytes).DoesNotContain("\a");
    }

    // ---- The terminal layer ---------------------------------------------------------------------

    [Test]
    public async Task DecoderReadsPrintableTextAndCtrlS()
    {
        // Arrange
        var decoder = new TuiInputDecoder();
        var events = new List<TuiEvent>();

        // Act
        decoder.Feed("o\x13"u8, events);

        // Assert
        await Assert.That(events[0].Key!.Value.Is('o')).IsTrue();
        await Assert.That(events[1].Key!.Value).IsEqualTo(KeyEvent.CtrlOf('s'));
    }

    [Test]
    public async Task DecoderReadsArrowKeysAndSgrMouse()
    {
        // Arrange
        var decoder = new TuiInputDecoder();
        var events = new List<TuiEvent>();

        // Act
        decoder.Feed("\e[A\e[<0;5;7M\e[<64;1;2M"u8, events);

        // Assert
        await Assert.That(events[0].Key!.Value.Key).IsEqualTo(TuiKey.Up);
        await Assert.That(events[1].Mouse).IsEqualTo(new MouseEvent(MouseKind.Press, 4, 6, 0));
        await Assert.That(events[2].Mouse!.Value.Kind).IsEqualTo(MouseKind.WheelUp);
    }

    [Test]
    public async Task LoneEscapeWaitsThenBecomesEsc()
    {
        // Arrange
        var decoder = new TuiInputDecoder();
        var events = new List<TuiEvent>();
        decoder.Feed([0x1B], events);

        // Act
        bool pending = decoder.EscapePending;
        decoder.FlushEscape(events);

        // Assert
        await Assert.That(pending).IsTrue();
        await Assert.That(events.Single().Key!.Value.Key).IsEqualTo(TuiKey.Esc);
    }

    [Test]
    public async Task SplitSequenceIsBufferedUntilComplete()
    {
        // Arrange
        var decoder = new TuiInputDecoder();
        var events = new List<TuiEvent>();

        // Act
        decoder.Feed("\e[<0;5"u8, events);
        int beforeRest = events.Count;
        decoder.Feed(";7M"u8, events);

        // Assert
        await Assert.That(beforeRest).IsEqualTo(0);
        await Assert.That(events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Osc11ReplyGivesTheTerminalBackground()
    {
        // Arrange
        var decoder = new TuiInputDecoder();
        var events = new List<TuiEvent>();

        // Act
        decoder.Feed("\e]11;rgb:ffff/ffff/ffff\e\\"u8, events);

        // Assert
        await Assert.That(events.Single().BackgroundColor).IsEqualTo(0xFFFFFFu);
    }

    [Test]
    public async Task BrightBackgroundPicksTheLightTheme()
    {
        // Act
        var theme = TuiTheme.ForBackground(0xFAFAFA);

        // Assert
        await Assert.That(theme).IsSameReferenceAs(TuiTheme.Light);
    }

    [Test]
    public async Task EncoderOnlyRepaintsChangedCells()
    {
        // Arrange
        var encoder = new FrameEncoder();
        var first = new Frame(10, 2);
        first.Text(0, 0, 10, "hello", 0xFFFFFF, 0);
        encoder.Encode(first);
        var second = new Frame(10, 2);
        second.Text(0, 0, 10, "hello", 0xFFFFFF, 0);
        second.Put(1, 1, "x", 0xFFFFFF, 0);

        // Act
        string output = Encoding.UTF8.GetString(encoder.Encode(second));

        // Assert
        await Assert.That(output).Contains("x");
        await Assert.That(output).DoesNotContain("hello");
    }

    [Test]
    public async Task WideCharacterTakesTwoCells()
    {
        // Arrange
        var frame = new Frame(10, 1);

        // Act
        int after = frame.Text(0, 0, 10, "日本", 1, 0);

        // Assert
        await Assert.That(after).IsEqualTo(4);
        await Assert.That(frame.RowText(0)).IsEqualTo("日本");
    }

    [Test]
    [Arguments(false, 60, 17, 6, 0x100ul)]
    [Arguments(true, 72, 32, 16, 0x100ul)]
    public async Task RawModeTermiosTablesPerOs(bool macOS, int size, int ccOffset, int vmin, ulong icrnl)
    {
        // Act
        var c = TermiosConstants.For(macOS);

        // Assert
        await Assert.That(c.Size).IsEqualTo(size);
        await Assert.That(c.CcOffset).IsEqualTo(ccOffset);
        await Assert.That(c.VMIN).IsEqualTo(vmin);
        await Assert.That(c.ICRNL).IsEqualTo(icrnl);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MakeRawClearsEchoCanonicalAndSignals(bool macOS)
    {
        // Arrange — a termios with everything set; flag width differs per OS
        var c = TermiosConstants.For(macOS);
        var bytes = new byte[c.Size];
        Array.Fill(bytes, (byte)0xFF);

        // Act
        c.MakeRaw(bytes);

        // Assert
        ulong lflag = c.Flag(bytes, TermiosConstants.LFlag);
        await Assert.That(lflag & c.ECHO).IsEqualTo(0ul);
        await Assert.That(lflag & c.ICANON).IsEqualTo(0ul);
        await Assert.That(lflag & c.ISIG).IsEqualTo(0ul);
        await Assert.That(bytes[c.CcOffset + c.VMIN]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task MergetoolIsItsOwnEntry()
    {
        // Act
        var merge = EntryCommand.Classify(["mergetool"], invokedAsTmux: false, "--helper");

        // Assert
        await Assert.That(merge).IsEqualTo(EntryKind.MergeTool);
    }

    [Test]
    public async Task EditorCommandIsSplitLikeShellWords()
    {
        // Act
        var argv = EditorCommandLine.Split("code --wait \"--some arg\"");

        // Assert
        await Assert.That(argv).IsEquivalentTo(["code", "--wait", "--some arg"], CollectionOrdering.Matching);
    }

    // ---- Real git (temp repositories only) -------------------------------------------------------

    private static (string WaitFor, byte[] Bytes) Keys(string waitFor, string keys) => (waitFor, Encoding.UTF8.GetBytes(keys));

    private static int RunMergeTool(TempRepo repo, FakeTerminal terminal, string[]? args = null, Func<string, string, string?>? editor = null) =>
        MergeTool.Run(terminal, args ?? [], repo.Dir, null, editor);

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task RepoModeResolvesAndStagesButNeverCommits()
    {
        // Arrange — one real conflict and two automatic merges
        using var repo = new TempRepo();
        repo.BuildConflict(
            r => r.Write("file.txt", "one\ntwo\nthree\nfour\nfive\nsix\nseven\n"),
            r => r.Write("file.txt", "one\ntwo-theirs\nthree\nfour\nfive\nsix\nseven\n"),
            r => r.Write("file.txt", "one\ntwo-ours\nthree\nfour\nfive\nsix\nseven\n"));
        string headBefore = repo.Git("rev-parse", "HEAD");
        var terminal = new FakeTerminal(140, 40, Keys("OUTPUT", "t"), Keys("", "\x13"));

        // Act
        int exit = RunMergeTool(repo, terminal);

        // Assert
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(repo.Read("file.txt")).IsEqualTo("one\ntwo-theirs\nthree\nfour\nfive\nsix\nseven\n");
        await Assert.That(repo.Git("rev-parse", "HEAD")).IsEqualTo(headBefore);
        await Assert.That(repo.Git("diff", "--name-only", "--diff-filter=U")).IsEqualTo("");
        await Assert.That(repo.Git("diff", "--cached", "--name-only")).Contains("file.txt");
        await Assert.That(repo.Exists(".git/MERGE_HEAD")).IsTrue(); // still mid-merge: never concluded for you
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task QuittingLeavesTheFileUntouchedAndExitsNonZero()
    {
        // Arrange
        using var repo = new TempRepo();
        repo.BuildConflict(
            r => r.Write("file.txt", "one\ntwo\nthree\n"),
            r => r.Write("file.txt", "one\nTHEIRS\nthree\n"),
            r => r.Write("file.txt", "one\nOURS\nthree\n"));
        string before = repo.Read("file.txt");
        var terminal = new FakeTerminal(140, 40, Keys("OUTPUT", "q"));

        // Act
        int exit = RunMergeTool(repo, terminal);

        // Assert
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(repo.Read("file.txt")).IsEqualTo(before);
        await Assert.That(repo.Git("diff", "--name-only", "--diff-filter=U").Trim()).IsEqualTo("file.txt");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task PathWithSpacesAndConflictWithAutoMergedLinesWork()
    {
        // Arrange — a non-conflicting edit on each side around the conflict
        using var repo = new TempRepo();
        const string Base = "a1\nb\nc\nd\ne\nf\ng2\n";
        repo.BuildConflict(
            r => r.Write("dir with space/my file.txt", Base),
            r => r.Write("dir with space/my file.txt", "a1\nb\nc\nd-theirs\ne\nf\ng2-theirs\n"),
            r => r.Write("dir with space/my file.txt", "a1-ours\nb\nc\nd-ours\ne\nf\ng2\n"));
        var terminal = new FakeTerminal(140, 40, Keys("OUTPUT", "b"), Keys("", "\x13"));

        // Act
        int exit = RunMergeTool(repo, terminal);

        // Assert
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(repo.Read("dir with space/my file.txt")).IsEqualTo("a1-ours\nb\nc\nd-ours\nd-theirs\ne\nf\ng2-theirs\n");
        await Assert.That(repo.Git("ls-files", "-u").Trim()).IsEqualTo("");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task AddAddConflictIsResolvedWithoutABase()
    {
        // Arrange
        using var repo = new TempRepo();
        repo.BuildConflict(
            r => r.Write("base.txt", "x\n"),
            r => r.Write("new.txt", "same\ntheirs\nend\n"),
            r => r.Write("new.txt", "same\nours\nend\n"));
        var terminal = new FakeTerminal(140, 40, Keys("OUTPUT", "o"), Keys("", "\x13"));

        // Act
        int exit = RunMergeTool(repo, terminal);

        // Assert
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(repo.Read("new.txt")).IsEqualTo("same\nours\nend\n");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task ModifyDeleteKeepsTheSurvivingFileWhenAsked()
    {
        // Arrange — deleted on main (ours), changed on the other branch
        using var repo = new TempRepo();
        repo.BuildConflict(
            r => r.Write("f.txt", "one\n"),
            r => r.Write("f.txt", "changed\n"),
            r => File.Delete(Path.Combine(r.Dir, "f.txt")));
        var terminal = new FakeTerminal(140, 40, Keys("Keep the file", "k"), Keys("", "\x13"));

        // Act
        int exit = RunMergeTool(repo, terminal);

        // Assert
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(repo.Read("f.txt")).IsEqualTo("changed\n");
        await Assert.That(repo.Git("ls-files", "-u").Trim()).IsEqualTo("");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task ModifyDeleteDeletesWhenAsked()
    {
        // Arrange
        using var repo = new TempRepo();
        repo.BuildConflict(
            r => r.Write("f.txt", "one\n"),
            r => r.Write("f.txt", "changed\n"),
            r => File.Delete(Path.Combine(r.Dir, "f.txt")));
        var terminal = new FakeTerminal(140, 40, Keys("Delete the file", "x"), Keys("", "\x13"));

        // Act
        int exit = RunMergeTool(repo, terminal);

        // Assert
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(repo.Exists("f.txt")).IsFalse();
        await Assert.That(repo.Git("ls-files", "-u").Trim()).IsEqualTo("");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task FourPathModeWritesMergedAndExitsZero()
    {
        // Arrange
        using var repo = new TempRepo();
        repo.Write("local", "one\nours\nthree\n");
        repo.Write("base", "one\ntwo\nthree\n");
        repo.Write("remote", "one\ntheirs\nthree\n");
        repo.Write("merged", "<<<<<<< placeholder\n");
        string[] args = [Path.Combine(repo.Dir, "local"), Path.Combine(repo.Dir, "base"), Path.Combine(repo.Dir, "remote"), Path.Combine(repo.Dir, "merged")];
        var terminal = new FakeTerminal(140, 40, Keys("OUTPUT", "o"), Keys("", "\x13"));

        // Act
        int exit = RunMergeTool(repo, terminal, args);

        // Assert
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(repo.Read("merged")).IsEqualTo("one\nours\nthree\n");
        await Assert.That(repo.Git("status", "--porcelain").Contains("merged", StringComparison.Ordinal)).IsTrue(); // not staged: git mergetool does that
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task EditorResultIsWrittenFromRepoMode()
    {
        // Arrange
        using var repo = new TempRepo();
        repo.BuildConflict(
            r => r.Write("f.txt", "a\nb\nc\n"),
            r => r.Write("f.txt", "a\nT\nc\n"),
            r => r.Write("f.txt", "a\nO\nc\n"));
        var terminal = new FakeTerminal(140, 40, Keys("OUTPUT", "e"), Keys("", "\x13"));

        // Act
        int exit = RunMergeTool(repo, terminal, editor: (_, _) => "from the editor\n");

        // Assert
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(repo.Read("f.txt")).IsEqualTo("a\nfrom the editor\nc\n");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task TerminalIsRestoredWhenTheScriptEnds()
    {
        // Arrange
        using var repo = new TempRepo();
        repo.BuildConflict(
            r => r.Write("f.txt", "a\nb\nc\n"),
            r => r.Write("f.txt", "a\nT\nc\n"),
            r => r.Write("f.txt", "a\nO\nc\n"));
        var terminal = new FakeTerminal(140, 40, Keys("OUTPUT", "q"));

        // Act
        RunMergeTool(repo, terminal);

        // Assert — left full screen at least once, whatever path exit took
        await Assert.That(terminal.LeaveCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task OutsideARepositoryReportsAnErrorExit()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), "etnotrepo" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var terminal = new FakeTerminal(80, 24);

        // Act
        int exit;
        try { exit = MergeTool.Run(terminal, [], dir, null, null); }
        finally { Directory.Delete(dir, recursive: true); }

        // Assert
        await Assert.That(exit).IsEqualTo(MergeTool.ExitError);
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task NoUnmergedFilesExitsZeroQuietly()
    {
        // Arrange
        using var repo = new TempRepo();
        repo.Write("a", "x\n");
        repo.CommitAll("first");
        var terminal = new FakeTerminal(80, 24);

        // Act
        int exit = RunMergeTool(repo, terminal);

        // Assert
        await Assert.That(exit).IsEqualTo(0);
    }
}
