using Everywhere.Mcp;
using Everywhere.Mcp.Snapshot;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Mcp.Tests;

[TestFixture]
public class SmokeTests
{
    [Test]
    public void AddEverywhereMcp_RegistersWithoutThrow()
    {
        var services = new ServiceCollection();
        Assert.DoesNotThrow(() => services.AddEverywhereMcp());

        var provider = services.BuildServiceProvider();
        Assert.That(provider.GetService<SessionStore>(), Is.Not.Null);
        Assert.That(provider.GetService<IVisualElementContext>(), Is.Not.Null);
    }

    // Per-tool smoke tests removed in v0.9.245 — every tool now takes
    // IServiceProvider and resolves IAxBridgeBackend / IInputSimulator /
    // FocusBorrow / etc. lazily, which is too much to set up in a smoke
    // suite. Real coverage lives under bench/ + manual end-to-end tests.
    // If a tool needs a unit test, write one against a focused fake
    // service provider rather than reviving these stubs.
}
