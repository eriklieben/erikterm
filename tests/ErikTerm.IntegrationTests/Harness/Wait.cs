namespace ErikTerm.IntegrationTests.Harness;

internal static class Wait
{
    /// <summary>Polls until <paramref name="condition"/> holds; a TimeoutException naming <paramref name="what"/> after the deadline.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"never: {what}");
            await Task.Delay(200);
        }
    }
}
