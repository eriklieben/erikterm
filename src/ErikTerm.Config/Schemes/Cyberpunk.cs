namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Cyberpunk (dark), from cyberpunk-iterm (https://github.com/Murderlon/cyberpunk-iterm).</summary>
    public static readonly ColorScheme Cyberpunk = BuiltIn(
        new("Cyberpunk", 0xE5E5E5, 0x332A57, 0x21F6BC, 0xFFFFFF, 0xC1DEFF,
            [0x000000, 0xFF7092, 0x00FBAC, 0xFFFA6A, 0x00BFFF, 0xDF95FF, 0x86CBFE, 0xFFFFFF],
            [0x000000, 0xFF8AA4, 0x21F6BC, 0xFFF787, 0x1BCCFD, 0xE6AEFE, 0x99D6FC, 0xFFFFFF],
            Split: 0xC1DEFF, Accent: 0xFF7092)
        {
            SelectionFg = 0x000000,
        },
        new SchemeCredit("cyberpunk-iterm", "Copyright (c) Merlijn Vos (Murderlon)", SchemeLicenses.Mit, "https://github.com/Murderlon/cyberpunk-iterm")
        {
            Notes = "MIT as stated in its README.",
        });
}
