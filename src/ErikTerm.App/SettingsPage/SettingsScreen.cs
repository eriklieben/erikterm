using System.Globalization;
using ErikTerm.Config;
using ErikTerm.Ui.Compare;

namespace ErikTerm.App.SettingsPage;

/// <summary>What the settings page needs from the outside: the file, checking a version of it, and an editor.</summary>
internal interface ISettingsHost
{
    /// <summary>The config file, as shown (home-relative).</summary>
    string DisplayPath { get; }
    /// <summary>The file's text; empty when it doesn't exist yet.</summary>
    string Read();
    /// <summary>Saves the text; an error message, or null when it worked.</summary>
    string? Write(string text);
    /// <summary>What erikterm would complain about in this text (the same messages a reload reports).</summary>
    IReadOnlyList<string> Problems(string text);
    /// <summary>The colour schemes there are: from files, then the built-in ones.</summary>
    IReadOnlyList<string> SchemeNames();
    /// <summary>Opens the file in the user's editor on <paramref name="line"/> (0: the top) and returns when it's closed.</summary>
    void EditFile(int line);
}

/// <summary>
/// <c>erikterm settings</c>: every setting with what it does and its value, changed with the arrow keys or the mouse
/// and written straight to the config file, so the open windows pick it up as they would a save.
/// The file and its values are <see cref="SettingsFile"/>'s, the drawing <see cref="SettingsView"/>'s; this is the
/// selection, the search, typing a value and the keys and clicks that change them.
/// </summary>
internal sealed class SettingsScreen : ITuiScreen
{
    private const int ActionSelect = SettingsView.ActionSelect, ActionValue = SettingsView.ActionValue, ActionGroup = SettingsView.ActionGroup;

    private readonly SettingsFile _file;
    private readonly SettingsView _view = new();
    private TuiTheme _theme = PageTheme(TuiTheme.Dark.Base);
    private List<SettingInfo> _visible = [.. SettingsCatalog.All];
    private int _selected;
    private string _query = "";
    private bool _searching;
    private string? _edit; // the text being typed for the selected setting
    private (string Text, bool Error) _status = ("", false);

    public SettingsScreen(ISettingsHost host)
    {
        _file = new SettingsFile(host);
    }

    public bool Done { get; private set; }
    public int ExitCode => 0;
    public SettingInfo Selected => _visible[_selected];
    public IReadOnlyList<SettingInfo> Visible => _visible;
    public string Status => _status.Text;
    public bool Editing => _edit is not null;

    /// <summary>Told the page's outermost colour once it's known (the top and bottom bars, which reach the corners).</summary>
    public Action<uint>? CardColor { get; init; }

    public void OnThemeColor(uint backgroundColor)
    {
        _theme = PageTheme(backgroundColor);
        CardColor?.Invoke(_theme.Crust);
    }

    /// <summary>
    /// The page's colours: on a dark terminal, one darker shade of its own background for the whole page, which erikterm
    /// draws as a rounded card as see-through as the window (one colour, so no part of it is more solid than another).
    /// </summary>
    internal static TuiTheme PageTheme(uint background)
    {
        var theme = TuiTheme.ForBackground(background);
        if (theme != TuiTheme.Dark) return theme;
        uint card = Shade(background, 0.6);
        return theme with { Base = card, Mantle = card, Crust = card };
    }

