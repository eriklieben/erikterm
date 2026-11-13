namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Gruvbox Light (light), from gruvbox (https://github.com/morhetz/gruvbox).</summary>
    public static readonly ColorScheme GruvboxLight = BuiltIn(
        new("Gruvbox Light", 0x3C3836, 0xFBF1C7, 0x3C3836, 0xFBF1C7, 0xBDAE93,
            [0xFBF1C7, 0xCC241D, 0x98971A, 0xD79921, 0x458588, 0xB16286, 0x689D6A, 0x7C6F64],
            [0x928374, 0x9D0006, 0x79740E, 0xB57614, 0x076678, 0x8F3F71, 0x427B58, 0x3C3836],
            Split: 0xBDAE93, Accent: 0x076678),
        new SchemeCredit("gruvbox", "By Pavel Pertsev (morhetz)", SchemeLicenses.Mit, "https://github.com/morhetz/gruvbox")
        {
            Notes = "MIT as stated in its README and package.json. Colours from colors/gruvbox.vim (light, medium contrast).",
        });
}
