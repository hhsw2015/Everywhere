using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class AddAnnotationTool
{
    private static readonly string AllowedSources =
        string.Join(", ", Enum.GetNames<AnnotationSource>().Select(n => n.ToLowerInvariant()));

    [McpServerTool(Name = "add_annotation")]
    [Description(
        "Queue a user note against a perception anchor. Ships in next [everywhere-ctx]. " +
        "source: pin|whiteboard|selected|linkrect. body: note text. " +
        "anchor_label: short human-readable target description. " +
        "anchor_ref (optional): opaque id (e.g. element_index for pin). " +
        "Returns {queued:<int>}.")]
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
            return ToolErrors.Error($"Invalid source '{source}'. Expected one of: {AllowedSources}.");

        int queuedAfter;
        try
        {
            queuedAfter = annotations.Add(new AnnotationItem(
                Source: parsedSource,
                Body: body.Trim(),
                AnchorRef: string.IsNullOrWhiteSpace(anchor_ref) ? null : anchor_ref.Trim(),
                AnchorLabel: anchor_label.Trim(),
                CapturedAtUtc: DateTimeOffset.UtcNow));
        }
        catch (ArgumentException ex)
        {
            // Stash rejected the input (oversize body / anchor_label / queue depth).
            return ToolErrors.Error(ex.Message);
        }

        var json = JsonSerializer.Serialize(new { queued = queuedAfter });
        return new CallToolResult { Content = [new TextContentBlock { Text = json }] };
    }
}
