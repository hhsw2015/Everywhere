using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Everywhere.Mcp.OpenDia;

/// <summary>
/// Translates an opendia extension tool spec (the JSON object the
/// extension hands us in its `register` payload) into the protocol-level
/// <see cref="Tool"/> DTO that <c>tools/list</c> responses need.
/// </summary>
internal static class OpenDiaToolListBuilder
{
    public const string Prefix = "browser_";

    public static Tool? BuildTool(JsonObject spec)
    {
        var origName = spec["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(origName)) return null;
        var description = spec["description"]?.GetValue<string>() ?? string.Empty;
        var schema = spec["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" };

        JsonElement schemaElement;
        try
        {
            schemaElement = JsonSerializer.Deserialize<JsonElement>(schema.ToJsonString());
        }
        catch
        {
            schemaElement = JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}""");
        }

        var name = origName!.StartsWith(Prefix, StringComparison.Ordinal)
            ? origName!
            : Prefix + origName;
        return new Tool
        {
            Name = name,
            Description = description,
            InputSchema = schemaElement,
        };
    }
}
