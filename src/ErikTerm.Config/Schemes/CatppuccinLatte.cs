namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Catppuccin Latte (light), from Catppuccin for wezterm (https://github.com/catppuccin/wezterm).</summary>
    public static readonly ColorScheme CatppuccinLatte = BuiltIn(
        new("Catppuccin Latte", 0x4C4F69, 0xEFF1F5, 0xDC8A78, 0xDCE0E8, 0xACB0BE,
            [0xBCC0CC, 0xD20F39, 0x40A02B, 0xDF8E1D, 0x1E66F5, 0xEA76CB, 0x179299, 0x5C5F77],
            [0xACB0BE, 0xD20F39, 0x40A02B, 0xDF8E1D, 0x1E66F5, 0xEA76CB, 0x179299, 0x6C6F85],
            Split: 0x9CA0B0, Accent: 0x8839EF)
        {
            SelectionFg = 0x4C4F69,
        },
        new SchemeCredit("Catppuccin for wezterm", "Copyright (c) 2021 Catppuccin", SchemeLicenses.Mit, "https://github.com/catppuccin/wezterm"));
}
