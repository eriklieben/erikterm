using ErikTerm.App.Integrations;
using ErikTerm.App.Ui;
using ErikTerm.App.WindowFeatures;
using ErikTerm.Config;

namespace ErikTerm.App.Windowing;

/// <summary>
/// Browse Themes…: every color scheme (built in, your colors directories, wezterm's) in the palette with live preview,
/// and the opt-in download of the iTerm2-Color-Schemes catalogue (Integrations/ThemeCatalog.cs): nothing touches the
/// network until the bar's [Download] is pressed; the schemes land in their own folder and are picked up like any other
/// scheme file.
/// </summary>
internal sealed class WindowThemes(IWindowHost window, ContextMenu menu, CommandPalette palette, Toasts toasts)
{
    private const string ThemeBarKey = "theme-download";
    private const int ThemeCatalogSize = 900; // about; the bar says "~" (the repository's wezterm/ folder)
    private static readonly (string[] Keys, string Label)[] ThemeHints = [(["↵"], "apply")];

    private int _themeDone, _themeTotal, _themeShown = -1;
    private bool _themeDownloadHere;

    private static long Now => Environment.TickCount64;

    private static string ShortPath(string path) =>
        FileRefResolver.ShortDisplay(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>One theme, previewed while highlighted (the palette's PreviewChanged finds it by its id).</summary>
    public Command ThemeCommand(ColorScheme s, string category) => new()
    {
        Id = "scheme:" + s.Name, Category = category, Title = s.Name + (s.Name == window.Scheme.Name && window.SchemePreview is null ? "  (current)" : ""),
        Icon = Icons.Color,
        Preview = () => { },
        Run = () =>
        {
            window.SchemeOverride = s;
            window.SchemePreview = null;
            window.ApplySchemeToAllWindows(); // the override is process-wide
            window.Toast(ToastKind.Success, $"Theme: {s.Name}", $"Set color_scheme = \"{s.Name}\" in the config to keep it");
        },
    };

    /// <summary>Where a scheme comes from, as a category in the browser.</summary>
    private static string ThemeSource(ColorScheme s)
    {
        if (s.SourcePath is not { } path) return "Built-in";
        string? dir = Path.GetDirectoryName(path);
        if (Path.GetFileName(dir) == ColorSchemeFiles.CatalogFolderName) return ColorSchemeFiles.CatalogFolderName;
        return Path.GetFileName(Path.GetDirectoryName(dir)) == "wezterm" ? "wezterm" : "Colors";
    }

    /// <summary>The BrowseThemes action.</summary>
    public void OpenBrowser()
    {
        long now = Now;
        var list = new List<Command>
        {
            new()
            {
                Id = "theme-download", Category = "Themes", Title = "Download More Color Schemes…", Icon = Icons.CloudDownload,
                Detail = "iTerm2-Color-Schemes", Run = ConfirmDownload, IsEnabled = () => !window.ThemeDownloadRunning,
            },
        };
        foreach (var scheme in window.AllSchemes()) list.Add(ThemeCommand(scheme, ThemeSource(scheme)));
        menu.Close(now);
        palette.Open(list, window.Theme, now, new CommandPalette.Options { Placeholder = "Search themes…", Noun = "themes", Hints = ThemeHints });
    }

    /// <summary>Asks on the info bar; nothing has touched the network yet.</summary>
    private void ConfirmDownload()
    {
        long now = Now;
        window.InfoBar.Show(new InfoBarMessage
        {
            Key = ThemeBarKey, Tone = InfoBarTone.Accent, Icon = Icons.CloudDownload,
            Text = $"Download ~{ThemeCatalogSize} color schemes from github.com/{ThemeCatalog.Repository}?",
            Detail = $"MIT · into {ShortPath(ColorSchemeFiles.CatalogDirectory(window.ConfigPath))}",
            Buttons =
            [
                new InfoBarButton("Download", StartDownload, primary: true),
                new InfoBarButton("Cancel", DismissBar),
            ],
            Dismissed = DismissBar,
        }, window.Theme, now);
        window.Redraw();
    }

    private void DismissBar()
    {
        window.InfoBar.Remove(ThemeBarKey, Now);
        window.Redraw();
    }

    private void StartDownload()
    {
        if (window.ThemeDownloadRunning) return;
        window.ThemeDownloadRunning = true;
        _themeDownloadHere = true;
        _themeDone = _themeTotal = 0;
        _themeShown = -1;
        ShowProgress(Now);
        string target = ColorSchemeFiles.CatalogDirectory(window.ConfigPath);
        _ = Task.Run(() =>
        {
            ThemeCatalog.Result result;
            try
            {
                result = ThemeCatalog.Download(ThemeCatalog.GitFetch, target, (done, total) =>
                {
                    Volatile.Write(ref _themeTotal, total);
                    Volatile.Write(ref _themeDone, done);
                    if (done % 8 == 0 || done == total) window.Wake();
                });
            }
            catch (Exception ex) { result = new ThemeCatalog.Result(0, 0, ex.Message); }
            window.Post(() => OnDownloadDone(result));
            window.ThemeDownloadRunning = false;
            window.Wake();
        });
    }

    /// <summary>Hook from Tick: the bar's "N of M" while a download started here is running.</summary>
    public void Tick(long now)
    {
        if (!_themeDownloadHere) return;
        int done = Volatile.Read(ref _themeDone);
        if (done != _themeShown) ShowProgress(now);
    }

    private void ShowProgress(long now)
    {
        int done = Volatile.Read(ref _themeDone), total = Volatile.Read(ref _themeTotal);
        _themeShown = done;
        window.InfoBar.Show(new InfoBarMessage
        {
            Key = ThemeBarKey, Tone = InfoBarTone.Accent, Icon = Icons.CloudDownload,
            Text = "Downloading color schemes…",
            Detail = total > 0 ? $"{done} of {total}" : "fetching from GitHub",
            Dismissible = false,
        }, window.Theme, now);
        window.Redraw();
    }

    private void OnDownloadDone(ThemeCatalog.Result result)
    {
        _themeDownloadHere = false;
        DismissBar();
        if (result.Error is { } error)
        {
            window.Toast(ToastKind.Error, "Color schemes not downloaded", error);
            return;
        }
        window.ReloadSchemesQuietly(); // the toast below says it all
        toasts.Show(ToastKind.Success, $"Downloaded {result.Saved} color schemes",
            result.Skipped > 0 ? $"{result.Skipped} not usable, skipped" : ShortPath(ColorSchemeFiles.CatalogDirectory(window.ConfigPath)),
            window.Theme, Now, "Browse", OpenBrowser);
    }
}
