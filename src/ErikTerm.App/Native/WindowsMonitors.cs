using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ErikTerm.App.Native;

/// <summary>Which monitor a window is on, as Windows sees it. Everything here returns 0 off Windows or when it isn't known.</summary>
internal static partial class WindowsMonitors
{
    private const uint DefaultToNull = 0; // MONITOR_DEFAULTTONULL

    [LibraryImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    /// <summary>The monitor of the window with this handle.</summary>
    public static nint OfWindow(nint hwnd) =>
        OperatingSystem.IsWindows() && hwnd != 0 ? MonitorFromWindow(hwnd, DefaultToNull) : 0;

    /// <summary>The monitor of the current foreground window, whichever program it belongs to.</summary>
    public static nint OfForegroundWindow() =>
        OperatingSystem.IsWindows() ? OfWindow(GetForegroundWindow()) : 0;
}
