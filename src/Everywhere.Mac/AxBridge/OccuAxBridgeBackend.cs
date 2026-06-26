using Everywhere.Interop;

namespace Everywhere.Mac.AxBridge;

/// <summary>
/// IAxBridgeBackend backed by libAxHelper.dylib (Swift wrapper over
/// OpenComputerUseKit). Each call routes through OccuTool which
/// already returns the MCP-shaped JSON we then unwrap to the
/// (Text, IsError) tuple expected at the tool layer.
///
/// All exceptions are caught and surfaced as <c>(message, true)</c>
/// — never propagated, since the caller (MCP tool) returns the
/// tuple verbatim and a thrown exception there would degrade to a
/// generic 500-style failure response.
/// </summary>
internal sealed class OccuAxBridgeBackend : IAxBridgeBackend
{
    public (string Text, bool IsError) ListApps() => Run(() => OccuTool.ListApps());

    public (string Text, bool IsError) GetAppState(string app, bool showFullText)
        => Run(() => OccuTool.GetAppState(app, showFullText));

    public (string Text, bool IsError) Click(string app, string? elementIndex, double x, double y, bool useXY, int clickCount, string mouseButton)
        => Run(() => OccuTool.Click(app, elementIndex, x, y, useXY, clickCount, mouseButton));

    public (string Text, bool IsError) Scroll(string app, string direction, string elementIndex, double pages)
        => Run(() => OccuTool.Scroll(app, direction, elementIndex, pages));

    public (string Text, bool IsError) Drag(string app, double fromX, double fromY, double toX, double toY)
        => Run(() => OccuTool.Drag(app, fromX, fromY, toX, toY));

    public (string Text, bool IsError) TypeText(string app, string text)
        => Run(() => OccuTool.TypeText(app, text));

    public (string Text, bool IsError) PressKey(string app, string key)
        => Run(() => OccuTool.PressKey(app, key));

    public (string Text, bool IsError) SetValue(string app, string elementIndex, string value)
        => Run(() => OccuTool.SetValue(app, elementIndex, value));

    private static (string, bool) Run(Func<OccuTool.OccuResult> op)
    {
        try
        {
            var r = op();
            return (r.PrimaryText, r.IsError);
        }
        catch (OccuTool.OccuToolException ex)
        {
            return ($"[occu helper error] {ex.Message}", true);
        }
        catch (Exception ex)
        {
            return ($"[unexpected ax bridge error] {ex.GetType().Name}: {ex.Message}", true);
        }
    }
}
