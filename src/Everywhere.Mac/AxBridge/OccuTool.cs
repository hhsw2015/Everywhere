using System.Text.Json;

namespace Everywhere.Mac.AxBridge;

/// <summary>
/// Wrapper invoking OCCU's tools through libAxHelper.dylib. Each
/// helper returns the OCCU MCP-shaped JSON ({content:[{type, text}],
/// isError:bool}) ready to be re-emitted through the Everywhere MCP
/// surface verbatim, OR throws OccuToolException on dylib-level
/// failure (NULL return + last_error).
///
/// Why not unmarshal into a structured AppSnapshot here? OCCU's
/// element-index API is the canonical handle into the snapshot —
/// click(elementIndex=N) reads from OCCU's own per-app cache
/// (snapshotsByApp), so we should pass through the index strings as
/// opaque tokens. Wrapping them through our IVisualElement layer
/// would re-introduce the .NET-side traversal we're trying to avoid.
/// </summary>
internal static class OccuTool
{
    public sealed class OccuToolException(string message) : Exception(message);

    /// <summary>
    /// {content:[{type:"text",text:"..."}], isError:false}
    /// </summary>
    public sealed class OccuResult
    {
        public string PrimaryText { get; init; } = string.Empty;
        public bool IsError { get; init; }
        public string Raw { get; init; } = string.Empty;
    }

    private static OccuResult Parse(nint cstr)
    {
        var json = LibAxHelper.ConsumeCString(cstr);
        if (json is null)
        {
            var err = LibAxHelper.LastErrorMessage();
            throw new OccuToolException(string.IsNullOrEmpty(err)
                ? "ax helper returned NULL with no error message"
                : err);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var isError = root.TryGetProperty("isError", out var e) && e.GetBoolean();
            string text = string.Empty;
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) &&
                        type.GetString() == "text" &&
                        item.TryGetProperty("text", out var t))
                    {
                        text = t.GetString() ?? string.Empty;
                        break;
                    }
                }
            }
            return new OccuResult { PrimaryText = text, IsError = isError, Raw = json };
        }
        catch (JsonException ex)
        {
            throw new OccuToolException($"ax helper returned malformed JSON: {ex.Message}\nbody: {json}");
        }
    }

    public static OccuResult ListApps() => Parse(LibAxHelper.ListApps());

    public static OccuResult GetAppState(string app, bool showFullText = false)
        => Parse(LibAxHelper.GetAppState(app, showFullText ? 1 : 0));

    public static OccuResult Click(string app, string? elementIndex, double x, double y, bool useXY, int clickCount, string mouseButton = "left")
        => Parse(LibAxHelper.Click(app, elementIndex, x, y, useXY ? 1 : 0, clickCount, mouseButton));

    public static OccuResult Scroll(string app, string direction, string elementIndex, double pages)
        => Parse(LibAxHelper.Scroll(app, direction, elementIndex, pages));

    public static OccuResult Drag(string app, double fromX, double fromY, double toX, double toY)
        => Parse(LibAxHelper.Drag(app, fromX, fromY, toX, toY));

    public static OccuResult TypeText(string app, string text)
        => Parse(LibAxHelper.TypeText(app, text));

    public static OccuResult PressKey(string app, string key)
        => Parse(LibAxHelper.PressKey(app, key));

    public static OccuResult SetValue(string app, string elementIndex, string value)
        => Parse(LibAxHelper.SetValue(app, elementIndex, value));
}
