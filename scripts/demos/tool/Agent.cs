using System.Diagnostics;
using System.Text;

namespace Demos;

/// <summary>
/// helper TASK: a stand-in coding agent for the README clips, with a plain CLI-assistant look of its own. It reports its
/// state to erikterm with OSC 7501 as app=helper (an agent erikterm doesn't know, so the pill and overview call it
/// "Agent"), plus cost and context through <c>erikterm cli statusline</c>. It never talks to any model. A word in the
/// task picks the scenario:
///
///     helper "Add moon phases"                works on, with progress
///     helper "Fix the flaky forecast test"    works, then asks before running a command (y approves)
///     helper "Document the station format"    works, then finishes
///     helper "Review the parser"              sits idle at its prompt
///
/// DEMO_AGENT_BLOCK_AFTER and DEMO_AGENT_DONE_AFTER (seconds) set when the flaky and doc scenarios block or finish.
/// </summary>
internal static class Agent
{
    private const string Cyan = "\e[36m", Bold = "\e[1m", Green = "\e[32m", Red = "\e[31m", Yellow = "\e[33m",
        Grey = "\e[90m", Reset = "\e[0m";
    private const string Spin = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string _spinWord = "thinking";
    private static string _task = "";

    public static int Run(string[] args)
    {
        _task = args.Length > 0 ? string.Join(' ', args) : "Review the parser";
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.TreatControlCAsInput = true;
        try
        {
            Play(_task.ToLowerInvariant());
        }
        finally
        {
            W($"\e]7501;state=clear:app=helper\e\\{Reset}\r\n");
        }
        return 0;
    }

    private static void Play(string p)
    {
        W($"\e]0;helper: {_task}\a");
        Header();
        if (p.Contains("moon"))
        {
            Status("working", "Reading the forecast code", progress: 10);
            Statusline(0.18, 12);
            Work(1.0);
            Step("read src/Tidewatch/Forecast.cs");
            Work(0.8);
            Step("read src/Tidewatch/Commands.cs");
            Status("working", "Writing Moon.cs", progress: 35);
            Statusline(0.31, 21);
            Work(1.2, "planning");
            Step($"wrote src/Tidewatch/Moon.cs {Green}+44{Reset}");
            Status("working", "Wiring the phase into the forecast", progress: 60);
            Statusline(0.44, 27);
            Work(1.4, "editing");
            Step($"edited src/Tidewatch/Commands.cs {Green}+3{Reset} {Red}-1{Reset}");
            Status("working", "Running dotnet test", progress: 80);
            Statusline(0.52, 31);
            Work(60, "running tests");
        }
        else if (p.Contains("flaky") || p.Contains("test"))
        {
            Status("working", "Looking at ForecastTests.cs", progress: 20);
            Statusline(0.09, 8);
            Work(0.8);
            Step("read tests/Tidewatch.Tests/ForecastTests.cs");
            Work(Seconds("DEMO_AGENT_BLOCK_AFTER", 2.5), "thinking");
            Note("Two_highs_a_day reads DateTime.Now; pin the start date, then run the suite");
            W($"\r\n  {Yellow}?{Reset} run {Bold}dotnet test{Reset}? {Grey}[y/N]{Reset} ");
            Status("blocked", "Run dotnet test?", kind: "permission");
            Statusline(0.21, 14);
            bool ok = Key("yY", "nN\r\e");
            W($"{(ok ? "yes" : "no")}\r\n");
            Status("working", "Running dotnet test", progress: 70);
            if (ok) Step("dotnet test: 24 passed");
            Work(2.0, "editing");
            Step($"edited tests/Tidewatch.Tests/ForecastTests.cs {Green}+2{Reset} {Red}-1{Reset}");
            Status("working", "Running dotnet test", progress: 90);
            Work(60, "running tests");
        }
        else if (p.Contains("doc"))
        {
            Status("working", "Reading Station.cs", progress: 30);
            Statusline(0.05, 6);
            Work(0.8);
            Step("read src/Tidewatch/Station.cs");
            Work(Seconds("DEMO_AGENT_DONE_AFTER", 3.0), "writing");
            Step($"edited README.md {Green}+14{Reset}");
            W($"\r\n  {Bold}done:{Reset} documented the station file format\r\n");
            Status("done", "Documented the station file format");
            Statusline(0.12, 9);
            PromptLine();
        }
        else
        {
            Status("idle");
            Statusline(0.02, 3);
            Note("ready; what should I look at in the parser?");
            PromptLine();
        }
        Key("\u0003\u0004", ""); // stays until Ctrl+C or Ctrl+D
    }

    private static void W(string s)
    {
        Console.Out.Write(s);
        Console.Out.Flush();
    }

    private static void Status(string state, string msg = "", string? kind = null, int? progress = null)
    {
        var f = new StringBuilder($"state={state}:app=helper");
        if (kind is not null) f.Append($":kind={kind}");
        if (progress is not null) f.Append($":progress={progress}");
        if (msg.Length > 0) f.Append($":msg={Convert.ToBase64String(Encoding.UTF8.GetBytes(msg))}");
        W($"\e]7501;{f}\e\\");
    }

    /// <summary>Cost and context, the way an agent's status line hook would report them.</summary>
    private static void Statusline(double cost, int context)
    {
        string usd = cost.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string json = "{\"model\":{\"display_name\":\"model-m\"},\"cost\":{\"total_cost_usd\":" + usd +
            "},\"context_window\":{\"used_percentage\":" + context + "}}";
        try
        {
            var psi = new ProcessStartInfo("erikterm", ["cli", "statusline"])
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            proc.StandardInput.Write(json);
            proc.StandardInput.Close();
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }
    }

    private static void Header()
    {
        string home = Environment.GetEnvironmentVariable("HOME") ?? "";
        string cwd = Environment.CurrentDirectory;
        if (home.Length > 0 && cwd.StartsWith(home, StringComparison.Ordinal)) cwd = "~" + cwd[home.Length..];
        W("\e[H\e[2J\r\n");
        W($"{Cyan}{Bold}helper{Reset} {Grey}0.9 · {cwd}{Reset}\r\n");
        W($"{Grey}task:{Reset} {_task}\r\n\r\n");
    }

    private static void Step(string text) => W($"  {Green}✓{Reset} {text}\r\n");

    private static void Note(string text) => W($"  {Grey}│{Reset} {text}\r\n");

    private static void PromptLine() => W($"\r\n{Cyan}helper>{Reset} ");

    /// <summary>A spinner line for a while.</summary>
    private static void Work(double seconds, string? word = null)
    {
        if (word is not null) _spinWord = word;
        var end = Clock.Elapsed + TimeSpan.FromSeconds(seconds);
        for (int i = 0; Clock.Elapsed < end; i++)
        {
            W($"\r\e[2K  {Cyan}{Spin[i % Spin.Length]}{Reset} {_spinWord}… {Grey}{(int)Clock.Elapsed.TotalSeconds}s{Reset}");
            Thread.Sleep(200);
        }
        W("\r\e[2K");
    }

    /// <summary>Waits for a key: true for one in accept, false for one in refuse; others are ignored.</summary>
    private static bool Key(string accept, string refuse)
    {
        while (true)
        {
            char c = Console.ReadKey(intercept: true).KeyChar;
            if (accept.Contains(c)) return true;
            if (refuse.Contains(c)) return false;
        }
    }

    private static double Seconds(string variable, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(variable), System.Globalization.CultureInfo.InvariantCulture,
            out double s) ? s : fallback;
}
