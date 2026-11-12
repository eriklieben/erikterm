namespace ErikTerm.IntegrationTests.Harness;

/// <summary>
/// How many erikterm processes run at once. Each renders with the software renderer on one thread, and the debug
/// script's steps are timed, so too many at once on a small machine (a CI runner has 4 cores) make them flaky.
/// ERIKTERM_IT_PARALLEL overrides it.
/// </summary>
public sealed class ScenarioLimit : TUnit.Core.Interfaces.IParallelLimit
{
    public int Limit { get; } = int.TryParse(Environment.GetEnvironmentVariable("ERIKTERM_IT_PARALLEL"), out int n) && n > 0
        ? n : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
}

/// <summary>Checks every scenario ends with.</summary>
internal static class Checks
{
    /// <summary>erikterm exited by itself with status 0, wrote no crash log and logged no exception or internal error.</summary>
    public static async Task CleanExitAsync(AppRun run, int timeoutMs = 60_000)
    {
        int code = await run.WaitForExitAsync(timeoutMs);
        await Assert.That(code).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.CrashLogs).IsEmpty().Because(string.Join("\n", run.CrashLogs));
        await Assert.That(run.ProblemLines).IsEmpty().Because(run.Output);
    }

    /// <summary>The screenshot exists and shows a rendered frame (not a blank or single-color surface).</summary>
    public static async Task<Bmp> RenderedAsync(AppRun run, string name)
    {
        var bmp = Bmp.Load(await run.WaitForFileAsync(name));
        await Assert.That(bmp.Width).IsGreaterThan(100);
        await Assert.That(bmp.Height).IsGreaterThan(100);
        await Assert.That(bmp.LooksRendered).IsTrue().Because($"{name}: {bmp.DistinctColors} colors, {bmp.DominantShare:P1} one color");
        return bmp;
    }

    /// <summary>Two frames differ in at least <paramref name="share"/> of their pixels (an overlay opened, a split appeared).</summary>
    public static async Task DifferAsync(Bmp a, Bmp b, double share = 0.002, string because = "")
    {
        double d = a.DifferenceFrom(b);
        await Assert.That(d).IsGreaterThanOrEqualTo(share).Because($"{because}: only {d:P2} of the pixels changed");
    }
}
