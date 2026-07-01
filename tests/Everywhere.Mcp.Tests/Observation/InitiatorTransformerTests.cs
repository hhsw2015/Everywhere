using System.Text.Json.Nodes;
using Everywhere.Mcp.OpenCli.Observation;

namespace Everywhere.Mcp.Tests.Observation;

[TestFixture]
public sealed class InitiatorTransformerTests
{
    [Test]
    public void FromCdp_MapsCallFrameKeys()
    {
        var initiator = JsonNode.Parse(@"{
          ""type"": ""script"",
          ""stack"": {
            ""callFrames"": [
              {""url"": ""https://a/b.js"", ""functionName"": ""onClick"", ""lineNumber"": 42, ""columnNumber"": 8},
              {""url"": ""https://a/c.js"", ""functionName"": """", ""lineNumber"": 0, ""columnNumber"": 0}
            ]
          }
        }");
        var frames = InitiatorTransformer.FromCdp(initiator);
        Assert.That(frames, Has.Count.EqualTo(2));
        Assert.That(frames[0].Url, Is.EqualTo("https://a/b.js"));
        Assert.That(frames[0].Function, Is.EqualTo("onClick"));
        Assert.That(frames[0].Line, Is.EqualTo(42));
        Assert.That(frames[0].Col, Is.EqualTo(8));
    }

    [Test]
    public void FromCdp_MissingStack_ReturnsEmpty()
    {
        Assert.That(InitiatorTransformer.FromCdp(JsonNode.Parse("{\"type\":\"other\"}")), Is.Empty);
        Assert.That(InitiatorTransformer.FromCdp(null), Is.Empty);
    }
}
