using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui.Compare;

namespace ErikTerm.App.SettingsPage;

/// <summary>What the settings page shows right now (<see cref="SettingsScreen"/>'s state, for <see cref="SettingsView"/>).</summary>
/// <param name="Edit">The text being typed for the selected setting, or null.</param>
internal readonly record struct SettingsViewState(SettingsFile File, TuiTheme Theme, List<SettingInfo> Visible, int Selected, string Query, bool Searching,
    string? Edit, (string Text, bool Error) Status);

/// <summary>
/// Draws the settings page: a header, the search line, the groups on the left, the settings with their explanations,
/// a status line and the keys. Keeps the scroll position; the screen's state comes in with each frame.
/// </summary>
internal sealed class SettingsView
{
    /// <summary>What a click on a hit region does (its argument: the entry, or the group).</summary>
    public const int ActionSelect = 1, ActionValue = 2, ActionGroup = 3;

    private const int NavWidth = 24, MinWidthForNav = 90;

    private enum LineKind { Group, Label, Help, Blank }

    private readonly record struct Line(int Entry, LineKind Kind, string Text);

    /// <summary>The selection the scroll position was last brought to, so the wheel can scroll away from it.</summary>
    private int _shownSelected = -1;
    private int _scroll;

    // The frame being drawn.
    private SettingsFile _file = null!;
    private TuiTheme _theme = null!;
    private List<SettingInfo> _visible = [];
    private int _selected;
    private string? _edit;

    /// <summary>The mouse wheel: <paramref name="lines"/> up (negative) or down.</summary>
    public void ScrollBy(int lines) => _scroll = Math.Max(0, _scroll + lines);

    /// <summary>The list changed (a search): back to the top.</summary>
    public void ScrollToTop() => _scroll = 0;

    /// <summary>The selection is where the user clicked: no scrolling to bring it into view.</summary>
    public void MarkShown(int selected) => _shownSelected = selected;

    public void Render(Frame frame, in SettingsViewState state)
    {
        (_file, _theme, _visible, _selected, _edit) = (state.File, state.Theme, state.Visible, state.Selected, state.Edit);
        frame.Clear(_theme.Text, _theme.Base);
        DrawHeader(frame);
        DrawSearch(frame, 1, state.Query, state.Searching);
        int top = 2, height = Math.Max(0, frame.Height - 4);
        int nav = frame.Width >= MinWidthForNav ? NavWidth : 0;
        if (nav > 0) DrawNav(frame, top, height);
        DrawList(frame, nav, top, frame.Width - nav, height);
        DrawStatus(frame, frame.Height - 2, state.Status);
        DrawFooter(frame, frame.Height - 1, state.Searching);
    }

    private void DrawHeader(Frame f)
    {
        f.Fill(0, 0, f.Width, 1, _theme.Text, _theme.Crust);
        int x = f.Text(1, 0, f.Width - 2, "Settings", _theme.Text, _theme.Crust, CellAttrs.Bold);
        x = f.Text(x + 2, 0, f.Width - x - 2, _file.DisplayPath, _theme.Subtext, _theme.Crust);
        string right = _file.Error is not null ? "syntax error in the file" : ChangedCount();
        int width = Width(right);
        if (x + 2 + width < f.Width) f.Text(f.Width - width - 1, 0, width, right, _file.Error is not null ? _theme.Red : _theme.Overlay, _theme.Crust);
    }

    private string ChangedCount()
    {
        int n = SettingsCatalog.All.Count(_file.IsSet);
        return n switch { 0 => "all defaults", 1 => "1 setting changed", _ => $"{n} settings changed" };
    }

    private void DrawSearch(Frame f, int y, string query, bool searching)
    {
        f.Fill(0, y, f.Width, 1, _theme.Text, _theme.Mantle);
        if (!searching && query.Length == 0)
        {
            f.Text(1, y, f.Width - 2, "/  search settings", _theme.Overlay, _theme.Mantle);
            return;
        }
        int x = f.Text(1, y, f.Width - 2, "/  ", _theme.Blue, _theme.Mantle);
        x = f.Text(x, y, f.Width - x - 1, query, _theme.Text, _theme.Mantle);
        if (searching) f.Text(x, y, 1, "▏", _theme.Blue, _theme.Mantle);
        string count = $"{_visible.Count} of {SettingsCatalog.All.Count}";
        if (x + 4 + count.Length < f.Width) f.Text(f.Width - count.Length - 1, y, count.Length, count, _theme.Overlay, _theme.Mantle);
    }

