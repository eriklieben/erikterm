namespace Tidewatch;

public enum TideKind { High, Low }

public readonly record struct Extreme(DateTime When, double Height, TideKind Kind);

public static class Forecast
{
    private static readonly DateTime Epoch = new(2026, 1, 1);

    /// <summary>Predicted water level at a moment, from the harmonic constituents.</summary>
    public static double HeightAt(Station station, DateTime when)
    {
        double hours = (when - Epoch).TotalHours;
        double level = station.Datum;
        foreach (var c in station.Constituents)
            level += c.Amplitude * Math.Cos(double.DegreesToRadians(c.Speed * hours - c.Phase));
        return level;
    }

    /// <summary>The extremes, highs and lows, between start and start + days.</summary>
    public static List<Extreme> Extremes(Station station, DateTime start, int days = 1, int stepMinutes = 6)
    {
        var step = TimeSpan.FromMinutes(stepMinutes);
        var found = new List<Extreme>();
        double prev = HeightAt(station, start - step), cur = HeightAt(station, start);
        for (var t = start; t < start.AddDays(days); t += step)
        {
            double next = HeightAt(station, t + step);
            if (cur > prev && cur >= next) found.Add(new Extreme(t, cur, TideKind.High));
            else if (cur < prev && cur <= next) found.Add(new Extreme(t, cur, TideKind.Low));
            (prev, cur) = (cur, next);
        }
        return found;
    }
}
