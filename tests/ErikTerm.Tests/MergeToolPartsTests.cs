using System.Text;
using ErikTerm.App.Compare;

namespace ErikTerm.Tests;

/// <summary>The merge tool's small parsers: the editor command line and git's unmerged listing.</summary>
public class MergeToolPartsTests
{
    [Test]
    [Arguments("nvim")]
    [Arguments("/usr/bin/vim")]
    [Arguments("vi")]
    [Arguments("nano")]
    [Arguments("emacs")]
    [Arguments("emacsclient")]
    [Arguments("kak")]
    [Arguments("micro")]
    [Arguments("joe")]
    [Arguments("ne")]
    [Arguments("nvim.exe")]   // the extension is dropped
    public async Task TerminalEditorsTakeALineNumber(string program)
    {
        // Act
        bool takes = EditorCommandLine.TakesLineNumber(program);

        // Assert
        await Assert.That(takes).IsTrue();
    }

    [Test]
    [Arguments("code")]
    [Arguments("notepad.exe")]
    [Arguments("subl")]
    [Arguments("")]
    public async Task OtherEditorsDont(string program)
    {
        // Act
        bool takes = EditorCommandLine.TakesLineNumber(program);

        // Assert
        await Assert.That(takes).IsFalse();
    }

    [Test]
    [Arguments("  vim   -u   NONE ", new[] { "vim", "-u", "NONE" })]
    [Arguments("emacs -nw ''", new[] { "emacs", "-nw", "" })]       // an empty quoted word is a word
    [Arguments("'my editor' --flag", new[] { "my editor", "--flag" })]
    [Arguments("a\"b c\"d", new[] { "ab cd" })]                        // quotes inside a word
    [Arguments("", new string[0])]
    public async Task SplitTakesQuotesLikeAShell(string command, string[] expected)
    {
        // Act
        var parts = EditorCommandLine.Split(command);

        // Assert
        await Assert.That(parts).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    private static byte[] Listing(params string[] records) => Encoding.UTF8.GetBytes(string.Join('\0', records) + "\0");

    [Test]
    public async Task StagesAreGroupedByPath()
    {
        // Arrange
        var listing = Listing("100644 aaa 1\tsrc/a.ts", "100644 bbb 2\tsrc/a.ts", "100644 ccc 3\tsrc/a.ts", "100644 ddd 2\tsrc/b.ts");

        // Act
        var stages = UnmergedPaths.Parse(listing);

        // Assert
        await Assert.That(stages.Select(s => (s.Path, s.Base, s.Ours, s.Theirs))).IsEquivalentTo(
            new (string, string?, string?, string?)[] { ("src/a.ts", "aaa", "bbb", "ccc"), ("src/b.ts", null, "ddd", null) }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("120000")]
    [Arguments("160000")]
    public async Task SymlinksAndSubmodulesAreFlagged(string mode)
    {
        // Act
        var stages = UnmergedPaths.Parse(Listing($"{mode} aaa 2\tlink"));

        // Assert
        await Assert.That(stages.Single().HasGitlinkOrSymlink).IsTrue();
    }

    [Test]
    public async Task MalformedRecordsAndUnknownStagesAreSkipped()
    {
        // Arrange: no tab, too few fields, a stage 0 entry
        var listing = Listing("100644 aaa 1 no-tab", "100644 aaa\tshort", "100644 eee 0\tclean.ts");

        // Act
        var stages = UnmergedPaths.Parse(listing);

        // Assert: only the stage 0 path is listed, with no stage taken
        var only = await Assert.That(stages).HasSingleItem();
        await Assert.That((only.Path, only.Base, only.Ours, only.Theirs, only.HasGitlinkOrSymlink))
            .IsEqualTo(("clean.ts", (string?)null, (string?)null, (string?)null, false));
    }
}
