namespace ErikTerm.App.Tabs;

/// <summary>The title a tab shows when it wasn't named: the program, or, for a shell, where it is.</summary>
internal static class TabTitles
{
    public static bool IsShell(string label) => label is "zsh" or "bash" or "fish" or "sh" or "dash" or "nu" or "pwsh" or "powershell" or "cmd";

    /// <summary>A shell says little, so its directory (OSC 7) or the title it set says more. Other programs keep their name.</summary>
    public static string Auto(string label, string termTitle, string? workingDirectory, string home)
    {
        if (!IsShell(label)) return label;
        if (workingDirectory is { } cwd)
        {
            if (cwd == home) return "~";
            return Path.GetFileName(cwd.TrimEnd('/')) is { Length: > 0 } name ? name : cwd;
        }
        return termTitle.Length > 0 && termTitle != "erikterm" ? termTitle : label;
    }
}
