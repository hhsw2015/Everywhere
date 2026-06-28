using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Everywhere.Mcp.Tools;

internal static class DocReaderResult
{
    public const int DefaultMaxChars = 2_000_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // Default escaper inflates CJK / accented chars (e.g. 中) ~6x. We
        // already return text inside a JSON string with quotes/backslashes
        // escaped, so the relaxed escaper is safe here.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static DocReaderResult()
    {
        // GB18030 lives in the CodePages provider; register once at type init.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static CallToolResult Build(string text, object metadata)
    {
        var truncated = text.Length > DefaultMaxChars;
        if (truncated)
        {
            text = text[..DefaultMaxChars];
        }

        var dict = (Dictionary<string, object?>)metadata;
        dict["truncated"] = truncated;

        var payload = JsonSerializer.Serialize(new { text, metadata = dict }, JsonOpts);
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
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try { return utf8.GetString(bytes); }
        catch (DecoderFallbackException) { /* fall through */ }

        try
        {
            var gb = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return gb.GetString(bytes);
        }
        catch (DecoderFallbackException) { /* fall through */ }
        catch (ArgumentException) { /* GB18030 not registered on this runtime */ }

        return Encoding.Latin1.GetString(bytes);
    }
}
