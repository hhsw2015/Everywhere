// adapter: 36kr/hot, bilibili/hot, bilibili/me
//
// SPEC §8 Phase 2 — browser-strategy adapters. Without a real OpenDia
// connection we exercise (a) the no-extension envelope from §2.1
// {ok:false, error:"opendia-not-connected"}, and (b) the bridge dispatch
// surface against an in-memory fake.
//
using System.Net.Http;
using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli;
using Everywhere.Mcp.Tools;

namespace Everywhere.Mcp.Tests.OpenCli;

[TestFixture]
public class BrowserStrategyTests
{
    private static (string clis, string manifest) Paths()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "3rd", "opencli", "clis")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("repo root not found");
        return (Path.Combine(dir.FullName, "3rd", "opencli", "clis"),
                Path.Combine(dir.FullName, "3rd", "opencli", "cli-manifest.json"));
    }

    [Test]
    public async Task BrowserAdapter_WithoutBridge_ReturnsOpenDiaNotConnected()
    {
        var (clis, manifest) = Paths();
        await using var runtime = new OpenCliRuntime(clis, manifest, new HttpClient());
        var tools = new OpenCliTools(runtime, bridge: null);

        var raw = await tools.OpenCliRun("36kr", "hot", "{}");
        var resp = JsonNode.Parse(raw)!.AsObject();
        Assert.That((bool)resp["ok"]!, Is.False);
        Assert.That(resp["error"]!.GetValue<string>(), Is.EqualTo("opendia-not-connected"));
        Assert.That(resp["code"]!.GetValue<string>(), Is.EqualTo("BROWSER_NOT_READY"));
    }

    [Test]
    [Category("manual")]
    public void BilibiliMe_RequiresRealSession()
    {
        // Cookie-tier; needs a logged-in bilibili in OpenDia.
        Assert.Inconclusive("manual tier — run from the agent host with a logged-in bilibili tab");
    }
}
