using System.ComponentModel;
using AngleSharp.Html.Parser;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class DocReadHtmlTool
{
    [McpServerTool(Name = "doc_read_html", ReadOnly = true)]
    [Description(
        "Extract visible text from an HTML / HTM file. Uses AngleSharp to walk the DOM. " +
        "Returns {text, metadata:{title, source, truncated}}.")]
    public static CallToolResult DocReadHtml(string path)
    {
        try
        {
            if (!File.Exists(path)) return DocReaderResult.NotFound(path);
            var html = DocReaderResult.ReadAllTextWithFallback(path);
            var parser = new HtmlParser();
            var dom = parser.ParseDocument(html);
            var body = dom.Body?.TextContent ?? string.Empty;
            return DocReaderResult.Build(body, new Dictionary<string, object?>
            {
                ["title"] = dom.Title,
                ["source"] = path,
            });
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "doc_read_html");
        }
    }
}
