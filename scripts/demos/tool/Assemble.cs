using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Demos;

/// <summary>
/// demo assemble FRAMES_DIR OUT_STEM [--fps 20] [--scale 1.0] [--crop W:H:X:Y] [--trim START_MS:END_MS] [--webp-q 0]
///
/// Frames recorded by erikterm's demo recorder (ERIKTERM_RECORD_DIR: &lt;ms&gt;.bmp, plus &lt;ms&gt;.on / &lt;ms&gt;.off
/// markers) become a steady-rate clip: OUT.webp (animated; --webp-q 0 is lossless), OUT.gif and OUT.png (the first
/// frame). Each output frame is the latest recorded frame at or before its time (erikterm only records a frame when it
/// draws, so a still screen is one frame held), and a held frame is written once with a longer duration. FRAMES_DIR/seq
/// gets one link per output frame, for sheet.sh.
/// </summary>
internal static class Assemble
{
    public static int Run(string[] args)
    {
        string dir = Path.GetFullPath(args[0]), outStem = args[1];
        double fps = 20, scale = 1.0;
        string? crop = null, trim = null;
        int webpQ = 0;
        for (int i = 2; i + 1 < args.Length; i += 2)
        {
            string v = args[i + 1];
            switch (args[i])
            {
                case "--fps": fps = double.Parse(v, CultureInfo.InvariantCulture); break;
                case "--scale": scale = double.Parse(v, CultureInfo.InvariantCulture); break;
                case "--crop": crop = v; break;
                case "--trim": trim = v; break;
                case "--webp-q": webpQ = int.Parse(v, CultureInfo.InvariantCulture); break;
                default: Console.Error.WriteLine($"assemble: unknown option {args[i]}"); return 2;
            }
        }

        var stamps = Stamps(dir, "*.bmp");
        var on = Stamps(dir, "*.on");
        var off = Stamps(dir, "*.off");
        if (stamps.Count == 0 || on.Count == 0)
        {
            Console.Error.WriteLine("assemble: no frames");
            return 1;
        }
        long start = on[0], end = off.Count > 0 ? off[^1] : stamps[^1];
        if (trim is not null)
        {
            var parts = trim.Split(':');
            long t0 = parts[0].Length > 0 ? long.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
            end = parts.Length > 1 && parts[1].Length > 0 ? start + long.Parse(parts[1], CultureInfo.InvariantCulture) : end;
            start += t0;
        }

        string seq = Path.Combine(dir, "seq");
        if (Directory.Exists(seq)) Directory.Delete(seq, recursive: true);
        Directory.CreateDirectory(seq);
        double step = 1000 / fps;
        int n = (int)((end - start) / step) + 1, j = 0;
        var groups = new List<(long Stamp, int Count)>(); // a recorded frame and the output frames it covers
        for (int k = 0; k < n; k++)
        {
            double t = start + k * step;
            while (j + 1 < stamps.Count && stamps[j + 1] <= t) j++;
            File.CreateSymbolicLink(Path.Combine(seq, $"{k:000000}.bmp"), Frame(dir, stamps[j]));
            if (groups.Count > 0 && groups[^1].Stamp == stamps[j]) groups[^1] = (stamps[j], groups[^1].Count + 1);
            else groups.Add((stamps[j], 1));
        }
        // a held frame becomes one long frame (the concat demuxer wants the last file listed twice)
        string listing = Path.Combine(dir, "frames.txt");
        var sb = new StringBuilder();
        foreach (var (stamp, count) in groups)
            sb.Append(CultureInfo.InvariantCulture, $"file '{Frame(dir, stamp)}'\nduration {count / fps:0.0000}\n");
        sb.Append(CultureInfo.InvariantCulture, $"file '{Frame(dir, groups[^1].Stamp)}'\n");
        File.WriteAllText(listing, sb.ToString());

        var vf = new List<string>();
        if (crop is not null) vf.Add($"crop={crop}");
        if (scale != 1.0) vf.Add(string.Create(CultureInfo.InvariantCulture, $"scale=iw*{scale}:-2:flags=lanczos"));
        string chain = vf.Count > 0 ? string.Join(',', vf) : "null";
        string[] input = ["-f", "concat", "-safe", "0", "-i", listing];
        string[] quality = webpQ == 0
            ? ["-lossless", "1", "-compression_level", "6"]
            : ["-quality", webpQ.ToString(CultureInfo.InvariantCulture), "-compression_level", "6"];
        string palette = Path.Combine(dir, "palette.png");

        Ffmpeg(["-i", Path.Combine(seq, "000000.bmp"), "-frames:v", "1", "-vf", chain, outStem + ".png"]);
        Ffmpeg([.. input, "-vf", chain, "-fps_mode", "vfr", "-c:v", "libwebp_anim", "-loop", "0", .. quality, outStem + ".webp"]);
        Ffmpeg([.. input, "-vf", chain + ",palettegen=max_colors=256:stats_mode=diff", palette]);
        Ffmpeg([.. input, "-i", palette, "-lavfi", $"{chain}[x];[x][1:v]paletteuse=dither=none:diff_mode=rectangle",
            "-fps_mode", "vfr", "-loop", "0", outStem + ".gif"]);
        foreach (var ext in new[] { "webp", "gif", "png" })
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{outStem}.{ext}: {new FileInfo($"{outStem}.{ext}").Length / 1e6:0.00} MB"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"duration {(end - start) / 1000.0:0.0}s, {stamps.Count} recorded frames"));
        return 0;
    }

    /// <summary>The millisecond stamps of the files matching a pattern (names like 00012345.bmp), sorted.</summary>
    private static List<long> Stamps(string dir, string pattern) =>
        [.. Directory.GetFiles(dir, pattern)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(s => s!.Length > 0 && s.All(char.IsAsciiDigit))
            .Select(s => long.Parse(s!, CultureInfo.InvariantCulture))
            .Order()];

    private static string Frame(string dir, long stamp) => Path.Combine(dir, $"{stamp:00000000}.bmp");

    private static void Ffmpeg(string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg");
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-y", .. args]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg failed ({p.ExitCode})");
    }
}
