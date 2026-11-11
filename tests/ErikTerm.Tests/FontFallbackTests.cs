using ErikTerm.App.Native;
using ErikTerm.App.Rendering;

namespace ErikTerm.Tests;

/// <summary>A platform with a counting "any font for this code point" lookup (what spawns fc-match on Linux).</summary>
internal sealed class CountingLocator(string? monospace, FontCoverage? coverage = null, Func<int, string?>? find = null) : IFontLocator
{
    private int _calls;
    private readonly List<int> _threads = [];

    public int Calls => Volatile.Read(ref _calls);
    public IReadOnlyList<int> Threads { get { lock (_threads) return [.. _threads]; } }

    public string? Find(string family, FontStyle style) => null; // nothing by name: every family is "not installed"
    public string? DefaultMonospace() => monospace;
    public IReadOnlyList<string> SymbolFallbackFamilies => [];
    public IEnumerable<string> FallbackFiles(string family) => [];
    public FontCoverage? BuildCoverage() => coverage;

    public string? FindForCodepoint(int codepoint)
    {
        Interlocked.Increment(ref _calls);
        lock (_threads) _threads.Add(Environment.CurrentManagedThreadId);
        return find?.Invoke(codepoint);
    }
}

/// <summary>Fallback font lookups happen off the UI thread (FallbackResolver), with a coverage index to skip hopeless ones.</summary>
public class FallbackResolverTests
{
    [Test]
    public async Task UnknownCodepointIsPendingAtFirst()
    {
        // Arrange
        var resolver = new FallbackResolver(new CountingLocator(null));

        // Act
        bool known = resolver.TryGet(0x1D800, FallbackKind.Text, out _);

        // Assert
        await Assert.That(known).IsFalse();
    }

    [Test]
    public async Task LookupRunsOnAnotherThread()
    {
        // Arrange
        var locator = new CountingLocator(null);
        var resolver = new FallbackResolver(locator);
        int caller = Environment.CurrentManagedThreadId;

        // Act
        resolver.TryGet(0x1D800, FallbackKind.Text, out _);
        resolver.WaitIdle(TimeSpan.FromSeconds(30));

        // Assert
        await Assert.That(locator.Calls).IsEqualTo(1);
        await Assert.That(locator.Threads[0]).IsNotEqualTo(caller);
    }