    private void DrawNav(Frame f, int top, int height)
    {
        f.Fill(0, top, NavWidth, height, _theme.Subtext, _theme.Mantle);
        string current = _visible.Count > 0 ? _visible[_selected].Group : "";
        for (int g = 0; g < SettingsCatalog.Groups.Count && g + 1 < height; g++)
            DrawGroup(f, g, top + 1 + g, current);
    }

    /// <summary>Group <paramref name="g"/>'s line of the navigation: highlighted when it is <paramref name="current"/>, dimmed when the search hides all of it.</summary>
    private void DrawGroup(Frame f, int g, int y, string current)
    {
        string group = SettingsCatalog.Groups[g];
        bool on = group == current;
        bool any = _visible.Any(s => s.Group == group);
        uint bg = on ? _theme.Blue : _theme.Mantle;
        uint fg = _theme.Surface1;
        if (on) fg = _theme.OnAccent;
        else if (any) fg = _theme.Subtext;
        f.Fill(1, y, NavWidth - 2, 1, fg, bg);
        f.Text(2, y, NavWidth - 5, group, fg, bg, on ? CellAttrs.Bold : CellAttrs.None);
        if (SettingsCatalog.All.Any(s => s.Group == group && _file.IsSet(s))) f.Text(NavWidth - 3, y, 1, "•", on ? _theme.OnAccent : _theme.Peach, bg);
        if (any) f.AddHit(1, y, NavWidth - 2, 1, ActionGroup, g);
    }

