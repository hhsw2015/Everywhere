// Mirrors: packages/OpenComputerUseKit/Sources/OpenComputerUseKit/InputSimulation.swift
// Upstream: iFurySt/open-codex-computer-use@<sha-pinned-in-UPSTREAM_REF.md>

namespace Everywhere.Mcp.Input;

/// <summary>
/// Platform-agnostic shape mirroring upstream <c>InputSimulator</c>. One implementation
/// per OS; the GUI host registers the right one via DI in its <c>Program.cs</c>.
/// Keep method names verbatim with upstream — tests share fixture data.
/// </summary>
public interface IInputSimulator
{
    void MoveTo(double x, double y);

    void Click(double x, double y, int clickCount = 1, MouseButton button = MouseButton.Left);

    void DragTo(double fromX, double fromY, double toX, double toY);

    void TypeText(string text);

    void PressKey(string xdotoolKeyName);
}

public enum MouseButton
{
    Left,
    Right,
    Middle,
}
