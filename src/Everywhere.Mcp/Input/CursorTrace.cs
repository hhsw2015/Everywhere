using System.Collections.Concurrent;
using Avalonia.Threading;

namespace Everywhere.Mcp.Input;

/// <summary>
/// Pub/sub channel: IInputSimulator decorator publishes every Move/Click/Drag
/// here; CursorOverlayWindow subscribes and animates a software cursor +
/// fading trail. Decoupled so non-GUI hosts (CLI / tests) can register the
/// trace service without an overlay attached.
///
/// Inspired by open-codex-computer-use's SoftwareCursorOverlay +
/// CursorMotionModel. Visual semantics (cursor glyph, trail length,
/// fade) match upstream; physics is a simpler exponential ease-out
/// rather than the Swift spring model — same end behavior visually.
/// </summary>
public sealed class CursorTrace
{
    public event Action<CursorTraceEvent>? Event;

    public void Publish(CursorTraceEvent ev)
    {
        // UI thread will subscribe; marshal the post-back to it so the
        // event handler can directly mutate Avalonia visuals without
        // re-dispatching every call.
        var handler = Event;
        if (handler is null) return;
        if (Dispatcher.UIThread.CheckAccess())
        {
            handler(ev);
        }
        else
        {
            Dispatcher.UIThread.Post(() => handler(ev));
        }
    }
}

public enum CursorTraceKind
{
    Move,
    Click,
    Drag,
    KeyPress,
    Type,
    Settle,
}

public readonly record struct CursorTraceEvent(
    CursorTraceKind Kind,
    double X,
    double Y,
    double? ToX = null,
    double? ToY = null,
    int ClickCount = 1,
    MouseButton Button = MouseButton.Left,
    string? Label = null,
    // ponytail: pid of the click's target app, when known (Click +
    // Drag from the dispatcher path). Lets the overlay raise itself
    // above that app's windows specifically — 1:1 OCCU
    // configureOrdering (SoftwareCursorOverlay.swift L319-345) which
    // sets panel.level to target window layer and orders above it.
    // null means "no target known" — overlay falls back to floating
    // level + orderFront(nil).
    int? TargetProcessId = null);
