using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Everywhere.Interop;
using Everywhere.Interop.Whiteboard;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Whiteboard;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ReadWhiteboardTool
{
    [McpServerTool(Name = "read_whiteboard", ReadOnly = true)]
    [Description(
        "Read the user's whiteboard annotations: rectangular gestures the user drew on top " +
        "of the screen to single out content for this agent (the Whiteboard hotkey). " +
        "Returns one markdown section per region with the region's gesture kind " +
        "(circle/underline/arrow/x — carrying intent: emphasis / focus on a single line / " +
        "pointing / strike-through) plus the Label and Hyperlink text the gesture captured. " +
        "Returns {\"drawn\": false} when no whiteboard is fresh (one-shot consume; expires 5 min). " +
        "ALWAYS call this when the stash hint mentions whiteboard / annotated regions / " +
        "gestures — DO NOT use read_pick for whiteboard sessions, they are different stashes.")]
    public static CallToolResult ReadWhiteboard(WhiteboardStash stash)
    {
        try
        {
            var regions = stash.Take();
            if (regions is null)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = "{\"drawn\":false,\"region_count\":0,\"markdown\":null}",
                    }],
                };
            }

            var sb = new StringBuilder();
            for (var i = 0; i < regions.Count; i++)
            {
                var r = regions[i];
                sb.Append("## Region ").Append(i + 1).Append(" (")
                  .Append(KindLabel(r.Kind)).Append(", ")
                  .Append(r.Leaves.Count).Append(' ').Append(r.Leaves.Count == 1 ? "leaf" : "leaves")
                  .Append(", confidence ").Append(r.Confidence.ToString("F2"))
                  .Append(")\n\n");
                foreach (var leaf in r.Leaves)
                {
                    var text = (leaf.GetText(maxLength: 4000) ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(text)) continue;
                    // Hybrid slice: OCR-detected per-line bboxes pick which
                    // a11y lines fall under the region; falls back to
                    // leaf-bbox proportional slice when OCR is missing.
                    text = HybridSlicer.Slice(
                        regionRect: r.Rect,
                        ocrLines: r.OcrLines,
                        a11yText: text,
                        leafBbox: leaf.BoundingRectangle);
                    sb.Append(leaf.Type == VisualElementType.Hyperlink ? "- " : "")
                      .Append(text).Append('\n');
                }
                sb.Append('\n');
            }

            // Attach the first reachable app key so the agent knows which app
            // these annotations came from (all regions share an app — they're
            // captured in one screenshot).
            string? appKey = null;
            foreach (var r in regions)
            {
                foreach (var lf in r.Leaves)
                {
                    if (lf.ProcessId > 0)
                    {
                        appKey = AppKey.FromProcessId(lf.ProcessId);
                        break;
                    }
                }
                if (appKey is not null) break;
            }

            var json = JsonSerializer.Serialize(new
            {
                drawn = true,
                region_count = regions.Count,
                app = appKey,
                markdown = sb.ToString(),
            });

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = json }],
            };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "read_whiteboard");
        }
    }

    private static string KindLabel(AnnotationKind k) => k switch
    {
        AnnotationKind.Circle    => "circle = emphasis",
        AnnotationKind.Underline => "underline = focus on a single line",
        AnnotationKind.Arrow     => "arrow = pointing at this leaf",
        AnnotationKind.X         => "x = strike-through / exclude",
        _ => "unknown gesture",
    };
}
