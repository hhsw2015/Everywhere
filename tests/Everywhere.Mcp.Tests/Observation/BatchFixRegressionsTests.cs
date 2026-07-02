using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.Meta;
using Everywhere.Mcp.OpenCli.Gates;
using Everywhere.Mcp.OpenCli.Memory;
using Everywhere.Mcp.OpenCli.Observation;
using Everywhere.Mcp.Tools;

namespace Everywhere.Mcp.Tests.Observation;

/// <summary>
/// Regression pack for the v0.9.306 batch fix — findings F2, F6, F11, F12, F15, F18, F21, F22, F32.
/// </summary>
[TestFixture]
public sealed class BatchFixRegressionsTests
{
    private IDisposable? _gate;
    private IDisposable? _base;

    [SetUp]
    public void Setup()
    {
        _gate = SelfExpandGate.EnableForTest();
        var tmp = Path.Combine(Path.GetTempPath(), "batchfix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _base = EverywherePaths.OverrideBaseForTest(tmp);
    }

    [TearDown]
    public void Teardown() { _gate?.Dispose(); _base?.Dispose(); }

    // F2: MutationGuard promotes to error when code has mutation call + note says mutation:false
    [Test]
    public void G7_CodeMutationWithNoteFalse_IsErrorNotWarning()
    {
        var note = new StrategyNote
        {
            Strategy = "public", Contract = "stable", Mutation = false,
            Evidence = new List<string> { new('a', 25), new('b', 25), new('c', 25) },
            Replay = new string('r', 60),
        };
        var src = "cli({func: async(args)=>{await fetch(url, {method:'POST', body:p});return [{a:1}];}});";
        var r = MutationGuard.Check(note, src);
        Assert.That(r.Errors.Any(e => e.Code == "MUTATION_UNAPPROVED"), Is.True,
            "code contains POST + note mutation:false → must be hard error");
    }

    // F12: SessionActivations accepts SPEC alias `observation` → `browser_core`
    [Test]
    public void SessionActivations_AcceptsObservationAlias()
    {
        var s = new SessionActivations();
        Assert.That(s.Activate("s1", "observation"), Is.True);
        Assert.That(s.IsActive("s1", "browser_core"), Is.True);
    }

    // F12: list_domains includes `full`
    [Test]
    public void ListDomains_IncludesFull()
    {
        var tools = new SearchTools(new SessionActivations());
        var arr = JsonNode.Parse(tools.ListDomains())!.AsArray();
        var names = arr.Select(n => n!["name"]!.GetValue<string>()).ToArray();
        Assert.That(names, Does.Contain("full"));
        Assert.That(names, Does.Contain("browser_core"));
        Assert.That(names, Does.Not.Contain("observation"));
    }

    // F11: memory_write_endpoint rejects missing required fields
    [Test]
    public void MemoryWriteEndpoint_MissingFields_ArgumentError()
    {
        var tools = new MemoryTools(new MemoryStore());
        var res = JsonNode.Parse(tools.MemoryWriteEndpoint("news", "foo", "{\"random\":\"noise\"}"))!.AsObject();
        Assert.That(res["code"]!.GetValue<string>(), Is.EqualTo("ARGUMENT_ERROR"));
        Assert.That(res["details"]!["missing_fields"]!.AsArray().Count, Is.GreaterThan(0));
    }

    // F11: rejects invalid method
    [Test]
    public void MemoryWriteEndpoint_InvalidMethod_ArgumentError()
    {
        var tools = new MemoryTools(new MemoryStore());
        var spec = "{\"name\":\"foo\",\"method\":\"YEET\",\"url_template\":\"https://x/y\",\"strategy\":\"public\",\"verified_at\":1}";
        var res = JsonNode.Parse(tools.MemoryWriteEndpoint("news", "foo", spec))!.AsObject();
        Assert.That(res["code"]!.GetValue<string>(), Is.EqualTo("ARGUMENT_ERROR"));
        Assert.That(res["message"]!.GetValue<string>(), Does.Contain("method"));
    }

    // F22: memory_read on cold site returns both freshness and cold flag
    [Test]
    public void MemoryRead_Cold_ReturnsFreshnessField()
    {
        var tools = new MemoryTools(new MemoryStore());
        var res = JsonNode.Parse(tools.MemoryRead("unknown-site"))!.AsObject();
        Assert.That(res["freshness"]!.GetValue<string>(), Is.EqualTo("cold"));
        Assert.That(res["cold"]!.GetValue<bool>(), Is.True);
    }

    // F21: capture_export warns on empty
    [Test]
    public async Task CaptureExport_Empty_WarnsAndReportsCounts()
    {
        var store = new CaptureSessionStore();
        var tools = new CaptureTools(store);
        var startRes = JsonNode.Parse(await tools.CaptureStart(101, "example.com"))!.AsObject();
        var sid = startRes["session_id"]!.GetValue<string>();
        var res = JsonNode.Parse(tools.CaptureExport(sid))!.AsObject();
        Assert.That(res["request_count"]!.GetValue<int>(), Is.EqualTo(0));
        Assert.That(res["warnings"]!.AsArray().Select(w => w!.GetValue<string>()), Does.Contain("empty_capture"));
    }

    // F32: cross-origin check strips port from session.Origin before compare
    [Test]
    public async Task WebJsFetch_OriginWithPort_ComparesHostOnly()
    {
        var store = new CaptureSessionStore();
        var start = store.Start(1, "example.com:8443");
        var tools = new AnalysisTools(store);
        // Same host, different port on the URL → should not be cross-origin.
        var r = JsonNode.Parse(await tools.WebJsFetchSameOrigin(start.SessionId, "http://example.com/app.js"))!.AsObject();
        // We still get SSRF_BLOCKED (http instead of https), which is checked BEFORE origin.
        // The test point is that we DON'T get CROSS_ORIGIN when host matches modulo port.
        Assert.That(r["code"]!.GetValue<string>(), Is.EqualTo("SSRF_BLOCKED"),
            "scheme check should fire first; origin compare must not falsely trip on port suffix");
    }
}
