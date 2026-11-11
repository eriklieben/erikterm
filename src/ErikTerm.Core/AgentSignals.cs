using System.Buffers.Text;
using System.Text;

namespace ErikTerm.Core;

/// <summary>OSC 7501 program states (Program Status rev 0.4).</summary>
public enum ProgramState : byte { Idle, Working, Done, Blocked, Error }

/// <summary>
/// One OSC 7501 record: <c>state=working:app=claude-code[:id=…][:kind=permission][:progress=40][:title=b64][:msg=b64]</c>.
/// <see cref="Id"/> is "" for the program's main record; subagents and background tasks carry their own.
/// Title and message are decoded, checked plain text (never markup).
/// </summary>
public sealed record ProgramStatusRecord(string App, string Id, ProgramState State, string? Kind, int? Progress, string? Title, string? Message);

/// <summary>A desktop notification a program asked for (OSC 9, OSC 777 ; notify, kitty OSC 99).</summary>
/// <param name="Source">"osc9", "osc777" or "osc99".</param>
/// <param name="Focus">kitty <c>a=focus</c>: activating the notification should focus the window.</param>
public sealed record TerminalNotification(string? Title, string Body, string Source, bool Focus = false);

/// <summary>
/// Parsers for the explicit "what is the program doing" signals coding agents send: OSC 7501 program status,
/// OSC 9 / OSC 777 / OSC 99 notifications and OSC 9 ; 4 progress. Pure functions over the OSC payload (the bytes
/// after "<c>NNN;</c>"), so they can be tested without a terminal.
/// </summary>
public static class AgentSignals
{
    public const int MaxTitleChars = 192;
    public const int MaxMessageChars = 2048;

    /// <summary>Reply to an <c>OSC 7501 ; ?</c> probe: this terminal understands every state and kind.</summary>
    public static ReadOnlySpan<byte> ProgramStatusReply => "\e]7501;?:states=idle,working,done,blocked,error:kinds=permission,question,auth\e\\"u8;

    /// <summary>The probe: an OSC 7501 payload starting with '?'.</summary>
    public static bool IsProgramStatusQuery(ReadOnlySpan<byte> arg) => !arg.IsEmpty && arg[0] == (byte)'?';

    /// <summary>
    /// What an OSC 7501 record asks for. <paramref name="clear"/> is set for <c>state=clear</c> (then
    /// <paramref name="clearId"/> is the id to drop, or null for all of them). Returns false for malformed input.
    /// </summary>
    public static bool TryParseProgramStatus(ReadOnlySpan<byte> arg, out ProgramStatusRecord? record, out bool clear, out string? clearId)
    {
        record = null;
        clear = false;
        clearId = null;
        var fields = new StatusFields();
        foreach (var range in arg.Split((byte)':'))
        {
            var field = arg[range];
            int eq = field.IndexOf((byte)'=');
            if (eq > 0) fields.Set(field[..eq], field[(eq + 1)..]);
        }
        if (fields.State is null) return false;
        if (fields.State == "clear")
        {
            clear = true;
            clearId = fields.Id;
            return true;
        }
        if (ParseProgramState(fields.State) is not { } s) return false;
        int? progress = s is ProgramState.Working or ProgramState.Blocked ? fields.Progress : null;
        string? kind = s == ProgramState.Blocked ? fields.Kind : null;
        record = new ProgramStatusRecord(fields.App ?? "", fields.Id ?? "", s, kind, progress, fields.Title, fields.Msg);
        return true;
    }

    /// <summary>The values of an OSC 7501 record's <c>key=value</c> fields; unknown keys are ignored.</summary>
    private struct StatusFields
    {
        public string? State, App, Id, Kind, Title, Msg;
        public int? Progress;

        public void Set(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        {
            if (key.SequenceEqual("state"u8)) State = Ascii(value, 16);
            else if (key.SequenceEqual("app"u8)) App = Ascii(value, 64);
            else if (key.SequenceEqual("id"u8)) Id = Ascii(value, 64);
            else if (key.SequenceEqual("kind"u8)) Kind = Ascii(value, 32);
            else if (key.SequenceEqual("progress"u8) && int.TryParse(value, out int p)) Progress = Math.Clamp(p, 0, 100);
            else if (key.SequenceEqual("title"u8)) Title = DecodeText(value, MaxTitleChars);
            else if (key.SequenceEqual("msg"u8)) Msg = DecodeText(value, MaxMessageChars);
        }
    }

