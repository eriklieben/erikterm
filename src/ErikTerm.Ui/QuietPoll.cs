namespace ErikTerm.Ui;

/// <summary>
/// Keeps a periodic check from running while the pane it concerns has printed nothing since the last one: what a program
/// in the pane did shows up as output. The slow fallback is for changes made from outside the terminal.
/// </summary>
public struct QuietPoll
{
    public const int FallbackMs = 60_000;

    private long _version, _checkedAt;
    private bool _checked;

    public readonly bool IsDue(long contentVersion, long now) =>
        !_checked || contentVersion != _version || now - _checkedAt >= FallbackMs;

    public void Checked(long contentVersion, long now) => (_version, _checkedAt, _checked) = (contentVersion, now, true);
}
