using System.Diagnostics;
using System.Runtime;
using System.Text;
using System.Text.Json;
using ErikTerm.Bench;
using ErikTerm.Core;
using ErikTerm.Pty;

// erikterm-bench — throughput of the VT parser + screen model (and optionally the PTY path).
//
//   erikterm-bench [--mb N] [--iters N] [--cols N] [--rows N] [--only a,b] [--pty]
//                  [--json results.json] [--baseline previous.json] [--list] [--reflow]
//                  [--scrollback N] [--memory file]
//
// --reflow times Resize() with a full scrollback (--scrollback, 10k lines by default; column changes re-wrap
// everything), as when dragging a pane divider.
//
// --memory feeds a file (LF -> CRLF, as a tty's onlcr does) into one Terminal with --scrollback lines and reports
// the managed heap and process RSS after a full GC: what the screen model and scrollback cost for that output, right
// after it and once the scrollback is compacted (as the PTY reader does when the output stops).
//
// Steady-state numbers: each workload reuses one Terminal, so after warm-up the scrollback is full
// and "alloc/MB" should be ~0. A non-zero value there is a regression in the hot path.

var opts = Options.Parse(args);
if (opts.List)
{
    foreach (var w in Workloads.All) Console.WriteLine($"{w.Name,-14} {w.Description}");
    return 0;
}

Console.WriteLine($"erikterm-bench  {opts.Cols}x{opts.Rows}  {opts.Megabytes} MB/workload  {opts.Iterations} iterations  " +
                  $"{(IsNativeAot() ? "NativeAOT" : "JIT")}  " +
                  $"GC={(GCSettings.IsServerGC ? "server" : "workstation")}");
Console.WriteLine();

if (opts.Memory is not null)
{
    RunMemory(opts);
    return 0;
}

var results = new List<Result>();
var selected = Workloads.All.Where(w => opts.Only is null || opts.Only.Contains(w.Name)).ToArray();

Console.WriteLine($"{"workload",-14} {"MB/s",9} {"best",9} {"ms/iter",9} {"alloc/MB",10} {"gc0/1/2",9}");
Console.WriteLine(new string('-', 66));
foreach (var w in selected)
{
    byte[] data = w.Generate(opts.Megabytes * 1024 * 1024, opts.Cols, opts.Rows);
    var r = RunInProcess(w.Name, data, opts);
    results.Add(r);
    Print(r);
}

if (opts.Reflow)
{
    Console.WriteLine();
    RunReflow(opts);
}

if (opts.Pty)
{
    Console.WriteLine();
    Console.WriteLine("end-to-end through a real PTY (`cat file` -> kernel tty -> read -> parse):");
    foreach (var w in selected)
    {
        byte[] data = w.Generate(opts.Megabytes * 1024 * 1024, opts.Cols, opts.Rows);
        var r = RunThroughPty("pty:" + w.Name, data, opts);
        results.Add(r);
        Print(r);
    }
}

if (opts.Baseline is not null) CompareWithBaseline(results, opts.Baseline);
if (opts.Json is not null) WriteJson(results, opts);
return 0;

// PublishAot also flips RuntimeFeature.IsDynamicCodeSupported under the JIT, so detect AOT directly:
// a JIT app has its managed assembly next to the executable, a native one doesn't.
static bool IsNativeAot() =>
    !File.Exists(Path.Combine(AppContext.BaseDirectory, "erikterm-bench.dll"));

static Result RunInProcess(string name, byte[] data, Options opts)
{
    var term = new Terminal(opts.Cols, opts.Rows, scrollbackLines: 10_000);
    term.Response = static _ => { };
    const int chunk = 64 * 1024; // typical PTY read size

    void FeedAll()
    {
        for (int off = 0; off < data.Length; off += chunk)
            term.Feed(data.AsSpan(off, Math.Min(chunk, data.Length - off)));
    }

    FeedAll(); // warm-up: fills scrollback, faults in pages, tiers up (under JIT)

    var times = new double[opts.Iterations];
    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
    long alloc = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < opts.Iterations; i++)
    {
        long start = Stopwatch.GetTimestamp();
        FeedAll();
        times[i] = Stopwatch.GetElapsedTime(start).TotalSeconds;
    }
    alloc = GC.GetAllocatedBytesForCurrentThread() - alloc;

    return Result.From(name, data.Length, times, alloc,
        GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2);
}

