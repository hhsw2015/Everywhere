// Mirrors: packages/OpenComputerUseKit/Sources/OpenComputerUseKit/InputSimulation.swift
// Upstream: iFurySt/open-codex-computer-use@<sha-pinned-in-UPSTREAM_REF.md>

using System.Runtime.InteropServices;
using Everywhere.Mcp.Input;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS implementation of <see cref="IInputSimulator"/> via CoreGraphics CGEvent.
/// Uses the HID system event tap so events look like genuine user input.
/// Requires the host process to have "Accessibility" / "Input Monitoring" permission;
/// macOS prompts on the first call.
/// </summary>
public sealed class MacInputSimulator : IInputSimulator
{
    public void MoveTo(double x, double y)
    {
        var src = CGEventSourceCreate(CGEventSourceStateID.HidSystemState);
        if (src == nint.Zero) throw new InvalidOperationException("Failed to create HID event source.");
        try
        {
            PostMouse(src, CGEventType.MouseMoved, x, y, CGMouseButton.Left, clickState: 1);
        }
        finally { CFRelease(src); }
    }

    public void Click(double x, double y, int clickCount = 1, MouseButton button = MouseButton.Left)
    {
        var src = CGEventSourceCreate(CGEventSourceStateID.HidSystemState);
        if (src == nint.Zero) throw new InvalidOperationException("Failed to create HID event source.");
        try
        {
            var (cgButton, downType, upType) = Map(button);
            for (var i = 0; i < Math.Max(clickCount, 1); i++)
            {
                PostMouse(src, CGEventType.MouseMoved, x, y, cgButton, clickCount);
                PostMouse(src, downType, x, y, cgButton, clickCount);
                PostMouse(src, upType, x, y, cgButton, clickCount);
            }
        }
        finally { CFRelease(src); }
    }

    public void DragTo(double fromX, double fromY, double toX, double toY)
    {
        var src = CGEventSourceCreate(CGEventSourceStateID.HidSystemState);
        if (src == nint.Zero) throw new InvalidOperationException("Failed to create HID event source.");
        try
        {
            PostMouse(src, CGEventType.MouseMoved, fromX, fromY, CGMouseButton.Left, 1);
            PostMouse(src, CGEventType.LeftMouseDown, fromX, fromY, CGMouseButton.Left, 1);
            for (var step = 1; step <= 10; step++)
            {
                var p = step / 10.0;
                PostMouse(src, CGEventType.LeftMouseDragged,
                    fromX + (toX - fromX) * p,
                    fromY + (toY - fromY) * p,
                    CGMouseButton.Left, 1);
            }
            PostMouse(src, CGEventType.LeftMouseUp, toX, toY, CGMouseButton.Left, 1);
        }
        finally { CFRelease(src); }
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // 64 UTF-16 unit chunks — matches upstream maxKeyboardUnicodeChunkLength.
        const int Max = 64;
        var pos = 0;
        while (pos < text.Length)
        {
            var len = Math.Min(Max, text.Length - pos);
            // Don't split a high surrogate.
            if (len < text.Length - pos && char.IsHighSurrogate(text[pos + len - 1])) len--;
            if (len <= 0) break;
            var chunk = text.Substring(pos, len);
            PostUnicodeChunk(chunk);
            Thread.Sleep(20);
            pos += len;
        }
    }

    public void PressKey(string xdotoolKeyName)
    {
        if (string.IsNullOrWhiteSpace(xdotoolKeyName))
            throw new ArgumentException("key specification is empty");

        var tokens = xdotoolKeyName
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant().Replace(" ", string.Empty))
            .Where(t => t.Length > 0)
            .ToList();

        if (tokens.Count == 0) throw new ArgumentException("key specification is empty");

        var keyToken = tokens[^1];
        var modifierTokens = tokens[..^1];
        var modifiers = new List<(ulong Flag, ushort KeyCode)>(modifierTokens.Count);
        foreach (var t in modifierTokens)
        {
            if (!MacKeyCodes.Modifiers.TryGetValue(t, out var mod))
                throw new ArgumentException($"unsupported modifier '{t}'");
            modifiers.Add(mod);
        }
        if (!MacKeyCodes.KeyByName.TryGetValue(keyToken, out var keyCode))
            throw new ArgumentException($"unsupported key '{xdotoolKeyName}'");

        ulong activeFlags = 0;

        // Modifier key-down sequence
        foreach (var (flag, mkc) in modifiers)
        {
            activeFlags |= flag;
            var ev = CGEventCreateKeyboardEvent(nint.Zero, mkc, true);
            if (ev == nint.Zero) throw new InvalidOperationException("Failed to create modifier key down event.");
            CGEventSetFlags(ev, activeFlags);
            CGEventPost(CGEventTapLocation.SessionEventTap,ev);
            CFRelease(ev);
        }

        // Main key down + up
        var down = CGEventCreateKeyboardEvent(nint.Zero, keyCode, true);
        var up = CGEventCreateKeyboardEvent(nint.Zero, keyCode, false);
        if (down == nint.Zero || up == nint.Zero)
        {
            if (down != nint.Zero) CFRelease(down);
            if (up != nint.Zero) CFRelease(up);
            throw new InvalidOperationException("Failed to create key event.");
        }
        try
        {
            CGEventSetFlags(down, activeFlags);
            CGEventSetFlags(up, activeFlags);
            CGEventPost(CGEventTapLocation.SessionEventTap,down);
            CGEventPost(CGEventTapLocation.SessionEventTap,up);
        }
        finally { CFRelease(down); CFRelease(up); }

