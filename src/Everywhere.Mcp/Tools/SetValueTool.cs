using System.ComponentModel;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class SetValueTool
{
    [McpServerTool(Name = "set_value")]
    [Description("Replace the textual value of an indexed editable element (text field, combo box, slider, etc.) via AX SetValue. NOTE: some web inputs (Stripe / Cloudflare / certain Electron apps) reject scripted SetValue on security-sensitive fields — for those, use type_text after focusing the element.")]
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
