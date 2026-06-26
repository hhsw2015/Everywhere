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
/// Default DI binding: null. Mac DI registers the OCCU
/// implementation when <c>EVERYWHERE_USE_OCCU=1</c> AND the helper
/// dylib is loadable.
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
}
