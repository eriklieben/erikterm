using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ErikTerm.App.Integrations;

internal readonly record struct ToolResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, string? StartError)
{
    public bool Ok => ExitCode == 0 && !TimedOut && StartError is null;
}

/// <summary>
/// Runs the developer tools erikterm integrates with (git, gh, nvim, delta, xdg-open) — always as an argv list,
/// never through a shell — with a timeout, stdin closed (nothing can prompt) and output captured. Availability is
/// looked up on $PATH once per tool.
/// </summary>
internal static class ToolRunner
{
    private static readonly ConcurrentDictionary<string, string?> Found = new(StringComparer.Ordinal);

    /// <summary>The full path of a tool on $PATH (cached), or null when it isn't installed.</summary>
    public static string? Find(string name) => Found.GetOrAdd(name, static n => Search(n, Environment.GetEnvironmentVariable("PATH")));

    public static bool Has(string name) => Find(name) is not null;

    internal static string? Search(string name, string? pathVar)
    {
        if (name.Contains('/')) return File.Exists(name) ? name : null;
        foreach (var dir in (pathVar ?? "/usr/local/bin:/usr/bin:/bin").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, name);
                if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe")) return candidate + ".exe"; // before a same-named script CreateProcess can't start
                if (File.Exists(candidate) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
                    return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* an unreadable PATH entry: try the next directory */ }
        }
        return null;
    }

    /// <summary>Quiet, non-interactive, no locks taken behind the user's back.</summary>
    private static readonly (string Key, string Value)[] QuietEnvironment =
    [
        ("GIT_OPTIONAL_LOCKS", "0"), ("GIT_TERMINAL_PROMPT", "0"), ("GIT_PAGER", "cat"), ("PAGER", "cat"),
        ("GH_PROMPT_DISABLED", "1"), ("GH_NO_UPDATE_NOTIFIER", "1"), ("GH_SPINNER_DISABLED", "1"), ("NO_COLOR", "1"), ("CLICOLOR", "0"),
        ("GIT_ASKPASS", ""), ("SSH_ASKPASS", ""),
    ];

    /// <summary>Runs <paramref name="argv"/> to completion or <paramref name="timeoutMs"/>, whichever comes first (the process tree is killed then).</summary>
    public static ToolResult Run(IReadOnlyList<string> argv, string? cwd, int timeoutMs, int maxOutputChars = 1 << 20)
    {
        if (ProgramPath(argv[0]) is not { } program) return new ToolResult(-1, "", "", false, $"{argv[0]} isn't on PATH");
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        for (int i = 1; i < argv.Count; i++) psi.ArgumentList.Add(argv[i]);
        if (cwd is not null && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
        foreach (var (k, v) in QuietEnvironment) psi.Environment[k] = v;

        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("not started"); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return new ToolResult(-1, "", "", false, ex.Message);
        }
        using (process)
        {
            try { process.StandardInput.Close(); } catch (IOException) { /* it closed its end already */ }
            var stdout = ReadCapped(process.StandardOutput, maxOutputChars);
            var stderr = ReadCapped(process.StandardError, 64 * 1024);
            bool exited = process.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { /* it exited on its own, or can't be killed: reported as timed out either way */ }
                try { process.WaitForExit(500); } catch (InvalidOperationException) { /* it is gone: reported as timed out either way */ }
                return new ToolResult(-1, "", "", true, null);
            }
            process.WaitForExit(); // flush the redirected streams
            string o = stdout.Wait(1000) ? stdout.Result : "", e = stderr.Wait(1000) ? stderr.Result : "";
            return new ToolResult(process.ExitCode, o, e, false, null);
        }
    }

    /// <summary>Never the bare name: Process.Start would also look in the current directory, which may be a repository being reviewed.</summary>
    public static string? ProgramPath(string name) =>
        Path.GetFileName(name) != name ? name : Find(name) ?? Search(name, Environment.GetEnvironmentVariable("PATH"));

    private static async Task<string> ReadCapped(StreamReader reader, int max)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                if (sb.Length < max) sb.Append(buffer, 0, Math.Min(n, max - sb.Length)); // keep draining past the cap
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* the stream broke or was closed: keep what was read */ }
        return sb.ToString();
    }

    /// <summary>Starts a program detached (xdg-open): no waiting, no output.</summary>
    public static string? Launch(IReadOnlyList<string> argv, string? cwd = null)
    {
        try
        {
            if (ProgramPath(argv[0]) is not { } program) return $"{argv[0]}: not found";
            var psi = new ProcessStartInfo(program) { UseShellExecute = false };
            for (int i = 1; i < argv.Count; i++) psi.ArgumentList.Add(argv[i]);
            if (cwd is not null && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return $"{argv[0]}: {ex.Message}";
        }
    }
}
