namespace ErikTerm.Tests;

// Temporary diagnostic (branch only): what the macOS runner reports for a pane's directory.
public class MacCwdDiagnosticTests
{
    [Test]
    public async Task ReportsWhatThePaneDirectoryLookupSees()
    {
        string dir = Directory.CreateTempSubdirectory("et-diag-").FullName;
        using var pane = TestPanes.Spawn(workingDirectory: dir);
        await Task.Delay(1500);
        int pid = pane.Pty.ProcessId;
        string report = $"dir={dir} pid={pid} alive={IsAlive(pid)} " +
            $"wd(pid)={ErikTerm.Pty.ProcessInfo.WorkingDirectory(pid) ?? "null"} " +
            $"wd(self)={ErikTerm.Pty.ProcessInfo.WorkingDirectory(Environment.ProcessId) ?? "null"} cwd(self)={Environment.CurrentDirectory} " +
            $"start={pane.StartDirectory(null) ?? "null"} name={pane.Pty.ForegroundProcessName ?? "null"}";
        await Assert.That(pane.StartDirectory(null)).IsEqualTo("diagnostic").Because(report);
        Directory.Delete(dir, true);
    }

    private static bool IsAlive(int pid)
    {
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return false; }
    }
}
