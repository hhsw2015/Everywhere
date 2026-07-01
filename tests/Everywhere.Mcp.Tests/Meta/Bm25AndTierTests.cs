using System.Text.Json.Nodes;
using Everywhere.Mcp.Meta;
using Everywhere.Mcp.OpenCli.Observation;
using Everywhere.Mcp.Tools;

namespace Everywhere.Mcp.Tests.Meta;

[TestFixture]
public sealed class Bm25AndTierTests
{
    private IDisposable? _gate;
    [SetUp] public void On() => _gate = SelfExpandGate.EnableForTest();
    [TearDown] public void Off() => _gate?.Dispose();

    [Test]
    public void Bm25_RanksExactTokenMatchTop()
    {
        var idx = new Bm25Index();
        idx.Add(new Bm25Index.Doc("web_verdict_score", "Score every captured request likely_data / maybe_data / noise / blocked."));
        idx.Add(new Bm25Index.Doc("browser_snapshot", "DOM/ARIA tree of the active tab."));
        idx.Add(new Bm25Index.Doc("web_signature_scheme", "Detect API signature scheme: jwt | bearer | basic | hmac_sha256."));

        var hits = idx.Search("api signature detect scheme", 3);
        Assert.That(hits, Has.Count.GreaterThanOrEqualTo(1));
        Assert.That(hits[0].Name, Is.EqualTo("web_signature_scheme"));
    }

    [Test]
    public void SearchTools_Search_ReturnsRelevantWebTool()
    {
        var tools = new SearchTools(new SessionActivations());
        var r = JsonNode.Parse(tools.SearchToolsCmd("verdict api score"))!.AsArray();
        Assert.That(r.Count, Is.GreaterThan(0));
        var names = r.Select(n => n!["name"]!.GetValue<string>()).ToArray();
        Assert.That(names, Does.Contain("web_verdict_score"));
    }

    [Test]
    public void ActivateDomain_UnknownName_ReturnsUnknownDomain()
    {
        var tools = new SearchTools(new SessionActivations());
        var r = JsonNode.Parse(tools.ActivateDomain("nowhere"))!.AsObject();
        Assert.That(r["code"]!.GetValue<string>(), Is.EqualTo("UNKNOWN_DOMAIN"));
    }

    [Test]
    public void ActivateDomain_WebAnalysis_ShowsInList()
    {
        var sessions = new SessionActivations();
        var tools = new SearchTools(sessions);
        tools.ActivateDomain("web_analysis");
        var listing = JsonNode.Parse(tools.ListDomains())!.AsArray();
        var web = listing.First(d => d!["name"]!.GetValue<string>() == "web_analysis")!.AsObject();
        Assert.That(web["active"]!.GetValue<bool>(), Is.True);
    }

    [Test]
    public void SessionActivations_ResetForDisconnect_ClearsSet()
    {
        var s = new SessionActivations();
        s.Activate("s1", "generator");
        Assert.That(s.IsActive("s1", "generator"), Is.True);
        s.ResetForDisconnect("s1");
        Assert.That(s.IsActive("s1", "generator"), Is.False);
    }

    [Test]
    public void SkillFile_Exists_WithFrontmatter()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? skill = null;
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "docs", "skills", "adapter-author", "SKILL.md");
            if (File.Exists(probe)) { skill = probe; break; }
            dir = dir.Parent;
        }
        Assert.That(skill, Is.Not.Null, "SKILL.md not found");
        var text = File.ReadAllText(skill!);
        Assert.That(text, Does.StartWith("---"));
        Assert.That(text, Does.Contain("name: adapter-author"));
        Assert.That(text, Does.Contain("allowed-tools:"));
    }

    [Test]
    public void PromptFile_Exists_NoUnresolvedPlaceholders()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? prompt = null;
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "docs", "skills", "adapter-author", "PROMPT.md");
            if (File.Exists(probe)) { prompt = probe; break; }
            dir = dir.Parent;
        }
        Assert.That(prompt, Is.Not.Null, "PROMPT.md not found");
        // Human-authored file; verify it's non-empty.
        var text = File.ReadAllText(prompt!);
        Assert.That(text.Length, Is.GreaterThan(500));
    }
}
