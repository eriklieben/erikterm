namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Catppuccin Macchiato (dark), from Catppuccin (https://github.com/catppuccin/catppuccin).</summary>
    public static readonly ColorScheme CatppuccinMacchiato = BuiltIn(
        new("Catppuccin Macchiato", 0xCAD3F5, 0x24273A, 0xF4DBD6, 0x24273A, 0x5B6078,
            [0x494D64, 0xED8796, 0xA6DA95, 0xEED49F, 0x8AADF4, 0xF5BDE6, 0x8BD5CA, 0xB8C0E0],
            [0x5B6078, 0xED8796, 0xA6DA95, 0xEED49F, 0x8AADF4, 0xF5BDE6, 0x8BD5CA, 0xA5ADCB],
            Split: 0x494D64, Accent: 0xB7BDF8),
        new SchemeCredit("Catppuccin", "Copyright (c) 2021 Catppuccin", SchemeLicenses.Mit, "https://github.com/catppuccin/catppuccin"));
}
