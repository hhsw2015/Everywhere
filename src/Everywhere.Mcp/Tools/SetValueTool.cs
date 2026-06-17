using System.ComponentModel;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class SetValueTool
{
    [McpServerTool(Name = "set_value")]
    [Description("Replace the textual value of an indexed editable element (text field, combo box, slider, etc.). Pure accessibility-pattern path; no keystroke simulation, no focus borrow.")]
    public static CallToolResult SetValue(
        string app,
        string element_index,
        string value,
        SessionStore sessions)
    {
        if (string.IsNullOrEmpty(element_index)) return ToolErrors.ParameterRequired("element_index");
        if (value is null) return ToolErrors.ParameterRequired("value");

        var (error, element) = ElementResolver.Resolve(sessions, element_index);
        if (error is not null) return error;

        try
        {
            element!.SetText(value);
            return new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        }
        catch (Exception ex)
        {
            return ToolErrors.Error($"Failed to set value: {ex.Message}");
        }
    }
}
