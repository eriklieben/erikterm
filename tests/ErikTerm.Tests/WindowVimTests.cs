using ErikTerm.App;
using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.App.Vim;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.VimMode;

namespace ErikTerm.Tests;

public class WindowVimTests
{
    private sealed class FakePane : IVimPane
    {
        public FakePane(int id, string label = "") { Id = id; Label = label; Term.Feed("hello world\r\n"u8); }
        public int Id { get; }
        public Terminal Term { get; } = new(40, 6, 100);
        public string Label { get; }
        public bool LayoutHidden { get; init; }
        public VimEngine? Vim { get; set; }
        public Selection? Selection { get; set; }
        public RectI Target => new(0, 0, 400, 120);
        public string? AgentName { get; init; }
        public string? TmuxTitle { get; init; }
        public bool RunsProgram { get; init; }
        public RectF OverlayArea(long now) => new(0, 0, 400, 120);
        public void SnapScroll() { }
    }

    private sealed class FakeSearch : IVimSearch<FakePane>
    {
        public FakePane? Pane { get; private set; }
        public bool IsVim { get; private set; }
        public string? VimError => null;
        public TerminalSearch Engine { get; } = new();
        public void BeginVim(FakePane pane, string pattern, Theme th, long now) { Pane = pane; IsVim = true; }
        public void EndVim(long now) => IsVim = false;
        public void CompleteScan(Terminal t, double budgetMs = 60) { }
        public void SetCurrent(SearchMatch? match, long now) { }
    }

    private sealed class FakeProcess : IVimProcess
    {
        public Dictionary<KeyChord, ActionSpec> Keys { get; } = [];
        public ColorScheme? Override;
        public int Applied;
        public readonly List<ConfigOverride> Added = [];
        public List<ColorScheme> AllSchemes() => [.. ColorSchemes.All];
        public void SetSchemeOverride(ColorScheme scheme) => Override = scheme;
        public void ApplySchemeToAllWindows() => Applied++;
        public void AddSessionOverride(ConfigOverride setting) => Added.Add(setting);
    }

    private sealed class FakeWindow(FakePane first) : IVimWindow<FakePane>
    {
        public WindowVim<FakePane> Vim = null!;
        public FakePane Focused { get; set; } = first;
        public FakePane? Zoomed { get; set; }
        public List<LayoutTree<FakePane>> Tabs { get; } = [new LayoutTree<FakePane>(first)];
        public LayoutTree<FakePane> Layout => Tabs[ActiveTabIndex];
        public int ShownPaneCount => Layout.LeafList.Count(p => !p.LayoutHidden);
        public int TabCount => Tabs.Count;
        public int ActiveTabIndex { get; set; }
        public Theme Theme { get; } = new();
        public Theme OverlayTheme => Theme;
        public bool TabBarEditing => false;
        public bool SuppressText { get; set; }
        public bool QuickSelectOpen => false;
        public ColorScheme? SchemePreview { get; set; }

        public readonly List<string> Calls = [];
        public readonly List<string> Toasts = [];
        public string CopiedText = "";
        public bool Animated;
        public (int TabIndex, FakePane Pane) IpcTargetResult;
        public int IpcTabResult;
        public bool MoveResult = true;

        public LayoutTree<FakePane> TabLayout(int tabIndex) => Tabs[tabIndex];
        public List<FakePane> VisiblePanes() => Layout.LeafList;
        public FakePane? FindPane(int id) => Layout.LeafList.FirstOrDefault(p => p.Id == id);
        public void Focus(FakePane pane) { Calls.Add($"focus:{pane.Id}"); Focused = pane; }
        public void SetZoom(FakePane? pane) => Zoomed = pane;
        public void ApplyLayout(bool animate) => Animated = animate;
        public void ActivateTab(int index) { Calls.Add($"tab:{index}"); ActiveTabIndex = index; }
        public void ClearTabZoom(int tabIndex) => Calls.Add($"clear-zoom:{tabIndex}");
        public void Split(SplitAxis axis, IReadOnlyList<string>? command) => Calls.Add($"split:{axis}");
        public void SplitRun(string[] argv) => Calls.Add("split-run");
        public void ClosePane(FakePane pane) => Calls.Add($"close:{pane.Id}");
        public void RequestClosePane(FakePane pane) => Calls.Add($"request-close:{pane.Id}");
        public void CloseActiveTab(bool force) => Calls.Add($"close-tab:{force}");
        public void SpawnTab() => Calls.Add("new-tab");
        public bool MovePaneToNewTab(FakePane pane) { Calls.Add($"move:{pane.Id}"); return MoveResult; }
        public void ActivateDirection(Direction direction) => Calls.Add($"dir:{direction}");
        public bool GrowFocused(SplitAxis axis, int cells) => true;
        public void Execute(ActionSpec action, uint key, ushort mod) => Calls.Add($"execute:{action.Name}");
        public void PasteText(FakePane pane, string text) => Calls.Add($"paste:{pane.Id}:{text}");
        public (int TabIndex, FakePane Pane) IpcTarget(IpcRequest request) => IpcTargetResult;
        public int IpcTab(IpcRequest request) => IpcTabResult;
        public bool FindLinkAt(FakePane pane, long line, int col, out string uri, out SmartLink? smart) { uri = ""; smart = null; return false; }
        public void OpenSmartLink(FakePane pane, SmartLink link) { }
        public void OpenLink(string uri) { }
        public string? PreferredEditor() => null;
        public (FakePane Pane, string Server)? RunningNvim() => null;
        public void RunInBackground(Func<string?> work, Action<string?> then) { }
        public void SetClipboard(string text) => CopiedText = text;
        public void SetPrimary(string text) { }
        public string Clipboard() => "";
        public string Primary() => "";
        public Theme PaneOverlayTheme(FakePane pane, long now) => Theme;
        public void CloseBarsForVim(long now) { }
        public void Redraw() { }
        public void Toast(ToastKind kind, string title, string? detail = null) => Toasts.Add(title);
        public (int Col, int Row) CellAt(FakePane pane, float x, float y) => (0, 0);
        public string? ShortcutText(string action) => null;
        public void OpenHints(long now) => Calls.Add("hints");
        public void CloseHints(FakePane pane, long now) { }
        public void ApplyScheme() => Calls.Add("apply-scheme");
        public void ScriptKeyDown(uint key, ushort mod, long now) => Vim.KeyDown(key, mod, now);
        public void ScriptTextInput(string text, long now) => Vim.TextInput(text);
    }

