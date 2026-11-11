using ErikTerm.App.SystemPreferences;
using ErikTerm.Config;

namespace ErikTerm.App.Host;

/// <summary>What <see cref="AccessibilityWatcher"/> needs from the process.</summary>
internal interface IAccessibilityOwner
{
    /// <summary>Wakes the event loop (a background read landed).</summary>
    void Wake();
    /// <summary>SDL is up and the loop can be woken; before that, the first read is collected at startup.</summary>
    bool LoopRunning { get; }
    /// <summary>The session runs on Wayland (decides whether the system's text size applies; see <c>ForThisSession</c>).</summary>
    bool IsWayland { get; }
    /// <summary>The system's preferences changed: the settings are worked out again and go to the windows.</summary>
    void SystemAccessibilityChanged();
}

/// <summary>
/// reduce_motion, high_contrast and text_scale: the system's accessibility preferences (SystemPreferences/), read at
/// start, when a window gets focus (changing them means visiting the system's settings, so coming back is when it
/// matters), on SDL's theme-changed event, and — where reading costs microseconds (Windows, macOS) — every few seconds
/// while the loop runs anyway. Every load of the config goes through <see cref="Apply"/>, which puts
/// <see cref="AccessibilityPolicy"/> on top of the configured settings; the config file is never written. A change goes
/// to the windows like a config reload.
/// </summary>
internal sealed class AccessibilityWatcher(IAccessibilityOwner owner) : ISettingsOverlay
{
    /// <summary>A focus change asks again at most this often.</summary>
    private const int SystemReadIntervalMs = 1000;
    /// <summary>Where a read is cheap, the loop asks this often without any focus change.</summary>
    private const int SystemPollMs = 5000;
    /// <summary>How long the first window waits for a slow read, so it opens at the right text size.</summary>
    private const int FirstReadWaitMs = 150;

    private readonly IAccessibilityPreferences _preferences = AccessibilityPreferences.ForThisPlatform();
    private SystemAccessibility _system = SystemAccessibility.Unknown;
    /// <summary>A background read's result, waiting for the main thread (Linux).</summary>
    private SystemAccessibility? _arrived;
    private Task? _reading;
    private long _readAt;

    private static long Now => Environment.TickCount64;

    /// <summary>What the accessibility settings came to (Theme.From takes HighContrast).</summary>
    public EffectiveAccessibility Effective { get; private set; } = EffectiveAccessibility.None;

    /// <summary>Freshly loaded settings, with the overrides the system asks for.</summary>
    public Settings Apply(Settings configured)
    {
        (var effective, Effective) = AccessibilityPolicy.Apply(configured, _system);
        return effective;
    }

    /// <summary>Hook from Run before SDL starts: a slow read gets going while SDL does.</summary>
    public void StartRead()
    {
        if (_preferences.ReadsSlowly) ReadInBackground();
    }

    /// <summary>Hook from Run before the first window: the system's answer, waited for briefly when it's slow.</summary>
    public void ApplyFirst()
    {
        SystemAccessibility? read;
        if (!_preferences.ReadsSlowly) read = _preferences.Read();
        else
        {
            _reading?.Wait(FirstReadWaitMs);
            read = Interlocked.Exchange(ref _arrived, null);
        }
        _readAt = Now;
        if (read is null) return; // still running: Tick applies it when it lands
        _system = ForThisSession(read);
        owner.SystemAccessibilityChanged(); // no windows yet: only the settings change
    }

    /// <summary>Hook from a window getting focus and the theme-changed event: ask again (not more than once a second).</summary>
    public void RequestRead(bool now = false)
    {
        if (!now && Now - _readAt < SystemReadIntervalMs) return;
        _readAt = Now;
        if (_preferences.ReadsSlowly) ReadInBackground();
        else ApplyRead(_preferences.Read());
    }

    /// <summary>Hook from the event loop: apply a background result, and poll where that's cheap.</summary>
    public void Tick()
    {
        if (Interlocked.Exchange(ref _arrived, null) is { } read) ApplyRead(read);
        else if (!_preferences.ReadsSlowly && Now - _readAt >= SystemPollMs) RequestRead();
    }

    private void ReadInBackground()
    {
        if (_reading is { IsCompleted: false }) return;
        _reading = Task.Run(() =>
        {
            Interlocked.Exchange(ref _arrived, _preferences.Read());
            if (owner.LoopRunning) owner.Wake(); // before SDL is up, ApplyFirst collects it instead
        });
    }

    private void ApplyRead(SystemAccessibility read)
    {
        read = ForThisSession(read);
        if (read == _system) return;
        _system = read;
        owner.SystemAccessibilityChanged();
    }

    /// <summary>
    /// On X11, GNOME already hands its text size to every program through Xft.dpi, which SDL turns into the display
    /// scale: applying it again would scale the text twice. Wayland has no such channel.
    /// </summary>
    private SystemAccessibility ForThisSession(SystemAccessibility read) =>
        OperatingSystem.IsLinux() && !owner.IsWayland && read.TextScale is not null ? read with { TextScale = null } : read;
}
