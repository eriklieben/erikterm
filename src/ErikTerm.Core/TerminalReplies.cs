namespace ErikTerm.Core;

/// <summary>
/// The terminal's replies to the program (DA, DSR, OSC color queries, kitty graphics, …). Every reply goes through
/// <see cref="SanitizeReply"/>: whatever ends up in a reply, it can never carry control characters the shell's line
/// editor would act on.
/// </summary>
internal sealed class TerminalReplies
{
    /// <summary>Where replies go (<see cref="Terminal.Response"/>); null drops them.</summary>
    public ResponseWriter? Writer { get; set; }

    public void Send(ReadOnlySpan<byte> data)
    {
        if (Writer is not { } response) return;
        if (IsCleanReply(data)) response(data);
        else response(SanitizeReply(data));
    }

    /// <summary>Sends an ASCII reply.</summary>
    public void Send(string text) => Send(System.Text.Encoding.ASCII.GetBytes(text));

    /// <summary>
    /// True for a reply that is printable ASCII apart from the ESC that starts it and the ESC of a trailing
    /// ST (<c>ESC \</c>) or the BEL ending an OSC reply: the shape of every reply the terminal makes.
    /// </summary>
    internal static bool IsCleanReply(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b is >= 0x20 and < 0x7F) continue;
            if (IsFraming(data, i)) continue;
            return false;
        }
        return true;
    }

    /// <summary>The reply's own framing: its first ESC, a trailing ST's ESC, or the BEL that ends an OSC reply.</summary>
    private static bool IsFraming(ReadOnlySpan<byte> data, int i) => data[i] switch
    {
        0x1B => i == 0 || (i == data.Length - 2 && data[^1] == (byte)'\\'),
        0x07 => i == data.Length - 1 && data.Length > 2 && data[0] == 0x1B && data[1] == (byte)']',
        _ => false,
    };

    /// <summary>
    /// Drops C0 controls, DEL, C1 controls (as bytes 0x80–0x9F and as U+0080–U+009F) and any ESC other than the
    /// reply's own framing; other UTF-8 text is kept, invalid UTF-8 is dropped.
    /// </summary>
    internal static byte[] SanitizeReply(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        Span<byte> utf8 = stackalloc byte[4];
        int i = 0;
        while (i < data.Length)
        {
            byte b = data[i];
            if (b < 0x80)
            {
                if (b is >= 0x20 and < 0x7F || IsFraming(data, i)) output.Add(b);
                i++;
                continue;
            }
            if (System.Text.Rune.DecodeFromUtf8(data[i..], out var rune, out int used) == System.Buffers.OperationStatus.Done
                && rune.Value >= 0xA0)
            {
                rune.EncodeToUtf8(utf8);
                for (int k = 0; k < used; k++) output.Add(utf8[k]);
            }
            i += Math.Max(1, used);
        }
        return output.ToArray();
    }
}
