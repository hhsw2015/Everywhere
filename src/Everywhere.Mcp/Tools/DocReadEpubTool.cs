using System.ComponentModel;
using System.Text;
using AngleSharp.Html.Parser;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VersOne.Epub;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class DocReadEpubTool
{
    [McpServerTool(Name = "doc_read_epub", ReadOnly = true)]
    [Description(
        "Extract text from a .epub file. Reading-order HTML chapters are HTML-stripped and concatenated. " +
        "Returns {text, metadata:{title, author, chapters, truncated, source}}.")]
    public static CallToolResult DocReadEpub(string path)
    {
        try
        {
            if (!File.Exists(path)) return DocReaderResult.NotFound(path);

            var book = EpubReader.ReadBook(path);
            var sb = new StringBuilder();
            var parser = new HtmlParser();
            int chapters = 0;
            foreach (var item in book.ReadingOrder)
            {
                chapters++;
                var dom = parser.ParseDocument(item.Content);
                sb.AppendLine(dom.Body?.TextContent ?? string.Empty);
            }

            return DocReaderResult.Build(sb.ToString(), new Dictionary<string, object?>
            {
                ["title"] = book.Title,
                ["author"] = book.Author,
                ["chapters"] = chapters,
                ["source"] = path,
            });
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "doc_read_epub");
        }
    }
}
