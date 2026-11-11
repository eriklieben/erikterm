namespace ErikTerm.Core;

/// <summary>
/// Whether a compiled terminfo entry can be found the way ncurses looks for one, so erikterm only sets
/// <c>TERM=xterm-erikterm</c> when programs will understand it (the <c>term = "auto"</c> setting).
/// </summary>
public static class TerminfoLocator
{
    /// <summary>The system directories ncurses builds usually search after $TERMINFO, ~/.terminfo and $TERMINFO_DIRS.</summary>
    public static readonly string[] SystemDirectories =
        ["/etc/terminfo", "/lib/terminfo", "/usr/share/terminfo", "/usr/lib/terminfo", "/usr/share/lib/terminfo", "/usr/local/share/terminfo"];

    /// <summary>
    /// The directories searched, in order: <c>$TERMINFO</c>, <c>~/.terminfo</c>, each <c>$TERMINFO_DIRS</c> entry (an empty
    /// entry means the system directories), then the system directories.
    /// </summary>
    public static IEnumerable<string> SearchPath(Func<string, string?> getEnv, string? home)
    {
        if (getEnv("TERMINFO") is { Length: > 0 } terminfo) yield return terminfo;
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, ".terminfo");
        if (getEnv("TERMINFO_DIRS") is { Length: > 0 } dirs)
        {
            foreach (string dir in dirs.Split(':'))
            {
                if (dir.Length > 0) yield return dir;
                else foreach (string sys in SystemDirectories) yield return sys;
            }
        }
        foreach (string sys in SystemDirectories) yield return sys;
    }

    /// <summary>True if <paramref name="name"/> is compiled in one of <see cref="SearchPath"/>'s directories.</summary>
    public static bool Exists(string name, Func<string, string?> getEnv, string? home)
    {
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\')) return false;
        string letter = name[..1], hex = ((int)name[0]).ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
        // Linux layout (x/xterm-erikterm) and the hashed one macOS's ncurses uses (78/xterm-erikterm).
        return SearchPath(getEnv, home).Any(dir => File.Exists(Path.Combine(dir, letter, name)) || File.Exists(Path.Combine(dir, hex, name)));
    }

    /// <summary>
    /// The TERM for a <c>term</c> setting: "auto" is <see cref="TermCapabilities.Name"/> when its entry is installed,
    /// else xterm-256color; any other value is used as it is.
    /// </summary>
    public static string Resolve(string setting, Func<string, string?> getEnv, string? home)
    {
        if (setting != "auto") return setting;
        return Exists(TermCapabilities.Name, getEnv, home) ? TermCapabilities.Name : TermCapabilities.BaseEntry;
    }

    /// <summary><see cref="Resolve(string, Func{string, string?}, string?)"/> for this process's environment.</summary>
    public static string Resolve(string setting) =>
        Resolve(setting, Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
