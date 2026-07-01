using System.Text.Json;
using Everywhere.Mcp.OpenCli.Analysis;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Analysis;

[TestFixture]
public sealed class VerdictScorerTests
{
    private static CaptureSession LoadHackerNews()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "tests", "Everywhere.Mcp.Tests", "fixtures", "observation", "hackernews-manual.json");
            if (File.Exists(probe))
            {
                var opts = new JsonSerializerOptions();
                return JsonSerializer.Deserialize<CaptureSession>(File.ReadAllText(probe), opts)!;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("hackernews-manual.json not found");
    }

    [Test]
    public void HnFixture_YieldsLikelyDataForAlgoliaAndKarma()
    {
        var session = LoadHackerNews();
        var results = VerdictScorer.Score(session);
        var likely = results.Where(r => r.Verdict == "likely_data").ToList();
        Assert.That(likely, Has.Count.GreaterThanOrEqualTo(1));
        // The algolia + karma endpoints have business shape; expect at least one
        Assert.That(likely.Any(l => l.RequestId is "hn-req-0004" or "hn-req-0008"), Is.True);
    }

    [Test]
    public void AnalyticsUrl_ClassifiedNoise_WithReason()
    {
        var session = LoadHackerNews();
        var ga = VerdictScorer.Score(session).First(r => r.RequestId == "hn-req-0005");
        Assert.That(ga.Verdict, Is.EqualTo("noise"));
        Assert.That(ga.Reasons, Does.Contain("not_json").Or.Contain("analytics_url"));
    }

    [Test]
    public void FailingRequest_ClassifiedBlocked_WithAuthFail()
    {
        var session = LoadHackerNews();
        var threads = VerdictScorer.Score(session).First(r => r.RequestId == "hn-req-0007");
        Assert.That(threads.Verdict, Is.EqualTo("blocked"));
        Assert.That(threads.Reasons, Does.Contain("auth_fail"));
    }

    [Test]
    public void ResponseShape_FlattensPathTypeMap()
    {
        var session = LoadHackerNews();
        var karma = VerdictScorer.Score(session).First(r => r.RequestId == "hn-req-0008");
        Assert.That(karma.ResponseShape, Is.Not.Empty);
        Assert.That(karma.ResponseShape.Keys, Does.Contain("data.karma").Or.Contain("data.user"));
    }

    [Test]
    public void EmptyInitiatorStack_StillClassifies()
    {
        var session = LoadHackerNews();
        var mainDoc = VerdictScorer.Score(session).First(r => r.RequestId == "hn-req-0001");
        Assert.That(mainDoc.Verdict, Is.EqualTo("noise")); // text/html
        Assert.That(mainDoc.Reasons, Does.Contain("not_json"));
    }
}
