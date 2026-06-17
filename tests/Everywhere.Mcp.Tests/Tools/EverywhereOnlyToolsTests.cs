using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using Everywhere.Mcp.Tests.Snapshot;
using Everywhere.Mcp.Tools;
using ModelContextProtocol.Protocol;

namespace Everywhere.Mcp.Tests.Tools;

[TestFixture]
public class EverywhereOnlyToolsTests
{
    [Test]
    public void GetSelectedText_ReturnsEmpty_WhenNoFocus()
    {
        var result = GetSelectedTextTool.GetSelectedText(new EmptyVisualElementContext());
        Assert.That(result.IsError, Is.Not.True);
        Assert.That(ExtractText(result), Is.Empty);
    }

    [Test]
    public void GetTerminalOutput_ReturnsEmpty_WhenNoFocus()
    {
        var result = GetTerminalOutputTool.GetTerminalOutput(50, new EmptyVisualElementContext());
        Assert.That(result.IsError, Is.Not.True);
        Assert.That(ExtractText(result), Is.Empty);
    }

    [Test]
    public async Task GetFocusedContext_ReturnsError_WhenNoFocus()
    {
        var sessions = new SessionStore();
        var result = await GetFocusedContextTool.GetFocusedContext(
            2000, new EmptyVisualElementContext(), sessions, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(ExtractText(result), Does.Contain("foreground"));
    }

    [Test]
    public void ExpandElement_ReturnsExpired_WhenIndexUnknown()
    {
        var result = ExpandElementTool.ExpandElement("999", null, new SessionStore());
        Assert.That(result.IsError, Is.True);
        Assert.That(ExtractText(result), Does.Contain("999").And.Contain("not found"));
    }

    [Test]
    public async Task Screenshot_ReturnsError_WhenNoFocus()
    {
        var result = await ScreenshotTool.Screenshot(
            null, new EmptyVisualElementContext(), new SessionStore(), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public void ReadPick_ReturnsPinnedFalse_WhenStashEmpty()
    {
        var stash = new PickStash();
        var sessions = new SessionStore();
        var result = ReadPickTool.ReadPick(stash, sessions);

        Assert.That(result.IsError, Is.Not.True);
        Assert.That(ExtractText(result), Does.Contain("\"pinned\":false"));
    }

    [Test]
    public void ReadPick_ConsumesStashedElement_OnFirstCall()
    {
        var stash = new PickStash();
        var sessions = new SessionStore();
        stash.Set(new FakeVisualElement("submit", []));

        var first = ReadPickTool.ReadPick(stash, sessions);
        var second = ReadPickTool.ReadPick(stash, sessions);

        Assert.That(ExtractText(first), Does.Contain("\"pinned\":true"));
        Assert.That(ExtractText(first), Does.Contain("submit"));
        Assert.That(ExtractText(second), Does.Contain("\"pinned\":false"));
    }

    private static string ExtractText(CallToolResult result) =>
        result.Content[0] is TextContentBlock block ? block.Text : string.Empty;
}
