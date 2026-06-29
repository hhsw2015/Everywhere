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
        "Run a sequence of tool calls in one round-trip. " +
        "steps_json = JSON array of {tool, args}, e.g. " +
        "'[{\"tool\":\"browser_snapshot\",\"args\":{}}, {\"tool\":\"browser_click\",\"args\":{\"ref\":\"@ref3\"}}]'. " +
        "browser_* steps forward via the OpenDia WS bridge; everywhere.* are dispatched locally. " +
        "Stops on first error and returns the partial result list. SPEC §3.3 ab agent_browser_batch.")]
    public static async Task<CallToolResult> Batch(
        OpenDiaBridge bridge,
        string steps_json,
        CancellationToken ct = default)
    {
        var results = new List<JsonNode?>();
        var stopAt = -1;
        string? err = null;

        JsonNode? root;
        try { root = JsonNode.Parse(steps_json ?? "[]"); }
        catch (Exception ex) { return ToolErrors.FromException(ex, "batch"); }

        if (root is not JsonArray arrNode)
        {
            return ToolErrors.FromException(new ArgumentException("steps_json must be a JSON array"), "batch");
        }
        var arr = arrNode.ToList();
        for (var i = 0; i < arr.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = arr[i] as JsonObject;
            if (step is null)
            {
                err = $"step[{i}] not an object"; stopAt = i; break;
            }
            var name = step["tool"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name))
            {
                err = $"step[{i}].tool missing"; stopAt = i; break;
            }
            var argsNode = step["args"]?.DeepClone();

            try
            {
                if (name!.StartsWith(OpenDiaToolListBuilder.Prefix, StringComparison.Ordinal))
                {
                    results.Add(await bridge.InvokeByPrefixedName(name, argsNode, ct: ct));
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

        var payload = new JsonObject
        {
            ["ok"] = err is null,
            ["count"] = results.Count,
            ["results"] = new JsonArray(results.Select(r => r?.DeepClone()).ToArray()),
        };
        if (err is not null)
        {
            payload["error"] = err;
            payload["step_index"] = stopAt;
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = payload.ToJsonString() }],
        };
    }
}
