namespace ErikTerm.Config;

/// <summary>
/// One <c>--set key=value</c> (or wezterm-style <c>--config key=value</c>) from the command line, parsed
/// into a small TOML table that is merged over the config file on every load.
/// </summary>
public sealed record ConfigOverride(string Text, TomlTable Table);

public static class ConfigOverrides
{
    /// <summary>The "line number" given to overridden keys; diagnostics show it as <c>--set</c>.</summary>
    public const int Line = -1;

    /// <summary>
    /// Parses <c>key=value</c>. The key may be dotted or quoted like a TOML key (<c>colors.split</c>,
    /// <c>keys."CTRL+h"</c>); the value is a TOML value, and anything that isn't one is taken as a string,
    /// so <c>color_scheme=Tokyo Night</c> works without quotes.
    /// </summary>
    public static bool TryParse(string arg, out ConfigOverride? result, out string error)
    {
        result = null;
        int eq = arg.IndexOf('=');
        if (eq <= 0)
        {
            error = $"expected KEY=VALUE, got '{arg}'";
            return false;
        }
        string key = arg[..eq].Trim(), value = arg[(eq + 1)..].Trim();

        // The key as TOML first, to reject garbage keys early.
        TomlTable keyOnly;
        try
        {
            keyOnly = Toml.Parse(key + " = 0");
        }
        catch (TomlException)
        {
            error = $"invalid key '{key}' in '{arg}'";
            return false;
        }

        TomlTable table;
        try
        {
            table = value.Length > 0 && !value.Contains('\n') ? Toml.Parse(key + " = " + value) : throw new TomlException("", 0);
        }
        catch (TomlException)
        {
            // Not a TOML value: a bare string.
            table = keyOnly;
            var leaf = table;
            string last = leaf.Keys.Single();
            while (leaf.TryGet(last, out var v, out _) && v is TomlTable { IsInline: false } inner)
            {
                leaf = inner;
                last = leaf.Keys.Single();
            }
            leaf.Set(last, value, 1);
        }
        result = new ConfigOverride(arg, table);
        error = "";
        return true;
    }

    /// <summary>Merges the overrides into <paramref name="root"/>, in order (later ones win).</summary>
    public static void ApplyTo(TomlTable root, IReadOnlyList<ConfigOverride>? overrides)
    {
        if (overrides is null) return;
        foreach (var o in overrides) Merge(root, o.Table);
    }

    /// <summary>
    /// Implicit tables (from dotted keys) merge key by key; values — including inline tables like
    /// <c>window_padding = { left = 2 }</c> — replace what was there.
    /// </summary>
    private static void Merge(TomlTable into, TomlTable from)
    {
        foreach (string key in from.Keys)
        {
            from.TryGet(key, out var value, out _);
            if (value is TomlTable { IsInline: false } sub)
            {
                if (!into.TryGet(key, out var existing, out _) || existing is not TomlTable { IsInline: false } target)
                {
                    target = new TomlTable();
                    into.Set(key, target, Line);
                }
                Merge(target, sub);
            }
            else into.Set(key, Relabel(value), Line);
        }
    }

    private static object Relabel(object value)
    {
        if (value is not TomlTable t) return value;
        var copy = new TomlTable { IsInline = t.IsInline };
        foreach (string k in t.Keys)
        {
            t.TryGet(k, out var v, out _);
            copy.Set(k, Relabel(v), Line);
        }
        return copy;
    }

    /// <summary>"path:line", or "--set" for a key that came from the command line.</summary>
    public static string Where(string path, int line) => line == Line ? "--set" : $"{path}:{line}";
}
