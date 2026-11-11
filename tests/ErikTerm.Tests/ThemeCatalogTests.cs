using ErikTerm.App.Integrations;
using ErikTerm.Config;

namespace ErikTerm.Tests;

/// <summary>The theme catalogue download: what git is asked to do, and what is kept of what arrives. A fake fetch stands in for git.</summary>
public class ThemeCatalogTests
{
    private const string ValidScheme = """
        [colors]
        foreground = "#ffffff"
        background = "#000000"
        ansi = ["#000000", "#111111", "#222222", "#333333", "#444444", "#555555", "#666666", "#777777"]
        brights = ["#888888", "#999999", "#aaaaaa", "#bbbbbb", "#cccccc", "#dddddd", "#eeeeee", "#ffffff"]

        [metadata]
        name = "Test Scheme"
        """;

    /// <summary>A fetch that writes these files where git would put them, plus optional extras at the top level.</summary>
    private static Func<string, string?> Checkout(Dictionary<string, string> schemes, string? license = "MIT") => dir =>
    {
        string folder = Path.Combine(dir, ThemeCatalog.SchemeFolder);
        Directory.CreateDirectory(folder);
        foreach (var (name, body) in schemes) File.WriteAllText(Path.Combine(folder, name), body);
        if (license is not null) File.WriteAllText(Path.Combine(dir, "LICENSE"), license);
        return null;
    };

    private static string TempTarget() => Path.Combine(Path.GetTempPath(), "et-themes-" + Guid.NewGuid().ToString("N"), "colors", "catalog");

