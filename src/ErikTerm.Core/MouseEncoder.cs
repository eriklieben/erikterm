using System.Text;

namespace ErikTerm.Core;

public enum MouseButton { Left = 0, Middle = 1, Right = 2, None = 3, WheelUp = 64, WheelDown = 65 }

public enum MouseEventKind { Press, Release, Motion }

[Flags]
public enum MouseModifiers { None = 0, Shift = 4, Alt = 8, Ctrl = 16 }

/// <summary>Encodes mouse events for applications that enabled tracking (modes 1000/1002/1003, SGR 1006).</summary>
public static class MouseEncoder
{
    /// <summary>Whether the terminal's current mode wants this event at all.</summary>
    public static bool Wants(Terminal t, MouseEventKind kind, bool buttonHeld) => t.MouseTracking switch
    {
        MouseTracking.None => false,
        MouseTracking.Press => kind != MouseEventKind.Motion,
        MouseTracking.ButtonMotion => kind != MouseEventKind.Motion || buttonHeld,
        _ => true,
    };

    /// <summary>Returns the escape sequence, or null if it can't be encoded (legacy encoding beyond column 223).</summary>
    public static string? Encode(Terminal t, MouseEventKind kind, MouseButton button, int col, int row, MouseModifiers mods)
    {
        int code = (int)button | (int)mods;
        if (kind == MouseEventKind.Motion) code += 32;
        int x = col + 1, y = row + 1;

        if (t.MouseSgr)
        {
            char final = kind == MouseEventKind.Release ? 'm' : 'M';
            return $"\e[<{code};{x};{y}{final}";
        }

        // X10/normal encoding: releases don't say which button.
        if (kind == MouseEventKind.Release) code = 3 | (int)mods;
        if (x > 223 || y > 223) return null;
        var sb = new StringBuilder("\e[M");
        sb.Append((char)(32 + code)).Append((char)(32 + x)).Append((char)(32 + y));
        return sb.ToString();
    }
}
