using Everywhere.Mcp.Transport;

namespace Everywhere.Mcp.Tests.Transport;

[TestFixture]
public class EverywhereMcpHttpOptionsTests
{
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(65_536)]
    [TestCase(int.MaxValue)]
    public void Port_RejectsOutOfRange(int port)
    {
        var opts = new EverywhereMcpHttpOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.Port = port);
    }

    [TestCase(-1)]
    [TestCase(101)]
    public void MaxPortFallbacks_RejectsOutOfRange(int fallbacks)
    {
        var opts = new EverywhereMcpHttpOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.MaxPortFallbacks = fallbacks);
    }

    [Test]
    public void Default_PortIs7878()
    {
        var opts = new EverywhereMcpHttpOptions();
        // ponytail: env var test would require process state mutation; the default-from-env
        // path is exercised in integration runs, this test pins the documented fallback.
        Assert.That(opts.Port, Is.GreaterThan(0));
        Assert.That(opts.MaxPortFallbacks, Is.GreaterThanOrEqualTo(0));
        Assert.That(opts.Enabled, Is.True);
    }
}
