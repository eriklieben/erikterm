namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Catppuccin Mocha (dark), from Catppuccin (https://github.com/catppuccin/catppuccin).</summary>
    public static readonly ColorScheme CatppuccinMocha = BuiltIn(
        new("Catppuccin Mocha", 0xCDD6F4, 0x1E1E2E, 0xF5E0DC, 0x1E1E2E, 0x585B70,
            [0x45475A, 0xF38BA8, 0xA6E3A1, 0xF9E2AF, 0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xBAC2DE],
            [0x585B70, 0xF38BA8, 0xA6E3A1, 0xF9E2AF, 0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xA6ADC8],
            Split: 0x45475A, Accent: 0xB4BEFE),
        new SchemeCredit("Catppuccin", "Copyright (c) 2021 Catppuccin", SchemeLicenses.Mit, "https://github.com/catppuccin/catppuccin"));
}
