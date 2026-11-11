using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Notifications;

/// <summary>
/// Whether erikterm may raise notifications on the desktop (notify-send). <c>Ask</c>: not yet decided — the first
/// notification that would go out shows a warning bar in the window instead.
/// </summary>
internal enum OsNotificationMode { Ask, On, Off }

/// <summary>How much a notification wants the user. Waiting items (input / permission) never auto-hide.</summary>
internal enum NotificationPriority { Low, Normal, Waiting }

internal enum NotificationSource { Program, Agent }

/// <summary>What happened to a posted notification (flags).</summary>
[Flags]
internal enum NotificationRoute
{
    None = 0,
    /// <summary>Recorded in the history (the Notifications panel, the bar's unread count).</summary>
    History = 1,
    /// <summary>Shown in the in-window notification bar.</summary>
    Bar = 2,
    /// <summary>Sent to the desktop (notify-send).</summary>
    Os = 4,
    /// <summary>The caller should show a toast (only when the notification bar is turned off).</summary>
    Toast = 8,
    /// <summary>Would have gone to the desktop, but the mode is still "ask": the warning bar asks first.</summary>
    Ask = 16,
}

/// <summary>A notification as a window posts it; the center cleans every text before keeping it.</summary>
/// <param name="App">Who sent it ("Claude", the pane's program); shown in the "wants to send notifications" warning.</param>
/// <param name="WindowId">The window the pane is in when it is posted (panes move between windows; jumps go by pane id).</param>
/// <param name="TabLabel">"Tab 2" when its window has several tabs, else "".</param>
/// <param name="Place">The pane's directory (or label).</param>
internal readonly record struct NotificationRequest(
    int PaneId, NotificationSource Source, NotificationPriority Priority, string App, string Title, string Body,
    uint WindowId = 0, string TabLabel = "", string Place = "", AgentState AgentState = AgentState.None);

/// <summary>Where the user is when a notification is posted.</summary>
/// <param name="PaneSeen">The pane is the focused pane of the focused window: nothing is shown at all.</param>
internal readonly record struct NotificationContext(bool PaneSeen, bool WindowFocused, long Now);

/// <summary>One entry of the history. Texts are plain, cleaned, capped strings (never markup).</summary>
internal sealed class NotificationItem
{
    public long Id;
    public int PaneId;
    public uint WindowId;
    public NotificationSource Source;
    public NotificationPriority Priority;
    public AgentState AgentState;
    public string App = "", Title = "", Body = "", TabLabel = "", Place = "";
    /// <summary>When it was (last) posted; identical repeats bump it and <see cref="Count"/>.</summary>
    public long At;
    public int Count = 1;
    public bool Read;
    /// <summary>A waiting item whose agent stopped waiting (answered, or the pane is gone): it may auto-hide now.</summary>
    public bool Resolved;
    public bool SentToDesktop;

    /// <summary>Waiting for the user and not answered yet: stays in the bar until read or dismissed.</summary>
    public bool Sticky => Priority == NotificationPriority.Waiting && !Resolved;

    /// <summary>"Window 3 · Tab 2 · ~/src" as seen from window <paramref name="viewer"/> (its own window isn't named).</summary>
    public string Where(uint viewer)
    {
        string w = WindowId != 0 && WindowId != viewer ? $"Window {WindowId}" : "";
        return NotificationText.Join(NotificationText.Join(w, TabLabel), Place);
    }
}

/// <summary>Sends a notification to the desktop. Host/ProcessNotifications.cs runs notify-send; tests record calls.</summary>
internal interface IDesktopNotifier
{
    /// <summary>Whether anything can be sent at all (notify-send on PATH): without it, erikterm never asks.</summary>
    bool Available { get; }

    /// <summary><paramref name="title"/> is plain text; <paramref name="markupBody"/> is already markup-escaped.</summary>
    void Send(int paneId, string title, string markupBody, bool urgent);
}
