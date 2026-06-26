using System.ComponentModel;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class TypeTextTool
{
    [McpServerTool(Name = "type_text")]
    [Description("Type literal text into the focused control of the named app via simulated keystrokes. Brings the target window to the foreground first.")]
    public static CallToolResult TypeText(
        string app,
        string text,
        IServiceProvider services)
    {
        if (text is null) return ToolErrors.ParameterRequired("text");
        if (text.Length > 100_000) return ToolErrors.Error("text exceeds 100 000 character limit.");

        if (services.GetService(typeof(IAxBridgeBackend)) is not IAxBridgeBackend backend)
            return ToolErrors.OccuRequired("type_text");

        var (txt, isError) = backend.TypeText(app, text);
        return isError
            ? ToolErrors.Error(txt)
            : new CallToolResult { Content = [new TextContentBlock { Text = txt }] };
    }
}
