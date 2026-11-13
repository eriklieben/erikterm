namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Dracula (dark), from Dracula for wezterm (https://github.com/dracula/wezterm).</summary>
    public static readonly ColorScheme Dracula = BuiltIn(
        new("Dracula", 0xF8F8F2, 0x282A36, 0xF8F8F2, 0x282A36, 0x44475A,
            [0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C, 0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2],
            [0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5, 0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF],
            Split: 0x6272A4, Accent: 0xBD93F9),
        new SchemeCredit("Dracula for wezterm", "Copyright (c) 2022 Dracula Theme", SchemeLicenses.Mit, "https://github.com/dracula/wezterm"));
}
