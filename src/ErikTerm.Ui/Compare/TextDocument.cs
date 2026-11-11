using System.Text;

namespace ErikTerm.Ui.Compare;

/// <summary>How a document's bytes map to text; saving uses the same mapping so bytes round-trip exactly.</summary>
public enum TextCodec { Utf8, Utf8Bom, Latin1, Utf16Le, Utf16Be }

/// <summary>Why a side can't be shown as text.</summary>
public enum DocumentProblem { None, Binary, TooLarge }

/// <summary>A file's content split into lines, each kept with its line ending so a merge writes the same bytes back.</summary>
public sealed class TextDocument
{
    /// <summary>Bigger than this is shown as a message instead of being compared.</summary>
    public const long MaxBytes = 128 * 1024 * 1024;

    private const int BinaryProbeBytes = 8000;

    public static readonly TextDocument Empty = new([], TextCodec.Utf8, DocumentProblem.None, 0);

    private TextDocument(string[] rawLines, TextCodec codec, DocumentProblem problem, long byteLength)
    {
        RawLines = rawLines;
        Codec = codec;
        Problem = problem;
        ByteLength = byteLength;
    }

    /// <summary>Every line with its line ending ("\n", "\r\n", or none on an unterminated last line).</summary>
    public string[] RawLines { get; }
    public TextCodec Codec { get; }
    public DocumentProblem Problem { get; }
    public long ByteLength { get; }

    public static TextDocument TooLarge(long byteLength) => new([], TextCodec.Utf8, DocumentProblem.TooLarge, byteLength);

    /// <summary>Decodes bytes; NUL bytes near the start without a UTF-16 BOM make it binary.</summary>
    public static TextDocument Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes) return TooLarge(bytes.Length);
        if (bytes.StartsWith<byte>([0xFF, 0xFE])) return FromText(Encoding.Unicode.GetString(bytes[2..]), TextCodec.Utf16Le, bytes.Length);
        if (bytes.StartsWith<byte>([0xFE, 0xFF])) return FromText(Encoding.BigEndianUnicode.GetString(bytes[2..]), TextCodec.Utf16Be, bytes.Length);
        if (bytes[..Math.Min(bytes.Length, BinaryProbeBytes)].IndexOf((byte)0) >= 0) return new TextDocument([], TextCodec.Utf8, DocumentProblem.Binary, bytes.Length);
        bool bom = bytes.StartsWith<byte>([0xEF, 0xBB, 0xBF]);
        if (bom) bytes = bytes[3..];
        // Strict UTF-8 first (never lossy); anything else is kept byte-for-byte as Latin-1.
        try
        {
            return FromText(StrictUtf8.GetString(bytes), bom ? TextCodec.Utf8Bom : TextCodec.Utf8, bytes.Length);
        }
        catch (DecoderFallbackException)
        {
            return FromText(Encoding.Latin1.GetString(bytes), TextCodec.Latin1, bytes.Length);
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static TextDocument FromText(string text, TextCodec codec = TextCodec.Utf8, long byteLength = -1) =>
        new(SplitLines(text), codec, DocumentProblem.None, byteLength < 0 ? text.Length : byteLength);

    /// <summary>Lines keeping their terminators; a lone CR stays inside its line, as in git.</summary>
    public static string[] SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
        {
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return [.. lines];
    }

    /// <summary>The line without its terminator.</summary>
    public static ReadOnlySpan<char> Content(string rawLine) => rawLine.AsSpan().TrimEnd("\r\n");

    public static string ContentString(string rawLine)
    {
        int len = Content(rawLine).Length;
        return len == rawLine.Length ? rawLine : rawLine[..len];
    }

    /// <summary>The bytes of <paramref name="text"/> in this codec, BOM included.</summary>
    public static byte[] Encode(string text, TextCodec codec) => codec switch
    {
        TextCodec.Latin1 => Encoding.Latin1.GetBytes(text),
        TextCodec.Utf8Bom => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)],
        TextCodec.Utf16Le => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)],
        TextCodec.Utf16Be => [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)],
        _ => Encoding.UTF8.GetBytes(text),
    };
}
