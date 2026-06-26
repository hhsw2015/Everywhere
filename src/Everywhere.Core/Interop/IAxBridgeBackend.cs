namespace Everywhere.Interop;

/// <summary>
/// Optional native AX backend (currently only the macOS OCCU /
/// libAxHelper.dylib path implements it). Wraps the platform's
/// snapshot-and-act primitives at the MCP-tool boundary so the
/// existing IVisualElement-based traversal can be bypassed when a
/// faster native path exists.
///
/// Each method returns a tuple <c>(text, isError)</c> mirroring the
/// shape of an MCP tools/call result content. Implementations MUST
/// NOT throw — wrap any backend errors into <c>(message, true)</c>.
///
/// Default DI binding on non-macOS: null. Mac DI registers the OCCU
/// implementation by default; set <c>EVERYWHERE_USE_OCCU=0</c> as a
/// kill switch to disable registration (the eight automation tools
/// then hard-error with <c>OccuRequired</c>).
/// </summary>
public interface IAxBridgeBackend
{
    (string Text, bool IsError) ListApps();
    (string Text, bool IsError) GetAppState(string app, bool showFullText);
    (string Text, bool IsError) Click(string app, string? elementIndex, double x, double y, bool useXY, int clickCount, string mouseButton);
    (string Text, bool IsError) Scroll(string app, string direction, string elementIndex, double pages);
    (string Text, bool IsError) Drag(string app, double fromX, double fromY, double toX, double toY);
    (string Text, bool IsError) TypeText(string app, string text);
    (string Text, bool IsError) PressKey(string app, string key);
    (string Text, bool IsError) SetValue(string app, string elementIndex, string value);
    (string Text, bool IsError) PerformSecondaryAction(string app, string elementIndex, string action);
}
