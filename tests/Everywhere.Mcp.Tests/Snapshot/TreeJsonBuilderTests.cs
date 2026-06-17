using Everywhere.Interop;
using Everywhere.Mcp.Snapshot;

namespace Everywhere.Mcp.Tests.Snapshot;

[TestFixture]
public class TreeJsonBuilderTests
{
    [Test]
    public void Build_PreservesBfsHierarchy()
    {
        // root
        // ├ a
        // │  └ a1
        // └ b
        var a1 = new FakeVisualElement("a1", []);
        var a = new FakeVisualElement("a", [a1]);
        var b = new FakeVisualElement("b", []);
        var root = new FakeVisualElement("root", [a, b]);

        var nodes = ElementIndexer.Walk(root);
        var tree = TreeJsonBuilder.Build(nodes);

        Assert.That(tree, Is.Not.Null);
        Assert.That(tree!.Name, Is.EqualTo("root"));
        Assert.That(tree.Children, Has.Count.EqualTo(2));
        Assert.That(tree.Children![0].Name, Is.EqualTo("a"));
        Assert.That(tree.Children[1].Name, Is.EqualTo("b"));
        Assert.That(tree.Children[0].Children, Has.Count.EqualTo(1));
        Assert.That(tree.Children[0].Children![0].Name, Is.EqualTo("a1"));
    }

    [Test]
    public void Build_DoesNotRelyOnParentReferenceEquality()
    {
        // Even if FakeVisualElement.Parent always returns null (mimicking platform wrappers
        // that hand out fresh instances), the BFS-recorded parent index keeps the tree intact.
        var leafA = new FakeVisualElement("la", []);
        var leafB = new FakeVisualElement("lb", []);
        var root = new FakeVisualElement("r", [leafA, leafB]);

        var nodes = ElementIndexer.Walk(root);
        Assert.That(nodes[1].ParentIndex, Is.EqualTo(0));
        Assert.That(nodes[2].ParentIndex, Is.EqualTo(0));

        var tree = TreeJsonBuilder.Build(nodes);
        Assert.That(tree!.Children, Has.Count.EqualTo(2));
    }
}
