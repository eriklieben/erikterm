namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Catppuccin Frappé (dark), from Catppuccin for wezterm (https://github.com/catppuccin/wezterm).</summary>
    public static readonly ColorScheme CatppuccinFrappe = BuiltIn(
        new("Catppuccin Frappé", 0xC6D0F5, 0x303446, 0xF2D5CF, 0x232634, 0x626880,
            [0x51576D, 0xE78284, 0xA6D189, 0xE5C890, 0x8CAAEE, 0xF4B8E4, 0x81C8BE, 0xB5BFE2],
            [0x626880, 0xE78284, 0xA6D189, 0xE5C890, 0x8CAAEE, 0xF4B8E4, 0x81C8BE, 0xA5ADCE],
            Split: 0x737994, Accent: 0xBABBF1)
        {
            SelectionFg = 0xC6D0F5,
        },
        new SchemeCredit("Catppuccin for wezterm", "Copyright (c) 2021 Catppuccin", SchemeLicenses.Mit, "https://github.com/catppuccin/wezterm"));
}
