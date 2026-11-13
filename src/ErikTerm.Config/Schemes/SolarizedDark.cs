namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Solarized Dark (dark), from Solarized (https://github.com/altercation/solarized).</summary>
    public static readonly ColorScheme SolarizedDark = BuiltIn(
        new("Solarized Dark", 0x839496, 0x002B36, 0x839496, 0x002B36, 0x073642,
            [0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5],
            [0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3],
            Split: 0x1A4049, Accent: 0x268BD2)
        {
            SelectionFg = 0x93A1A1,
        },
        new SchemeCredit("Solarized", "Copyright (c) 2011 Ethan Schoonover", SchemeLicenses.Mit, "https://github.com/altercation/solarized"));
}
