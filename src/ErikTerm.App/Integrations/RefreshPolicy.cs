namespace ErikTerm.App.Integrations;

/// <summary>How often a background status may refresh.</summary>
/// <param name="MinIntervalMs">Never start two runs closer than this, whatever asked for them.</param>
/// <param name="PeriodMs">Refresh unprompted after this long (0 = only when asked).</param>
/// <param name="BackoffMs">First retry delay after an error; doubles per consecutive error up to <paramref name="MaxBackoffMs"/>.</param>
/// <param name="TimeoutBackoffMs">After a timeout (a huge repository): leave it alone this long.</param>
/// <param name="RateLimitBackoffMs">After a rate-limit answer.</param>
internal readonly record struct RefreshTiming(int MinIntervalMs, int PeriodMs, int BackoffMs, int MaxBackoffMs, int TimeoutBackoffMs, int RateLimitBackoffMs)
{
    /// <summary>git status: cheap and local.</summary>
    public static readonly RefreshTiming Git = new(MinIntervalMs: 2_000, PeriodMs: 10_000, BackoffMs: 5_000, MaxBackoffMs: 60_000,
        TimeoutBackoffMs: 120_000, RateLimitBackoffMs: 60_000);

    /// <summary>gh: network calls against a rate-limited API.</summary>
    public static readonly RefreshTiming GitHub = new(MinIntervalMs: 20_000, PeriodMs: 180_000, BackoffMs: 30_000, MaxBackoffMs: 900_000,
        TimeoutBackoffMs: 120_000, RateLimitBackoffMs: 900_000);
}

internal enum RefreshOutcome : byte { Ok, Failed, TimedOut, RateLimited }

/// <summary>Per-key scheduling state (one repo, or one repo + branch).</summary>
internal struct RefreshState
{
    public long LastStart, LastDone;
    public long BlockedUntil;
    public bool InFlight, Wanted, Ever;
    public int Failures;
}

/// <summary>
/// The pure scheduling rules for background refreshes: events (cwd change, command end, focus) mark a key wanted;
/// a wanted key starts once <see cref="RefreshTiming.MinIntervalMs"/> passed since its last start, a periodic one
/// after <see cref="RefreshTiming.PeriodMs"/>; errors back off exponentially, timeouts and rate limits for longer.
/// </summary>
internal static class RefreshPolicy
{
    public static void Request(ref RefreshState s) => s.Wanted = true;

    /// <summary>Whether a run should start now. <paramref name="periodic"/>: the key is visible, so periodic refreshes apply.</summary>
    public static bool Due(in RefreshState s, long now, in RefreshTiming t, bool periodic)
    {
        if (s.InFlight || now < s.BlockedUntil) return false;
        if (!s.Ever) return s.Wanted || periodic;
        if (s.Wanted && now - s.LastStart >= t.MinIntervalMs) return true;
        return periodic && t.PeriodMs > 0 && now - s.LastDone >= t.PeriodMs && now - s.LastStart >= t.MinIntervalMs;
    }

    public static void Started(ref RefreshState s, long now)
    {
        s.InFlight = true;
        s.Wanted = false;
        s.Ever = true;
        s.LastStart = now;
    }

    public static void Completed(ref RefreshState s, long now, RefreshOutcome outcome, in RefreshTiming t)
    {
        s.InFlight = false;
        s.LastDone = now;
        switch (outcome)
        {
            case RefreshOutcome.Ok:
                s.Failures = 0;
                s.BlockedUntil = 0;
                break;
            case RefreshOutcome.Failed:
                s.Failures++;
                s.BlockedUntil = now + Backoff(s.Failures, t);
                break;
            case RefreshOutcome.TimedOut:
                s.Failures++;
                s.BlockedUntil = now + t.TimeoutBackoffMs;
                break;
            case RefreshOutcome.RateLimited:
                s.Failures++;
                s.BlockedUntil = now + Math.Max(t.RateLimitBackoffMs, Backoff(s.Failures, t));
                break;
        }
    }

    public static long Backoff(int failures, in RefreshTiming t)
    {
        if (failures <= 0) return 0;
        long ms = t.BackoffMs;
        for (int i = 1; i < failures && ms < t.MaxBackoffMs; i++) ms *= 2;
        return Math.Min(ms, t.MaxBackoffMs);
    }

    /// <summary>Milliseconds until <see cref="Due"/> could turn true for a wanted/periodic key (for the event loop's sleep).</summary>
    public static long MsUntilDue(in RefreshState s, long now, in RefreshTiming t, bool periodic)
    {
        if (s.InFlight) return long.MaxValue;
        long at = long.MaxValue;
        if (!s.Ever) at = s.Wanted || periodic ? now : long.MaxValue;
        else
        {
            if (s.Wanted) at = s.LastStart + t.MinIntervalMs;
            if (periodic && t.PeriodMs > 0) at = Math.Min(at, Math.Max(s.LastDone + t.PeriodMs, s.LastStart + t.MinIntervalMs));
        }
        if (at == long.MaxValue) return long.MaxValue;
        return Math.Max(0, Math.Max(at, s.BlockedUntil) - now);
    }
}
