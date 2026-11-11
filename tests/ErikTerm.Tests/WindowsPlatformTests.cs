using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Config;

namespace ErikTerm.Tests;

public class WindowsFontNamesTests
{
    private static WindowsFontNames.Entry E(string family, bool bold = false, bool italic = false) => new(family, bold, italic);

    [Test]
    [Arguments("Cascadia Code (TrueType)", "Cascadia Code", false, false)]
    [Arguments("Cascadia Code Regular (TrueType)", "Cascadia Code", false, false)]
    [Arguments("Cascadia Code Bold (TrueType)", "Cascadia Code", true, false)]
    [Arguments("Cascadia Code Italic (TrueType)", "Cascadia Code", false, true)]
    [Arguments("Consolas Bold Italic (TrueType)", "Consolas", true, true)]
    [Arguments("Courier New Bold Oblique (OpenType)", "Courier New", true, true)]
    [Arguments("JetBrains Mono SemiBold Italic (TrueType)", "JetBrains Mono SemiBold", false, true)]
    [Arguments("Segoe UI Black (TrueType)", "Segoe UI Black", false, false)]
    [Arguments("Book Antiqua Bold", "Book Antiqua", true, false)]
    [Arguments("Bold (TrueType)", "Bold", false, false)] // never peel the whole name away
    public async Task ParsesSingleNames(string valueName, string family, bool bold, bool italic)
    {
        // Act
        var entries = WindowsFontNames.ParseValueName(valueName, isCollection: false);

        // Assert
        await Assert.That(entries).IsEquivalentTo([E(family, bold, italic)], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("MS Gothic & MS UI Gothic & MS PGothic (TrueType)", "MS Gothic")]
    [Arguments("Yu Gothic Regular & Yu Gothic UI Semilight (TrueType)", "Yu Gothic")]
    public async Task CollectionsOnlyNameFaceZero(string valueName, string family)
    {
        // Act
        var entries = WindowsFontNames.ParseValueName(valueName, isCollection: true);

        // Assert
        await Assert.That(entries).IsEquivalentTo([E(family)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SingleFilesWithSeveralNamesListThemAll()
    {
        // Act
        var entries = WindowsFontNames.ParseValueName("MS Gothic & MS UI Gothic & MS PGothic (TrueType)", isCollection: false);

        // Assert
        await Assert.That(entries).IsEquivalentTo([E("MS Gothic"), E("MS UI Gothic"), E("MS PGothic")], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("Regular", true)]
    [Arguments("", true)]
    [Arguments(null, true)]
    [Arguments("Bold", true)]
    [Arguments("Italic", true)]
    [Arguments("Bold Italic", true)]
    [Arguments("Oblique", true)]
    [Arguments("SemiBold", false)]
    [Arguments("Light Italic", false)]
    public async Task RecognisesFreeTypeStyleNames(string? style, bool ok)
    {
        // Act
        bool parsed = WindowsFontNames.TryParseStyleName(style, out _, out _);

        // Assert
        await Assert.That(parsed).IsEqualTo(ok);
    }

    [Test]
    [Arguments("Regular", false, false)]
    [Arguments("", false, false)]
    [Arguments(null, false, false)]
    [Arguments("Bold", true, false)]
    [Arguments("Italic", false, true)]
    [Arguments("Bold Italic", true, true)]
    [Arguments("Oblique", false, true)]
    public async Task ParsesFreeTypeStyleNames(string? style, bool bold, bool italic)
    {
        // Act
        WindowsFontNames.TryParseStyleName(style, out bool b, out bool i);

        // Assert
        await Assert.That((b, i)).IsEqualTo((bold, italic));
    }

    [Test]
    [Arguments("consola.ttf", @"C:\Windows\Fonts", @"C:\Windows\Fonts\consola.ttf")]
    [Arguments("consola.ttf", @"C:\Windows\Fonts\", @"C:\Windows\Fonts\consola.ttf")]
    [Arguments(@"C:\Users\erik\AppData\Local\Microsoft\Windows\Fonts\JetBrainsMono-Regular.ttf", @"C:\Windows\Fonts",
        @"C:\Users\erik\AppData\Local\Microsoft\Windows\Fonts\JetBrainsMono-Regular.ttf")]
    [Arguments("d:/fonts/x.otf", @"C:\Windows\Fonts", "d:/fonts/x.otf")]
    [Arguments(@"\\server\fonts\x.ttf", @"C:\Windows\Fonts", @"\\server\fonts\x.ttf")]
    [Arguments("\"quoted.ttf\"", @"C:\Windows\Fonts", @"C:\Windows\Fonts\quoted.ttf")]
    public async Task ResolvesRegistryPaths(string data, string fontsDir, string expected)
    {
        // Act
        string path = WindowsFontNames.ResolvePath(data, fontsDir);

        // Assert
        await Assert.That(path).IsEqualTo(expected);
    }

    [Test]
    [Arguments("a.ttf", true)]
    [Arguments("A.TTC", true)]
    [Arguments("a.otf", true)]
    [Arguments("vgasys.fon", false)]
    [Arguments("readme.txt", false)]
    public async Task RecognisesScalableFonts(string path, bool scalable)
    {
        // Act
        bool result = WindowsFontNames.IsScalableFontFile(path);

        // Assert
        await Assert.That(result).IsEqualTo(scalable);
    }
}

/// <summary>Only the pure name lists: nothing here registers the process-wide resolver.</summary>
public class NativeLibrariesTests
{
    [Test]
    [Arguments(NativeLibraries.Sdl3, "libSDL3.so.0")]
    [Arguments(NativeLibraries.FreeType, "libfreetype.so.6")]
    public async Task LinuxNamesAreTheSystemSonames(string library, string expected)
    {
        // Act
        string first = NativeLibraries.Candidates(library, windows: false, macOS: false)[0];

        // Assert
        await Assert.That(first).IsEqualTo(expected);
    }

    [Test]
    [Arguments(NativeLibraries.Sdl3, "SDL3.dll")]
    [Arguments(NativeLibraries.FreeType, "freetype.dll")]
    public async Task WindowsNamesAreDlls(string library, string expected)
    {
        // Act
        string first = NativeLibraries.Candidates(library, windows: true, macOS: false)[0];

        // Assert
        await Assert.That(first).IsEqualTo(expected);
    }

    [Test]
    public async Task OtherLibrariesAreLeftToTheRuntime()
    {
        // Act
        var candidates = NativeLibraries.Candidates("libc", windows: false, macOS: false);

        // Assert
        await Assert.That(candidates).IsEmpty();
    }
}

public class ConfigPathTests
{
    [Test]
    [Arguments("~", "/home/erik")]
    [Arguments("~/x/erikterm.toml", "/home/erik/x/erikterm.toml")]
    [Arguments(@"~\x", @"~\x")] // a literal file name on Unix
    [Arguments("~erik/x", "~erik/x")]
    [Arguments("/etc/erikterm.toml", "/etc/erikterm.toml")]
    public async Task ExpandsHomeOnUnix(string path, string expected)
    {
        // Act
        string expanded = SettingsLoader.ExpandHome(path, "/home/erik", windows: false);

        // Assert
        await Assert.That(expanded).IsEqualTo(expected);
    }

    [Test]
    [Arguments("~", @"C:\Users\erik")]
    [Arguments(@"~\AppData\erikterm.toml", @"C:\Users\erik\AppData\erikterm.toml")]
    [Arguments("~/erikterm.toml", @"C:\Users\erik/erikterm.toml")]
    [Arguments(@"D:\cfg\erikterm.toml", @"D:\cfg\erikterm.toml")]
    public async Task ExpandsHomeOnWindows(string path, string expected)
    {
        // Act
        string expanded = SettingsLoader.ExpandHome(path, @"C:\Users\erik", windows: true);

        // Assert
        await Assert.That(expanded).IsEqualTo(expected);
    }

    [Test]
    public async Task ExplicitPathWins()
    {
        // Act
        string path = SettingsLoader.DefaultPath("/tmp/custom.toml");

        // Assert
        await Assert.That(path).IsEqualTo("/tmp/custom.toml");
    }
}
