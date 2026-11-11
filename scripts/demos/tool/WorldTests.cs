namespace Demos;

/// <summary>The demo repository's TUnit tests (the stand-in dotnet test counts their [Test] attributes).</summary>
internal static class WorldTests
{
    public static readonly (string File, string Text)[] Committed =
    [
        ("UnitsTests.cs", """
            namespace Tidewatch.Tests;

            public class UnitsTests
            {
                [Test]
                public async Task ToFeet_converts_metres() => await Assert.That(Units.ToFeet(2)).IsEqualTo(6.56168);

                [Test]
                public async Task ToMetres_converts_feet() => await Assert.That(Units.ToMetres(3.28084)).IsEqualTo(1);

                [Test]
                public async Task Round_trip_keeps_the_height() =>
                    await Assert.That(Units.ToMetres(Units.ToFeet(1.23))).IsEqualTo(1.23).Within(1e-9);

                [Test]
                public async Task Metres_get_two_decimals() => await Assert.That(Units.FormatHeight(1.5)).IsEqualTo("1.50 m");

                [Test]
                public async Task Feet_get_one_decimal() => await Assert.That(Units.FormatHeight(1, "ft")).IsEqualTo("3.3 ft");

                [Test]
                public async Task Negative_heights_keep_their_sign() =>
                    await Assert.That(Units.FormatHeight(-0.25)).IsEqualTo("-0.25 m");
            }

            """),
        ("StationTests.cs", """"
            namespace Tidewatch.Tests;

            public class StationTests
            {
                private const string Sample = """
                    # a comment
                    id=1;name=Test Bay;lat=1.5;lon=-2.5;datum=0.5
                    M2 0.5 330 28.98
                    K1 0.3 106 15.04
                    """;

                [Test]
                public async Task Parses_the_header() => await Assert.That(Station.Parse(Sample).Name).IsEqualTo("Test Bay");

                [Test]
                public async Task Parses_the_constituents() =>
                    await Assert.That(Station.Parse(Sample).Constituents.Count).IsEqualTo(2);

                [Test]
                public async Task Skips_comments() => await Assert.That(Station.Parse(Sample).Id).IsEqualTo("1");

                [Test]
                public async Task Datum_defaults_to_zero() =>
                    await Assert.That(Station.Parse("id=2;name=X;lat=0;lon=0").Datum).IsEqualTo(0);

                [Test]
                public async Task A_short_line_fails() =>
                    await Assert.That(() => Station.Parse("id=3;name=X;lat=0;lon=0\nM2 0.5")).Throws<IndexOutOfRangeException>();
            }

            """"),
        ("ForecastTests.cs", """
            namespace Tidewatch.Tests;

            public class ForecastTests
            {
                private static readonly Station Bay = new("1", "Bay", 0, 0, 1.0, [new Constituent("M2", 0.5, 0, 28.9841042)]);
                private static readonly DateTime Start = new(2026, 3, 1);

                [Test]
                public async Task Height_repeats_every_period()
                {
                    // Arrange
                    var later = Start.AddHours(360 / 28.9841042);

                    // Act
                    double a = Forecast.HeightAt(Bay, Start), b = Forecast.HeightAt(Bay, later);

                    // Assert
                    await Assert.That(b).IsEqualTo(a).Within(1e-9);
                }

                [Test]
                public async Task Datum_offsets_the_level() =>
                    await Assert.That(Forecast.HeightAt(Bay with { Constituents = [] }, Start)).IsEqualTo(1.0);

                [Test]
                public async Task Finds_highs() =>
                    await Assert.That(Forecast.Extremes(Bay, Start).Where(e => e.Kind == TideKind.High)).IsNotEmpty();

                [Test]
                public async Task Finds_lows() =>
                    await Assert.That(Forecast.Extremes(Bay, Start).Where(e => e.Kind == TideKind.Low)).IsNotEmpty();

                [Test]
                public async Task Highs_and_lows_alternate()
                {
                    var kinds = Forecast.Extremes(Bay, Start, days: 3).Select(e => e.Kind).ToList();
                    await Assert.That(kinds.Zip(kinds.Skip(1)).All(p => p.First != p.Second)).IsTrue();
                }

                [Test]
                public async Task Two_highs_a_day() =>
                    await Assert.That(Forecast.Extremes(Bay, DateTime.Now.Date, days: 2).Count).IsEqualTo(8);

                [Test]
                public async Task A_smaller_step_finds_the_same_extremes() =>
                    await Assert.That(Forecast.Extremes(Bay, Start, stepMinutes: 1).Count).IsEqualTo(Forecast.Extremes(Bay, Start).Count);

                [Test]
                public async Task A_station_without_constituents_has_none() =>
                    await Assert.That(Forecast.Extremes(Bay with { Constituents = [] }, Start)).IsEmpty();
            }

            """),
        ("CommandsTests.cs", """
            namespace Tidewatch.Tests;

            public class CommandsTests
            {
                private static readonly Station Bay = new("7", "Bay", 0, 0, 1.0, [new Constituent("M2", 0.5, 0, 28.9841042)]);

                private static string Forecast(int days = 1, string unit = "m")
                {
                    var output = new StringWriter();
                    Commands.PrintForecast(output, Bay, new DateTime(2026, 3, 1), days, unit);
                    return output.ToString();
                }

                [Test]
                public async Task Prints_the_station() => await Assert.That(Forecast()).StartsWith("Bay (7)");

                [Test]
                public async Task Days_prints_more_extremes() =>
                    await Assert.That(Forecast(days: 2).Split('\n').Length).IsGreaterThan(Forecast().Split('\n').Length);

                [Test]
                public async Task Feet_prints_feet() => await Assert.That(Forecast(unit: "ft")).Contains(" ft");

                [Test]
                public async Task Metres_by_default() => await Assert.That(Forecast()).Contains(" m");
            }

            """),
    ];

    public const string Moon = """
        namespace Tidewatch.Tests;

        public class MoonTests
        {
            [Test]
            public async Task Full_moon_in_february() =>
                await Assert.That(Tidewatch.Moon.PhaseName(new DateTime(2026, 2, 1, 22, 0, 0))).IsEqualTo("full moon");
        }

        """;
}
