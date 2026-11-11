using ErikTerm.App;

namespace ErikTerm.Tests;

/// <summary>crash-&lt;time&gt;.log under the state directory: the stack trace a one-line stderr message used to lose.</summary>
public class CrashLogTests
{
    private static string TempState() => Directory.CreateTempSubdirectory("erikterm-crash-").FullName;

    private static Exception Thrown()
    {
        try { throw new InvalidOperationException("render went wrong"); }
        catch (Exception ex) { return ex; }
    }

    [Test]
    public async Task LogHasTheStackTrace()
    {
        // Arrange
        string state = TempState();

        // Act
        string? path = CrashLog.Write("frame", Thrown(), state);

        // Assert
        await Assert.That(path).IsNotNull();
        string text = File.ReadAllText(path!);
        await Assert.That(text).Contains("System.InvalidOperationException: render went wrong");
        await Assert.That(text).Contains(nameof(Thrown));
        Directory.Delete(state, recursive: true);
    }

    [Test]
    [RunOn(OS.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task LogIsReadableOnlyByTheUser()
    {
        // Arrange
        string state = TempState();

        // Act
        string path = CrashLog.Write("frame", Thrown(), state)!;

        // Assert
        await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.Delete(state, recursive: true);
    }

    [Test]
    public async Task LatestIsTheNewestLog()
    {
        // Arrange
        string state = TempState();
        string older = CrashLog.Write("first", Thrown(), state, new DateTime(2026, 11, 7, 10, 0, 0))!;
        File.SetLastWriteTime(older, new DateTime(2026, 11, 7, 10, 0, 0));
        string newer = CrashLog.Write("second", Thrown(), state, new DateTime(2026, 11, 7, 11, 0, 0))!;

        // Act
        var latest = CrashLog.Latest(state);

        // Assert
        await Assert.That(latest?.Path).IsEqualTo(newer);
        Directory.Delete(state, recursive: true);
    }

    [Test]
    public async Task NoLogsMeansNoLatest()
    {
        // Arrange
        string state = TempState();

        // Act
        var latest = CrashLog.Latest(state);

        // Assert
        await Assert.That(latest).IsNull();
        Directory.Delete(state, recursive: true);
    }
}
