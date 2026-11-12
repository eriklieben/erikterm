using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using ErikTerm.App.Ipc;
using ErikTerm.App.Notifications;
using ErikTerm.App.Panes;
using ErikTerm.App.Security;
using ErikTerm.App.Session;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Core.Images;

namespace ErikTerm.Tests;

/// <summary>
/// Regression tests for the security review's proofs of concept. Each recreates its PoC's input here (no files
/// from outside the repository) and checks it is now harmless.
/// </summary>
public class SecurityTests
{
    private static (Terminal T, List<byte[]> Replies) Term(int cols = 80, int rows = 24)
    {
        var t = new Terminal(cols, rows) { CellPixelWidth = 10, CellPixelHeight = 20 };
        var replies = new List<byte[]>();
        t.Response = d => replies.Add(d.ToArray());
        return (t, replies);
    }

    private static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    private static string Kitty(string control, string payload = "") => $"\e_G{control};{payload}\e\\";

    /// <summary>The replies as text, to compare with the expected answers.</summary>
    private static List<string> Texts(List<byte[]> replies) => [.. replies.Select(r => Encoding.ASCII.GetString(r))];

    /// <summary>Only printable ASCII, with ESC only where replies are framed (first byte, and an ST at the end).</summary>
    private static async Task AssertSafeReply(byte[] reply)
    {
        for (int i = 0; i < reply.Length; i++)
        {
            byte b = reply[i];
            if (b is >= 0x20 and < 0x7F) continue;
            bool framing = b == 0x1B && (i == 0 || (i == reply.Length - 2 && reply[^1] == '\\'))
                || b == 0x07 && i == reply.Length - 1 && reply.Length > 2 && reply[0] == 0x1B && reply[1] == ']'; // OSC reply ended like its query
            await Assert.That(framing).IsTrue()
                .Because($"control byte 0x{b:x2} at {i} in reply {Encoding.ASCII.GetString(reply).Replace("\e", "\\e")}");
        }
    }

    /// <summary>A unique temporary directory (with a short name: sockets go in it), deleted with everything in it.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "et-sec-" + Guid.NewGuid().ToString("N")[..10]);

        public TempDir() => Directory.CreateDirectory(Path);

        public string File(string name, byte[] contents)
        {
            string file = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllBytes(file, contents);
            return file;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>
    /// CPU time (ms) this thread spends on <paramref name="work"/> with a fresh terminal, for each of a few rounds.
    /// Bounds are checked against the best round: CPU time, not wall-clock time, so a busy machine doesn't fail them.
    /// </summary>
    private static List<double> CpuMsPerRound(Action<Terminal> work, int rounds = 3)
    {
        var ms = new List<double>();
        for (int round = 0; round < rounds; round++)
        {
            var (t, _) = Term();
            ms.Add(ThreadCpuTime.Measure(() => work(t)).TotalMilliseconds);
        }
        return ms;
    }

    private static string Rounds(List<double> ms) =>
        $"{ms.Min():F0} ms CPU at best (rounds: {string.Join(", ", ms.Select(m => m.ToString("F0")))})";

    // ---- 1. kitty error replies echoed file names into the PTY --------------------------------------------

    private const string CouldNotRead = "\e_Gi=1;EBADF:could not read the file\e\\";

    /// <summary>
    /// PoC poc/inject: a README that, when cat'ed, asks for a planted file whose name is "x\x15touch PWNED\r"; the
    /// error reply used to echo the name, so zsh/bash read ^U (kill line), "touch PWNED", Enter.
    /// </summary>
    [Test]
    // The PoC's exact request (a=q, t=f, default format, no size), then variants that do get to the file; each with
    // file transmission off and on.
    [Arguments(false, "a=q,i=1,t=f", "planted")]
    [Arguments(false, "a=q,i=2,t=f,f=100", "planted")]
    [Arguments(false, "a=t,i=3,t=f,f=32,s=1,v=1", "planted")]
    [Arguments(false, "a=t,i=4,t=f,f=100", "missing")]
    [Arguments(false, "a=t,i=5,t=t,f=100", "missing")]
    [Arguments(false, "a=t,i=6,t=s,f=100", "shared memory")]
    [Arguments(true, "a=q,i=1,t=f", "planted")]
    [Arguments(true, "a=q,i=2,t=f,f=100", "planted")]
    [Arguments(true, "a=t,i=3,t=f,f=32,s=1,v=1", "planted")]
    [Arguments(true, "a=t,i=4,t=f,f=100", "missing")]
    [Arguments(true, "a=t,i=5,t=t,f=100", "missing")]
    [Arguments(true, "a=t,i=6,t=s,f=100", "shared memory")]
    public async Task Kitty_error_reply_never_echoes_a_planted_file_name(bool allowImageFiles, string control, string target)
    {
        // Arrange
        using var dir = new TempDir();
        // NTFS refuses control characters in a name, so nothing can be planted there: the request still names one.
        string planted = OperatingSystem.IsWindows()
            ? Path.Combine(dir.Path, "x\x15touch PWNED\r")
            : dir.File("x\x15touch PWNED\r", "secret"u8.ToArray());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(planted, UnixFileMode.None); // unreadable
        string missing = Path.Combine(dir.Path, "y\x15touch PWNED2\r");
        string name = target switch
        {
            "planted" => planted,
            "missing" => missing,
            _ => "\x15touch PWNED\r",
        };
        var (t, replies) = Term();
        t.AllowImageFiles = allowImageFiles;

        // Act
        Feed(t, "Just a harmless looking README\n" + Kitty(control, B64(name)));
        var texts = Texts(replies);

        // Assert
        var text = await Assert.That(texts).HasSingleItem();
        await AssertSafeReply(replies[0]);
        await Assert.That(text).DoesNotContain("touch");
        await Assert.That(text).DoesNotContain(dir.Path);
    }

    /// <summary>Finding 7: missing, unreadable, empty, too short, directory, FIFO and device all answer the same.</summary>
    [Test]
    [Arguments("missing")]
    [Arguments("empty")]
    [Arguments("directory")]
    [Arguments("device")]
    public async Task Kitty_file_errors_are_one_generic_reply(string kind)
    {
        // Arrange
        using var dir = new TempDir();
        string path = kind switch
        {
            "missing" => Path.Combine(dir.Path, "missing"),
            "empty" => dir.File("empty", []),
            "directory" => dir.Path,
            _ => "/dev/null",
        };
        var (t, replies) = Term();
        t.AllowImageFiles = true;

        // Act
        Feed(t, Kitty("a=t,i=1,t=f,f=32,s=1,v=1", B64(path)));
        var texts = Texts(replies);

        // Assert
        var text = await Assert.That(texts).HasSingleItem();
        await Assert.That(text).IsEqualTo(CouldNotRead);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task Kitty_file_error_for_an_unreadable_file_is_the_generic_reply()
    {
        // Arrange
        Skip.When(IsRoot(), "root can read the file anyway");
        using var dir = new TempDir();
        string unreadable = dir.File("unreadable", [1, 2, 3, 4]);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(unreadable, UnixFileMode.None);
        var (t, replies) = Term();
        t.AllowImageFiles = true;

        // Act
        Feed(t, Kitty("a=t,i=1,t=f,f=32,s=1,v=1", B64(unreadable)));
        var texts = Texts(replies);

        // Assert
        var text = await Assert.That(texts).HasSingleItem();
        await Assert.That(text).IsEqualTo(CouldNotRead);
    }

    [Test]
    [RunOn(OS.Linux)]
    [NotInParallel(Isolation.Processes)]
    public async Task Kitty_file_error_for_a_fifo_is_the_generic_reply()
    {
        // Arrange
        using var dir = new TempDir();
        string fifo = Path.Combine(dir.Path, "fifo");
        using (var mk = Process.Start("mkfifo", fifo)) mk.WaitForExit();
        var (t, replies) = Term();
        t.AllowImageFiles = true;

        // Act
        Feed(t, Kitty("a=t,i=1,t=f,f=32,s=1,v=1", B64(fifo)));
        var texts = Texts(replies);

        // Assert
        var text = await Assert.That(texts).HasSingleItem();
        await Assert.That(text).IsEqualTo(CouldNotRead);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Kitty_file_error_for_a_symlink_is_the_generic_reply()
    {
        // Arrange
        using var dir = new TempDir();
        string small = dir.File("small", [1, 2, 3, 4]); // a good 1×1 image, if the link were followed
        string link = Path.Combine(dir.Path, "link");
        File.CreateSymbolicLink(link, small);
        var (t, replies) = Term();
        t.AllowImageFiles = true;

        // Act
        Feed(t, Kitty("a=t,i=1,t=f,f=32,s=1,v=1", B64(link))); // O_NOFOLLOW: symlinks aren't followed
        var texts = Texts(replies);

        // Assert
        var text = await Assert.That(texts).HasSingleItem();
        await Assert.That(text).IsEqualTo(CouldNotRead);
    }

    [Test]
    // "file too short" used to report the size: S= / O= past the end look the same as a missing file.
    [Arguments("a=t,i=1,t=f,f=32,s=1,v=1,S=100")]
    [Arguments("a=t,i=1,t=f,f=32,s=1,v=1,O=2,S=4")]
    // Raw data shorter than s*v used to say "{have} < {need}"; a readable file that isn't an image, or isn't
    // zlib data, must not answer differently from a missing one either.
    [Arguments("a=t,i=1,t=f,f=32,s=2,v=2")]
    [Arguments("a=t,i=1,t=f,f=100")]
    [Arguments("a=t,i=1,t=f,f=32,s=1,v=1,o=z")]
    public async Task Kitty_file_that_reads_badly_gets_the_generic_reply(string control)
    {
        // Arrange
        using var dir = new TempDir();
        string small = dir.File("small", [1, 2, 3, 4]);
        var (t, replies) = Term();
        t.AllowImageFiles = true;

        // Act
        Feed(t, Kitty(control, B64(small)));
        var texts = Texts(replies);

        // Assert
        var text = await Assert.That(texts).HasSingleItem();
        await Assert.That(text).IsEqualTo(CouldNotRead);
    }

    [Test]
    public async Task Kitty_inline_data_keeps_its_specific_errors()
    {
        // Arrange
        var (t, replies) = Term();
        t.AllowImageFiles = true;

        // Act
        Feed(t, Kitty("a=t,i=1,f=32,s=2,v=2", Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })));
        var texts = Texts(replies);

        // Assert
        var text = await Assert.That(texts).HasSingleItem();
        await Assert.That(text).IsEqualTo("\e_Gi=1;ENODATA:insufficient image data\e\\");
    }

    private static bool IsRoot() => !OperatingSystem.IsWindows() && Environment.UserName == "root";

    [Test]
    public async Task Kitty_file_transmission_is_off_by_default()
    {
        // Arrange
        using var dir = new TempDir();
        string file = dir.File("pic.rgba", [1, 2, 3, 4]);
        var (t, replies) = Term();
        bool allowedByDefault = t.AllowImageFiles;

        // Act
        Feed(t, Kitty("a=t,i=1,t=f,f=32,s=1,v=1", B64(file)));
        var texts = Texts(replies);
        var stored = t.Images.ById(1);

        // Assert
        await Assert.That(allowedByDefault).IsFalse();
        var text = await Assert.That(texts).HasSingleItem();
        await Assert.That(text).IsEqualTo("\e_Gi=1;EPERM:file transmission is disabled\e\\");
        await Assert.That(stored).IsNull();
    }

    [Test]
    public async Task Kitty_inline_data_still_works_with_file_transmission_off()
    {
        // Arrange
        var (t, replies) = Term();

        // Act
        Feed(t, Kitty("a=t,i=2,f=32,s=1,v=1", Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })));
        var texts = Texts(replies);

