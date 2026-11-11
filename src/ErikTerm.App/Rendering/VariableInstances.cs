namespace ErikTerm.App.Rendering;

/// <summary>A named instance of a variable font: 1-based index for FT_Set_Named_Instance, name, axis coordinates.</summary>
internal readonly record struct VariableInstance(int Index, string? Name, double? Weight, double? Italic, double? Slant)
{
    /// <summary>Upright vs italic: the ital axis, a non-zero slant, or the name.</summary>
    public bool IsItalic
    {
        get
        {
            if (Italic is { } i) return i >= 0.5;
            if (Slant is { } s && s != 0) return true;
            return Name is { } n && (n.Contains("Italic", StringComparison.OrdinalIgnoreCase) || n.Contains("Oblique", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The wght coordinate, or a guess from the name for fonts without a weight axis.</summary>
    public double EffectiveWeight => Weight ?? VariableInstances.WeightFromName(Name);
}

/// <summary>
/// Picks the named instance for a style when a family ships one variable font file instead of separate
/// Bold/Italic files (Cascadia Code on Windows 11, Adwaita Sans, many [wght] fonts).
/// </summary>
internal static class VariableInstances
{
    /// <summary>
    /// The instance index for <paramref name="style"/>, or 0 if the font has none (e.g. italic from an upright-only
    /// file). An exact style name wins ("Bold", "Bold Italic", …); otherwise the instance with the right slant whose
    /// weight is closest to 700 (bold, at least 600) or 400 (regular).
    /// </summary>
    public static int Pick(IReadOnlyList<VariableInstance> instances, FontStyle style)
    {
        if (instances.Count == 0) return 0;
        bool bold = style is FontStyle.Bold or FontStyle.BoldItalic, italic = style is FontStyle.Italic or FontStyle.BoldItalic;
        string want = (bold, italic) switch
        {
            (true, true) => "bolditalic",
            (true, false) => "bold",
            (false, true) => "italic",
            _ => "regular",
        };
        return ExactStyleMatch(instances, want, italic) ?? ClosestWeight(instances, bold, italic);
    }

    private static int? ExactStyleMatch(IReadOnlyList<VariableInstance> instances, string want, bool italic)
    {
        foreach (var i in instances)
        {
            string n = Normalize(i.Name);
            if (n == want || (italic && n == want.Replace("italic", "oblique"))) return i.Index;
        }
        return null;
    }

    private static int ClosestWeight(IReadOnlyList<VariableInstance> instances, bool bold, bool italic)
    {
        int best = 0;
        double bestDistance = double.MaxValue, target = bold ? 700 : 400;
        foreach (var i in instances)
        {
            if (i.IsItalic != italic) continue;
            double w = i.EffectiveWeight;
            if (bold && w < 600) continue;
            if (!bold && w >= 600) continue;
            double d = Math.Abs(w - target);
            if (d < bestDistance) { bestDistance = d; best = i.Index; }
        }
        return best;
    }

    private static string Normalize(string? name) =>
        name is null ? "" : string.Concat(name.Where(char.IsLetter)).ToLowerInvariant();

    internal static double WeightFromName(string? name)
    {
        string n = Normalize(name);
        // Longest names first: "semibold" and "extrabold" contain "bold".
        (string Key, double Weight)[] table =
        [
            ("extralight", 200), ("ultralight", 200), ("semibold", 600), ("demibold", 600), ("extrabold", 800),
            ("ultrabold", 800), ("thin", 100), ("light", 300), ("medium", 500), ("bold", 700), ("black", 900), ("heavy", 900),
        ];
        foreach (var (key, weight) in table)
            if (n.Contains(key)) return weight;
        return 400;
    }
}
