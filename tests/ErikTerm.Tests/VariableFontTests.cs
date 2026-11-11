using ErikTerm.App.Native;
using ErikTerm.App.Rendering;

namespace ErikTerm.Tests;

public class VariableFontTests
{
    private static VariableInstance I(int index, string? name, double? wght = null, double? ital = null, double? slnt = null) =>
        new(index, name, wght, ital, slnt);

    // Like Cascadia Code's upright file: wght 200–700, upright only.
    private static readonly VariableInstance[] Cascadia =
    [
        I(1, "ExtraLight", 200), I(2, "Light", 300), I(3, "SemiLight", 350), I(4, "Regular", 400), I(5, "SemiBold", 600), I(6, "Bold", 700),
    ];

    [Test]
    [Arguments(FontStyle.Bold, 6)]
    [Arguments(FontStyle.Regular, 4)]
    [Arguments(FontStyle.Italic, 0)] // no italic instances: regular stays
    [Arguments(FontStyle.BoldItalic, 0)]
    public async Task PicksByStyleName(FontStyle style, int expected)
    {
        // Act
        int picked = VariableInstances.Pick(Cascadia, style);

        // Assert
        await Assert.That(picked).IsEqualTo(expected);
    }

    [Test]
    public async Task PicksNothingFromAnEmptyList()
    {
        // Act
        int picked = VariableInstances.Pick([], FontStyle.Bold);

        // Assert
        await Assert.That(picked).IsEqualTo(0);
    }

    // Mixed case and punctuation in the names.
    private static readonly VariableInstance[] LooselyNamed = [I(1, "regular", 400, 0), I(2, "Bold-Italic", 700, 1), I(3, "BOLD", 700, 0), I(4, "Italic", 400, 1)];

    [Test]
    [Arguments(FontStyle.BoldItalic, 2)]
    [Arguments(FontStyle.Bold, 3)]
    [Arguments(FontStyle.Italic, 4)]
    public async Task NamesAreMatchedLoosely(FontStyle style, int expected)
    {
        // Act
        int picked = VariableInstances.Pick(LooselyNamed, style);

        // Assert
        await Assert.That(picked).IsEqualTo(expected);
    }

    [Test]
    public async Task ObliqueCountsAsItalic()
    {
        // Arrange
        VariableInstance[] list = [I(1, "Book", 400), I(2, "Bold Oblique", 700, slnt: -10)];

        // Act
        int picked = VariableInstances.Pick(list, FontStyle.BoldItalic);

        // Assert
        await Assert.That(picked).IsEqualTo(2);
    }

    // Localized / unusual names: weight 700 (or the nearest ≥ 600) wins; slant must match.
    private static readonly VariableInstance[] German =
        [I(1, "Buch", 400), I(2, "Halbfett", 600), I(3, "Fett", 720), I(4, "Kursiv", 400, slnt: -12), I(5, "Fett Kursiv", 700, slnt: -12)];

    [Test]
    [Arguments(FontStyle.Bold, 3)]
    [Arguments(FontStyle.BoldItalic, 5)]
    [Arguments(FontStyle.Italic, 4)]
    [Arguments(FontStyle.Regular, 1)]
    public async Task FallsBackToTheWeightAxis(FontStyle style, int expected)
    {
        // Act
        int picked = VariableInstances.Pick(German, style);

        // Assert
        await Assert.That(picked).IsEqualTo(expected);
    }

    [Test]
    public async Task OnlyLightWeightsHaveNoBold()
    {
        // Arrange
        VariableInstance[] list = [I(1, "Thin", 100), I(2, "Book", 450)];

        // Act
        int picked = VariableInstances.Pick(list, FontStyle.Bold);

        // Assert
        await Assert.That(picked).IsEqualTo(0);
    }

    [Test]
    [Arguments("Bold", 700)]
    [Arguments("SemiBold", 600)]
    [Arguments("ExtraBold Italic", 800)]
    [Arguments(null, 400)]
    public async Task WeightFromNameWithoutAxis(string? name, int expected)
    {
        // Act
        double weight = I(1, name).EffectiveWeight;

        // Assert
        await Assert.That(weight).IsEqualTo(expected);
    }

    [Test]
    public async Task ItalicFromNameWithoutAxis()
    {
        // Act
        bool italic = I(1, "Bold Italic").IsItalic;

        // Assert
        await Assert.That(italic).IsTrue();
    }

    [Test]
    public async Task ItalicAxisWinsOverTheName()
    {
        // Act
        bool italic = I(1, "Italic", ital: 0).IsItalic;

        // Assert
        await Assert.That(italic).IsFalse();
    }

    /// <summary>What FreeType reports for a real variable font, read in one go (pointers can't live in an async test).</summary>
    private sealed record FontProbe(
        int InitError, int OpenError, IReadOnlyList<VariableInstance> Instances, int Bold, int SetInstanceError, string? StyleName);

    private static unsafe FontProbe ProbeVariableFont(string path)
    {
        NativeLibraries.Register();
        nint lib, face;
        int initError = FreeType.InitFreeType(&lib);
        if (initError != 0) return new FontProbe(initError, -1, [], 0, -1, null);
        try
        {
            int openError = FreeType.NewFace(lib, path, 0, &face);
            if (openError != 0) return new FontProbe(0, openError, [], 0, -1, null);
            var instances = FreeType.NamedInstances(lib, face);
            int bold = VariableInstances.Pick(instances, FontStyle.Bold);
            int setError = bold == 0 ? -1 : FreeType.SetNamedInstance(face, (uint)bold);
            string? style = FreeType.StyleName(face);
            FreeType.DoneFace(face);
            return new FontProbe(0, 0, instances, bold, setError, style);
        }
        finally
        {
            FreeType.DoneFreeType(lib);
        }
    }

    /// <summary>Reads a real variable font's named instances through FreeType, if one is installed here.</summary>
    [Test]
    [NotInParallel(Isolation.Natives)]
    public async Task ReadsNamedInstancesFromAVariableFont()
    {
        // Arrange
        string[] candidates =
        [
            "/usr/share/fonts/Adwaita/AdwaitaSans-Regular.ttf",
            @"C:\Windows\Fonts\CascadiaCode.ttf",
        ];
        string? path = candidates.FirstOrDefault(File.Exists);
        if (path is null) Skip.Test("no known variable font installed");

        // Act
        var probe = ProbeVariableFont(path);

        // Assert
        await Assert.That(probe.InitError).IsEqualTo(0);
        await Assert.That(probe.OpenError).IsEqualTo(0);
        await Assert.That(probe.Instances).IsNotEmpty();
        await Assert.That(probe.Instances).All(i => i.Weight is not null);
        await Assert.That(probe.Bold).IsNotEqualTo(0);
        await Assert.That(probe.Instances[probe.Bold - 1].Weight).IsEqualTo(700);
        await Assert.That(probe.SetInstanceError).IsEqualTo(0);
        await Assert.That(probe.StyleName).Contains("Bold");
    }
}
