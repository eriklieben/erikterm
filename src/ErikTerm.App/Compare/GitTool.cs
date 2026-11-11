using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ErikTerm.App.Integrations;
using ErikTerm.Ui.Compare;

namespace ErikTerm.App.Compare;

/// <summary>The result of one git command; stdout as bytes, since file content isn't text until it's decoded.</summary>
internal readonly record struct GitResult(int ExitCode, byte[] Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
    public string Text => Encoding.UTF8.GetString(Stdout).Trim();
}

/// <summary>
/// Runs git as an argv (never through a shell), reading bytes up to <see cref="TextDocument.MaxBytes"/>. Git asks for
/// nothing on the terminal. Stateless, usable from background threads; failure to start is exit code -1.
/// </summary>
internal static class GitTool
{
    /// <summary>Exit code of a command that ran past its time limit.</summary>
    public const int TimedOut = -3;

    public static GitResult Run(string? workingDirectory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null, int timeoutMs = 0)
    {
        if (StartInfo(workingDirectory, arguments, environment, Environment.GetEnvironmentVariable("PATH")) is not { } psi)
            return new GitResult(-1, [], "git isn't on PATH");

        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("not started"); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) { return new GitResult(-1, [], ex.Message); }
        using (process)
        {
            process.StandardInput.Close();
            bool timedOut = false;
            using var deadline = timeoutMs <= 0 ? null : new Timer(_ =>
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { /* it exited on its own, or can't be killed from here */ }
            }, null, timeoutMs, Timeout.Infinite);
            var stderr = process.StandardError.ReadToEndAsync();
            using var output = new MemoryStream();
            bool overflow = ReadCapped(process, output);
            process.WaitForExit();
            string err = stderr.Wait(5000) ? stderr.Result : "";
            int code = -2;
            if (!overflow) code = timedOut ? TimedOut : process.ExitCode;
            return new GitResult(code, output.ToArray(), err.Trim());
        }
    }

    public static GitResult Run(string? workingDirectory, params string[] arguments) => Run(workingDirectory, (IReadOnlyList<string>)arguments);

    /// <summary>
    /// The full path from <paramref name="pathVariable"/>, or null when git isn't there: given a bare name, Process.Start
    /// also looks in the program's own folder and the current directory, and a repository can ship a file named git.
    /// </summary>
    internal static ProcessStartInfo? StartInfo(string? workingDirectory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, string? pathVariable)
    {
        if (ToolRunner.Search("git", pathVariable) is not { } git) return null;
        var psi = new ProcessStartInfo(git)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        if (environment is not null) foreach (var (name, value) in environment) psi.Environment[name] = value;
        foreach (string a in arguments) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Copies stdout into <paramref name="output"/>; past the limit it kills the process and returns true.</summary>
    private static bool ReadCapped(Process process, MemoryStream output)
    {
        var buffer = new byte[64 * 1024];
        var stream = process.StandardOutput.BaseStream;
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
            if (output.Length <= TextDocument.MaxBytes) continue;
            try { process.Kill(); } catch (InvalidOperationException) { /* it exited on its own */ }
            return true;
        }
        return false;
    }

    /// <summary>The bytes of a <c>git show</c> spec (<c>rev:path</c>, <c>:1:path</c> or an object id) with content filters off.</summary>
    public static GitResult Show(string? workingDirectory, string spec) =>
        Run(workingDirectory, "show", "--no-textconv", "--end-of-options", spec);

    /// <summary>The top level of the repository containing <paramref name="cwd"/>, or null.</summary>
    public static string? TopLevel(string cwd)
    {
        var r = Run(cwd, "rev-parse", "--show-toplevel");
        return r.Ok && r.Text.Length > 0 ? r.Text : null;
    }

    /// <summary>
    /// Stages one file: a literal pathspec, so characters like '*' mean themselves. For a path deleted in the work
    /// tree this records the removal (<c>git add</c> stages deletions).
    /// </summary>
    public static GitResult Add(string root, string path) =>
        Run(root, "--literal-pathspecs", "add", "--", path);
}