    /// <summary>The colour with each channel scaled by <paramref name="factor"/>; never the colour itself, so it's never the see-through one.</summary>
    private static uint Shade(uint rgb, double factor)
    {
        uint Channel(int shift) => (uint)Math.Round(((rgb >> shift) & 0xFF) * factor);
        uint shaded = (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
        return shaded == rgb ? 0x0B0B0E : shaded;
    }

    /// <summary>Back in focus: the file may have been edited by hand meanwhile.</summary>
    public void OnFocus(bool focused)
    {
        if (focused && _edit is null) _file.Reload();
    }

    public void Render(Frame frame) =>
        _view.Render(frame, new SettingsViewState(_file, _theme, _visible, _selected, _query, _searching, _edit, _status));

    // ---- Changes -----------------------------------------------------------------------------------------------

    /// <summary>←/→ on a setting: the next or previous choice, a step up or down, on/off.</summary>
    public void Change(int direction)
    {
        var info = Selected;
        switch (info.Kind)
        {
            case SettingKind.Toggle: Set(info, TomlEditor.Literal(!_file.CurrentBool(info))); break;
            case SettingKind.Choice: StepChoice(info, direction); break;
            case SettingKind.Number: StepNumber(info, direction); break;
            default: Activate(); break;
        }
    }

    /// <summary>Enter, a space or a click on the value.</summary>
    public void Activate()
    {
        var info = Selected;
        switch (info.Kind)
        {
            case SettingKind.Toggle or SettingKind.Choice: Change(+1); break;
            case SettingKind.File: EditFile(info); break;
            default: _edit = _file.IsSet(info) || info.Default is not null ? _file.ValueText(info) : ""; break;
        }
    }

    private void StepChoice(SettingInfo info, int direction)
    {
        var choices = _file.ChoicesOf(info);
        if (choices.Count == 0) return;
        string current = _file.ValueText(info);
        int index = -1;
        for (int i = 0; i < choices.Count; i++)
            if (string.Equals(choices[i], current, StringComparison.OrdinalIgnoreCase)) index = i;
        int first = direction > 0 ? 0 : choices.Count - 1; // from a value that isn't one of the choices
        int next = index < 0 ? first : (index + direction + choices.Count) % choices.Count;
        Set(info, TomlEditor.Literal(choices[next]));
    }

    private void StepNumber(SettingInfo info, int direction)
    {
        double value = Math.Round((_file.CurrentNumber(info) + direction * info.Step) / info.Step) * info.Step;
        value = Math.Clamp(Math.Round(value, 6), info.Min, info.Max);
        Set(info, TomlEditor.Literal(value));
    }

    /// <summary>Enter while typing: the typed value, checked like the file would be.</summary>
    private void CommitEdit()
    {
        var info = Selected;
        string typed = _edit!.Trim();
        _edit = null;
        if (typed.Length == 0 && info.Default is null) { Reset(); return; }
        switch (info.Kind)
        {
            case SettingKind.Number: CommitNumber(info, typed); return;
            case SettingKind.List: Set(info, TomlEditor.Literal(typed.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0))); return;
            default: Set(info, TomlEditor.Literal(typed)); return;
        }
    }