        // Assert
        var text = await Assert.That(texts).HasSingleItem();
        await Assert.That(text).IsEqualTo("\e_Gi=2;OK\e\\");
    }

    [Test]
    [Arguments("", false)]
    [Arguments("allow_kitty_file_transmission = true", true)]
    public async Task Kitty_file_transmission_setting_is_off_unless_turned_on(string toml, bool allowed)
    {
        // Act
        bool setting = SettingsLoader.Parse(toml).Settings.AllowKittyFileTransmission;

        // Assert
        await Assert.That(setting).IsEqualTo(allowed);
    }

    /// <summary>A /dev/shm object with a unique name; it (and a "-link" next to it) is deleted afterwards.</summary>
    private sealed class SharedMemoryObject : IDisposable
    {
        public string Name { get; } = "et-sec-victim-" + Guid.NewGuid().ToString("N")[..8];

        public string Path => "/dev/shm/" + Name;

        public SharedMemoryObject(byte[] contents) => File.WriteAllBytes(Path, contents);

        public void Dispose()
        {
            File.Delete(Path + "-link");
            File.Delete(Path);
        }
    }

    private const string OtherProgramsState = "some other program's shared state";

    /// <summary>
    /// PoC harness (a): t=s used to delete any user-owned /dev/shm name, even one holding no image or that failed to
    /// read. Now only an object that was read is deleted; names with '/' (or too long) are refused.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    // Not the size the keys ask for (S= past the end): read fails, the object stays.
    [Arguments("a=q,i=1,t=s,f=32,s=100,v=100,S=40000")]
    // The PoC itself: read fine, but not an image. Only an object that gave an image is deleted.
    [Arguments("a=q,i=1,t=s,f=100")]
    public async Task Kitty_shared_memory_is_kept_when_it_gives_no_image(string control)
    {
        // Arrange
        Skip.Unless(Directory.Exists("/dev/shm"), "needs /dev/shm");
        using var victim = new SharedMemoryObject(Encoding.UTF8.GetBytes(OtherProgramsState));
        var (t, _) = Term();
        t.AllowImageFiles = true;

        // Act
        Feed(t, Kitty(control, B64(victim.Name)));
        bool kept = File.Exists(victim.Path);

        // Assert
        await Assert.That(kept).IsTrue();
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Kitty_shared_memory_name_that_leaves_dev_shm_is_refused()
    {
        // Arrange
        Skip.Unless(Directory.Exists("/dev/shm"), "needs /dev/shm");
        using var victim = new SharedMemoryObject(Encoding.UTF8.GetBytes(OtherProgramsState));
        var (t, replies) = Term();
        t.AllowImageFiles = true;

        // Act
        Feed(t, Kitty("a=q,i=1,t=s,f=100", B64("../../tmp/" + victim.Name)));
        var texts = Texts(replies);
        bool kept = File.Exists(victim.Path);

        // Assert
        var text = await Assert.That(texts).HasSingleItem();
        await Assert.That(text).IsEqualTo("\e_Gi=1;EINVAL:bad shared memory name\e\\");
        await Assert.That(kept).IsTrue();
    }

    /// <summary>A symlink in /dev/shm is not followed (nor deleted).</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Kitty_shared_memory_symlink_is_neither_followed_nor_deleted()
    {
        // Arrange
        Skip.Unless(Directory.Exists("/dev/shm"), "needs /dev/shm");
        using var victim = new SharedMemoryObject(Encoding.UTF8.GetBytes(OtherProgramsState));
        string link = victim.Path + "-link";
        File.CreateSymbolicLink(link, victim.Path);
        var (t, _) = Term();
        t.AllowImageFiles = true;

        // Act
        Feed(t, Kitty("a=t,i=2,t=s,f=32,s=1,v=1", B64(victim.Name + "-link")));
        bool linkKept = File.Exists(link) || new FileInfo(link).LinkTarget is not null;
        bool victimKept = File.Exists(victim.Path);

        // Assert
        await Assert.That(linkKept).IsTrue();
        await Assert.That(victimKept).IsTrue();
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Kitty_shared_memory_is_deleted_after_a_good_read()
    {
        // A good read deletes it.

        // Arrange
        Skip.Unless(Directory.Exists("/dev/shm"), "needs /dev/shm");
        using var victim = new SharedMemoryObject([1, 2, 3, 4]);
        var (t, replies) = Term();
        t.AllowImageFiles = true;

        // Act
        Feed(t, Kitty("a=t,i=3,t=s,f=32,s=1,v=1", B64(victim.Name)));
        var texts = Texts(replies);
        bool kept = File.Exists(victim.Path);

        // Assert
        var text = await Assert.That(texts).HasSingleItem();
        await Assert.That(text).IsEqualTo("\e_Gi=3;OK\e\\");
        await Assert.That(kept).IsFalse();
    }

    public static IEnumerable<string> NamesOutsideDevShm() => ["../etc/passwd", "a/b", new string('a', 256), ".."];

    /// <summary>Names that leave /dev/shm are refused before anything is opened.</summary>
    [Test]
    [MethodDataSource(nameof(NamesOutsideDevShm))]
    public async Task Shared_memory_names_that_leave_dev_shm_are_refused(string name)
    {
        // Act
        string? path = ImageFiles.SharedMemoryPath(name);

        // Assert
        await Assert.That(path).IsNull();
    }

    [Test]
    public async Task Shared_memory_name_may_start_with_a_slash()
    {
        // Act
        string? path = ImageFiles.SharedMemoryPath("/abc");

        // Assert
        await Assert.That(path).IsEqualTo("/dev/shm/abc");
    }

    [Test]
    [RunOn(OS.Linux)]
    [NotInParallel(Isolation.Processes)]
    public async Task Kitty_file_read_of_a_fifo_does_not_block()
    {
        // Finding 9: the file is opened O_NONBLOCK first and checked on the handle, so a FIFO can't hang the lock.

        // Arrange
        using var dir = new TempDir();
        string fifo = Path.Combine(dir.Path, "tty-graphics-protocol-fifo");
        using (var mk = Process.Start("mkfifo", fifo)) mk.WaitForExit();

        // Act
        byte[]? read = ImageFiles.Read(fifo, 0, 0);
        bool kept = File.Exists(fifo);

        // Assert
        await Assert.That(read).IsNull();
        await Assert.That(kept).IsTrue(); // not deleted: nothing was read
    }

    [Test]
    [RunOn(OS.Linux)]
    [Arguments("/proc/self/status")]
    [Arguments("/sys/kernel/mm/transparent_hugepage/enabled")]
    public async Task Kitty_file_read_refuses_proc_and_sys_files(string path)
    {
        // Act
        byte[]? read = ImageFiles.Read(path, 0, 0);

        // Assert
        await Assert.That(read).IsNull();
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Kitty_file_read_starts_at_the_offset()
    {
        // Arrange
        using var dir = new TempDir();
        string ok = dir.File("ok", [7, 8, 9]);

        // Act
        byte[]? read = ImageFiles.Read(ok, 1, 0);

        // Assert
        await Assert.That(read).IsEquivalentTo(new byte[] { 8, 9 }, CollectionOrdering.Matching);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Kitty_file_read_past_the_end_is_refused()
    {
        // Arrange
        using var dir = new TempDir();
        string ok = dir.File("ok", [7, 8, 9]);

        // Act
        byte[]? read = ImageFiles.Read(ok, 1, 3);

        // Assert
        await Assert.That(read).IsNull();
    }

    [Test]
    public async Task Every_reply_has_the_safe_shape()
    {
        // Arrange
        var (t, replies) = Term();

        // Act
        Feed(t, "\e[c\e[>c\e[>q\e[5n\e[6n\e[?1$p\e[?2026$p\e[14t\e[16t\e[18t\e[?u\e]10;?\e\\\e]11;?\a");
        Feed(t, "\e[=c\e[?6n\e[?996n\e[4$p\e]4;1;?\a\e]12;?\e\\\eP$qm\e\\\eP$q q\e\\\eP+q4D73;544E\e\\\e[?2048h");
        Feed(t, Kitty("a=q,i=7,f=24,s=1,v=1", Convert.ToBase64String(new byte[] { 1, 2, 3 })));
        Feed(t, Kitty("a=p,i=99"));
        var unclean = replies.Where(r => !TerminalReplies.IsCleanReply(r)).Select(r => Encoding.ASCII.GetString(r).Replace("\e", "\\e")).ToList();

        // Assert
        await Assert.That(replies.Count).IsGreaterThanOrEqualTo(26);
        foreach (var r in replies) await AssertSafeReply(r);
        await Assert.That(unclean).IsEmpty(); // passed through as is
    }

    [Test]
    [Arguments("\e_Gi=1;OK\e\\", true)]
    [Arguments("\e[?62;4;22c", true)]
    [Arguments("\e_Gi=1;x\x15touch y\r\e\\", false)]
    [Arguments("\e]0;\e[31m\e\\", false)]
    [Arguments("\e]11;rgb:0000/0000/0000\a", true)]  // an OSC reply ended with BEL, like its query
    [Arguments("\e]0;\a;x\a", false)]                // a BEL anywhere else
    [Arguments("\e[?62c\a", false)]                   // BEL only ends OSC replies
    public async Task Reply_sanitizer_knows_a_clean_reply(string reply, bool clean)
    {
        // Arrange
        byte[] bytes = Encoding.UTF8.GetBytes(reply);

        // Act
        bool isClean = TerminalReplies.IsCleanReply(bytes);

        // Assert
        await Assert.That(isClean).IsEqualTo(clean);
    }

    [Test]
    [Arguments("\e_Gi=1;x\x15touch y\r\e\\", "\e_Gi=1;xtouch y\e\\")]
    // A stray ESC in the middle (it would start a sequence of its own) and DEL go.
    [Arguments("\e]0;\e[31m\x7f\e\\", "\e]0;[31m\e\\")]
    public async Task Reply_sanitizer_strips_controls_but_keeps_framing(string reply, string expected)
    {
        // Arrange
        byte[] bytes = Encoding.UTF8.GetBytes(reply);
        byte[] wanted = Encoding.UTF8.GetBytes(expected);

        // Act
        byte[] sanitized = TerminalReplies.SanitizeReply(bytes);

        // Assert
        await Assert.That(sanitized).IsEquivalentTo(wanted, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Reply_sanitizer_strips_c1_and_invalid_utf8()
    {
        // Arrange
        // C1 as raw bytes and as UTF-8 (U+009B = CSI), invalid UTF-8; other text survives.
        byte[] reply = [0x1B, (byte)']', (byte)'0', (byte)';', 0x9B, 0xC2, 0x9B, 0xC3, 0xA9, 0xFF, 0x1B, (byte)'\\'];

        // Act
        byte[] sanitized = TerminalReplies.SanitizeReply(reply);

        // Assert
        await Assert.That(sanitized).IsEquivalentTo("\e]0;\u00e9\e\\"u8.ToArray(), CollectionOrdering.Matching);
    }

    // ---- 3. decoder exceptions crashed the process -------------------------------------------------------------

    private const string PocGif = "R0lGODlhAQABAIAAAAAAAP///ywAAAAA/////wACAkwBADs=";

    /// <summary>PoC poc-gif-crash.txt: a 1×1 GIF whose frame is 65535×65535 (fw*fh overflowed int).</summary>
    [Test]
    public async Task Gif_frame_size_overflow_is_an_unsupported_image()
    {
        // Arrange
        byte[] gif = Convert.FromBase64String(PocGif);

        // Act
        var decode = () => ImageDecoder.Decode(gif);

        // Assert
        await Assert.That(decode).ThrowsExactly<ImageDecodeException>();
    }

    /// <summary>The PoC's GIF sent with OSC 1337 shows as the "unsupported image" box.</summary>
    [Test]
    public async Task Gif_frame_size_overflow_shows_the_unsupported_image_box()
    {
        // Arrange
        var (t, _) = Term();

        // Act
        Feed(t, "\e]1337;File=inline=1:" + PocGif + "\a");

        // Assert
        var img = await Assert.That(t.Images.Images).HasSingleItem();
        await Assert.That(img!.Width).IsEqualTo(48); // the "unsupported image" box
    }

    /// <summary>The input of poc-sixel-crash.txt: a sixel DCS with 65537 × "!32768?".</summary>
    private static string SixelOverflowPoc()
    {
        var sb = new StringBuilder("\eP0;1;0q");
        for (int i = 0; i < 65537; i++) sb.Append("!32768?");
        return sb.Append("~\e\\").ToString();
    }

    /// <summary>PoC poc-sixel-crash.txt: 65537 × "!32768?" overflowed the sixel x position.</summary>
    [Test]
    [NotInParallel]
    public async Task Sixel_position_overflow_is_harmless()
    {
        // Arrange
        string poc = SixelOverflowPoc();

        // Act
        var cpuMs = CpuMsPerRound(t => Feed(t, poc));

        // Assert
        await Assert.That(cpuMs.Min()).IsLessThan(5000).Because(Rounds(cpuMs));
    }

    [Test]
    public async Task Sixel_position_overflow_is_a_decode_error()
    {
        // Arrange
        // The PoC's sixel data: the DCS body without the final '~'.
        byte[] data = Encoding.ASCII.GetBytes(SixelOverflowPoc()[8..^2])[..^1];

        // Act
        var decode = () => Sixel.Decode(data);

        // Assert
        await Assert.That(decode).ThrowsExactly<ImageDecodeException>();
    }

    [Test]
    public async Task Sixel_bands_past_the_limit_and_huge_repeats_are_harmless()
    {
        // Arrange
        // Bands past the limit and huge repeats on painted pixels.
        var s2 = new StringBuilder("\ePq#1;2;100;0;0");
        for (int i = 0; i < 10000; i++) s2.Append("-");
        s2.Append("!999999~$!999999~\e\\");
        var (t, _) = Term();

        // Act
        var feed = () => Feed(t, s2.ToString());

        // Assert
        await Assert.That(feed).ThrowsNothing();
    }

    /// <summary>64 MB of zeros, ~64 KB compressed, as kitty payload.</summary>
    private static readonly Lazy<string> ZlibBomb = new(() => Convert.ToBase64String(Deflate(64 << 20)));

    private static byte[] Deflate(int zeros)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var chunk = new byte[1 << 20];
            for (int i = 0; i < zeros >> 20; i++) z.Write(chunk);
        }
        return ms.ToArray();
    }

    /// <summary>PoC poc-kitty-zlib-bomb.txt: o=z data was inflated (to gigabytes) before s/v were checked.</summary>
    [Test]
    public async Task Kitty_zlib_bomb_is_refused_before_inflating()
    {
        // Arrange
        string bomb = ZlibBomb.Value;
        var (t, replies) = Term();
        long before = GC.GetAllocatedBytesForCurrentThread();

        // Act
        Feed(t, Kitty("a=T,f=32,s=32768,v=32768,o=z,i=9", bomb)); // the PoC's keys
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var texts = Texts(replies);

        // Assert
        var text = await Assert.That(texts).HasSingleItem();
        await Assert.That(text).IsEqualTo("\e_Gi=9;EFBIG:image too large\e\\");
        await Assert.That(allocated).IsLessThan(40L << 20).Because("inflated far past the limit");
    }

    [Test]
    public async Task Kitty_zlib_inflation_stops_past_the_image_size()
    {
        // Plausible keys, too much data: inflation stops one byte past s*v*4 (4 MB), never producing the 64 MB.

        // Arrange
        string bomb = ZlibBomb.Value;
        var (t, replies) = Term();
        long before = GC.GetAllocatedBytesForCurrentThread();

        // Act
        Feed(t, Kitty("a=T,f=32,s=1024,v=1024,o=z,i=10", bomb));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var texts = Texts(replies);

        // Assert
        var text = await Assert.That(texts).HasSingleItem();
        await Assert.That(text).IsEqualTo("\e_Gi=10;EFBIG:decompressed data too large\e\\");
        await Assert.That(allocated).IsLessThan(40L << 20).Because("inflated far past the limit");
    }

    [Test]
    public async Task Kitty_zlib_bomb_requests_together_allocate_less_than_the_bound()
    {
        // The PoC's keys and then plausible keys on one terminal: both together stay under the bound.

        // Arrange
        string bomb = ZlibBomb.Value;
        var (t, replies) = Term();
        long before = GC.GetAllocatedBytesForCurrentThread();

        // Act
        Feed(t, Kitty("a=T,f=32,s=32768,v=32768,o=z,i=9", bomb));
        Feed(t, Kitty("a=T,f=32,s=1024,v=1024,o=z,i=10", bomb));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var texts = Texts(replies);

        // Assert
        await Assert.That(texts).IsEquivalentTo(["\e_Gi=9;EFBIG:image too large\e\\", "\e_Gi=10;EFBIG:decompressed data too large\e\\"], CollectionOrdering.Matching);
        await Assert.That(allocated).IsLessThan(40L << 20).Because("inflated far past the limit");
    }

    [Test]
    // Unknown format / compression are refused before reading anything.
    [Arguments("a=T,f=7,o=z,i=11", "\e_Gi=11;EINVAL:unsupported format\e\\")]
    [Arguments("a=T,f=32,s=1,v=1,o=x,i=12", "\e_Gi=12;EINVAL:unsupported compression\e\\")]
    public async Task Kitty_unknown_format_or_compression_is_refused(string control, string expected)
    {
        // Arrange
        string bomb = ZlibBomb.Value;
        var (t, replies) = Term();

        // Act
        Feed(t, Kitty(control, bomb));
        var texts = Texts(replies);

        // Assert
        var text = await Assert.That(texts).HasSingleItem();
        await Assert.That(text).IsEqualTo(expected);
    }

    /// <summary>Seeded random corruption of every image format: only ImageDecodeException may come out, and Feed never throws.</summary>
    [Test]
    public async Task Decoders_survive_random_corruption()
    {
        // One test on purpose: both loops draw from one seeded sequence and feed one terminal.

        // Arrange
        var rng = new Random(20261107);
        byte[] png = PngTests.EncodeRgba(9, 7, (x, y) => (uint)(x * 25 << 24 | y * 30 << 16 | 0x80FF));
        byte[] gif = Convert.FromBase64String("R0lGODlhAgACAPAAAP8AAAAA/yH5BAAAAAAALAAAAAACAAIAAAIChFEAOw=="); // 2×2
        byte[][] samples = [png, gif, Encoding.ASCII.GetBytes("#0;2;100;0;0#1;2;0;100;0\"1;1;8;12#0!8~-#1!8~$#0!4N")];
        var (t, _) = Term(40, 10);

        // Act
        var corruptEverything = () =>
        {
            for (int round = 0; round < 3000; round++)
            {
                byte[] src = samples[round % samples.Length];
                byte[] data = (byte[])src.Clone();
                int edits = 1 + rng.Next(8);
                for (int e = 0; e < edits; e++)
                {
                    int at = rng.Next(data.Length);
                    switch (rng.Next(4))
                    {
                        case 0: data[at] = (byte)rng.Next(256); break;
                        case 1: data[at] = 0xFF; break;
                        case 2: data[at] = 0; break;
                        default: data = data[..at]; break;
                    }
                    if (data.Length == 0) break;
                }
                if (round % samples.Length == 2)
                {
                    try { Sixel.Decode(data); } catch (ImageDecodeException) { }
                    t.Feed([.. "\eP0;1;0q"u8, .. data.Where(b => b != 0x1B && b != 0x9C), .. "\e\\"u8]);
                }
                else
                {
                    try { ImageDecoder.Decode(data); } catch (ImageDecodeException) { }
                    Feed(t, "\e]1337;File=inline=1:" + Convert.ToBase64String(data) + "\a");
                    Feed(t, Kitty($"a=T,f=100,i={1 + round % 50},q=2", Convert.ToBase64String(data)));
                }
            }
            // Kitty raw and zlib with corrupt keys and data.
            for (int round = 0; round < 500; round++)
            {
                var data = new byte[rng.Next(64)];
                rng.NextBytes(data);
                string keys = $"a=T,f={(rng.Next(2) == 0 ? 24 : 32)},s={rng.Next(-2, 70000)},v={rng.Next(-2, 70000)},o={(rng.Next(2) == 0 ? "z" : "")},i=3,x={rng.Next(-5, 5)},c={rng.Next(-3, 600)},r={rng.Next(-3, 600)}";
                Feed(t, Kitty(keys, Convert.ToBase64String(data)));
            }
        };

        // Assert
        await Assert.That(corruptEverything).ThrowsNothing();
    }

    // ---- 4. session restore typed the saved argv into the shell --------------------------------------------
    // Restored programs are now started directly (RestoreLaunch, see also SessionTests); these keep the PoC inputs.

    private static readonly IReadOnlyList<string> RestoreAllowed = ["nvim", "less"];

    /// <summary>nvim on a file whose name and arguments are full of shell syntax, but no control characters.</summary>
    private static SessionProgram NvimOnOddNames() =>
        new() { Name = "nvim", Argv = ["nvim", "my notes.md", "it's", "$(touch x)`y`\"z\\"] };

    /// <summary>PoC poc/restore: nvim started on a file named "\x15touch MARKER\r" was re-run by typing its quoted argv.</summary>
    [Test]
    [Arguments("\x15touch PWNED\r")]
    [Arguments("a\nb")]
    [Arguments("tab\there")]
    [Arguments("del\x7f")]
    [Arguments("c1\u009b31m")]
    [Arguments("\u2028")]
    [Arguments("para\u2029")]
    public async Task Restore_refuses_argv_with_control_characters(string bad)
    {
        // Arrange
        var program = new SessionProgram { Name = "nvim", Argv = ["nvim", bad] };

        // Act
        bool safe = RestoreLaunch.IsSafeArgument(bad);
        var argv = RestoreLaunch.ProgramArgv(program, RestoreAllowed);
        var underZsh = RestoreLaunch.CommandFor(program, RestoreAllowed, ["/bin/zsh"]);
        var underFish = RestoreLaunch.CommandFor(program, RestoreAllowed, ["/usr/bin/fish"]);
        var asProgramName = RestoreLaunch.CommandFor(new SessionProgram { Name = bad }, [bad], ["/bin/zsh"]);

        // Assert
        await Assert.That(safe).IsFalse();
        await Assert.That(argv).IsNull();
        await Assert.That(underZsh).IsNull();
        await Assert.That(underFish).IsNull();
        await Assert.That(asProgramName).IsNull();
    }

    [Test]
    public async Task Restore_keeps_shell_syntax_in_arguments_as_it_is()
    {
        // Act
        var argv = RestoreLaunch.ProgramArgv(NvimOnOddNames(), RestoreAllowed);

        // Assert
        await Assert.That(argv!).IsEquivalentTo(["nvim", "my notes.md", "it's", "$(touch x)`y`\"z\\"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Restore_refuses_programs_that_are_not_allowed()
    {
        // Act
        var command = RestoreLaunch.CommandFor(new SessionProgram { Name = "cargo" }, RestoreAllowed, ["/bin/bash"]);

        // Assert
        await Assert.That(command).IsNull();
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task Restore_runs_the_program_through_the_wrapper_then_the_shell()
    {
        // Act
        var command = RestoreLaunch.CommandFor(NvimOnOddNames(), RestoreAllowed, ["/bin/zsh"]);

        // Assert
        await Assert.That(command!).IsEquivalentTo(
            ["/bin/sh", "-c", RestoreLaunch.Script, RestoreLaunch.WrapperName, "4", "nvim", "my notes.md", "it's", "$(touch x)`y`\"z\\", "/bin/zsh"],
            CollectionOrdering.Matching);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task Restore_works_the_same_under_any_shell()
    {
        // Act
        // Any shell works the same way (nothing is quoted for it): pwsh too.
        var command = RestoreLaunch.CommandFor(NvimOnOddNames(), RestoreAllowed, ["pwsh"]);

        // Assert
        await Assert.That(command).IsNotNull();
        await Assert.That(command![^1]).IsEqualTo("pwsh");
    }

    /// <summary>The launch argv runs the program with its arguments untouched (nothing is parsed), then the shell.</summary>
    [Test]
    [ExcludeOn(OS.Windows)]
    [NotInParallel(Isolation.Processes)]
    public async Task Restore_launch_argv_passes_arguments_verbatim()
    {
        // Arrange
        Skip.Unless(File.Exists("/bin/sh"), "needs /bin/sh");
        using var dir = new TempDir();
        string marker = Path.Combine(dir.Path, "PWNED");
        string[] args = ["printf", "%s|", "a b", $"$(touch {marker})", $"`touch {marker}`", "x'\"\\", $"; touch {marker}"];

        // Act
        // `true` stands in for the shell it execs afterwards.
        var argv = RestoreLaunch.CommandFor(new SessionProgram { Name = "printf", Argv = [.. args] }, ["printf"], ["true"])!;
        var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir.Path };
        foreach (var a in argv[1..]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        bool touched = File.Exists(marker);

        // Assert
        await Assert.That(output).IsEqualTo(string.Join("", args[2..].Select(a => a + "|")));
        await Assert.That(touched).IsFalse();
    }

    private static string[] FishTestArgs() => ["printf", "%s|", @"dir\", "it's", @"a\'b", "%self"];

    private static string[] FishShell() =>
        File.Exists("/usr/bin/fish") ? ["/usr/bin/fish", "--no-config", "-c", "printf fish"] : ["printf", "fish"];

    /// <summary>
    /// Finding 16: POSIX '\'' quoting broke in fish on a trailing backslash. Nothing is quoted any more — with fish as
    /// the pane's shell, backslashes and quotes reach the program as they were.
    /// </summary>
    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task Restore_under_fish_hands_the_arguments_over_unquoted()
    {
        // Arrange
        string[] args = FishTestArgs();
        string[] fish = FishShell();
        string[] expected = [.. args, .. fish];

        // Act
        var argv = RestoreLaunch.CommandFor(new SessionProgram { Name = "printf", Argv = [.. args] }, ["printf"], fish)!;
        string[] handedOver = argv[5..];

        // Assert
        await Assert.That(handedOver).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>Finding 16, run: the program, then the shell, print the arguments as they were.</summary>
    [Test]
    [ExcludeOn(OS.Windows)]
    [NotInParallel(Isolation.Processes)]
    public async Task Restore_under_fish_keeps_backslashes_and_quotes()
    {
        // Arrange
        Skip.Unless(File.Exists("/bin/sh"), "needs /bin/sh");
        var argv = RestoreLaunch.CommandFor(new SessionProgram { Name = "printf", Argv = [.. FishTestArgs()] }, ["printf"], FishShell())!;
        var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true };
        foreach (var a in argv[1..]) psi.ArgumentList.Add(a);

        // Act
        using var proc = Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        // Assert
        await Assert.That(output).IsEqualTo(@"dir\|it's|a\'b|%self|fish");
    }

    // ---- 5. CPU DoS under the terminal lock ------------------------------------------------------------------

    /// <summary>8 tiny images, then 10 000 placements of them.</summary>
    private static string ManyPlacements()
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= 8; i++) sb.Append(Kitty($"a=t,f=32,s=1,v=1,i={i},q=2", "AAAA/w=="));
        for (int n = 0; n < 10000; n++) sb.Append(Kitty($"a=p,i={1 + n % 8},p={n + 1},C=1,q=2"));
        return sb.ToString();
    }

    [Test]
    [NotInParallel]
    public async Task Many_kitty_placements_stay_fast()
    {
        // Arrange
        string input = ManyPlacements();

        // Act
        var cpuMs = CpuMsPerRound(t => Feed(t, input));

        // Assert
        await Assert.That(cpuMs.Min()).IsLessThan(3000).Because(Rounds(cpuMs));
    }

    [Test]
    public async Task Many_kitty_placements_are_capped()
    {
        // Arrange
        var (t, _) = Term(80, 24);

        // Act
        Feed(t, ManyPlacements());
        int placements = t.Images.Placements(false).Count;

        // Assert
        await Assert.That(placements).IsLessThanOrEqualTo(ImageStore.MaxPlacements);
    }

    [Test]
    public async Task Many_kitty_placements_are_capped_per_image()
    {
        // Arrange
        var (t, _) = Term(80, 24);

        // Act
        Feed(t, ManyPlacements());
        int[] perImage = [.. t.Images.Placements(false).GroupBy(p => p.Image).Select(g => g.Count())];

        // Assert
        await Assert.That(perImage).All(n => n <= ImageStore.MaxPlacementsPerImage);
    }

    /// <summary>Two images, a root placement, then fans and chains of relative placements; the root is deleted at the end.</summary>
    private static string ManyRelativePlacements()
    {
        var sb = new StringBuilder(Kitty("a=t,f=32,s=1,v=1,i=1,q=2", "AAAA/w=="));
        sb.Append(Kitty("a=t,f=32,s=1,v=1,i=2,q=2", "AAAA/w=="));
        sb.Append(Kitty("a=p,i=1,p=1,C=1,q=2"));
        // Fans and chains of relative placements, re-placing the root now and then (which moves / drops children).
        for (int n = 0; n < 6000; n++)
        {
            int parent = n % 7 == 0 ? 1 : 2 + (n - 1) % 6;
            uint parentImage = parent == 1 ? 1u : 2u;
            sb.Append(Kitty($"a=p,i=2,p={2 + n % 6},P={parentImage},Q={parent},H=1,V=1,C=1,q=2"));
            sb.Append(Kitty($"a=p,i=2,P=1,Q=1,H=1,C=1,q=2"));
            if (n % 500 == 0) sb.Append(Kitty("a=p,i=1,p=1,C=1,q=2"));
        }
        return sb.ToString();
    }

    private static readonly string DeleteRootPlacement = Kitty("a=d,d=i,i=1,p=1,q=2");

    [Test]
    [NotInParallel]
    public async Task Many_relative_kitty_placements_stay_fast()
    {
        // Arrange
        string input = ManyRelativePlacements();

        // Act
        var cpuMs = CpuMsPerRound(t =>
        {
            Feed(t, input);
            Feed(t, DeleteRootPlacement);
        });

        // Assert
        await Assert.That(cpuMs.Min()).IsLessThan(3000).Because(Rounds(cpuMs));
    }

    [Test]
    public async Task Many_relative_kitty_placements_leave_no_orphans()
    {
        // Arrange
        var (t, _) = Term(80, 24);

        // Act
        Feed(t, ManyRelativePlacements());
        Feed(t, DeleteRootPlacement);
        var orphans = t.Images.Placements(false).Where(p => p.IsRelative && t.Images.ParentOf(p, false) is null).ToList();

        // Assert
        await Assert.That(orphans).IsEmpty();
    }

    private static string ManyTinyImages()
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= ImageStore.MaxImages + 500; i++) sb.Append(Kitty($"a=t,f=32,s=1,v=1,i={i},q=2", "AAAA/w=="));
        return sb.ToString();
    }

    [Test]
    [NotInParallel]
    public async Task Many_tiny_images_stay_fast()
    {
        // Arrange
        string input = ManyTinyImages();

        // Act
        var cpuMs = CpuMsPerRound(t => Feed(t, input));

        // Assert
        await Assert.That(cpuMs.Min()).IsLessThan(3000).Because(Rounds(cpuMs));
    }

    [Test]
    public async Task Many_tiny_images_are_capped()
    {
        // Arrange
        var (t, _) = Term();

        // Act
        Feed(t, ManyTinyImages());
        int images = t.Images.Images.Count;

        // Assert
        await Assert.That(images).IsEqualTo(ImageStore.MaxImages);
    }

    [Test]
    public async Task Many_tiny_images_evict_the_least_recently_used()
    {
        // Arrange
        var (t, _) = Term();

        // Act
        Feed(t, ManyTinyImages());
        var first = t.Images.ById(1);
        var last = t.Images.ById(ImageStore.MaxImages + 500);

        // Assert
        await Assert.That(first).IsNull(); // the least recently used went
        await Assert.That(last).IsNotNull();
    }

    /// <summary>'e', 200 000 combining acute accents, then 'x'.</summary>
    private static string EndlessCombiningMarks()
    {
        var sb = new StringBuilder("e");
        for (int i = 0; i < 200_000; i++) sb.Append('\u0301');
        return sb.Append('x').ToString();
    }

    /// <summary>A base followed by 200 000 combining marks: each append used to copy the whole cluster (O(n²)).</summary>
    [Test]
    [NotInParallel]
    public async Task Endless_combining_marks_stay_fast()
    {
        // Arrange
        string input = EndlessCombiningMarks();

        // Act
        var cpuMs = CpuMsPerRound(t => Feed(t, input));

        // Assert
        await Assert.That(cpuMs.Min()).IsLessThan(2000).Because(Rounds(cpuMs));
    }

    [Test]
    public async Task Endless_combining_marks_are_capped()
    {
        // Arrange
        var (t, _) = Term();

        // Act
        Feed(t, EndlessCombiningMarks());
        var row = t.GetScreenRow(0);
        bool found = row.TryGetCluster(0, out var cluster);
        int clusterLength = cluster.Length;
        char next = (char)row.Cells[1].Rune;

        // Assert
        await Assert.That(found).IsTrue();
        await Assert.That(clusterLength).IsEqualTo(Row.MaxClusterCodePoints); // 'e' + 31 marks (all BMP)
        await Assert.That(next).IsEqualTo('x');
    }

    [Test]
    public async Task Real_clusters_are_not_capped()
    {
        // Arrange
        // Real clusters are untouched: a family ZWJ sequence (11 code points with surrogates).
        const string Family = "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466";
        var (t, _) = Term();

        // Act
        Feed(t, Family);
        bool found = t.GetScreenRow(0).TryGetCluster(0, out var cluster);
        string text = cluster.ToString();

        // Assert
        await Assert.That(found).IsTrue();
        await Assert.That(text).IsEqualTo(Family);
    }

    // ---- 10. OSC 1337 user variables -------------------------------------------------------------------------

    private static string SetUserVar(string name, string value) => $"\e]1337;SetUserVar={name}={B64(value)}\a";

    [Test]
    public async Task User_vars_are_capped()
    {
        // Arrange
        var (t, _) = Term();

        // Act
        for (int i = 0; i < 100; i++) Feed(t, SetUserVar($"V{i}", "x"));
        int count = t.UserVars.Count;

        // Assert
        await Assert.That(count).IsEqualTo(Terminal.MaxUserVars);
    }

    [Test]
    public async Task Full_user_vars_still_take_updates_to_existing_names()
    {
        // Arrange
        var (t, _) = Term();
        for (int i = 0; i < 100; i++) Feed(t, SetUserVar($"V{i}", "x"));

        // Act
        Feed(t, SetUserVar(new string('n', 65), "x"));
        Feed(t, SetUserVar("V0", new string('v', 4097)));
        Feed(t, SetUserVar("V0", new string('v', 4096))); // existing names may still be updated
        int v0Length = t.UserVars["V0"].Length;
        int count = t.UserVars.Count;

        // Assert
        await Assert.That(v0Length).IsEqualTo(4096);
        await Assert.That(count).IsEqualTo(Terminal.MaxUserVars);
    }

    [Test]
    public async Task User_var_names_longer_than_64_are_ignored()
    {
        // Arrange
        var (t, _) = Term();
        string name = new('n', 65);

        // Act
        Feed(t, SetUserVar(name, "x"));

        // Assert
        await Assert.That(t.UserVars.Keys).DoesNotContain(name);
    }

    [Test]
    public async Task User_var_values_longer_than_4096_are_ignored()
    {
        // Arrange
        var (t, _) = Term();

        // Act
        Feed(t, SetUserVar("V0", new string('v', 4097)));

        // Assert
        await Assert.That(t.UserVars.Keys).DoesNotContain("V0");
    }

    [Test]
    public async Task User_var_names_with_controls_are_ignored()
    {
        // Arrange
        var (t, _) = Term();

        // Act
        Feed(t, SetUserVar("bad\x01name", "x"));

        // Assert
        await Assert.That(t.UserVars.Keys).DoesNotContain("bad\x01name");
    }

    [Test]
    public async Task User_vars_are_cleared_at_the_next_prompt()
    {
        // A program spoofs IS_NVIM, exits; the shell's next prompt clears it.

        // Arrange
        var (t, _) = Term();
        Feed(t, "\e]133;A\a$ \e]133;B\a" + SetUserVar("IS_NVIM", "true"));
        t.UserVars.TryGetValue("IS_NVIM", out string? spoofed);

        // Act
        Feed(t, "\e]133;D;0\a\e]133;A\a$ ");

        // Assert
        await Assert.That(spoofed).IsEqualTo("true");
        await Assert.That(t.UserVars).IsEmpty();
    }

    // ---- 11. OSC 7 from another host ------------------------------------------------------------------------

    private static bool IsLocal(string h) => h is "" or "localhost" or "mybox";

    [Test]
    [Arguments("file:///home/me/src", "/home/me/src")]
    [Arguments("file://localhost/home/me/a%20b", "/home/me/a b")]
    [Arguments("file://mybox/srv", "/srv")]
    [Arguments("file://evil.example/etc", null)]
    [Arguments("http://mybox/srv", null)]
    [Arguments("file://mybox", null)]
    [Arguments("file:///a%00b", null)]
    public async Task Osc7_from_another_host_is_not_a_local_directory(string uri, string? expected)
    {
        // Act
        string? dir = PaneCwd.FromOsc7(uri, IsLocal);

        // Assert
        await Assert.That(dir).IsEqualTo(expected);
    }

    [Test]
    [Arguments("file:///C:/Windows/System32", "C:/Windows/System32")]
    [Arguments("file://localhost/C:/Program%20Files", "C:/Program Files")]
    [Arguments("file:///c:/src/erikterm", "c:/src/erikterm")]
    [Arguments("file:///home/me/src", "/home/me/src")] // no drive letter: untouched
    [Arguments("file:///C/notadrive", "/C/notadrive")] // no colon: untouched
    public async Task Osc7_on_windows_drops_the_slash_before_a_drive_letter(string uri, string expected)
    {
        // A file URI's path always starts with a slash, so a Windows shell reports "/C:/src"; left alone, no
        // Windows API accepts it and the directory is dropped as non-existent. Forced on, so it runs everywhere.

        // Act
        string? dir = PaneCwd.FromOsc7(uri, IsLocal, windows: true);

        // Assert
        await Assert.That(dir).IsEqualTo(expected);
    }

    [Test]
    [Arguments("file:///C:/Windows", "/C:/Windows")]
    [Arguments("file:///home/me/src", "/home/me/src")]
    public async Task Osc7_off_windows_keeps_the_leading_slash(string uri, string expected)
    {
        // "/C:/x" is a real (if odd) relative-to-root path on Unix: nothing may be taken off it there.

        // Act
        string? dir = PaneCwd.FromOsc7(uri, IsLocal, windows: false);

        // Assert
        await Assert.That(dir).IsEqualTo(expected);
    }

    public static IEnumerable<(string Host, bool Local)> HostNames() =>
    [
        ("", true),
        ("LOCALHOST", true),
        (System.Net.Dns.GetHostName(), true),
        (Environment.MachineName, true),
        ("definitely-not-" + Environment.MachineName, false),
    ];

    [Test]
    [MethodDataSource(nameof(HostNames))]
    public async Task Only_this_machine_is_a_local_host(string host, bool local)
    {
        // Act
        bool isLocal = PaneCwd.IsLocalHost(host);

        // Assert
        await Assert.That(isLocal).IsEqualTo(local);
    }

    // ---- 13. IPC directory and socket trust ------------------------------------------------------------------

    [Test]
    [RunOn(OS.Linux)]
    public async Task Ipc_directory_is_created_private()
    {
        // Fresh: created 0700 and accepted.

        // Arrange
        using var root = new TempDir();
        string fresh = Path.Combine(root.Path, "fresh");

        // Act
        IpcPaths.EnsureDirectory(fresh);
        UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(fresh);
        string? problem = IpcPaths.DirectoryProblem(fresh);

        // Assert
        await Assert.That(mode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Assert.That(problem).IsNull();
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Ipc_directory_planted_as_a_symlink_is_refused()
    {
        // Planted as a symlink to a directory the attacker controls: refused, nothing created through it.

        // Arrange
        using var root = new TempDir();
        string target = Path.Combine(root.Path, "attacker");
        Directory.CreateDirectory(target);
        string link = Path.Combine(root.Path, "erikterm-me");
        File.CreateSymbolicLink(link, target);

        // Act
        var ensure = () => IpcPaths.EnsureDirectory(link);
        string? problem = IpcPaths.DirectoryProblem(link);

        // Assert
        await Assert.That(ensure).ThrowsExactly<IOException>();
        await Assert.That(problem).Contains("symbolic link");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Ipc_directory_open_to_others_is_refused()
    {
        // World-writable (someone could have planted sockets in it): refused, not "fixed".

        // Arrange
        using var root = new TempDir();
        string open = Path.Combine(root.Path, "open");
        Directory.CreateDirectory(open);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(open, (UnixFileMode)0x1FF);

        // Act
        var ensure = () => IpcPaths.EnsureDirectory(open);
        string? discovered = IpcPaths.Discover(open);

        // Assert
        await Assert.That(ensure).ThrowsExactly<IOException>();
        await Assert.That(discovered).IsNull();
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Ipc_directory_that_is_a_plain_file_is_refused()
    {
        // A plain file instead of a directory.

        // Arrange
        using var root = new TempDir();
        string file = root.File("file", []);

        // Act
        var ensure = () => IpcPaths.EnsureDirectory(file);

        // Assert
        await Assert.That(ensure).ThrowsExactly<IOException>();
    }

    /// <summary>A Unix socket listening at <paramref name="path"/>, so it counts as "alive".</summary>
    private static System.Net.Sockets.Socket Listen(string path)
    {
        var listener = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        return listener;
    }

    [Test]
    [RunOn(OS.Linux)]
    [NotInParallel(Isolation.Sockets)]
    public async Task Ipc_marker_must_name_our_socket_in_our_directory()
    {
        // Arrange
        using var root = new TempDir();
        string dir = Path.Combine(root.Path, "erikterm");
        IpcPaths.EnsureDirectory(dir);
        // An attacker's socket elsewhere (here: listening, so it would be "alive").
        string elsewhere = Path.Combine(root.Path, "erikterm-1.sock");
        using var listener = Listen(elsewhere);
        IpcPaths.WriteMarker(dir, elsewhere);

        // Act
        bool own = IpcPaths.IsOwnSocket(dir, elsewhere);
        string? discovered = IpcPaths.Discover(dir);

        // Assert
        await Assert.That(own).IsFalse();
        await Assert.That(discovered).IsNull();
    }

    [Test]
    [RunOn(OS.Linux)]
    [NotInParallel(Isolation.Sockets)]
    public async Task Ipc_marker_that_leaves_our_directory_is_ignored()
    {
        // Arrange
        using var root = new TempDir();
        string dir = Path.Combine(root.Path, "erikterm");
        IpcPaths.EnsureDirectory(dir);
        // The attacker's socket next to our directory, listening, so it would be "alive".
        using var listener = Listen(Path.Combine(root.Path, "erikterm-1.sock"));
        IpcPaths.WriteMarker(dir, Path.Combine(dir, "../erikterm-1.sock"));

        // Act
        string? discovered = IpcPaths.Discover(dir);

        // Assert
        await Assert.That(discovered).IsNull();
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Ipc_regular_file_with_a_socket_name_is_not_our_socket()
    {
        // A regular file with a socket's name is not a socket.

        // Arrange
        using var root = new TempDir();
        string dir = Path.Combine(root.Path, "erikterm");
        IpcPaths.EnsureDirectory(dir);
        string fake = Path.Combine(dir, "erikterm-2.sock");
        File.WriteAllText(fake, "");

        // Act
        bool own = IpcPaths.IsOwnSocket(dir, fake);

        // Assert
        await Assert.That(own).IsFalse();
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Ipc_socket_with_another_name_is_not_ours()
    {
        // Arrange
        using var root = new TempDir();
        string dir = Path.Combine(root.Path, "erikterm");
        IpcPaths.EnsureDirectory(dir);

        // Act
        bool own = IpcPaths.IsOwnSocket(dir, Path.Combine(dir, "other.sock"));

        // Assert
        await Assert.That(own).IsFalse();
    }

    /// <summary>A directory whose socket paths can't fit in sun_path; unique, and never created.</summary>
    private static string TooLongDirectory() => Path.Combine(Path.GetTempPath(), new string('d', 88) + Guid.NewGuid().ToString("N"));

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task Ipc_socket_path_too_long_is_an_io_error()
    {
        // Arrange
        string path = IpcPaths.SocketFor(TooLongDirectory(), 12345);

        // Act
        var check = () => IpcPaths.CheckSocketPath(path);

        // Assert
        await Assert.That(check).ThrowsExactly<IOException>().WithMessageContaining("at most");
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task Ipc_server_on_a_too_long_path_fails_with_an_io_error_and_creates_nothing()
    {
        // Arrange
        string dir = TooLongDirectory();
        var server = new IpcServer(IpcPaths.SocketFor(dir, 12345), _ => Task.FromResult(IpcResponse.Success()));

        // Act
        var start = () => server.Start();

        // Assert
        await Assert.That(start).ThrowsExactly<IOException>(); // caught by StartIpc, unlike the ArgumentOutOfRangeException from Bind
        await Assert.That(Directory.Exists(dir)).IsFalse();
    }

    // ---- 15. TOML nesting -----------------------------------------------------------------------------------

    /// <summary>harness-config PoC: a = [[[[...]]]] 100 000 deep overflowed the stack.</summary>
    [Test]
    [Arguments(65)]
    [Arguments(100_000)]
    public async Task Toml_array_nesting_is_capped(int depth)
    {
        // Arrange
        string toml = "a = " + new string('[', depth) + new string(']', depth) + "\n";

        // Act
        var parse = () => Toml.Parse(toml);

        // Assert
        var ex = await Assert.That(parse).ThrowsExactly<TomlException>();
        await Assert.That(ex!.Line).IsEqualTo(1);
    }

    [Test]
    [Arguments(65)]
    [Arguments(100_000)]
    public async Task Toml_inline_table_nesting_is_capped(int depth)
    {
        // Arrange
        string toml = "x = 1\n\nb = " + string.Concat(Enumerable.Repeat("{ a = ", depth)) + "1" + new string('}', depth);

        // Act
        var parse = () => Toml.Parse(toml);

        // Assert
        await Assert.That(parse).ThrowsExactly<TomlException>();
    }

    [Test]
    public async Task Toml_nesting_error_names_its_line()
    {
        // Arrange
        string toml = "x = 1\n\nb = " + string.Concat(Enumerable.Repeat("{ a = ", 70)) + "1" + new string('}', 70);

        // Act
        var parse = () => Toml.Parse(toml);

        // Assert
        var ex = await Assert.That(parse).ThrowsExactly<TomlException>();
        await Assert.That(ex!.Line).IsEqualTo(3);
    }

    [Test]
    public async Task Toml_nesting_up_to_the_cap_parses()
    {
        // Arrange
        string toml = "a = " + new string('[', 64) + new string(']', 64) + "\n";

        // Act
        var parse = () => Toml.Parse(toml);

        // Assert
        await Assert.That(parse).ThrowsNothing();
    }

    [Test]
    public async Task Toml_dotted_key_depth_is_capped()
    {
        // Arrange
        string toml = string.Join('.', Enumerable.Repeat("k", 100)) + " = 1";

        // Act
        var parse = () => Toml.Parse(toml);

        // Assert
        await Assert.That(parse).ThrowsExactly<TomlException>();
    }

    // ---- 16. Windows command lines for batch files ----------------------------------------------------------

    [Test]
    [Arguments(@"C:\x\run.BAT", true)]
    [Arguments(@"C:\x\run.cmd. ", true)]
    [Arguments(@"C:\x\run.exe", false)]
    public async Task Batch_files_are_recognised(string program, bool batchFile)
    {
        // Act
        bool isBatchFile = ErikTerm.Pty.WindowsCommandLine.IsBatchFile(program);

        // Assert
        await Assert.That(isBatchFile).IsEqualTo(batchFile);
    }

    [Test]
    public async Task Batch_file_arguments_are_quoted()
    {
        // Act
        string commandLine = ErikTerm.Pty.WindowsCommandLine.Build(["run.bat", "a", "b c", "x&calc", "a|b", "d\\", "d e\\"], batchFile: true);

        // Assert
        await Assert.That(commandLine).IsEqualTo("\"run.bat\" a \"b c\" \"x&calc\" \"a|b\" d\\ \"d e\\\\\"");
    }

    [Test]
    [Arguments("\"&calc")]
    [Arguments("%PATH%")]
    [Arguments("a\r\nb")]
    [Arguments("\0")]
    public async Task Batch_file_arguments_that_cannot_be_quoted_are_refused(string bad)
    {
        // Act
        var build = () => ErikTerm.Pty.WindowsCommandLine.Build(["run.bat", bad], batchFile: true);

        // Assert
        await Assert.That(build).ThrowsExactly<ArgumentException>();
    }

    private const string Cmd = @"C:\Windows\system32\cmd.exe";

    [Test]
    public async Task Batch_files_start_through_cmd_with_their_quoting_intact()
    {
        // Arrange
        string[] argv = [@"C:\x\run.bat", "a", "b c", "x&calc"];

        // Act
        string commandLine = ErikTerm.Pty.WindowsCommandLine.BuildCmdLaunch(Cmd, argv, waitForGuiProgram: false);

        // Assert: cmd /s drops the outer quotes only, and what it then runs has the metacharacter inside quotes.
        await Assert.That(commandLine).IsEqualTo("\"" + Cmd + "\" /d /v:off /s /c \"\"C:\\x\\run.bat\" a \"b c\" \"x&calc\"\"");
        await Assert.That(OutsideQuotes(CmdRuns(commandLine))).DoesNotContain("&");
    }

    [Test]
    public async Task Batch_files_left_to_CreateProcess_would_lose_their_quoting()
    {
        // Arrange: what CreateProcess runs for a batch file it is handed directly: cmd /c and the command line as it is.
        string implicitLine = Cmd + " /c " + ErikTerm.Pty.WindowsCommandLine.Build([@"C:\x\run.bat", "a", "x&calc"], batchFile: true);

        // Act
        string runs = CmdRuns(implicitLine);

        // Assert: cmd dropped the first and the last quote, which leaves the & outside quotes ("BatBadBut").
        await Assert.That(OutsideQuotes(runs)).Contains("&");
    }

    [Test]
    public async Task Erikterm_itself_starts_through_cmd_and_waits()
    {
        // Act
        string commandLine = ErikTerm.Pty.WindowsCommandLine.BuildCmdLaunch(Cmd, [@"C:\Program Files\erikterm\erikterm.exe", "settings"], waitForGuiProgram: true);

        // Assert
        await Assert.That(CmdRuns(commandLine)).IsEqualTo("start \"\" /b /wait \"C:\\Program Files\\erikterm\\erikterm.exe\" settings");
    }

    /// <summary>
    /// What cmd.exe runs for a command line with /c (cmd /? on quotes): with /s, or unless the rest has exactly two quotes
    /// around something with a space, it drops a leading quote and the last quote.
    /// </summary>
    private static string CmdRuns(string commandLine)
    {
        int c = commandLine.IndexOf(" /c ", StringComparison.Ordinal);
        string rest = commandLine[(c + 4)..];
        bool slashS = commandLine[..c].Contains(" /s", StringComparison.Ordinal);
        bool keep = !slashS && rest.Count(ch => ch == '"') == 2 && rest.TrimEnd().EndsWith('"') && rest.Trim('"').Contains(' ');
        if (keep || !rest.StartsWith('"')) return rest;
        int last = rest.LastIndexOf('"');
        return rest[1..last] + rest[(last + 1)..];
    }

    /// <summary>The characters of a cmd command that are outside quotes (where &amp;, | and the like take effect).</summary>
    private static string OutsideQuotes(string command)
    {
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char ch in command)
        {
            if (ch == '"') quoted = !quoted;
            else if (!quoted) sb.Append(ch);
        }
        return sb.ToString();
    }

    [Test]
    public async Task Non_batch_file_arguments_follow_the_crt_rules()
    {
        // Act
        string commandLine = ErikTerm.Pty.WindowsCommandLine.Build(["prog", "\"&calc"]); // not a batch file: CRT rules

        // Assert
        await Assert.That(commandLine).IsEqualTo("prog \"\\\"&calc\"");
    }

    [Test]
    public async Task Windows_libraries_load_only_from_the_app_directory()
    {
        // Act
        var paths = ErikTerm.App.Native.NativeLibraries.WindowsPaths(ErikTerm.App.Native.NativeLibraries.HarfBuzz, @"C:\Program Files\erikterm");

        // Assert
        await Assert.That(paths).IsNotEmpty();
        foreach (string p in paths) await Assert.That(p).StartsWith(@"C:\Program Files\erikterm");
    }

    [Test]
    public async Task Windows_library_paths_are_empty_for_libraries_we_do_not_ship()
    {
        // Act
        var paths = ErikTerm.App.Native.NativeLibraries.WindowsPaths("libc", @"C:\x");

        // Assert
        await Assert.That(paths).IsEmpty();
    }
}

public class PasteSanitizerTests
{
    private const string Start = PasteSanitizer.BracketStart, End = PasteSanitizer.BracketEnd;

    /// <summary>The OSC 52 PoC: the clipboard text a program set ("echo hi ESC[20 ESC[201~ 1~ CR touch PWNED2 CR").</summary>
    private static readonly string PocClipboard = Encoding.UTF8.GetString(Convert.FromBase64String("ZWNobyBoaRtbMjAbWzIwMX4xfg10b3VjaCBQV05FRDIN"));

    /// <summary>No ESC and no control except TAB / CR between the markers, and exactly one end marker, at the end.</summary>
    private static async Task AssertSafeBracketed(string sent)
    {
        await Assert.That(sent).StartsWith(Start);
        await Assert.That(sent).EndsWith(End);
        string body = sent[Start.Length..^End.Length];
        var leftover = body.Where(c => PasteSanitizer.IsControl(c) && c is not '\t' and not '\r').Select(c => $"U+{(int)c:X4}").ToList();
        await Assert.That(leftover).IsEmpty().Because("controls left in the paste");
        await Assert.That(sent.IndexOf(End, StringComparison.Ordinal)).IsEqualTo(sent.Length - End.Length);
    }

    [Test]
    public async Task NestedEndMarkerFromThePocCannotEndThePaste()
    {
        // Act
        var r = PasteSanitizer.Prepare(PocClipboard, bracketed: true);

        // Assert
        await AssertSafeBracketed(r.Text);
        await Assert.That(r.Text).IsEqualTo(Start + "echo hi1~\rtouch PWNED2\r" + End);
        await Assert.That(r.Removed).IsGreaterThan(0);
    }

    [Test]
    public async Task ThePocDefeatsASinglePassReplace()
    {
        // Act
        // What the old single-pass Replace sent: the marker reassembles.
        string oldPaste = Start + PocClipboard.Replace(End, "") + End;

        // Assert
        await Assert.That(oldPaste).Contains(End + "\rtouch");
    }

    [Test]
    [Arguments("a\e[201~b")]
    [Arguments("a\e[20\e[201~1~b")]           // split marker, reassembled by a one-pass replace
    [Arguments("a\e\e[201~[201~b")]
    [Arguments("a\e[2\e[201~01~b")]
    [Arguments("a\u009b201~b")]                  // 8-bit CSI
    [Arguments("a\e]52;c;eA==\ab")]            // OSC
    [Arguments("a\eP+q\e\\b")]                 // DCS
    [Arguments("a\u0015\u0003\u0004\u001a\u007f\u0008b")]  // ^U ^C ^D ^Z DEL BS
    [Arguments("a\u0085\u0090\u009c\u009d\u009fb")]       // C1
    [Arguments("a\0b")]
    [Arguments("\e")]
    [Arguments("a\e[")]
    [Arguments("a\e]unterminated")]
    public async Task BracketedPasteKeepsNoControls(string text)
    {
        // Act
        var r = PasteSanitizer.Prepare(text, bracketed: true);

        // Assert
        await Assert.That(r.Removed).IsGreaterThan(0);
        if (r.Text.Length > 0) await AssertSafeBracketed(r.Text); // some inputs leave nothing to send
    }

    [Test]
    public async Task KeepsTabsAndNormalisesLineEndings()
    {
        // Act
        var r = PasteSanitizer.Prepare("a\tb\r\nc\nd\r", bracketed: true);

        // Assert
        await Assert.That(r.Text).IsEqualTo(Start + "a\tb\rc\rd\r" + End);
        await Assert.That(r.Removed).IsEqualTo(0);
    }

    [Test]
    [Arguments("\e[1;31mred\e[0m plain", "red plain")]
    [Arguments("\e]0;x\atitle", "title")]
    [Arguments("a\e(Bb", "ab")]
    [Arguments("a\e7b", "ab")]
    [Arguments("a\e]]unterminated", "a]unterminated")]
    public async Task EscapeSequencesGoWhole(string input, string expected)
    {
        // Act
        var cleaned = PasteSanitizer.Clean(input);

        // Assert
        await Assert.That(cleaned.Text).IsEqualTo(expected);
    }

    [Test]
    public async Task CleanCountsTheRemovedCharacters()
    {
        // Act
        var cleaned = PasteSanitizer.Clean("\e[1;31mred\e[0m plain");

        // Assert
        await Assert.That(cleaned).IsEqualTo(("red plain", 11));
    }

    [Test]
    public async Task Utf8AndNonAsciiSurvive()
    {
        // Arrange
        string text = "héllo 文字 😀 \u00a0nbsp \u200b zw \u2028";

        // Act
        var r = PasteSanitizer.Prepare(text, bracketed: true);

        // Assert
        // U+2028 is a line separator for ReplaceLineEndings: it becomes Enter; nothing else changes.
        await Assert.That(r.Text).IsEqualTo(Start + "héllo 文字 😀 \u00a0nbsp \u200b zw \r" + End);
        await Assert.That(r.Removed).IsEqualTo(0);
    }

    [Test]
    public async Task LoneSurrogateIsPassedOn()
    {
        // Act
        var cleaned = PasteSanitizer.Clean("a\ud800b");

        // Assert
        // UTF-8 encoding turns it into U+FFFD later.
        await Assert.That(cleaned.Text).IsEqualTo("a\ud800b");
    }

    [Test]
    public async Task OutsideBracketedPasteControlsAreRemoved()
    {
        // Act
        var r = PasteSanitizer.Prepare("\u0015ls\u0003\n", bracketed: false);

        // Assert
        await Assert.That(r).IsEqualTo(new PasteSanitizer.Result("ls\r", 2));
    }

    [Test]
    public async Task OutsideBracketedPasteSanitizeIsConfigurable()
    {
        // Act
        var r = PasteSanitizer.Prepare("\e[31mx\n", bracketed: false, sanitize: false);

        // Assert
        await Assert.That(r.Text).IsEqualTo("\e[31mx\r");
    }

    [Test]
    public async Task BracketedPasteIsSanitizedEvenWithSanitizeOff()
    {
        // Act
        var r = PasteSanitizer.Prepare("a\e[201~b", bracketed: true, sanitize: false);

        // Assert
        await AssertSafeBracketed(r.Text);
    }

    [Test]
    [Arguments("\e[201~")]
    [Arguments("")]
    public async Task NothingLeftSendsNothing(string text)
    {
        // Act
        var r = PasteSanitizer.Prepare(text, bracketed: true);

        // Assert
        await Assert.That(r.Text).IsEqualTo("");
    }

    [Test]
    public async Task DroppedPathsWithControlsAreSkipped()
    {
        // Act
        string joined = ErikTerm.App.Integrations.ShellQuote.JoinPaths(["/a", "/x\e[201~;touch y", "/b c", "/t\tab", "/d\u007f"]);

        // Assert
        await Assert.That(joined).IsEqualTo("/a '/b c' ");
    }

    /// <summary>Random input: whatever goes in, a bracketed paste never contains a control (but TAB/CR) or an early end.</summary>
    [Test]
    public async Task FuzzedInputStaysInsideThePaste()
    {
        // Arrange
        var rng = new Random(52);
        char[] alphabet = ['\e', '[', ']', '2', '0', '1', '~', '\a', '\\', '\u009b', '\u009c', '\u009d', '\r', '\n', '\t', 'a', ';', 'P', '\u0015', '\u007f', 'é'];
        var inputs = new List<string>();
        for (int n = 0; n < 5000; n++)
        {
            var sb = new StringBuilder();
            int len = rng.Next(1, 40);
            for (int i = 0; i < len; i++) sb.Append(alphabet[rng.Next(alphabet.Length)]);
            inputs.Add(sb.ToString());
        }

        // Act
        var sent = inputs.Select(s => PasteSanitizer.Prepare(s, bracketed: true).Text).Where(s => s.Length > 0).ToList();

        // Assert
        foreach (string s in sent) await AssertSafeBracketed(s);
    }
}

public class Osc52GateTests
{
    private static Terminal Term() => new(10, 2, 0);

    private static Osc52Gate Gate() => new(new ConditionalWeakTable<Terminal, object>());

    [Test]
    public async Task AllowDecidesAtOnce()
    {
        // Arrange
        var gate = Gate();

        // Act
        var decision = gate.Offer(Osc52Policy.Allow, Term(), ClipboardTarget.Clipboard, "x", 0);

        // Assert
        await Assert.That(decision).IsEqualTo(Osc52Decision.Apply);
        await Assert.That(gate.Pending).IsNull();
    }

    [Test]
    public async Task DenyDecidesAtOnce()
    {
        // Arrange
        var gate = Gate();

        // Act
        var decision = gate.Offer(Osc52Policy.Deny, Term(), ClipboardTarget.Clipboard, "x", 0);

        // Assert
        await Assert.That(decision).IsEqualTo(Osc52Decision.Denied);
        await Assert.That(gate.Pending).IsNull();
    }

    [Test]
    public async Task AskDefersEveryRequest()
    {
        // Arrange
        var gate = Gate();
        var t = Term();

        // Act
        var first = gate.Offer(Osc52Policy.Ask, t, ClipboardTarget.Clipboard, "old", 0);
        var second = gate.Offer(Osc52Policy.Ask, t, ClipboardTarget.Primary, "new", 100);

        // Assert
        await Assert.That(first).IsEqualTo(Osc52Decision.Asking);
        await Assert.That(second).IsEqualTo(Osc52Decision.Asking);
    }

    /// <summary>A gate asking about two requests from the same pane: "old" (clipboard), then "new" (primary).</summary>
    private static (Osc52Gate Gate, Terminal Source) AskedTwice()
    {
        var gate = Gate();
        var t = Term();
        gate.Offer(Osc52Policy.Ask, t, ClipboardTarget.Clipboard, "old", 0);
        gate.Offer(Osc52Policy.Ask, t, ClipboardTarget.Primary, "new", 100);
        return (gate, t);
    }

    [Test]
    public async Task AskHoldsTheNewestRequest()
    {
        // Act
        var (gate, _) = AskedTwice();

        // Assert
        await Assert.That(gate.Pending!.Text).IsEqualTo("new");
    }

    [Test]
    public async Task AllowAppliesTheHeldRequest()
    {
        // Arrange
        var (gate, _) = AskedTwice();

        // Act
        var r = gate.Resolve(Osc52Choice.Allow, 200);

        // Assert
        await Assert.That((r!.Text, r.Target)).IsEqualTo(("new", ClipboardTarget.Primary));
        await Assert.That(gate.Pending).IsNull();
    }

    [Test]
    public async Task AnAnsweredRequestIsAnsweredOnce()
    {
        // Arrange
        var (gate, _) = AskedTwice();
        gate.Resolve(Osc52Choice.Allow, 200);

        // Act
        var again = gate.Resolve(Osc52Choice.Allow, 300);

        // Assert
        await Assert.That(again).IsNull();
    }

    [Test]
    public async Task AllowIsForThatRequestOnly()
    {
        // Arrange
        var (gate, t) = AskedTwice();
        gate.Resolve(Osc52Choice.Allow, 200);

        // Act
        var next = gate.Offer(Osc52Policy.Ask, t, ClipboardTarget.Clipboard, "again", 400);

        // Assert
        await Assert.That(next).IsEqualTo(Osc52Decision.Asking);
    }

    [Test]
    public async Task DenyDropsIt()
    {
        // Arrange
        var gate = Gate();
        gate.Offer(Osc52Policy.Ask, Term(), ClipboardTarget.Clipboard, "x", 0);

        // Act
        var r = gate.Resolve(Osc52Choice.Deny, 10);

        // Assert
        await Assert.That(r).IsNull();
        await Assert.That(gate.Pending).IsNull();
    }

    /// <summary>A gate asking about a request offered at <paramref name="at"/>.</summary>
    private static Osc52Gate Asking(long at)
    {
        var gate = Gate();
        gate.Offer(Osc52Policy.Ask, Term(), ClipboardTarget.Clipboard, "x", at);
        return gate;
    }

    [Test]
    public async Task ExpiryCountsDownFromTheOffer()
    {
        // Arrange
        var gate = Asking(1000);

        // Act
        int ms = gate.MsUntilExpiry(1000);

        // Assert
        await Assert.That(ms).IsEqualTo(Osc52Gate.ExpireMs);
    }

    [Test]
    public async Task DoesNotExpireBeforeTenSeconds()
    {
        // Arrange
        var gate = Asking(1000);

        // Act
        bool expired = gate.Expire(1000 + Osc52Gate.ExpireMs - 1);

        // Assert
        await Assert.That(expired).IsFalse();
    }

    [Test]
    public async Task ExpiresAfterTenSeconds()
    {
        // Arrange
        var gate = Asking(1000);
        gate.Expire(1000 + Osc52Gate.ExpireMs - 1);

        // Act
        bool expired = gate.Expire(1000 + Osc52Gate.ExpireMs);

        // Assert
        await Assert.That(expired).IsTrue();
        await Assert.That(gate.Pending).IsNull();
    }

    [Test]
    public async Task ALateAnswerAppliesNothing()
    {
        // Arrange
        var gate = Asking(0);

        // Act
        var r = gate.Resolve(Osc52Choice.Allow, Osc52Gate.ExpireMs + 5); // a late click

        // Assert
        await Assert.That(r).IsNull();
    }

    /// <summary>Pane <c>a</c> answered "always for this pane"; <c>b</c> is another pane.</summary>
    private sealed record TrustSetup(ConditionalWeakTable<Terminal, object> Trusted, Osc52Gate Gate, Terminal A, Terminal B, Osc52Gate.Request? Answer);

    private static TrustSetup TrustedA()
    {
        var trusted = new ConditionalWeakTable<Terminal, object>();
        var a = Term();
        var gate = new Osc52Gate(trusted);
        gate.Offer(Osc52Policy.Ask, a, ClipboardTarget.Clipboard, "x", 0);
        var answer = gate.Resolve(Osc52Choice.AlwaysForPane, 1);
        return new TrustSetup(trusted, gate, a, Term(), answer);
    }

    [Test]
    public async Task AlwaysForPaneAppliesThePendingRequest()
    {
        // Act
        var setup = TrustedA();

        // Assert
        await Assert.That(setup.Answer).IsNotNull();
    }

    [Test]
    public async Task AlwaysTrustsThatPane()
    {
        // Arrange
        var s = TrustedA();

        // Act
        var decision = s.Gate.Offer(Osc52Policy.Ask, s.A, ClipboardTarget.Clipboard, "y", 2);

        // Assert
        await Assert.That(decision).IsEqualTo(Osc52Decision.Apply);
    }

    [Test]
    public async Task AlwaysTrustsThatPaneOnly()
    {
        // Arrange
        var s = TrustedA();

        // Act
        var decision = s.Gate.Offer(Osc52Policy.Ask, s.B, ClipboardTarget.Clipboard, "z", 3);

        // Assert
        await Assert.That(decision).IsEqualTo(Osc52Decision.Asking);
    }

    [Test]
    public async Task AlwaysTrustsThatPane_AcrossGates()
    {
        // Arrange
        // Another window's gate (the pane was torn off) shares the trust.
        var s = TrustedA();
        var otherWindow = new Osc52Gate(s.Trusted);

        // Act
        var decision = otherWindow.Offer(Osc52Policy.Ask, s.A, ClipboardTarget.Clipboard, "w", 4);

        // Assert
        await Assert.That(decision).IsEqualTo(Osc52Decision.Apply);
    }

    [Test]
    public async Task DenyStillWinsOverTrust()
    {
        // Arrange
        var s = TrustedA();

        // Act
        var decision = s.Gate.Offer(Osc52Policy.Deny, s.A, ClipboardTarget.Clipboard, "v", 5);

        // Assert
        await Assert.That(decision).IsEqualTo(Osc52Decision.Denied);
    }

    [Test]
    public async Task CancelForAnotherPaneKeepsTheRequest()
    {
        // Arrange
        var gate = Gate();
        gate.Offer(Osc52Policy.Ask, Term(), ClipboardTarget.Clipboard, "x", 0);

        // Act
        gate.Cancel(Term());

        // Assert
        await Assert.That(gate.Pending).IsNotNull();
    }

    [Test]
    public async Task CancelForgetsAClosedPanesRequest()
    {
        // Arrange
        var gate = Gate();
        var a = Term();
        gate.Offer(Osc52Policy.Ask, a, ClipboardTarget.Clipboard, "x", 0);
        gate.Cancel(Term());

        // Act
        gate.Cancel(a);

        // Assert
        await Assert.That(gate.Pending).IsNull();
    }

    [Test]
    public async Task TerminalStillParsesOsc52IntoARequest()
    {
        // Arrange
        var t = Term();
        string? got = null;
        t.ClipboardRequested += (_, text) => got = text;

        // Act
        t.Feed("\e]52;c;aGk=\a"u8.ToArray());

        // Assert
        await Assert.That(got).IsEqualTo("hi");
    }
}

public class Osc52PromptTests
{
    [Test]
    public async Task ThePromptNamesTheProgramThePlaceAndTheSize()
    {
        // Arrange
        string who = "vim", where = "Tab 2 · ~/src";

        // Act
        var (label, text) = Osc52Prompt.Texts(who, where, ClipboardTarget.Clipboard, "héllo");

        // Assert
        await Assert.That(label).IsEqualTo("Tab 2 · ~/src");
        await Assert.That(text).IsEqualTo("vim wants to set the clipboard (5 characters)");
    }

    [Test]
    public async Task ProgramControlledTextsAreCleaned()
    {
        // Arrange
        string who = "evil\e]8;;http://x\a\u202Eexe\nname";
        string where = "Tab 1 · ~/\u202Ednuos\r\n";

        // Act
        var (label, text) = Osc52Prompt.Texts(who, where, ClipboardTarget.Primary, "x");

        // Assert
        foreach (string s in new[] { label, text })
            await Assert.That(s.ToCharArray()).DoesNotContain(ch => char.IsControl(ch) || ch == '\u202E');
        await Assert.That(text).EndsWith("wants to set the primary selection (1 character)");
    }

    [Test]
    public async Task AnUnnamedPaneIsAProgram()
    {
        // Arrange
        string? who = null;

        // Act
        var (label, text) = Osc52Prompt.Texts(who, null, ClipboardTarget.Clipboard, "");

        // Assert
        await Assert.That(label).IsEqualTo("");
        await Assert.That(text).IsEqualTo("A program wants to set the clipboard (0 characters)");
    }

    [Test]
    public async Task OnePromptPerPane()
    {
        // Act
        string key = Osc52Prompt.Key(7);
        string other = Osc52Prompt.Key(8);

        // Assert
        await Assert.That(key).IsEqualTo("osc52:7");
        await Assert.That(other).IsNotEqualTo(key);
    }

    [Test]
    public async Task ThePromptSitsAboveTheNotificationQuestion()
    {
        // Act
        bool above = Osc52Prompt.Priority > NotificationCenter.AskPriority;

        // Assert
        await Assert.That(above).IsTrue();
    }

    [Test]
    public async Task AnswersAreInChoiceOrder()
    {
        // Act
        string[] answers =
        [
            Osc52Prompt.Answers[(int)Osc52Choice.Allow],
            Osc52Prompt.Answers[(int)Osc52Choice.Deny],
            Osc52Prompt.Answers[(int)Osc52Choice.AlwaysForPane],
        ];

        // Assert
        await Assert.That(answers).IsEquivalentTo(["Allow", "Deny", "Always for this pane"], CollectionOrdering.Matching);
    }
}

public class PagerCommandTests
{
    [Test]
    public async Task KeyBindingsGoThroughAPipeNotATempFile()
    {
        // Arrange
        string text = "erikterm key bindings\n\nCtrl+Shift+C  CopyTo(Clipboard)\n";

        // Act
        var argv = PagerCommand.Less(text);

        // Assert
        await Assert.That(argv).IsEquivalentTo(["sh", "-c", PagerCommand.Script, "erikterm-pager", text], CollectionOrdering.Matching);
        await Assert.That(argv).DoesNotContain(a => a.Contains(Path.GetTempPath(), StringComparison.Ordinal) || a.Contains("/tmp", StringComparison.Ordinal));
    }

    [Test]
    public async Task LongTextIsCapped()
    {
        // Act
        var argv = PagerCommand.Less(new string('x', PagerCommand.MaxChars * 2));

        // Assert
        await Assert.That(argv[4].Length).IsLessThan(PagerCommand.MaxChars + 8);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [NotInParallel(Isolation.Processes)]
    public async Task ThePagerScriptPrintsTheTextVerbatim()
    {
        // Arrange
        Skip.Unless(File.Exists("/bin/sh"), "needs /bin/sh");
        string text = "a $(echo no) `x` %s \\n\n";
        var argv = PagerCommand.Less(text);
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardInput = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(argv[2].Replace("| less", "| cat", StringComparison.Ordinal));
        foreach (var a in argv.Skip(3)) psi.ArgumentList.Add(a);

        // Act
        using var p = System.Diagnostics.Process.Start(psi)!;
        string output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();

        // Assert
        await Assert.That(output).IsEqualTo(text);
    }
}
