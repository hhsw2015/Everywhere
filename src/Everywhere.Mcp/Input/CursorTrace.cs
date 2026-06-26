using System.Collections.Concurrent;
using Avalonia;
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

    /// <summary>
    /// Optional sync hook the AX click path used to await before issuing
    /// AXPress, so the on-screen soft cursor reached the target visibly
    /// first. After the C# AX click path was retired (now all routed
    /// through OCCU's Swift bridge, which manages its own cursor
    /// ordering), nothing currently invokes this. The setter is still
    /// wired up by CursorOverlayBridge for forward compatibility with
    /// any future caller that wants the same handshake.
    /// </summary>
    public Func<int?, Point, Task>? MoveAndAwait { get; set; }

    public void Publish(CursorTraceEvent ev)
    {
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
