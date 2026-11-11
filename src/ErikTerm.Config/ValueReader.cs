namespace ErikTerm.Config;

/// <summary>
/// Reads one table's values for <see cref="SettingsLoader"/>: each reader checks a value's shape and range, and reports
/// what is wrong with it (file, line, key) instead of throwing, so one bad value doesn't throw away the rest.
/// </summary>
internal sealed class ValueReader(TomlTable table, string path, List<string> diagnostics, string prefix = "")
{
    public void Warn(string key, string message) =>
        diagnostics.Add($"{ConfigOverrides.Where(path, table.LineOf(key))}: {prefix}{key}: {message}");

    public string? String(string key)
    {
        table.TryGet(key, out var v, out _);
        if (v is string s) return s;
        Warn(key, "expected a string");
        return null;
    }

    public bool? Bool(string key)
    {
        table.TryGet(key, out var v, out _);
        if (v is bool b) return b;
        Warn(key, "expected true or false");
        return null;
    }

    public double? ReadNumber(string key, double min, double max)
    {
        table.TryGet(key, out var v, out _);
        double? d = v switch { long l => l, double x => x, _ => null };
        if (d is null) { Warn(key, "expected a number"); return null; }
        if (d < min || d > max || double.IsNaN(d.Value)) { Warn(key, $"must be between {min} and {max}"); return null; }
        return d;
    }

    public List<string>? StringList(string key)
    {
        table.TryGet(key, out var v, out _);
        if (v is List<object> list && list.All(o => o is string)) return list.Cast<string>().ToList();
        if (v is string single) return [single];
        Warn(key, "expected an array of strings");
        return null;
    }

    public Dictionary<string, string>? StringTable(string key)
    {
        table.TryGet(key, out var v, out _);
        if (v is TomlTable t)
        {
            var result = new Dictionary<string, string>();
            foreach (var k in t.Keys)
            {
                t.TryGet(k, out var value, out _);
                if (value is string s) result[k] = s;
                else Warn(key, $"'{k}' must be a string");
            }
            return result;
        }
        Warn(key, "expected a table, e.g. { EDITOR = \"nvim\" }");
        return null;
    }

    public uint? ReadColor(string key)
    {
        table.TryGet(key, out var v, out _);
        if (v is string s && SettingsLoader.TryParseColor(s, out uint rgb)) return rgb;
        Warn(key, "expected a color like \"#rrggbb\"");
        return null;
    }

    public uint[]? ColorList(string key, int count)
    {
        table.TryGet(key, out var v, out _);
        if (v is List<object> list && list.Count == count)
        {
            var result = new uint[count];
            for (int i = 0; i < count; i++)
            {
                if (list[i] is not string s || !SettingsLoader.TryParseColor(s, out result[i]))
                {
                    Warn(key, $"entry {i + 1} is not a color like \"#rrggbb\"");
                    return null;
                }
            }
            return result;
        }
        Warn(key, $"expected an array of {count} colors");
        return null;
    }

    public Hsb? Hsb(string key)
    {
        table.TryGet(key, out var v, out _);
        if (v is TomlTable t)
        {
            double Get(string name, double fallback)
            {
                if (!t.TryGet(name, out var x, out _)) return fallback;
                return x switch { long l => l, double d => d, _ => throw new FormatException(name) };
            }
            try
            {
                foreach (var k in t.Keys.Where(field => field is not ("hue" or "saturation" or "brightness")))
                    Warn(key, $"unknown field '{k}'");
                return new Hsb(Get("hue", 1), Get("saturation", 1), Get("brightness", 1));
            }
            catch (FormatException ex)
            {
                Warn(key, $"'{ex.Message}' must be a number");
                return null;
            }
        }
        Warn(key, "expected { saturation = 0.9, brightness = 0.8 }");
        return null;
    }

    public Padding? Padding(string key)
    {
        table.TryGet(key, out var v, out _);
        if (v is long all && all is >= 0 and <= 500) return Config.Padding.All((int)all);
        if (v is TomlTable t) return PaddingFromTable(key, t);
        Warn(key, "expected a number or { left = 8, right = 8, top = 4, bottom = 4 }");
        return null;
    }

    private Padding? PaddingFromTable(string key, TomlTable t)
    {
        int Get(string side)
        {
            if (!t.TryGet(side, out var x, out _)) return 0;
            return x is long l && l is >= 0 and <= 500 ? (int)l : throw new FormatException(side);
        }
        try
        {
            foreach (var k in t.Keys.Where(side => side is not ("left" or "right" or "top" or "bottom")))
                Warn(key, $"unknown side '{k}'");
            return new Padding(Get("left"), Get("top"), Get("right"), Get("bottom"));
        }
        catch (FormatException ex)
        {
            Warn(key, $"'{ex.Message}' must be a whole number of pixels (0–500)");
            return null;
        }
    }
}