static void RunMemory(Options opts)
{
    byte[] bytes = Load(opts.Memory!);
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    long rssBefore = Rss(), heapBefore = GC.GetTotalMemory(true);

    var term = new Terminal(opts.Cols, opts.Rows, scrollbackLines: opts.Scrollback);
    term.Response = static _ => { };
    const int chunk = 64 * 1024;
    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
    long alloc = GC.GetAllocatedBytesForCurrentThread();
    long start = Stopwatch.GetTimestamp();
    for (int off = 0; off < bytes.Length; off += chunk)
        term.Feed(bytes.AsSpan(off, Math.Min(chunk, bytes.Length - off)));
    double secs = Stopwatch.GetElapsedTime(start).TotalSeconds;
    alloc = GC.GetAllocatedBytesForCurrentThread() - alloc;
    g0 = GC.CollectionCount(0) - g0; g1 = GC.CollectionCount(1) - g1; g2 = GC.CollectionCount(2) - g2;
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    long heapFed = GC.GetTotalMemory(true) - heapBefore;
    long rssFed = Rss();
    int lines = term.ScrollbackCount;

    // What the PTY reader does once the output stops: compact the scrollback, then one aggressive GC.
    long c0 = Stopwatch.GetTimestamp();
    while (term.CompactScrollback(512)) { }
    double compactMs = Stopwatch.GetElapsedTime(c0).TotalMilliseconds;
    GC.Collect(2, GCCollectionMode.Aggressive, true, true); // gives freed memory back to the system (as the app does)
    long heap = GC.GetTotalMemory(true) - heapBefore;
    long rss = Rss();

    // A divider drag over this scrollback: a few one-column steps each way.
    var times = new List<double>();
    for (int i = 0; i < 6; i++)
    {
        long t0 = Stopwatch.GetTimestamp();
        term.Resize(i % 2 == 0 ? opts.Cols - 1 : opts.Cols, opts.Rows);
        times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    }
    times.Sort();

    Console.WriteLine($"memory: {Path.GetFileName(opts.Memory)} {bytes.Length / (1024.0 * 1024):F1} MB, {opts.Cols}x{opts.Rows}, " +
                      $"scrollback {lines}/{opts.Scrollback}");
    Console.WriteLine($"  right after the output: terminal heap {heapFed / (1024.0 * 1024):F1} MB, RSS {rssFed / (1024.0 * 1024):F1} MB (before {rssBefore / (1024.0 * 1024):F1} MB, peak {Rss("VmHWM:") / (1024.0 * 1024):F1} MB)");
    Console.WriteLine($"  compacted in {compactMs:F1} ms: terminal heap {heap / (1024.0 * 1024):F1} MB, RSS {rss / (1024.0 * 1024):F1} MB");
    Console.WriteLine($"  feed {bytes.Length / (1024.0 * 1024) / secs:F1} MB/s, allocated {alloc / (1024.0 * 1024):F1} MB, gc0/1/2 {g0}/{g1}/{g2}");
    Console.WriteLine($"  reflow (1-column steps): median {times[times.Count / 2]:F1} ms, max {times[^1]:F1} ms");
    GC.KeepAlive(term);
    GC.KeepAlive(bytes);
}

static byte[] Load(string path)
{
    byte[] raw = File.ReadAllBytes(path);
    var data = new List<byte>(raw.Length + raw.Length / 16);
    foreach (byte b in raw)
    {
        if (b == (byte)'\n') data.Add((byte)'\r');
        data.Add(b);
    }
    return data.ToArray();
}

