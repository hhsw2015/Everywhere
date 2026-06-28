using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class DocReadPdfTool
{
    [McpServerTool(Name = "doc_read_pdf", ReadOnly = true)]
    [Description(
        "Extract text from a PDF file. Uses PdfPig with content-order layout. " +
        "If extracted text length < 100 chars, sets metadata.likely_scanned=true (no OCR on non-macOS). " +
        "Returns {text, metadata:{pages, likely_scanned, truncated, source}}.")]
    public static CallToolResult DocReadPdf(string path)
    {
        try
        {
            if (!File.Exists(path)) return DocReaderResult.NotFound(path);

            var sb = new StringBuilder();
            int pageCount;
            using (var doc = PdfDocument.Open(path))
            {
                pageCount = doc.NumberOfPages;
                for (var i = 1; i <= pageCount; i++)
                {
                    var page = doc.GetPage(i);
                    var content = ContentOrderTextExtractor.GetText(page);
                    sb.AppendLine(content);
                }
            }

            var text = sb.ToString();
            return DocReaderResult.Build(text, new Dictionary<string, object?>
            {
                ["pages"] = pageCount,
                ["likely_scanned"] = text.Trim().Length < 100,
                ["source"] = path,
            });
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "doc_read_pdf");
        }
    }
}