    private readonly FakePane _a = new(1, "shell"), _b = new(2, "claude-notes");
    private readonly FakeWindow _window;
    private readonly FakeProcess _process = new();
    private readonly FakeSearch _search = new();
    private readonly WindowVim<FakePane> _sut;

    public WindowVimTests()
    {
        _window = new FakeWindow(_a);
        _window.Layout.Split(_a, _b, SplitAxis.Horizontal);
        _sut = new WindowVim<FakePane>(_window, _process, _search, new Lock());
        _window.Vim = _sut;
    }

    private void Toggle() => _sut.ExecuteAction("ActivateCopyMode", "", default);

    // ---- Key routing -------------------------------------------------------------------------------

    [Test]
    public async Task KeyPressesStandForVimKeysOnlyWithoutAltOrSuper()
    {
        // Act and Assert: each line is one key press
        await Assert.That(VimInput.KeyFor('w', ctrl: true, shift: false, alt: false, super: false)).IsEqualTo(VimKeys.Ctrl('w'));
        await Assert.That(VimInput.KeyFor('[', ctrl: true, shift: false, alt: false, super: false)).IsEqualTo(VimKeys.Esc);
        await Assert.That(VimInput.KeyFor(Sdl.K_TAB, ctrl: false, shift: true, alt: false, super: false)).IsEqualTo(VimKeys.ShiftTab);
        await Assert.That(VimInput.KeyFor(Sdl.K_HOME, ctrl: false, shift: true, alt: false, super: false)).IsNull();
        await Assert.That(VimInput.KeyFor('w', ctrl: true, shift: true, alt: false, super: false)).IsNull();
        await Assert.That(VimInput.KeyFor('w', ctrl: true, shift: false, alt: true, super: false)).IsNull();
    }

    [Test]
    public async Task ABoundCtrlKeyGoesToItsBindingUnlessVimOwnsItOrIsBusy()
    {
        // Act and Assert: Ctrl+h stays bound, Ctrl+w is vim's, a prompt takes everything, Esc always cancels
        await Assert.That(VimInput.TakesFirst(VimKeys.Ctrl('h'), ctrl: true, bound: true, busy: false)).IsFalse();
        await Assert.That(VimInput.TakesFirst(VimKeys.Ctrl('w'), ctrl: true, bound: true, busy: false)).IsTrue();
        await Assert.That(VimInput.TakesFirst(VimKeys.Ctrl('h'), ctrl: true, bound: true, busy: true)).IsTrue();
        await Assert.That(VimInput.TakesFirst(VimKeys.Esc, ctrl: true, bound: true, busy: false)).IsTrue();
    }

    [Test]
    public async Task ABoundKeyRunsItsActionAndVimsOwnCtrlKeyDoesNot()
    {
        // Arrange
        Toggle();
        _process.Keys[KeyNames.Chord('h', Sdl.KMOD_CTRL)!.Value] = new ActionSpec("SwapPane", null);
        _process.Keys[KeyNames.Chord('w', Sdl.KMOD_CTRL)!.Value] = new ActionSpec("EqualizePanes", null);

        // Act
        _sut.KeyDown('h', Sdl.KMOD_CTRL, 0);
        _sut.KeyDown('w', Sdl.KMOD_CTRL, 0);

        // Assert
        await Assert.That(_window.Calls).IsEquivalentTo(new[] { "execute:SwapPane" }, CollectionOrdering.Matching);
        await Assert.That(_a.Vim!.Pending).IsNotEmpty();
    }

    // ---- Files and themes -----------------------------------------------------------------------------

    [Test]
    public async Task ExportedLineNumbersCountSoftWrappedRowsOnce()
    {
        // Arrange: a line wrapped over two rows, then a second line
        var t = new Terminal(5, 6, 100);
        t.Feed("aaaaaaaaaa\r\nb"u8);

        // Act
        long second = VimFiles.CountLines(t, t.FirstLine, t.FirstLine + 2);

        // Assert
        await Assert.That(VimFiles.CountLines(t, t.FirstLine, t.FirstLine)).IsEqualTo(1L);
        await Assert.That(second).IsEqualTo(2L);
    }

