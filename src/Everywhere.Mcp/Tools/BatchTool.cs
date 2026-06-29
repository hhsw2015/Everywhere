using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenDia;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

/// <summary>
/// SPEC §3.3 batch — cross-substrate sequenced tool runner. Each step is
/// {tool, args}; we dispatch browser_&lt;name&gt; steps through OpenDiaBridge
/// (the WS pipe to the extension) and report a `note` for everywhere_*
/// steps until the local-tool reflective dispatcher lands in Phase 2.
///
/// On any step error the batch stops; partial results are returned with
/// {error, step_index} alongside the completed steps.
/// </summary>
[McpServerToolType]
public static class BatchTool
{
    [McpServerTool(Name = "batch")]
    [Description(
        "Run a sequence of tool calls in one round-trip. steps[] = [{tool, args}, ...]. " +
        "browser_* steps forward via the OpenDia WS bridge; everywhere.* are dispatched locally. " +
        "Stops on first error and returns the partial result list. SPEC §3.3 ab agent_browser_batch.")]
    public static async Task<CallToolResult> Batch(
        OpenDiaBridge bridge,
        JsonElement steps,
        CancellationToken ct = default)
    {
        var results = new List<JsonNode?>();
        var stopAt = -1;
        string? err = null;

        if (steps.ValueKind != JsonValueKind.Array)
        {
            return ToolErrors.FromException(new ArgumentException("steps must be a JSON array"), "batch");
        }

        var arr = steps.EnumerateArray().ToList();
        for (var i = 0; i < arr.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = arr[i];
            var name = step.TryGetProperty("tool", out var tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                err = $"step[{i}].tool missing"; stopAt = i; break;
            }
            JsonNode? argsNode = null;
            if (step.TryGetProperty("args", out var an) && an.ValueKind != JsonValueKind.Null)
            {
                argsNode = JsonNode.Parse(an.GetRawText());
            }

            try
            {
                if (name!.StartsWith("browser_", StringComparison.Ordinal))
                {
                    var origName = name.Substring("browser_".Length);
                    var r = await bridge.CallToolAsync(origName, argsNode, ct: ct);
                    results.Add(r is null ? null : JsonNode.Parse(JsonSerializer.Serialize(r)));
                }
                else if (name.StartsWith("everywhere.", StringComparison.Ordinal))
                {
                    // Phase 2: a local-tool reflective dispatcher will let
                    // batch sequence everywhere.* steps too. For now flag the
                    // step so callers know it was a no-op.
                    results.Add(JsonNode.Parse(JsonSerializer.Serialize(new
                    {
                        note = "everywhere.* dispatch not yet wired in batch (Phase 2)",
                        tool = name,
                    })));
                }
                else
                {
                    err = $"step[{i}].tool=\"{name}\" missing browser_ / everywhere. prefix";
                    stopAt = i;
                    break;
                }
            }
            catch (Exception ex)
            {
                err = ex.Message; stopAt = i; break;
            }
        }

        var payload = err is null
            ? new { ok = true, count = results.Count, results }
            : (object)new { ok = false, error = err, step_index = stopAt, count = results.Count, results };

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
        };
    }
}
