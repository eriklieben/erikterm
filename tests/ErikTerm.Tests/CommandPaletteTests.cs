using ErikTerm.App.Native;
using ErikTerm.App.Ui;

namespace ErikTerm.Tests;

/// <summary>The command palette's filtering, selection, recency, previews and prompts, driven through its keys.</summary>
public class CommandPaletteTests
{
    private const ushort None = 0, Ctrl = Sdl.KMOD_CTRL, Shift = Sdl.KMOD_SHIFT;

    private sealed class Harness
    {
        public readonly CommandPalette Palette = new();
        public readonly List<(string Id, RunModifiers Mods)> Ran = [];
        public readonly List<string> Previews = []; // "revert" for null

        public Harness()
        {
            Palette.Execute = (cmd, mods) => Ran.Add((cmd.Id, mods));
            Palette.PreviewChanged = cmd => Previews.Add(cmd?.Id ?? "revert");
        }

        public void Open(List<Command> commands, CommandPalette.Options? options = null) => Palette.Open(commands, new Theme(), 0, options);
        public void Key(uint key, ushort mod = None) => Palette.HandleKey(key, mod, 0);
        public void Type(string text) => Palette.HandleText(text, 0);
        public string? Run(ushort mod = None)
        {
            Key(Sdl.K_RETURN, mod);
            return Ran.Count > 0 ? Ran[^1].Id : null;
        }
    }

    private static Command Cmd(string id, string category = "Pane", string? title = null, Func<bool>? enabled = null, bool preview = false, string? detail = null) =>
        new() { Id = id, Category = category, Title = title ?? id, IsEnabled = enabled, Preview = preview ? () => { } : null, Detail = detail };

    private static List<Command> Abc() => [Cmd("alpha"), Cmd("bravo"), Cmd("charlie")];

