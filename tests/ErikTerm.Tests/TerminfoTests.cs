using System.Diagnostics;
using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>
/// The xterm-erikterm terminfo entry and the XTGETTCAP replies come from one table (TermCapabilities): the checked-in
/// source must be what the table generates, and the compiled entry must say what XTGETTCAP says.
/// </summary>
public class TerminfoTests
{
    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ErikTerm.slnx"))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException("repository root not found");
    }

    private static string? Which(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Select(d => Path.Combine(d, tool)).FirstOrDefault(File.Exists);

    private static string Run(string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{program} exited with {p.ExitCode}");
        return output;
    }

    /// <summary>Compiles the checked-in entry into a temporary directory and returns infocmp's capabilities (name → value).</summary>
    private static Dictionary<string, string> Compiled(string dir)
    {
        Run("tic", "-x", "-o", dir, RepoFile("contrib/terminfo/erikterm.terminfo"));
        var psi = new ProcessStartInfo("infocmp") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-x", "-1", "-q", "-A", dir, TermCapabilities.Name]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return Parse(text);
    }

    /// <summary>infocmp -1 output as name → value ("" for booleans; numbers in decimal).</summary>
    private static Dictionary<string, string> Parse(string text)
    {
        var caps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            if (!raw.StartsWith('\t')) continue;
            string line = raw.Trim().TrimEnd(',');
            int eq = line.IndexOf('='), hash = line.IndexOf('#');
            if (eq > 0) caps[line[..eq]] = line[(eq + 1)..];
            else if (hash > 0)
            {
                string number = line[(hash + 1)..]; // infocmp may print 0x100
                caps[line[..hash]] = number.StartsWith("0x") ? Convert.ToInt32(number[2..], 16).ToString() : number;
            }
            else if (!line.EndsWith('@')) caps[line] = "";
        }
        return caps;
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "etti-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task CheckedInSourceIsWhatTheTableGenerates()
    {
        // Arrange
        string onDisk = File.ReadAllText(RepoFile("contrib/terminfo/erikterm.terminfo")).ReplaceLineEndings("\n");

        // Act
        string generated = TermCapabilities.TerminfoSource();

        // Assert: if this fails, run `erikterm --terminfo > contrib/terminfo/erikterm.terminfo`
        await Assert.That(onDisk).IsEqualTo(generated);
    }

    [Test]
    public async Task EveryCapabilityHasAKnownSource()
    {
        // Arrange
        var names = TermCapabilities.All.Select(c => c.Name).ToList();

        // Act
        var duplicates = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        // Assert
        await Assert.That(duplicates).IsEmpty();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task CompiledEntryAgreesWithXtGetTcap()
    {
        // Arrange
        if (Which("tic") is null || Which("infocmp") is null) Skip.Test("tic / infocmp not installed");
        string dir = TempDir();
        try
        {
            var compiled = Compiled(dir);
            var mismatches = new List<string>();

            // Act: every capability XTGETTCAP answers from the entry (written or inherited) must be in the compiled
            // entry with the same value.
            foreach (var cap in TermCapabilities.All)
            {
                if (cap.Source == CapSource.QueryOnly) continue;
                bool present = compiled.TryGetValue(cap.Name, out var value);
                if (cap.Kind == CapKind.Cancel) { if (present) mismatches.Add($"{cap.Name} should be cancelled"); continue; }
                if (!present) { mismatches.Add($"{cap.Name} missing"); continue; }
                string expected = TermCapabilities.ReplyValue(cap, TermCapabilities.Name) ?? "";
                string actual = cap.Kind == CapKind.String ? TermCapabilities.DecodeTerminfo(value!) : value!;
                if (actual != expected) mismatches.Add($"{cap.Name}: entry {value}, XTGETTCAP {cap.Value}");
            }

            // Assert
            await Assert.That(mismatches).IsEmpty();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task EntryExtensionsAreAllInTheTable()
    {
        // Arrange: the capabilities the entry adds or changes relative to xterm-256color
        if (Which("tic") is null || Which("infocmp") is null) Skip.Test("tic / infocmp not installed");
        string dir = TempDir();
        try
        {
            var compiled = Compiled(dir);
            var baseCaps = Parse(Run("infocmp", "-x", "-1", "-q", TermCapabilities.BaseEntry));

            // Act
            var unknown = compiled
                .Where(kv => !baseCaps.TryGetValue(kv.Key, out var v) || v != kv.Value)
                .Select(kv => kv.Key)
                .Where(name => !TermCapabilities.Lookup(name, out _))
                .ToList();

            // Assert
            await Assert.That(unknown).IsEmpty();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    [Arguments(@"\E[4:%p1%dm", "\e[4:%p1%dm")]
    [Arguments(@"^?", "\x7f")]
    [Arguments(@"^G", "\a")]
    [Arguments(@"\007", "\a")]
    [Arguments(@"a\,b\\c\:d", @"a,b\c:d")]
    public async Task DecodesTerminfoEscapes(string source, string expected)
    {
        // Act
        string decoded = TermCapabilities.DecodeTerminfo(source);

        // Assert
        await Assert.That(decoded).IsEqualTo(expected);
    }

    // ---- TERM selection ----------------------------------------------------------------------------

    [Test]
    public async Task AutoUsesErikTermWhenTheEntryIsInHomeTerminfo()
    {
        // Arrange
        string home = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".terminfo", "x"));
            File.WriteAllText(Path.Combine(home, ".terminfo", "x", TermCapabilities.Name), "");

            // Act
            string term = TerminfoLocator.Resolve("auto", _ => null, home);

            // Assert
            await Assert.That(term).IsEqualTo(TermCapabilities.Name);
        }
        finally { Directory.Delete(home, true); }
    }

    [Test]
    [ExcludeOn(OS.Windows)] // TERMINFO_DIRS is colon-separated, which a drive letter breaks; Windows sets no TERM (TermWindow.ChildEnvironment)
    public async Task AutoFindsTheHashedMacOsLayoutInTerminfoDirs()
    {
        // Arrange
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "78"));
            File.WriteAllText(Path.Combine(dir, "78", TermCapabilities.Name), "");

            // Act
            string term = TerminfoLocator.Resolve("auto", k => k == "TERMINFO_DIRS" ? dir + ":" : null, home: null);

            // Assert
            await Assert.That(term).IsEqualTo(TermCapabilities.Name);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task AutoFallsBackToXterm256ColorWithoutTheEntry()
    {
        // Arrange
        string home = TempDir();
        try
        {
            // Act: an empty home, TERMINFO pointing at an empty directory (and the system dirs, which lack it in CI)
            bool installedSystemWide = TerminfoLocator.SystemDirectories.Any(d => File.Exists(Path.Combine(d, "x", TermCapabilities.Name)));
            string term = TerminfoLocator.Resolve("auto", k => k == "TERMINFO" ? home : null, home);

            // Assert
            await Assert.That(term).IsEqualTo(installedSystemWide ? TermCapabilities.Name : TermCapabilities.BaseEntry);
        }
        finally { Directory.Delete(home, true); }
    }

    [Test]
    [Arguments("xterm-256color")]
    [Arguments("xterm-erikterm")]
    [Arguments("screen-256color")]
    public async Task AnExplicitTermIsUsedAsGiven(string setting)
    {
        // Act
        string term = TerminfoLocator.Resolve(setting, _ => null, home: null);

        // Assert
        await Assert.That(term).IsEqualTo(setting);
    }

    [Test]
    public async Task TheSearchPathFollowsNcursesOrder()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["TERMINFO"] = "/a", ["TERMINFO_DIRS"] = "/b::/c" };

        // Act
        var path = TerminfoLocator.SearchPath(k => env.GetValueOrDefault(k), "/home/me").Take(4).ToList();

        // Assert
        await Assert.That(path).IsEquivalentTo(["/a", Path.Combine("/home/me", ".terminfo"), "/b", TerminfoLocator.SystemDirectories[0]], CollectionOrdering.Matching);
    }
}
