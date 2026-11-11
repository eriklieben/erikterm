namespace ErikTerm.App.Security;

/// <summary>
/// Shows text in `less` in a new pane without a temporary file (a predictable name in /tmp could be pre-created or
/// symlinked by another user): the text is an argument of the wrapper and reaches less through a pipe.
/// </summary>
internal static class PagerCommand
{
    /// <summary>Linux caps one argument at 128 KiB (MAX_ARG_STRLEN); UTF-8 takes up to 3 bytes per UTF-16 unit.</summary>
    public const int MaxChars = 40_000;

    public const string Script = "printf '%s' \"$1\" | less";

    public static string[] Less(string text)
    {
        if (text.Length > MaxChars) text = text[..MaxChars] + "\n…\n";
        return ["sh", "-c", Script, "erikterm-pager", text];
    }
}
