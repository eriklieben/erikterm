// Fuzz targets for everything erikterm reads from programs and files: bytes a program writes to the terminal, the
// images it sends, and the settings file. libFuzzer (through SharpFuzz) generates the inputs; an exception that is not
// the target's own "bad input" error, a hang or a runaway allocation is a finding.
//
//   dotnet run -c Release --project fuzz/ErikTerm.Fuzz -- <target> --repro <file>   one input, no fuzzer
//   fuzz/run.sh <target> [seconds]                                                     instrument and fuzz
//
// Targets: terminal (Terminal.Feed: CSI, OSC, DCS, APC, kitty graphics, sixel, iTerm2 images, synchronized output,
// then a resize and the text read back), png, gif, jpeg, sixel, toml.
//
// The image decoders are called below their catch-all wrappers (ImageDecoder.Decode, Sixel.Decode turn any exception
// into ImageDecodeException, and KittyGraphics replies with it), so a bug those wrappers would hide still shows here.
using System.Reflection;
using System.Text;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Core.Images;
using SharpFuzz;

namespace ErikTerm.Fuzz;

public static class Program
{
    private delegate DecodedImage SpanDecoder(ReadOnlySpan<byte> data);

    public static int Main(string[] args)
    {
        if (args.Length == 0 || Target(args[0]) is not { } target)
        {
            Console.Error.WriteLine("usage: ErikTerm.Fuzz <terminal|png|gif|jpeg|sixel|toml> [--repro <file>...]");
            return 2;
        }
        if (args.Length > 1 && args[1] == "--repro")
        {
            foreach (var file in args.Skip(2)) target(File.ReadAllBytes(file));
            Console.WriteLine("no failure");
            return 0;
        }
        Fuzzer.LibFuzzer.Run(span => target(span));
        return 0;
    }

    private static ReadOnlySpanAction? Target(string name) => name switch
    {
        "terminal" => FeedTerminal,
        "png" => data => Expect<ImageDecodeException>(data, static d => Png.Decode(d)),
        "gif" => data => Expect<ImageDecodeException>(data, static d => Gif.Decode(d)),
        "jpeg" => data => Expect<ImageDecodeException>(data, static d => Jpeg.Decode(d)),
        "sixel" => data => Expect<ImageDecodeException>(data, SixelCore),
        "toml" => ParseToml,
        _ => null,
    };

    /// <summary>Runs <paramref name="decode"/>; its own error type is a fine answer to bad input, anything else is a finding.</summary>
    private static void Expect<TError>(ReadOnlySpan<byte> data, SpanDecoder decode) where TError : Exception
    {
        try
        {
            var image = decode(data);
            if (image.Width <= 0 || image.Height <= 0 || image.Rgba.Length != (long)image.Width * image.Height * 4)
                throw new InvalidOperationException($"inconsistent image {image.Width}x{image.Height}, {image.Rgba.Length} bytes");
        }
        catch (TError) { }
    }

    // Sixel.Decode catches everything; DecodeCore is what it wraps.
    private static readonly SpanDecoder SixelCore =
        typeof(Sixel).GetMethod("DecodeCore", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<SpanDecoder>();

    private static void ParseToml(ReadOnlySpan<byte> data)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { return; } // the settings loader reads UTF-8; invalid UTF-8 never gets here
        try { Toml.Parse(text); }
        catch (TomlException) { }
    }

    private static ReadOnlySpan<byte> SyncEnd => "\e[?2026l"u8;

    /// <summary>
    /// The first three bytes pick the size and where the output is split into two reads (a sequence cut in half at a
    /// read boundary is the parser's hardest case); the rest is what the program wrote.
    /// </summary>
    private static void FeedTerminal(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3) return;
        int cols = 1 + data[0] % 120, rows = 1 + data[1] % 40;
        var output = data[3..];
        int split = output.IsEmpty ? 0 : data[2] * output.Length / 256;

        var term = new Terminal(cols, rows, scrollbackLines: 200);
        term.Feed(output[..split]);
        term.Feed(output[split..]);
        term.Feed(SyncEnd); // let output held for a synchronized update through

        term.Resize(1 + data[1] % 120, 1 + data[0] % 40);
        for (int y = 0; y < term.Rows; y++) term.GetLineText(y);
        term.ExportStyledLines(50);
    }
}
