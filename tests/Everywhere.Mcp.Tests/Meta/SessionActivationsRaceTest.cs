using Everywhere.Mcp.Meta;

namespace Everywhere.Mcp.Tests.Meta;

/// <summary>Regression: F20 — inner set was HashSet, unsynchronized.</summary>
[TestFixture]
public sealed class SessionActivationsRaceTest
{
    [Test]
    public void Concurrent_Activations_And_Reads_DoNotThrow()
    {
        var sessions = new SessionActivations();
        var domains = new[] { "observation", "web_analysis", "memory", "gates", "generator" };
        var errors = 0;
        Parallel.For(0, 200, i =>
        {
            try
            {
                var domain = domains[i % domains.Length];
                if (i % 3 == 0) sessions.Activate("s1", domain);
                else if (i % 3 == 1) _ = sessions.IsActive("s1", domain);
                else _ = sessions.Get("s1");
            }
            catch { Interlocked.Increment(ref errors); }
        });
        Assert.That(errors, Is.EqualTo(0));
        Assert.That(sessions.Get("s1"), Has.Count.GreaterThan(0));
    }
}
