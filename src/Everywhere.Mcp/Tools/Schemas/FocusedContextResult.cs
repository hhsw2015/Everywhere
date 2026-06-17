using System.Text.Json.Serialization;

namespace Everywhere.Mcp.Tools.Schemas;

/// <summary>
/// JSON shape returned by <c>get_focused_context</c>. A superset of <see cref="AppStateResult"/>
/// — the extra fields advertise budget pressure and a structured tree alternative for agents
/// that prefer JSON to indented text. Null fields are suppressed.
/// </summary>
public sealed class FocusedContextResult
{
    [JsonPropertyName("app")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? App { get; set; }

    [JsonPropertyName("window_title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowTitle { get; set; }

    [JsonPropertyName("window_bounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WindowBounds? WindowBounds { get; set; }

    [JsonPropertyName("screenshot_png_b64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScreenshotPngBase64 { get; set; }

    [JsonPropertyName("tree_text")]
    public string TreeText { get; set; } = string.Empty;

    [JsonPropertyName("focused_summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FocusedSummary { get; set; }

    [JsonPropertyName("selected_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedText { get; set; }

    [JsonPropertyName("omitted_children")]
    public bool OmittedChildren { get; set; }

    [JsonPropertyName("omitted_node_count")]
    public int OmittedNodeCount { get; set; }

    [JsonPropertyName("tree_json")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TreeNode? TreeJson { get; set; }

    [JsonPropertyName("selected_items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SemanticItem>? SelectedItems { get; set; }

    [JsonPropertyName("focused_items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SemanticItem>? FocusedItems { get; set; }

    [JsonPropertyName("focused_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SemanticItem>? FocusedPath { get; set; }
}

public sealed class TreeNode
{
    [JsonPropertyName("element_index")]
    public int ElementIndex { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("bounds")]
    public WindowBounds Bounds { get; set; }

    [JsonPropertyName("states")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? States { get; set; }

    [JsonPropertyName("children")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TreeNode>? Children { get; set; }
}
