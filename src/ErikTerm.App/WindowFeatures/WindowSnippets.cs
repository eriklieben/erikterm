using ErikTerm.App.Integrations;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>
/// Snippets from the config (<c>[snippets]</c>): "Snippet › name" in the palette and SendSnippet("name") for [keys].
/// Inserting is a paste into the focused pane — sanitized, bracketed when the program asked for it — after filling in
/// <c>{{cwd}}</c> and <c>{{selection}}</c>; Shift+Enter in the palette also presses Enter.
/// </summary>
internal sealed class WindowSnippets(IFeatureWindow window, IPaneInput input, WindowRecent recent, Lock gate)
{
    private void Insert(Snippet snippet, bool run)
    {
        var focused = window.Focused;
        string? selection;
        lock (gate) selection = focused.Selection?.Text(focused.Term);
        input.PasteText(focused, Snippets.Expand(snippet.Text, focused.StartDirectory(null), selection), broadcast: true);
        if (run) recent.PressEnter();
    }

    /// <summary>SendSnippet("name").</summary>
    public void SendNamed(string name)
    {
        var snippet = window.Settings.Snippets.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (snippet is null) { window.Toast(ToastKind.Error, "No such snippet", name); return; }
        Insert(snippet, run: false);
    }

    /// <summary>Palette entries (hook from BuildCommands).</summary>
    public void AddCommands(List<Command> list)
    {
        foreach (var snippet in window.Settings.Snippets)
        {
            var s = snippet;
            list.Add(new Command
            {
                Id = "snippet:" + s.Name, Category = "Snippet", Title = s.Name, Icon = Icons.Snippet,
                Detail = s.Description ?? FirstLine(s.Text),
                RunWith = mods => Insert(s, run: (mods & RunModifiers.Shift) != 0),
            });
        }
    }

    private static string FirstLine(string text)
    {
        int nl = text.IndexOfAny(['\r', '\n']);
        return (nl < 0 ? text : text[..nl] + " …").Trim();
    }
}
