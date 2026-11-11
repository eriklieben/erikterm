using System.Text;

namespace ErikTerm.Bench;

/// <summary>Deterministic synthetic terminal output modelled on real programs.</summary>
public static class Workloads
{
    public sealed record Workload(string Name, string Description, Func<int, int, int, byte[]> Generate);

    public static readonly Workload[] All =
    [
        new("ascii", "plain text lines (cat of a log/source file)", Ascii),
        new("sgr-256", "256-color words, e.g. ls --color / compiler output", Sgr256),
        new("sgr-truecolor", "24-bit fg+bg runs, e.g. bat / delta / syntax highlighting", SgrTrueColor),
        new("unicode", "accents, greek, box drawing, CJK (wide), emoji", Unicode),
        new("cursor", "random CUP + short writes + EL, e.g. htop / tmux status redraws", CursorMoves),
        new("scroll-region", "DECSTBM region scrolling with IL/DL, e.g. vim / less", ScrollRegion),
        new("alt-screen", "full-screen repaints in the alternate screen", AltScreen),
    ];

    private static readonly string[] Words =
    [
        "terminal", "buffer", "render", "glyph", "cursor", "scroll", "parser", "escape", "sequence", "color",
        "unicode", "cell", "atlas", "shader", "frame", "latency", "throughput", "kernel", "process", "signal",
        "a", "of", "to", "in", "is", "it", "the", "and", "for", "on", "with", "as", "at", "by", "fn", "let",
    ];

    private static void Fill(StringBuilder sb, int bytes, Action<StringBuilder> chunk)
    {
        while (sb.Length < bytes) chunk(sb);
    }

    private static byte[] Ascii(int bytes, int cols, int rows)
    {
        var rng = new Random(1);
        var sb = new StringBuilder(bytes + 256);
        Fill(sb, bytes, s =>
        {
            int target = rng.Next(10, cols);
            int len = 0;
            while (len < target)
            {
                string w = Words[rng.Next(Words.Length)];
                s.Append(w).Append(' ');
                len += w.Length + 1;
            }
            s.Append("\r\n");
        });
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] Sgr256(int bytes, int cols, int rows)
    {
        var rng = new Random(2);
        var sb = new StringBuilder(bytes + 256);
        Fill(sb, bytes, s =>
        {
            int len = 0;
            while (len < cols - 12)
            {
                string w = Words[rng.Next(Words.Length)];
                if (rng.Next(3) == 0) s.Append("\e[1m");
                s.Append("\e[38;5;").Append(rng.Next(256)).Append('m').Append(w).Append("\e[0m ");
                len += w.Length + 1;
            }
            s.Append("\r\n");
        });
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] SgrTrueColor(int bytes, int cols, int rows)
    {
        var rng = new Random(3);
        var sb = new StringBuilder(bytes + 256);
        Fill(sb, bytes, s =>
        {
            s.Append("\e[48;2;30;32;40m");
            int len = 0;
            while (len < cols - 12)
            {
                string w = Words[rng.Next(Words.Length)];
                s.Append("\e[38;2;").Append(rng.Next(256)).Append(';').Append(rng.Next(256)).Append(';')
                    .Append(rng.Next(256)).Append('m').Append(w).Append(' ');
                len += w.Length + 1;
            }
            s.Append("\e[K\e[0m\r\n");
        });
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] Unicode(int bytes, int cols, int rows)
    {
        string[] pieces = ["café", "naïve", "Ωμέγα", "λόγος", "─│┌┐└┘├┤", "日本語", "中文字符", "한국어", "🦀", "✨", "→", "€", "ß"];
        var rng = new Random(4);
        var sb = new StringBuilder(bytes);
        int lineWidth = 0;
        while (Encoding.UTF8.GetByteCount(sb.ToString()) < bytes)
        {
            for (int i = 0; i < 256; i++)
            {
                string p = pieces[rng.Next(pieces.Length)];
                sb.Append(p).Append(' ');
                lineWidth += p.Length * 2 + 1;
                if (lineWidth > cols) { sb.Append("\r\n"); lineWidth = 0; }
            }
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] CursorMoves(int bytes, int cols, int rows)
    {
        var rng = new Random(5);
        var sb = new StringBuilder(bytes + 256);
        Fill(sb, bytes, s =>
        {
            s.Append("\e[").Append(rng.Next(1, rows + 1)).Append(';').Append(rng.Next(1, cols - 20)).Append('H');
            s.Append("\e[3").Append(rng.Next(8)).Append('m');
            s.Append(Words[rng.Next(Words.Length)]).Append(' ').Append(rng.Next(100000));
            if (rng.Next(4) == 0) s.Append("\e[K");
        });
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] ScrollRegion(int bytes, int cols, int rows)
    {
        var rng = new Random(6);
        var sb = new StringBuilder(bytes + 256);
        sb.Append("\e[2;").Append(rows - 1).Append('r');
        Fill(sb, bytes, s =>
        {
            switch (rng.Next(10))
            {
                case 0: s.Append("\e[").Append(rng.Next(2, rows - 1)).Append(";1H\e[2L"); break;
                case 1: s.Append("\e[").Append(rng.Next(2, rows - 1)).Append(";1H\e[M"); break;
                default:
                    s.Append("\e[").Append(rows - 1).Append(";1H\n");
                    for (int i = 0; i < 6; i++) s.Append(Words[rng.Next(Words.Length)]).Append(' ');
                    break;
            }
        });
        sb.Append("\e[r");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] AltScreen(int bytes, int cols, int rows)
    {
        var rng = new Random(7);
        var sb = new StringBuilder(bytes + 256);
        sb.Append("\e[?1049h");
        Fill(sb, bytes, s =>
        {
            s.Append("\e[H\e[2J");
            for (int y = 1; y <= rows; y++)
            {
                s.Append("\e[").Append(y).Append(";1H\e[3").Append(y % 8).Append('m');
                int len = 0;
                while (len < cols - 10)
                {
                    string w = Words[rng.Next(Words.Length)];
                    s.Append(w).Append(' ');
                    len += w.Length + 1;
                }
            }
        });
        sb.Append("\e[?1049l");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
