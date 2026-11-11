using System.Text;

namespace ErikTerm.Core;

/// <summary>
/// Writes cell attributes back out as SGR parameters: for styled text exports (session restore, <c>get-text
/// --escapes</c>) and for the DECRQSS reply to <c>DCS $ q m ST</c>. One place, so every export carries the same
/// attributes (underline styles, underline color, overline included).
/// </summary>
public static class SgrText
{
    /// <summary>
    /// Appends "0" and then <c>;param</c> for every attribute and color (no CSI, no final <c>m</c>).
    /// <paramref name="colon"/> writes extended colors in the ISO 8613-6 colon form (<c>38:2::r:g:b</c>, <c>38:5:n</c>)
    /// that DECRQSS replies use; otherwise the widely understood <c>38;2;r;g;b</c>. Styled underlines and the
    /// underline color are always colon / 58 forms (there is no other).
    /// </summary>
    public static void AppendParams(StringBuilder sb, uint fg, uint bg, CellAttributes flags, uint underlineColor, bool colon)
    {
        sb.Append('0');
        if ((flags & CellAttributes.Bold) != 0) sb.Append(";1");
        if ((flags & CellAttributes.Dim) != 0) sb.Append(";2");
        if ((flags & CellAttributes.Italic) != 0) sb.Append(";3");
        switch (CellStyle.Underline(flags))
        {
            case UnderlineStyle.Single: sb.Append(";4"); break;
            case UnderlineStyle.Double: sb.Append(";4:2"); break;
            case UnderlineStyle.Curly: sb.Append(";4:3"); break;
            case UnderlineStyle.Dotted: sb.Append(";4:4"); break;
            case UnderlineStyle.Dashed: sb.Append(";4:5"); break;
        }
        if ((flags & CellAttributes.Blink) != 0) sb.Append(";5");
        if ((flags & CellAttributes.Inverse) != 0) sb.Append(";7");
        if ((flags & CellAttributes.Hidden) != 0) sb.Append(";8");
        if ((flags & CellAttributes.Strike) != 0) sb.Append(";9");
        if ((flags & CellAttributes.Overline) != 0) sb.Append(";53");
        AppendColor(sb, fg, 30, 90, 38, colon);
        AppendColor(sb, bg, 40, 100, 48, colon);
        if ((flags & CellAttributes.Underline) != 0 && !TermColor.IsDefault(underlineColor))
            AppendColor(sb, underlineColor, -1, -1, 58, colon: true);
    }

    /// <summary>A complete <c>ESC [ 0 … m</c> that sets exactly these attributes.</summary>
    public static string Sequence(uint fg, uint bg, CellAttributes flags, uint underlineColor = 0)
    {
        var sb = new StringBuilder("\e[");
        AppendParams(sb, fg, bg, flags, underlineColor, colon: false);
        return sb.Append('m').ToString();
    }

    private static void AppendColor(StringBuilder sb, uint color, int basic, int bright, int extended, bool colon)
    {
        char sep = colon ? ':' : ';';
        if (TermColor.IsIndexed(color))
        {
            int i = TermColor.Index(color);
            if (i < 8 && basic >= 0) sb.Append(';').Append(basic + i);
            else if (i < 16 && bright >= 0) sb.Append(';').Append(bright + i - 8);
            else sb.Append(';').Append(extended).Append(sep).Append('5').Append(sep).Append(i);
        }
        else if (TermColor.IsRgb(color))
        {
            sb.Append(';').Append(extended).Append(sep).Append('2').Append(sep);
            if (colon) sb.Append(':'); // empty color-space id: 38:2::r:g:b
            sb.Append((color >> 16) & 0xFF).Append(sep).Append((color >> 8) & 0xFF).Append(sep).Append(color & 0xFF);
        }
    }
}
