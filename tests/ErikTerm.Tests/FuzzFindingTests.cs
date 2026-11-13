using System.Text;
using ErikTerm.Core;
using ErikTerm.Core.Images;

namespace ErikTerm.Tests;

/// <summary>
/// Regression tests for what fuzz/ErikTerm.Fuzz found (docs/development/fuzzing.md). Each input is a minimal one
/// written for the bug, not the fuzzer's.
/// </summary>
public class FuzzFindingTests
{
    private static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));

    /// <summary>A kitty a=T of one RGB pixel with the given placement keys.</summary>
    private static string KittyPixel(string keys) => $"\e_Ga=T,f=24,s=1,v=1,q=2,{keys};{Convert.ToBase64String([1, 2, 3])}\e\\";

    [Test]
    [Arguments("c=2147483647")]
    [Arguments("c=4294967295")]
    public async Task Kitty_placement_wider_than_any_screen_leaves_the_cursor_on_the_screen(string keys)
    {
        // Arrange
        var t = new Terminal(20, 5);
        Feed(t, "\e[1;6H");

        // Act
        Feed(t, KittyPixel(keys));
        Feed(t, "ab");

        // Assert
        await Assert.That(t.CursorX).IsBetween(0, 19);
        await Assert.That(t.CursorY).IsBetween(0, 4);
    }

    [Test]
    public async Task Kitty_placement_taller_than_any_screen_moves_the_cursor_in_bounded_time()
    {
        // Arrange
        var t = new Terminal(20, 5);

        // Act
        var cpu = ThreadCpuTime.Measure(() =>
        {
            Feed(t, KittyPixel("r=2147483647"));
            Feed(t, "ab");
        });

        // Assert
        await Assert.That(t.CursorY).IsEqualTo(4);
        await Assert.That(cpu).IsLessThan(TimeSpan.FromSeconds(2)).Because("one line feed per requested row");
    }

}
