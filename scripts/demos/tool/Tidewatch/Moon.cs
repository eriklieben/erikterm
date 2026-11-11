namespace Tidewatch;

public static class Moon
{
    public const double SynodicMonth = 29.530588853;

    private static readonly DateTime NewMoon =
        new(2026, 1, 18, 19, 52, 0);

    private static readonly string[] Names =
    [
        "new moon", "waxing crescent",
        "first quarter", "waxing gibbous",
        "full moon", "waning gibbous",
        "last quarter", "waning crescent",
    ];

    /// <summary>
    /// Days since the last new moon.
    /// </summary>
    public static double Age(DateTime when)
    {
        double days = (when - NewMoon).TotalDays;
        return (days % SynodicMonth + SynodicMonth)
            % SynodicMonth;
    }

    public static string PhaseName(DateTime when)
    {
        double lunation = Age(when) / SynodicMonth;
        return Names[(int)(lunation * 8 + 0.5) % 8];
    }

    /// <summary>
    /// Spring tides come a day or two after new
    /// and full moon.
    /// </summary>
    public static bool IsSpringTide(DateTime when)
    {
        double age = Age(when);
        return Math.Min(Math.Abs(age - 1.5),
            Math.Abs(age - 16.3)) < 2;
    }
}
