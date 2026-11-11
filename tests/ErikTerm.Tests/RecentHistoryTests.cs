using System.Text;
using ErikTerm.App.Integrations;
using ErikTerm.App.Session;
using ErikTerm.Config;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>Recent commands and directories: dedupe, order, privacy, persistence.</summary>
public class RecentHistoryTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "et-history-" + Guid.NewGuid().ToString("N"), "history.json");

    private static void Remove(string file)
    {
        string? dir = Path.GetDirectoryName(file);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task ClearReportsAFileItCouldNotDelete()
    {
        // Arrange: a directory where the file should be can't be deleted as a file
        string path = Directory.CreateTempSubdirectory("erikterm-recent").FullName;
        var history = new RecentHistory();

        // Act
        bool cleared = history.Clear(path);
        Directory.Delete(path);

        // Assert
        await Assert.That(cleared).IsFalse();
    }

    [Test]
    public async Task ClearSucceedsWhenThereIsNoFile()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), $"erikterm-recent-{Guid.NewGuid():N}");
        var history = new RecentHistory();

        // Act
        bool cleared = history.Clear(path);

        // Assert
        await Assert.That(cleared).IsTrue();
    }

    [Test]
    public async Task TheMostRecentCommandComesFirst()
    {
        // Arrange
        var h = new RecentHistory();

        // Act
        h.AddCommand("one", null, 1, false, 0);
        h.AddCommand("two", null, 2, false, 0);

        // Assert
        await Assert.That(h.Commands.Select(c => c.Text)).IsEquivalentTo(new[] { "two", "one" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ARepeatedCommandMovesToTheFrontOnce()
    {
        // Arrange
        var h = new RecentHistory();
        h.AddCommand("one", null, 1, false, 0);
        h.AddCommand("two", null, 2, false, 0);

        // Act
        h.AddCommand("one", "/tmp", 3, false, 0);

        // Assert
        await Assert.That(h.Commands.Select(c => c.Text)).IsEquivalentTo(new[] { "one", "two" }, CollectionOrdering.Matching);
        await Assert.That(h.Commands[0].Cwd).IsEqualTo("/tmp");
    }

    [Test]
    public async Task TheListIsCapped()
    {
        // Arrange
        var h = new RecentHistory();

        // Act
        for (int i = 0; i < RecentHistory.MaxCommands + 20; i++) h.AddCommand($"cmd{i}", null, i, false, 0);

        // Assert
        await Assert.That(h.Commands.Count).IsEqualTo(RecentHistory.MaxCommands);
        await Assert.That(h.Commands[0].Text).IsEqualTo($"cmd{RecentHistory.MaxCommands + 19}");
    }

    [Test]
    public async Task APrivateCommandIsNotKept()
    {
        // Arrange
        var h = new RecentHistory();

        // Act
        bool kept = h.AddCommand("export TOKEN=secret", null, 1, isPrivate: true, 0);

        // Assert
        await Assert.That(kept).IsFalse();
        await Assert.That(h.Commands.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(" ls")]
    [Arguments("\tls")]
    [Arguments("a\nb")]
    [Arguments("a\u001bb")]
    [Arguments("")]
    public async Task UnacceptableCommandsAreNotKept(string command)
    {
        // Arrange
        var h = new RecentHistory();

        // Act
        bool kept = h.AddCommand(command, null, 1, false, 0);

        // Assert
        await Assert.That(kept).IsFalse();
    }

    [Test]
    public async Task AnOverlongCommandIsNotKept()
    {
        // Arrange
        var h = new RecentHistory();

        // Act
        bool kept = h.AddCommand(new string('x', RecentHistory.MaxCommandLength + 1), null, 1, false, 0);

        // Assert
        await Assert.That(kept).IsFalse();
    }

    [Test]
    public async Task ADirectoryIsDeduplicated()
    {
        // Arrange
        var h = new RecentHistory();
        h.AddDirectory("/a", 1, 0);
        h.AddDirectory("/b", 2, 0);

        // Act
        h.AddDirectory("/a", 3, 0);

        // Assert
        await Assert.That(h.Directories.Select(d => d.Text)).IsEquivalentTo(new[] { "/a", "/b" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task StayingInADirectoryIsNotAChange()
    {
        // Arrange
        var h = new RecentHistory();
        h.AddDirectory("/a", 1, 0);
        string file = TempFile();
        h.Save(file);

        // Act
        bool changed = h.AddDirectory("/a", 2, 0);

        // Assert
        await Assert.That(changed).IsFalse();
        await Assert.That(h.Dirty).IsFalse();
        Remove(file);
    }

    [Test]
    public async Task ARelativeDirectoryIsNotKept()
    {
        // Arrange
        var h = new RecentHistory();

        // Act
        bool kept = h.AddDirectory("relative/path", 1, 0);

        // Assert
        await Assert.That(kept).IsFalse();
    }

    [Test]
    public async Task SavedHistoryComesBackInOrder()
    {
        // Arrange
        string file = TempFile();
        var h = new RecentHistory();
        h.AddCommand("first", "/x", 100, false, 0);
        h.AddCommand("second", null, 200, false, 0);
        h.AddDirectory("/x", 150, 0);

        // Act
        h.Save(file);
        var loaded = RecentHistory.Load(file);

        // Assert
        await Assert.That(loaded.Commands.Select(c => c.Text)).IsEquivalentTo(new[] { "second", "first" }, CollectionOrdering.Matching);
        await Assert.That(loaded.Commands[1].Cwd).IsEqualTo("/x");
        await Assert.That(loaded.Directories.Single().Text).IsEqualTo("/x");
        Remove(file);
    }

    [Test]
    public async Task SavingClearsDirty()
    {
        // Arrange
        string file = TempFile();
        var h = new RecentHistory();
        h.AddCommand("x", null, 1, false, 0);

        // Act
        h.Save(file);

        // Assert
        await Assert.That(h.Dirty).IsFalse();
        Remove(file);
    }

    [Test]
    public async Task SavingMergesWhatAnotherProcessWrote()
    {
        // Arrange
        string file = TempFile();
        var other = new RecentHistory();
        other.AddCommand("from-other", null, 500, false, 0);
        other.Save(file);
        var mine = new RecentHistory();
        mine.AddCommand("mine", null, 900, false, 0);

        // Act
        mine.Save(file);
        var loaded = RecentHistory.Load(file);

        // Assert
        await Assert.That(loaded.Commands.Select(c => c.Text)).IsEquivalentTo(new[] { "mine", "from-other" }, CollectionOrdering.Matching);
        Remove(file);
    }

    [Test]
    public async Task AMissingFileLoadsEmpty()
    {
        // Act
        var h = RecentHistory.Load(TempFile());

        // Assert
        await Assert.That(h.Commands.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AMalformedFileLoadsEmpty()
    {
        // Arrange
        string file = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ not json");

        // Act
        var h = RecentHistory.Load(file);

        // Assert
        await Assert.That(h.Commands.Count).IsEqualTo(0);
        Remove(file);
    }

    [Test]
    public async Task AnEditedFileCannotSmuggleInControlCharacters()
    {
        // Arrange
        string file = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"version":1,"commands":[{"text":"rm -rf x\u000d","at":1},{"text":"ls","at":2}],"directories":[]}""");

        // Act
        var h = RecentHistory.Load(file);

        // Assert
        await Assert.That(h.Commands.Single().Text).IsEqualTo("ls");
        Remove(file);
    }

    [Test]
    [RunOn(OS.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task TheFileIsPrivate()
    {
        // Arrange
        string file = TempFile();
        var h = new RecentHistory();
        h.AddCommand("x", null, 1, false, 0);

        // Act
        h.Save(file);

        // Assert
        await Assert.That(File.GetUnixFileMode(file)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Remove(file);
    }

    [Test]
    public async Task ClearingDeletesTheFile()
    {
        // Arrange
        string file = TempFile();
        var h = new RecentHistory();
        h.AddCommand("x", null, 1, false, 0);
        h.Save(file);

        // Act
        h.Clear(file);

        // Assert
        await Assert.That(File.Exists(file)).IsFalse();
        await Assert.That(h.Commands.Count).IsEqualTo(0);
        Remove(file);
    }

    // ---- What the terminal says about a command --------------------------------------------------

    private static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));

    private static FinishedCommand Run(Terminal t, string cparams, string typed = "ls")
    {
        Feed(t, $"\e]133;A\e\\$ \e]133;B\e\\{typed}\r\n\e]133;C{cparams}\e\\out\r\n\e]133;D;0\e\\");
        return t.LastFinishedCommand;
    }

    [Test]
    public async Task ACommandLineWithALeadingSpaceIsPrivate()
    {
        // Arrange
        var t = new Terminal(40, 5);

        // Act
        var c = Run(t, ";cmdline_url=%20export%20TOKEN");

        // Assert
        await Assert.That(c.Private).IsTrue();
    }

    [Test]
    public async Task AnOrdinaryCommandLineIsNotPrivate()
    {
        // Arrange
        var t = new Terminal(40, 5);

        // Act
        var c = Run(t, ";cmdline_url=make%20test");

        // Assert
        await Assert.That(c.Private).IsFalse();
    }

    [Test]
    public async Task AScreenCommandTypedWithALeadingSpaceIsPrivate()
    {
        // Arrange
        var t = new Terminal(40, 5);

        // Act
        var c = Run(t, "", typed: " secret");

        // Assert
        await Assert.That(c.Private).IsTrue();
    }

    [Test]
    public async Task AScreenCommandIsNotPrivateByItself()
    {
        // Arrange
        var t = new Terminal(40, 5);

        // Act
        var c = Run(t, "", typed: "ls");

        // Assert
        await Assert.That(c.Private).IsFalse();
    }

    [Test]
    public async Task AShellThatSendsCommandLinesHoldingOneBackMeansPrivate()
    {
        // Arrange
        var t = new Terminal(40, 5);
        Run(t, ";cmdline_url=ls");

        // Act
        var c = Run(t, "", typed: "secret"); // erikterm's bash script: not in history, so no cmdline_url

        // Assert
        await Assert.That(c.Private).IsTrue();
    }

    [Test]
    public async Task AMultiLineCommandIsFlagged()
    {
        // Arrange
        var t = new Terminal(40, 5);

        // Act
        var c = Run(t, ";cmdline_url=for%20x%20in%20a%3B%20do%0Aecho%20x%0Adone");

        // Assert
        await Assert.That(c.MultiLine).IsTrue();
    }

    [Test]
    public async Task ATrailingNewlineIsNotMultiLine()
    {
        // Arrange
        var t = new Terminal(40, 5);

        // Act
        var c = Run(t, ";cmdline_url=ls%0A");

        // Assert
        await Assert.That(c.MultiLine).IsFalse();
    }

    [Test]
    public async Task CommandsFinishingBetweenTwoLooksAreAllThere()
    {
        // Arrange
        var t = new Terminal(40, 5);
        Run(t, ";cmdline_url=first");
        Run(t, ";cmdline_url=second");
        Run(t, ";cmdline_url=third");

        // Act
        var found = new List<string>();
        for (long i = 0; i < t.CommandsFinished; i++)
            if (t.TryGetFinishedCommand(i, out var c)) found.Add(c.CommandLine ?? "");

        // Assert
        await Assert.That(found).IsEquivalentTo(new[] { "first", "second", "third" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task OnlyTheLatestFinishedCommandsAreKept()
    {
        // Arrange
        var t = new Terminal(40, 5);
        for (int i = 0; i < 20; i++) Run(t, $";cmdline_url=c{i}");

        // Act
        bool oldest = t.TryGetFinishedCommand(0, out _);
        bool latest = t.TryGetFinishedCommand(19, out var last);

        // Assert
        await Assert.That(oldest).IsFalse();
        await Assert.That(latest).IsTrue();
        await Assert.That(last.CommandLine).IsEqualTo("c19");
    }

    // ---- The setting ------------------------------------------------------------------------------

    [Test]
    public async Task RememberCommandsIsOnByDefault()
    {
        // Act
        var s = SettingsLoader.Parse("", "cfg.toml").Settings;

        // Assert
        await Assert.That(s.RememberCommands).IsTrue();
    }

    [Test]
    public async Task RememberCommandsCanBeTurnedOff()
    {
        // Act
        var r = SettingsLoader.Parse("remember_commands = false", "cfg.toml");

        // Assert
        await Assert.That(r.Settings.RememberCommands).IsFalse();
        await Assert.That(r.Diagnostics.Count).IsEqualTo(0);
    }

    // ---- Go to: the cd line ------------------------------------------------------------------------

    [Test]
    [Arguments("Posix", "/srv/it's here", @"cd -- '/srv/it'\''s here'")]
    [Arguments("Fish", @"/srv/it's\here", @"cd '/srv/it\'s\\here'")]
    [Arguments("PowerShell", @"C:\it's", @"Set-Location -LiteralPath 'C:\it''s'")]
    [Arguments("Cmd", @"C:\a b", "cd /d \"C:\\a b\"")]
    [Arguments("Nu", "/a b", "cd r#'/a b'#")]
    public async Task TheDirectoryIsQuotedForTheShell(string shell, string dir, string expected)
    {
        // Act
        string? line = ShellCd.Command(dir, Enum.Parse<ShellSyntax>(shell));

        // Assert
        await Assert.That(line).IsEqualTo(expected);
    }

    [Test]
    [Arguments("/a\nrm -rf ~")]
    [Arguments("/a\u001b[201~")]
    [Arguments("")]
    public async Task APathThatCantBeTypedGivesNoCommand(string dir)
    {
        // Act
        string? line = ShellCd.Command(dir, ShellSyntax.Posix);

        // Assert
        await Assert.That(line).IsNull();
    }

    [Test]
    [Arguments("/usr/bin/fish", "Fish")]
    [Arguments("-zsh", "Posix")]
    [Arguments(@"C:\Program Files\PowerShell\7\pwsh.exe", "PowerShell")]
    [Arguments("cmd.exe", "Cmd")]
    [Arguments("nu", "Nu")]
    public async Task TheShellIsTakenFromTheProgramName(string program, string expected)
    {
        // Act
        var shell = ShellCd.Detect(program);

        // Assert
        await Assert.That(shell.ToString()).IsEqualTo(expected);
    }

    [Test]
    [Arguments("zsh", true)]
    [Arguments("-bash", true)]
    [Arguments("nvim", false)]
    public async Task ShellsAreRecognised(string program, bool expected)
    {
        // Act
        bool shell = ShellCd.IsShell(program);

        // Assert
        await Assert.That(shell).IsEqualTo(expected);
    }
}
