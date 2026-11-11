using ErikTerm.App.Compare;
using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;

namespace ErikTerm.Tests;

/// <summary>Tools are started by the full path found on PATH: a bare name would also find a file in the current directory.</summary>
public class ToolLookupTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "erikterm-tool-lookup-" + Guid.NewGuid().ToString("N"));

    [After(Test)]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string PlaceFakeGit()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, OperatingSystem.IsWindows() ? "git.exe" : "git");
        File.WriteAllText(path, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Test]
    public async Task GitIsStartedByItsFullPathFromPath()
    {
        // Arrange
        string git = PlaceFakeGit();

        // Act
        var psi = GitTool.StartInfo(null, ["version"], null, _dir);

        // Assert
        await Assert.That(psi).IsNotNull();
        await Assert.That(psi!.FileName).IsEqualTo(git);
        await Assert.That(Path.IsPathRooted(psi.FileName)).IsTrue();
    }

    [Test]
    public async Task GitThatIsNotOnPathIsNotStartedByBareName()
    {
        // Arrange
        Directory.CreateDirectory(_dir);

        // Act
        var psi = GitTool.StartInfo(null, ["version"], null, _dir);

        // Assert
        await Assert.That(psi).IsNull();
    }

    [Test]
    public async Task AToolThatIsNotOnPathReportsThatInsteadOfBeingStartedByBareName()
    {
        // Arrange
        string[] argv = ["erikterm-no-such-tool-" + Guid.NewGuid().ToString("N")];

        // Act
        var result = ToolRunner.Run(argv, null, 1000);

        // Assert
        await Assert.That(result.Ok).IsFalse();
        await Assert.That(result.StartError).IsEqualTo($"{argv[0]} isn't on PATH");
    }

    [Test]
    public async Task DisposingAnIpcServerTwiceIsHarmless()
    {
        // Arrange
        Directory.CreateDirectory(_dir);
        var server = new IpcServer(Path.Combine(_dir, "s.sock"), _ => Task.FromResult(new IpcResponse { Ok = true }));
        server.Dispose();

        // Act and Assert
        await Assert.That(() => server.Dispose()).ThrowsNothing();
    }
}