    [Test]
    public async Task WritePrivateCreatesAFileOnlyYouCanReadAndWontOverwriteUnlessAsked()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("vim-export-");
        string path = Path.Combine(dir.FullName, "out.txt");
        try
        {
            // Act
            VimFiles.WritePrivate(path, "one", overwrite: false);

            // Assert
            if (!OperatingSystem.IsWindows())
                await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await Assert.That(() => VimFiles.WritePrivate(path, "two", overwrite: false)).Throws<IOException>();
            VimFiles.WritePrivate(path, "two", overwrite: true);
            await Assert.That(File.ReadAllText(path)).IsEqualTo("two");
        }
        finally { dir.Delete(recursive: true); }
    }

    [Test]
    public async Task ThemeLookupPrefersExactThenPrefixThenFuzzy()
    {
        // Arrange
        List<ColorScheme> all = [.. ColorSchemes.All];

        // Act and Assert
        await Assert.That(VimSchemes.Find(all, " tokyo night ")).IsEqualTo(ColorSchemes.TokyoNight);
        await Assert.That(VimSchemes.Find(all, "catppuccin m")).IsEqualTo(ColorSchemes.CatppuccinMacchiato);
        await Assert.That(VimSchemes.Find(all, "tkynght")).IsEqualTo(ColorSchemes.TokyoNight);
        await Assert.That(VimSchemes.Find(all, "zzqq")).IsNull();
    }

    // ---- Yank, selection, hints -------------------------------------------------------------------------

    [Test]
    public async Task YankCopiesAndToastsTheCharacterCount()
    {
        // Act
        _sut.Script("ggvey");

        // Assert
        await Assert.That(_window.CopiedText).IsEqualTo("hello");
        await Assert.That(_window.Toasts).Contains("Copied");
    }

    [Test]
    public async Task AMouseSelectionBecomesVisualOnEnteringAndLeavingClearsIt()
    {
        // Arrange
        long line = _a.Term.FirstLine;
        _a.Selection = new Selection { AnchorLine = line, AnchorCol = 0, HeadLine = line, HeadCol = 5, Mode = SelectionMode.Cell };

        // Act
        Toggle();

        // Assert
        await Assert.That(_a.Vim!.InVisual).IsTrue();
        await Assert.That(_a.Selection!.Vim).IsTrue();

        // Act
        Toggle();

        // Assert
        await Assert.That(_a.Vim).IsNull();
        await Assert.That(_a.Selection).IsNull();
    }

    [Test]
    public async Task AHintYanksItsTextOrMovesTheCursorOfAPaneInVimMode()
    {
        // Arrange
        Toggle();

        // Act
        _sut.HintChosen(_a, "abc", null, yank: true);
        _sut.HintChosen(_a, "abc", new VimPos(_a.Term.FirstLine, 3), yank: false);

        // Assert
        await Assert.That(_window.CopiedText).IsEqualTo("abc");
        await Assert.That(_a.Vim!.Cursor).IsEqualTo(new VimPos(_a.Term.FirstLine, 3));
    }

    // ---- Ex commands ------------------------------------------------------------------------------------

    [Test]
    public async Task SendPrefersAnAgentNameOverALabelAndSkipsItself()
    {
        // Arrange: pane 3's agent name matches, pane 2 only by label, and pane 1 is the sender (its label matches too)
        var agent = new FakePane(3, "other") { AgentName = "claude" };
        _window.Layout.Split(_b, agent, SplitAxis.Vertical);
        _sut.Script("ggvey");

        // Act
        _sut.Script(":send claude<CR>");

        // Assert
        await Assert.That(_window.Calls).Contains("paste:3:hello");
        await Assert.That(_window.Toasts).Contains("Sent to claude (pane 3)");
    }

    [Test]
    public async Task SendWithNothingToSendReportsItInThePrompt()
    {
        // Act
        _sut.Script(":send 2<CR>");

        // Assert
        await Assert.That(_a.Vim!.PromptError).IsNotNull().And.Contains("Nothing to send");
        await Assert.That(_window.Calls.Where(c => c.StartsWith("paste"))).IsEmpty();
    }

    [Test]
    public async Task OnlyRefusesBusyPanesUnlessForced()
    {
        // Arrange
        var busy = new FakePane(3) { RunsProgram = true };
        _window.Layout.Split(_b, busy, SplitAxis.Vertical);

        // Act
        _sut.Script(":only<CR>");

        // Assert
        await Assert.That(_a.Vim!.PromptError).IsNotNull().And.Contains("1 other pane is running");

        // Act
        _sut.Script("<C-u>only!<CR>");

        // Assert: nothing is closed until the key is done, and then the other panes go
        await Assert.That(_window.Calls).IsEquivalentTo(new[] { "close:2", "close:3" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnUnknownLayoutIsAnErrorAndAKnownOneRunsOnlyWithPanesToArrange()
    {
        // Act
        _sut.Script(":layout nope<CR>");

        // Assert
        await Assert.That(_a.Vim!.PromptError).IsNotNull().And.Contains("Unknown layout: nope");

        // Act
        _sut.Script("<C-u>layout even<CR>");

        // Assert: two panes: applied, animated
        await Assert.That(_window.Animated).IsTrue();
    }

    [Test]
    public async Task ThemeCommandIsPreviewedWhileTypedAndEscGoesBack()
    {
        // Act
        _sut.Script(":theme tokyo");

        // Assert
        await Assert.That(_window.SchemePreview).IsEqualTo(ColorSchemes.TokyoNight);

        // Act
        _sut.Script("<Esc>");

        // Assert
        await Assert.That(_window.SchemePreview).IsNull();
        await Assert.That(_window.Calls.Count(c => c == "apply-scheme")).IsEqualTo(2);
    }

    [Test]
    public async Task ThemeCommandPicksTheSchemeForTheSessionAndEndsThePreview()
    {
        // Act
        _sut.Script(":theme tokyo<CR>");

        // Assert
        await Assert.That(_process.Override).IsEqualTo(ColorSchemes.TokyoNight);
        await Assert.That(_process.Applied).IsEqualTo(1);
        await Assert.That(_window.SchemePreview).IsNull();
        await Assert.That(_window.Toasts).Contains("Theme: Tokyo Night");
    }

    [Test]
    public async Task SetAddsASessionOverrideAndRejectsBadInputInThePrompt()
    {
        // Act
        _sut.Script(":set font_size=14<CR>");

        // Assert
        await Assert.That(_process.Added).HasSingleItem();
        await Assert.That(_window.Toasts).Contains("Set for this session");

        // Act
        _sut.Script(":set nokey<CR>");

        // Assert
        await Assert.That(_a.Vim!.PromptError).IsNotNull();
        await Assert.That(_process.Added).HasSingleItem();
    }

    // ---- Ctrl-w, tabs, pane commands ----------------------------------------------------------------------

    [Test]
    public async Task CtrlWWWalksTheShownPanesAndWraps()
    {
        // Arrange: pane 3 is parked (a teammate): it takes no room and isn't a stop
        var parked = new FakePane(3) { LayoutHidden = true };
        _window.Layout.Split(_b, parked, SplitAxis.Vertical);
        Toggle();

        // Act
        _sut.Script("<C-w>w");
        _sut.Script("<C-w>w");

        // Assert: from 1 to 2, then wrapping past the parked pane to 1
        await Assert.That(_window.Calls.Where(c => c.StartsWith("focus"))).IsEquivalentTo(new[] { "focus:2", "focus:1" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task GtWrapsAroundAndNGtPicksATabByNumber()
    {
        // Arrange
        _window.Tabs.Add(new LayoutTree<FakePane>(new FakePane(4)));
        _window.Tabs.Add(new LayoutTree<FakePane>(new FakePane(5)));

        // Act: from tab 0, gT wraps to the last
        _sut.Script("gT");

        // Assert
        await Assert.That(_window.ActiveTabIndex).IsEqualTo(2);

        // Act: {n}gt is 1-based
        _sut.Script("<Esc>");
        Toggle();
        _sut.Script("2gt");

        // Assert
        await Assert.That(_window.Calls).Contains("tab:1");
    }

    [Test]
    public async Task ArrangingNeedsMoreThanOneShownPane()
    {
        // Arrange: pane 2 goes; pane 1 is alone
        _window.Layout.Remove(_b);

        // Act
        _sut.ExecuteAction("RotatePanes", "", default);

        // Assert
        await Assert.That(_window.Toasts).Contains("Only one pane");
        await Assert.That(_window.Animated).IsFalse();
    }

    [Test]
    public async Task PaneCommandsRunAfterTheKeyThatAskedForThem()
    {
        // Act
        _sut.Script(":tabnew<CR>");

        // Assert: :tabnew is deferred, so the key was fully handled before the tab opened
        await Assert.That(_window.Calls).Contains("new-tab");
    }

    // ---- IPC ----------------------------------------------------------------------------------------------

    [Test]
    public async Task SwapPaneInABackgroundTabLeavesItsZoomAndDoesntAnimate()
    {
        // Arrange: tab 1 holds panes 4 and 5; tab 0 is active
        var four = new FakePane(4);
        var five = new FakePane(5);
        var layout = new LayoutTree<FakePane>(four);
        layout.Split(four, five, SplitAxis.Horizontal);
        _window.Tabs.Add(layout);
        _window.IpcTargetResult = (1, four);

        // Act
        var response = _sut.HandleIpc(new IpcRequest { Command = "swap-pane" });

        // Assert
        await Assert.That(response!.Ok).IsTrue();
        await Assert.That(_window.Calls).Contains("clear-zoom:1");
        await Assert.That(_window.Animated).IsFalse();
        await Assert.That(layout.LeafList[0]).IsEqualTo(five);
    }

    [Test]
    public async Task RotateNeedsTwoPanesInTheTab()
    {
        // Arrange
        _window.Tabs.Add(new LayoutTree<FakePane>(new FakePane(4)));
        _window.IpcTabResult = 1;

        // Act and Assert
        await Assert.That(() => _sut.HandleIpc(new IpcRequest { Command = "rotate-panes" })).Throws<IpcException>().WithMessage("only one pane in the tab");
    }

    [Test]
    public async Task MoveToNewTabShowsTheTabFirstAndReturnsThePane()
    {
        // Arrange: pane 4 in background tab 1
        var four = new FakePane(4);
        var layout = new LayoutTree<FakePane>(four);
        layout.Split(four, new FakePane(5), SplitAxis.Horizontal);
        _window.Tabs.Add(layout);
        _window.IpcTargetResult = (1, four);

        // Act
        var response = _sut.HandleIpc(new IpcRequest { Command = "move-pane-to-new-tab" });

        // Assert
        await Assert.That(response!.PaneId).IsEqualTo(4);
        await Assert.That(_window.Calls).IsEquivalentTo(new[] { "tab:1", "move:4" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnUnknownIpcCommandIsNotVims()
    {
        // Act
        var response = _sut.HandleIpc(new IpcRequest { Command = "list-panes" });

        // Assert
        await Assert.That(response).IsNull();
    }

    // ---- Key and text routing --------------------------------------------------------------------------

    [Test]
    public async Task KeysAreNotVimsOutsideVimMode()
    {
        // Act
        bool key = _sut.KeyDown('j', 0, 0);
        bool text = _sut.TextInput("j");

        // Assert
        await Assert.That(key).IsFalse();
        await Assert.That(text).IsFalse();
    }

    [Test]
    public async Task APrintableKeyWaitsForItsTextEvent()
    {
        // Arrange
        Toggle();

        // Act
        bool handled = _sut.KeyDown('j', 0, 0);

        // Assert: taken, but the text that follows is what vim reads.
        await Assert.That(handled).IsTrue();
        await Assert.That(_window.SuppressText).IsFalse();
    }

    [Test]
    public async Task AnUnboundCtrlKeyReachesNoProgram()
    {
        // Arrange
        Toggle();

        // Act
        bool handled = _sut.KeyDown('q', Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT, 0);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(_window.SuppressText).IsTrue();
        await Assert.That(_window.Calls.Where(c => c.StartsWith("execute"))).IsEmpty();
    }

    [Test]
    public async Task TheCopyModeKeyLeavesVimMode()
    {
        // Arrange
        Toggle();
        _process.Keys[KeyNames.Chord('x', Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT)!.Value] = new ActionSpec("ActivateCopyMode", null);

        // Act
        _sut.KeyDown('x', Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT, 0);

        // Assert
        await Assert.That(_a.Vim).IsNull();
    }

    [Test]
    public async Task TheSearchKeyOpensVimsSlashPrompt()
    {
        // Arrange
        Toggle();
        _process.Keys[KeyNames.Chord('f', Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT)!.Value] = new ActionSpec("Search", null);

        // Act
        _sut.KeyDown('f', Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT, 0);

        // Assert
        await Assert.That(_a.Vim!.Prompt).IsEqualTo(VimPromptKind.SearchForward);
    }

    [Test]
    public async Task TheCopyKeyYanksAVisualSelection()
    {
        // Arrange
        _sut.Script("ggve");
        _process.Keys[KeyNames.Chord('c', Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT)!.Value] = new ActionSpec("CopyTo", "Clipboard");

        // Act
        _sut.KeyDown('c', Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT, 0);

        // Assert
        await Assert.That(_window.CopiedText).IsEqualTo("hello");
    }

    [Test]
    public async Task TheCopyKeyOutsideVisualModeIsAnOrdinaryAction()
    {
        // Arrange
        Toggle();
        _process.Keys[KeyNames.Chord('c', Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT)!.Value] = new ActionSpec("CopyTo", "Clipboard");

        // Act
        _sut.KeyDown('c', Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT, 0);

        // Assert
        await Assert.That(_window.Calls).Contains("execute:CopyTo");
    }

    [Test]
    public async Task OpenSearchPromptOutsideVimModeIsNotVims()
    {
        // Act
        bool opened = _sut.OpenSearchPrompt();

        // Assert
        await Assert.That(opened).IsFalse();
    }

    [Test]
    public async Task OpenSearchPromptInVimModeOpensSlash()
    {
        // Arrange
        Toggle();

        // Act
        bool opened = _sut.OpenSearchPrompt();

        // Assert
        await Assert.That(opened).IsTrue();
        await Assert.That(_a.Vim!.Prompt).IsEqualTo(VimPromptKind.SearchForward);
    }

    [Test]
    public async Task TypingASearchHighlightsWithVimsSearch()
    {
        // Arrange
        Toggle();

        // Act
        _sut.Script("/wor");

        // Assert
        await Assert.That(_search.IsVim).IsTrue();
        await Assert.That(_search.Pane).IsSameReferenceAs(_a);
    }

    [Test]
    public async Task EscapingASearchEndsTheHighlights()
    {
        // Arrange
        Toggle();
        _sut.Script("/wor");

        // Act
        _sut.Script("<Esc>");

        // Assert
        await Assert.That(_search.IsVim).IsFalse();
    }

    [Test]
    public async Task TickKeepsAPaneInVimModeAndReflowIsOnlyForThem()
    {
        // Arrange
        Toggle();
        _sut.Script("j");

        // Act
        _sut.Tick();
        _sut.Reflowed(_a);
        _sut.Reflowed(_b); // not in vim mode: nothing to do

        // Assert
        await Assert.That(_a.Vim).IsNotNull();
        await Assert.That(_b.Vim).IsNull();
    }

    // ---- The cheat sheet -------------------------------------------------------------------------------

    [Test]
    public async Task GQuestionOpensTheCheatSheetWhichTakesTheKeys()
    {
        // Arrange
        Toggle();
        _sut.Script("g?");

        // Act
        bool key = _sut.KeyDown('j', 0, 0);
        bool text = _sut.TextInput("j");

        // Assert
        await Assert.That(key).IsTrue();
        await Assert.That(text).IsTrue();
        await Assert.That(_window.SuppressText).IsTrue();
        await Assert.That(_sut.IsAnimating(Environment.TickCount64)).IsTrue();
    }

    [Test]
    public async Task EscClosesTheCheatSheetAndVimModeStays()
    {
        // Arrange
        Toggle();
        _sut.Script("g?");

        // Act
        _sut.KeyDown(Sdl.K_ESCAPE, 0, 0);
        bool after = _sut.KeyDown('j', 0, 0);

        // Assert: the next key is vim's again.
        await Assert.That(after).IsTrue();
        await Assert.That(_window.SuppressText).IsFalse();
        await Assert.That(_a.Vim).IsNotNull();
    }

    [Test]
    public async Task TheCheatSheetTakesClicksAndTheWheelWhileOpen()
    {
        // Arrange
        Toggle();
        _sut.Script("g?");

        // Act
        bool wheel = _sut.HelpWheel(1, 0);
        bool click = _sut.HelpMouseDown(1, 1, 0);
        bool afterClose = _sut.HelpWheel(1, 0);

        // Assert: a click outside the card closed it.
        await Assert.That(wheel).IsTrue();
        await Assert.That(click).IsTrue();
        await Assert.That(afterClose).IsFalse();
    }

    [Test]
    public async Task CloseHelpClosesIt()
    {
        // Arrange
        Toggle();
        _sut.Script("g?");

        // Act
        _sut.CloseHelp(0);

        // Assert
        await Assert.That(_sut.HelpWheel(1, 0)).IsFalse();
    }

    // ---- The mouse ---------------------------------------------------------------------------------------

    [Test]
    public async Task ClicksOutsideVimModeAreNotVims()
    {
        // Act
        bool down = _sut.MouseDown(_a, 1, 0, 0);
        bool drag = _sut.MouseDrag(0, 0);
        bool up = _sut.MouseUp();

        // Assert
        await Assert.That(down || drag || up).IsFalse();
    }

    [Test]
    public async Task AClickMovesTheCursor()
    {
        // Arrange
        Toggle();
        _sut.Script("j$");

        // Act
        bool handled = _sut.MouseDown(_a, 1, 0, 0);

        // Assert: the fake window puts every point on the top-left cell.
        await Assert.That(handled).IsTrue();
        await Assert.That(_a.Vim!.Cursor).IsEqualTo(new VimPos(_a.Term.ViewTopLine, 0));
        await Assert.That(_a.Vim!.InVisual).IsFalse();
    }

    [Test]
    public async Task ADoubleClickSelectsTheWord()
    {
        // Arrange
        Toggle();

        // Act
        _sut.MouseDown(_a, 2, 0, 0);

        // Assert
        await Assert.That(_a.Vim!.Mode).IsEqualTo(VimMode.Visual);
        await Assert.That(_a.Vim!.SelectionText()).IsEqualTo("hello");
    }

    [Test]
    public async Task ATripleClickSelectsTheLine()
    {
        // Arrange
        Toggle();

        // Act
        _sut.MouseDown(_a, 3, 0, 0);

        // Assert
        await Assert.That(_a.Vim!.Mode).IsEqualTo(VimMode.VisualLine);
        await Assert.That(_a.Selection!.Mode).IsEqualTo(SelectionMode.Line);
    }

    [Test]
    public async Task AClickUnderAPromptDoesNotMoveTheCursor()
    {
        // Arrange
        Toggle();
        _sut.Script("j:");
        var before = _a.Vim!.Cursor;

        // Act
        bool handled = _sut.MouseDown(_a, 1, 0, 0);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(_a.Vim!.Cursor).IsEqualTo(before);
    }

    [Test]
    public async Task DraggingBelowThePaneStartsAVisualSelection()
    {
        // Arrange
        Toggle();
        _sut.MouseDown(_a, 1, 0, 0);

        // Act: below the pane's rectangle: one line further.
        bool dragged = _sut.MouseDrag(0, 500);
        bool up = _sut.MouseUp();

        // Assert
        await Assert.That(dragged).IsTrue();
        await Assert.That(up).IsTrue();
        await Assert.That(_a.Vim!.InVisual).IsTrue();
    }

    [Test]
    public async Task DraggingOnTheSameCellIsNoSelection()
    {
        // Arrange
        Toggle();
        _sut.MouseDown(_a, 1, 0, 0);

        // Act
        bool dragged = _sut.MouseDrag(0, 10);

        // Assert
        await Assert.That(dragged).IsTrue();
        await Assert.That(_a.Vim!.InVisual).IsFalse();
    }

    [Test]
    public async Task ADragEndsWhenThePaneLeftVimMode()
    {
        // Arrange
        Toggle();
        _sut.MouseDown(_a, 1, 0, 0);
        Toggle();

        // Act
        bool dragged = _sut.MouseDrag(0, 500);

        // Assert
        await Assert.That(dragged).IsFalse();
    }

    // ---- Ctrl-w ------------------------------------------------------------------------------------------

    [Test]
    [Arguments("h", "dir:Left")]
    [Arguments("j", "dir:Down")]
    [Arguments("k", "dir:Up")]
    [Arguments("l", "dir:Right")]
    [Arguments("s", "split:Vertical")]
    [Arguments("n", "split:Vertical")]
    [Arguments("v", "split:Horizontal")]
    [Arguments("q", "request-close:1")]
    [Arguments("c", "request-close:1")]
    [Arguments("T", "move:1")]
    public async Task CtrlWCommandsAskTheWindow(string command, string call)
    {
        // Arrange
        Toggle();

        // Act
        _sut.Script("<C-w>" + command);

        // Assert
        await Assert.That(_window.Calls).Contains(call);
    }

    [Test]
    [Arguments("x")]
    [Arguments("r")]
    [Arguments("R")]
    [Arguments("=")]
    [Arguments("H")]
    [Arguments("J")]
    [Arguments("K")]
    [Arguments("L")]
    [Arguments(">")]
    [Arguments("<")]
    [Arguments("+")]
    [Arguments("-")]
    public async Task CtrlWArrangementsReLayOutAnimated(string command)
    {
        // Arrange
        Toggle();

        // Act
        _sut.Script("<C-w>" + command);

        // Assert
        await Assert.That(_window.Animated).IsTrue();
    }

    [Test]
    public async Task CtrlWOZoomsAndUnzooms()
    {
        // Arrange
        Toggle();

        // Act
        _sut.Script("<C-w>o");
        var zoomed = _window.Zoomed;
        _sut.Script("<C-w>o");

        // Assert
        await Assert.That(zoomed).IsSameReferenceAs(_a);
        await Assert.That(_window.Zoomed).IsNull();
    }

    [Test]
    public async Task CtrlWCapitalWWalksBackwards()
    {
        // Arrange
        Toggle();

        // Act
        _sut.Script("<C-w>W");

        // Assert
        await Assert.That(_window.Calls).Contains("focus:2");
    }

    [Test]
    public async Task AnUnknownCtrlWCommandExplains()
    {
        // Arrange
        Toggle();

        // Act
        _sut.Script("<C-w>y");

        // Assert
        await Assert.That(_window.Toasts).Contains("Ctrl-w y: not a window command");
    }

    [Test]
    public async Task ArrangingLeavesZoomFirst()
    {
        // Arrange
        _window.Zoomed = _a;

        // Act
        _sut.ExecuteAction("SwapPane", "Previous", default);

        // Assert
        await Assert.That(_window.Zoomed).IsNull();
        await Assert.That(_window.Animated).IsTrue();
    }

    [Test]
    [Arguments("SwapPane", "")]
    [Arguments("RotatePanes", "CounterClockwise")]
    [Arguments("EqualizePanes", "")]
    [Arguments("MovePaneToEdge", "")]
    [Arguments("PaneLayout", "tiled")]
    [Arguments("PaneLayout", "even")]
    public async Task ArrangementActionsReLayOutAnimated(string action, string arg)
    {
        // Act
        _sut.ExecuteAction(action, arg, Direction.Left);

        // Assert
        await Assert.That(_window.Animated).IsTrue();
    }

    [Test]
    public async Task AnUnknownPaneLayoutDoesNothing()
    {
        // Act
        _sut.ExecuteAction("PaneLayout", "spiral", default);

        // Assert
        await Assert.That(_window.Animated).IsFalse();
        await Assert.That(_window.Toasts).IsEmpty();
    }

    [Test]
    public async Task MovePaneToNewTabAsksTheWindow()
    {
        // Act
        _sut.ExecuteAction("MovePaneToNewTab", "", default);

        // Assert
        await Assert.That(_window.Calls).Contains("move:1");
    }

    // ---- Ex commands -------------------------------------------------------------------------------------

    [Test]
    [Arguments(":split<CR>", "split:Vertical")]
    [Arguments(":vsplit<CR>", "split:Horizontal")]
    [Arguments(":vsplit htop<CR>", "split:Horizontal")]
    [Arguments(":quit<CR>", "request-close:1")]
    [Arguments(":quit!<CR>", "close:1")]
    [Arguments(":tabclose!<CR>", "close-tab:True")]
    [Arguments(":EqualizePanes<CR>", "execute:EqualizePanes")]
    public async Task ExCommandsAskTheWindow(string keys, string call)
    {
        // Act
        _sut.Script(keys);

        // Assert
        await Assert.That(_window.Calls).Contains(call);
    }

    [Test]
    public async Task TabNextAndPreviousWalkTheTabs()
    {
        // Arrange
        _window.Tabs.Add(new LayoutTree<FakePane>(new FakePane(4)));
        _window.Tabs.Add(new LayoutTree<FakePane>(new FakePane(5)));

        // Act
        _sut.Script(":tabnext<CR>");
        int next = _window.ActiveTabIndex;
        Toggle();
        _sut.Script(":tabprevious 2<CR>");

        // Assert
        await Assert.That(next).IsEqualTo(1);
        await Assert.That(_window.ActiveTabIndex).IsEqualTo(2);
    }

    [Test]
    public async Task TabNextWithANumberGoesToThatTab()
    {
        // Arrange
        _window.Tabs.Add(new LayoutTree<FakePane>(new FakePane(4)));

        // Act
        _sut.Script(":tabnext 9<CR>");

        // Assert: clamped to the last tab.
        await Assert.That(_window.ActiveTabIndex).IsEqualTo(1);
    }

    [Test]
    public async Task ABadActionIsAnErrorInThePrompt()
    {
        // Act
        _sut.Script(":EqualizePanes(now)<CR>");

        // Assert
        await Assert.That(_a.Vim!.PromptError).IsNotNull();
        await Assert.That(_window.Calls.Where(c => c.StartsWith("execute"))).IsEmpty();
    }

    [Test]
    public async Task AnUnknownExCommandIsAnError()
    {
        // Act
        _sut.Script(":frobnicate<CR>");

        // Assert
        await Assert.That(_a.Vim!.PromptError).IsNotNull();
    }

    [Test]
    public async Task WriteSavesTheScrollbackToAFile()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("vim-write-");
        string path = Path.Combine(dir.FullName, "out.txt");
        try
        {
            // Act
            _sut.Script($":write {path}<CR>");

            // Assert
            await Assert.That(File.ReadAllText(path)).StartsWith("hello world");
            await Assert.That(_window.Toasts.Any(t => t.StartsWith("Wrote "))).IsTrue();
        }
        finally { dir.Delete(recursive: true); }
    }

    [Test]
    public async Task WriteWontOverwriteWithoutABang()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("vim-write-");
        string path = Path.Combine(dir.FullName, "out.txt");
        File.WriteAllText(path, "keep");
        try
        {
            // Act
            _sut.Script($":write {path}<CR>");

            // Assert
            await Assert.That(_a.Vim!.PromptError).IsNotNull().And.Contains("File exists");
            await Assert.That(File.ReadAllText(path)).IsEqualTo("keep");
        }
        finally { dir.Delete(recursive: true); }
    }

    [Test]
    public async Task WriteToADirectoryIsAnError()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("vim-write-");
        try
        {
            // Act
            _sut.Script($":write {dir.FullName}<CR>");

            // Assert
            await Assert.That(_a.Vim!.PromptError).IsNotNull().And.Contains("is a directory");
        }
        finally { dir.Delete(recursive: true); }
    }

    [Test]
    public async Task WriteIntoAMissingDirectoryIsAnError()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), "erikterm-missing-" + Guid.NewGuid().ToString("N"), "out.txt");

        // Act
        _sut.Script($":write {path}<CR>");

        // Assert
        await Assert.That(_a.Vim!.PromptError).IsNotNull().And.Contains("No such directory");
    }

    [Test]
    public async Task SendByPaneIdPastesThere()
    {
        // Arrange
        _sut.Script("ggvey");

        // Act
        _sut.Script(":send %2<CR>");

        // Assert
        await Assert.That(_window.Calls).Contains("paste:2:hello");
        await Assert.That(_window.Toasts).Contains("Sent to pane 2 · claude-notes");
    }

    [Test]
    public async Task SendToNobodyIsAnError()
    {
        // Arrange
        _sut.Script("ggvey");

        // Act
        _sut.Script(":send nobody<CR>");

        // Assert
        await Assert.That(_a.Vim!.PromptError).IsNotNull().And.Contains("No pane or agent matches");
    }

    [Test]
    public async Task AnUnknownThemeIsAnError()
    {
        // Act
        _sut.Script(":theme zzqq<CR>");

        // Assert
        await Assert.That(_a.Vim!.PromptError).IsNotNull().And.Contains("Unknown theme");
    }

    // ---- Opening things ---------------------------------------------------------------------------------

    [Test]
    [Arguments("gx", "No link under the cursor")]
    [Arguments("gf", "No file under the cursor")]
    [Arguments("go", "No prompts marked yet")]
    public async Task OpeningWhatIsNotThereExplains(string keys, string toast)
    {
        // Arrange
        Toggle();

        // Act
        _sut.Script(keys);

        // Assert
        await Assert.That(_window.Toasts).Contains(toast);
    }

    [Test]
    public async Task HintsAreTheWindowsQuickSelect()
    {
        // Arrange
        Toggle();

        // Act
        _sut.Script("gh");

        // Assert
        await Assert.That(_window.Calls).Contains("hints");
    }

    // ---- The palette and IPC ----------------------------------------------------------------------------

    [Test]
    public async Task ThePaletteNamesTheVimToggleForTheState()
    {
        // Arrange
        var before = new List<string>();
        var during = new List<(string Title, bool? Enabled)>();

        // Act
        _sut.AddCommands((_, title, _, _, _) => before.Add(title));
        Toggle();
        _sut.AddCommands((_, title, _, _, enabled) => during.Add((title, enabled?.Invoke())));

        // Assert
        await Assert.That(before[0]).IsEqualTo("Vim Mode (Copy Mode)");
        await Assert.That(during[0].Title).IsEqualTo("Leave Vim Mode");
        await Assert.That(during.Count).IsEqualTo(16);
        await Assert.That(during[1].Enabled).IsTrue();
    }

    [Test]
    public async Task SwappingWithAPaneOfAnotherTabFails()
    {
        // Arrange
        _window.IpcTargetResult = (0, _a);

        // Act + Assert
        await Assert.That(() => _sut.HandleIpc(new IpcRequest { Command = "swap-pane", OtherPaneId = 99 })).Throws<IpcException>();
    }

    [Test]
    public async Task SwappingAPaneWithItselfFails()
    {
        // Arrange
        _window.IpcTargetResult = (0, _a);

        // Act + Assert
        await Assert.That(() => _sut.HandleIpc(new IpcRequest { Command = "swap-pane", OtherPaneId = 1 })).Throws<IpcException>();
    }

    [Test]
    public async Task SwappingWithANamedPaneSwapsThem()
    {
        // Arrange
        _window.IpcTargetResult = (0, _a);

        // Act
        var response = _sut.HandleIpc(new IpcRequest { Command = "swap-pane", OtherPaneId = 2 });

        // Assert
        await Assert.That(response!.Ok).IsTrue();
        await Assert.That(_window.Layout.LeafList[0]).IsSameReferenceAs(_b);
    }

    [Test]
    public async Task SwappingInATabOfOnePaneFails()
    {
        // Arrange
        _window.Layout.Remove(_b);
        _window.IpcTargetResult = (0, _a);

        // Act + Assert
        await Assert.That(() => _sut.HandleIpc(new IpcRequest { Command = "swap-pane" })).Throws<IpcException>();
    }

    [Test]
    public async Task RotatingInATabOfOnePaneFails()
    {
        // Arrange
        _window.Layout.Remove(_b);

        // Act + Assert
        await Assert.That(() => _sut.HandleIpc(new IpcRequest { Command = "rotate-panes" })).Throws<IpcException>();
    }

    [Test]
    public async Task MovingAPaneThatIsAloneFails()
    {
        // Arrange
        _window.Layout.Remove(_b);
        _window.IpcTargetResult = (0, _a);

        // Act + Assert
        await Assert.That(() => _sut.HandleIpc(new IpcRequest { Command = "move-pane-to-new-tab" })).Throws<IpcException>();
    }

    [Test]
    public async Task AFailedMoveIsAnError()
    {
        // Arrange
        _window.IpcTargetResult = (0, _a);
        _window.MoveResult = false;

        // Act + Assert
        await Assert.That(() => _sut.HandleIpc(new IpcRequest { Command = "move-pane-to-new-tab" })).Throws<IpcException>();
    }
}
