using System.ComponentModel;
using System.Text.Json;
using Everywhere.Interop.Whiteboard;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class ReadWhiteboardImageTool
{
    [McpServerTool(Name = "read_whiteboard_image", ReadOnly = true)]
    [Description(
        "Fetch the actual pixels of one image surfaced by a prior read_whiteboard call. " +
        "Use this only when the user explicitly asks to see the image, or when the image's " +
        "alt text + bbox suggest the visual content is critical (screenshots of code, " +
        "diagrams, charts). Plain logos / icons typically don't warrant the multimodal " +
        "token cost — skip in those cases. The image_id is the value from the ![image: ...] " +
        "marker in read_whiteboard's markdown. Images live for ~5 minutes after the " +
        "whiteboard was drawn.")]
    public static CallToolResult ReadWhiteboardImage(
        WhiteboardStash stash,
        [Description("The image_id from read_whiteboard's output (e.g. \"wb-img-7e3a\").")]
        string image_id)
    {
        try
        {
            var bytes = stash.PeekImageBytes(image_id);
            if (bytes is null)
            {
                var json = JsonSerializer.Serialize(new
                {
                    found = false,
                    image_id,
                    reason = "image expired or not found — whiteboard images live 5 minutes",
                });
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = json }],
                };
            }
            return new CallToolResult
            {
                Content = [new ImageContentBlock
                {
                    // ImageContentBlock.Data type is ReadOnlyMemory<byte>.
                    // The SDK's JSON converter base64-encodes on the wire.
                    Data = bytes,
                    MimeType = "image/png",
                }],
            };
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "read_whiteboard_image");
        }
    }
}