    private static ProgramState? ParseProgramState(string state) => state switch
    {
        "idle" => ProgramState.Idle,
        "working" => ProgramState.Working,
        "done" => ProgramState.Done,
        "blocked" => ProgramState.Blocked,
        "error" => ProgramState.Error,
        _ => null,
    };

    /// <summary>
    /// OSC 9 payload: iTerm2's notification (<c>OSC 9 ; text</c>; Claude Code sends "title: message"), unless it's
    /// ConEmu's progress (<c>9 ; 4 ; …</c>) or another ConEmu sub-command (a digit and ';'), which return null.
    /// </summary>
    public static TerminalNotification? ParseOsc9(ReadOnlySpan<byte> arg)
    {
        if (arg.Length >= 2 && arg[0] is >= (byte)'0' and <= (byte)'9' && (arg[1] == (byte)';' || arg.Length == 1)) return null;
        if (arg.Length == 1 && arg[0] is >= (byte)'0' and <= (byte)'9') return null;
        string? body = Clean(Encoding.UTF8.GetString(arg), MaxMessageChars);
        return body is null ? null : new TerminalNotification(null, body, "osc9");
    }

    /// <summary>ConEmu / Windows Terminal progress, <c>OSC 9 ; 4 ; state ; value</c>: (state 0–4, percent) or null.</summary>
    public static (int State, int Value)? ParseOsc9Progress(ReadOnlySpan<byte> arg)
    {
        if (!arg.StartsWith("4;"u8) && !arg.SequenceEqual("4"u8)) return null;
        var rest = arg.Length > 2 ? arg[2..] : default;
        int semi = rest.IndexOf((byte)';');
        var st = semi < 0 ? rest : rest[..semi];
        int state = 0;
        if (!st.IsEmpty && !int.TryParse(st, out state)) state = -1;
        if (state is < 0 or > 4) return null;
        int value = semi >= 0 && int.TryParse(rest[(semi + 1)..], out int pv) ? Math.Clamp(pv, 0, 100) : 0;
        return (state, value);
    }

    /// <summary>OSC 777 ; notify ; title ; body (rxvt-unicode, ghostty, foot).</summary>
    public static TerminalNotification? ParseOsc777(ReadOnlySpan<byte> arg)
    {
        if (!arg.StartsWith("notify;"u8)) return null;
        var rest = arg["notify;".Length..];
        int semi = rest.IndexOf((byte)';');
        string? title = Clean(Encoding.UTF8.GetString(semi < 0 ? rest : rest[..semi]), MaxTitleChars);
        string? body = semi < 0 ? null : Clean(Encoding.UTF8.GetString(rest[(semi + 1)..]), MaxMessageChars);
        if (title is null && body is null) return null;
        return new TerminalNotification(body is null ? null : title, body ?? title!, "osc777");
    }

    /// <summary>
    /// Kitty's OSC 99 builds a notification over several sequences sharing an id: <c>i=1:d=0:p=title ; …</c>,
    /// <c>i=1:p=body ; …</c>, then a final one without <c>d=0</c>. Feed each payload; a finished notification comes back.
    /// </summary>
    public sealed class KittyNotifications
    {
        private sealed class Pending
        {
            public StringBuilder Title = new(), Body = new();
            public bool Focus;
        }

        private readonly Dictionary<string, Pending> _pending = [];

