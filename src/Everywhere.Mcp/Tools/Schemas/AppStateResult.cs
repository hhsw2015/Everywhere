using System.Text.Json.Serialization;

namespace Everywhere.Mcp.Tools.Schemas;

/// <summary>
/// JSON shape returned by <c>get_app_state</c>. Field names mirror upstream
/// (snake_case) so existing clients deserialize unchanged. Null fields are
/// suppressed so the agent doesn't see noisy <c>"selected_text":null</c> rows.
/// </summary>
public sealed class AppStateResult
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

    [JsonPropertyName("tree_json")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TreeNode? TreeJson { get; set; }

    [JsonPropertyName("focused_summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FocusedSummary { get; set; }

    [JsonPropertyName("selected_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedText { get; set; }
}

public readonly record struct WindowBounds(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);
