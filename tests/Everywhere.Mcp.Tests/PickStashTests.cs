using Avalonia;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Tests.Snapshot;
using Microsoft.Extensions.Time.Testing;

namespace Everywhere.Mcp.Tests;

[TestFixture]
public class PickStashTests
{
    [Test]
    public void Take_ReturnsNull_WhenEmpty()
    {
        var stash = new PickStash();
        Assert.That(stash.Take(), Is.Null);
        Assert.That(stash.HasFreshPin, Is.False);
    }

    [Test]
    public void Take_ConsumesTheSlot()
    {
        var stash = new PickStash();
        var element = new FakeVisualElement("a", []);
        stash.Set(element);
        Assert.That(stash.HasFreshPin, Is.True);

        Assert.That(stash.Take(), Is.SameAs(element));
        Assert.That(stash.Take(), Is.Null);
    }

    [Test]
    public void Set_ReplacesUnreadPin()
    {
        var stash = new PickStash();
        var first = new FakeVisualElement("first", []);
        var second = new FakeVisualElement("second", []);
        stash.Set(first);
        stash.Set(second);

        Assert.That(stash.Take(), Is.SameAs(second));
    }

    [Test]
    public void ExpiredPin_ReturnsNull()
    {
        var clock = new FakeTimeProvider();
        var stash = new PickStash(clock);
        stash.Set(new FakeVisualElement("a", []), TimeSpan.FromSeconds(30));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.That(stash.Take(), Is.Null);
        Assert.That(stash.HasFreshPin, Is.False);
    }
}