    [Test]
    public async Task AnswerIsKeptAfterItArrives()
    {
        // Arrange
        var locator = new CountingLocator(null, find: _ => "/fonts/signwriting.ttf");
        var resolver = new FallbackResolver(locator);
        resolver.TryGet(0x1D800, FallbackKind.Text, out _);
        resolver.WaitIdle(TimeSpan.FromSeconds(30));

        // Act
        bool known = resolver.TryGet(0x1D800, FallbackKind.Text, out string? path);

        // Assert
        await Assert.That(known).IsTrue();
        await Assert.That(path).IsEqualTo("/fonts/signwriting.ttf");
        await Assert.That(locator.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task CodepointNoFontHasIsAnsweredWithoutALookup()
    {
        // Arrange
        var coverage = FontCoverage.Parse("/fonts/latin.ttf\t20-7e a0-17f\n");
        var locator = new CountingLocator(null, coverage);
        var resolver = new FallbackResolver(locator);

        // Act
        for (int cp = 0x1D800; cp < 0x1D800 + 600; cp++) resolver.TryGet(cp, FallbackKind.Text, out _);
        resolver.WaitIdle(TimeSpan.FromSeconds(30));

        // Assert
        await Assert.That(locator.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task NeighbourReusesTheFileFoundForTheFirstCodepoint()
    {
        // Arrange
        var coverage = FontCoverage.Parse("/fonts/latin.ttf\t20-7e\n/fonts/signwriting.ttf\t1d800-1daaf\n");
        var locator = new CountingLocator(null, coverage, find: _ => "/fonts/signwriting.ttf");
        var resolver = new FallbackResolver(locator);
        resolver.TryGet(0x1D800, FallbackKind.Text, out _);
        resolver.WaitIdle(TimeSpan.FromSeconds(30));

        // Act
        for (int cp = 0x1D801; cp < 0x1D900; cp++) resolver.TryGet(cp, FallbackKind.Text, out _);
        resolver.WaitIdle(TimeSpan.FromSeconds(30));
        resolver.TryGet(0x1D8FF, FallbackKind.Text, out string? path);

        // Assert
        await Assert.That(locator.Calls).IsEqualTo(1);
        await Assert.That(path).IsEqualTo("/fonts/signwriting.ttf");
    }

    [Test]
    public async Task CoveringFileReplacesAGuessThatLacksTheCodepoint()
    {
        // Arrange: fc-match always names *a* font (NimbusSans), whether or not it has the glyph.
        var coverage = FontCoverage.Parse("/fonts/nimbus.otf\t20-7e\n/fonts/cuneiform.ttf\t12000-123ff\n");
        var resolver = new FallbackResolver(new CountingLocator(null, coverage, find: _ => "/fonts/nimbus.otf"));
        resolver.TryGet(0x12000, FallbackKind.Text, out _);
        resolver.WaitIdle(TimeSpan.FromSeconds(30));

        // Act
        resolver.TryGet(0x12000, FallbackKind.Text, out string? path);

        // Assert
        await Assert.That(path).IsEqualTo("/fonts/cuneiform.ttf");
    }

    [Test]
    public async Task WithoutAnIndexABlockStopsBeingLookedUpAfterAFewMisses()
    {
        // Arrange
        var locator = new CountingLocator(null); // finds nothing, and no coverage index (Windows)
        var resolver = new FallbackResolver(locator);

        // Act
        for (int cp = 0x10600; cp < 0x10600 + 100; cp++) resolver.TryGet(cp, FallbackKind.Text, out _);
        resolver.WaitIdle(TimeSpan.FromSeconds(30));

        // Assert
        await Assert.That(locator.Calls).IsEqualTo(3);
    }
}

public class FontCoverageTests
{
    [Test]
    [Arguments(0x41, true)]
    [Arguments(0x7F, false)]
    [Arguments(0x1D8FF, true)]
    [Arguments(0x2603, true)]
    public async Task KnowsWhichCodepointsAnyFontHas(int cp, bool expected)
    {
        // Arrange
        var coverage = FontCoverage.Parse("/a.ttf\t20-7e\n/b.ttf\t1d800-1daaf 2603\n");

        // Act
        bool covered = coverage.AnyCovers(cp);

        // Assert
        await Assert.That(covered).IsEqualTo(expected);
    }

    [Test]
    public async Task PrefersAScalableFile()
    {
        // Arrange
        var coverage = FontCoverage.Parse("/misc/fixed.pcf.gz\t20-7e\n/ttf/mono.ttf\t20-7e\n");

        // Act
        string? path = coverage.FirstCovering('A');

        // Assert
        await Assert.That(path).IsEqualTo("/ttf/mono.ttf");
    }

    [Test]
    public async Task MergesTheFacesOfACollection()
    {
        // Arrange
        var coverage = FontCoverage.Parse("/cjk.ttc\t4e00-4eff\n/cjk.ttc\t3040-309f\n");

        // Act
        bool both = coverage.Covers("/cjk.ttc", 0x4E00) && coverage.Covers("/cjk.ttc", 0x3042);

        // Assert
        await Assert.That(both).IsTrue();
    }

    [Test]
    public async Task SkipsLinesThatDoNotParse()
    {
        // Act
        var coverage = FontCoverage.Parse("garbage\n\n/a.ttf\tzz 41\n");

        // Assert
        await Assert.That(coverage.FileCount).IsEqualTo(1);
        await Assert.That(coverage.AnyCovers('A')).IsTrue();
    }
}

/// <summary>FontSet with a fake platform: real FreeType, a real monospace file, no fc-match.</summary>
[RunOn(OS.Linux)]
[NotInParallel(Isolation.Natives)]
public class FontSetFallbackTests
{
    private static string? RealMonospace()
    {
        NativeLibraries.Register();
        return new FontconfigLocator().DefaultMonospace();
    }

    [Test]
    public async Task MissingPrimaryFontIsReported()
    {
        // Arrange
        string? mono = RealMonospace();
        if (mono is null) Skip.Test("no monospace font installed");
        var locator = new CountingLocator(mono);

        // Act
        using var fonts = new FontSet("Nonexistent Font Family", [], 16, locator: locator, resolver: new FallbackResolver(locator));

        // Assert
        await Assert.That(fonts.Warnings).Contains(w => w.Contains("no installed font matches \"Nonexistent Font Family\"", StringComparison.Ordinal));
    }

    [Test]
    public async Task MissingFallbackFontIsReported()
    {
        // Arrange
        string? mono = RealMonospace();
        if (mono is null) Skip.Test("no monospace font installed");
        var locator = new CountingLocator(mono);

        // Act
        using var fonts = new FontSet("Nonexistent Font Family", ["No Such Symbols"], 16, locator: locator, resolver: new FallbackResolver(locator));

        // Assert
        await Assert.That(fonts.Warnings).Contains(w => w.Contains("font_fallback: no font matches \"No Such Symbols\"", StringComparison.Ordinal));
    }

    /// <summary>Resolves a code point the way a frame does and reports which threads the platform lookup ran on.</summary>
    private static (uint Glyph, int Caller, int CallsDuringFrame) ResolveInFrame(FontSet fonts, CountingLocator locator, int cp)
    {
        int caller = Environment.CurrentManagedThreadId;
        fonts.ResolveFace(cp, FontStyle.Regular, out uint glyph);
        int callsHere = locator.Threads.Count(t => t == caller);
        return (glyph, caller, callsHere);
    }

    [Test]
    public async Task UncoveredCodepointIsNotLookedUpInTheFrame()
    {
        // Arrange
        string? mono = RealMonospace();
        if (mono is null) Skip.Test("no monospace font installed");
        var locator = new CountingLocator(mono);
        var resolver = new FallbackResolver(locator);
        using var fonts = new FontSet("Nonexistent Font Family", [], 16, locator: locator, resolver: resolver);

        // Act
        var (glyph, _, callsDuringFrame) = ResolveInFrame(fonts, locator, 0x1D800); // Sutton SignWriting

        // Assert
        await Assert.That(glyph).IsEqualTo(0u); // .notdef for now
        await Assert.That(callsDuringFrame).IsEqualTo(0);
    }

    [Test]
    public async Task AnsweredLookupAsksForARedraw()
    {
        // Arrange
        string? mono = RealMonospace();
        if (mono is null) Skip.Test("no monospace font installed");
        var locator = new CountingLocator(mono);
        var resolver = new FallbackResolver(locator);
        using var fonts = new FontSet("Nonexistent Font Family", [], 16, locator: locator, resolver: resolver);
        ResolveInFrame(fonts, locator, 0x1D800);
        resolver.WaitIdle(TimeSpan.FromSeconds(30));
        int generation = fonts.Generation;

        // Act
        bool redraw = fonts.PollFallbacks();

        // Assert
        await Assert.That(redraw).IsTrue();
        await Assert.That(fonts.Generation).IsEqualTo(generation + 1);
    }

    [Test]
    public async Task AnsweredCodepointIsNotLookedUpAgain()
    {
        // Arrange
        string? mono = RealMonospace();
        if (mono is null) Skip.Test("no monospace font installed");
        var locator = new CountingLocator(mono);
        var resolver = new FallbackResolver(locator);
        using var fonts = new FontSet("Nonexistent Font Family", [], 16, locator: locator, resolver: resolver);
        ResolveInFrame(fonts, locator, 0x1D800);
        resolver.WaitIdle(TimeSpan.FromSeconds(30));
        fonts.PollFallbacks();

        // Act
        for (int frame = 0; frame < 5; frame++) ResolveInFrame(fonts, locator, 0x1D800);
        resolver.WaitIdle(TimeSpan.FromSeconds(30));

        // Assert
        await Assert.That(locator.Calls).IsEqualTo(1);
        await Assert.That(fonts.PollFallbacks()).IsFalse();
    }
}

[RunOn(OS.Linux | OS.MacOs)]
public class FontLookupCacheTests
{
    /// <summary>A stand-in for fontconfig that records every time it is started.</summary>
    private static (string Directory, string Log) FakeFontconfig()
    {
        string directory = Directory.CreateTempSubdirectory("erikterm-fc").FullName;
        string log = Path.Combine(directory, "calls");
        string script = Path.Combine(directory, "fc-match");
        File.WriteAllText(script, $"#!/bin/sh\necho x >> '{log}'\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        WaitUntilItRuns(script);
        File.Delete(log);
        return (directory, log);
    }

    /// <summary>A process starting elsewhere can briefly hold a just-written file open, and starting it then fails (ETXTBSY).</summary>
    private static void WaitUntilItRuns(string script)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(script);
                process.WaitForExit();
                return;
            }
            catch (System.ComponentModel.Win32Exception) when (attempt < 200)
            {
                Thread.Sleep(10);
            }
        }
    }

    private static int Calls(string log) => File.Exists(log) ? File.ReadAllLines(log).Length : 0;

    [Test]
    public async Task TheSameLookupRunsOnlyOnce()
    {
        // Arrange
        var (directory, log) = FakeFontconfig();
        var locator = new FontconfigLocator(directory);

        // Act
        locator.Find("Some Font", FontStyle.Regular);
        locator.Find("Some Font", FontStyle.Regular);

        // Assert
        await Assert.That(Calls(log)).IsEqualTo(1);
        Directory.Delete(directory, recursive: true);
    }

    [Test]
    public async Task DifferentStylesAreSeparateLookups()
    {
        // Arrange
        var (directory, log) = FakeFontconfig();
        var locator = new FontconfigLocator(directory);

        // Act
        locator.Find("Some Font", FontStyle.Regular);
        locator.Find("Some Font", FontStyle.Bold);

        // Assert
        await Assert.That(Calls(log)).IsEqualTo(2);
        Directory.Delete(directory, recursive: true);
    }

    [Test]
    public async Task ForgettingLookupsAsksAgain()
    {
        // Arrange
        var (directory, log) = FakeFontconfig();
        var locator = new FontconfigLocator(directory);
        locator.Find("Some Font", FontStyle.Regular);

        // Act
        locator.ForgetLookups();
        locator.Find("Some Font", FontStyle.Regular);

        // Assert
        await Assert.That(Calls(log)).IsEqualTo(2);
        Directory.Delete(directory, recursive: true);
    }
}
