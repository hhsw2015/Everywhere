using System.Text.Json.Serialization;

namespace Everywhere.Mcp.Tools.Schemas;

/// <summary>
/// First-class semantic view of an element — index + type + inline label text
/// + a11y states. Used in <c>selected_items</c> / <c>focused_items</c> /
/// <c>focused_path</c> top-level fields so the agent doesn't grep tree_text.
/// </summary>
public sealed class SemanticItem
{
    [JsonPropertyName("element_index")]
    public int ElementIndex { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("states")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? States { get; set; }

    /// <summary>
    /// Suggested MCP tools the agent can use on this element, derived from element type.
    /// E.g. ["click","perform_secondary_action"] for Button; ["set_value"] for TextEdit.
    /// </summary>
    [JsonPropertyName("available_actions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AvailableActions { get; set; }
}
