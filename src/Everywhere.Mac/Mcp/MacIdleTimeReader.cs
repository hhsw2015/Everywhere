using System.Runtime.InteropServices;
using Everywhere.Mcp.Input;

namespace Everywhere.Mac.Mcp;

/// <summary>
/// macOS idle-time reader via CoreGraphics CGEventSourceSecondsSinceLastEventType.
/// Returns seconds since the most recent input event from any input device.
/// </summary>
public sealed class MacIdleTimeReader : IIdleTimeReader
{
    private const int CGEventSourceStateCombinedSessionState = 0;
    private const uint CGEventTypeAnyInputEventType = uint.MaxValue; // ~0 → all event types

    public double GetIdleSeconds()
    {
        try
        {
            return CGEventSourceSecondsSinceLastEventType(CGEventSourceStateCombinedSessionState, CGEventTypeAnyInputEventType);
        }
        catch
        {
            return 0;
        }
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern double CGEventSourceSecondsSinceLastEventType(int stateID, uint eventType);
}
