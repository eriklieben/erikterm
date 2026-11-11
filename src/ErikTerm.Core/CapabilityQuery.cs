using System.Globalization;
using System.Text;

namespace ErikTerm.Core;

/// <summary><c>DCS + q hex(name);hex(name)… ST</c> (XTGETTCAP): one reply per name, from <see cref="TermCapabilities"/>.</summary>
internal static class CapabilityQuery
{
    public static void Answer(ReadOnlySpan<byte> body, string terminalName, TerminalReplies replies)
    {
        while (true)
        {
            int semi = body.IndexOf((byte)';');
            var hex = semi < 0 ? body : body[..semi];
            if (!hex.IsEmpty) replies.Send(Reply(hex, terminalName));
            if (semi < 0) break;
            body = body[(semi + 1)..];
        }
    }

    private static string Reply(ReadOnlySpan<byte> hex, string terminalName)
    {
        var sb = new StringBuilder("\eP");
        string? name = HexDecode(hex);
        if (name is not null && TermCapabilities.Lookup(name, out var cap))
        {
            sb.Append("1+r").Append(HexEncode(name));
            if (TermCapabilities.ReplyValue(cap, terminalName) is { } value) sb.Append('=').Append(HexEncode(value));
        }
        else
        {
            sb.Append("0+r");
            foreach (byte b in hex) if (Uri.IsHexDigit((char)b)) sb.Append((char)b); // echo the (valid hex part of the) name, as kitty does
        }
        sb.Append("\e\\");
        return sb.ToString();
    }

    private static string? HexDecode(ReadOnlySpan<byte> hex)
    {
        if (hex.Length % 2 != 0 || hex.Length > 128) return null;
        var chars = new char[hex.Length / 2];
        for (int i = 0; i < chars.Length; i++)
        {
            int hi = XColorSpec.HexValue(hex[2 * i]), lo = XColorSpec.HexValue(hex[2 * i + 1]);
            if (hi < 0 || lo < 0) return null;
            int c = hi << 4 | lo;
            if (c is < 0x21 or > 0x7E) return null;
            chars[i] = (char)c;
        }
        return new string(chars);
    }

    private static string HexEncode(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (char c in s) sb.Append(((int)c & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
