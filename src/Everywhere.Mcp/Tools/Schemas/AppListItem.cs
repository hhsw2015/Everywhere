using System.Text.Json.Serialization;

namespace Everywhere.Mcp.Tools.Schemas;

/// <summary>
/// One entry in the <c>list_apps</c> response. Field names mirror upstream so
/// existing client-side parsers work unchanged.
/// </summary>
public sealed class AppListItem
{
    [JsonPropertyName("app")]
    public string App { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("process_id")]
    public int ProcessId { get; set; }
}