    private static void Remove(string target)
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(target)!)!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    // ---- File names and the git command --------------------------------------------------------------

    [Test]
    [Arguments("Dracula.toml", true)]
    [Arguments("Builtin Solarized (Dark).toml", true)]
    [Arguments("../evil.toml", false)]
    [Arguments("a/b.toml", false)]
    [Arguments("a\\b.toml", false)]
    [Arguments(".hidden.toml", false)]
    [Arguments("x.sh", false)]
    [Arguments("C:evil.toml", false)]
    [Arguments("a\nb.toml", false)]
    public async Task FileNamesAreChecked(string name, bool expected)
    {
        // Act
        bool safe = ThemeCatalog.SafeFileName(name);

        // Assert
        await Assert.That(safe).IsEqualTo(expected);
    }

    [Test]
    public async Task TheCloneIsHttpsOnlyWithoutRedirectsHooksOrSymlinks()
    {
        // Act
        string[] argv = ThemeCatalog.CloneCommand("/tmp/x");

        // Assert
        await Assert.That(argv).Contains("protocol.allow=never");
        await Assert.That(argv).Contains("protocol.https.allow=always");
        await Assert.That(argv).Contains("http.followRedirects=false");
        await Assert.That(argv).Contains("core.symlinks=false");
        await Assert.That(argv.Any(a => a.StartsWith("core.hooksPath=", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task TheCloneNamesItsUrlAfterADoubleDash()
    {
        // Act
        var argv = ThemeCatalog.CloneCommand("/tmp/x").ToList();

        // Assert
        await Assert.That(argv[argv.IndexOf("--") + 1]).IsEqualTo(ThemeCatalog.CloneUrl);
        await Assert.That(ThemeCatalog.CloneUrl).StartsWith("https://github.com/");
    }

    [Test]
    public async Task OnlyTheSchemeFolderIsCheckedOut()
    {
        // Act
        string[] argv = ThemeCatalog.SparseCommand("/tmp/x");

        // Assert
        await Assert.That(argv[^1]).IsEqualTo(ThemeCatalog.SchemeFolder);
        await Assert.That(argv).Contains("core.symlinks=false");
    }

    [Test]
    public async Task GitsErrorIsReducedToOneCleanLine()
    {
        // Act
        string? line = ThemeCatalog.LastLine("Cloning...\nfatal: unable to \u001b[31maccess\n\n");

        // Assert
        await Assert.That(line).IsEqualTo("fatal: unable to [31maccess");
    }

    // ---- What is kept --------------------------------------------------------------------------------

    [Test]
    public async Task ValidSchemesAreSaved()
    {
        // Arrange
        string target = TempTarget();

        // Act
        var result = ThemeCatalog.Download(Checkout(new() { ["Good.toml"] = ValidScheme }), target, null);

        // Assert
        await Assert.That(result.Saved).IsEqualTo(1);
        await Assert.That(result.Error).IsNull();
        await Assert.That(File.Exists(Path.Combine(target, "Good.toml"))).IsTrue();
        Remove(target);
    }

    [Test]
    public async Task SavedSchemesLoadWithTheOrdinaryParser()
    {
        // Arrange
        string target = TempTarget();
        ThemeCatalog.Download(Checkout(new() { ["Good.toml"] = ValidScheme }), target, null);

        // Act
        var scheme = ColorSchemeFiles.Parse(File.ReadAllText(Path.Combine(target, "Good.toml")), "Good.toml", []);

        // Assert
        await Assert.That(scheme).IsNotNull();
        Remove(target);
    }

    [Test]
    [Arguments("not toml at all [[[")]
    [Arguments("[colors]\nforeground = 12\n")]
    public async Task FilesThatArentSchemesAreSkipped(string body)
    {
        // Arrange
        string target = TempTarget();
        var schemes = new Dictionary<string, string> { ["Good.toml"] = ValidScheme, ["Bad.toml"] = body };

        // Act
        var result = ThemeCatalog.Download(Checkout(schemes), target, null);

        // Assert
        await Assert.That(result.Skipped).IsEqualTo(1);
        await Assert.That(File.Exists(Path.Combine(target, "Bad.toml"))).IsFalse();
        Remove(target);
    }

    [Test]
    public async Task AnOversizedFileIsSkipped()
    {
        // Arrange
        string target = TempTarget();
        string huge = ValidScheme + "\n#" + new string('x', ThemeCatalog.MaxFileBytes);
        var schemes = new Dictionary<string, string> { ["Good.toml"] = ValidScheme, ["Huge.toml"] = huge };

        // Act
        var result = ThemeCatalog.Download(Checkout(schemes), target, null);

        // Assert
        await Assert.That(result.Skipped).IsEqualTo(1);
        await Assert.That(File.Exists(Path.Combine(target, "Huge.toml"))).IsFalse();
        Remove(target);
    }

    [Test]
    public async Task OtherFilesInTheFolderAreIgnored()
    {
        // Arrange
        string target = TempTarget();
        var schemes = new Dictionary<string, string> { ["Good.toml"] = ValidScheme, ["run.sh"] = "echo hi", [".hidden.toml"] = ValidScheme };

        // Act
        ThemeCatalog.Download(Checkout(schemes), target, null);

        // Assert
        await Assert.That(Directory.GetFiles(target).Select(f => Path.GetFileName(f)!)).IsEquivalentTo(new[] { "Good.toml", "LICENSE" });
        Remove(target);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task ASymlinkInTheCheckoutIsNotFollowed()
    {
        // Arrange — a link posing as a scheme, pointing outside the checkout
        string target = TempTarget();
        string outside = Path.GetTempFileName();
        File.WriteAllText(outside, ValidScheme);
        Func<string, string?> fetch = dir =>
        {
            Checkout(new() { ["Good.toml"] = ValidScheme })(dir);
            File.CreateSymbolicLink(Path.Combine(dir, ThemeCatalog.SchemeFolder, "Link.toml"), outside);
            return null;
        };

        // Act
        var result = ThemeCatalog.Download(fetch, target, null);

        // Assert
        await Assert.That(result.Skipped).IsEqualTo(1);
        await Assert.That(File.Exists(Path.Combine(target, "Link.toml"))).IsFalse();
        File.Delete(outside);
        Remove(target);
    }

    [Test]
    public async Task TheLicenseComesAlong()
    {
        // Arrange
        string target = TempTarget();

        // Act
        ThemeCatalog.Download(Checkout(new() { ["Good.toml"] = ValidScheme }), target, null);

        // Assert
        await Assert.That(File.ReadAllText(Path.Combine(target, "LICENSE"))).IsEqualTo("MIT");
        Remove(target);
    }

    [Test]
    public async Task ANewDownloadReplacesTheOldFolder()
    {
        // Arrange
        string target = TempTarget();
        ThemeCatalog.Download(Checkout(new() { ["Old.toml"] = ValidScheme }), target, null);

        // Act
        ThemeCatalog.Download(Checkout(new() { ["New.toml"] = ValidScheme }), target, null);

        // Assert
        await Assert.That(File.Exists(Path.Combine(target, "Old.toml"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(target, "New.toml"))).IsTrue();
        Remove(target);
    }

    [Test]
    public async Task AFailedFetchLeavesTheOldFolderAlone()
    {
        // Arrange
        string target = TempTarget();
        ThemeCatalog.Download(Checkout(new() { ["Old.toml"] = ValidScheme }), target, null);

        // Act
        var result = ThemeCatalog.Download(_ => "fatal: unable to access", target, null);

        // Assert
        await Assert.That(result.Error).IsEqualTo("fatal: unable to access");
        await Assert.That(File.Exists(Path.Combine(target, "Old.toml"))).IsTrue();
        Remove(target);
    }

    [Test]
    public async Task NoUsableSchemesIsAnErrorAndLeavesNothingBehind()
    {
        // Arrange
        string target = TempTarget();

        // Act
        var result = ThemeCatalog.Download(Checkout(new() { ["Bad.toml"] = "nope [[[" }), target, null);

        // Assert
        await Assert.That(result.Error).IsNotNull();
        await Assert.That(Directory.Exists(target)).IsFalse();
        await Assert.That(Directory.GetFileSystemEntries(Path.GetDirectoryName(Path.GetDirectoryName(target)!)!).Length).IsEqualTo(0);
        Remove(target);
    }

    [Test]
    public async Task ProgressCountsEveryFile()
    {
        // Arrange
        string target = TempTarget();
        var seen = new List<(int Done, int Total)>();
        var schemes = new Dictionary<string, string> { ["A.toml"] = ValidScheme, ["B.toml"] = ValidScheme, ["C.toml"] = "nope [[[" };

        // Act
        ThemeCatalog.Download(Checkout(schemes), target, (d, t) => seen.Add((d, t)));

        // Assert
        await Assert.That(seen[^1]).IsEqualTo((3, 3));
        await Assert.That(seen.Count).IsEqualTo(3);
        Remove(target);
    }

    // ---- Where it goes ------------------------------------------------------------------------------

    [Test]
    public async Task TheCatalogueGoesNextToTheConfigFile()
    {
        // Act
        string dir = ColorSchemeFiles.CatalogDirectory("/some/where/erikterm.toml");

        // Assert
        await Assert.That(dir).IsEqualTo(Path.GetFullPath("/some/where/colors/" + ColorSchemeFiles.CatalogFolderName));
    }

    [Test]
    public async Task TheCatalogueIsLoadedAfterYourOwnSchemes()
    {
        // Act
        var dirs = ColorSchemeFiles.DefaultDirectories("/some/where/erikterm.toml");

        // Assert
        await Assert.That(dirs[0]).IsEqualTo(Path.GetFullPath("/some/where/colors"));
        await Assert.That(dirs[1]).IsEqualTo(ColorSchemeFiles.CatalogDirectory("/some/where/erikterm.toml"));
    }

    [Test]
    public async Task ASchemeKnowsItsFile()
    {
        // Act
        var scheme = ColorSchemeFiles.Parse(ValidScheme, "/colors/x.toml", []);

        // Assert
        await Assert.That(scheme!.SourcePath).IsEqualTo("/colors/x.toml");
    }
}
