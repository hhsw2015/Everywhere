using System.ComponentModel;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class SetValueTool
{
    [McpServerTool(Name = "set_value")]
    [Description("Replace text of an indexed editable element via AX SetValue. Some fields (Stripe/Cloudflare/some Electron) reject scripted SetValue — fall back to click + press_key(super+a) + press_key(BackSpace) + type_text.")]
    public static CallToolResult SetValue(
        string app,
        string element_index,
        string value,
        IServiceProvider services)
    {
        if (string.IsNullOrEmpty(element_index)) return ToolErrors.ParameterRequired("element_index");
        if (value is null) return ToolErrors.ParameterRequired("value");

        if (services.GetService(typeof(IAxBridgeBackend)) is not IAxBridgeBackend backend)
            return ToolErrors.OccuRequired("set_value");

        var (txt, isError) = backend.SetValue(app, element_index, value);
        return isError
            ? ToolErrors.Error(txt)
            : new CallToolResult { Content = [new TextContentBlock { Text = txt }] };
    }
}
