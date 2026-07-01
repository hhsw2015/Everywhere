using System.Text.RegularExpressions;

namespace Everywhere.Mcp.Tests.Observation;

/// <summary>
/// SPEC §Phase 1 acceptance 1.C — capture_export output contains no raw
/// JWT / ghp_ / sk_live_ / xox[bapr]-. The Phase 0.5 manual fixtures live
/// under the source tree; keep them sanitized-by-hand so tests can rely on
/// them without running the Redactor first.
/// </summary>
[TestFixture]
public sealed class FixtureSecretsTests
{
    // Test binary lives under tests/…/bin/…/net10.0. Walk up to the repo
    // root and locate the fixture dir under the source tree.
    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "tests", "Everywhere.Mcp.Tests", "Fixtures", "observation");
            if (Directory.Exists(probe)) return probe;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("fixtures/observation dir not found relative to test host");
    }

    private static readonly (Regex, string)[] Forbidden = new[]
    {
        (new Regex(@"eyJ[A-Za-z0-9+/=._-]{20,}", RegexOptions.Compiled), "raw JWT"),
        (new Regex(@"gh[pous]_[A-Za-z0-9]{36}", RegexOptions.Compiled), "raw GitHub token"),
        (new Regex(@"sk_(live|test)_[A-Za-z0-9]{24,}", RegexOptions.Compiled), "raw Stripe key"),
        (new Regex(@"xox[baprs]-[A-Za-z0-9-]+", RegexOptions.Compiled), "raw Slack token"),
        (new Regex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled), "raw AWS key"),
    };

    [Test]
    public void ManualFixtures_ContainNoRawSecrets()
    {
        var dir = FixtureDir();
        foreach (var file in Directory.EnumerateFiles(dir, "*-manual.json"))
        {
            var text = File.ReadAllText(file);
            foreach (var (rx, label) in Forbidden)
            {
                var m = rx.Match(text);
                Assert.That(m.Success, Is.False, $"{Path.GetFileName(file)} contained {label}: {m.Value}");
            }
        }
    }
}
