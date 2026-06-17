using System.ComponentModel;
using System.Text.Json;
using Everywhere.Mcp.Input;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class GetIdleTimeTool
{
    [McpServerTool(Name = "get_idle_time", ReadOnly = true)]
    [Description(
        "Return seconds since the user last touched any input device, as JSON " +
        "{\"idle_seconds\": number}. Useful for deciding whether the user is " +
        "actively at the keyboard before showing a notification or grabbing focus.")]
    public static CallToolResult GetIdleTime(IIdleTimeReader reader)
    {
        try
        {
            var idle = reader.GetIdleSeconds();
            return new CallToolResult
            {
                Content = [new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new { idle_seconds = idle }),
                }],
            };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "get_idle_time");
        }
    }
}
