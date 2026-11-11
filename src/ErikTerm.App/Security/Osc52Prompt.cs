using ErikTerm.App.Notifications;
using ErikTerm.Core;

namespace ErikTerm.App.Security;

/// <summary>
/// What the osc52 = "ask" prompt on the window's info bar says (WindowFeatures/WindowOsc52.cs draws it): one message per pane,
/// above the desktop-notification question. Every text can come from a program (its title, its directory), so it is
/// cleaned like a notification's.
/// </summary>
internal static class Osc52Prompt
{
    /// <summary>Above the desktop-notification question: this one holds up a program's action.</summary>
    public const int Priority = NotificationCenter.AskPriority + 10;

    /// <summary>The button labels, in <see cref="Osc52Choice"/> order (the first is the suggested answer).</summary>
    public static readonly string[] Answers = ["Allow", "Deny", "Always for this pane"];

    /// <summary>The info bar key: a newer request from the same pane replaces the older one.</summary>
    public static string Key(int paneId) => "osc52:" + paneId;

    /// <param name="who">The pane's program (its foreground process, else its title).</param>
    /// <param name="where">"Tab 2 · ~/src".</param>
    public static (string Label, string Text) Texts(string? who, string? where, ClipboardTarget target, string text)
    {
        string name = NotificationText.Clean(who, NotificationText.MaxApp);
        string what = target == ClipboardTarget.Clipboard ? "the clipboard" : "the primary selection";
        int chars = text.EnumerateRunes().Count();
        return (NotificationText.Clean(where, NotificationText.MaxPlace),
            $"{(name.Length > 0 ? name : "A program")} wants to set {what} ({chars} character{(chars == 1 ? "" : "s")})");
    }
}
