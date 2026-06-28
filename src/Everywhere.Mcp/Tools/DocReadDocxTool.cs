using System.ComponentModel;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class DocReadDocxTool
{
    [McpServerTool(Name = "doc_read_docx", ReadOnly = true)]
    [Description(
        "Extract text from a .docx file via DocumentFormat.OpenXml. " +
        "Paragraphs are emitted as newline-separated lines (mirrors `pandoc -t plain`). " +
        "Returns {text, metadata:{paragraphs, truncated, source}}.")]
    public static CallToolResult DocReadDocx(string path)
    {
        try
        {
            if (!File.Exists(path)) return DocReaderResult.NotFound(path);

            var sb = new StringBuilder();
            int paragraphCount = 0;
            using (var doc = WordprocessingDocument.Open(path, false))
            {
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body is not null)
                {
                    foreach (var para in body.Descendants<Paragraph>())
                    {
                        var pText = para.InnerText;
                        sb.AppendLine(pText);
                        paragraphCount++;
                    }
                }
            }

            return DocReaderResult.Build(sb.ToString(), new Dictionary<string, object?>
            {
                ["paragraphs"] = paragraphCount,
                ["source"] = path,
            });
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "doc_read_docx");
        }
    }
}
