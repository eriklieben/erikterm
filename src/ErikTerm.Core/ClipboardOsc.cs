using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace ErikTerm.Core;

/// <summary>Which clipboard an OSC 52 write targets.</summary>
public enum ClipboardTarget : byte { Clipboard, Primary }

/// <summary>OSC 52 clipboard writes (reading the clipboard back is never allowed).</summary>
internal static class ClipboardOsc
{
    private const int MaxClipboardBytes = 1 << 20;

    /// <summary>
    /// OSC 52 ; Pc ; Pd — Pc selects c (clipboard), p/s (primary); Pd is base64, or ? to query (denied: it would leak the
    /// clipboard to any program). False for a query, bad base64 or more than 1 MB of text.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> arg, out string text, out bool clipboard, out bool primary)
    {
        text = "";
        clipboard = primary = false;
        int semi = arg.IndexOf((byte)';');
        if (semi < 0) return false;
        var targets = arg[..semi];
        var data = arg[(semi + 1)..];
        if (data.SequenceEqual("?"u8)) return false;

        byte[] decoded = ArrayPool<byte>.Shared.Rent(Base64.GetMaxDecodedFromUtf8Length(data.Length));
        try
        {
            if (Base64.DecodeFromUtf8(data, decoded, out _, out int written) != OperationStatus.Done) return false;
            if (written > MaxClipboardBytes) return false;
            text = Encoding.UTF8.GetString(decoded, 0, written);
            clipboard = targets.IsEmpty || targets.Contains((byte)'c');
            primary = targets.Contains((byte)'p') || targets.Contains((byte)'s');
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(decoded);
        }
    }
}
