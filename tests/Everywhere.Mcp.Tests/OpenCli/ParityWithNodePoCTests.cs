// adapter: 36kr/news, hackernews/top
//
// SPEC §8 Phase 1 — diff our runtime's output against the Node PoC's
// recorded baseline. The baseline lives at
// tests/fixtures/opencli/<site>-<name>-poc.json and is committed by
// running bench/opencli/poc/freeze.mjs against a locked upstream sha.
//
// We can't run the PoC inside a unit test (no Node), so the parity
// check operates on whatever fixtures exist; if a fixture is missing
// the test is marked inconclusive instead of failing — the bench
// harness owns the live re-freeze.
//
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli;

namespace Everywhere.Mcp.Tests.OpenCli;

[TestFixture]
public class ParityWithNodePoCTests
{
    private static (string clis, string manifest, string fixturesDir) Paths()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "3rd", "opencli", "clis")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("repo root not found");
        return (
            Path.Combine(dir.FullName, "3rd", "opencli", "clis"),
            Path.Combine(dir.FullName, "3rd", "opencli", "cli-manifest.json"),
            Path.Combine(dir.FullName, "tests", "fixtures", "opencli"));
    }

    [TestCase("hackernews", "top")]
    [TestCase("36kr",       "news")]
    public async Task ContractDiff(string site, string name)
    {
        var (clis, manifest, fixturesDir) = Paths();
        var pocPath = Path.Combine(fixturesDir, $"{site}-{name}-poc.json");
        if (!File.Exists(pocPath))
            Assert.Inconclusive($"no PoC baseline at {pocPath}; run bench/opencli/poc/freeze.mjs to record one");

        var poc = JsonNode.Parse(await File.ReadAllTextAsync(pocPath))!.AsObject();
        var requests = poc["__requests"] as JsonArray ?? new JsonArray();
        var stubs = new Dictionary<string, string>();
        foreach (var entry in requests.OfType<JsonObject>())
        {
            var url = entry["url"]?.GetValue<string>();
            var body = entry["body"]?.GetValue<string>();
            if (url is not null && body is not null) stubs[url] = body;
        }
        var args = poc["args"] as JsonObject ?? new JsonObject();

        var http = new HttpClient(new ReplayHandler(stubs));
        await using var runtime = new OpenCliRuntime(clis, manifest, http);
        var resp = await runtime.InvokeAsync(site, name, args, new Phase1StubPage());

        Assert.That((bool)resp["ok"]!, Is.True, () => resp.ToJsonString());
        var ourData = JsonSerializer.Serialize(resp["data"], new JsonSerializerOptions { WriteIndented = false });
        var theirData = JsonSerializer.Serialize(poc["data"], new JsonSerializerOptions { WriteIndented = false });
        Assert.That(ourData, Is.EqualTo(theirData), "data drift from PoC baseline");
    }

    private sealed class ReplayHandler(Dictionary<string, string> table) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (!table.TryGetValue(url, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"no replay for {url}") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
