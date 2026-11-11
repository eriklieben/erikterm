using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ErikTerm.App.Agents;
using ErikTerm.App.Native;
using ErikTerm.App.Notifications;
using ErikTerm.App.Panes;
using ErikTerm.App.Security;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>
/// OSC 52 clipboard writes (osc52 = "allow" | "ask" | "deny"). Requests arrive on a pane's reader thread and go into the
/// process's queue (<see cref="Attach"/>); App.TickClipboard hands each to the window that has the pane now (panes move
/// between windows), which calls <see cref="Apply"/>. With "ask" the window's info bar (Notifications/WindowNotifications.cs)
/// asks "[Tab 2 · ~/src] vim wants to set the clipboard (12 characters) [Allow] [Deny] [Always for this pane] ✕"
/// (✕ = Deny). One request per pane (the newest replaces an older one, keyed "osc52:&lt;pane id&gt;"); an unanswered
/// request goes away after 10 s (Security/Osc52Gate.cs).
/// </summary>
internal sealed class WindowOsc52(IFeatureWindow window, InfoBar infoBar, ConcurrentQueue<(Terminal Term, ClipboardTarget Target, string Text)> writes)
{
    /// <summary>"Always for this pane" holds for the terminal wherever it goes (tear-off, another window), until it is gone.</summary>
    private static readonly ConditionalWeakTable<Terminal, object> s_osc52Trusted = new();

    /// <summary>A gate per pane (by pane id) with a request waiting; removed once it's answered, expired or cancelled.</summary>
    private readonly Dictionary<int, Osc52Gate> _osc52 = [];
    private readonly List<int> _osc52Done = [];
    private (string Text, ClipboardTarget Target, long At) _lastClipboardWrite;

    private static long Now => Environment.TickCount64;

    /// <summary>SpawnPane hook: the terminal's OSC 52 requests go to the process's queue (reader thread), which wakes the loop.</summary>
    public void Attach(Terminal term)
    {
        term.AllowClipboardWrite = window.Settings.Osc52 != Osc52Policy.Deny;
        var queue = writes;
        term.ClipboardRequested += (target, text) =>
        {
            if (queue.Count < 16) queue.Enqueue((term, target, text));
            window.Wake();
        };
    }

    /// <summary>Main thread: a request from <paramref name="term"/>, handed over by App.TickClipboard.</summary>
    public void Apply(Terminal term, ClipboardTarget target, string text)
    {
        if (window.Settings.Osc52 == Osc52Policy.Deny) return;
        // Programs that think they're in tmux send it twice: raw and in tmux passthrough. Apply it once.
        if (_lastClipboardWrite.Text == text && _lastClipboardWrite.Target == target && Now - _lastClipboardWrite.At < 1000) return;
        _lastClipboardWrite = (text, target, Now);
        Offer(term, target, text);
    }

    /// <summary>A write (dedup already done): applies, drops or asks.</summary>
    private void Offer(Terminal term, ClipboardTarget target, string text)
    {
        long now = Now;
        var policy = window.Settings.Osc52;
        if (FindPane(term) is not { } found)
        {
            // Not a pane of this window (it closed meanwhile): apply when allowed outright, never ask.
            if (new Osc52Gate(s_osc52Trusted).Offer(policy, term, target, text, now) == Osc52Decision.Apply)
                WriteClipboard(term, target, text);
            return;
        }
        var (pane, tabIndex) = found;
        if (!_osc52.TryGetValue(pane.Id, out var gate)) gate = new Osc52Gate(s_osc52Trusted);
        switch (gate.Offer(policy, term, target, text, now))
        {
            case Osc52Decision.Apply: WriteClipboard(term, target, text); break;
            case Osc52Decision.Asking:
                _osc52[pane.Id] = gate;
                int id = pane.Id;
                infoBar.Show(Osc52Message(id, pane.Label, Osc52Where(pane, tabIndex), target, text, c => Answer(id, c)), window.Theme, now);
                window.Redraw();
                break;
        }
    }

