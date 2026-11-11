using ErikTerm.Config;

namespace ErikTerm.App.SystemPreferences;

/// <summary>Where the operating system's accessibility preferences come from (Host/AccessibilityWatcher.cs reads them).</summary>
internal interface IAccessibilityPreferences
{
    /// <summary>The read runs programs and can take a while: the app calls <see cref="Read"/> off the main thread then.</summary>
    bool ReadsSlowly { get; }

    /// <summary>The preferences as they are now. Never throws; what can't be read is null.</summary>
    SystemAccessibility Read();
}

internal static class AccessibilityPreferences
{
    public static IAccessibilityPreferences ForThisPlatform()
    {
        if (OperatingSystem.IsWindows()) return new WindowsAccessibilityPreferences();
        if (OperatingSystem.IsMacOS()) return new MacAccessibilityPreferences();
        if (OperatingSystem.IsLinux()) return new LinuxAccessibilityPreferences();
        return new NoAccessibilityPreferences();
    }
}

/// <summary>A platform with nothing to ask: no preferences, so "auto" means off.</summary>
internal sealed class NoAccessibilityPreferences : IAccessibilityPreferences
{
    public bool ReadsSlowly => false;
    public SystemAccessibility Read() => SystemAccessibility.Unknown;
}
