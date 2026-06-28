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
                var imgCount = r.ImageLeaves.Count;
                sb.Append("## Region ").Append(i + 1).Append(" (")
                  .Append(KindLabel(r.Kind)).Append(", ")
                  .Append(r.Leaves.Count).Append(' ').Append(r.Leaves.Count == 1 ? "leaf" : "leaves");
                if (imgCount > 0)
                    sb.Append(", ").Append(imgCount).Append(imgCount == 1 ? " image" : " images");
                sb.Append(", confidence ").Append(r.Confidence.ToString("F2"))
                  .Append(")\n\n");
                // De-duplicate leaves whose text is fully contained in
                // another selected leaf's text — happens when a Hyperlink
                // and its child Label both pass the snapper filter.
                var emitted = new HashSet<string>(StringComparer.Ordinal);
                var emittedAnyLeafText = false;
                foreach (var leaf in r.Leaves)
                {
                    // Use a generous cap so long code-block Labels aren't
                    // truncated before the slicer can index into them.
                    var text = (leaf.GetText(maxLength: 64000) ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(text)) continue;
                    // Hybrid slice: OCR-detected per-line bboxes pick which
                    // a11y lines fall under the region; falls back to
                    // leaf-bbox proportional slice when OCR is missing.
                    var sliced = HybridSlicer.Slice(
                        regionRect: r.Rect,
                        ocrLines: r.OcrLines,
                        a11yText: text,
                        leafBbox: leaf.BoundingRectangle);
                    // Arrow points AT a paragraph — expand the slice to
                    // the nearest blank-line boundaries on each side so
                    // the agent receives the whole paragraph, not just
                    // whichever rows happened to overlap the small arrow
                    // tip rect. Circle/X/underline already imply
                    // user-drawn boundaries, so they keep the tight slice.
                    text = r.Kind == AnnotationKind.Arrow
                        ? ExpandToParagraph(text, sliced)
                        : sliced;
                    text = text.Trim();
                    if (string.IsNullOrEmpty(text)) continue;
                    if (!emitted.Add(text)) continue;
                    sb.Append(leaf.Type == VisualElementType.Hyperlink ? "- " : "")
                      .Append(text).Append('\n');
                    emittedAnyLeafText = true;
                }
                // OCR fallback: every leaf in this region had empty text
                // (anchor wrapping an icon/canvas, hidden Label) but OCR
                // saw real glyphs in the gesture rect. Fall back to those
                // lines so the agent gets the visible content instead of
                // an empty region body.
                if (!emittedAnyLeafText && r.OcrLines.Count > 0)
                {
                    foreach (var line in r.OcrLines)
                    {
                        var t = (line.Text ?? string.Empty).Trim();
                        if (string.IsNullOrEmpty(t)) continue;
                        if (!emitted.Add(t)) continue;
                        sb.Append(t).Append('\n');
                    }
                }
                // Image markers: surface metadata only. The agent decides
                // whether the image is worth pulling in — call
                // read_whiteboard_image(image_id) when it is, otherwise
                // skip and save the multimodal tokens.
                foreach (var img in r.ImageLeaves)
                {
                    sb.Append("![image: ");
                    sb.Append(SanitizeAlt(img.Alt));
                    sb.Append(' ').Append(img.Bbox.Width).Append('x').Append(img.Bbox.Height);
                    sb.Append(", image_id=").Append(img.ImageId).Append("]\n");
                    sb.Append("(call read_whiteboard_image(\"")
                      .Append(img.ImageId).Append("\") to view)\n");
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

    /// <summary>
    /// Expand <paramref name="sliced"/> to the paragraph boundaries it
    /// sits inside in <paramref name="full"/>. Paragraphs are
    /// blank-line-separated. Used for Arrow gestures: the tip naturally
    /// points at "this paragraph", but a row-bbox slice cuts mid-paragraph.
    /// </summary>
    private static string ExpandToParagraph(string full, string sliced)
    {
        var fullLines = full.Split('\n');
        var slicedLines = sliced.Split('\n');
        // Find slice's first non-empty line in full.
        var anchor = slicedLines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (string.IsNullOrEmpty(anchor)) return sliced;
        var idx = Array.IndexOf(fullLines, anchor);
        if (idx < 0) return sliced;
        // Walk up to previous blank line (or start).
        var start = idx;
        while (start > 0 && !string.IsNullOrWhiteSpace(fullLines[start - 1])) start--;
        // Walk down to next blank line (or end).
        var end = idx;
        while (end < fullLines.Length - 1 && !string.IsNullOrWhiteSpace(fullLines[end + 1])) end++;
        return string.Join('\n', fullLines.Skip(start).Take(end - start + 1));
    }

    /// <summary>
    /// Strip characters that would break the single-line marker format
    /// (![image: ALT WxH, image_id=...]) and cap length so a multi-line
    /// alt doesn't flood the markdown.
    /// </summary>
    private static string SanitizeAlt(string? alt)
    {
        if (string.IsNullOrEmpty(alt)) return "(no alt)";
        var cleaned = alt
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace(']', ')')
            .Trim();
        const int Cap = 120;
        if (cleaned.Length > Cap) cleaned = cleaned.Substring(0, Cap) + "…";
        return cleaned;
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