        // Modifier key-up sequence (reverse)
        for (var i = modifiers.Count - 1; i >= 0; i--)
        {
            var (flag, mkc) = modifiers[i];
            var ev = CGEventCreateKeyboardEvent(nint.Zero, mkc, false);
            if (ev == nint.Zero) throw new InvalidOperationException("Failed to create modifier key up event.");
            CGEventSetFlags(ev, activeFlags);
            CGEventPost(CGEventTapLocation.SessionEventTap,ev);
            CFRelease(ev);
            activeFlags &= ~flag;
        }

        Thread.Sleep(100);
    }

    private static (CGMouseButton, CGEventType, CGEventType) Map(MouseButton b) => b switch
    {
        MouseButton.Right => (CGMouseButton.Right, CGEventType.RightMouseDown, CGEventType.RightMouseUp),
        MouseButton.Middle => (CGMouseButton.Center, CGEventType.OtherMouseDown, CGEventType.OtherMouseUp),
        _ => (CGMouseButton.Left, CGEventType.LeftMouseDown, CGEventType.LeftMouseUp),
    };

    private static void PostMouse(nint source, CGEventType type, double x, double y, CGMouseButton button, int clickState)
    {
        var ev = CGEventCreateMouseEvent(source, type, new CGPoint(x, y), button);
        if (ev == nint.Zero) throw new InvalidOperationException($"Failed to create mouse event {type}.");
        try
        {
            CGEventSetIntegerValueField(ev, CGEventField.MouseEventClickState, clickState);
            CGEventPost(CGEventTapLocation.SessionEventTap,ev);
        }
        finally { CFRelease(ev); }
        Thread.Sleep(30);
    }

    private static void PostUnicodeChunk(string chunk)
    {
        var down = CGEventCreateKeyboardEvent(nint.Zero, virtualKey: 0, keyDown: true);
        var up = CGEventCreateKeyboardEvent(nint.Zero, virtualKey: 0, keyDown: false);
        if (down == nint.Zero || up == nint.Zero)
        {
            if (down != nint.Zero) CFRelease(down);
            if (up != nint.Zero) CFRelease(up);
            throw new InvalidOperationException("Failed to create keyboard event.");
        }
        try
        {
            // Marshal as UTF-16 LE buffer of UniChar (UInt16).
            var units = chunk.ToCharArray();
            unsafe
            {
                fixed (char* p = units)
                {
                    CGEventKeyboardSetUnicodeString(down, units.Length, (ushort*)p);
                    CGEventKeyboardSetUnicodeString(up, units.Length, (ushort*)p);
                }
            }
            CGEventPost(CGEventTapLocation.SessionEventTap,down);
            CGEventPost(CGEventTapLocation.SessionEventTap,up);
        }
        finally { CFRelease(down); CFRelease(up); }
    }

    // ---- CGEvent / CoreFoundation P/Invoke ----

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private enum CGEventSourceStateID
    {
        Private = -1,
        CombinedSessionState = 0,
        HidSystemState = 1,
    }

    private enum CGEventTapLocation
    {
        HidEventTap = 0,
        SessionEventTap = 1,
        AnnotatedSessionEventTap = 2,
    }

    private enum CGEventType : uint
    {
        Null = 0,
        LeftMouseDown = 1,
        LeftMouseUp = 2,
        RightMouseDown = 3,
        RightMouseUp = 4,
        MouseMoved = 5,
        LeftMouseDragged = 6,
        RightMouseDragged = 7,
        KeyDown = 10,
        KeyUp = 11,
        FlagsChanged = 12,
        ScrollWheel = 22,
        OtherMouseDown = 25,
        OtherMouseUp = 26,
        OtherMouseDragged = 27,
    }

    private enum CGMouseButton : uint
    {
        Left = 0,
        Right = 1,
        Center = 2,
    }

    private enum CGEventField : uint
    {
        MouseEventClickState = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
        public CGPoint(double x, double y) { X = x; Y = y; }
    }

    [DllImport(CoreGraphics)]
    private static extern nint CGEventSourceCreate(CGEventSourceStateID stateID);

    [DllImport(CoreGraphics)]
    private static extern nint CGEventCreateMouseEvent(nint source, CGEventType mouseType, CGPoint point, CGMouseButton button);

    [DllImport(CoreGraphics)]
    private static extern nint CGEventCreateKeyboardEvent(nint source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(CoreGraphics)]
    private static extern void CGEventPost(CGEventTapLocation tap, nint @event);

    [DllImport(CoreGraphics)]
    private static extern void CGEventSetIntegerValueField(nint @event, CGEventField field, long value);

    [DllImport(CoreGraphics)]
    private static extern void CGEventSetFlags(nint @event, ulong flags);

    [DllImport(CoreGraphics)]
    private static extern unsafe void CGEventKeyboardSetUnicodeString(nint @event, long stringLength, ushort* unicodeString);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);
}
