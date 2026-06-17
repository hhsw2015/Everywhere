using Everywhere.Mcp.Input;

namespace Everywhere.Mcp.Tests.Input;

[TestFixture]
public class KeyMappingTests
{
    [TestCase("Return", KeyMapping.KeyToken.Return)]
    [TestCase("Tab", KeyMapping.KeyToken.Tab)]
    [TestCase("Esc", KeyMapping.KeyToken.Escape)]
    [TestCase("Escape", KeyMapping.KeyToken.Escape)]
    [TestCase("space", KeyMapping.KeyToken.Space)]
    [TestCase("Backspace", KeyMapping.KeyToken.BackSpace)]
    [TestCase("Up", KeyMapping.KeyToken.Up)]
    [TestCase("PageDown", KeyMapping.KeyToken.PageDown)]
    [TestCase("Page_Up", KeyMapping.KeyToken.PageUp)]
    [TestCase("KP_5", KeyMapping.KeyToken.KP_5)]
    [TestCase("F12", KeyMapping.KeyToken.F12)]
    public void MapToken_KnownNames(string input, KeyMapping.KeyToken expected)
    {
        Assert.That(KeyMapping.MapToken(input), Is.EqualTo(expected));
    }

    [TestCase("a")]
    [TestCase("0")]
    [TestCase(";")]
    public void MapToken_AsciiFallsThroughToLiteral(string input)
    {
        Assert.That(KeyMapping.MapToken(input), Is.EqualTo(KeyMapping.KeyToken.AsciiLiteral));
    }

    [Test]
    public void Parse_HandlesModifierCombination()
    {
        var combo = KeyMapping.Parse("super+c");
        Assert.That(combo.Tokens, Has.Count.EqualTo(2));
        Assert.That(combo.Tokens[0], Is.EqualTo(KeyMapping.KeyToken.Super));
        Assert.That(combo.Tokens[1], Is.EqualTo(KeyMapping.KeyToken.AsciiLiteral));
    }

    [Test]
    public void Parse_HandlesCtrlShiftR()
    {
        var combo = KeyMapping.Parse("ctrl+shift+R");
        Assert.That(combo.Tokens, Has.Count.EqualTo(3));
        Assert.That(combo.Tokens[0], Is.EqualTo(KeyMapping.KeyToken.Control));
        Assert.That(combo.Tokens[1], Is.EqualTo(KeyMapping.KeyToken.Shift));
        Assert.That(combo.Tokens[2], Is.EqualTo(KeyMapping.KeyToken.AsciiLiteral));
    }
}
