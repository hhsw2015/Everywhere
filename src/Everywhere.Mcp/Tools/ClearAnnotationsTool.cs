using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ClearAnnotationsTool
{
    [McpServerTool(Name = "clear_annotations")]
    [Description(
        "Drop every queued annotation without sending. Use when the user " +
        "abandons a draft or you've decided the annotations are stale.")]
    public static CallToolResult ClearAnnotations(AnnotationStash annotations)
    {
        var before = annotations.Count;
        annotations.Clear();
        var json = JsonSerializer.Serialize(new { cleared = before });
        return new CallToolResult { Content = [new TextContentBlock { Text = json }] };
    }
}
