using System.ComponentModel;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Drawing;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class DocReadPptxTool
{
    [McpServerTool(Name = "doc_read_pptx", ReadOnly = true)]
    [Description(
        "Extract text from a .pptx file. Concatenates text frames in slide order. " +
        "Returns {text, metadata:{slides, truncated, source}}.")]
    public static CallToolResult DocReadPptx(string path)
    {
        try
        {
            if (!File.Exists(path)) return DocReaderResult.NotFound(path);

            var sb = new StringBuilder();
            int slideCount = 0;
            using (var doc = PresentationDocument.Open(path, false))
            {
                var pres = doc.PresentationPart;
                if (pres?.SlideParts is not null)
                {
                    foreach (var slide in pres.SlideParts)
                    {
                        slideCount++;
                        var slideRoot = slide.Slide;
                        if (slideRoot is null) continue;
                        foreach (var t in slideRoot.Descendants<Text>())
                        {
                            sb.AppendLine(t.Text);
                        }
                    }
                }
            }

            return DocReaderResult.Build(sb.ToString(), new Dictionary<string, object?>
            {
                ["slides"] = slideCount,
                ["source"] = path,
            });
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "doc_read_pptx");
        }
    }
}
