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
    public void MoveTo(double x, double y, int? targetPid = null)
    {
        // OCCU mirrors: targeted path uses CombinedSessionState, global uses HidSystemState.
        var stateId = targetPid.HasValue ? CGEventSourceStateID.CombinedSessionState : CGEventSourceStateID.HidSystemState;
        var src = CGEventSourceCreate(stateId);
        if (src == nint.Zero) throw new InvalidOperationException("Failed to create event source.");
        try
        {
            PostMouse(src, CGEventType.MouseMoved, x, y, CGMouseButton.Left, clickState: 1, targetPid);
        }
        finally { CFRelease(src); }
    }

    public void Click(double x, double y, int clickCount = 1, MouseButton button = MouseButton.Left, int? targetPid = null)
    {
        var stateId = targetPid.HasValue ? CGEventSourceStateID.CombinedSessionState : CGEventSourceStateID.HidSystemState;
        var src = CGEventSourceCreate(stateId);
        if (src == nint.Zero) throw new InvalidOperationException("Failed to create event source.");
        try
        {
            var (cgButton, downType, upType) = Map(button);
            // For GLOBAL clicks (targetPid=null → HidEventTap), warp the
            // hardware cursor to the click point first. Otherwise mouseDown
            // can land on the physical-cursor's prior position because
            // macOS samples real-cursor location at hit-test time.
            // Targeted (PostToPid) doesn't go through the system tap so
            // it doesn't need this.
            if (!targetPid.HasValue)
            {
                CGWarpMouseCursorPosition(new CGPoint(x, y));
                CGAssociateMouseAndMouseCursorPosition(1);
                Thread.Sleep(10); // settle
            }
            for (var i = 0; i < Math.Max(clickCount, 1); i++)
            {
                PostMouse(src, CGEventType.MouseMoved, x, y, cgButton, clickCount, targetPid);
                PostMouse(src, downType, x, y, cgButton, clickCount, targetPid);
                PostMouse(src, upType, x, y, cgButton, clickCount, targetPid);
            }
        }
        finally { CFRelease(src); }
    }

    public void DragTo(double fromX, double fromY, double toX, double toY, int? targetPid = null)
    {
        var stateId = targetPid.HasValue ? CGEventSourceStateID.CombinedSessionState : CGEventSourceStateID.HidSystemState;
        var src = CGEventSourceCreate(stateId);
        if (src == nint.Zero) throw new InvalidOperationException("Failed to create event source.");
        try
        {
            PostMouse(src, CGEventType.MouseMoved, fromX, fromY, CGMouseButton.Left, 1, targetPid);
            PostMouse(src, CGEventType.LeftMouseDown, fromX, fromY, CGMouseButton.Left, 1, targetPid);
            for (var step = 1; step <= 10; step++)
            {
                var p = step / 10.0;
                PostMouse(src, CGEventType.LeftMouseDragged,
                    fromX + (toX - fromX) * p,
                    fromY + (toY - fromY) * p,
                    CGMouseButton.Left, 1, targetPid);
            }
            PostMouse(src, CGEventType.LeftMouseUp, toX, toY, CGMouseButton.Left, 1, targetPid);
        }
        finally { CFRelease(src); }
    }

    public void TypeText(string text, int? targetPid = null)
    {
        if (string.IsNullOrEmpty(text)) return;

        // OCCU keyboardUnicodeChunks (InputSimulation.swift L165): walk
        // by grapheme cluster (Swift `for character in text`), accumulate
        // each cluster's UTF-16 units into the current chunk, flush
        // before exceeding maxUTF16Units=64. We use
        // StringInfo.GetTextElementEnumerator which yields grapheme
        // clusters per Unicode TR29 — handles emoji ZWJ sequences,
        // flag pairs, family stickers, combining marks, etc.
        const int Max = 64;
        var sb = new System.Text.StringBuilder(Max);
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var grapheme = (string)enumerator.Current!;
            if (sb.Length > 0 && sb.Length + grapheme.Length > Max)
            {
                PostUnicodeChunk(sb.ToString(), targetPid);
                Thread.Sleep(20);
                sb.Clear();
            }
            // Single grapheme longer than Max (rare — pathological emoji
            // family with many ZWJ joins). Send as its own oversized
            // chunk rather than splitting mid-grapheme.
            if (grapheme.Length > Max && sb.Length == 0)
            {
                PostUnicodeChunk(grapheme, targetPid);
                Thread.Sleep(20);
                continue;
            }
            sb.Append(grapheme);
        }
        if (sb.Length > 0) PostUnicodeChunk(sb.ToString(), targetPid);
    }

    public void PressKey(string xdotoolKeyName, int? targetPid = null)
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
            PostEvent(ev, targetPid);
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
            PostEvent(down, targetPid);
            PostEvent(up, targetPid);
        }
        finally { CFRelease(down); CFRelease(up); }

        // Modifier key-up sequence (reverse)
        for (var i = modifiers.Count - 1; i >= 0; i--)
        {
            var (flag, mkc) = modifiers[i];
            var ev = CGEventCreateKeyboardEvent(nint.Zero, mkc, false);
            if (ev == nint.Zero) throw new InvalidOperationException("Failed to create modifier key up event.");
            CGEventSetFlags(ev, activeFlags);
            PostEvent(ev, targetPid);
            CFRelease(ev);
            activeFlags &= ~flag;
        }

        Thread.Sleep(100);
    }

    public void Scroll(double x, double y, string direction, double pages = 1, int? targetPid = null)
    {
        // OCCU InputSimulation.scrollTargeted/Globally L82-100. Two-axis
        // scroll wheel event: wheel1 = vertical (up=+, down=-), wheel2 =
        // horizontal (left=+, right=-). Delta = round(12 * pages),
        // clamped to int32 with floor 1.
        var delta = ComputeScrollDelta(pages);
        var (w1, w2) = direction.ToLowerInvariant() switch
        {
            "up"    => (delta, 0),
            "down"  => (-delta, 0),
            "left"  => (0, delta),
            "right" => (0, -delta),
            _       => (0, 0),
        };
        if (w1 == 0 && w2 == 0) return;

        var ev = CGEventCreateScrollWheelEvent2(
            nint.Zero, CGScrollEventUnit.Line, wheelCount: 2,
            wheel1: w1, wheel2: w2, wheel3: 0);
        if (ev == nint.Zero) throw new InvalidOperationException("Failed to create scroll wheel event.");
        try
        {
            CGEventSetLocation(ev, new CGPoint(x, y));
            PostEvent(ev, targetPid);
        }
        finally { CFRelease(ev); }
        Thread.Sleep(100);
    }

    private static int ComputeScrollDelta(double pages)
    {
        var raw = Math.Round(12.0 * pages, MidpointRounding.AwayFromZero);
        var clamped = Math.Min((double)int.MaxValue, Math.Max(1.0, raw));
        return (int)clamped;
    }

    /// <summary>Single dispatch: targeted (CGEventPostToPid) or global.
    /// OCCU posts global events to <c>.cghidEventTap</c> (HID-level)
    /// because that's the layer SwiftUI's NSGestureRecognizer hooks
    /// onto. Posting to SessionEventTap goes through the application
    /// session layer where gestures may be filtered out, making it
    /// look like the click never happened (Calculator '7' was a real
    /// regression of this).</summary>
    private static void PostEvent(nint ev, int? targetPid)
    {
        if (targetPid is { } pid) CGEventPostToPid(pid, ev);
        else CGEventPost(CGEventTapLocation.HidEventTap, ev);
    }

    private static (CGMouseButton, CGEventType, CGEventType) Map(MouseButton b) => b switch
    {
        MouseButton.Right => (CGMouseButton.Right, CGEventType.RightMouseDown, CGEventType.RightMouseUp),
        MouseButton.Middle => (CGMouseButton.Center, CGEventType.OtherMouseDown, CGEventType.OtherMouseUp),
        _ => (CGMouseButton.Left, CGEventType.LeftMouseDown, CGEventType.LeftMouseUp),
    };

    private static void PostMouse(nint source, CGEventType type, double x, double y, CGMouseButton button, int clickState, int? targetPid = null)
    {
        var ev = CGEventCreateMouseEvent(source, type, new CGPoint(x, y), button);
        if (ev == nint.Zero) throw new InvalidOperationException($"Failed to create mouse event {type}.");
        try
        {
            CGEventSetIntegerValueField(ev, CGEventField.MouseEventClickState, clickState);
            PostEvent(ev, targetPid);
        }
        finally { CFRelease(ev); }
        Thread.Sleep(30);
    }

    private static void PostUnicodeChunk(string chunk, int? targetPid = null)
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
            PostEvent(down, targetPid);
            PostEvent(up, targetPid);
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

    /// <summary>
    /// Targeted post — bypasses the global cursor / keyboard. The
    /// receiving process gets the event via its native event queue
    /// without any IOKit HID hop, so the user's real mouse stays put
    /// and other apps see nothing. OCCU equivalent of clickTargeted /
    /// scrollTargeted / dragTargeted / keysTargeted.
    /// </summary>
    [DllImport(CoreGraphics)]
    private static extern void CGEventPostToPid(int pid, nint @event);

    /// <summary>
    /// Teleports the OS hardware-cursor position to (x,y) immediately.
    /// CGEventPost(HidEventTap, mouseMoved) only schedules a synthetic
    /// move event — macOS's hit-test pipeline may sample the real
    /// cursor position before the move event drains, causing the
    /// subsequent mouseDown to land on whatever was under the
    /// physical cursor, not the target. Warping first guarantees the
    /// target IS the physical cursor at click time.
    /// Returns 0 on success.
    /// </summary>
    [DllImport(CoreGraphics)]
    private static extern int CGWarpMouseCursorPosition(CGPoint newCursorPosition);

    /// <summary>
    /// Re-associates the synthetic-event location with the actual
    /// hardware-cursor position. After CGWarpMouseCursorPosition macOS
    /// briefly latches the cursor to the warped point and ignores
    /// further mouseMoved events; calling this restores normal
    /// hardware tracking. Without it the user can't move their mouse
    /// for a few hundred ms after a synthesized click.
    /// </summary>
    [DllImport(CoreGraphics)]
    private static extern int CGAssociateMouseAndMouseCursorPosition(int connected);

    [DllImport(CoreGraphics)]
    private static extern nint CGEventCreateScrollWheelEvent2(
        nint source,
        CGScrollEventUnit units,
        uint wheelCount,
        int wheel1, int wheel2, int wheel3);

    [DllImport(CoreGraphics)]
    private static extern void CGEventSetLocation(nint @event, CGPoint location);

    private enum CGScrollEventUnit : uint
    {
        Pixel = 0,
        Line = 1,
    }

    [DllImport(CoreGraphics)]
    private static extern void CGEventSetIntegerValueField(nint @event, CGEventField field, long value);

    [DllImport(CoreGraphics)]
    private static extern void CGEventSetFlags(nint @event, ulong flags);

    [DllImport(CoreGraphics)]
    private static extern unsafe void CGEventKeyboardSetUnicodeString(nint @event, long stringLength, ushort* unicodeString);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);
}
