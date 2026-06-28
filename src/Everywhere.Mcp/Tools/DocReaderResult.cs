using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Everywhere.Mcp.Tools;

internal static class DocReaderResult
{
    public const int DefaultMaxChars = 2_000_000;

    public static CallToolResult Build(string text, object metadata)
    {
        var truncated = text.Length > DefaultMaxChars;
        if (truncated)
        {
            text = text[..DefaultMaxChars];
        }

        // metadata is already a Dictionary<string, object?>; project truncated into it.
        var dict = (Dictionary<string, object?>)metadata;
        dict["truncated"] = truncated;

        var payload = JsonSerializer.Serialize(new { text, metadata = dict });
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = payload }],
        };
    }

    public static CallToolResult NotFound(string source) =>
        ToolErrors.Error($"file not found: {source}");

    public static string ReadAllTextWithFallback(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // Try UTF-8 strict, then GB18030, then Latin-1 (single-byte, never throws).
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // ignore
        }

        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var gb = System.Text.Encoding.GetEncoding("GB18030", System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback);
            return gb.GetString(bytes);
        }
        catch (Exception)
        {
            // ignore
        }

        return System.Text.Encoding.Latin1.GetString(bytes);
    }
}
