using ErikTerm.App.Native;
using ErikTerm.App.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>
/// The About screen (opened by the ShowAbout action: palette › Help › About erikterm, context menu) and palette › Show
/// Config Problems: every diagnostic of the last config (re)load plus font warnings, as a list in the palette (the
/// toast only has room for the first, and stderr is invisible when erikterm was started from a launcher).
/// </summary>
internal sealed class AboutPanel(IWindowHost window, AboutScreen about, ContextMenu menu, CommandPalette palette, PaneSelect paneSelect)
{
    private AboutInfo? _info;

    private static long Now => Environment.TickCount64;

    public void Show()
    {
        long now = Now;
        menu.Close(now);
        palette.Close(now);
        paneSelect.Close(now);

        int panes = 0;
        for (int i = 0; i < window.TabCount; i++) panes += window.TabLayout(i).Count;
        var crash = CrashLog.Latest();
        string configPath = window.ConfigPath;
        _info = AboutProbe.Collect(window.SdlWindow, window.Renderer, window.Fonts, window.Atlas) with
        {
            FontPoints = window.Settings.FontSize * window.FontZoom,
            WindowOpacity = window.Opaque ? 1 : window.Theme.WindowOpacity,
            Tabs = Math.Max(1, window.TabCount),
            Panes = Math.Max(1, panes),
            ColorScheme = window.Scheme.Name,
            ConfigPath = configPath,
            ConfigExists = File.Exists(configPath),
            ConfigProblems = window.ConfigProblemCount,
            LastCrashLog = crash?.Path,
            LastCrashTime = crash?.Time ?? default,
        };
        about.CopyDiagnostics = CopyDiagnostics;
        about.OpenConfig = window.OpenConfigFile;
        about.Open(_info, window.Theme, now);
        window.Redraw();
    }

    private void CopyDiagnostics()
    {
        if (_info is null) return;
        string text = AboutFormat.Diagnostics(_info);
        Sdl.SetClipboardText(text);
        int lines = text.Count(c => c == '\n');
        window.Toast(ToastKind.Success, "Copied diagnostics", $"{lines} lines on the clipboard");
    }

    /// <summary>Show Config Problems (App.Process.cs): the list in the palette, or a toast when there is none.</summary>
    public void ShowConfigProblems(List<string> problems, bool kept)
    {
        long now = Now;
        if (problems.Count == 0)
        {
            window.Toast(ToastKind.Success, "No config problems", Path.GetFileName(window.ConfigPath));
            return;
        }
        string all = string.Join('\n', problems) + '\n';
        var list = new List<Command>
        {
            new()
            {
                Id = "config-problems-copy", Category = "Config", Icon = Icons.Copy,
                Title = kept ? "Copy All (config not reloaded: previous settings kept)" : "Copy All",
                Run = () => window.CopyText(all, problems.Count == 1 ? "Copied 1 problem" : $"Copied {problems.Count} problems"),
            },
            new() { Id = "config-problems-open", Category = "Config", Title = "Open Configuration File", Icon = Icons.Gear, Run = window.OpenConfigFile },
        };
        for (int i = 0; i < problems.Count; i++)
        {
            string text = problems[i];
            list.Add(new Command
            {
                Id = "config-problem:" + i, Category = "Problem", Title = text, Icon = Icons.Warning,
                Run = () => window.CopyText(text, "Copied"),
            });
        }
        menu.Close(now);
        palette.Open(list, window.Theme, now);
        window.Redraw();
    }
}