static long Rss(string field = "VmRSS:")
{
    foreach (string line in File.ReadLines("/proc/self/status"))
        if (line.StartsWith(field, StringComparison.Ordinal))
            return long.Parse(line.AsSpan(field.Length).Trim().TrimEnd("kB").Trim()) * 1024;
    return 0;
}

static void RunReflow(Options opts)
{
    // Mixed content: the ascii workload plus long lines that wrap several times.
    var term = new Terminal(opts.Cols, opts.Rows, scrollbackLines: opts.Scrollback);
    term.Response = static _ => { };
    term.Feed(Workloads.All[0].Generate(Math.Max(4, opts.Scrollback / 2500) * 1024 * 1024, opts.Cols, opts.Rows));
    var sb = new StringBuilder();
    for (int i = 0; i < 2000; i++) sb.Append(new string((char)('a' + i % 26), 50 + i % 300)).Append("\r\n");
    term.Feed(Encoding.UTF8.GetBytes(sb.ToString()));
    term.Feed(Workloads.All[0].Generate(256 * 1024, opts.Cols, opts.Rows));

    // A divider drag: shrink by one column at a time, then grow back.
    var widths = new List<int>();
    for (int c = opts.Cols - 1; c >= opts.Cols / 2; c--) widths.Add(c);
    for (int c = opts.Cols / 2 + 1; c <= opts.Cols; c++) widths.Add(c);
    for (int i = 0; i < 3; i++) term.Resize(i % 2 == 0 ? opts.Cols / 2 : opts.Cols, opts.Rows); // warm-up

    var times = new double[widths.Count];
    for (int i = 0; i < widths.Count; i++)
    {
        long start = Stopwatch.GetTimestamp();
        term.Resize(widths[i], opts.Rows);
        times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    Array.Sort(times);
    Console.WriteLine($"reflow: {widths.Count} resizes {opts.Cols}->{opts.Cols / 2}->{opts.Cols} cols, " +
                      $"{term.ScrollbackCount} scrollback lines: median {times[times.Length / 2]:F2} ms, " +
                      $"p95 {times[(int)(times.Length * 0.95)]:F2} ms, max {times[^1]:F2} ms");
}

static Result RunThroughPty(string name, byte[] data, Options opts)
{
    string file = Path.Combine(Path.GetTempPath(), $"erikterm-bench-{Environment.ProcessId}.bin");
    File.WriteAllBytes(file, data);
    try
    {
        var term = new Terminal(opts.Cols, opts.Rows, scrollbackLines: 10_000);
        term.Response = static _ => { };
        var buffer = new byte[64 * 1024];
        var times = new double[opts.Iterations];
        long alloc = 0;
        int g0 = 0, g1 = 0, g2 = 0;

        // Allocations and GCs are counted around the read + parse loop only, like the in-process rows: the setup
        // (the Terminal and its 10k-line scrollback filling up in the warm-up, spawning `cat` each iteration) used to
        // fall inside the GC count window, so these rows showed "0 B" alloc/MB next to non-zero gc0/1/2.
        for (int i = -1; i < opts.Iterations; i++) // i == -1 is warm-up
        {
            using var pty = Pty.Spawn(new PtyOptions
            {
                Program = "cat",
                Arguments = ["cat", file],
                Columns = opts.Cols,
                Rows = opts.Rows,
            });
            int c0 = GC.CollectionCount(0), c1 = GC.CollectionCount(1), c2 = GC.CollectionCount(2);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            int n;
            while ((n = pty.Read(buffer)) > 0) term.Feed(buffer.AsSpan(0, n));
            double t = Stopwatch.GetElapsedTime(start).TotalSeconds;
            if (i >= 0)
            {
                times[i] = t;
                alloc += GC.GetAllocatedBytesForCurrentThread() - a0;
                g0 += GC.CollectionCount(0) - c0;
                g1 += GC.CollectionCount(1) - c1;
                g2 += GC.CollectionCount(2) - c2;
            }
            pty.WaitForExit();
        }
        return Result.From(name, data.Length, times, alloc, g0, g1, g2);
    }
    finally
    {
        File.Delete(file);
    }
}

static void Print(Result r) =>
    Console.WriteLine($"{r.Name,-14} {r.MedianMBps,9:F1} {r.BestMBps,9:F1} {r.MedianMs,9:F2} {FormatBytes(r.AllocPerMB),10} {r.Gc0 + "/" + r.Gc1 + "/" + r.Gc2,9}");

static string FormatBytes(double b) => b switch
{
    < 1024 => $"{b:F0} B",
    < 1024 * 1024 => $"{b / 1024:F1} KB",
    _ => $"{b / (1024 * 1024):F1} MB",
};

static void WriteJson(List<Result> results, Options opts)
{
    using var stream = File.Create(opts.Json!);
    using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
    w.WriteStartObject();
    w.WriteString("timestamp", DateTimeOffset.Now);
    w.WriteString("machine", Environment.MachineName);
    w.WriteString("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
    w.WriteString("size", $"{opts.Cols}x{opts.Rows}");
    w.WriteNumber("megabytes", opts.Megabytes);
    w.WriteStartArray("results");
    foreach (var r in results)
    {
        w.WriteStartObject();
        w.WriteString("name", r.Name);
        w.WriteNumber("medianMBps", Math.Round(r.MedianMBps, 2));
        w.WriteNumber("bestMBps", Math.Round(r.BestMBps, 2));
        w.WriteNumber("allocPerMB", Math.Round(r.AllocPerMB, 1));
        w.WriteNumber("gen0", r.Gc0);
        w.WriteEndObject();
    }
    w.WriteEndArray();
    w.WriteEndObject();
    Console.WriteLine($"\nwrote {opts.Json}");
}

static void CompareWithBaseline(List<Result> results, string path)
{
    using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
    var baseline = doc.RootElement.GetProperty("results").EnumerateArray()
        .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("medianMBps").GetDouble());

    Console.WriteLine($"\nvs baseline {path}:");
    foreach (var r in results)
    {
        if (!baseline.TryGetValue(r.Name, out double before)) continue;
        double delta = (r.MedianMBps - before) / before * 100;
        string flag = delta < -5 ? "  <-- slower" : delta > 5 ? "  faster" : "";
        Console.WriteLine($"  {r.Name,-20} {before,9:F1} -> {r.MedianMBps,9:F1} MB/s  {delta,+7:+0.0;-0.0}%{flag}");
    }
}

sealed record Result(string Name, double MedianMBps, double BestMBps, double MedianMs, double AllocPerMB, int Gc0, int Gc1, int Gc2)
{
    public static Result From(string name, int bytes, double[] seconds, long allocated, int g0, int g1, int g2)
    {
        var sorted = seconds.Order().ToArray();
        double median = sorted[sorted.Length / 2];
        double mb = bytes / (1024.0 * 1024.0);
        return new Result(name, mb / median, mb / sorted[0], median * 1000,
            allocated / (mb * seconds.Length), g0, g1, g2);
    }
}

sealed class Options
{
    public int Megabytes = 16;
    public int Iterations = 7;
    public int Cols = 120;
    public int Rows = 40;
    public HashSet<string>? Only;
    public bool Pty;
    public bool Reflow;
    public bool List;
    public string? Json;
    public string? Baseline;
    public string? Memory;
    public int Scrollback = 10_000;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--mb": o.Megabytes = int.Parse(Next()); break;
                case "--iters": o.Iterations = Math.Max(1, int.Parse(Next())); break;
                case "--cols": o.Cols = int.Parse(Next()); break;
                case "--rows": o.Rows = int.Parse(Next()); break;
                case "--only": o.Only = Next().Split(',').ToHashSet(); break;
                case "--pty": o.Pty = true; break;
                case "--reflow": o.Reflow = true; break;
                case "--list": o.List = true; break;
                case "--json": o.Json = Next(); break;
                case "--baseline": o.Baseline = Next(); break;
                case "--memory": o.Memory = Next(); break;
                case "--scrollback": o.Scrollback = Math.Max(0, int.Parse(Next())); break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }
        return o;
    }
}
