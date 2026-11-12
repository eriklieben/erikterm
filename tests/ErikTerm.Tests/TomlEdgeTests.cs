using ErikTerm.Config;

namespace ErikTerm.Tests;

/// <summary>TOML forms the config tests don't write: special floats, every escape, line-ending backslashes, CRLF, and the redefinition errors.</summary>
public class TomlEdgeTests
{
    private static object Value(string toml, string key = "v")
    {
        Toml.Parse(toml).TryGet(key, out var value, out _);
        return value;
    }

    [Test]
    [Arguments("v = inf", double.PositiveInfinity)]
    [Arguments("v = +inf", double.PositiveInfinity)]
    [Arguments("v = -inf", double.NegativeInfinity)]
    [Arguments("v = 1e3", 1000.0)]
    [Arguments("v = 1_000.5", 1000.5)]
    public async Task SpecialAndExponentFloats(string toml, double expected)
    {
        // Act
        object value = Value(toml);

        // Assert
        await Assert.That(value).IsEqualTo(expected);
    }

    [Test]
    [Arguments("v = nan")]
    [Arguments("v = -nan")]
    public async Task NotANumber(string toml)
    {
        // Act
        object value = Value(toml);

        // Assert
        await Assert.That(double.IsNaN((double)value)).IsTrue();
    }

    [Test]
    [Arguments("v = 12abc")]
    [Arguments("v = 0xZZ")]
    public async Task ANumberThatIsntOneIsAnError(string toml)
    {
        // Act
        var act = () => Toml.Parse(toml);

        // Assert
        await Assert.That(act).ThrowsExactly<TomlException>();
    }

    [Test]
    [Arguments(@"v = ""\n\r\b\f\e\\""", "\n\r\b\f\e\\")]
    [Arguments(@"v = ""A\U0001F600""", "A\U0001F600")]
    public async Task Escapes(string toml, string expected)
    {
        // Act
        object value = Value(toml);

        // Assert
        await Assert.That(value).IsEqualTo(expected);
    }

    [Test]
    [Arguments(@"v = ""\q""")]           // no such escape
    [Arguments(@"v = ""\u12""")]         // too few digits
    [Arguments(@"v = ""\uD800""")]       // a surrogate is no character
    [Arguments(@"v = ""\U00110000""")]   // past Unicode
    [Arguments(@"v = ""\UFFFFFFFF""")]   // parses negative
    [Arguments("v = \"abc")]             // never closed
    [Arguments("v = \"abc\nx = 1")]
    [Arguments("v = 'abc")]
    [Arguments("v = \"\"\"abc")]
    [Arguments("v = '''abc")]
    [Arguments("v = \"\"\"abc\\")]       // an escape at the very end
    public async Task BrokenStringsAreErrors(string toml)
    {
        // Act
        var act = () => Toml.Parse(toml);

        // Assert
        await Assert.That(act).ThrowsExactly<TomlException>();
    }

    [Test]
    public async Task ALineEndingBackslashJoinsLinesInAMultilineString()
    {
        // Act
        object value = Value("v = \"\"\"one \\\n    two \\\r\n\t three\"\"\"");

        // Assert
        await Assert.That(value).IsEqualTo("one two three");
    }

    [Test]
    public async Task ALineEndingBackslashCountsTheLinesItSkips()
    {
        // Arrange: x is on line 4 after a string that spans lines 1 to 3
        var table = Toml.Parse("v = \"\"\"a \\\n\n  b\"\"\"\nx = 1");

        // Act
        int line = table.LineOf("x");

        // Assert
        await Assert.That(line).IsEqualTo(4);
    }

    [Test]
    public async Task AMultilineStringDropsTheNewlineAfterItsOpeningCrlf()
    {
        // Act
        object value = Value("v = \"\"\"\r\nline\"\"\"");

        // Assert
        await Assert.That(value).IsEqualTo("line");
    }

    [Test]
    public async Task AMultilineLiteralStringKeepsBackslashesAndCountsLines()
    {
        // Arrange
        var table = Toml.Parse("v = '''\na\\b\nc'''\nx = 1");

        // Act
        table.TryGet("v", out var value, out _);

        // Assert
        await Assert.That(value).IsEqualTo("a\\b\nc");
        await Assert.That(table.LineOf("x")).IsEqualTo(4);
    }

    [Test]
    public async Task CrlfLineEndsAndCommentsAfterValues()
    {
        // Act
        var table = Toml.Parse("a = 1 # one\r\nb = 2\r\n");

        // Assert
        await Assert.That(table.Count).IsEqualTo(2);
        await Assert.That(table.LineOf("b")).IsEqualTo(2);
    }

    [Test]
    public async Task LineOfAnUnknownKeyIsZero()
    {
        // Act
        int line = Toml.Parse("a = 1").LineOf("nope");

        // Assert
        await Assert.That(line).IsEqualTo(0);
    }

    [Test]
    public async Task AnEmptyInlineTable()
    {
        // Act
        object value = Value("v = {}");

        // Assert
        await Assert.That(((TomlTable)value).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("v = { a = 1 b = 2 }")]
    [Arguments("v = [1 2]")]
    [Arguments("v = 1 2")]
    public async Task MissingSeparatorsAreErrors(string toml)
    {
        // Act
        var act = () => Toml.Parse(toml);

        // Assert
        await Assert.That(act).ThrowsExactly<TomlException>();
    }

    [Test]
    [Arguments("a = 1\n[[a]]")]               // a value, then an array of tables
    [Arguments("a = [1]\n[[a]]")]             // an array of values
    [Arguments("[a]\n[[a]]")]                 // a table
    [Arguments("a = 1\n[a]")]
    [Arguments("a = 1\n[a.b]")]
    public async Task RedefiningAKeyAsAnotherKindIsAnError(string toml)
    {
        // Act
        var act = () => Toml.Parse(toml);

        // Assert
        await Assert.That(act).ThrowsExactly<TomlException>();
    }

    [Test]
    public async Task ASubTableAfterAnArrayOfTablesGoesIntoItsLastTable()
    {
        // Arrange
        var table = Toml.Parse("[[a]]\nx = 1\n[[a]]\nx = 2\n[a.b]\ny = 3");
        table.TryGet("a", out var a, out _);
        var last = (TomlTable)((List<object>)a)[^1];

        // Act
        bool found = last.TryGet("b", out var b, out _);

        // Assert
        await Assert.That(found).IsTrue();
        await Assert.That(((TomlTable)b).TryGet("y", out _, out _)).IsTrue();
    }

    [Test]
    public async Task AnEmptyArrayValueCantBecomeAnArrayOfTables()
    {
        // Act: TOML 1.0, arrays of tables: appending to a statically defined array, even an empty one, is an error
        var act = () => Toml.Parse("a = []\n[[a]]\nx = 1");

        // Assert
        await Assert.That(act).ThrowsExactly<TomlException>();
    }
}
