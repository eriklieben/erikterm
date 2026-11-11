using System.Runtime.InteropServices;
using System.Text;
using ErikTerm.App;
using ErikTerm.App.Ipc;
using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Config;
using ErikTerm.Pty;

namespace ErikTerm.Tests;

/// <summary>The macOS port's pure parts, checked on any OS: libc constants, paths, fonts, keys.</summary>
public class MacOsConstantsTests
{
    [Test]
    public async Task MacOsValuesMatchTheSdkHeaders()
    {
        // Arrange
        var mac = PtyConstants.MacOS;

        // Act
        var values = (mac.O_NOCTTY, mac.O_CLOEXEC, mac.O_NONBLOCK, mac.EAGAIN, mac.POSIX_SPAWN_SETSID, mac.POSIX_SPAWN_CLOEXEC_DEFAULT, mac.POSIX_SPAWN_SETEXEC);

        // Assert: bsd/sys/fcntl.h, bsd/sys/errno.h, bsd/sys/spawn.h
        await Assert.That(values).IsEqualTo((0x20000, 0x1000000, 0x4, 35, (short)0x400, (short)0x4000, (short)0x40));
    }

    [Test]
    public async Task MacIoctlNumbersFollowIoccom()
    {
        // Arrange: bsd/sys/ioccom.h — _IOW(g, n, t) = IOC_IN | sizeof(t) << 16 | g << 8 | n; _IO(g, n) = IOC_VOID | g << 8 | n
        const ulong IOC_IN = 0x80000000, IOC_VOID = 0x20000000;
        ulong tiocswinsz = IOC_IN | (8UL << 16) | ((ulong)'t' << 8) | 103; // struct winsize: four unsigned shorts
        ulong tiocsctty = IOC_VOID | ((ulong)'t' << 8) | 97;

        // Act
        var mac = PtyConstants.MacOS;

        // Assert
        await Assert.That(mac.TIOCSWINSZ).IsEqualTo(tiocswinsz);
        await Assert.That(mac.TIOCSCTTY).IsEqualTo(tiocsctty);
    }

    [Test]
    public async Task LinuxValuesAreTheOnesUsedBefore()
    {
        // Act
        var linux = PtyConstants.For(macOS: false);

        // Assert
        await Assert.That((linux.O_NOCTTY, linux.O_CLOEXEC, linux.O_NONBLOCK, linux.EAGAIN, linux.TIOCSWINSZ, linux.POSIX_SPAWN_SETSID))
            .IsEqualTo((0x100, 0x80000, 0x800, 11, 0x5414UL, (short)0x80));
    }

    [Test]
    public async Task TheCurrentTableIsThisOperatingSystems()
    {
        // Act
        var current = PtyConstants.Current;

        // Assert
        await Assert.That(current).IsSameReferenceAs(OperatingSystem.IsMacOS() ? PtyConstants.MacOS : PtyConstants.Linux);
    }

    [Test]
    [Arguments(true, Architecture.Arm64, true)]
    [Arguments(true, Architecture.X64, false)]
    [Arguments(false, Architecture.Arm64, false)]
    [Arguments(false, Architecture.X64, false)]
    public async Task OnlyAppleArm64PassesVariadicArgumentsOnTheStack(bool macOS, Architecture arch, bool expected)
    {
        // Act
        bool onStack = PtyConstants.IsVariadicOnStack(macOS, arch);

        // Assert
        await Assert.That(onStack).IsEqualTo(expected);
    }

    [Test]
    public async Task ProcVnodePathInfoHasTheSdkSize()
    {
        // Act: two vnode_info_path (vnode_info 152 + MAXPATHLEN 1024)
        var layout = (PtyConstants.ProcVnodePathInfoSize, PtyConstants.CwdPathOffset);

        // Assert
        await Assert.That(layout).IsEqualTo((2352, 152));
    }
}

