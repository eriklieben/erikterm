using ErikTerm.App.Cli;
using ErikTerm.App.Session;
using ErikTerm.Config;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>Tab colors: names and #rrggbb, the action, the cli, the session, and programs' iTerm2 OSC 6.</summary>
public class TabColorTests
{
    [Test]
    [Arguments("Red", "red")]
    [Arguments(" blue ", "blue")]
    [Arguments("bright_green", "bright-green")]
    [Arguments("BrightCyan", "bright-cyan")]
    [Arguments("purple", "magenta")]
    [Arguments("gray", "bright-black")]
    [Arguments("#FF8800", "#ff8800")]
    [Arguments("#f80", "#ff8800")]
    public async Task ColorsAreKeptInTheirCanonicalSpelling(string text, string expected)
    {
        // Act
        bool ok = TabColors.TryNormalize(text, out var canonical);

        // Assert
        await Assert.That(ok).IsTrue();
        await Assert.That(canonical).IsEqualTo(expected);
    }

    [Test]
    [Arguments("none")]
    [Arguments("")]
    [Arguments("Default")]
    [Arguments(null)]
    public async Task ResetWordsMeanNoColor(string? text)
    {
        // Act
        bool ok = TabColors.TryNormalize(text, out var canonical);

        // Assert
        await Assert.That(ok).IsTrue();
        await Assert.That(canonical).IsNull();
    }

    [Test]
    [Arguments("chartreuse")]
    [Arguments("#12345")]
    [Arguments("bright-")]
    public async Task OtherWordsAreNotColors(string text)
    {
        // Act
        bool ok = TabColors.TryNormalize(text, out _);

        // Assert
        await Assert.That(ok).IsFalse();
    }

    [Test]
    [Arguments("red", 0xED8796u)]
    [Arguments("bright-black", 0x5B6078u)]
    [Arguments("#123456", 0x123456u)]
    public async Task NamedColorsFollowTheScheme(string canonical, uint expected)
    {
        // Act
        uint? rgb = TabColors.Resolve(canonical, ColorSchemes.CatppuccinMacchiato);

        // Assert
        await Assert.That(rgb).IsEqualTo(expected);
    }

    [Test]
    public async Task SetTabColorActionTakesAColor()
    {
        // Act
        bool ok = ActionSpec.TryParse("SetTabColor(blue)", out var action, out _);

        // Assert
        await Assert.That(ok).IsTrue();
        await Assert.That(action).IsEqualTo(new ActionSpec("SetTabColor", "blue"));
    }

    [Test]
    public async Task SetTabColorActionRejectsAnUnknownColor()
    {
        // Act
        bool ok = ActionSpec.TryParse("SetTabColor(chartreuse)", out _, out string error);

        // Assert
        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("SetTabColor");
    }

    [Test]
    public async Task CliSetTabColorTargetsATab()
    {
        // Act
        var r = CliParser.Parse(["set-tab-color", "--tab-id", "4", "#00ff00"]).Request;

        // Assert
        await Assert.That((r.Command, r.TabId, r.Color)).IsEqualTo(("set-tab-color", (int?)4, "#00ff00"));
    }

    [Test]
    [Arguments("set-tab-color")]
    [Arguments("set-tab-color", "chartreuse")]
    public async Task CliSetTabColorNeedsAColor(params string[] args)
    {
        // Act
        var act = () => CliParser.Parse(args);

        // Assert
        await Assert.That(act).ThrowsExactly<CliUsageException>();
    }

    [Test]
    public async Task TabColorIsSavedWithTheSession()
    {
        // Arrange
        var file = new SessionFile { Windows = [new SessionWindow { Tabs = [new SessionTab { Color = "bright-blue" }] }] };

        // Act
        var back = SessionJson.Deserialize(SessionJson.Serialize(file), out _);

        // Assert
        await Assert.That(back!.Windows[0].Tabs[0].Color).IsEqualTo("bright-blue");
    }

    // ---- OSC 6 (iTerm2) ------------------------------------------------------------------------

    [Test]
    public async Task ProgramSetsATabColorComponentByComponent()
    {
        // Arrange
        var t = new Terminal(20, 3);

        // Act
        t.Feed("\e]6;1;bg;red;brightness;255\a\e]6;1;bg;green;brightness;136\a\e]6;1;bg;blue;brightness;0\a"u8);

        // Assert
        await Assert.That(t.ProgramTabColor).IsEqualTo(0xFF8800u);
    }

    [Test]
    public async Task ProgramResetsItsTabColor()
    {
        // Arrange
        var t = new Terminal(20, 3);
        t.Feed("\e]6;1;bg;red;brightness;255\a"u8);

        // Act
        t.Feed("\e]6;1;bg;*;default\a"u8);

        // Assert
        await Assert.That(t.ProgramTabColor).IsNull();
    }

    [Test]
    public async Task ResetForgetsTheProgramsTabColor()
    {
        // Arrange
        var t = new Terminal(20, 3);
        t.Feed("\e]6;1;bg;blue;brightness;200\a"u8);

        // Act
        t.Feed("\ec"u8);

        // Assert
        await Assert.That(t.ProgramTabColor).IsNull();
    }

    [Test]
    public async Task XtermsOsc6IsNotATabColor()
    {
        // Arrange
        var t = new Terminal(20, 3);

        // Act
        t.Feed("\e]6;1;1\a"u8);

        // Assert
        await Assert.That(t.ProgramTabColor).IsNull();
    }
}
