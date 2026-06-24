namespace Everywhere.Mcp.Input;

/// <summary>
/// Wraps any IInputSimulator and emits CursorTrace events for every
/// Move/Click/Drag/Type/PressKey. The wrapped simulator does the real
/// platform work (CGEvent/SendInput/xdotool). Trace overlay can subscribe
/// to draw a software cursor + trail over the user's screen.
///
/// Registered as the public IInputSimulator when CursorOverlayEnabled is
/// true; otherwise consumers get the platform impl directly with no
/// observation overhead.
/// </summary>
public sealed class TracedInputSimulator : IInputSimulator
{
    private readonly IInputSimulator _inner;
    private readonly CursorTrace _trace;

    public TracedInputSimulator(IInputSimulator inner, CursorTrace trace)
    {
        _inner = inner;
        _trace = trace;
    }

    public void MoveTo(double x, double y)
    {
        _trace.Publish(new CursorTraceEvent(CursorTraceKind.Move, x, y));
        _inner.MoveTo(x, y);
    }

    public void Click(double x, double y, int clickCount = 1, MouseButton button = MouseButton.Left)
    {
        _trace.Publish(new CursorTraceEvent(CursorTraceKind.Click, x, y, ClickCount: clickCount, Button: button));
        _inner.Click(x, y, clickCount, button);
    }

    public void DragTo(double fromX, double fromY, double toX, double toY)
    {
        _trace.Publish(new CursorTraceEvent(CursorTraceKind.Drag, fromX, fromY, ToX: toX, ToY: toY));
        _inner.DragTo(fromX, fromY, toX, toY);
    }

    public void TypeText(string text)
    {
        // No coords to emit; surface as a non-positional event so the
        // overlay can flash a "typing…" indicator near the last cursor
        // pos if it wants. Most overlays will ignore this.
        _trace.Publish(new CursorTraceEvent(CursorTraceKind.Type, 0, 0, Label: text));
        _inner.TypeText(text);
    }

    public void PressKey(string xdotoolKeyName)
    {
        _trace.Publish(new CursorTraceEvent(CursorTraceKind.KeyPress, 0, 0, Label: xdotoolKeyName));
        _inner.PressKey(xdotoolKeyName);
    }
}
