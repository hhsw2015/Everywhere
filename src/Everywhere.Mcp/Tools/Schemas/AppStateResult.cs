using System.Text.Json.Serialization;

namespace Everywhere.Mcp.Tools.Schemas;

/// <summary>
/// JSON shape returned by <c>get_app_state</c>. Field names mirror upstream
/// (snake_case) so existing clients deserialize unchanged.
/// </summary>
public sealed class AppStateResult
{
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
}

public readonly record struct WindowBounds(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);
