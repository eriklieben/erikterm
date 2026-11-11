using System.Globalization;
using ErikTerm.Config;

namespace ErikTerm.App.SettingsPage;

/// <summary>
/// The config file as the settings page sees it: its text and parsed values, what each setting is set to, and changes
/// written back through the host — checked first, so a change never adds a problem, and undoable one by one.
/// </summary>
internal sealed class SettingsFile
{
    private readonly ISettingsHost _host;
    private readonly Stack<string> _undo = new();
    private TomlTable? _table;

    public SettingsFile(ISettingsHost host)
    {
        _host = host;
        Reload();
    }

    /// <summary>The file's text as last read.</summary>
    public string Text { get; private set; } = "";
    /// <summary>The file's syntax error, or null; while there is one, nothing is changed.</summary>
    public string? Error { get; private set; }
    /// <summary>The config file, as shown (home-relative).</summary>
    public string DisplayPath => _host.DisplayPath;

    /// <summary>Reads the file again; a syntax error is kept in <see cref="Error"/> and stops changes until it's fixed.</summary>
    public void Reload()
    {
        Text = _host.Read();
        try
        {
            _table = Toml.Parse(Text);
            Error = null;
        }
        catch (TomlException ex)
        {
            _table = null;
            Error = ex.Message;
        }
    }

    // ---- Values ------------------------------------------------------------------------------------------------

    /// <summary>The value the file sets, or null when it's left at its default.</summary>
    private object? FileValue(SettingInfo info) => _table is not null && _table.TryGet(info.Key, out var value, out _) ? value : null;

    public bool IsSet(SettingInfo info) => FileValue(info) is not null;

    /// <summary>The line the setting is on (0: not in the file).</summary>
    public int LineOf(SettingInfo info) => _table?.LineOf(info.Key) ?? 0;

    /// <summary>The value as the page shows it: what the file says, else the default.</summary>
    public string ValueText(SettingInfo info) => FileValue(info) switch
    {
        null => info.Default ?? info.DefaultLabel ?? "",
        bool b => TomlEditor.Literal(b),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => TomlEditor.Literal(d),
        string s => s,
        List<object> list => string.Join(", ", list.Select(item => item as string ?? Convert.ToString(item, CultureInfo.InvariantCulture))),
        _ => "set in the file",
    };

    public bool CurrentBool(SettingInfo info) => FileValue(info) is bool b ? b : info.Default == "true";

    public double CurrentNumber(SettingInfo info) => FileValue(info) switch
    {
        long l => l,
        double d => d,
        _ => double.TryParse(info.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : info.Min,
    };

    /// <summary>The values a choice offers; color_scheme's are the schemes there are.</summary>
    public IReadOnlyList<string> ChoicesOf(SettingInfo info) => info.Key == "color_scheme" ? _host.SchemeNames() : info.Choices;

    // ---- Changes -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes <paramref name="changed"/> unless it adds a problem. True when it was written; false with
    /// <paramref name="problem"/> saying why not, or with a null problem when nothing changed.
    /// </summary>
    public bool TrySave(string changed, out string? problem)
    {
        problem = null;
        if (Error is not null) { problem = "Fix the syntax error first: e opens the file"; return false; }
        if (changed == Text) return false;
        var before = _host.Problems(Text).ToHashSet(StringComparer.Ordinal);
        if (_host.Problems(changed).FirstOrDefault(p => !before.Contains(p)) is { } added) { problem = added; return false; }
        if (_host.Write(changed) is { } error) { problem = error; return false; }
        _undo.Push(Text);
        Reload();
        return true;
    }

    /// <summary>Writes the file as it was before the last change. False with a null error when there is nothing to undo.</summary>
    public bool TryUndo(out string? error)
    {
        error = null;
        if (!_undo.TryPop(out var previous)) return false;
        if (_host.Write(previous) is { } failed) { error = failed; return false; }
        Reload();
        return true;
    }

    /// <summary>Opens the file in the user's editor on <paramref name="line"/> (0: the top), then reads it again.</summary>
    public void Edit(int line)
    {
        _host.EditFile(line);
        Reload();
    }
}
