using System.Globalization;
using System.Text;

namespace ErikTerm.Core;

/// <summary>
/// <c>ERIKTERM_LOG_UNHANDLED=1</c> (stderr) or <c>=PATH</c> (appended): a log of the escape sequences the terminal
/// ignores — CSI (incl. unknown DEC/ANSI modes), OSC, DCS, APC and ESC — one line each, in the style of
/// ERIKTERM_TMUX_LOG: <c>2026-11-07T22:14:20.123 [4242] unhandled CSI ?1015h</c>. Each distinct sequence is logged
/// once, and at most <see cref="MaxLinesPerSecond"/> lines a second (the rest are counted and reported).
/// Only the terminal's ignore paths call it, and <see cref="Terminal.Unhandled"/> is null when the switch is off,
/// so the parser's hot path is unchanged and allocation-free.
/// </summary>
public sealed class UnhandledLog
{
    public const string Variable = "ERIKTERM_LOG_UNHANDLED";
    public const int MaxLinesPerSecond = 20;
    /// <summary>Distinct sequences remembered for the once-only rule; past this, only the rate limit applies.</summary>
    public const int MaxDistinct = 4096;
    /// <summary>OSC/DCS/APC payloads are cut to this many characters ("…").</summary>
    public const int MaxPayloadChars = 120;

    /// <summary>The process's log (from the environment), or null when it is off.</summary>
    public static UnhandledLog? Shared { get; } = FromEnvironment(Environment.GetEnvironmentVariable(Variable));

    private readonly TextWriter _writer;
    private readonly Func<long> _clockMs;
    private readonly Func<DateTime> _now;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private long _windowStart = long.MinValue / 2; // "long ago", without overflowing now - _windowStart
    private int _inWindow, _suppressed;

    /// <param name="writer">Where lines go.</param>
    /// <param name="clockMs">Monotonic milliseconds for the rate limit (default: Environment.TickCount64).</param>
    /// <param name="now">The timestamp printed on each line (default: DateTime.Now).</param>
    public UnhandledLog(TextWriter writer, Func<long>? clockMs = null, Func<DateTime>? now = null)
    {
        _writer = writer;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>"1" → stderr, a path → that file (appended), unset / "" / "0" → off.</summary>
    public static UnhandledLog? FromEnvironment(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == "0") return null;
        if (value == "1") return new UnhandledLog(Console.Error);
        try
        {
            // FileShare.Delete: as on Unix, the log can be removed while erikterm runs (Windows refuses that otherwise).
            var file = new FileStream(value, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
            return new UnhandledLog(TextWriter.Synchronized(new StreamWriter(file) { AutoFlush = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"erikterm: {Variable}: {ex.Message}");
            return null;
        }
    }

    // ---- Entry points (Terminal's ignore paths) ----------------------------------------------------

    public void Csi(CsiParams p, ReadOnlySpan<byte> intermediates, byte privateMarker, byte final) =>
        Write(FormatCsi(p, intermediates, privateMarker, final));

    /// <summary>An unknown mode in <c>CSI h</c>/<c>l</c> (<paramref name="dec"/>: <c>CSI ? … h</c>/<c>l</c>).</summary>
    public void Mode(bool dec, int mode, bool on) =>
        Write($"CSI {(dec ? "?" : "")}{mode.ToString(CultureInfo.InvariantCulture)}{(on ? 'h' : 'l')}");

    /// <summary>A sequence already written out ("CSI 15n").</summary>
    public void Sequence(string text) => Write(text);

    public void Osc(ReadOnlySpan<byte> data) => Write(FormatString("OSC", data));
    public void Dcs(ReadOnlySpan<byte> data) => Write(FormatString("DCS", data));
    public void Apc(ReadOnlySpan<byte> data) => Write(FormatString("APC", data));
    public void Esc(ReadOnlySpan<byte> intermediates, byte final) => Write("ESC " + Printable(intermediates, int.MaxValue) + (char)final);

    // ---- Formatting --------------------------------------------------------------------------------

    /// <summary>"CSI ?2031h", "CSI >4;2m", "CSI 38:2::1:2:3m", "CSI 1 q" (intermediates before the final).</summary>
    public static string FormatCsi(CsiParams p, ReadOnlySpan<byte> intermediates, byte privateMarker, byte final)
    {
        var sb = new StringBuilder("CSI ");
        if (privateMarker != 0) sb.Append((char)privateMarker);
        for (int i = 0; i < p.Count; i++)
        {
            if (i > 0) sb.Append(p.IsSub(i) ? ':' : ';');
            if (p[i] != 0 || !p.IsSub(i)) sb.Append(p[i].ToString(CultureInfo.InvariantCulture));
        }
        sb.Append(Printable(intermediates, int.MaxValue));
        sb.Append((char)final);
        return sb.ToString();
    }

    /// <summary>"OSC 4;1;?", "DCS +q544e" — control bytes as \xNN, cut at <see cref="MaxPayloadChars"/>.</summary>
    public static string FormatString(string kind, ReadOnlySpan<byte> data) => kind + " " + Printable(data, MaxPayloadChars);

    private static string Printable(ReadOnlySpan<byte> data, int max)
    {
        var sb = new StringBuilder();
        string text = Encoding.UTF8.GetString(data);
        foreach (var rune in text.EnumerateRunes())
        {
            if (sb.Length >= max) { sb.Append('…'); break; }
            if (rune.Value is < 0x20 or 0x7F or (>= 0x80 and < 0xA0)) sb.Append($"\\x{rune.Value:x2}");
            else sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    // ---- Output ------------------------------------------------------------------------------------

    private void Write(string sequence)
    {
        lock (_lock)
        {
            if (_seen.Contains(sequence)) return; // once per distinct sequence
            long now = _clockMs();
            if (now - _windowStart >= 1000)
            {
                _windowStart = now;
                _inWindow = 0;
                if (_suppressed > 0)
                {
                    WriteLine($"… {_suppressed} more unhandled sequence{(_suppressed == 1 ? "" : "s")} not logged (over {MaxLinesPerSecond}/s)");
                    _suppressed = 0;
                }
            }
            if (_inWindow >= MaxLinesPerSecond) { _suppressed++; return; }
            _inWindow++;
            if (_seen.Count < MaxDistinct) _seen.Add(sequence);
            WriteLine("unhandled " + sequence);
        }
    }

    private void WriteLine(string text)
    {
        try
        {
            _writer.Write($"{_now():yyyy-MM-ddTHH:mm:ss.fff} [{Environment.ProcessId}] {text}\n");
            _writer.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* a log that can't be written has nowhere to report to */ }
    }
}
