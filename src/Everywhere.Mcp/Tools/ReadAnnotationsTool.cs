using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ReadAnnotationsTool
{
    [McpServerTool(Name = "read_annotations", ReadOnly = true)]
    [Description(
        "List queued user annotations without consuming them. Each entry: " +
        "{source, body, anchor_label, anchor_ref, captured_at}. " +
        "Returns {\"count\":<int>, \"annotations\":[...]}. " +
        "Annotations are drained automatically on the next SnapshotContext " +
        "capture; use clear_annotations to drop them without sending.")]
    public static CallToolResult ReadAnnotations(AnnotationStash annotations)
    {
        var items = annotations.Peek();
        var rows = items.Select(item => new
        {
            source = item.Source.ToString().ToLowerInvariant(),
            body = item.Body,
            anchor_label = item.AnchorLabel,
            anchor_ref = item.AnchorRef,
            captured_at = item.CapturedAtUtc,
        }).ToArray();

        var json = JsonSerializer.Serialize(new { count = rows.Length, annotations = rows });
        return new CallToolResult { Content = [new TextContentBlock { Text = json }] };
    }
}
