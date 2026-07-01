using System.Text.Json.Nodes;
using Everywhere.Mcp.Meta;
using Everywhere.Mcp.OpenCli.Observation;
using Everywhere.Mcp.Tools;

namespace Everywhere.Mcp.Tests.Meta;

[TestFixture]
public sealed class AdapterCatalogTests
{
    private static string TempManifest(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), "manifest-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, body);
        return path;
    }

    [Test]
    public void Load_IndexesVendoredEntries_AndSearchFindsThem()
    {
        var body = @"[
          {""site"":""hackernews"",""name"":""user_karma"",""description"":""Get karma of a Hacker News user"",""strategy"":""public""},
          {""site"":""sf-express"",""name"":""track"",""description"":""Track SF express package by tracking number"",""strategy"":""public""},
          {""site"":""bilibili"",""name"":""hot"",""description"":""Bilibili trending videos this week"",""strategy"":""cookie""}
        ]";
        var idx = new AdapterCatalogIndex();
        idx.Load(TempManifest(body));
        Assert.That(idx.Count, Is.EqualTo(3));

        var hits = idx.Search("track express package");
        Assert.That(hits, Is.Not.Empty);
        Assert.That(hits[0].Entry.Site, Is.EqualTo("sf-express"));
        Assert.That(hits[0].Entry.Origin, Is.EqualTo("vendored"));
    }

    [Test]
    public void Load_MergesLocalRegistry_WithoutOverwritingVendored()
    {
        using var _ = EverywherePaths.OverrideBaseForTest(Path.Combine(Path.GetTempPath(), "cat-" + Guid.NewGuid().ToString("N")));
        // Seed a local adapter under a distinct site so it doesn't collide.
        var localPath = Everywhere.Mcp.OpenCli.Generator.LocalRegistry.ResolvePath("intranet", "roster");
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllText(localPath, "// noop");
        // Empty manifest — only local entries should be indexed.
        var idx = new AdapterCatalogIndex();
        idx.Load(TempManifest("[]"));
        Assert.That(idx.Count, Is.EqualTo(1));
        var hits = idx.Search("intranet roster");
        Assert.That(hits[0].Entry.Origin, Is.EqualTo("local"));
    }

    [Test]
    public void Load_VendoredWinsOnCollision()
    {
        using var _ = EverywherePaths.OverrideBaseForTest(Path.Combine(Path.GetTempPath(), "cat-" + Guid.NewGuid().ToString("N")));
        var localPath = Everywhere.Mcp.OpenCli.Generator.LocalRegistry.ResolvePath("hackernews", "user_karma");
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllText(localPath, "// local override");
        var body = @"[{""site"":""hackernews"",""name"":""user_karma"",""description"":""Vendored karma"",""strategy"":""public""}]";
        var idx = new AdapterCatalogIndex();
        idx.Load(TempManifest(body));
        var hits = idx.Search("hackernews karma");
        Assert.That(hits[0].Entry.Origin, Is.EqualTo("vendored"),
            "vendored must win on (site,name) collision (SPEC §2.1)");
    }

    [Test]
    public void SearchAdapters_Tool_ReturnsMergedResults()
    {
        using var _ = SelfExpandGate.EnableForTest();
        var tools = new SearchTools(new SessionActivations());
        var res = tools.SearchAdapters("hackernews karma");
        var arr = JsonNode.Parse(res)!.AsArray();
        Assert.That(arr, Is.Not.Null);
    }
}
