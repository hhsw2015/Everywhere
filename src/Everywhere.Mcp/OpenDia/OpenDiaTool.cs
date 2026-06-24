using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.OpenDia;

/// <summary>
/// Concrete <see cref="McpServerTool"/> backed by the opendia browser
/// extension over the websocket bridge. Each tool the extension registers
/// in its `register` payload becomes one of these. Adding to
/// <see cref="ModelContextProtocol.Server.McpServerOptions.ToolCollection"/>
/// makes it visible to MCP clients (cmux, claude-code, ...) alongside
/// Everywhere's static tools.
/// </summary>
internal sealed class OpenDiaTool : McpServerTool
{
    private readonly OpenDiaBridge _bridge;
    private readonly string _extToolName;
    public override Tool ProtocolTool { get; }

    public OpenDiaTool(OpenDiaBridge bridge, string extToolName, Tool protocolTool)
    {
        _bridge = bridge;
        _extToolName = extToolName;
        ProtocolTool = protocolTool;
    }

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        // Repack the strongly-typed Arguments dict back into a JsonObject —
        // that's the shape the opendia extension expects on the wire.
        JsonNode? args = null;
        var argsDict = request.Params?.Arguments;
        if (argsDict is { Count: > 0 })
        {
            var obj = new JsonObject();
            foreach (var (k, v) in argsDict)
            {
                obj[k] = JsonNode.Parse(v.GetRawText());
            }
            args = obj;
        }

        try
        {
            var result = await _bridge.CallToolAsync(_extToolName, args, ct: cancellationToken);
            return new CallToolResult
            {
                Content = [new TextContentBlock
                {
                    Text = result is null ? "{}" : result.ToJsonString(),
                }],
            };
        }
        catch (Exception ex)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = ex.Message }],
            };
        }
    }
}
