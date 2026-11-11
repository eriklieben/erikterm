using System.Text;

namespace ErikTerm.Core;

/// <summary>
/// User variables set with OSC 1337 SetUserVar (wezterm/iTerm2), e.g. IS_NVIM from smart-splits.nvim. They describe
/// the program running now, so a new prompt (OSC 133;A) clears them.
/// </summary>
internal sealed class UserVariables
{
    private readonly Dictionary<string, string> _vars = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> All => _vars;

    public void Clear()
    {
        if (_vars.Count > 0) _vars.Clear();
    }

    /// <summary>
    /// OSC 1337 ; SetUserVar=NAME=base64(value). Names are printable ASCII; at most <see cref="Terminal.MaxUserVars"/>
    /// variables, longer names or values are ignored, and so is anything else.
    /// </summary>
    public void Set(ReadOnlySpan<byte> arg)
    {
        ReadOnlySpan<byte> prefix = "SetUserVar="u8;
        if (!arg.StartsWith(prefix)) return;
        var rest = arg[prefix.Length..];
        int eq = rest.IndexOf((byte)'=');
        if (eq is <= 0 or > Terminal.MaxUserVarName) return;
        foreach (byte b in rest[..eq])
            if (b is < 0x21 or > 0x7E) return;
        var encoded = rest[(eq + 1)..];
        if (encoded.Length > (Terminal.MaxUserVarValue + 2) / 3 * 4 + 4) return;
        string name = Encoding.ASCII.GetString(rest[..eq]);
        string value;
        try
        {
            value = Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(encoded)));
        }
        catch (FormatException) { return; }
        if (Encoding.UTF8.GetByteCount(value) > Terminal.MaxUserVarValue) return;
        if (_vars.Count >= Terminal.MaxUserVars && !_vars.ContainsKey(name)) return;
        _vars[name] = value;
    }
}
