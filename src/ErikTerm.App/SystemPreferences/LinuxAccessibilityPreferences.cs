using ErikTerm.App.Integrations;
using ErikTerm.Config;

namespace ErikTerm.App.SystemPreferences;

/// <summary>
/// Linux has no one place for these. The XDG desktop portal's Settings interface (org.freedesktop.appearance: contrast,
/// reduced-motion) is asked through gdbus, and GNOME's own keys through gsettings: enable-animations,
/// text-scaling-factor (Large Text) and the a11y high-contrast switch. Both tools come with GLib and are only run when
/// they're installed, as argv lists with a short timeout, off the main thread. KDE and tiling compositors usually have
/// neither key set: the config's reduce_motion / high_contrast are for them.
/// </summary>
internal sealed class LinuxAccessibilityPreferences : IAccessibilityPreferences
{
    private const int TimeoutMs = 1000;

    public bool ReadsSlowly => true;

    public SystemAccessibility Read()
    {
        try
        {
            string portal = Run("gdbus", "call", "--session", "--dest", "org.freedesktop.portal.Desktop",
                "--object-path", "/org/freedesktop/portal/desktop",
                "--method", "org.freedesktop.portal.Settings.ReadAll", "['org.freedesktop.appearance']");
            return PreferenceParsing.Linux(
                PreferenceParsing.PortalUInt(portal, "contrast"),
                PreferenceParsing.PortalUInt(portal, "reduced-motion"),
                PreferenceParsing.GsettingsBool(Gsettings("org.gnome.desktop.interface", "enable-animations")),
                PreferenceParsing.GsettingsBool(Gsettings("org.gnome.desktop.a11y.interface", "high-contrast")),
                PreferenceParsing.GsettingsDouble(Gsettings("org.gnome.desktop.interface", "text-scaling-factor")));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return SystemAccessibility.Unknown;
        }
    }

    private static string Gsettings(string schema, string key) => Run("gsettings", "get", schema, key);

    /// <summary>The program's output, or "" when it isn't installed, failed or took too long.</summary>
    private static string Run(params string[] argv)
    {
        if (!ToolRunner.Has(argv[0])) return "";
        var result = ToolRunner.Run(argv, null, TimeoutMs, maxOutputChars: 16 * 1024);
        return result.Ok ? result.Stdout : "";
    }
}
