using Everywhere.Interop;
using Everywhere.Mcp;
using Everywhere.Mcp.Input;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Tools;
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

    [Test]
    public void ListAppsTool_ReturnsEmptyJson_WhenNoVisualElements()
    {
        var result = ListAppsTool.ListApps(new EmptyVisualElementContext());
        Assert.That(result.IsError, Is.Not.True);
        Assert.That(result.Content, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetAppStateTool_ReturnsAppNotRunning_WhenAppIsUnknown()
    {
        var sessions = new SessionStore();
        var result = await GetAppStateTool.GetAppState(
            "doesnotexist", false, new EmptyVisualElementContext(), sessions, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        var text = ExtractText(result);
        Assert.That(text, Does.Contain("doesnotexist"));
        Assert.That(text, Does.Contain("not running"));
    }

    [Test]
    public void ClickTool_ReturnsExpired_WhenIndexUnknown()
    {
        var sessions = new SessionStore();
        var input = new TestInputSimulator();
        var focus = new FocusBorrow(new TestFocusBackend());
        var result = ClickTool.Click("any", sessions, input, focus, new EmptyVisualElementContext(), element_index: "999");
        Assert.That(result.IsError, Is.True);
        Assert.That(ExtractText(result), Does.Contain("999").And.Contain("not found"));
    }

    private static string ExtractText(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.Content[0] is ModelContextProtocol.Protocol.TextContentBlock block ? block.Text : string.Empty;

    private sealed class TestInputSimulator : IInputSimulator
    {
        public void MoveTo(double x, double y) { }
        public void Click(double x, double y, int clickCount = 1, MouseButton button = MouseButton.Left) { }
        public void DragTo(double fromX, double fromY, double toX, double toY) { }
        public void TypeText(string text) { }
        public void PressKey(string xdotoolKeyName) { }
    }

    private sealed class TestFocusBackend : IFocusBackend
    {
        public nint GetForegroundWindow() => 0;
        public bool TryAxRaise(nint window) => true;
        public void Activate(nint window) { }
    }

    [Test]
    public void ScrollTool_ReturnsValidationError_OnBadDirection()
    {
        var result = ScrollTool.Scroll("any", "0", "diagonal", null, new SessionStore());
        Assert.That(result.IsError, Is.True);
    }
}
