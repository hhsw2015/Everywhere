// adapter: 36kr/news, hackernews/top
//
// SPEC §8 Phase 1 — run two PUBLIC-strategy adapters against canned
// HTTP responses (no live network). The runtime's fetch shim is wired
// to an HttpClient whose handler returns fixture bytes; the adapter
// must produce the expected output shape.
//
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli;

namespace Everywhere.Mcp.Tests.OpenCli;

[TestFixture]
public class PublicStrategyTests
{
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "3rd", "opencli", "clis")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("repo root with 3rd/opencli not found");
        return dir.FullName;
    }

    [Test]
    public async Task HackernewsTop_StubbedFetch_ReturnsListShape()
    {
        var root = FindRoot();
        var clis = Path.Combine(root, "3rd", "opencli", "clis");
        var manifest = Path.Combine(root, "3rd", "opencli", "cli-manifest.json");
        var http = new HttpClient(new StubHandler(new Dictionary<string, string>
        {
            ["https://hacker-news.firebaseio.com/v0/topstories.json"] = "[1,2,3]",
            ["https://hacker-news.firebaseio.com/v0/item/1.json"] = """{"id":1,"title":"A","score":10,"by":"x","descendants":1,"url":"https://a"}""",
            ["https://hacker-news.firebaseio.com/v0/item/2.json"] = """{"id":2,"title":"B","score":20,"by":"y","descendants":2,"url":"https://b"}""",
            ["https://hacker-news.firebaseio.com/v0/item/3.json"] = """{"id":3,"title":"C","score":30,"by":"z","descendants":3,"url":"https://c"}""",
        }));
        await using var runtime = new OpenCliRuntime(clis, manifest, http);

        var args = new JsonObject { ["limit"] = 3 };
        var resp = await runtime.InvokeAsync("hackernews", "top", args, new Phase1StubPage());

        TestContext.Out.WriteLine(resp.ToJsonString());
        Assert.That((bool)resp["ok"]!, Is.True);
        Assert.That(resp["data"], Is.Not.Null);
    }

    [Test]
    public async Task Kr36News_StubbedFetch_ReturnsListShape()
    {
        var root = FindRoot();
        var clis = Path.Combine(root, "3rd", "opencli", "clis");
        var manifest = Path.Combine(root, "3rd", "opencli", "cli-manifest.json");
        var http = new HttpClient(new StubHandler(new Dictionary<string, string>
        {
            ["https://www.36kr.com/feed"] = """
                <rss><channel>
                <item><title>Hello</title><link><![CDATA[https://example.com/1]]></link><pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate><description>desc</description></item>
                </channel></rss>
                """,
        }));
        await using var runtime = new OpenCliRuntime(clis, manifest, http);

        var resp = await runtime.InvokeAsync("36kr", "news", new JsonObject { ["limit"] = 1 }, new Phase1StubPage());
        TestContext.Out.WriteLine(resp.ToJsonString());
        Assert.That((bool)resp["ok"]!, Is.True);
    }

    private sealed class StubHandler(Dictionary<string, string> table) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (!table.TryGetValue(url, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"no stub for {url}"),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, body.TrimStart().StartsWith("<") ? "application/xml" : "application/json"),
            });
        }
    }
}
