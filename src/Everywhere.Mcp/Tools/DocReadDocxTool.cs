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
                var main = doc.MainDocumentPart;
                var body = main?.Document?.Body;
                if (body is not null)
                {
                    foreach (var para in body.Descendants<Paragraph>())
                    {
                        sb.AppendLine(para.InnerText);
                        paragraphCount++;
                    }
                }
                // Footnotes
                if (main?.FootnotesPart?.Footnotes is { } fns)
                {
                    foreach (var p in fns.Descendants<Paragraph>())
                    {
                        sb.AppendLine(p.InnerText);
                    }
                }
                // Endnotes
                if (main?.EndnotesPart?.Endnotes is { } ens)
                {
                    foreach (var p in ens.Descendants<Paragraph>())
                    {
                        sb.AppendLine(p.InnerText);
                    }
                }
                // Headers + footers
                if (main is not null)
                {
                    foreach (var h in main.HeaderParts)
                    {
                        if (h.Header is { } header)
                        {
                            foreach (var p in header.Descendants<Paragraph>()) sb.AppendLine(p.InnerText);
                        }
                    }
                    foreach (var f in main.FooterParts)
                    {
                        if (f.Footer is { } footer)
                        {
                            foreach (var p in footer.Descendants<Paragraph>()) sb.AppendLine(p.InnerText);
                        }
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
