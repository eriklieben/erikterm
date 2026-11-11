using System.Diagnostics;

namespace ErikTerm.Tests;

/// <summary>
/// Polling for things that happen on another thread or in another process (file watchers, a child's exec).
/// The deadline is generous on purpose: polling returns as soon as the condition holds, so a long deadline costs
/// nothing when things work and only keeps a loaded machine from turning into a failure.
/// </summary>
internal static class Eventually
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>True once <paramref name="condition"/> holds; false if it still doesn't after <paramref name="timeout"/>.</summary>
    public static async Task<bool> True(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > limit) return condition();
            await Task.Delay(10);
        }
        return true;
    }

    /// <summary>The first value of <paramref name="read"/> that satisfies <paramref name="done"/>, or the last value read at the deadline.</summary>
    public static async Task<T> Value<T>(Func<T> read, Func<T, bool> done, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var sw = Stopwatch.StartNew();
        T value;
        while (!done(value = read()))
        {
            if (sw.Elapsed > limit) return value;
            await Task.Delay(10);
        }
        return value;
    }
}
