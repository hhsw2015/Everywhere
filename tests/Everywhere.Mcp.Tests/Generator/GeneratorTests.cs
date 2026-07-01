using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Generator;
using Everywhere.Mcp.OpenCli.Memory;
using Everywhere.Mcp.OpenCli.Observation;
using Everywhere.Mcp.Tools;

namespace Everywhere.Mcp.Tests.Generator;

[TestFixture]
public sealed class GeneratorTests
{
    private IDisposable? _gate;
    private IDisposable? _base;

    [SetUp]
    public void Setup()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "everywhere-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        _base = EverywherePaths.OverrideBaseForTest(tmp);
        _gate = SelfExpandGate.EnableForTest();
    }

    [TearDown]
    public void Teardown()
    {
        _gate?.Dispose();
        _base?.Dispose();
    }

    private static StrategyNote CompleteNote() => new()
    {
        Strategy = "public", Contract = "stable",
        Evidence = new List<string>
        {
            "GET /api/karma?id=pg returns 200 application/json body",
            "response body is stable JSON {data:{karma:number}} across days",
            "no signature header required — public endpoint",
        },
        Replay = "browser → HN username page → devtools network tab → same request replays cleanly without cookies",
        Mutation = false,
    };

    [Test]
    public void Scaffold_HasNoUnresolvedPlaceholders_E9()
    {
        var store = new CaptureSessionStore();
        var mem = new MemoryStore();
        mem.WriteStrategyNote("news", "user_karma", CompleteNote());
        var sess = store.Start(1, "news.ycombinator.com");
        sess.Network.Requests.Add(new CaptureSession.NetworkRequest
        {
            RequestId = "r1", Url = "https://news.ycombinator.com/api/karma?id=pg", Status = 200,
            ResponseContentType = "application/json", ResponseSize = 900, ResponseBodySha256 = "h",
        });
        sess.Network.BodiesByHash["h"] = "{\"data\":{\"karma\":157321,\"user\":\"pg\",\"about\":\"test\"}}";

        var tools = new GeneratorTools(store, mem);
        var res = JsonNode.Parse(tools.AdapterScaffold("news", "user_karma", sess.SessionId, "get karma of an HN user"))!.AsObject();
        var prompt = res["llm_prompt"]!.GetValue<string>();
        Assert.That(System.Text.RegularExpressions.Regex.IsMatch(prompt, @"\{\{\s*[a-zA-Z_][a-zA-Z0-9_.]*\s*\}\}"), Is.False,
            "llm_prompt contains unresolved {{...}} placeholders");
    }

    [Test]
    public void Scaffold_MissingStrategyNote_Fails_G1()
    {
        var store = new CaptureSessionStore();
        var sess = store.Start(1, "example.com");
        var tools = new GeneratorTools(store, new MemoryStore());
        var res = JsonNode.Parse(tools.AdapterScaffold("news", "user_karma", sess.SessionId))!.AsObject();
        Assert.That(res["code"]!.GetValue<string>(), Is.EqualTo("STRATEGY_NOTE_MISSING"));
    }

    [Test]
    public void Save_UntypedThrow_ReturnsUntypedThrowCode()
    {
        var mem = new MemoryStore();
        mem.WriteStrategyNote("example", "thing", CompleteNote());
        var tools = new GeneratorTools(new CaptureSessionStore(), mem);
        var badSrc = "func: async (args) => { throw new Error('nope'); }";
        var fixture = JsonSerializer.Serialize(new VerifyFixture
        {
            Cmd = "thing", ExpectedRowCountMin = 1, ExpectedRowCountMax = 30,
            Patterns = new Dictionary<string, string> { ["id"] = "^\\d+$" },
            NotEmpty = new List<string> { "id" },
            MustBeTruthy = new List<string> { "id" },
            MustNotContain = new Dictionary<string, List<string>> { ["id"] = new List<string> { "" } },
        });
        var res = JsonNode.Parse(tools.AdapterSave("example", "thing", badSrc, fixture))!.AsObject();
        Assert.That(res["code"]!.GetValue<string>(), Is.EqualTo("UNTYPED_THROW"));
    }

    [Test]
    public void Save_GoodAdapter_Persists_And_ListLocal_ShowsIt()
    {
        var mem = new MemoryStore();
        mem.WriteStrategyNote("news", "user_karma", CompleteNote());
        var tools = new GeneratorTools(new CaptureSessionStore(), mem);
        var goodSrc = @"import { cli, Strategy } from '@jackwener/opencli/registry';
import { EmptyResultError, ArgumentError } from '@jackwener/opencli/errors';
cli({
  site: 'news', name: 'user_karma', description: 'x',
  strategy: Strategy.PUBLIC, browser: false,
  args: [{ name: 'id', type: 'string' }], columns: ['karma'],
  func: async (args) => {
    if (!args.id) throw new ArgumentError('id required');
    const res = await fetch('https://news.ycombinator.com/api/karma?id=' + encodeURIComponent(args.id));
    const body = await res.json();
    if (!body.data) throw new EmptyResultError('no data');
    return [{ karma: body.data.karma }];
  },
});";
        var fx = JsonSerializer.Serialize(new VerifyFixture
        {
            Cmd = "user_karma", ExpectedRowCountMin = 1, ExpectedRowCountMax = 1,
            Patterns = new Dictionary<string, string> { ["karma"] = "^\\d+$" },
            NotEmpty = new List<string> { "karma" },
            MustBeTruthy = new List<string> { "karma" },
            MustNotContain = new Dictionary<string, List<string>> { ["karma"] = new() { "" } },
        });
        var saveRes = JsonNode.Parse(tools.AdapterSave("news", "user_karma", goodSrc, fx))!.AsObject();
        Assert.That(saveRes["ok"]!.GetValue<bool>(), Is.True);
        var listRes = JsonNode.Parse(tools.AdapterListLocal())!.AsArray();
        Assert.That(listRes.Count, Is.EqualTo(1));
        Assert.That(listRes[0]!["site"]!.GetValue<string>(), Is.EqualTo("news"));
        Assert.That(File.Exists(LocalRegistry.ResolvePath("news", "user_karma")), Is.True);
    }

    [Test]
    public void RestrictedHost_RejectsCrossOrigin_And_ChildProcess_And_CdpRuntimeEvaluate()
    {
        var everywhereRoot = EverywherePaths.Root;
        Assert.That(RestrictedHostPolicy.AllowFsRead("/etc/passwd", everywhereRoot, "/tmp/nowhere"), Is.False);
        Assert.That(RestrictedHostPolicy.AllowFsWrite("/etc/passwd", everywhereRoot), Is.False);
        Assert.That(RestrictedHostPolicy.AllowFetch("http://internal:8080", "news.ycombinator.com"), Is.False);
        Assert.That(RestrictedHostPolicy.AllowFetch("https://other.com", "news.ycombinator.com"), Is.False);
        Assert.That(RestrictedHostPolicy.AllowFetch("https://news.ycombinator.com/x", "news.ycombinator.com"), Is.True);
        Assert.That(RestrictedHostPolicy.AllowCdp("Runtime.evaluate"), Is.False);
        Assert.That(RestrictedHostPolicy.AllowCdp("Network.enable"), Is.True);
        Assert.That(RestrictedHostPolicy.AllowChildProcess(), Is.False);
    }

    [Test]
    public void OpendiaSmokeCheck_WithoutBridge_ReportsIncompatible()
    {
        var tools = new GeneratorTools(new CaptureSessionStore(), new MemoryStore());
        var res = JsonNode.Parse(tools.OpendiaSmokeCheck())!.AsObject();
        Assert.That(res["code"]!.GetValue<string>(), Is.EqualTo("OPENDIA_INCOMPATIBLE"));
    }

    [Test]
    public void AdapterRegenerate_NoSessionOrCapture_Errors()
    {
        var mem = new MemoryStore();
        mem.WriteStrategyNote("news", "user_karma", CompleteNote());
        var tools = new GeneratorTools(new CaptureSessionStore(), mem);
        var res = JsonNode.Parse(tools.AdapterRegenerate("news", "user_karma"))!.AsObject();
        Assert.That(res["code"]!.GetValue<string>(), Is.EqualTo("ADAPTER_REGENERATE_NEEDS_CAPTURE"));
    }

    [Test]
    public void AdapterListLocal_TraversalSiteId_RaisesIdentifier()
    {
        // Reflect the traversal defense — LocalRegistry.ResolvePath validates via Identifier.
        Assert.Throws<InvalidIdentifierException>(() => LocalRegistry.ResolvePath("../..", "top"));
    }

    [Test]
    public void DriftDetector_UnchangedHash_ReportsOk()
    {
        var fx = new VerifyFixture
        {
            Cmd = "x", Patterns = new() { ["id"] = "^\\d+$" }, NotEmpty = new() { "id" },
            MustBeTruthy = new() { "id" }, MustNotContain = new() { ["id"] = new() { "" } },
        };
        var body = "[{\"id\":42}]";
        var hash = LocalRegistry.Sha256Of(body);
        var report = DriftDetector.Compare(body, fx, hash, 100);
        Assert.That(report.Status, Is.EqualTo("ok"));
    }

    [Test]
    public void DriftDetector_ChangedButPatternsMatch_ReportsDrift()
    {
        var fx = new VerifyFixture
        {
            Cmd = "x", Patterns = new() { ["id"] = "\\d+", ["title"] = ".{1,300}", ["author"] = ".{1,80}" },
            NotEmpty = new() { "id" }, MustBeTruthy = new() { "id" },
            MustNotContain = new() { ["id"] = new() { "" } },
        };
        // 3 patterns match the body → drift, not broken
        var body = "[{\"id\":42,\"title\":\"hello\",\"author\":\"pg\"}]";
        var report = DriftDetector.Compare(body, fx, "different-old-hash", 100);
        Assert.That(report.Status, Is.EqualTo("drift"));
    }
}
