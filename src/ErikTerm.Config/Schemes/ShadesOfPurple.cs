namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Shades of Purple (dark), from Shades of Purple (https://github.com/ahmadawais/shades-of-purple-vscode).</summary>
    public static readonly ColorScheme ShadesOfPurple = BuiltIn(
        new("Shades of Purple", 0xFFFFFF, 0x1E1E3F, 0xFAD000, 0x1E1E3F, 0x6D42A5,
            [0x000000, 0xE43937, 0x3AD900, 0xFAD000, 0x7857FE, 0xFF2C70, 0x80FCFF, 0xFFFFFF],
            [0x5C5C61, 0xE43937, 0x3AD900, 0xFAD000, 0x6943FF, 0xFB94FF, 0x80FCFF, 0xFFFFFF],
            Split: 0x6D42A5, Accent: 0xFAD000),
        new SchemeCredit("Shades of Purple", "Copyright (c) 2015-present Ahmad Awais", SchemeLicenses.Mit, "https://github.com/ahmadawais/shades-of-purple-vscode")
        {
            Notes = "Its license adds \"Any thing you build with this should also be MIT licensed\"; erikterm is. The slightly translucent red is composited over the background.",
        });
}
