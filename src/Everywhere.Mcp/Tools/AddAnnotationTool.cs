using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class AddAnnotationTool
{
    [McpServerTool(Name = "add_annotation")]
    [Description(
        "Queue a user-authored note against a perception anchor. Notes accumulate " +
        "across pin / whiteboard / selected / linkrect captures and ship together " +
        "in the next [everywhere-ctx] payload. " +
        "source: one of \"pin\"|\"whiteboard\"|\"selected\"|\"linkrect\". " +
        "body: the user's note text. " +
        "anchor_label: short human-readable description of what the note attaches to " +
        "(e.g. 'AXButton \"Submit\" in Notes', 'region 210x110 circle in Pages'). " +
        "anchor_ref (optional): opaque id the source-specific stash can re-resolve " +
        "(e.g. element_index for pin). " +
        "Returns {\"queued\":<int>} = total annotations now waiting to be sent.")]
    public static CallToolResult AddAnnotation(
        AnnotationStash annotations,
        string source,
        string body,
        string anchor_label,
        string? anchor_ref = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            return ToolErrors.ParameterRequired("source");
        if (string.IsNullOrWhiteSpace(body))
            return ToolErrors.ParameterRequired("body");
        if (string.IsNullOrWhiteSpace(anchor_label))
            return ToolErrors.ParameterRequired("anchor_label");

        if (!Enum.TryParse<AnnotationSource>(source, ignoreCase: true, out var parsedSource))
            return ToolErrors.Error($"Invalid source '{source}'. Expected one of: pin, whiteboard, selected, linkrect.");

        annotations.Add(new AnnotationItem(
            Source: parsedSource,
            Body: body.Trim(),
            AnchorRef: string.IsNullOrWhiteSpace(anchor_ref) ? null : anchor_ref.Trim(),
            AnchorLabel: anchor_label.Trim(),
            CapturedAtUtc: DateTimeOffset.UtcNow));

        var json = JsonSerializer.Serialize(new { queued = annotations.Count });
        return new CallToolResult { Content = [new TextContentBlock { Text = json }] };
    }
}
