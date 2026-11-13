namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Gruvbox Dark (dark), from gruvbox (https://github.com/morhetz/gruvbox).</summary>
    public static readonly ColorScheme GruvboxDark = BuiltIn(
        new("Gruvbox Dark", 0xEBDBB2, 0x282828, 0xEBDBB2, 0x282828, 0x665C54,
            [0x282828, 0xCC241D, 0x98971A, 0xD79921, 0x458588, 0xB16286, 0x689D6A, 0xA89984],
            [0x928374, 0xFB4934, 0xB8BB26, 0xFABD2F, 0x83A598, 0xD3869B, 0x8EC07C, 0xEBDBB2],
            Split: 0x665C54, Accent: 0xFABD2F),
        new SchemeCredit("gruvbox", "By Pavel Pertsev (morhetz)", SchemeLicenses.Mit, "https://github.com/morhetz/gruvbox")
        {
            Notes = "MIT as stated in its README and package.json. Colours from colors/gruvbox.vim (medium contrast).",
        });
}
