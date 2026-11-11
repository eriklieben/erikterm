using System.Diagnostics;
using System.Text;
using Tidewatch;

namespace Demos;

/// <summary>
/// The demo world's dotnet (~/.local/bin/dotnet): output shaped like the .NET 10 SDK's terminal logger and
/// Microsoft.Testing.Platform, for the tidewatch repository, with the same timings every run. A real restore and build
/// would take a while and need NuGet; the clips only need it to look right.
///
///     dotnet build                             restore, both projects, an OSC 9;4 progress report
///     dotnet test                              the test run; the count is the [Test] attributes under tests/
///     dotnet run --project src/Tidewatch -- chart|forecast    runs the real tidewatch code (compiled into demo)
///     dotnet run --project src/Tidewatch -- serve             a web host's request log (World.ServerLog), then waits
///     dotnet nuget list source                 nuget.org only
///
/// Where a line wouldn't fit the terminal, an output path is shortened to its file name rather than wrapping.
/// </summary>
internal static class FakeDotnet
{
    private const string Cyan = "\e[36;1m", Green = "\e[32;1m", Grey = "\e[90m", Reset = "\e[m", Tfm = Cyan + "net10.0" + Reset;
    private const string App = "src/Tidewatch/bin/Debug/net10.0/tidewatch.dll";
    private const string Tests = "tests/Tidewatch.Tests/bin/Debug/net10.0/Tidewatch.Tests.dll";

    private static readonly DateTime Today = new(2026, 11, 10);

    public static int Run(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string verb = args.Length > 0 ? args[0] : "";
        try
        {
            return verb switch
            {
                "build" => Build(),
                "test" => Test(),
                "run" => RunApp(args[1..]),
                "nuget" when args.Skip(1).Take(2).SequenceEqual(["list", "source"]) => NugetSources(),
                _ => Unknown(verb),
            };
        }
        finally
        {
            W("\e]9;4;0;\e\\\e[?25h");
        }
    }

    private static int Build()
    {
        var clock = Stopwatch.StartNew();
        Restore(clock);
        Project("Tidewatch", App, [("ResolveAssemblyReferences", 0.3), ("CoreCompile", 1.5), ("CopyFilesToOutputDirectory", 0.2)], 15, 55);
        Project("Tidewatch.Tests", Tests, [("ResolveAssemblyReferences", 0.3), ("CoreCompile", 1.2), ("GenerateBuildDependencyFile", 0.2)], 55, 100);
        W($"\r\nBuild {Green}succeeded{Reset} in {Seconds(clock)}\r\n");
        return 0;
    }

    private static int Test()
    {
        var clock = Stopwatch.StartNew();
        W("\e[?25l");
        Progress(5);
        Live("  Tidewatch.Tests", "Build", 0.7);
        int total = CountTests();
        string dll = Fit(Tests, "Running tests from ".Length + " (net10.0|x64)".Length);
        W($"Running tests from {Grey}{dll}{Reset} (net10.0|x64)\r\n");
        for (int done = 0; done <= total; done += 3)
        {
            int n = Math.Min(done, total);
            Progress(10 + 90 * n / total);
            W($"\r\e[2K[{Green}✓{n}{Reset}/\e[31mx0{Reset}/\e[33m↓0{Reset}] {Grey}{Path.GetFileName(Tests)}{Reset} ({(int)clock.Elapsed.TotalSeconds}s)");
            Thread.Sleep(170);
        }
        dll = Fit(Tests, " (net10.0|x64) passed (1s 337ms)".Length);
        W($"\r\e[2K{Grey}{dll}{Reset} (net10.0|x64) \e[32mpassed{Reset} {Grey}(1s 337ms){Reset}\r\n");
        W("\r\n");
        W($"\e[32mTest run summary: Passed!\r\n{Reset}");
        W($"  total: {total}\r\n  failed: 0\r\n\e[32m  succeeded: {total}\r\n{Reset}  skipped: 0\r\n");
        W($"  duration: {(int)clock.Elapsed.TotalSeconds}s {clock.Elapsed.Milliseconds:000}ms\r\n");
        return 0;
    }