public class MacOsProcessTests
{
    private static byte[] ProcArgs(int argc, string exe, int padding, params string[] rest)
    {
        var bytes = new List<byte>(BitConverter.GetBytes(argc));
        bytes.AddRange(Encoding.UTF8.GetBytes(exe));
        bytes.AddRange(new byte[1 + padding]);
        foreach (var s in rest)
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(s));
            bytes.Add(0);
        }
        return [.. bytes];
    }

    [Test]
    public async Task ParsesKernProcArgs2()
    {
        // Arrange: argc, exec path, NUL padding, argv, then the environment
        byte[] raw = ProcArgs(3, "/opt/homebrew/bin/nvim", 5, "nvim", "-u", "NONE", "HOME=/Users/me", "TERM=xterm-256color");

        // Act
        var argv = ProcessInfo.ParseProcArgs2(raw);

        // Assert
        await Assert.That(argv).IsEquivalentTo(["nvim", "-u", "NONE"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task KeepsNonAsciiArguments()
    {
        // Arrange
        byte[] raw = ProcArgs(2, "/usr/bin/less", 0, "less", "notities — één.md");

        // Act
        var argv = ProcessInfo.ParseProcArgs2(raw);

        // Assert
        await Assert.That(argv).IsEquivalentTo(["less", "notities — één.md"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task RejectsANonsenseArgumentCount(int argc)
    {
        // Arrange
        byte[] raw = ProcArgs(argc, "/bin/zsh", 0, "zsh");

        // Act
        var argv = ProcessInfo.ParseProcArgs2(raw);

        // Assert
        await Assert.That(argv).IsNull();
    }

    [Test]
    public async Task RejectsATruncatedBuffer()
    {
        // Arrange: argc says 3, only one argument follows (and no NUL after it would be fine for the last one only)
        byte[] raw = ProcArgs(3, "/bin/zsh", 0, "zsh");

        // Act
        var argv = ProcessInfo.ParseProcArgs2(raw);

        // Assert
        await Assert.That(argv).IsNull();
    }

    [Test]
    public async Task HelperArgvCarriesDirectoryProgramAndArgv()
    {
        // Act
        var argv = Pty.Pty.SpawnHelperArgv(["/Applications/erikterm"], "zsh", ["-zsh", "-l"], "/Users/me/src");

        // Assert
        await Assert.That(argv).IsEquivalentTo(
            ["/Applications/erikterm", Pty.Pty.SpawnHelperArgument, "/Users/me/src", "zsh", "-zsh", "-l"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task HelperArgvKeepsTheDirectoryWhenNoneIsGiven()
    {
        // Act
        var argv = Pty.Pty.SpawnHelperArgv(["dotnet", "ErikTerm.Tests.dll"], "/bin/sh", ["sh", "-c", "true"], null);

        // Assert
        await Assert.That(argv).IsEquivalentTo(
            ["dotnet", "ErikTerm.Tests.dll", Pty.Pty.SpawnHelperArgument, "", "/bin/sh", "sh", "-c", "true"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(true, new[] { "/bin/zsh", "-l" })]
    [Arguments(false, new[] { "/bin/zsh" })]
    public async Task OnlyMacOsStartsALoginShell(bool macOS, string[] expected)
    {
        // Act
        var argv = Pty.Pty.DefaultShellCommand("/bin/zsh", macOS);

        // Assert
        await Assert.That(argv).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }
}

public class MacOsIpcPathTests
{
    [Test]
    public async Task UsesThePerUserTempDirectory()
    {
        // Arrange: what confstr(_CS_DARWIN_USER_TEMP_DIR) / $TMPDIR look like
        const string temp = "/var/folders/zz/zyxvpxvq6csfxvn_n0000000000000/T/";

        // Act
        string dir = IpcPaths.MacDirectory(temp, 501);

        // Assert
        await Assert.That(dir).IsEqualTo("/var/folders/zz/zyxvpxvq6csfxvn_n0000000000000/T/erikterm");
        await Assert.That(Encoding.UTF8.GetByteCount(IpcPaths.SocketFor(dir, 9_999_999))).IsLessThanOrEqualTo(IpcPaths.MacMaxSocketPathBytes);
    }

    [Test]
    public async Task FallsBackToTmpWhenSocketPathsWouldNotFit()
    {
        // Arrange: sun_path is 104 bytes on macOS
        string temp = "/Volumes/Some Very Long External Disk Name/private/var/folders/zz/zyxvpxvq6csfxvn_n0000000000000/T/";

        // Act
        string dir = IpcPaths.MacDirectory(temp, 501);

        // Assert
        await Assert.That(dir).IsEqualTo("/tmp/erikterm-501");
    }

    [Test]
    [Arguments("")]
    [Arguments("relative/T")]
    public async Task FallsBackToTmpWithoutAnAbsoluteTempDirectory(string temp)
    {
        // Act
        string dir = IpcPaths.MacDirectory(temp, 502);

        // Assert
        await Assert.That(dir).IsEqualTo("/tmp/erikterm-502");
    }

    [Test]
    [Arguments(96, true)]
    [Arguments(103, true)]
    [Arguments(104, false)]
    public async Task SocketPathLimitIsInclusive(int socketBytes, bool fits)
    {
        // Arrange: a directory whose longest socket path ("/erikterm-9999999.sock" is 22 bytes) is socketBytes long
        string dir = "/" + new string('d', socketBytes - 1 - 22);

        // Act
        bool ok = IpcPaths.FitsSocketPaths(dir, IpcPaths.MacMaxSocketPathBytes);

        // Assert
        await Assert.That(ok).IsEqualTo(fits);
    }

    [Test]
    public async Task MacStatOffsetsMatchTheSdkStruct()
    {
        // Act: struct stat (64-bit inodes): dev_t, mode_t, nlink_t, ino_t, uid_t …
        var offsets = (UnixFiles.MacStatModeOffset, UnixFiles.MacStatUidOffset, UnixFiles.MacStatSize);

        // Assert
        await Assert.That(offsets).IsEqualTo((4, 16, 144));
    }
}

public class MacOsFontFileTests
{
    [Test]
    [Arguments("/System/Library/Fonts/Menlo.ttc#1", "/System/Library/Fonts/Menlo.ttc", 1)]
    [Arguments("/System/Library/Fonts/Menlo.ttc", "/System/Library/Fonts/Menlo.ttc", 0)]
    [Arguments("/fonts/C#.ttf", "/fonts/C#.ttf", 0)]
    [Arguments("/fonts/odd#name", "/fonts/odd#name", 0)]
    [Arguments("/fonts/a.ttc#", "/fonts/a.ttc#", 0)]
    public async Task SplitsAFaceIndexOffTheFile(string path, string file, int index)
    {
        // Act
        var split = FontFiles.Split(path, exists: _ => false);

        // Assert
        await Assert.That(split).IsEqualTo((file, index));
    }

    [Test]
    public async Task AFileThatExistsWithAHashInItsNameIsTakenLiterally()
    {
        // Act
        var split = FontFiles.Split("/fonts/weird#2", exists: p => p == "/fonts/weird#2");

        // Assert
        await Assert.That(split).IsEqualTo(("/fonts/weird#2", 0));
    }

    [Test]
    [Arguments(0, "/f/Menlo.ttc")]
    [Arguments(3, "/f/Menlo.ttc#3")]
    public async Task JoinsOnlyFacesPastTheFirst(int index, string expected)
    {
        // Act
        string path = FontFiles.Join("/f/Menlo.ttc", index);

        // Assert
        await Assert.That(path).IsEqualTo(expected);
    }

    [Test]
    [Arguments("2", "/f/a.ttc#2")]
    [Arguments("65536", "/f/a.ttc")]   // named instance 1 of face 0
    [Arguments("131073", "/f/a.ttc#1")] // named instance 2 of face 1
    [Arguments("x", "/f/a.ttc")]
    public async Task FontconfigIndexKeepsOnlyTheFace(string index, string expected)
    {
        // Act
        string path = FontFiles.FromFontconfig("/f/a.ttc", index);

        // Assert
        await Assert.That(path).IsEqualTo(expected);
    }

    [Test]
    public async Task FontconfigListKeysBecomeLocatorPaths()
    {
        // Arrange
        const string fcList = "/f/Menlo.ttc#0\t20-7e\n/f/Menlo.ttc#1\t20-7e\n/f/Var.ttf#65536\t20-7e\n";

        // Act
        var coverage = FontCoverage.Parse(FontFiles.NormalizeFontconfigList(fcList));

        // Assert: Menlo's two faces stay apart, the variable font's instances merge into its file
        await Assert.That(coverage.FileCount).IsEqualTo(3);
        await Assert.That(coverage.Covers("/f/Menlo.ttc#1", 'A')).IsTrue();
        await Assert.That(coverage.Covers("/f/Var.ttf", 'A')).IsTrue();
    }

    [Test]
    public async Task ListsScalableFontFilesOncePerDirectoryOrder()
    {
        // Arrange: a fake file system
        var fs = new Dictionary<string, string[]>
        {
            [FontFiles.MacFontDirectories("/Users/me")[0]] = ["/Users/me/Library/Fonts/JetBrainsMono-Regular.ttf", "/Users/me/Library/Fonts/readme.txt"],
            ["/Library/Fonts"] = ["/Library/Fonts/Arial Unicode.ttf"],
            ["/System/Library/Fonts"] = ["/System/Library/Fonts/Menlo.ttc", "/System/Library/Fonts/Supplemental/Courier New.ttf", "/System/Library/Fonts/Apple Color Emoji.ttc", "/System/Library/Fonts/LastResort.otf", "/System/Library/Fonts/Geneva.dfont"],
        };
        IEnumerable<string> List(string dir) => fs.TryGetValue(dir, out var files) ? files : throw new DirectoryNotFoundException(dir);

        // Act
        var files = FontFiles.MacFontFiles(FontFiles.MacFontDirectories("/Users/me"), List);

        // Assert
        await Assert.That(files).IsEquivalentTo(
        [
            "/Users/me/Library/Fonts/JetBrainsMono-Regular.ttf", "/Library/Fonts/Arial Unicode.ttf", "/System/Library/Fonts/Menlo.ttc",
            "/System/Library/Fonts/Supplemental/Courier New.ttf", "/System/Library/Fonts/Apple Color Emoji.ttc",
            "/System/Library/Fonts/LastResort.otf", "/System/Library/Fonts/Geneva.dfont",
        ], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("/u/JetBrainsMono-Bold.ttf", "JetBrains Mono", true)]
    [Arguments("/s/SF-Mono-Regular.otf", "SF Mono", true)]
    [Arguments("/s/Apple Color Emoji.ttc", "Apple Color Emoji", true)]
    [Arguments("/s/Menlo.ttc", "menlo", true)]
    [Arguments("/s/Monaco.ttf", "Menlo", false)]
    [Arguments("/s/Menlo.ttc", "", false)]
    public async Task FileNamesSuggestFamilies(string file, string family, bool expected)
    {
        // Act
        bool suggests = FontFiles.NameSuggests(file, family);

        // Assert
        await Assert.That(suggests).IsEqualTo(expected);
    }

    [Test]
    public async Task FindsHomebrewFontconfigAppleSiliconPrefixFirst()
    {
        // Arrange
        var installed = new HashSet<string>(((string[])["/opt/homebrew/bin", "/usr/local/bin"]).SelectMany(d => (string[])[Path.Combine(d, "fc-match"), Path.Combine(d, "fc-list")]));

        // Act
        string? dir = FontFiles.MacFontconfigDirectory(installed.Contains);

        // Assert
        await Assert.That(dir).IsEqualTo("/opt/homebrew/bin");
    }

    [Test]
    public async Task NoFontconfigWithoutBothTools()
    {
        // Arrange
        var installed = new HashSet<string> { "/usr/local/bin/fc-match" };

        // Act
        string? dir = FontFiles.MacFontconfigDirectory(installed.Contains);

        // Assert
        await Assert.That(dir).IsNull();
    }

    [Test]
    public async Task CoverageFromRangesKeepsTheGivenOrder()
    {
        // Arrange: Menlo first (preferred), a CJK font that also has ASCII after it
        var coverage = FontCoverage.FromRanges(
        [
            ("/s/Menlo.ttc", [(0x20, 0x7E)]),
            ("/s/PingFang.ttc#2", [(0x20, 0x7E), (0x4E00, 0x9FFF)]),
        ]);

        // Act
        var (ascii, han, none) = (coverage.FirstCovering('A'), coverage.FirstCovering(0x4E2D), coverage.FirstCovering(0x1F600));

        // Assert
        await Assert.That(ascii).IsEqualTo("/s/Menlo.ttc");
        await Assert.That(han).IsEqualTo("/s/PingFang.ttc#2");
        await Assert.That(none).IsNull();
    }
}

/// <summary>MacFontLocator's FreeType scan, on whatever fonts this machine has (copied into a fake font directory).</summary>
[ExcludeOn(OS.Windows)]
[NotInParallel(Isolation.Natives)]
public class MacFontLocatorScanTests
{
    private static string? RealMonospace()
    {
        NativeLibraries.Register();
        return OperatingSystem.IsMacOS() ? "/System/Library/Fonts/Menlo.ttc" : new FontconfigLocator().DefaultMonospace();
    }

    private static unsafe (string? Family, string? Style, long Faces) Probe(string path, int index)
    {
        nint lib, face;
        if (FreeType.InitFreeType(&lib) != 0) return (null, null, 0);
        try
        {
            if (FreeType.NewFace(lib, path, index, &face) != 0) return (null, null, 0);
            var result = (FreeType.FamilyName(face), FreeType.StyleName(face), FreeType.NumFaces(face));
            FreeType.DoneFace(face);
            return result;
        }
        finally
        {
            FreeType.DoneFreeType(lib);
        }
    }

    /// <summary>
    /// An installed font whose typographic family (name ID 16) differs from its family_name (ID 1) and whose
    /// typographic subfamily is Regular — a Nerd Font ("JetBrainsMono NF" vs "JetBrainsMono Nerd Font") — or null.
    /// </summary>
    private static unsafe (string Path, string Legacy, string Typographic)? FontWithTwoFamilyNames()
    {
        NativeLibraries.Register(); // not every test that runs first has done this
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] dirs = ["/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(home, ".local/share/fonts"),
            Path.Combine(home, "Library/Fonts"), "/Library/Fonts"];
        nint lib;
        if (FreeType.InitFreeType(&lib) != 0) return null;
        try
        {
            foreach (var dir in dirs.Where(Directory.Exists))
                foreach (var file in Directory.EnumerateFiles(dir, "*-Regular.ttf", SearchOption.AllDirectories).Take(400))
                {
                    nint face;
                    if (FreeType.NewFace(lib, file, 0, &face) != 0) continue;
                    try
                    {
                        var (typo, typoStyle) = FreeType.TypographicNames(face);
                        if (FreeType.FamilyName(face) is { } legacy && typo is { Length: > 0 } && typoStyle is null or "Regular"
                            && !string.Equals(legacy, typo, StringComparison.OrdinalIgnoreCase))
                            return (file, legacy, typo);
                    }
                    finally
                    {
                        FreeType.DoneFace(face);
                    }
                }
        }
        finally
        {
            FreeType.DoneFreeType(lib);
        }
        return null;
    }

    [Test]
    public async Task FindsAFamilyByItsTypographicName()
    {
        // Arrange
        if (FontWithTwoFamilyNames() is not { } font) { Skip.Test("no installed font with a separate typographic family name"); return; }
        string fileName = font.Typographic.Replace(" ", "") + "-Regular.ttf"; // named after its family, as installed
        string dir = FontDirectoryWith(font.Path, fileName);
        try
        {
            var locator = new MacFontLocator([dir]);

            // Act — the name fontconfig reports, which a config written on Linux uses
            string? found = locator.Find(font.Typographic, FontStyle.Regular);

            // Assert
            await Assert.That(found).IsEqualTo(Path.Combine(dir, "Supplemental", fileName));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The first face after face 0 whose (family, style) no earlier face has, with a style the locator indexes.</summary>
    private static (string, int, string, FontStyle)? DistinctLaterFace(string ttc)
    {
        var seen = new HashSet<(string, FontStyle)>();
        long faces = Probe(ttc, 0).Faces;
        for (int i = 0; i < faces && i < 64; i++)
        {
            var (family, styleName, _) = Probe(ttc, i);
            if (family is null || family.StartsWith('.') || !WindowsFontNames.TryParseStyleName(styleName, out bool bold, out bool italic)) continue;
            var style = (bold ? FontStyle.Bold : FontStyle.Regular) | (italic ? FontStyle.Italic : FontStyle.Regular);
            if (seen.Add((family, style)) && i > 0) return (ttc, i, family, style);
        }
        return null;
    }

    private static string FontDirectoryWith(string font, string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "erikterm-macfonts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Supplemental"));
        File.Copy(font, Path.Combine(dir, "Supplemental", name));
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a font");
        return dir;
    }

    [Test]
    public async Task FindsAFamilyByItsName()
    {
        // Arrange
        string? mono = RealMonospace();
        if (mono is null || !File.Exists(mono)) Skip.Test("no monospace font installed");
        var (family, style, _) = Probe(mono!, 0);
        if (family is null) Skip.Test("FreeType can't read " + mono);
        // Named after its family, as installed fonts are: the built-in families (Menlo, SF Mono, …) are only
        // looked up by file name, never with a full scan, so a copy named "Whatever" would be skipped on purpose.
        string fileName = family!.Replace(" ", "") + "-Test" + Path.GetExtension(mono);
        string dir = FontDirectoryWith(mono!, fileName);
        try
        {
            var locator = new MacFontLocator([dir]);
            bool regular = WindowsFontNames.TryParseStyleName(style, out bool bold, out bool italic) && !bold && !italic;

            // Act
            string? found = locator.Find(family!, FontStyle.Regular);
            string? missing = locator.Find("No Such Family At All", FontStyle.Regular);

            // Assert
            if (regular) await Assert.That(found).IsEqualTo(Path.Combine(dir, "Supplemental", fileName));
            await Assert.That(missing).IsNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task CoverageCoversAsciiFromTheScannedFont()
    {
        // Arrange
        string? mono = RealMonospace();
        if (mono is null || !File.Exists(mono)) Skip.Test("no monospace font installed");
        string dir = FontDirectoryWith(mono!, "Mono" + Path.GetExtension(mono));
        try
        {
            var locator = new MacFontLocator([dir]);

            // Act
            var coverage = locator.BuildCoverage();
            string? forA = locator.FindForCodepoint('A');

            // Assert
            await Assert.That(coverage).IsNotNull();
            await Assert.That(coverage!.AnyCovers('A')).IsTrue();
            await Assert.That(forA is not null && forA.StartsWith(Path.Combine(dir, "Supplemental", "Mono"), StringComparison.Ordinal)).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task NamesTheFacesOfACollection()
    {
        // Arrange: an installed .ttc with a face past the first whose family and plain style ("Bold", …) are its own
        NativeLibraries.Register();
        IEnumerable<string> collections = OperatingSystem.IsMacOS() ? ["/System/Library/Fonts/Menlo.ttc"]
            : new[] { "/usr/share/fonts" }.Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.ttc", SearchOption.AllDirectories)).Take(40);
        (string Path, int Index, string Family, FontStyle Style)? pick = null;
        foreach (var file in collections.Where(File.Exists))
        {
            if ((pick = DistinctLaterFace(file)) is not null) break;
        }
        if (pick is not { } p) { Skip.Test("no font collection with a usable second face installed"); return; }
        var (ttc, index, family, style) = p;
        string fileName = family.Replace(" ", "") + "-Test.ttc"; // named after its family (see FindsAFamilyByItsName)
        string dir = FontDirectoryWith(ttc, fileName);
        try
        {
            var locator = new MacFontLocator([dir]);

            // Act
            string? found = locator.Find(family, style);

            // Assert
            await Assert.That(found).IsEqualTo(FontFiles.Join(Path.Combine(dir, "Supplemental", fileName), index));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class MacOsKeyTests
{
    [Test]
    public async Task MacDefaultBindingsAllParse()
    {
        // Act
        int built = DefaultKeys.Build(macOS: true).Count;

        // Assert
        await Assert.That(built).IsEqualTo(DefaultKeys.MacTable.Length + DefaultKeys.Table.Length);
    }

    [Test]
    public async Task MacDefaultsBindCommandChords()
    {
        // Act
        var keys = DefaultKeys.Build(macOS: true).ToDictionary(b => b.Chord, b => b.Action.ToString());

        // Assert
        await Assert.That(keys[new KeyChord(KeyMods.Super, "c")]).IsEqualTo("CopyTo(Clipboard)");
        await Assert.That(keys[new KeyChord(KeyMods.Super, "v")]).IsEqualTo("PasteFrom(Clipboard)");
        await Assert.That(keys[new KeyChord(KeyMods.Super, "t")]).IsEqualTo("SpawnTab");
        await Assert.That(keys[new KeyChord(KeyMods.Super, "w")]).IsEqualTo("CloseCurrentPane");
        await Assert.That(keys[new KeyChord(KeyMods.Super, "n")]).IsEqualTo("SpawnWindow");
        await Assert.That(keys[new KeyChord(KeyMods.Super, "f")]).IsEqualTo("Search");
        await Assert.That(keys[new KeyChord(KeyMods.Super | KeyMods.Shift, "p")]).IsEqualTo("ActivateCommandPalette");
        await Assert.That(keys[new KeyChord(KeyMods.Ctrl | KeyMods.Shift, "c")]).IsEqualTo("CopyTo(Clipboard)"); // still there
    }

    [Test]
    public async Task MacDefaultsHaveNoDuplicateChords()
    {
        // Act
        var dupes = DefaultKeys.Build(macOS: true).GroupBy(b => b.Chord).Where(g => g.Count() > 1).Select(g => g.Key.ToString()).ToList();

        // Assert
        await Assert.That(dupes).IsEmpty();
    }

    [Test]
    public async Task MacDefaultsLeaveTheMenuBarShortcutsAlone()
    {
        // Arrange: handled by SDL's menu bar (quit, hide, minimize, full screen)
        KeyChord[] menu =
        [
            new(KeyMods.Super, "q"), new(KeyMods.Super, "h"), new(KeyMods.Super, "m"), new(KeyMods.Super | KeyMods.Ctrl, "f"),
        ];

        // Act
        var chords = DefaultKeys.Build(macOS: true).Select(b => b.Chord).ToHashSet();

        // Assert
        await Assert.That(menu.Where(chords.Contains)).IsEmpty();
    }

    [Test]
    public async Task OtherSystemsHaveNoCommandBindings()
    {
        // Act
        var keys = DefaultKeys.Build(macOS: false);

        // Assert
        await Assert.That(keys.Count).IsEqualTo(DefaultKeys.Table.Length);
        await Assert.That(keys.Any(b => b.Chord.Mods == KeyMods.Super)).IsFalse();
    }

    private const ushort LAlt = 0x0100, RAlt = 0x0200, Shift = 0x0001;

    [Test]
    [Arguments("left", LAlt, LAlt)]
    [Arguments("left", RAlt, RAlt)]
    [Arguments("right", RAlt, LAlt)]
    [Arguments("right", LAlt, RAlt)]
    [Arguments("both", RAlt, LAlt)]
    [Arguments("both", (ushort)(LAlt | RAlt), LAlt)]
    [Arguments("none", LAlt, RAlt)]
    [Arguments("left", (ushort)(Shift | LAlt), (ushort)(Shift | LAlt))]
    [Arguments("none", Shift, Shift)]
    public async Task OptionKeysMapToAltAsConfigured(string setting, ushort mod, ushort expected)
    {
        // Act
        ushort mapped = MacKeys.MapOption(mod, setting);

        // Assert
        await Assert.That(mapped).IsEqualTo(expected);
    }

    [Test]
    [Arguments("\"both\"", "both")]
    [Arguments("\"Right\"", "right")]
    public async Task OptionAsAltIsASetting(string value, string expected)
    {
        // Act
        var result = SettingsLoader.Parse($"macos_option_as_alt = {value}");

        // Assert
        await Assert.That(result.Settings.MacosOptionAsAlt).IsEqualTo(expected);
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task OptionAsAltRejectsOtherValues()
    {
        // Act
        var result = SettingsLoader.Parse("macos_option_as_alt = \"meta\"");

        // Assert
        await Assert.That(result.Settings.MacosOptionAsAlt).IsEqualTo("left");
        await Assert.That(result.Diagnostics).Contains(d => d.Contains("macos_option_as_alt", StringComparison.Ordinal));
    }
}
