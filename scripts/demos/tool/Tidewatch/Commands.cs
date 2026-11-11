namespace Tidewatch;

public static class Commands
{
    public static void PrintForecast(TextWriter output, Station station, DateTime start, int days, string unit)
    {
        output.WriteLine($"{station.Name} ({station.Id})");
        foreach (var (when, height, kind) in Forecast.Extremes(station, start, days))
            output.WriteLine($"  {when:ddd HH:mm}  {Label(kind)}  {Units.FormatHeight(height, unit)}");
    }

    public static void PrintChart(TextWriter output, Station station, DateTime start, int hours)
    {
        output.WriteLine($"\e[1m{station.Name}\e[0m ({station.Id})  ·  next {hours} hours");
        output.WriteLine();
        Kitty.WritePng(output, Chart.Render(station, start, hours));
        foreach (var (when, height, kind) in Forecast.Extremes(station, start, hours / 24).Take(6))
            output.WriteLine($"  {when:ddd HH:mm}  {Label(kind)}  {Units.FormatHeight(height)}");
    }

    private static string Label(TideKind kind) => kind == TideKind.High ? "\e[33mhigh\e[0m" : "\e[35mlow \e[0m";
}
