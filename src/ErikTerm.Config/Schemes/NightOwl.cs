namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Night Owl (dark), from Night Owl (https://github.com/sdras/night-owl-vscode-theme).</summary>
    public static readonly ColorScheme NightOwl = BuiltIn(
        new("Night Owl", 0xD6DEEB, 0x011627, 0x80A4C2, 0x011627, 0x093B5E,
            [0x011627, 0xEF5350, 0x22DA6E, 0xC5E478, 0x82AAFF, 0xC792EA, 0x21C7A8, 0xFFFFFF],
            [0x575656, 0xEF5350, 0x22DA6E, 0xFFEB95, 0x82AAFF, 0xC792EA, 0x7FDBCA, 0xFFFFFF],
            Split: 0x093B5E, Accent: 0x82AAFF),
        new SchemeCredit("Night Owl", "Copyright (c) 2018 Sarah Drasner", SchemeLicenses.Mit, "https://github.com/sdras/night-owl-vscode-theme")
        {
            Notes = "The translucent selection is composited over the background.",
        });
}
