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
    // Guards Enqueue+Trim so concurrent Record calls can't race the
    // eviction: without the lock two threads observing Count > capacity
    // can both TryDequeue and trim below capacity.
    private readonly object _writeLock = new();

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
        lock (_writeLock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity && _entries.TryDequeue(out _)) { }
        }
    }

    public JsonArray Snapshot()
    {
        // Newest first. DeepClone each entry because a JsonObject can
        // only have one parent — putting the queued object into the
        // returned JsonArray would re-parent it out of the queue and
        // break the next Snapshot() call. The clone is unavoidable.
        var arr = new JsonArray();
        var snapshot = _entries.ToArray();
        for (var i = snapshot.Length - 1; i >= 0; i--)
        {
            arr.Add(snapshot[i].DeepClone());
        }
        return arr;
    }
}

/// <summary>
/// In-memory runtime token registry. Everywhere daemon runs loopback-only
/// so tokens serve no auth role today — LoopbackOnly middleware already
/// gates every request. This store exists purely so the vendored
/// upstream Web Console's Access page has a working create/list/revoke
/// surface for parity.
///
/// <para>NOT a security boundary:</para>
/// <list type="bullet">
///   <item>Create returns a plaintext token that is never persisted or hashed.
///         There is no Validate(token) method because no request path
///         checks tokens today.</item>
///   <item>lastUsedAt is intentionally always null — no code path updates it.</item>
///   <item>List returns revoked entries alongside active ones; revocation is
///         a display flag, not enforcement.</item>
/// </list>
/// If a future maintainer wires this to real auth, do NOT extend this
/// class in place — it would silently accept the plaintext token as
/// valid because Create doesn't hash it. Rewrite with hash-on-create +
/// constant-time Validate first, or move to a proper auth store.
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
