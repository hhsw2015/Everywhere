using System.Text.Json.Serialization;

namespace Everywhere.Mcp.Tools.Schemas;

/// <summary>
/// JSON shape returned by <c>get_focused_context</c>. A superset of <see cref="AppStateResult"/>
/// — the extra fields advertise budget pressure and a structured tree alternative for agents
/// that prefer JSON to indented text.
/// </summary>
public sealed class FocusedContextResult
{
    [JsonPropertyName("app")]
    public string? App { get; set; }

    [JsonPropertyName("window_title")]
    public string? WindowTitle { get; set; }

    [JsonPropertyName("window_bounds")]
    public WindowBounds? WindowBounds { get; set; }

    [JsonPropertyName("screenshot_png_b64")]
    public string? ScreenshotPngBase64 { get; set; }

    [JsonPropertyName("tree_text")]
    public string TreeText { get; set; } = string.Empty;

    [JsonPropertyName("focused_summary")]
    public string? FocusedSummary { get; set; }

    [JsonPropertyName("selected_text")]
    public string? SelectedText { get; set; }

    [JsonPropertyName("omitted_children")]
    public bool OmittedChildren { get; set; }

    [JsonPropertyName("omitted_node_count")]
    public int OmittedNodeCount { get; set; }

    [JsonPropertyName("tree_json")]
    public TreeNode? TreeJson { get; set; }
}

public sealed class TreeNode
{
    [JsonPropertyName("element_index")]
    public int ElementIndex { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("bounds")]
    public WindowBounds Bounds { get; set; }

    [JsonPropertyName("states")]
    public string? States { get; set; }

    [JsonPropertyName("children")]
    public List<TreeNode>? Children { get; set; }
}
