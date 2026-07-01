using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenDia;

namespace Everywhere.Mcp.OpenCli.Observation;

/// <summary>Production <see cref="IBrowserCallSink"/> that forwards to the OpenDia bridge.</summary>
public sealed class OpenDiaBrowserCallSink : IBrowserCallSink
{
    private readonly OpenDiaBridge _bridge;
    public OpenDiaBrowserCallSink(OpenDiaBridge bridge) { _bridge = bridge; }
    public Task<JsonNode?> CallAsync(string tool, JsonObject args, CancellationToken ct)
        => _bridge.CallToolAsync(tool, args, ct: ct);
}