    [Test]
    public async Task EnterRunsTheFirstCommandInItsNaturalOrder()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());

        // Act
        string? ran = h.Run();

        // Assert
        await Assert.That(ran).IsEqualTo("alpha");
        await Assert.That(h.Palette.IsOpen).IsFalse();
    }

    [Test]
    public async Task DownMovesTheSelection()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());

        // Act
        h.Key(Sdl.K_DOWN);

        // Assert
        await Assert.That(h.Run()).IsEqualTo("bravo");
    }

    [Test]
    public async Task UpFromTheFirstRowWrapsToTheLast()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());

        // Act
        h.Key(Sdl.K_UP);

        // Assert
        await Assert.That(h.Run()).IsEqualTo("charlie");
    }

    [Test]
    public async Task ShiftTabMovesBackAndTabForward()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());

        // Act
        h.Key(Sdl.K_TAB);
        h.Key(Sdl.K_TAB);
        h.Key(Sdl.K_TAB, Shift);

        // Assert
        await Assert.That(h.Run()).IsEqualTo("bravo");
    }

    [Test]
    public async Task PageDownClampsAtTheEndInsteadOfWrapping()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());

        // Act
        h.Key(Sdl.K_PAGEDOWN);
        h.Key(Sdl.K_PAGEDOWN);
        h.Key(Sdl.K_PAGEDOWN);

        // Assert
        await Assert.That(h.Run()).IsEqualTo("charlie");
    }

    [Test]
    public async Task PageUpClampsAtTheStart()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());
        h.Key(Sdl.K_DOWN);

        // Act
        h.Key(Sdl.K_PAGEUP);
        h.Key(Sdl.K_PAGEUP);

        // Assert
        await Assert.That(h.Run()).IsEqualTo("alpha");
    }

    [Test]
    public async Task CtrlEndJumpsToTheLastRow()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());

        // Act
        h.Key(Sdl.K_END, Ctrl);

        // Assert
        await Assert.That(h.Run()).IsEqualTo("charlie");
    }

    [Test]
    public async Task CtrlHomeJumpsToTheFirstRow()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());
        h.Key(Sdl.K_DOWN);

        // Act
        h.Key(Sdl.K_HOME, Ctrl);

        // Assert
        await Assert.That(h.Run()).IsEqualTo("alpha");
    }

    [Test]
    [Arguments('n', "bravo")]
    [Arguments('j', "bravo")]
    [Arguments('p', "charlie")]
    [Arguments('k', "charlie")]
    public async Task EmacsAndVimCtrlKeysMoveTheSelection(char key, string expected)
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());

        // Act
        h.Key(key, Ctrl);

        // Assert
        await Assert.That(h.Run()).IsEqualTo(expected);
    }

    [Test]
    public async Task TheWheelMovesTheSelectionDownForNegativeLines()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());

        // Act
        bool handled = h.Palette.HandleWheel(-1, 0);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(h.Run()).IsEqualTo("bravo");
    }

    [Test]
    public async Task TypingFiltersToTheMatchingCommand()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());

        // Act
        h.Type("char");

        // Assert
        await Assert.That(h.Run()).IsEqualTo("charlie");
    }

    [Test]
    public async Task TheCategoryIsSearchedToo()
    {
        // Arrange
        var h = new Harness();
        h.Open([Cmd("split", "Pane", "Split Right"), Cmd("theme", "Appearance", "Pick a Theme")]);

        // Act
        h.Type("appear");

        // Assert
        await Assert.That(h.Run()).IsEqualTo("theme");
    }

    [Test]
    public async Task TheDetailIsSearchedToo()
    {
        // Arrange
        var h = new Harness();
        h.Open([Cmd("one", detail: "lists files"), Cmd("two", detail: "builds the project")]);

        // Act
        h.Type("builds");

        // Assert
        await Assert.That(h.Run()).IsEqualTo("two");
    }

    [Test]
    public async Task NoMatchMeansEnterRunsNothing()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());
        h.Type("zzzz");

        // Act
        string? ran = h.Run();

        // Assert
        await Assert.That(ran).IsNull();
        await Assert.That(h.Palette.IsOpen).IsTrue();
    }

    [Test]
    public async Task BackspaceWidensTheFilterAgain()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());
        h.Type("bravox");

        // Act
        h.Key(Sdl.K_BACKSPACE);

        // Assert
        await Assert.That(h.Run()).IsEqualTo("bravo");
    }

    [Test]
    public async Task CtrlBackspaceDeletesTheLastWord()
    {
        // Arrange: "zzz" leaves no match, so only deleting the whole word brings "bravo" back.
        var h = new Harness();
        h.Open(Abc());
        h.Type("bravo zzz");

        // Act
        h.Key(Sdl.K_BACKSPACE, Ctrl);

        // Assert
        await Assert.That(h.Run()).IsEqualTo("bravo");
    }

    [Test]
    public async Task CtrlBackspaceOnASingleWordClearsTheQuery()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());
        h.Type("zzz");

        // Act
        h.Key(Sdl.K_BACKSPACE, Ctrl);

        // Assert: the full list is back, in its natural order.
        await Assert.That(h.Run()).IsEqualTo("alpha");
    }

    [Test]
    public async Task BackspaceRemovesASurrogatePairWhole()
    {
        // Arrange: an emoji is two UTF-16 units; one Backspace must take both.
        var h = new Harness();
        h.Open([Cmd("cake", title: "cake")]);
        h.Type("cake\U0001F382");

        // Act
        h.Key(Sdl.K_BACKSPACE);

        // Assert
        await Assert.That(h.Run()).IsEqualTo("cake");
    }

    [Test]
    public async Task BackspaceOnAnEmptyQueryKeepsTheList()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());
        h.Key(Sdl.K_DOWN);

        // Act
        h.Key(Sdl.K_BACKSPACE);

        // Assert: nothing was refiltered, so the selection stayed.
        await Assert.That(h.Run()).IsEqualTo("bravo");
    }

    [Test]
    public async Task CtrlUClearsTheQuery()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());
        h.Type("zzz");

        // Act
        h.Key('u', Ctrl);

        // Assert
        await Assert.That(h.Run()).IsEqualTo("alpha");
    }

    [Test]
    public async Task RecentlyRunCommandsComeFirstNextTime()
    {
        // Arrange
        var h = new Harness();
        var commands = Abc();
        h.Open(commands);
        h.Key(Sdl.K_END, Ctrl);
        h.Palette.HandleKey(Sdl.K_RETURN, None, 500);

        // Act
        h.Open(commands);

        // Assert
        await Assert.That(h.Run()).IsEqualTo("charlie");
    }

    [Test]
    public async Task EqualScoresPreferTheRecentlyUsedCommand()
    {
        // Arrange: "Test" matches both titles equally well; "two" was used last.
        var h = new Harness();
        var commands = new List<Command> { Cmd("one", title: "Test One"), Cmd("two", title: "Test Two") };
        h.Open(commands);
        h.Key(Sdl.K_DOWN);
        h.Palette.HandleKey(Sdl.K_RETURN, None, 500);
        h.Open(commands);

        // Act
        h.Type("Test");

        // Assert
        await Assert.That(h.Run()).IsEqualTo("two");
    }

    [Test]
    public async Task EqualScoresWithoutHistoryAreInTitleOrder()
    {
        // Arrange
        var h = new Harness();
        h.Open([Cmd("b", title: "Test B"), Cmd("a", title: "Test A")]);

        // Act
        h.Type("Test");

        // Assert
        await Assert.That(h.Run()).IsEqualTo("a");
    }

    [Test]
    public async Task ADisabledCommandDoesNotRunAndTheListStays()
    {
        // Arrange
        var h = new Harness();
        h.Open([Cmd("off", enabled: () => false), Cmd("on")]);

        // Act
        string? ran = h.Run();

        // Assert
        await Assert.That(ran).IsNull();
        await Assert.That(h.Palette.IsOpen).IsTrue();
    }

    [Test]
    [Arguments(Sdl.KMOD_SHIFT, (int)RunModifiers.Shift)]
    [Arguments(Sdl.KMOD_CTRL, (int)RunModifiers.Ctrl)]
    [Arguments(Sdl.KMOD_LALT, (int)RunModifiers.Alt)]
    [Arguments((ushort)(Sdl.KMOD_SHIFT | Sdl.KMOD_CTRL), (int)(RunModifiers.Shift | RunModifiers.Ctrl))]
    public async Task EnterPassesTheHeldModifiers(ushort mod, int expected)
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());

        // Act
        h.Key(Sdl.K_KP_ENTER, mod);

        // Assert
        await Assert.That((int)h.Ran[0].Mods).IsEqualTo(expected);
    }

    [Test]
    public async Task HighlightingAPreviewableCommandPreviewsIt()
    {
        // Arrange
        var h = new Harness();
        h.Open([Cmd("plain"), Cmd("dark", preview: true)]);

        // Act
        h.Key(Sdl.K_DOWN);

        // Assert
        await Assert.That(h.Previews).IsEquivalentTo(["dark"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task MovingOffAPreviewRevertsIt()
    {
        // Arrange
        var h = new Harness();
        h.Open([Cmd("plain"), Cmd("dark", preview: true)]);
        h.Key(Sdl.K_DOWN);

        // Act
        h.Key(Sdl.K_UP);

        // Assert
        await Assert.That(h.Previews).IsEquivalentTo(["dark", "revert"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task MovingBetweenPreviewsShowsEachOnce()
    {
        // Arrange
        var h = new Harness();
        h.Open([Cmd("dark", preview: true), Cmd("light", preview: true)]);

        // Act
        h.Key(Sdl.K_DOWN);

        // Assert: opening previewed the first row, moving the second.
        await Assert.That(h.Previews).IsEquivalentTo(["dark", "light"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task EscapeClosesAndRevertsThePreview()
    {
        // Arrange
        var h = new Harness();
        h.Open([Cmd("dark", preview: true)]);

        // Act
        h.Key(Sdl.K_ESCAPE);

        // Assert
        await Assert.That(h.Palette.IsOpen).IsFalse();
        await Assert.That(h.Previews[^1]).IsEqualTo("revert");
    }

    [Test]
    public async Task RunningAPreviewedCommandKeepsThePreview()
    {
        // Arrange
        var h = new Harness();
        h.Open([Cmd("dark", preview: true)]);

        // Act
        h.Run();

        // Assert: no revert after the run.
        await Assert.That(h.Previews).IsEquivalentTo(["dark"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task APromptHandsOverTheTypedTextAndModifiers()
    {
        // Arrange
        var h = new Harness();
        (string Text, RunModifiers Mods)? got = null;
        h.Open([], new CommandPalette.Options { Prompt = (text, mods) => got = (text, mods) });
        h.Type("feature/login");

        // Act
        h.Key(Sdl.K_RETURN, Ctrl);

        // Assert
        await Assert.That(got).IsEqualTo(("feature/login", RunModifiers.Ctrl));
        await Assert.That(h.Palette.IsOpen).IsFalse();
    }

    [Test]
    public async Task ABlankPromptClosesWithoutCallingIt()
    {
        // Arrange
        var h = new Harness();
        bool called = false;
        h.Open([], new CommandPalette.Options { Prompt = (_, _) => called = true });
        h.Type("   ");

        // Act
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(called).IsFalse();
        await Assert.That(h.Palette.IsOpen).IsFalse();
    }

    [Test]
    public async Task APromptIgnoresTheCommandList()
    {
        // Arrange
        var h = new Harness();
        string? got = null;
        h.Open(Abc(), new CommandPalette.Options { Prompt = (text, _) => got = text });
        h.Type("alpha");

        // Act
        h.Key(Sdl.K_RETURN);

        // Assert: the text went to the prompt, no command ran.
        await Assert.That(got).IsEqualTo("alpha");
        await Assert.That(h.Ran).IsEmpty();
    }

    [Test]
    public async Task WhileOpenEveryKeyIsSwallowed()
    {
        // Arrange
        var h = new Harness();
        h.Open(Abc());

        // Act
        bool handled = h.Palette.HandleKey('x', None, 0);

        // Assert
        await Assert.That(handled).IsTrue();
    }

    [Test]
    public async Task WhileClosedNothingIsHandled()
    {
        // Arrange
        var h = new Harness();

        // Act
        bool key = h.Palette.HandleKey(Sdl.K_RETURN, None, 0);
        bool wheel = h.Palette.HandleWheel(1, 0);
        bool move = h.Palette.HandleMouseMove(1, 1, 0);
        bool down = h.Palette.HandleMouseDown(1, 1, 0);
        h.Type("x");

        // Assert
        await Assert.That(key || wheel || move || down).IsFalse();
        await Assert.That(h.Ran).IsEmpty();
    }

    [Test]
    public async Task AClickOutsideThePanelCloses()
    {
        // Arrange: nothing was drawn, so every point is outside the panel.
        var h = new Harness();
        h.Open(Abc());

        // Act
        bool handled = h.Palette.HandleMouseDown(5, 5, 0);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(h.Palette.IsOpen).IsFalse();
        await Assert.That(h.Ran).IsEmpty();
    }

    [Test]
    public async Task ItStaysVisibleWhileItFadesOut()
    {
        // Arrange
        var h = new Harness();
        h.Palette.Open(Abc(), new Theme { AnimationMs = 100 }, 0);
        h.Palette.Close(1000);

        // Act
        bool during = h.Palette.IsVisible(1010);
        bool after = h.Palette.IsVisible(5000);

        // Assert
        await Assert.That(during).IsTrue();
        await Assert.That(after).IsFalse();
    }
}
