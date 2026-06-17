using Avalonia;
using Avalonia.Platform;
using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;
using SkiaSharp;

namespace Everywhere.Mcp.Tests.Snapshot;

[TestFixture]
public class ElementIndexerTests
{
    [Test]
    public void Walk_AssignsZeroBasedIndices()
    {
        var root = BuildTree(
            depth: 2,
            childrenPerNode: 2);

        var nodes = ElementIndexer.Walk(root);
        Assert.That(nodes, Has.Count.GreaterThan(0));
        Assert.That(nodes[0].Index, Is.EqualTo(0));
        for (var i = 1; i < nodes.Count; i++)
        {
            Assert.That(nodes[i].Index, Is.EqualTo(i));
        }
    }

    [Test]
    public void Walk_StopsAtMaxNodeCount()
    {
        var root = BuildTree(depth: 8, childrenPerNode: 4);
        var nodes = ElementIndexer.Walk(root, maxNodeCount: 10);
        Assert.That(nodes, Has.Count.EqualTo(10));
    }

    [Test]
    public void Walk_StopsAtMaxDepth()
    {
        var root = BuildTree(depth: 8, childrenPerNode: 1);
        var nodes = ElementIndexer.Walk(root, maxDepth: 3);
        Assert.That(nodes.All(n => n.Depth <= 3), Is.True);
    }

    [Test]
    public void Render_ProducesIndentedText()
    {
        var root = BuildTree(depth: 2, childrenPerNode: 1, namePrefix: "Node");
        var nodes = ElementIndexer.Walk(root);
        var text = SnapshotRenderer.Render(nodes, showFullText: false);

        Assert.That(text, Does.Contain("[0] "));
        Assert.That(text, Does.Contain("[1] "));
    }

    private static IVisualElement BuildTree(int depth, int childrenPerNode, string namePrefix = "n")
    {
        IVisualElement Build(int level, int idx) =>
            new FakeVisualElement(
                $"{namePrefix}-{level}-{idx}",
                level + 1 > depth
                    ? Array.Empty<IVisualElement>()
                    : Enumerable.Range(0, childrenPerNode)
                        .Select(i => Build(level + 1, i))
                        .ToArray());

        return Build(0, 0);
    }
}

internal sealed class FakeVisualElement(string name, IReadOnlyList<IVisualElement> children) : IVisualElement
{
    public string Id => name;
    public IVisualElement? Parent => null;
    public VisualElementSiblingAccessor SiblingAccessor => throw new NotSupportedException();
    public IEnumerable<IVisualElement> Children => children;
    public VisualElementType Type => VisualElementType.Panel;
    public VisualElementStates States => VisualElementStates.None;
    public string? Name => name;
    public PixelRect BoundingRectangle => new(0, 0, 0, 0);
    public int ProcessId => -1;
    public nint NativeWindowHandle => 0;
    public string? GetText(int maxLength = -1) => name;
    public string? GetSelectionText() => null;
    public void Invoke() { }
    public void SetText(string text) { }
    public void SendShortcut(KeyboardShortcut shortcut) { }
    public Task<IVisualElement.ICapturedBitmapData> CaptureAsync(CancellationToken cancellationToken) =>
        Task.FromException<IVisualElement.ICapturedBitmapData>(new NotSupportedException());
}
