using System.Text;

namespace ErikTerm.App.Integrations;

/// <summary>How a file link opens (<c>editor_open</c>).</summary>
internal enum EditorOpenMode { Nvim, Split, System, None }

/// <summary>
/// Command lines for opening a file at a line: in a running Neovim (<c>nvim --server S --remote-send KEYS</c>, the path
/// escaped for the Ex command line and for key notation) or in a new pane (<c>nvim +call\ cursor(l,c) path</c>,
/// <c>$VISUAL +line path</c>). Everything is an argv list; nothing goes through a shell.
/// </summary>
internal static class EditorCommands
{
    public static EditorOpenMode ParseMode(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "split" or "pane" => EditorOpenMode.Split,
        "system" or "xdg-open" or "open" => EditorOpenMode.System,
        "none" or "off" or "false" => EditorOpenMode.None,
        _ => EditorOpenMode.Nvim,
    };

    /// <summary>Vim's fnameescape(): backslash before characters special on the Ex command line.</summary>
    public static string FnameEscape(string path)
    {
        const string special = " \t\n*?[{`$\\%#'\"|!<";
        var sb = new StringBuilder(path.Length + 8);
        if (path.Length > 0 && (path[0] is '+' or '>' || path == "-")) sb.Append('\\');
        foreach (char c in path)
        {
            if (special.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Text for --remote-send / nvim_input: '&lt;' starts key notation, so it becomes &lt;lt&gt;.</summary>
    public static string KeyNotationEscape(string s) => s.Replace("<", "<lt>", StringComparison.Ordinal);

    /// <summary>
    /// Keys that leave any mode, open the file (switching to its window when it is already shown; <c>:drop</c> splits
    /// instead of failing when the current buffer has unsaved changes) and put the cursor on line/col.
    /// </summary>
    public static string RemoteSendKeys(string path, int line, int col)
    {
        var sb = new StringBuilder();
        sb.Append(@"<C-\><C-N>:<C-U>drop ").Append(KeyNotationEscape(FnameEscape(path))).Append("<CR>");
        if (line > 0) sb.Append(":<C-U>call cursor(").Append(line).Append(',').Append(Math.Max(1, col)).Append(") | execute 'normal! zvzz' | echo ''<CR>");
        return sb.ToString();
    }

    /// <summary>argv for <c>nvim --server SOCKET --remote-send KEYS</c>.</summary>
    public static string[] RemoteSendArgv(string nvim, string server, string path, int line, int col) =>
        [nvim, "--server", server, "--remote-send", RemoteSendKeys(path, line, col)];

    /// <summary>A Neovim server address we're willing to talk to: an absolute socket path (not a TCP address).</summary>
    public static bool IsValidServer(string? server) =>
        server is { Length: > 1 and < 1024 } && server[0] == '/' && !server.AsSpan().ContainsAny("\0\n\r") && !server.Contains("/../", StringComparison.Ordinal);

    /// <summary>
    /// argv to open <paramref name="path"/> at a line in a new pane with <paramref name="editor"/> ($VISUAL / $EDITOR,
    /// split on blanks — "code -w" works — or "nvim"). vi-likes get the column too, Helix takes path:line:col,
    /// everything else the common <c>+line</c> convention.
    /// </summary>
    public static string[] EditorArgv(string? editor, string path, int line, int col)
    {
        var parts = (string.IsNullOrWhiteSpace(editor) ? "nvim" : editor).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var argv = new List<string>(parts);
        string name = Path.GetFileName(parts[0]);
        if (name is "hx" or "helix")
        {
            argv.Add(line > 0 ? FileRefResolver.Describe(path, line, col) : path);
            return argv.ToArray();
        }
        if (line > 0)
        {
            if (name is "nvim" or "vim" or "vi" or "gvim" or "nvim-qt") argv.Add($"+call cursor({line},{Math.Max(1, col)})");
            else argv.Add($"+{line}");
        }
        if (path.StartsWith('-')) argv.Add("--"); // (paths are absolute here; belt and braces)
        argv.Add(path);
        return argv.ToArray();
    }

    /// <summary>
    /// The pane command that edits a file with no line to jump to: <paramref name="editor"/> (or nvim) when it can be
    /// found, else vi where POSIX guarantees one. Null on Windows without the editor, which has neither vi nor a
    /// shell to look for one with: hand the file to the system opener instead. Never through a shell, so a path
    /// with spaces or quotes stays one argument.
    /// </summary>
    public static string[]? EditFileArgv(string? editor, string path, bool windows, Func<string, bool> found)
    {
        var argv = EditorArgv(editor, path, 0, 0);
        if (found(argv[0])) return argv;
        return windows ? null : ["vi", path];
    }

    /// <summary>$VISUAL, else $EDITOR, else null.</summary>
    public static string? PreferredEditor(Func<string, string?> env)
    {
        if (env("VISUAL") is { Length: > 0 } v) return v;
        return env("EDITOR") is { Length: > 0 } e ? e : null;
    }

    /// <summary>True when the editor command is Neovim (so a running instance can take the file instead).</summary>
    public static bool IsNvim(string? editor) =>
        editor is null || Path.GetFileName(editor.Trim().Split(' ', 2)[0]) is "nvim";
}
