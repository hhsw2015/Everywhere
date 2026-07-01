using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Observation;

[TestFixture]
public sealed class IdentifierTests
{
    [TestCase("hackernews", true)]
    [TestCase("news.ycombinator.com", true)]
    [TestCase("user_karma", true)]
    [TestCase("a", true)]
    [TestCase("../..", false)]
    [TestCase("../../etc", false)]
    [TestCase("Hacker", false)]
    [TestCase("_leading_underscore", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsValid_MatchesSpecPattern(string? id, bool expected)
    {
        Assert.That(Identifier.IsValid(id), Is.EqualTo(expected));
    }

    [Test]
    public void Require_ThrowsForInvalidWithArgName()
    {
        var ex = Assert.Throws<InvalidIdentifierException>(() => Identifier.Require("site", "../.."));
        Assert.That(ex!.ArgName, Is.EqualTo("site"));
        Assert.That(ex.BadValue, Is.EqualTo("../.."));
    }
}
