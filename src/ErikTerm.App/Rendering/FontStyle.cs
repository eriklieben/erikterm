namespace ErikTerm.App.Rendering;

public enum FontStyle { Regular = 0, Bold = 1, Italic = 2, BoldItalic = 3 }

internal static class FontStyles
{
    public static FontStyle Of(bool bold, bool italic) => (FontStyle)((bold ? 1 : 0) | (italic ? 2 : 0));
}
