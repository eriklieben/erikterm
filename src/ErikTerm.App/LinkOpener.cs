using System.ComponentModel;
using System.Diagnostics;

namespace ErikTerm.App;

/// <summary>
/// Opens a link with the desktop's handler: xdg-open (Linux), open (macOS), explorer (Windows).
/// The URI is passed as a single argv entry — never through a shell — and only for schemes a
/// terminal has business opening.
/// </summary>
internal static class LinkOpener
{
    private static readonly string[] AllowedSchemes = ["http", "https", "ftp", "ftps", "file", "mailto", "sftp", "ssh", "git"];

    /// <summary>The URI to hand to the opener (www. gets https://), or null if it isn't one we open.</summary>
    public static string? Normalize(string uri)
    {
        uri = uri.Trim();
        if (uri.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) uri = "https://" + uri;
        int colon = uri.IndexOf(':');
        if (colon <= 0) return null;
        string scheme = uri[..colon].ToLowerInvariant();
        if (!AllowedSchemes.Contains(scheme)) return null;
        if (uri.AsSpan().ContainsAny('\0', '\n', '\r')) return null;
        return uri;
    }

    /// <summary>The program that opens files and links on this platform.</summary>
    internal static string SystemOpener()
    {
        if (OperatingSystem.IsWindows()) return "explorer.exe";
        return OperatingSystem.IsMacOS() ? "open" : "xdg-open";
    }

    /// <summary>Starts the opener; returns an error message, or null on success.</summary>
    public static string? Open(string uri)
    {
        if (Normalize(uri) is not { } target) return "Not a link erikterm opens";
        string program = SystemOpener();
        try
        {
            if (Integrations.ToolRunner.ProgramPath(program) is not { } path) return $"{program}: not found";
            var psi = new ProcessStartInfo(path) { UseShellExecute = false };
            psi.ArgumentList.Add(target);
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return $"{program}: {ex.Message}";
        }
    }
}
