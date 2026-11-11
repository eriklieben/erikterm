using ErikTerm.App.Ui;
using ErikTerm.Config;

namespace ErikTerm.App.Host;

/// <summary>What <see cref="ProcessConfig"/> needs from the process: waking the loop and telling the windows.</summary>
internal interface IConfigOwner
{
    /// <summary>Wakes the event loop (called from the file watchers' threads).</summary>
    void Wake();
    /// <summary>A reload that took: every window applies the new settings (<paramref name="old"/>: the ones they had).</summary>
    void ApplyReloadedSettings(Settings old);
    void ToastAll(ToastKind kind, string title, string? detail);
    /// <summary>Adds each window's font warnings that aren't in <paramref name="problems"/> yet.</summary>
    void AddFontWarnings(List<string> problems);
}

/// <summary>What every load's settings go through before the windows get them (the system's accessibility preferences).</summary>
internal interface ISettingsOverlay
{
    Settings Apply(Settings configured);
}

/// <summary>
/// The configuration of the process: the settings and key bindings every window uses, the config file and the
/// color scheme files it watches, <c>--set</c> and <c>:set</c> overrides re-applied on every load, a theme picked in
/// the palette, and reporting what is wrong with the file. Editors write in bursts: a change reloads once the file
/// has been quiet for 100 ms (<see cref="ReloadIfDue"/>, on the main thread).
/// </summary>
internal sealed class ProcessConfig : IDisposable
{
    private readonly string _configPath;
    private readonly IConfigOwner _owner;
    private readonly ISettingsOverlay? _overlay;
    /// <summary>The settings as the config file has them, before the overlay.</summary>
    private Settings _configured = new();
    private readonly IReadOnlyList<string> _schemeDirectories;
    private readonly List<ConfigOverride> _sessionOverrides = [];
    private readonly List<SchemeDirectoryWatch> _schemeWatchers = [];
    private IReadOnlyList<ColorScheme> _customSchemes = [];
    private IReadOnlyList<string> _lastDiagnostics = [];
    private FileSystemWatcher? _watcher, _targetWatcher;
    private long _configChangedAt;
    private bool _reloadQuietly;

    /// <param name="overlay">Put on top of every load's settings; null: none.</param>
    /// <param name="schemeDirectories">Where color scheme files are read from; null: the usual places (ColorSchemeFiles.DefaultDirectories).</param>
    public ProcessConfig(string configPath, IReadOnlyList<ConfigOverride> overrides, IConfigOwner owner, ISettingsOverlay? overlay = null,
        IReadOnlyList<string>? schemeDirectories = null)
    {
        _overlay = overlay;
        _configPath = configPath;
        _schemeDirectories = schemeDirectories ?? ColorSchemeFiles.DefaultDirectories(configPath);
        Overrides = overrides;
        _owner = owner;
    }

    private static long Now => Environment.TickCount64;

    public Settings Settings { get; private set; } = new();
    public Dictionary<KeyChord, ActionSpec> Keys { get; private set; } = [];
    public string ConfigPath => _configPath;
    /// <summary>A theme chosen from the palette (any window); applies to all windows until the config reloads.</summary>
    public ColorScheme? SchemeOverride { get; set; }
    /// <summary><c>--set key=value</c> / <c>--config key=value</c>, re-applied on every reload.</summary>
    public IReadOnlyList<ConfigOverride> Overrides { get; }
    /// <summary>The last reload hit a syntax error and kept the settings it had.</summary>
    public bool Kept { get; private set; }
    /// <summary>A change is waiting out its quiet period: the loop polls instead of sleeping.</summary>
    public bool ReloadPending => Volatile.Read(ref _configChangedAt) != 0;

    /// <summary>Startup: reads the file (and says which), before anything else uses the settings.</summary>
    public void LoadInitial() => Settings = Load(initial: true);

    private Settings Load(bool initial = false)
    {
        var result = SettingsLoader.Load(_configPath, LoadOptions());
        if (initial && result.Path is not null) Console.Error.WriteLine($"erikterm: config {result.Path}");
        foreach (var message in result.Diagnostics) Console.Error.WriteLine($"erikterm: {message}");
        _lastDiagnostics = result.Diagnostics;
        if (!initial)
        {
            // A half-saved file (an editor's autosave) mustn't reset the font, colors and keys of every window.
            (var settings, bool kept) = SettingsLoader.Reloaded(Settings, result);
            Kept = kept;
            if (Kept)
            {
                Console.Error.WriteLine("erikterm: config not reloaded: keeping the previous settings until the syntax error is fixed");
                return settings;
            }
        }
        _customSchemes = result.CustomSchemes;
        Keys = result.Settings.EffectiveKeys().ToDictionary(b => b.Chord, b => b.Action);
        _configured = result.Settings;
        return _overlay?.Apply(_configured) ?? _configured; // e.g. the system's reduce motion, contrast, text size on top
    }

    /// <summary>The overlay changed its mind (the system's preferences changed): the settings again, from the file's. Returns the ones before.</summary>
    public Settings Reapply()
    {
        var old = Settings;
        Settings = _overlay?.Apply(_configured) ?? _configured;
        return old;
    }

    private LoadOptions LoadOptions() => new()
    {
        Overrides = AllOverrides(), // --set, then :set
        SchemeDirectories = _schemeDirectories,
    };

