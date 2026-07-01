using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Analysis;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Observation;

[TestFixture]
public sealed class CaptureHookTests
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
    public async Task StartAsync_InstallsInitScript_AndCoverCurrentDocumentViaCdpEvaluate()
    {
        var sink = new ScriptedSink
        {
            Responder = (tool, args) => tool == "add_init_script"
                ? new JsonObject { ["ok"] = true, ["id"] = "script-42" }
                : null,
        };
        var orch = new CaptureOrchestrator(sink);
        var id = await orch.StartAsync(tabId: 101, ct: CancellationToken.None);
        Assert.That(id, Is.EqualTo("script-42"));
        Assert.That(sink.Calls.Select(c => c.Tool), Is.EqualTo(new[] { "add_init_script", "cdp_evaluate" }));
        Assert.That(sink.Calls[0].Args["tab_id"]!.GetValue<int>(), Is.EqualTo(101));
        Assert.That(sink.Calls[0].Args["script"]!.GetValue<string>(), Does.Contain("__ew_capture__"));
    }

    [Test]
    public async Task StopAsync_DrainsSignaturesIntoSession_AndRemovesScript()
    {
        var drainPayload = new JsonObject
        {
            ["signatures"] = new JsonArray
            {
                new JsonObject
                {
                    ["ts"] = 1000,
                    ["url"] = "https://api.example.com/v1/query?token=SHOULD_BE_REDACTED",
                    ["method"] = "POST",
                    ["payload_sha256"] = "deadbeef",
                    ["payload_shape"] = "string",
                    ["payload_sample"] = "hello world",
                    ["signature_headers"] = new JsonObject
                    {
                        ["X-Signature"] = "0123456789abcdef0123456789abcdef",
                    },
                },
            },
            ["dropped"] = 0,
        };
        var sink = new ScriptedSink
        {
            Responder = (tool, args) => tool switch
            {
                "cdp_evaluate" => JsonValue.Create(drainPayload.ToJsonString()),
                _ => null,
            },
        };
        var orch = new CaptureOrchestrator(sink);
        var store = new CaptureSessionStore();
        var session = store.Start(101, "example.com");

        await orch.StopAsync(session.SessionId, tabId: 101, scriptId: "script-42", store, CancellationToken.None);

        var stored = store.Get(session.SessionId);
        Assert.That(stored.Signatures, Has.Count.EqualTo(1));
        var s = stored.Signatures[0];
        Assert.That(s.Method, Is.EqualTo("POST"));
        Assert.That(s.SignatureHeaders["X-Signature"], Does.Not.Contain("REDACTED"));
        Assert.That(s.Url, Does.Contain("token=<REDACTED>"));
        // remove_init_script should have been requested
        Assert.That(sink.Calls.Any(c => c.Tool == "remove_init_script"), Is.True);
    }

    [Test]
    public async Task StopAsync_BuggyPayload_DropsSilently_And_LeavesSessionEmpty()
    {
        var sink = new ScriptedSink
        {
            Responder = (tool, args) => tool == "cdp_evaluate" ? JsonValue.Create("<not-json>") : null,
        };
        var store = new CaptureSessionStore();
        var session = store.Start(101, "example.com");
        var orch = new CaptureOrchestrator(sink);
        await orch.StopAsync(session.SessionId, tabId: 101, scriptId: null, store, CancellationToken.None);
        Assert.That(store.Get(session.SessionId).Signatures, Is.Empty);
    }

    [Test]
    public void SignatureScheme_Detect_ClassifiesHexHmacFromHookExample()
    {
        var session = new CaptureSession { Origin = "example.com", SessionId = "s1" };
        session.Signatures.Add(new CaptureSession.SignatureSample
        {
            Url = "https://example.com/api",
            Method = "POST",
            PayloadSha256 = "deadbeef",
            PayloadSample = "{\"a\":1}",
            SignatureHeaders = new(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Sign"] = "0123456789abcdef0123456789abcdef",
            },
        });
        var v = SignatureScheme.Detect(session);
        Assert.That(v.Scheme, Is.EqualTo("hmac_sha256"));
        Assert.That(v.Examples, Has.Count.EqualTo(1));
        Assert.That(v.Examples[0].Headers["X-Sign"], Is.Not.Null);
    }

    [Test]
    public void SignatureScheme_HookJwt_PromotesJwtVerdict()
    {
        var session = new CaptureSession { Origin = "example.com", SessionId = "s2" };
        session.Signatures.Add(new CaptureSession.SignatureSample
        {
            Url = "https://example.com/me", Method = "GET",
            SignatureHeaders = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer eyJhbGciOiJIUzI1NiJ9.stuff",
            },
        });
        Assert.That(SignatureScheme.Detect(session).Scheme, Is.EqualTo("jwt"));
    }
}
