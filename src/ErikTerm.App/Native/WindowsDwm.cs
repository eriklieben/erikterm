using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ErikTerm.App.Native;

/// <summary>
/// The two bits of window styling Windows 11 will do for us: rounded corners and a one-pixel border in a color we
/// choose — the frame Windows Terminal wears. Both are asked of the compositor rather than drawn, so the corners
/// clip the window (shadow included) instead of leaving our own painted corners to fight the system's, and the
/// border lands outside the client area where no cell can reach it.
/// <para>
/// Everything here is a no-op off Windows, and on Windows 10, where DwmSetWindowAttribute returns a failure code
/// for both attributes and the window simply stays square. Nothing else depends on it, so failures are ignored.
/// </para>
/// </summary>
internal static partial class WindowsDwm
{
    private const int WindowCornerPreference = 33; // DWMWA_WINDOW_CORNER_PREFERENCE (Windows 11 22000+)
    private const int BorderColor = 34;            // DWMWA_BORDER_COLOR
    private const int SystemBackdropType = 38;     // DWMWA_SYSTEMBACKDROP_TYPE (Windows 11 22621+)
    private const int UseImmersiveDarkMode = 20;   // DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 1809+)

    /// <summary>
    /// What the compositor paints behind the window's translucent pixels — the blur Windows Terminal calls
    /// "acrylic". Only what we don't paint over shows, so it is only visible while window_background_opacity
    /// is below 1.
    /// </summary>
    public enum Backdrop
    {
        /// <summary>DWMSBT_NONE: the desktop shows through unblurred, as it did before.</summary>
        None = 1,
        /// <summary>DWMSBT_MAINWINDOW: Mica, which tints from the wallpaper rather than what is behind.</summary>
        Mica = 2,
        /// <summary>DWMSBT_TRANSIENTWINDOW: acrylic, a live blur of whatever is behind the window.</summary>
        Acrylic = 3,
    }

    private const int CornerDefault = 0, CornerRound = 2;

    /// <summary>DWMWA_COLOR_DEFAULT: back to the system's own border color.</summary>
    private const uint ColorDefault = 0xFFFFFFFF;

    [LibraryImport("dwmapi.dll")]
    [SupportedOSPlatform("windows")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>Rounds (or un-rounds) the window's corners. Safe to call on any platform.</summary>
    public static void SetRoundedCorners(nint hwnd, bool rounded)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return;
        int value = rounded ? CornerRound : CornerDefault;
        Try(hwnd, WindowCornerPreference, ref value);
    }

    /// <summary>
    /// Paints the window's border in <paramref name="rgb"/> (0xRRGGBB), or hands it back to the system when null.
    /// DWM wants a COLORREF, which orders the channels the other way round.
    /// </summary>
    public static void SetBorderColor(nint hwnd, uint? rgb)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return;
        unchecked
        {
            uint colorRef = rgb is { } c ? (c & 0xFFu) << 16 | c & 0xFF00u | (c >> 16) & 0xFFu : ColorDefault;
            int value = (int)colorRef;
            Try(hwnd, BorderColor, ref value);
        }
    }

    /// <summary>
    /// Opts the window into DWM's dark palette, which decides the tint the backdrop mixes into its blur. It is a
    /// per-window choice that defaults to off, so a window that never asks gets the light tint even on a desktop
    /// in dark mode — that is what washes a dark color scheme out to grey behind acrylic. Driven by the scheme
    /// rather than the system setting, because it is erikterm's own background it has to sit under.
    /// </summary>
    public static void SetDarkMode(nint hwnd, bool dark)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return;
        int value = dark ? 1 : 0;
        Try(hwnd, UseImmersiveDarkMode, ref value);
    }

    /// <summary>
    /// Asks the compositor to blur what is behind the window. Older Windows 11 builds and Windows 10 don't know
    /// the attribute and leave the window as it was, which costs nothing but the blur.
    /// </summary>
    public static void SetBackdrop(nint hwnd, Backdrop backdrop)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return;
        int value = (int)backdrop;
        Try(hwnd, SystemBackdropType, ref value);
    }

    [SupportedOSPlatform("windows")]
    private static void Try(nint hwnd, int attribute, ref int value)
    {
        try
        {
            DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch (DllNotFoundException) { /* no dwmapi.dll: nothing to style */ }
        catch (EntryPointNotFoundException) { /* an older dwmapi.dll: nothing to style */ }
    }
}
