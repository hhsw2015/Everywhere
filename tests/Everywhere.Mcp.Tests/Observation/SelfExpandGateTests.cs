using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Observation;

/// <summary>
/// Regression: v0.9.302 flipped the gate default from opt-in to opt-out.
/// Users no longer need to export EVERYWHERE_MCP_SELFEXPAND=1;
/// EVERYWHERE_MCP_SELFEXPAND=0 becomes the emergency kill switch.
/// </summary>
[TestFixture]
public sealed class SelfExpandGateTests
{
    [Test]
    public void Enabled_ByDefault_WhenEnvUnset()
    {
        var prior = Environment.GetEnvironmentVariable("EVERYWHERE_MCP_SELFEXPAND");
        Environment.SetEnvironmentVariable("EVERYWHERE_MCP_SELFEXPAND", null);
        try
        {
            Assert.That(SelfExpandGate.Enabled, Is.True);
        }
        finally { Environment.SetEnvironmentVariable("EVERYWHERE_MCP_SELFEXPAND", prior); }
    }

    [Test]
    public void Disabled_WhenEnvZero()
    {
        var prior = Environment.GetEnvironmentVariable("EVERYWHERE_MCP_SELFEXPAND");
        Environment.SetEnvironmentVariable("EVERYWHERE_MCP_SELFEXPAND", "0");
        try
        {
            Assert.That(SelfExpandGate.Enabled, Is.False);
        }
        finally { Environment.SetEnvironmentVariable("EVERYWHERE_MCP_SELFEXPAND", prior); }
    }

    [Test]
    public void Enabled_WhenEnvOne_Backcompat()
    {
        var prior = Environment.GetEnvironmentVariable("EVERYWHERE_MCP_SELFEXPAND");
        Environment.SetEnvironmentVariable("EVERYWHERE_MCP_SELFEXPAND", "1");
        try
        {
            Assert.That(SelfExpandGate.Enabled, Is.True);
        }
        finally { Environment.SetEnvironmentVariable("EVERYWHERE_MCP_SELFEXPAND", prior); }
    }

    [Test]
    public void DisableForTest_OverridesEnvUntilDisposed()
    {
        using (SelfExpandGate.DisableForTest())
        {
            Assert.That(SelfExpandGate.Enabled, Is.False);
        }
        // Reverts to default (env unset in local test → enabled)
        Assert.That(SelfExpandGate.Enabled, Is.True);
    }
}