        public TerminalNotification? Feed(ReadOnlySpan<byte> arg)
        {
            int semi = arg.IndexOf((byte)';');
            var meta = semi < 0 ? arg : arg[..semi];
            var payload = semi < 0 ? default : arg[(semi + 1)..];
            var header = ParseHeader(meta);
            if (header.Part is "?" or "close" or "alive") return null; // queries / close requests: nothing to show
            var p = PendingFor(header.Id);
            p.Focus |= header.Focus;
            string text = header.Base64 ? DecodeBase64(payload) ?? "" : Encoding.UTF8.GetString(payload);
            var target = header.Part switch
            {
                "body" => p.Body,
                "title" => p.Title,
                _ => null,
            };
            if (target is not null && target.Length + text.Length <= MaxMessageChars) target.Append(text);
            if (!header.Done) return null;
            _pending.Remove(header.Id);
            string? title = Clean(p.Title.ToString(), MaxTitleChars), body = Clean(p.Body.ToString(), MaxMessageChars);
            if (title is null && body is null) return null;
            return new TerminalNotification(body is null ? null : title, body ?? title!, "osc99", p.Focus);
        }

        private struct Header
        {
            public string Id, Part;
            public bool Done, Base64, Focus;
        }

        /// <summary>Reads the <c>key=value:…</c> metadata before the ';'.</summary>
        private static Header ParseHeader(ReadOnlySpan<byte> meta)
        {
            var h = new Header { Id = "0", Part = "title", Done = true };
            foreach (var range in meta.Split((byte)':'))
            {
                var kv = meta[range];
                int eq = kv.IndexOf((byte)'=');
                if (eq <= 0) continue;
                var k = kv[..eq];
                var v = kv[(eq + 1)..];
                if (k.SequenceEqual("i"u8)) h.Id = Ascii(v, 64) ?? "0";
                else if (k.SequenceEqual("d"u8)) h.Done = !v.SequenceEqual("0"u8);
                else if (k.SequenceEqual("e"u8)) h.Base64 = v.SequenceEqual("1"u8);
                else if (k.SequenceEqual("p"u8)) h.Part = Ascii(v, 16) ?? "title";
                else if (k.SequenceEqual("a"u8)) h.Focus |= v.IndexOf("focus"u8) >= 0;
            }
            return h;
        }

        private Pending PendingFor(string id)
        {
            if (_pending.TryGetValue(id, out var p)) return p;
            if (_pending.Count >= 16) _pending.Clear(); // a program that never finishes its notifications
            return _pending[id] = new Pending();
        }
    }

    // ---- Text hygiene -------------------------------------------------------------------------

    /// <summary>Base64 → UTF-8 text, refused (null) when it holds control characters; bidi overrides are removed.</summary>
    public static string? DecodeText(ReadOnlySpan<byte> b64, int maxChars)
    {
        string? text = DecodeBase64(b64);
        if (text is null) return null;
        if (text.Any(char.IsControl)) return null; // per spec: reject, don't try to render it
        return Clean(text, maxChars);
    }

    private static string? DecodeBase64(ReadOnlySpan<byte> b64)
    {
        if (b64.IsEmpty) return null;
        var buf = new byte[Base64.GetMaxDecodedFromUtf8Length(b64.Length)];
        if (Base64.DecodeFromUtf8(b64, buf, out _, out int n) != System.Buffers.OperationStatus.Done) return null;
        try { return new UTF8Encoding(false, true).GetString(buf, 0, n); }
        catch (DecoderFallbackException) { return null; }
    }

    /// <summary>
    /// Plain one-line display text: control characters become spaces, bidi embedding/override/isolate controls
    /// (which could make a message read backwards) are dropped, runs of spaces collapse, and it's capped. Null if empty.
    /// </summary>
    public static string? Clean(string s, int maxChars)
    {
        var sb = new StringBuilder(Math.Min(s.Length, maxChars));
        bool space = false;
        foreach (char c in s)
        {
            if (c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069' or '\u200e' or '\u200f' or '\u061c') continue;
            bool ws = char.IsControl(c) || char.IsWhiteSpace(c);
            if (ws) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            if (sb.Length >= maxChars) break;
            sb.Append(c);
        }
        if (sb.Length >= maxChars) { sb.Length = maxChars - 1; sb.Append('…'); }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string? Ascii(ReadOnlySpan<byte> v, int max)
    {
        if (v.IsEmpty || v.Length > max) return null;
        foreach (byte b in v) if (b is < 0x21 or > 0x7E) return null;
        return Encoding.ASCII.GetString(v);
    }
}
