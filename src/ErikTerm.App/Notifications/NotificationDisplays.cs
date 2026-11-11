namespace ErikTerm.App.Notifications;

/// <summary>
/// Which display to name for a notification from a window on another display, on Windows, where there are no workspaces.
/// Pure: the monitors and names come from Host/ProcessNotifications.cs.
/// </summary>
internal static class NotificationDisplays
{
    private const int MaxNameChars = 40;

    /// <summary>
    /// The cleaned name of the window's display when it isn't the one in use; null with one display, when either
    /// monitor is unknown (0), when the name is empty, or when another display has the same name.
    /// </summary>
    public static string? OtherDisplayName(nint windowMonitor, nint currentMonitor, string? windowDisplayName, IReadOnlyList<string> allDisplayNames)
    {
        if (allDisplayNames.Count < 2 || windowMonitor == 0 || currentMonitor == 0 || windowMonitor == currentMonitor) return null;
        string name = NotificationText.Clean(windowDisplayName, MaxNameChars);
        if (name.Length == 0) return null;
        int sameName = allDisplayNames.Count(other => string.Equals(NotificationText.Clean(other, MaxNameChars), name, StringComparison.OrdinalIgnoreCase));
        return sameName == 1 ? name : null;
    }

    /// <summary>For the in-window places, next to "Window 2": "On DELL U2720Q".</summary>
    public static string? Label(string? displayName) => displayName is null ? null : $"On {displayName}";
}
