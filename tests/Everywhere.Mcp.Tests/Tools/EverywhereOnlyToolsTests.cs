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
    public void GetSelectedText_ReturnsSelectedFalseEnvelope_WhenNoFocus()
    {
        var result = GetSelectedTextTool.GetSelectedText(new EmptyVisualElementContext());
        Assert.That(result.IsError, Is.Not.True);
        var json = System.Text.Json.JsonDocument.Parse(ExtractText(result)).RootElement;
        Assert.That(json.GetProperty("selected").GetBoolean(), Is.False);
        Assert.That(json.GetProperty("text").GetString(), Is.Empty);
    }

    [Test]
    public void GetTerminalOutput_ReturnsIsTerminalFalse_WhenNoFocus()
    {
        var result = GetTerminalOutputTool.GetTerminalOutput(50, new EmptyVisualElementContext());
        Assert.That(result.IsError, Is.Not.True);
        var json = System.Text.Json.JsonDocument.Parse(ExtractText(result)).RootElement;
        Assert.That(json.GetProperty("is_terminal").GetBoolean(), Is.False);
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

        var firstJson = System.Text.Json.JsonDocument.Parse(ExtractText(first)).RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(firstJson.GetProperty("pinned").GetBoolean(), Is.True);
            Assert.That(firstJson.GetProperty("picked_index").GetInt32(), Is.EqualTo(0));
            Assert.That(firstJson.TryGetProperty("element", out _), Is.True);
        });

        var secondJson = System.Text.Json.JsonDocument.Parse(ExtractText(second)).RootElement;
        Assert.That(secondJson.GetProperty("pinned").GetBoolean(), Is.False);
    }

    [Test]
    public void ReadPick_RegistersIndexInSessionStoreForFollowUp()
    {
        var stash = new PickStash();
        var sessions = new SessionStore();
        var pick = new FakeVisualElement("submit", []);
        stash.Set(pick);

        ReadPickTool.ReadPick(stash, sessions);

        // ResolveAcrossSessions(0) should now find the pinned element so a subsequent
        // click(element_index="0") works on the pinned element.
        var hit = sessions.ResolveAcrossSessions(0);
        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Value.Element, Is.SameAs(pick));
    }

    private static string ExtractText(CallToolResult result) =>
        result.Content[0] is TextContentBlock block ? block.Text : string.Empty;
}
