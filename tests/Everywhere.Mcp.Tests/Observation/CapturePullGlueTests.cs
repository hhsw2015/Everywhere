using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Observation;

/// <summary>
/// v0.9.304 regression: capture_stop must pull cdp_list_network_requests +
/// cdp_list_console_messages + drain the signature hook, filtered to
/// entries since session.StartedAt.
/// </summary>
[TestFixture]
public sealed class CapturePullGlueTests
{
    private sealed class ScriptedSink : IBrowserCallSink
    {
        public List<(string Tool, JsonObject Args)> Calls { get; } = new();
        public Func<string, JsonObject, JsonNode?>? Responder { get; init; }
        public Task<JsonNode?> CallAsync(string tool, JsonObject args, CancellationToken ct)
        {
            Calls.Add((tool, JsonNode.Parse(args.ToJsonString())!.AsObject()));
            return Task.FromResult(Responder?.Invoke(tool, args));
        }
    }

    [Test]
    public async Task StopAsync_PullsNetworkAndConsole_FiltersPreStartMessages()
    {
        var clock = new FakeClock(1_000_000);
        var store = new CaptureSessionStore(clock);
        var session = store.Start(tabId: 42, origin: "example.com");
        clock.Advance(TimeSpan.FromSeconds(3));

        var sink = new ScriptedSink
        {
            Responder = (tool, args) => tool switch
            {
                "cdp_list_network_requests" => new JsonObject
                {
                    ["requests"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["requestId"] = "r1",
                            ["url"] = "https://example.com/api/foo?token=SECRET",
                            ["method"] = "GET", ["type"] = "XHR",
                            ["ts"] = 1_002_000,
                            ["status"] = 200, ["mime"] = "application/json", ["size"] = 512,
                        },
                    },
                },
                "cdp_get_response_body" => new JsonObject
                {
                    ["body"] = "{\"data\":{\"karma\":123}}",
                    ["base64"] = false,
                },
                "cdp_list_console_messages" => new JsonObject
                {
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["level"] = "log", ["text"] = "before capture", ["ts"] = 999_000 },
                        new JsonObject { ["level"] = "log", ["text"] = "in capture", ["ts"] = 1_001_500 },
                    },
                },
                "cdp_evaluate" => JsonValue.Create("null"),
                _ => null,
            },
        };

        var orch = new CaptureOrchestrator(sink);
        await orch.StopAsync(session.SessionId, tabId: 42, scriptId: null, store, CancellationToken.None);

        var pulled = store.Get(session.SessionId);

        // Network pulled + URL redacted + body sha256 recorded + body-by-hash present
        Assert.That(pulled.Network.Requests, Has.Count.EqualTo(1));
        var req = pulled.Network.Requests[0];
        Assert.That(req.Url, Does.Contain("token=<REDACTED>"));
        Assert.That(req.ResponseBodySha256, Is.Not.Empty);
        Assert.That(pulled.Network.BodiesByHash.ContainsKey(req.ResponseBodySha256), Is.True);

        // Console filtered: only in-capture message survives
        Assert.That(pulled.Console.Messages, Has.Count.EqualTo(1));
        Assert.That(pulled.Console.Messages[0].Text, Is.EqualTo("in capture"));

        // Since_ms passed through
        var netCall = sink.Calls.First(c => c.Tool == "cdp_list_network_requests");
        Assert.That(netCall.Args["since_ms"]!.GetValue<long>(), Is.EqualTo(session.StartedAt));
    }
}
