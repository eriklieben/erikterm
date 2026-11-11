using System.Globalization;

namespace Tidewatch;

/// <summary>One harmonic constituent: amplitude in metres, phase in degrees, speed in degrees per hour.</summary>
public sealed record Constituent(string Name, double Amplitude, double Phase, double Speed);

public sealed record Station(string Id, string Name, double Lat, double Lon, double Datum,
    IReadOnlyList<Constituent> Constituents)
{
    public static Station Parse(string text)
    {
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
        var header = lines[0].Split(';')
            .Select(part => part.Split('=', 2))
            .ToDictionary(kv => kv[0], kv => kv[1]);
        return new Station(header["id"], header["name"], Number(header["lat"]), Number(header["lon"]),
            header.TryGetValue("datum", out var datum) ? Number(datum) : 0,
            lines.Skip(1).Select(ParseConstituent).ToList());
    }

    private static Constituent ParseConstituent(string line)
    {
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new Constituent(f[0], Number(f[1]), Number(f[2]), Number(f[3]));
    }

    private static double Number(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
