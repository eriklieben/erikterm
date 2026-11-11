using ErikTerm.Ui;

namespace ErikTerm.Tests;

public class TextPatternTests
{
    private static string[] Urls(string text) => TextPatterns.FindUrls(text).Select(m => m.Of(text)).ToArray();

    private static (MatchKind, string)[] Quick(string text) =>
        TextPatterns.FindQuickSelect(text).Select(m => (m.Kind, m.Of(text))).ToArray();

    [Test]
    [Arguments("see https://example.com/a?b=c&d=e#f for more", "https://example.com/a?b=c&d=e#f")]
    [Arguments("(https://en.wikipedia.org/wiki/Foo_(bar)).", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    [Arguments("<http://localhost:8080/x>", "http://localhost:8080/x")]
    [Arguments("go to https://example.com.", "https://example.com")]
    [Arguments("'https://example.com/q'", "https://example.com/q")]
    [Arguments("mail mailto:me@example.com, thanks", "mailto:me@example.com")]
    [Arguments("open file:///home/erik/a%20b.txt", "file:///home/erik/a%20b.txt")]
    [Arguments("│ https://example.com/x │", "https://example.com/x")]
    [Arguments("visit www.example.org!", "www.example.org")]
    [Arguments("[link](https://md.example/x)", "https://md.example/x")]
    public async Task FindsUrls(string text, string expected)
    {
        // Act
        var urls = Urls(text);

        // Assert
        await Assert.That(urls).IsEquivalentTo([expected], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("no links here")]
    [Arguments("https://")]
    [Arguments("xhttps://glued.example")]
    [Arguments("http:/one-slash.example")]
    public async Task IgnoresNonUrls(string text)
    {
        // Act
        var urls = Urls(text);

        // Assert
        await Assert.That(urls).IsEmpty();
    }

    [Test]
    public async Task FindsSeveralUrlsOnOneLine()
    {
        // Act
        var urls = Urls("http://a.example and https://b.example/x");

        // Assert
        await Assert.That(urls).IsEquivalentTo(["http://a.example", "https://b.example/x"], CollectionOrdering.Matching);
    }

    private const string EveryKind =
        "commit 3f2a9c1 at https://git.example/r/3f2a9c1d src/App.cs:42 ~/notes.md " +
        "550e8400-e29b-41d4-a716-446655440000 10.0.0.1:8080 fe80::1ff:fe23:4567:890a 2001:db8::1 pid 123456 n 12";

    [Test]
    [Arguments(MatchKind.Hash, "3f2a9c1")]
    [Arguments(MatchKind.Url, "https://git.example/r/3f2a9c1d")]
    [Arguments(MatchKind.Path, "src/App.cs:42")]
    [Arguments(MatchKind.Path, "~/notes.md")]
    [Arguments(MatchKind.Uuid, "550e8400-e29b-41d4-a716-446655440000")]
    [Arguments(MatchKind.Ipv4, "10.0.0.1:8080")]
    [Arguments(MatchKind.Ipv6, "fe80::1ff:fe23:4567:890a")]
    [Arguments(MatchKind.Ipv6, "2001:db8::1")]
    [Arguments(MatchKind.Number, "123456")]
    public async Task QuickSelect_FindsEachKind(MatchKind kind, string text)
    {
        // Act
        var found = Quick(EveryKind);

        // Assert
        await Assert.That(found).Contains((kind, text));
    }

    [Test]
    public async Task QuickSelect_SkipsShortNumbers()
    {
        // Act
        var found = Quick(EveryKind);

        // Assert
        await Assert.That(found).DoesNotContain(f => f.Item2 == "12");
    }

    [Test]
    public async Task QuickSelect_DoesNotOfferTheHashInsideAUrlSeparately()
    {
        // Act
        var found = Quick(EveryKind);

        // Assert
        // No overlaps: the hash inside the URL isn't offered separately.
        await Assert.That(found).HasSingleItem(f => f.Item2.Contains("3f2a9c1d"));
    }

    [Test]
    public async Task QuickSelect_AddressesWithPrefixesAreNotPaths()
    {
        // Act
        var found = Quick("lo 127.0.0.1/8 ::1/128 wlan0 fe80::1ff:fe23:4567:890a/64");

        // Assert
        await Assert.That(found).IsEquivalentTo(
            [(MatchKind.Ipv4, "127.0.0.1/8"), (MatchKind.Ipv6, "::1/128"), (MatchKind.Ipv6, "fe80::1ff:fe23:4567:890a/64")],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task QuickSelect_IgnoresTimesWordsAndShortNumbers()
    {
        // Act
        var found = Quick("at 12:34:56 the decade faced 123 errors");

        // Assert
        await Assert.That(found).IsEmpty();
    }

    [Test]
    public async Task QuickSelect_MatchesAreInTextOrderWithoutOverlap()
    {
        // Act
        var matches = TextPatterns.FindQuickSelect("/usr/bin/env 1234 deadbeef42 /tmp/x");

        // Assert
        for (int i = 1; i < matches.Count; i++) await Assert.That(matches[i - 1].End).IsLessThanOrEqualTo(matches[i].Start);
        await Assert.That(matches.Count).IsEqualTo(4);
    }

    public static IEnumerable<int> LabelCounts() => [1, 26, 27, 100, 676];

    [Test]
    [MethodDataSource(nameof(LabelCounts))]
    public async Task HintLabels_HaveTheRequestedCount(int count)
    {
        // Act
        var labels = HintLabels.Generate(count);

        // Assert
        await Assert.That(labels.Count).IsEqualTo(count);
    }

    [Test]
    [MethodDataSource(nameof(LabelCounts))]
    public async Task HintLabels_AreUnique(int count)
    {
        // Act
        var labels = HintLabels.Generate(count);

        // Assert
        await Assert.That(labels.Distinct().Count()).IsEqualTo(count);
    }

    [Test]
    [MethodDataSource(nameof(LabelCounts))]
    public async Task HintLabels_ArePrefixFree(int count)
    {
        // Act
        var labels = HintLabels.Generate(count);

        // Assert
        var prefixes = labels.SelectMany(a => labels.Where(b => a != b && b.StartsWith(a, StringComparison.Ordinal))
            .Select(b => $"{a} is a prefix of {b}"));
        await Assert.That(prefixes).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(LabelCounts))]
    public async Task HintLabels_AreOneOrTwoLetters(int count)
    {
        // Act
        var labels = HintLabels.Generate(count);

        // Assert
        await Assert.That(labels).All(l => l.Length is >= 1 and <= 2);
    }

    [Test]
    public async Task HintLabels_StartWithA()
    {
        // Act
        var labels = HintLabels.Generate(3);

        // Assert
        await Assert.That(labels[0]).IsEqualTo("a");
    }

    [Test]
    public async Task HintLabels_UseSingleLettersWhilePossible()
    {
        // Act
        var labels = HintLabels.Generate(30);

        // Assert
        await Assert.That(labels.Count(l => l.Length == 1)).IsGreaterThanOrEqualTo(20); // only a few letters become prefixes
    }
}
