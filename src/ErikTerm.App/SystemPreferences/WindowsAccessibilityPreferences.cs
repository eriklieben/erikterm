using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ErikTerm.Config;
using Microsoft.Win32;

namespace ErikTerm.App.SystemPreferences;

/// <summary>
/// Windows: "Animation effects" (SPI_GETCLIENTAREAANIMATION), a contrast theme (SPI_GETHIGHCONTRAST, and then the
/// theme's colours from GetSysColor) and "Make text bigger" (HKCU\Software\Microsoft\Accessibility TextScaleFactor, the
/// percentage UISettings.TextScaleFactor reports, without WinRT). All of it takes microseconds, so it's read on the
/// main thread. The text size is apart from the display scaling, which SDL already gives the window.
/// </summary>
internal sealed partial class WindowsAccessibilityPreferences : IAccessibilityPreferences
{
    public bool ReadsSlowly => false;

    public SystemAccessibility Read()
    {
        if (!OperatingSystem.IsWindows()) return SystemAccessibility.Unknown;
        try
        {
            bool? highContrast = HighContrastOn();
            return new SystemAccessibility
            {
                ReduceMotion = AnimationsOn() is { } on ? !on : null,
                HighContrast = highContrast,
                TextScale = TextScale(),
                HighContrastColors = highContrast == true ? ThemeColors() : null,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return SystemAccessibility.Unknown;
        }
    }

    private const uint SpiGetHighContrast = 0x0042, SpiGetClientAreaAnimation = 0x1042;
    private const uint HcfHighContrastOn = 0x1;
    private const int ColorWindow = 5, ColorWindowText = 8, ColorHighlight = 13, ColorHighlightText = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [SupportedOSPlatform("windows")]
    private static bool? AnimationsOn()
    {
        int enabled = 0;
        return SystemParametersInfoW(SpiGetClientAreaAnimation, 0, ref enabled, 0) != 0 ? enabled != 0 : null;
    }

    [SupportedOSPlatform("windows")]
    private static bool? HighContrastOn()
    {
        var info = new HighContrastInfo { Size = (uint)Unsafe.SizeOf<HighContrastInfo>() };
        return SystemParametersInfoW(SpiGetHighContrast, info.Size, ref info, 0) != 0 ? (info.Flags & HcfHighContrastOn) != 0 : null;
    }

    [SupportedOSPlatform("windows")]
    private static SystemColors ThemeColors() => new(
        Foreground: PreferenceParsing.ColorRefToRgb(GetSysColor(ColorWindowText)),
        Background: PreferenceParsing.ColorRefToRgb(GetSysColor(ColorWindow)),
        SelectionBg: PreferenceParsing.ColorRefToRgb(GetSysColor(ColorHighlight)),
        SelectionFg: PreferenceParsing.ColorRefToRgb(GetSysColor(ColorHighlightText)),
        Accent: PreferenceParsing.ColorRefToRgb(GetSysColor(ColorHighlight)));

    [SupportedOSPlatform("windows")]
    private static double? TextScale()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
        return PreferenceParsing.WindowsTextScale(key?.GetValue("TextScaleFactor"));
    }

    // SystemParametersInfo writes the answer into the value it is given, one overload per kind of answer read here.
    [LibraryImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial int SystemParametersInfoW(uint action, uint param, ref int value, uint winIni);

    [LibraryImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial int SystemParametersInfoW(uint action, uint param, ref HighContrastInfo value, uint winIni);

    [LibraryImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial uint GetSysColor(int index);
}
