using Avalonia;

namespace Everywhere.Mcp.CursorOverlay;

/// <summary>
/// Lightweight ABI for tools to flash a "this is the target window"
/// indicator without taking a hard dependency on the Avalonia overlay
/// implementation. When the overlay isn't enabled the no-op impl is
/// registered, callers don't need to know.
/// </summary>
public interface ITargetWindowHighlighter
{
    /// <summary>Highlight an AX-window's screen rectangle for the
    /// configured display window. Coordinates are in screen pixels.</summary>
    void Highlight(PixelRect rect, string? label = null);
}

internal sealed class NoopTargetWindowHighlighter : ITargetWindowHighlighter
{
    public void Highlight(PixelRect rect, string? label = null) { }
}
