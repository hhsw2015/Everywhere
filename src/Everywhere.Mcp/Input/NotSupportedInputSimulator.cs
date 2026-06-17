namespace Everywhere.Mcp.Input;

/// <summary>
/// Default fallback when no platform-specific <see cref="IInputSimulator"/> is registered.
/// Coordinates / keystrokes paths surface a clear error instead of silently doing nothing.
/// Replace via DI in <c>Everywhere.Mac</c>/<c>Everywhere.Windows</c>/<c>Everywhere.Linux</c>
/// once the native bindings are in place.
/// </summary>
internal sealed class NotSupportedInputSimulator : IInputSimulator
{
    private static readonly NotSupportedException Reason = new(
        "Coordinate-based input simulation is not wired on this platform yet. Register an IInputSimulator implementation via AddSingleton<IInputSimulator, ...> in your host's Program.cs.");

    public void MoveTo(double x, double y) => throw Reason;
    public void Click(double x, double y, int clickCount = 1, MouseButton button = MouseButton.Left) => throw Reason;
    public void DragTo(double fromX, double fromY, double toX, double toY) => throw Reason;
    public void TypeText(string text) => throw Reason;
    public void PressKey(string xdotoolKeyName) => throw Reason;
}

internal sealed class NotSupportedFocusBackend : IFocusBackend
{
    public nint GetForegroundWindow() => 0;
    public bool TryAxRaise(nint window) => false;
    public void Activate(nint window) { }
}
