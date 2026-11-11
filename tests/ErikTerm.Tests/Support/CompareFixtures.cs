using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ErikTerm.Ui.Compare;

namespace ErikTerm.Tests;

/// <summary>Host for merge-screen tests: records saves in memory.</summary>
internal sealed class FakeMergeHost(bool repoMode = true) : IMergeHost
{
    public bool RepoMode { get; } = repoMode;
    public Dictionary<string, byte[]?> Saved { get; } = [];
    public List<string> Staged { get; } = [];
    public string? FailWith { get; set; }
    public Func<string, string?>? Editor { get; set; }

    public string? StageDescription(MergeEntry entry) => $"git add -- {entry.Path}";

    public string? Save(MergeEntry entry, byte[]? content, bool stage)
    {
        if (FailWith is not null) return FailWith;
        Saved[entry.Path] = content;
        if (stage && RepoMode) Staged.Add(entry.Path);
        return null;
    }

    public string? EditExternally(MergeEntry entry, string text, int line) => Editor is null ? text : Editor(text);
}

/// <summary>Helpers building merge models from strings.</summary>
internal static class CompareFixtures
{
    public static string[] Raw(params string[] lines) => [.. lines.Select(l => l + "\n")];

    public static MergeFile Merged(string baseText, string ours, string theirs, string path = "src/a.ts", bool baseKnown = true) =>
        MergeFile.FromText(path, "main", "feature/x", TextDocument.FromText(baseText), TextDocument.FromText(ours), TextDocument.FromText(theirs), baseKnown);

    public static string Text(this byte[]? bytes) => bytes is null ? "" : Encoding.UTF8.GetString(bytes);

    public static MergeSession Session(FakeMergeHost host, params MergeFile[] files)
    {
        var session = new MergeSession(host);
        foreach (var f in files) session.Entries.Add(new MergeEntry(f.Path) { File = f });
        return session;
    }

    /// <summary>A rendered frame of a screen at a size.</summary>
    public static Frame Render(ITuiScreen screen, int cols, int rows)
    {
        var frame = new Frame(cols, rows);
        screen.Render(frame);
        return frame;
    }
}

/// <summary>
/// A terminal whose input is a script: each step waits until the screen has printed a text (escape sequences removed),
/// then delivers its bytes. Output is kept; input stops when the script ends, which ends the program.
/// </summary>
internal sealed partial class FakeTerminal(int cols, int rows, params (string WaitFor, byte[] Bytes)[] script) : ITuiTerminal
{
    private readonly object _gate = new();
    private readonly StringBuilder _output = new();
    private int _step;

    public int LeaveCount { get; private set; }

    public string OutputText { get { lock (_gate) return StripEscapes(_output.ToString()); } }

    [GeneratedRegex(@"\x1b\[[0-9;?<]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)")]
    private static partial Regex EscapeSequence();

    public static string StripEscapes(string s) => EscapeSequence().Replace(s, "");

    public (int Cols, int Rows) Size() => (cols, rows);

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_step >= script.Length) return -1;
        var (waitFor, bytes) = script[_step];
        if (waitFor.Length > 0 && !OutputText.Contains(waitFor, StringComparison.Ordinal))
        {
            Thread.Sleep(2);
            return 0;
        }
        _step++;
        bytes.CopyTo(buffer);
        return bytes.Length;
    }

    public void Write(ReadOnlySpan<byte> bytes) { lock (_gate) _output.Append(Encoding.UTF8.GetString(bytes)); }
    public void Wake() { }
    public bool TakeResized() => false;
    public void EnterFullScreen() { }
    public void LeaveFullScreen() => LeaveCount++;
}

/// <summary>A throwaway git repository with a fixed identity and no signing.</summary>
internal sealed class TempRepo : IDisposable
{
    public string Dir { get; }

    public TempRepo()
    {
        Dir = Path.Combine(Path.GetTempPath(), "etmerge" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Dir);
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
    }

    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Dir, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        LastExit = p.ExitCode;
        return stdout + stderr;
    }

    public int LastExit { get; private set; }

    public void Write(string path, string content)
    {
        string full = Path.Combine(Dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Encoding.UTF8.GetBytes(content));
    }

    public string Read(string path) => File.ReadAllText(Path.Combine(Dir, path));
    public bool Exists(string path) => File.Exists(Path.Combine(Dir, path));

    public void CommitAll(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    /// <summary>Base commit on main, a change on 'other' and a change on main, merged: the merge stops with conflicts.</summary>
    public void BuildConflict(Action<TempRepo> baseFiles, Action<TempRepo> onOther, Action<TempRepo> onMain)
    {
        baseFiles(this);
        CommitAll("base");
        Git("switch", "-q", "-c", "other");
        onOther(this);
        CommitAll("other");
        Git("switch", "-q", "main");
        onMain(this);
        CommitAll("main");
        Git("merge", "-q", "--no-edit", "other");
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
