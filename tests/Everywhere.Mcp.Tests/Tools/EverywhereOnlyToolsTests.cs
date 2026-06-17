using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Tools;

namespace Everywhere.Mcp.Tests.Tools;

[TestFixture]
public class EverywhereOnlyToolsTests
{
    [Test]
    public void GetSelectedText_ReturnsEmpty_WhenNoFocus()
    {
        var result = GetSelectedTextTool.GetSelectedText(new EmptyVisualElementContext());
        Assert.That(result.IsError, Is.Not.True);
    }

    [Test]
    public void GetTerminalOutput_ReturnsEmpty_WhenNoFocus()
    {
        var result = GetTerminalOutputTool.GetTerminalOutput(50, new EmptyVisualElementContext());
        Assert.That(result.IsError, Is.Not.True);
    }

    [Test]
    public void GetFocusedContext_ReturnsError_WhenNoFocus()
    {
        var sessions = new SessionStore();
        var result = GetFocusedContextTool
            .GetFocusedContext(2000, new EmptyVisualElementContext(), sessions, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public void ExpandElement_ReturnsExpired_WhenIndexUnknown()
    {
        var result = ExpandElementTool.ExpandElement("999", null, new SessionStore());
        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public void Screenshot_ReturnsError_WhenNoFocus()
    {
        var result = ScreenshotTool
            .Screenshot(null, new EmptyVisualElementContext(), new SessionStore(), CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.That(result.IsError, Is.True);
    }
}
