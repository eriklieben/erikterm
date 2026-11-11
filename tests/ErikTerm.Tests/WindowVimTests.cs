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
}
