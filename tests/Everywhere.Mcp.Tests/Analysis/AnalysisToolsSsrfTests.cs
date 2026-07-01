using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Observation;
using Everywhere.Mcp.Tools;

namespace Everywhere.Mcp.Tests.Analysis;

[TestFixture]
public sealed class AnalysisToolsSsrfTests
{
    private IDisposable? _gate;

    [SetUp] public void On() => _gate = SelfExpandGate.EnableForTest();
    [TearDown] public void Off() => _gate?.Dispose();

    [Test]
    public async Task WebJsFetchSameOrigin_HttpScheme_SsrfBlocked()
    {
        var store = new CaptureSessionStore();
        var sess = store.Start(1, "example.com");
        var tools = new AnalysisTools(store);
        var r = JsonNode.Parse(await tools.WebJsFetchSameOrigin(sess.SessionId, "http://127.0.0.1:8080/x.js"))!.AsObject();
        Assert.That(r["code"]!.GetValue<string>(), Is.EqualTo("SSRF_BLOCKED"));
    }

    [Test]
    public async Task WebJsFetchSameOrigin_CrossOrigin_Rejected()
    {
        var store = new CaptureSessionStore();
        var sess = store.Start(1, "example.com");
        var tools = new AnalysisTools(store);
        var r = JsonNode.Parse(await tools.WebJsFetchSameOrigin(sess.SessionId, "https://other.com/app.js"))!.AsObject();
        Assert.That(r["code"]!.GetValue<string>(), Is.EqualTo("CROSS_ORIGIN"));
    }
}
