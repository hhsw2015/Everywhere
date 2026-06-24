// Mirrors: packages/OpenComputerUseKit/Sources/OpenComputerUseKit/InputSimulation.swift
// Upstream: iFurySt/open-codex-computer-use@<sha-pinned-in-UPSTREAM_REF.md>

namespace Everywhere.Mcp.Input;

/// <summary>
/// Platform-agnostic shape mirroring upstream <c>InputSimulator</c>. One implementation
/// per OS; the GUI host registers the right one via DI in its <c>Program.cs</c>.
/// Keep method names verbatim with upstream — tests share fixture data.
///
/// targetPid (Mac only, OCCU parity): when non-null, events are sent via
/// CGEventPostToPid(pid) instead of the global session tap. The user's
/// real cursor doesn't move; only the target process sees the event.
/// Equivalent to OCCU's clickTargeted/scrollTargeted/dragTargeted/keysTargeted
/// path — same input as a human would have produced, but the user can
/// keep using the rest of the desktop. When null, falls back to the
/// global SessionEventTap (real cursor moves, real keystrokes hit
/// whichever app has focus).
/// </summary>
public interface IInputSimulator
{
    void MoveTo(double x, double y, int? targetPid = null);

    void Click(double x, double y, int clickCount = 1, MouseButton button = MouseButton.Left, int? targetPid = null);

    void DragTo(double fromX, double fromY, double toX, double toY, int? targetPid = null);

    void TypeText(string text, int? targetPid = null);

    void PressKey(string xdotoolKeyName, int? targetPid = null);
}

public enum MouseButton
{
    Left,
    Right,
    Middle,
}
