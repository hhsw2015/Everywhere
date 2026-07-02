using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Observation;

/// <summary>
/// v0.9.307 regression — PollNetworkDeltaAsync must dedupe by request id
/// across successive calls with a shared `seenRequestIds` set. Ensures the
/// background poller doesn't re-append every request every 2s.
/// </summary>
[TestFixture]
public sealed class CapturePollerDedupTests
{
    private sealed class ScriptedSink : IBrowserCallSink
    {
        public Func<string, JsonObject, JsonNode?>? Responder { get; init; }
        public Task<JsonNode?> CallAsync(string tool, JsonObject args, CancellationToken ct)
            => Task.FromResult(Responder?.Invoke(tool, args));
    }

    private static JsonObject Req(string id, string url) => new()
    {
        ["requestId"] = id, ["url"] = url, ["method"] = "GET",
        ["ts"] = 1_000_000, ["status"] = 200, ["mime"] = "application/json", ["size"] = 100,
    };

    [Test]
    public async Task PollNetworkDelta_DoesNotAppendSameRequestTwice()
    {
        var store = new CaptureSessionStore(new FakeClock(1_000_000));
        var session = store.Start(1, "example.com");
        var sink = new ScriptedSink
        {
            Responder = (tool, _) => tool == "cdp_list_network_requests"
                ? new JsonObject { ["requests"] = new JsonArray { Req("r1", "https://x/a"), Req("r2", "https://x/b") } }
                : (tool == "cdp_get_response_body" ? new JsonObject { ["body"] = "{\"data\":{\"a\":1,\"b\":2,\"c\":3}}" } : null),
        };
        var orch = new CaptureOrchestrator(sink);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var (n1, _, _) = await orch.PollNetworkDeltaAsync(session, 1, 0, store, seen, CancellationToken.None);
        var (n2, _, _) = await orch.PollNetworkDeltaAsync(session, 1, 0, store, seen, CancellationToken.None);

        Assert.That(n1, Is.EqualTo(2), "first poll should append both r1 and r2");
        Assert.That(n2, Is.EqualTo(0), "second poll with same seen-set should append nothing");
        Assert.That(store.Get(session.SessionId).Network.Requests, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task PollNetworkDelta_BodiesUnavailable_CountedForRetryFailure()
    {
        var store = new CaptureSessionStore(new FakeClock(1_000_000));
        var session = store.Start(1, "example.com");
        var sink = new ScriptedSink
        {
            Responder = (tool, _) => tool == "cdp_list_network_requests"
                ? new JsonObject { ["requests"] = new JsonArray { Req("r1", "https://x/a") } }
                // Simulate CDP dropping the body — return object without `body`.
                : (tool == "cdp_get_response_body" ? new JsonObject { ["success"] = false } : null),
        };
        var orch = new CaptureOrchestrator(sink);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var (_, fetched, unavail) = await orch.PollNetworkDeltaAsync(session, 1, 0, store, seen, CancellationToken.None);
        Assert.That(fetched, Is.EqualTo(0));
        Assert.That(unavail, Is.EqualTo(1));
    }
}
