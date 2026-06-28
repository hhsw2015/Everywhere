using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Everywhere.Mcp.Tools;

[McpServerToolType]
public static class DocReadTxtTool
{
    [McpServerTool(Name = "doc_read_txt", ReadOnly = true)]
    [Description(
        "Read a plain-text or Markdown file from an absolute POSIX path. " +
        "Tries UTF-8, then GB18030, then Latin-1 to handle mixed-encoding ingestion. " +
        "Returns {text, metadata:{bytes, encoding_fallback, truncated, source}}.")]
    public static CallToolResult DocReadTxt(string path)
    {
        try
        {
            if (!File.Exists(path)) return DocReaderResult.NotFound(path);
            var text = DocReaderResult.ReadAllTextWithFallback(path);
            return DocReaderResult.Build(text, new Dictionary<string, object?>
            {
                ["bytes"] = new FileInfo(path).Length,
                ["source"] = path,
            });
        }
        catch (Exception ex)
        {
            return ToolErrors.FromException(ex, "doc_read_txt");
        }
    }
}
