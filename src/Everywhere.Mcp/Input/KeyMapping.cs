// Mirrors: packages/OpenComputerUseKit/Sources/OpenComputerUseKit/KeyMapping.swift
// Upstream: iFurySt/open-codex-computer-use@<sha-pinned-in-UPSTREAM_REF.md>

namespace Everywhere.Mcp.Input;

/// <summary>
/// xdotool-style key name → cross-platform tokens. Names match the canonical xdotool
/// vocabulary (<c>Return</c>, <c>Tab</c>, <c>super+c</c>, <c>KP_0</c>) so user-facing strings
/// stay portable; the platform-specific simulators translate this token to the local keycode.
/// Coverage must match upstream tests.
/// </summary>
public static class KeyMapping
{
    public readonly record struct Combination(IReadOnlyList<KeyToken> Tokens);

    public enum KeyToken
    {
        Unknown,
        // Modifiers
        Shift, Control, Alt, Super,
        // Common
        Return, Enter, Tab, Escape, Space, BackSpace, Delete,
        Up, Down, Left, Right,
        Home, End, PageUp, PageDown,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        // Numpad
        KP_0, KP_1, KP_2, KP_3, KP_4, KP_5, KP_6, KP_7, KP_8, KP_9,
        // Letters / digits handled inline as ASCII via TypeText fallback.
        AsciiLiteral,
    }

    public static Combination Parse(string xdotoolKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xdotoolKey);
        var parts = xdotoolKey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokens = new List<KeyToken>(parts.Length);
        foreach (var part in parts)
        {
            tokens.Add(MapToken(part));
        }
        return new Combination(tokens);
    }

    public static KeyToken MapToken(string name)
    {
        return name switch
        {
            "shift" or "Shift" => KeyToken.Shift,
            "ctrl" or "control" or "Control" => KeyToken.Control,
            "alt" or "option" or "Alt" => KeyToken.Alt,
            "super" or "meta" or "cmd" or "Super" or "Meta" or "Command" => KeyToken.Super,
            "Return" => KeyToken.Return,
            "Enter" => KeyToken.Enter,
            "Tab" => KeyToken.Tab,
            "Escape" or "Esc" => KeyToken.Escape,
            "space" or "Space" => KeyToken.Space,
            "BackSpace" or "Backspace" => KeyToken.BackSpace,
            "Delete" or "Del" => KeyToken.Delete,
            "Up" => KeyToken.Up,
            "Down" => KeyToken.Down,
            "Left" => KeyToken.Left,
            "Right" => KeyToken.Right,
            "Home" => KeyToken.Home,
            "End" => KeyToken.End,
            "Page_Up" or "PageUp" or "Prior" => KeyToken.PageUp,
            "Page_Down" or "PageDown" or "Next" => KeyToken.PageDown,
            "F1" => KeyToken.F1, "F2" => KeyToken.F2, "F3" => KeyToken.F3, "F4" => KeyToken.F4,
            "F5" => KeyToken.F5, "F6" => KeyToken.F6, "F7" => KeyToken.F7, "F8" => KeyToken.F8,
            "F9" => KeyToken.F9, "F10" => KeyToken.F10, "F11" => KeyToken.F11, "F12" => KeyToken.F12,
            "KP_0" => KeyToken.KP_0, "KP_1" => KeyToken.KP_1, "KP_2" => KeyToken.KP_2, "KP_3" => KeyToken.KP_3,
            "KP_4" => KeyToken.KP_4, "KP_5" => KeyToken.KP_5, "KP_6" => KeyToken.KP_6, "KP_7" => KeyToken.KP_7,
            "KP_8" => KeyToken.KP_8, "KP_9" => KeyToken.KP_9,
            _ => KeyToken.AsciiLiteral,
        };
    }
}
