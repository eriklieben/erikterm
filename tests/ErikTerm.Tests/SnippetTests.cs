using ErikTerm.App.Security;
using ErikTerm.Config;

namespace ErikTerm.Tests;

/// <summary>The snippets setting (both TOML forms), its placeholders, and that inserting one is an ordinary sanitized paste.</summary>
public class SnippetTests
{
    private static LoadResult Parse(string toml) => SettingsLoader.Parse(toml, "cfg.toml");

    [Test]
    public async Task ATableEntryIsNameAndText()
    {
        // Act
        var r = Parse("[snippets]\ngs = \"git status\"\n");

        // Assert
        var s = await Assert.That(r.Settings.Snippets).HasSingleItem();
        await Assert.That(s!.Name).IsEqualTo("gs");
        await Assert.That(s.Text).IsEqualTo("git status");
        await Assert.That(s.Description).IsNull();
    }

    [Test]
    public async Task ATableEntryCanHaveADescription()
    {
        // Act
        var r = Parse("[snippets]\ndeploy = { text = \"make deploy\", description = \"Ship it\" }\n");

        // Assert
        var s = await Assert.That(r.Settings.Snippets).HasSingleItem();
        await Assert.That(s!.Text).IsEqualTo("make deploy");
        await Assert.That(s.Description).IsEqualTo("Ship it");
    }

