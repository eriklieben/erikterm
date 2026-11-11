using ErikTerm.App.Native;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.VimMode;

namespace ErikTerm.App.Vim;

/// <summary>How vim mode reads a key press.</summary>
internal static class VimInput
{
    /// <summary>Vim's own Ctrl keys: these beat a [keys] binding of the same chord (Ctrl+h/j/k/l stay yours).</summary>
    private const string CtrlKeys = "udfbeyvwcqnpro";

    /// <summary>The vim key an SDL key press stands for, or null for keys that are read from text input or bindings.</summary>
    public static string? KeyFor(uint key, bool ctrl, bool shift, bool alt, bool super)
    {
        if (alt || super) return null;
        if (ctrl && !shift)
        {
            if (key is >= 'a' and <= 'z') return VimKeys.Ctrl((char)key);
            return key == '[' ? VimKeys.Esc : null;
        }
        if (ctrl) return null;
        return key switch
        {
            Sdl.K_ESCAPE => VimKeys.Esc,
            Sdl.K_RETURN or Sdl.K_KP_ENTER => VimKeys.Enter,
            Sdl.K_BACKSPACE => VimKeys.Backspace,
            Sdl.K_TAB => shift ? VimKeys.ShiftTab : VimKeys.Tab,
            Sdl.K_UP => VimKeys.Up,
            Sdl.K_DOWN => VimKeys.Down,
            Sdl.K_LEFT => VimKeys.Left,
            Sdl.K_RIGHT => VimKeys.Right,
            Sdl.K_HOME when !shift => VimKeys.Home,
            Sdl.K_END when !shift => VimKeys.End,
            Sdl.K_PAGEUP when !shift => VimKeys.PageUp,
            Sdl.K_PAGEDOWN when !shift => VimKeys.PageDown,
            _ => null,
        };
    }

    /// <summary>
    /// Whether vim takes a key before any [keys] binding of the same chord: every unbound key, a prompt or a half-typed
    /// command, Esc, and vim's own Ctrl keys. <paramref name="busy"/>: a prompt is open or a command is half typed.
    /// </summary>
    public static bool TakesFirst(string vk, bool ctrl, bool bound, bool busy) =>
        !ctrl || !bound || busy || VimKeys.IsCtrl(vk, out char c) && CtrlKeys.Contains(c) || vk == VimKeys.Esc;
}

/// <summary>Files vim mode writes: exports for Neovim and <c>:write</c>.</summary>
internal static class VimFiles
{
    /// <summary>1-based line of <paramref name="line"/> in text exported from <paramref name="first"/> (wrapped rows join).</summary>
    public static long CountLines(Terminal t, long first, long line)
    {
        if (line < first) return 1;
        long n = 1;
        for (long l = first; l < line; l++) if (t.GetLine(l)?.Wrapped != true) n++;
        return n;
    }

    /// <summary>Writes a file readable only by you (0600 when it's created).</summary>
    public static void WritePrivate(string path, string text, bool overwrite)
    {
        var options = new FileStreamOptions { Mode = overwrite ? FileMode.Create : FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }
}

/// <summary>:theme's name lookup.</summary>
internal static class VimSchemes
{
    /// <summary>An exact name first (any case), then a prefix, then the best fuzzy match.</summary>
    public static ColorScheme? Find(List<ColorScheme> all, string name)
    {
        name = name.Trim();
        return all.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(s => s.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            ?? all.Select(s => (Scheme: s, Ok: Fuzzy.Match(name, s.Name, out int score), Score: score))
                .Where(x => x.Ok).OrderByDescending(x => x.Score).Select(x => x.Scheme).FirstOrDefault();
    }
}