    /// <summary>The prompt for pane <paramref name="paneId"/> (texts: Security/Osc52Prompt.cs). ✕ = Deny.</summary>
    private static InfoBarMessage Osc52Message(int paneId, string? who, string where, ClipboardTarget target, string text, Action<Osc52Choice> answer)
    {
        var (label, title) = Osc52Prompt.Texts(who, where, target, text);
        var buttons = new InfoBarButton[Osc52Prompt.Answers.Length];
        for (int i = 0; i < buttons.Length; i++)
        {
            var choice = (Osc52Choice)i;
            buttons[i] = new InfoBarButton(Osc52Prompt.Answers[i], () => answer(choice), primary: i == 0);
        }
        return new InfoBarMessage
        {
            Key = Osc52Prompt.Key(paneId),
            Priority = Osc52Prompt.Priority,
            Tone = InfoBarTone.Warning,
            Icon = Icons.Paste,
            Label = label,
            Text = title,
            Buttons = buttons,
            Dismissed = () => answer(Osc52Choice.Deny),
        };
    }

    /// <summary>"Tab 2 · ~/src" (the tab only when there are several).</summary>
    private string Osc52Where(Pane pane, int tabIndex) =>
        NotificationText.Join(window.TabCount > 1 ? $"Tab {tabIndex + 1}" : "", WindowAgents<Pane>.PaneDirName(pane));

    /// <summary>The pane of this window (any tab) that shows <paramref name="term"/>, and its tab's index.</summary>
    private (Pane Pane, int TabIndex)? FindPane(Terminal term)
    {
        var tabs = window.Tabs;
        for (int t = 0; t < tabs.Count; t++)
        {
            var tab = tabs.Items[t];
            var layout = ReferenceEquals(tab, tabs.Active) ? window.Layout : tab.Layout;
            foreach (var p in layout.LeafList) if (ReferenceEquals(p.Term, term)) return (p, t);
        }
        return null;
    }

    private void WriteClipboard(Terminal term, ClipboardTarget target, string text)
    {
        if (target == ClipboardTarget.Clipboard) Sdl.SetClipboardText(text);
        else Sdl.SetPrimarySelectionText(text);
        int chars = text.EnumerateRunes().Count();
        string where = target == ClipboardTarget.Clipboard ? "" : " to the primary selection";
        window.Toast(ToastKind.Success, "Copied from program", $"{Osc52Who(term)} · {chars} character{(chars == 1 ? "" : "s")}{where}");
    }

    private string Osc52Who(Terminal term) => FindPane(term)?.Pane.Label ?? "a program";

    private void Answer(int paneId, Osc52Choice choice)
    {
        long now = Now;
        if (_osc52.Remove(paneId, out var gate) && gate.Resolve(choice, now) is { } r) WriteClipboard(r.Source, r.Target, r.Text);
        infoBar.Remove(Osc52Prompt.Key(paneId), now);
        window.Redraw();
    }

    /// <summary>
    /// Tick hook: the active tab's panes follow the policy; a request expires, or its pane closed (or moved), or the
    /// policy is no longer "ask".
    /// </summary>
    public void Tick(long now, Osc52Policy policy, List<Pane> leaves)
    {
        foreach (var p in leaves) p.Term.AllowClipboardWrite = policy != Osc52Policy.Deny;
        if (_osc52.Count == 0) return;
        bool ask = policy == Osc52Policy.Ask;
        foreach (var (id, gate) in _osc52)
        {
            if (gate.Pending is not { } p) { _osc52Done.Add(id); continue; }
            if (!ask || FindPane(p.Source) is not { } f || f.Pane.Id != id) gate.Cancel();
            if (gate.Expire(now) || gate.Pending is null) _osc52Done.Add(id);
        }
        if (_osc52Done.Count == 0) return;
        foreach (int id in _osc52Done)
        {
            _osc52.Remove(id);
            infoBar.Remove(Osc52Prompt.Key(id), now);
        }
        _osc52Done.Clear();
        window.Redraw();
    }

    /// <summary>ChromeWakeMs hook: wake for the soonest request's expiry.</summary>
    public int WakeMs(long now)
    {
        int ms = int.MaxValue;
        foreach (var gate in _osc52.Values) ms = Math.Min(ms, gate.MsUntilExpiry(now));
        return ms;
    }
}
