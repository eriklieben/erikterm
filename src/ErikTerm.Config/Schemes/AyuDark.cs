namespace ErikTerm.Config;

public static partial class ColorSchemes
{
    /// <summary>Ayu Dark (dark), from ayu for VS Code (https://github.com/ayu-theme/vscode-ayu).</summary>
    public static readonly ColorScheme AyuDark = BuiltIn(
        new("Ayu Dark", 0xBEBCB5, 0x0D1017, 0xE6B450, 0x0D1017, 0x172E51,
            [0x1B1F29, 0xF06B73, 0x70BF56, 0xFDB04C, 0x4FBFFF, 0xD0A1FF, 0x93E2C8, 0xC7C7C7],
            [0x686868, 0xF07178, 0xAAD94C, 0xFFB454, 0x59C2FF, 0xD2A6FF, 0x95E6CB, 0xFFFFFF],
            Split: 0x172E51, Accent: 0xE6B450),
        new SchemeCredit("ayu for VS Code", "Copyright (c) 2016 Ike Kurghinyan", SchemeLicenses.Mit, "https://github.com/ayu-theme/vscode-ayu")
        {
            Notes = "The translucent text and selection colours are composited over the background.",
        });
}