    private void DrawList(Frame f, int x, int top, int width, int height)
    {
        if (_visible.Count == 0)
        {
            f.Text(x + 3, top + 1, width - 4, "No setting matches. Esc clears the search.", _theme.Subtext, _theme.Base);
            return;
        }
        var lines = BuildLines(width);
        if (_selected != _shownSelected)
        {
            BringIntoView(lines, height);
            _shownSelected = _selected;
        }
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, lines.Count - height));
        for (int row = 0; row < height && _scroll + row < lines.Count; row++)
            DrawLine(f, x, top + row, width, lines[_scroll + row]);
    }

    private List<Line> BuildLines(int width)
    {
        var lines = new List<Line>();
        for (int i = 0; i < _visible.Count; i++)
        {
            var info = _visible[i];
            if (i == 0 || _visible[i - 1].Group != info.Group)
            {
                if (i > 0) lines.Add(new Line(-1, LineKind.Blank, ""));
                lines.Add(new Line(-1, LineKind.Group, info.Group));
            }
            lines.Add(new Line(i, LineKind.Label, ""));
            foreach (string help in Wrap(info.Help, Math.Max(10, width - 6))) lines.Add(new Line(i, LineKind.Help, help));
            lines.Add(new Line(-1, LineKind.Blank, ""));
        }
        return lines;
    }

    private void BringIntoView(List<Line> lines, int height)
    {
        int first = lines.FindIndex(l => l.Entry == _selected);
        int last = lines.FindLastIndex(l => l.Entry == _selected);
        if (first < 0) return;
        if (first > 0 && lines[first - 1].Kind == LineKind.Group) first--;
        if (first < _scroll) _scroll = first;
        else if (last + 1 >= _scroll + height) _scroll = Math.Min(first, last + 2 - height);
    }

    private void DrawLine(Frame f, int x, int y, int width, Line line)
    {
        bool selected = line.Entry == _selected && line.Kind is LineKind.Label or LineKind.Help;
        uint bg = selected ? _theme.Surface0 : _theme.Base;
        f.Fill(x, y, width, 1, _theme.Text, bg);
        // The row's region first: the value's, added by the label, is on top of it (Frame.HitAt takes the last one).
        if (line.Entry >= 0) f.AddHit(x, y, width, 1, ActionSelect, line.Entry);
        switch (line.Kind)
        {
            case LineKind.Group:
                f.Text(x + 2, y, width - 4, line.Text.ToUpperInvariant(), _theme.Blue, bg, CellAttrs.Bold);
                break;
            case LineKind.Label:
                DrawLabel(f, x, y, width, line.Entry, bg);
                break;
            case LineKind.Help:
                f.Text(x + 4, y, width - 6, line.Text, _theme.Subtext, bg);
                break;
        }
    }

    private void DrawLabel(Frame f, int x, int y, int width, int entry, uint bg)
    {
        var info = _visible[entry];
        bool selected = entry == _selected;
        var (value, valueFg, valueBg) = ValueDisplay(info, selected);
        int valueWidth = Math.Min(Width(value), Math.Max(8, width / 2));
        int valueX = x + width - 2 - valueWidth;
        if (_file.IsSet(info)) f.Text(x + 2, y, 1, "•", _theme.Peach, bg);
        int cx = f.Text(x + 4, y, valueX - x - 6, info.Label, _theme.Text, bg, CellAttrs.Bold);
        cx = f.Text(cx + 2, y, Math.Max(0, valueX - cx - 4), info.Key, _theme.Mauve, bg);
        if (info.Platforms is not null)
            f.Text(cx + 2, y, Math.Max(0, valueX - cx - 4), info.PlatformNote, info.AppliesHere ? _theme.Overlay : _theme.Peach, bg);
        f.Text(valueX, y, valueWidth, value, valueFg, valueBg);
        f.AddHit(valueX - 1, y, valueWidth + 2, 1, ActionValue, entry);
    }

    /// <summary>The value at the right of a setting's first line, and its colours.</summary>
    private (string Text, uint Fg, uint Bg) ValueDisplay(SettingInfo info, bool selected)
    {
        uint bg = selected ? _theme.Surface0 : _theme.Base;
        if (selected && _edit is not null)
        {
            string shown = _edit.Length > 40 ? "…" + _edit[^39..] : _edit;
            return (" " + shown + "▏ ", _theme.Text, _theme.Surface1);
        }
        switch (info.Kind)
        {
            case SettingKind.Toggle:
                return _file.CurrentBool(info) ? ("● on", _theme.Green, bg) : ("○ off", _theme.Overlay, bg);
            case SettingKind.File:
                return ("edit in the file ›", selected ? _theme.Blue : _theme.Overlay, bg);
        }
        return ValueText(info, selected, bg);
    }

    /// <summary><see cref="ValueDisplay"/> for a setting whose value is text: a choice, a number, a list, text.</summary>
    private (string Text, uint Fg, uint Bg) ValueText(SettingInfo info, bool selected, uint bg)
    {
        if (!_file.IsSet(info) && info.Default is null) return (info.DefaultLabel ?? "", _theme.Overlay, bg);
        string text = _file.ValueText(info) + UnitSuffix(info);
        if (info.Kind is SettingKind.Text or SettingKind.List && text.Length == 0) return (info.DefaultLabel ?? "empty", _theme.Overlay, bg);
        if (selected && info.Kind is SettingKind.Choice or SettingKind.Number) text = "‹ " + text + " ›";
        return (text, selected ? _theme.Blue : _theme.Text, bg);
    }

    /// <summary>" ms" and the like after a number with a unit; empty for everything else.</summary>
    private static string UnitSuffix(SettingInfo info) => info.Kind == SettingKind.Number && info.Unit.Length > 0 ? " " + info.Unit : "";

    private void DrawStatus(Frame f, int y, (string Text, bool Error) status)
    {
        if (y < 2) return;
        var (text, color) = StatusLine(status);
        f.Text(2, y, f.Width - 3, text, color, _theme.Base);
    }

    private (string Text, uint Color) StatusLine((string Text, bool Error) status)
    {
        if (_file.Error is { } error) return ($"The file has a syntax error ({error}). e opens it in your editor.", _theme.Red);
        if (status.Text.Length > 0) return (status.Text, status.Error ? _theme.Red : _theme.Green);
        if (_visible.Count == 0) return ("", _theme.Subtext);
        var info = _visible[_selected];
        if (!info.AppliesHere) return ($"{info.PlatformNote}: no effect on this computer, but kept in the file for the others.", _theme.Peach);
        string shown = info.Default is { Length: > 0 } d ? d + UnitSuffix(info) : info.DefaultLabel ?? "none";
        return ($"Default: {shown}", _theme.Overlay);
    }

    private void DrawFooter(Frame f, int y, bool searching)
    {
        f.Fill(0, y, f.Width, 1, _theme.Subtext, _theme.Crust);
        int x = 1;
        foreach (string item in FooterKeys(searching))
        {
            if (x + Width(item) + 1 >= f.Width) break;
            x = f.Text(x, y, f.Width - x, item, _theme.Subtext, _theme.Crust) + 3;
        }
    }

    private string[] FooterKeys(bool searching)
    {
        if (_edit is not null) return ["enter save", "esc cancel", "ctrl+u clear"];
        return searching
            ? ["type to filter", "enter done", "esc clear"]
            : ["↑↓ move", "←→ change", "enter edit", "r default", "u undo", "e open file", "/ search", "tab group", "q close"];
    }

    /// <summary>Words wrapped to <paramref name="width"/> cells; a word longer than that is cut.</summary>
    internal static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var line = new System.Text.StringBuilder();
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word.Length > width ? word[..width] : word);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return lines;
    }

    private static int Width(string text)
    {
        int width = 0;
        foreach (var rune in text.EnumerateRunes()) width += Math.Max(0, CharWidth.Of(rune.Value));
        return width;
    }
}
