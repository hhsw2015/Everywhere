using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Everywhere.Mcp.Connector;

/// <summary>
/// SPEC docs/specs/everywhere-connector.md §9.1 — in-memory ring buffer
/// of the last N provider action invocations. ConnectorRuntime.InvokeAsync
/// records into this store; the Web Console reads /api/runs.
///
/// Not persisted: process restart wipes the log. Callers that need
/// durable auditing should treat this as a debug aid, not a
/// compliance record.
/// </summary>
public sealed class RunLogStore
{
    private readonly int _capacity;
    private readonly ConcurrentQueue<JsonObject> _entries = new();

    public RunLogStore(int capacity = 500)
    {
        _capacity = Math.Max(1, capacity);
    }

    public void Record(string actionId, string caller, DateTimeOffset startedAt, DateTimeOffset completedAt, bool ok, string? errorCode, string? errorMessage, JsonNode? inputSummary)
    {
        var entry = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["actionId"] = actionId,
            ["caller"] = caller,
            ["startedAt"] = startedAt.ToString("O"),
            ["completedAt"] = completedAt.ToString("O"),
            ["durationMs"] = (long)(completedAt - startedAt).TotalMilliseconds,
            ["ok"] = ok,
            ["inputSummary"] = inputSummary?.DeepClone(),
            ["errorCode"] = errorCode,
            ["errorMessage"] = errorMessage,
        };
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _)) { }
    }

    public JsonArray Snapshot()
    {
        var arr = new JsonArray();
        // Newest first — SPA renders most recent at top.
        foreach (var e in _entries.ToArray().Reverse())
        {
            arr.Add(e.DeepClone());
        }
        return arr;
    }
}

/// <summary>
/// In-memory runtime token registry. Everywhere daemon runs loopback-only
/// so this is largely ceremonial — the console can create/list/delete
/// tokens for parity with upstream open-connector, but they aren't used
/// for auth here.
/// </summary>
public sealed class RuntimeTokenStore
{
    private readonly ConcurrentDictionary<string, JsonObject> _tokens = new();

    public JsonArray List()
    {
        var arr = new JsonArray();
        foreach (var t in _tokens.Values.OrderByDescending(v => v["createdAt"]?.GetValue<string>()))
        {
            arr.Add(new JsonObject
            {
                ["id"] = t["id"]?.GetValue<string>(),
                ["name"] = t["name"]?.GetValue<string>(),
                ["createdAt"] = t["createdAt"]?.GetValue<string>(),
                ["lastUsedAt"] = t["lastUsedAt"]?.GetValue<string>(),
                ["revokedAt"] = t["revokedAt"]?.GetValue<string>(),
            });
        }
        return arr;
    }

    public JsonObject Create(string name)
    {
        var id = Guid.NewGuid().ToString("N");
        var token = "ev_" + Guid.NewGuid().ToString("N");
        var summary = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["lastUsedAt"] = null,
            ["revokedAt"] = null,
        };
        _tokens[id] = summary;
        return new JsonObject
        {
            ["token"] = token,
            ["record"] = summary.DeepClone(),
        };
    }

    public bool Revoke(string id)
    {
        if (_tokens.TryGetValue(id, out var t))
        {
            t["revokedAt"] = DateTimeOffset.UtcNow.ToString("O");
            return true;
        }
        return false;
    }
}
