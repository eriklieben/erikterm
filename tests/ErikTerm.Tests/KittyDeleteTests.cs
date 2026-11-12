using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>
/// Kitty graphics delete selectors one by one (<see cref="ImageTests"/> walks a, i, c, p and z): which placements each
/// takes and whether the upper-case form frees the image.
/// </summary>
public class KittyDeleteTests
{
    private static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));

    private static string Kitty(string control, string payload = "") => $"\e_G{control};{payload}\e\\";

    private static readonly string OnePixel = Convert.ToBase64String([0xFF, 0, 0, 0xFF]);

    /// <summary>Images 1, 2 and 3, placed at (1,1), (2,3) with z=5, and (4,6).</summary>
    private static Terminal Placed(bool alt = false)
    {
        var t = new Terminal(20, 6, 100) { CellPixelWidth = 10, CellPixelHeight = 20 };
        if (alt) Feed(t, "\e[?1049h");
        Feed(t, Kitty("a=t,f=32,s=1,v=1,i=1,q=2", OnePixel));
        Feed(t, Kitty("a=t,f=32,s=1,v=1,i=2,q=2", OnePixel));
        Feed(t, Kitty("a=t,f=32,s=1,v=1,i=3,q=2", OnePixel));
        Feed(t, "\e[1;1H" + Kitty("a=p,i=1,C=1,q=2") + "\e[2;3H" + Kitty("a=p,i=2,z=5,C=1,q=2") + "\e[4;6H" + Kitty("a=p,i=3,C=1,q=2"));
        return t;
    }

    private static uint[] PlacedImages(Terminal t, bool alt = false) =>
        [.. t.Images.Placements(alt).Select(p => p.Image.Id).Order()];

    [Test]
    [Arguments("d=q,x=3,y=2,z=5", new uint[] { 1, 3 })]   // the cell, with its z
    [Arguments("d=q,x=3,y=2,z=4", new uint[] { 1, 2, 3 })]   // the cell, another z
    [Arguments("d=x,x=6", new uint[] { 1, 2 })]            // the column
    [Arguments("d=y,y=2", new uint[] { 1, 3 })]            // the row
    [Arguments("d=r,x=1,y=2", new uint[] { 3 })]           // an id range
    [Arguments("d=w", new uint[] { 1, 2, 3 })]             // no such selector
    public async Task SelectorsDeleteTheirPlacements(string control, uint[] left)
    {
        // Arrange
        var t = Placed();

        // Act
        Feed(t, Kitty("a=d," + control));

        // Assert
        await Assert.That(PlacedImages(t)).IsEquivalentTo(left, CollectionOrdering.Matching);
    }

    [Test]
    public async Task LowerCaseRangeKeepsTheImages()
    {
        // Arrange
        var t = Placed();

        // Act
        Feed(t, Kitty("a=d,d=r,x=1,y=2"));

        // Assert
        await Assert.That(t.Images.ById(1)).IsNotNull();
    }

    [Test]
    public async Task UpperCaseRangeFreesTheImagesInIt()
    {
        // Arrange
        var t = Placed();

        // Act
        Feed(t, Kitty("a=d,d=R,x=2,y=3"));

        // Assert
        await Assert.That(t.Images.Images.Select(i => i.Id)).IsEquivalentTo(new uint[] { 1 });
    }

    [Test]
    public async Task ByNumberDeletesThatImagesPlacements()
    {
        // Arrange: an image with number 7, placed
        var t = Placed();
        Feed(t, Kitty("a=T,f=32,s=1,v=1,I=7,C=1,q=2", OnePixel));
        int before = t.Images.Placements(false).Count;

        // Act
        Feed(t, Kitty("a=d,d=n,I=7"));

        // Assert
        await Assert.That(t.Images.Placements(false).Count).IsEqualTo(before - 1);
        await Assert.That(t.Images.ByNumber(7)).IsNotNull();
    }

    [Test]
    public async Task UpperCaseByNumberFreesTheImage()
    {
        // Arrange
        var t = Placed();
        Feed(t, Kitty("a=T,f=32,s=1,v=1,I=7,C=1,q=2", OnePixel));

        // Act
        Feed(t, Kitty("a=d,d=N,I=7"));

        // Assert
        await Assert.That(t.Images.ByNumber(7)).IsNull();
    }

    [Test]
    public async Task AnUnknownNumberDeletesNothing()
    {
        // Arrange
        var t = Placed();

        // Act
        Feed(t, Kitty("a=d,d=N,I=99"));

        // Assert
        await Assert.That(PlacedImages(t)).IsEquivalentTo(new uint[] { 1, 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ByIdOnTheAlternateScreenAlsoDeletesTheMainScreensPlacements()
    {
        // Arrange: image 1 placed on the main screen, then again on the alternate one
        var t = Placed();
        Feed(t, "\e[?1049h" + Kitty("a=p,i=1,C=1,q=2"));

        // Act
        Feed(t, Kitty("a=d,d=i,i=1"));

        // Assert
        await Assert.That(PlacedImages(t, alt: true)).IsEmpty();
        await Assert.That(PlacedImages(t)).IsEquivalentTo(new uint[] { 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DeleteAllOnTheAlternateScreenLeavesTheMainScreen()
    {
        // Arrange
        var t = Placed();
        Feed(t, "\e[?1049h" + Kitty("a=p,i=1,C=1,q=2"));

        // Act
        Feed(t, Kitty("a=d,d=a"));

        // Assert
        await Assert.That(PlacedImages(t, alt: true)).IsEmpty();
        await Assert.That(PlacedImages(t)).IsEquivalentTo(new uint[] { 1, 2, 3 }, CollectionOrdering.Matching);
    }
}
