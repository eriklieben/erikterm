using System.Text;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.Core;

namespace ErikTerm.App.Windowing;

/// <summary>
/// Sending keys to the program with the kitty keyboard protocol's alternate keys (flag 4) and associated text
/// (flag 16). SDL delivers a key's text in an SDL_EVENT_TEXT_INPUT right after its SDL_EVENT_KEY_DOWN, so with
/// flag 16 a text key's press is held back until that text arrives, another event comes in, or
/// <see cref="PendingKeyTimeoutMs"/> passes (an IME that swallowed the key).
/// </summary>
internal sealed class KeyInputRouter(IWindowHost window, Lock gate, WindowBroadcast broadcast)
{
    private const int PendingKeyTimeoutMs = 15;

    private readonly record struct PendingKey(
        Pane Pane, uint Key, uint Scancode, ushort Mod, bool AppCursor, KittyKeyboardModes Flags, KeyEventKind Kind, KeyAlternates Alternates, long Since);

    private PendingKey? _pendingKey;

    /// <summary>Keys whose press went to the program, so only their releases are reported (kitty event types).</summary>
    private readonly HashSet<uint> _keysSentDown = [];

    private static long Now => Environment.TickCount64;

    public static KeyAlternates AlternatesFor(uint scancode, KittyKeyboardModes flags)
    {
        if (scancode == 0 || (flags & KittyKeyboardModes.ReportAlternateKeys) == 0) return default;
        const ushort leftShift = 0x0001;
        return new KeyAlternates(Sdl.GetKeyFromScancode(scancode, 0, false), Sdl.GetKeyFromScancode(scancode, leftShift, false));
    }

    /// <summary>
    /// Encodes a key press/repeat for the focused pane and sends it (or holds it for its text). Returns false when the
    /// key sends nothing, so its text input (if any) goes through as text.
    /// </summary>
    public bool SendKey(uint key, uint scancode, ushort mod, KeyEventKind kind)
    {
        FlushPendingKey();
        var focused = window.Focused;
        bool appCursor;
        KittyKeyboardModes kitty;
        lock (gate) (appCursor, kitty) = (focused.Term.CursorKeysApplication, focused.Term.KeyboardFlags);
        var alternates = AlternatesFor(scancode, kitty);

        if (KeyEncoder.WantsText(key, mod, kitty, kind))
            _pendingKey = new PendingKey(focused, key, scancode, mod, appCursor, kitty, kind, alternates, Now);
        else if (KeyEncoder.Encode(key, mod, appCursor, kitty, kind, alternates) is { } seq)
        {
            window.SendInput(focused, seq);
            broadcast.Key(key, scancode, mod, kind); // the same key to the other panes, broadcast input (Windowing/WindowBroadcast.cs)
        }
        else
            return false;

        if ((kitty & KittyKeyboardModes.ReportEventTypes) != 0) _keysSentDown.Add(key);
        return true;
    }

    /// <summary>Sends a key release, if its press went to the program.</summary>
    public void SendKeyRelease(uint key, uint scancode, ushort mod)
    {
        FlushPendingKey();
        if (!_keysSentDown.Remove(key)) return;
        var focused = window.Focused;
        bool appCursor;
        KittyKeyboardModes kitty;
        lock (gate) (appCursor, kitty) = (focused.Term.CursorKeysApplication, focused.Term.KeyboardFlags);
        if (KeyEncoder.Encode(key, mod, appCursor, kitty, KeyEventKind.Release, AlternatesFor(scancode, kitty)) is { } seq)
            window.WriteInput(focused, Encoding.UTF8.GetBytes(seq));
    }

    /// <summary>Text input for a held-back key: sends the key with its text. False if no key was waiting.</summary>
    public bool TakePendingKeyText(string text)
    {
        if (_pendingKey is not { } p) return false;
        _pendingKey = null;
        if (KeyEncoder.Encode(p.Key, p.Mod, p.AppCursor, p.Flags, p.Kind, p.Alternates, text) is { } seq) window.SendInput(p.Pane, seq);
        if (ReferenceEquals(p.Pane, window.Focused)) broadcast.Key(p.Key, p.Scancode, p.Mod, p.Kind, text);
        return true;
    }

    /// <summary>Sends a held-back key without text.</summary>
    public void FlushPendingKey()
    {
        if (_pendingKey is not { } p) return;
        _pendingKey = null;
        if (KeyEncoder.Encode(p.Key, p.Mod, p.AppCursor, p.Flags, p.Kind, p.Alternates) is { } seq) window.SendInput(p.Pane, seq);
        if (ReferenceEquals(p.Pane, window.Focused)) broadcast.Key(p.Key, p.Scancode, p.Mod, p.Kind);
    }

    /// <summary>The pane closed or left the window: a key held back for it goes out now.</summary>
    public void Forget(Pane pane)
    {
        if (_pendingKey is { } pk && pk.Pane == pane) FlushPendingKey();
    }

    /// <summary>Called after each batch of events: gives up waiting for text after the timeout.</summary>
    public void FlushPendingKeyIfDue(long now)
    {
        if (_pendingKey is { } p && now - p.Since >= PendingKeyTimeoutMs) FlushPendingKey();
    }

    public int PendingKeyWakeMs(long now) =>
        _pendingKey is { } p ? (int)Math.Max(0, p.Since + PendingKeyTimeoutMs - now) : int.MaxValue;
}