    [Test]
    public async Task AnArrayOfTablesWorksToo()
    {
        // Act
        var r = Parse("[[snippets]]\nname = \"a\"\ntext = \"one\"\n\n[[snippets]]\nname = \"b\"\ntext = \"two\"\ndescription = \"second\"\n");

        // Assert
        await Assert.That(r.Settings.Snippets.Select(s => s.Name)).IsEquivalentTo(new[] { "a", "b" }, CollectionOrdering.Matching);
        await Assert.That(r.Settings.Snippets[1].Description).IsEqualTo("second");
        await Assert.That(r.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AnInlineArrayWorksToo()
    {
        // Act
        var r = Parse("snippets = [{ name = \"a\", text = \"one\" }]\n");

        // Assert
        await Assert.That(r.Settings.Snippets.Single().Text).IsEqualTo("one");
    }

    [Test]
    [Arguments("[snippets]\nx = 3\n")]
    [Arguments("[snippets]\nx = { description = \"no text\" }\n")]
    [Arguments("[[snippets]]\ntext = \"no name\"\n")]
    [Arguments("snippets = 3\n")]
    public async Task ABadEntryIsReportedAndSkipped(string toml)
    {
        // Act
        var r = Parse(toml);

        // Assert
        await Assert.That(r.Settings.Snippets.Count).IsEqualTo(0);
        await Assert.That(r.Diagnostics.Count).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task AnUnknownFieldIsReported()
    {
        // Act
        var r = Parse("[snippets]\nx = { text = \"t\", colour = \"red\" }\n");

        // Assert
        await Assert.That(r.Diagnostics.Single()).Contains("colour");
        await Assert.That(r.Settings.Snippets.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ADuplicateNameKeepsTheFirst()
    {
        // Act
        var r = Parse("[[snippets]]\nname = \"a\"\ntext = \"one\"\n[[snippets]]\nname = \"A\"\ntext = \"two\"\n");

        // Assert
        await Assert.That(r.Settings.Snippets.Single().Text).IsEqualTo("one");
        await Assert.That(r.Diagnostics.Single()).Contains("twice");
    }

    [Test]
    public async Task AnOverlongTextIsRefused()
    {
        // Act
        var r = Parse($"[snippets]\nx = \"{new string('y', Snippets.MaxTextLength + 1)}\"\n");

        // Assert
        await Assert.That(r.Settings.Snippets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TheSettingsAreKnownKeys()
    {
        // Assert
        await Assert.That(SettingsLoader.KnownKeys).Contains("snippets");
        await Assert.That(SettingsLoader.KnownKeys).Contains("remember_commands");
    }

    // ---- TOML: arrays of tables ---------------------------------------------------------------------

    [Test]
    public async Task ArrayOfTablesCollectsItsTables()
    {
        // Act
        var root = Toml.Parse("[[a]]\nx = 1\n[[a]]\nx = 2\n");

        // Assert
        root.TryGet("a", out var value, out _);
        var list = (List<object>)value;
        await Assert.That(list.Count).IsEqualTo(2);
        ((TomlTable)list[1]).TryGet("x", out var x, out _);
        await Assert.That(x).IsEqualTo(2L);
    }

    [Test]
    public async Task ASubTableGoesIntoTheLastElement()
    {
        // Act
        var root = Toml.Parse("[[a]]\nx = 1\n[a.b]\ny = 2\n");

        // Assert
        root.TryGet("a", out var value, out _);
        var element = (TomlTable)((List<object>)value)[0];
        await Assert.That(element.TryGet("b", out _, out _)).IsTrue();
    }

    [Test]
    public async Task AnArrayOfTablesCantReuseAValue()
    {
        // Act
        var parse = () => Toml.Parse("a = 1\n[[a]]\nx = 1\n");

        // Assert
        await Assert.That(parse).Throws<TomlException>();
    }

    // ---- Placeholders -------------------------------------------------------------------------------

    [Test]
    [Arguments("cd {{cwd}}", "cd /work")]
    [Arguments("grep {{ selection }} .", "grep needle .")]
    [Arguments("{{cwd}}/{{selection}}", "/work/needle")]
    public async Task PlaceholdersAreFilledIn(string text, string expected)
    {
        // Act
        string result = Snippets.Expand(text, "/work", "needle");

        // Assert
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task AMissingValueIsEmpty()
    {
        // Act
        string result = Snippets.Expand("[{{selection}}]", "/w", null);

        // Assert
        await Assert.That(result).IsEqualTo("[]");
    }

    [Test]
    [Arguments("echo {{other}}")]
    [Arguments("echo {{unclosed")]
    [Arguments("no placeholders")]
    public async Task OtherTextStaysAsWritten(string text)
    {
        // Act
        string result = Snippets.Expand(text, "/w", "s");

        // Assert
        await Assert.That(result).IsEqualTo(text);
    }

    [Test]
    public async Task FourBracesAreALiteralPair()
    {
        // Act
        string result = Snippets.Expand("{{{{cwd}}", "/w", null);

        // Assert
        await Assert.That(result).IsEqualTo("{{cwd}}");
    }

    [Test]
    public async Task TheValuesAreNotExpandedAgain()
    {
        // Act
        string result = Snippets.Expand("{{selection}}", "/w", "{{cwd}}");

        // Assert
        await Assert.That(result).IsEqualTo("{{cwd}}");
    }

    // ---- Inserting is a paste -----------------------------------------------------------------------

    [Test]
    public async Task ASelectionCantBreakOutOfTheBracketedPaste()
    {
        // Arrange
        string text = Snippets.Expand("run {{selection}}", null, "x\e[201~rm -rf ~\r");

        // Act
        var paste = PasteSanitizer.Prepare(text, bracketed: true);

        // Assert
        await Assert.That(paste.Text).IsEqualTo("\e[200~run xrm -rf ~\r\e[201~");
    }

    [Test]
    public async Task ControlCharactersInASnippetAreRemoved()
    {
        // Act
        var paste = PasteSanitizer.Prepare("a\u0003b", bracketed: false, sanitize: true);

        // Assert
        await Assert.That(paste.Text).IsEqualTo("ab");
        await Assert.That(paste.Removed).IsEqualTo(1);
    }

    [Test]
    public async Task SendSnippetIsAnActionWithAName()
    {
        // Act
        bool ok = ActionSpec.TryParse("SendSnippet(\"deploy\")", out var action, out _);

        // Assert
        await Assert.That(ok).IsTrue();
        await Assert.That(action.Name).IsEqualTo("SendSnippet");
        await Assert.That(action.Arg).IsEqualTo("deploy");
    }
}
