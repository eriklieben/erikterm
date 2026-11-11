namespace ErikTerm.App.Integrations;

/// <summary>Which shell syntax a pane's program speaks, as far as quoting and changing directory go.</summary>
internal enum ShellSyntax { Posix, Fish, PowerShell, Cmd, Nu }

/// <summary>The command that changes a shell to a directory, quoted for that shell ("Go to: dir" in the palette).</summary>
internal static class ShellCd
{
    private static readonly string[] Names = ["bash", "zsh", "sh", "dash", "ksh", "ash", "fish", "nu", "pwsh", "powershell", "cmd"];

    /// <summary>A program name without directory and <c>.exe</c>, lower-cased, without a login shell's leading dash.</summary>
    private static string Plain(string program)
    {
        string name = program.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (name.EndsWith(".exe", StringComparison.Ordinal)) name = name[..^4];
        return name.TrimStart('-');
    }

    /// <summary>Whether the program is a shell we know how to talk to.</summary>
    public static bool IsShell(string? program) => program is { Length: > 0 } && Names.Contains(Plain(program));

    public static ShellSyntax Detect(string? program)
    {
        if (program is null) return ShellSyntax.Posix;
        return Plain(program) switch
        {
            "fish" => ShellSyntax.Fish,
            "pwsh" or "powershell" => ShellSyntax.PowerShell,
            "cmd" => ShellSyntax.Cmd,
            "nu" => ShellSyntax.Nu,
            _ => ShellSyntax.Posix,
        };
    }

    /// <summary>The line to type, or null for a path that can't be typed (control characters: a newline would run what follows it).</summary>
    public static string? Command(string directory, ShellSyntax shell)
    {
        if (directory.Length == 0 || directory.Any(Security.PasteSanitizer.IsControl)) return null;
        return shell switch
        {
            // In fish's single quotes \ and ' are the two characters that need escaping; its cd takes no "--" reliably.
            ShellSyntax.Fish => $"cd '{directory.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}'",
            ShellSyntax.PowerShell => $"Set-Location -LiteralPath '{directory.Replace("'", "''", StringComparison.Ordinal)}'",
            ShellSyntax.Cmd => directory.Contains('"') ? null : $"cd /d \"{directory}\"",
            // A raw string has no escapes at all; only its own terminator can't appear inside.
            ShellSyntax.Nu => directory.Contains("'#", StringComparison.Ordinal)
                ? $"cd \"{directory.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
                : $"cd r#'{directory}'#",
            _ => $"cd -- '{directory.Replace("'", "'\\''", StringComparison.Ordinal)}'",
        };
    }
}