    private void CommitNumber(SettingInfo info, string typed)
    {
        if (!double.TryParse(typed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) { Fail($"\"{typed}\" isn't a number"); return; }
        if (number < info.Min || number > info.Max) { Fail($"{info.Label} goes from {TomlEditor.Literal(info.Min)} to {TomlEditor.Literal(info.Max)}"); return; }
        Set(info, TomlEditor.Literal(number));
    }

    /// <summary>r: back to the default, by commenting the line out.</summary>
    public void Reset()
    {
        var info = Selected;
        if (!_file.IsSet(info)) { Say($"{info.Label} is already the default"); return; }
        Save(TomlEditor.Unset(_file.Text, info.Key), $"{info.Key} is back to its default");
    }

    private void Set(SettingInfo info, string literal) => Save(TomlEditor.Set(_file.Text, info.Key, literal), $"{info.Key} = {literal}");

    /// <summary>Writes a changed file unless it adds a problem (then nothing is written and the problem is shown).</summary>
    private void Save(string changed, string done)
    {
        if (_file.TrySave(changed, out string? problem)) Say($"{done}, saved");
        else if (problem is not null) Fail(problem);
    }

    /// <summary>u: the file as it was before the last change.</summary>
    public void Undo()
    {
        if (_file.TryUndo(out string? error)) Say("Undone, saved");
        else if (error is not null) Fail(error);
        else Say("Nothing to undo");
    }

    private void EditFile(SettingInfo? info)
    {
        _file.Edit(info is null ? 0 : _file.LineOf(info));
        Say(_file.Error is null ? "Back from the editor" : "");
    }

    private void Say(string text) => _status = (text, false);
    private void Fail(string text) => _status = (text, true);

    // ---- Moving around -----------------------------------------------------------------------------------------

    private void Select(int index)
    {
        if (_visible.Count == 0) return;
        _selected = Math.Clamp(index, 0, _visible.Count - 1);
        _edit = null;
    }

    private void NextGroup(int direction)
    {
        if (_visible.Count == 0) return;
        string group = Selected.Group;
        int i = _selected;
        while (i + direction >= 0 && i + direction < _visible.Count && _visible[i + direction].Group == group) i += direction;
        if (direction > 0) { Select(i + 1 < _visible.Count ? i + 1 : _selected); return; }
        // Back: the first setting of the previous group (or of this one, when not already there).
        if (i == _selected && i > 0) i = FirstOfGroup(_visible[i - 1].Group);
        Select(i);
    }

    private int FirstOfGroup(string group) => Math.Max(0, _visible.FindIndex(s => s.Group == group));

    /// <summary>Only the settings whose label, key, group or explanation contain every word typed.</summary>
    private void Filter()
    {
        string key = _visible.Count > 0 ? Selected.Key : "";
        var words = _query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _visible = [.. SettingsCatalog.All.Where(s => words.All(w => Matches(s, w)))];
        _selected = Math.Max(0, _visible.FindIndex(s => s.Key == key));
        _view.ScrollToTop();
    }

    private static bool Matches(SettingInfo s, string word) =>
        s.Label.Contains(word, StringComparison.OrdinalIgnoreCase) || s.Key.Contains(word, StringComparison.OrdinalIgnoreCase)
        || s.Group.Contains(word, StringComparison.OrdinalIgnoreCase) || s.Help.Contains(word, StringComparison.OrdinalIgnoreCase);

    // ---- Input -------------------------------------------------------------------------------------------------

    public void OnKey(KeyEvent key)
    {
        if (_edit is not null) { EditKey(key); return; }
        if (_searching) { SearchKey(key); return; }
        _status = ("", false);
        if (key.Key == TuiKey.Char && !key.Ctrl && !key.Alt) { CharKey((char)key.Rune); return; }
        if (key.Key == TuiKey.Char && key.Ctrl && key.Rune == 'c') { Done = true; return; }
        NavigationKey(key.Key);
    }

    private void NavigationKey(TuiKey key)
    {
        switch (key)
        {
            case TuiKey.Up: Select(_selected - 1); break;
            case TuiKey.Down: Select(_selected + 1); break;
            case TuiKey.PageUp: Select(_selected - 5); break;
            case TuiKey.PageDown: Select(_selected + 5); break;
            case TuiKey.Home: Select(0); break;
            case TuiKey.End: Select(_visible.Count - 1); break;
            case TuiKey.Tab: NextGroup(+1); break;
            case TuiKey.BackTab: NextGroup(-1); break;
            case TuiKey.Left: WhenAny(() => Change(-1)); break;
            case TuiKey.Right: WhenAny(() => Change(+1)); break;
            case TuiKey.Enter: WhenAny(Activate); break;
            case TuiKey.Esc: Escape(); break;
        }
    }

    private void CharKey(char c)
    {
        switch (c)
        {
            case 'k': NavigationKey(TuiKey.Up); break;
            case 'j': NavigationKey(TuiKey.Down); break;
            case 'h': NavigationKey(TuiKey.Left); break;
            case 'l': NavigationKey(TuiKey.Right); break;
            case ' ': WhenAny(Activate); break;
            case 'r': WhenAny(Reset); break;
            case 'u': Undo(); break;
            case 'e': EditFile(_visible.Count > 0 ? Selected : null); break;
            case '/': _searching = true; break;
            case 'q': Done = true; break;
        }
    }

    private void WhenAny(Action action)
    {
        if (_visible.Count > 0) action();
    }

    private void Escape()
    {
        if (_query.Length == 0) { Done = true; return; }
        _query = "";
        Filter();
    }

    private void SearchKey(KeyEvent key)
    {
        switch (key.Key)
        {
            case TuiKey.Enter or TuiKey.Down or TuiKey.Tab: _searching = false; return;
            case TuiKey.Esc: _searching = false; _query = ""; Filter(); return;
            case TuiKey.Backspace when _query.Length > 0: _query = _query[..^1]; Filter(); return;
            case TuiKey.Char when !key.Ctrl && !key.Alt: _query += char.ConvertFromUtf32(key.Rune); Filter(); return;
        }
    }

    private void EditKey(KeyEvent key)
    {
        switch (key.Key)
        {
            case TuiKey.Enter: CommitEdit(); return;
            case TuiKey.Esc: _edit = null; return;
            case TuiKey.Backspace when _edit is { Length: > 0 } edit: _edit = edit[..^1]; return;
            case TuiKey.Char when key.Ctrl && key.Rune == 'u': _edit = ""; return;
            case TuiKey.Char when !key.Ctrl && !key.Alt: _edit += char.ConvertFromUtf32(key.Rune); return;
        }
    }

    public void OnPaste(string text)
    {
        if (_edit is not null) _edit += text.ReplaceLineEndings(" ");
        else if (_searching) { _query += text.ReplaceLineEndings(" "); Filter(); }
    }

    public bool WantsPaste => true;

    public void OnMouse(MouseEvent mouse, Frame? lastFrame)
    {
        switch (mouse.Kind)
        {
            case MouseKind.WheelUp: _view.ScrollBy(-3); return;
            case MouseKind.WheelDown: _view.ScrollBy(3); return;
            case MouseKind.Press when mouse.Button == 0 && lastFrame?.HitAt(mouse.X, mouse.Y) is { } hit: OnHit(hit); return;
        }
    }

    private void OnHit(HitRegion hit)
    {
        _searching = false;
        _status = ("", false);
        switch (hit.Action)
        {
            case ActionSelect: Select(hit.Arg); _view.MarkShown(hit.Arg); break;
            case ActionValue: Select(hit.Arg); _view.MarkShown(hit.Arg); Activate(); break;
            case ActionGroup: Select(FirstOfGroup(SettingsCatalog.Groups[hit.Arg])); break;
        }
    }
}