    /// <summary>The overrides every load applies: --set first, then :set (later ones win).</summary>
    private IReadOnlyList<ConfigOverride> AllOverrides() => _sessionOverrides.Count == 0 ? Overrides : [.. Overrides, .. _sessionOverrides];

    /// <summary>:set — applies to every window now (a reload) and on every later reload, until erikterm exits.</summary>
    public void AddSessionOverride(ConfigOverride o)
    {
        _sessionOverrides.Add(o);
        OnConfigChanged();
    }

    /// <summary>Schemes from files first, then the built-ins they don't shadow (the palette's Theme list).</summary>
    public List<ColorScheme> AllSchemes() => ColorSchemes.WithCustom(_customSchemes);

    // ---- Watching ------------------------------------------------------------------------------------------------

    /// <summary>Watches the config file (and where it links to), then tells about the problems the first load had.</summary>
    public void Watch()
    {
        string? dir = Path.GetDirectoryName(_configPath);
        if (dir is null) return;
        try
        {
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => OnConfigChanged();
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (_, _) => OnConfigChanged();
            _watcher.EnableRaisingEvents = true;
            WatchTarget(changed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"erikterm: not watching config: {ex.Message}");
        }
        ShowProblems();
    }

    /// <summary>
    /// A config file that is a symlink (e.g. into a dotfiles repo) changes where the link points, not in the link's
    /// directory, so the target's directory is watched as well.
    /// </summary>
    private void WatchTarget(FileSystemEventHandler changed)
    {
        string? target = SettingsLoader.ConfigLinkTarget(_configPath);
        if (target is null || Path.GetDirectoryName(target) is not { } targetDir || !Directory.Exists(targetDir)) return;
        _targetWatcher = new FileSystemWatcher(targetDir, Path.GetFileName(target))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _targetWatcher.Changed += changed;
        _targetWatcher.Created += changed;
        _targetWatcher.Renamed += (_, _) => OnConfigChanged();
        _targetWatcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Reload when a scheme file changes, in every colors directory — also ones created while erikterm runs (the
    /// nearest existing parent is watched until they appear; see SchemeDirectoryWatch).
    /// </summary>
    public void WatchSchemeFiles()
    {
        foreach (string dir in _schemeDirectories)
            _schemeWatchers.Add(new SchemeDirectoryWatch(dir, () => OnConfigChanged()));
    }

    /// <summary>The config file changed (or should be read again): any thread.</summary>
    public void OnConfigChanged()
    {
        Volatile.Write(ref _configChangedAt, Now);
        _owner.Wake();
    }

    /// <summary>New scheme files arrived and the caller says so itself: reload without the "Configuration reloaded" toast.</summary>
    public void ReloadSchemesQuietly()
    {
        _reloadQuietly = true;
        OnConfigChanged();
    }

    // ---- Reloading -----------------------------------------------------------------------------------------------

    /// <summary>Hook from the event loop: reloads once the file has been quiet for 100 ms. Applies to every window.</summary>
    public void ReloadIfDue()
    {
        long at = Volatile.Read(ref _configChangedAt);
        if (at == 0 || Now - at < 100) return;
        Volatile.Write(ref _configChangedAt, 0);

        (Rendering.FontLocator.Current as Rendering.FontconfigLocator)?.ForgetLookups();
        var old = Settings;
        Settings = Load();
        if (!Kept)
        {
            SchemeOverride = null;
            _owner.ApplyReloadedSettings(old);
        }

        bool quiet = _reloadQuietly;
        _reloadQuietly = false;
        if (!ShowProblems() && !quiet) _owner.ToastAll(ToastKind.Success, "Configuration reloaded", Path.GetFileName(_configPath));
    }

    /// <summary>Everything worth telling about the configuration: the last load's diagnostics, then each window's font warnings.</summary>
    public List<string> Problems()
    {
        var list = new List<string>(_lastDiagnostics);
        _owner.AddFontWarnings(list);
        return list;
    }

    /// <summary>Toasts the first problem (palette: Show Config Problems lists them all). False if there are none.</summary>
    private bool ShowProblems()
    {
        var problems = Problems();
        if (problems.Count == 0) return false;
        string first = problems[0];
        int colon = first.IndexOf(": ", StringComparison.Ordinal);
        string where = colon > 0 && first[..colon].Contains(Path.GetFileName(_configPath), StringComparison.Ordinal) ? Path.GetFileName(first[..colon]) : "";
        string what = where.Length > 0 ? first[(colon + 2)..] : first;
        // The detail line gets cut to the toast's width: the count goes in the title.
        string more = problems.Count > 1 ? $" (+{problems.Count - 1} more: palette › Show Config Problems)" : "";
        string title;
        if (Kept) title = "Config not reloaded: previous settings kept";
        else title = where.Length > 0 ? $"Config problem {where}" : "Config problem";
        _owner.ToastAll(ToastKind.Error, title + more, what);
        return true;
    }

    /// <summary>The file watchers and the scheme directories' watchers.</summary>
    public void Dispose()
    {
        _watcher?.Dispose();
        _targetWatcher?.Dispose();
        foreach (var w in _schemeWatchers) w.Dispose();
        _schemeWatchers.Clear();
    }
}
