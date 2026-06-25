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

    /// <summary>
    /// Synthesize mouse down+up at (x,y).
    ///
    /// Precondition (macOS, targetPid=null): the caller MUST have raised
    /// the target app within the last ~370ms — typically by holding a
    /// <see cref="FocusBorrow.Acquire"/> with requireFocus:true, which
    /// runs OCCU's prepareAppForGlobalPointerInput sequence (AXRaise →
    /// 120ms → activate → 250ms). Without that prep, the global click
    /// hits whatever window is currently frontmost and the cursor's
    /// physical position, NOT the (x,y) you passed. The MacInputSimulator
    /// implementation no longer warps the cursor as a safety net; it
    /// trusts the caller to have prepared focus.
    /// </summary>
    void Click(double x, double y, int clickCount = 1, MouseButton button = MouseButton.Left, int? targetPid = null);

    void DragTo(double fromX, double fromY, double toX, double toY, int? targetPid = null);

    void TypeText(string text, int? targetPid = null);

    void PressKey(string xdotoolKeyName, int? targetPid = null);

    /// <summary>
    /// Scroll wheel event at a screen-pixel coordinate. Mirrors OCCU
    /// InputSimulation.scrollTargeted/scrollGlobally:
    /// CGEventCreateScrollWheelEvent2 with units=line, wheelCount=2,
    /// wheel1=vertical (up=+, down=-), wheel2=horizontal (left=+,
    /// right=-), each delta = round(12 * pages) clamped to int32.
    /// direction is one of "up", "down", "left", "right".
    /// 100ms post-event sleep applied internally.
    /// </summary>
    void Scroll(double x, double y, string direction, double pages = 1, int? targetPid = null) { /* default no-op for unsupported platforms */ }
}

public enum MouseButton
{
    Left,
    Right,
    Middle,
}
