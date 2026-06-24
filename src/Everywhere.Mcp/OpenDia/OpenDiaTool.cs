using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.OpenDia;

/// <summary>
/// Concrete <see cref="AIFunction"/> backed by an opendia browser-extension
/// tool. Wrapped via <see cref="McpServerTool.Create(AIFunction, McpServerToolCreateOptions)"/>
/// so we get the full MCP server plumbing (JSON-schema-driven argument
/// validation, ListTools/CallTool dispatch, error envelope) without
/// re-implementing every abstract member of <see cref="McpServerTool"/>.
/// </summary>
internal sealed class OpenDiaAIFunction : AIFunction
{
    private readonly OpenDiaBridge _bridge;
    private readonly string _extToolName;
    public override string Name { get; }
    public override string Description { get; }
    public override JsonElement JsonSchema { get; }

    public OpenDiaAIFunction(
        OpenDiaBridge bridge,
        string mcpName,
        string extToolName,
        string description,
        JsonElement schema)
    {
        _bridge = bridge;
        Name = mcpName;
        _extToolName = extToolName;
        Description = description;
        JsonSchema = schema;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // Repack the AIFunctionArguments dict into a JsonObject — that's
        // the shape the opendia extension expects on the wire.
        JsonNode? args = null;
        if (arguments is { Count: > 0 })
        {
            var obj = new JsonObject();
            foreach (var (k, v) in arguments)
            {
                obj[k] = v switch
                {
                    null => null,
                    JsonElement el => JsonNode.Parse(el.GetRawText()),
                    JsonNode jn => jn.DeepClone(),
                    _ => JsonValue.Create(v),
                };
            }
            args = obj;
        }
        var result = await _bridge.CallToolAsync(_extToolName, args, ct: cancellationToken);
        // Returning the raw JsonNode lets the SDK serialize it as-is into
        // the CallToolResult content block. AIFunctionMcpServerTool wraps
        // the return value automatically.
        return result is null ? "{}" : result.ToJsonString();
    }
}