    private static int RunApp(string[] args)
    {
        int dashes = Array.IndexOf(args, "--");
        string[] appArgs = dashes >= 0 ? args[(dashes + 1)..] : [];
        Live("  Tidewatch", "CoreCompile", 0.6);
        string command = appArgs.Length > 0 ? appArgs[0] : "forecast";
        string stationFile = appArgs.Skip(1).FirstOrDefault(a => a.EndsWith(".txt", StringComparison.Ordinal)) ?? "stations/9414290.txt";
        var station = Station.Parse(File.ReadAllText(stationFile));
        var output = Console.Out;
        switch (command)
        {
            case "chart":
                Commands.PrintChart(output, station, Today, hours: 48);
                break;
            case "serve":
                Serve(output);
                break;
            case "forecast":
                int i = Array.IndexOf(appArgs, "--days");
                int days = i >= 0 && i + 1 < appArgs.Length ? int.Parse(appArgs[i + 1]) : 1;
                Commands.PrintForecast(output, station, Today.AddHours(9.5), days, "m");
                break;
            default:
                Console.Error.WriteLine("usage: tidewatch [forecast|chart] [STATION_FILE] [--days N] [--unit m|ft]");
                return 2;
        }
        output.Flush();
        return 0;
    }

    /// <summary>dotnet nuget list source, with only nuget.org configured.</summary>
    private static int NugetSources()
    {
        W("Registered Sources:\r\n  1.  nuget.org [Enabled]\r\n      https://api.nuget.org/v3/index.json\r\n");
        return 0;
    }

    /// <summary>dotnet run -- serve: a web host's startup lines and its request log so far; then it runs until Ctrl+C.</summary>
    private static void Serve(TextWriter output)
    {
        output.Write("info: Microsoft.Hosting.Lifetime[14]\n      Now listening on: http://localhost:5080\n" +
            "info: Microsoft.Hosting.Lifetime[0]\n      Application started. Press Ctrl+C to shut down.\n" +
            "info: Microsoft.Hosting.Lifetime[0]\n      Hosting environment: Development\n");
        output.Write(World.ServerLog());
        output.Flush();
        Thread.Sleep(Timeout.Infinite);
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"dotnet {verb}: this demo's stand-in only knows build, test, run and nuget list source");
        return 1;
    }

    private static void Restore(Stopwatch clock)
    {
        Progress(5);
        Live("Restore", "", 0.5, label: false);
        W($"Restore complete ({Seconds(clock)})\r\n");
    }

    /// <summary>A project's targets as a live line with a running time, then its "succeeded" line.</summary>
    private static void Project(string name, string output, (string Target, double Seconds)[] targets, int from, int to)
    {
        var clock = Stopwatch.StartNew();
        double all = targets.Sum(t => t.Seconds), done = 0;
        foreach (var (target, seconds) in targets)
        {
            Progress(from + (int)((to - from) * done / all));
            Live($"  {name} {Tfm}", target, seconds, clock);
            done += seconds;
        }
        Progress(to);
        string head = $"  {name} net10.0 succeeded ({Seconds(clock)}) → ";
        W($"  {name} {Tfm} {Green}succeeded{Reset} ({Seconds(clock)}) → {Fit(output, head.Length)}\r\n");
    }

    /// <summary>A terminal-logger status line that counts up for a while and then goes.</summary>
    private static void Live(string what, string target, double seconds, Stopwatch? clock = null, bool label = true)
    {
        clock ??= Stopwatch.StartNew();
        var end = clock.Elapsed + TimeSpan.FromSeconds(seconds);
        while (clock.Elapsed < end)
        {
            W($"\r\e[2K{what}{(label ? " " : "")}{target} ({Seconds(clock)})");
            Thread.Sleep(100);
        }
        W("\r\e[2K");
    }

    private static int CountTests()
    {
        int n = 0;
        if (Directory.Exists("tests"))
            foreach (var f in Directory.EnumerateFiles("tests", "*.cs", SearchOption.AllDirectories))
                n += File.ReadLines(f).Count(l => l.Trim() == "[Test]");
        return Math.Max(n, 1);
    }

    /// <summary>The path, or only its file name when the line wouldn't fit the terminal.</summary>
    private static string Fit(string path, int rest)
    {
        int width = Console.IsOutputRedirected ? 200 : Console.WindowWidth;
        return rest + path.Length < width ? path : Path.GetFileName(path);
    }

    private static string Seconds(Stopwatch clock) =>
        clock.Elapsed.TotalSeconds.ToString("0.0s", System.Globalization.CultureInfo.InvariantCulture);

    private static void Progress(int percent) => W($"\e]9;4;1;{percent}\e\\");

    private static void W(string s)
    {
        Console.Out.Write(s);
        Console.Out.Flush();
    }
}
