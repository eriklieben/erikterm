namespace Tidewatch;

public static class Units
{
    public const double FeetPerMetre = 3.28084;

    public static double ToFeet(double metres) => metres * FeetPerMetre;

    public static double ToMetres(double feet) => feet / FeetPerMetre;

    public static string FormatHeight(double metres, string unit = "m") => unit switch
    {
        "ft" => $"{ToFeet(metres):0.0} ft",
        _ => $"{metres:0.00} m",
    };
}
