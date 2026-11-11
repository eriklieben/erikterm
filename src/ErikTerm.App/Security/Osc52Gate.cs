using System.Runtime.CompilerServices;
using ErikTerm.Config;
using ErikTerm.Core;

namespace ErikTerm.App.Security;

internal enum Osc52Decision { Apply, Denied, Asking }

internal enum Osc52Choice { Allow, Deny, AlwaysForPane }

/// <summary>
/// The osc52 setting's state machine. "allow" applies every write, "deny" none; "ask" holds the latest request until
/// the user answers it (or it expires after <see cref="ExpireMs"/>). "Always for this pane" trusts that terminal
/// from then on — process-wide, so it survives the pane moving to another window, and forgotten with the terminal.
/// Main thread only.
/// </summary>
internal sealed class Osc52Gate(ConditionalWeakTable<Terminal, object> trusted)
{
    public const int ExpireMs = 10_000;

    public sealed record Request(Terminal Source, ClipboardTarget Target, string Text, long At);

    /// <summary>The request waiting for an answer (newest wins: an older one is dropped).</summary>
    public Request? Pending { get; private set; }

    public bool IsTrusted(Terminal source) => trusted.TryGetValue(source, out _);

    public Osc52Decision Offer(Osc52Policy policy, Terminal source, ClipboardTarget target, string text, long now)
    {
        switch (policy)
        {
            case Osc52Policy.Deny: return Osc52Decision.Denied;
            case Osc52Policy.Allow: return Osc52Decision.Apply;
        }
        if (IsTrusted(source)) return Osc52Decision.Apply;
        Pending = new Request(source, target, text, now);
        return Osc52Decision.Asking;
    }

    /// <summary>The user's answer: the request to apply now, or null (denied, or nothing is pending any more).</summary>
    public Request? Resolve(Osc52Choice choice, long now)
    {
        Expire(now);
        var p = Pending;
        Pending = null;
        if (p is null || choice == Osc52Choice.Deny) return null;
        if (choice == Osc52Choice.AlwaysForPane) trusted.AddOrUpdate(p.Source, true);
        return p;
    }

    /// <summary>Drops the pending request once it is too old. True when it did.</summary>
    public bool Expire(long now)
    {
        if (Pending is null || now - Pending.At < ExpireMs) return false;
        Pending = null;
        return true;
    }

    /// <summary>The pane went away, or the policy changed away from ask: forget its request.</summary>
    public void Cancel(Terminal? source = null)
    {
        if (source is null || Pending?.Source == source) Pending = null;
    }

    public int MsUntilExpiry(long now) => Pending is null ? int.MaxValue : (int)Math.Max(1, Pending.At + ExpireMs - now);
}
