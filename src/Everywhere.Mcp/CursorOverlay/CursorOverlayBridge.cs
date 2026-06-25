using Avalonia;
using Everywhere.Mcp.Input;

namespace Everywhere.Mcp.CursorOverlay;

/// <summary>
/// Wires the input-simulator trace channel (Move/Click/Drag events
/// already emitted by TracedInputSimulator) into a live
/// SoftwareCursorOverlay. Subscribes on construction; the overlay
/// itself decides idle/hide/animation cadence.
/// </summary>
public sealed class CursorOverlayBridge : IDisposable
{
    private readonly CursorTrace _trace;
    private readonly SoftwareCursorOverlay _overlay;

    public CursorOverlayBridge(CursorTrace trace, SoftwareCursorOverlay overlay)
    {
        _trace = trace;
        _overlay = overlay;
        _trace.Event += OnTraceEvent;
        _trace.MoveAndAwait = overlay.MoveCursorAsync;
    }

    private void OnTraceEvent(CursorTraceEvent ev)
    {
        switch (ev.Kind)
        {
            case CursorTraceKind.Move:
                _overlay.RaiseAboveTarget(ev.TargetProcessId);
                _overlay.MoveCursor(new Point(ev.X, ev.Y));
                break;
            case CursorTraceKind.Click:
                _overlay.RaiseAboveTarget(ev.TargetProcessId);
                _overlay.MoveCursor(new Point(ev.X, ev.Y));
                _overlay.PulseClick(new Point(ev.X, ev.Y), ev.ClickCount,
                    rightButton: ev.Button == MouseButton.Right);
                break;
            case CursorTraceKind.Drag:
                if (ev.ToX is { } tx && ev.ToY is { } ty)
                {
                    _overlay.RaiseAboveTarget(ev.TargetProcessId);
                    _overlay.MoveCursor(new Point(ev.X, ev.Y));
                    _overlay.MoveCursor(new Point(tx, ty));
                }
                break;
            case CursorTraceKind.Settle:
                _overlay.Settle(new Point(ev.X, ev.Y));
                break;
            case CursorTraceKind.KeyPress:
            case CursorTraceKind.Type:
                // Overlay shows nothing for keyboard events — the cursor
                // sits at its last position.
                break;
        }
    }

    public void Dispose()
    {
        _trace.Event -= OnTraceEvent;
        _ = _overlay.DisposeAsync();
    }
}
